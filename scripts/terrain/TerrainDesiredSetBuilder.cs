using Godot;
using System.Collections.Generic;
using System.Diagnostics;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainDesiredSetBuilder
{
    private static readonly Vector2I[] NeighborOffsets =
    {
        Vector2I.Right,
        Vector2I.Left,
        Vector2I.Up,
        Vector2I.Down
    };

    private readonly HashSet<Vector2I> _desiredColumns = new();
    private readonly Dictionary<Vector2I, CandidateState> _candidateStates = new();
    private readonly PriorityQueue<FrontierEntry, float> _frontier = new();
    private readonly PriorityQueue<SelectedEntry, float> _selectedColumns = new();
    private readonly Dictionary<Vector2I, SelectedState> _selectedStates = new();

    private int _epoch = 1;
    private int _nextToken = 1;
    private bool _selectedScoresDirty = true;
    private bool _reseededThisEpoch;
    private bool _frontierExhaustedThisEpoch;
    private bool _settledThisEpoch;
    private string _lastInvalidationReason = "startup";

    public IReadOnlyCollection<Vector2I> DesiredColumns => _desiredColumns;
    public int DesiredColumnCount => _desiredColumns.Count;
    public int FrontierCount => _frontier.Count;
    public int VisitedCandidateCount => _candidateStates.Count;
    public int SearchEpoch => _epoch;
    public long InvalidationCount { get; private set; }
    public long StaleRefreshCount { get; private set; }
    public long FrontierCompactionCount { get; private set; }
    public double LastSearchMs { get; private set; }
    public DesiredSearchThrottleState ThrottleState { get; private set; } = DesiredSearchThrottleState.ThresholdLimited;
    public ColumnPriorityInfo LastSelectedColumnInfo { get; private set; }
    public string LastInvalidationReason => _lastInvalidationReason;

    public bool ContainsDesiredColumn(Vector2I key)
    {
        return _desiredColumns.Contains(key);
    }

    public void Invalidate(
        string reason,
        TerrainDesiredSetContext context,
        IEnumerable<Vector2I> residentColumns,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        _epoch++;
        InvalidationCount++;
        _lastInvalidationReason = reason;
        _reseededThisEpoch = false;
        _frontierExhaustedThisEpoch = false;
        _selectedScoresDirty = true;
        _settledThisEpoch = false;

        PruneOutOfRangeColumns(context);
        ReseedFrontier(context, residentColumns, evaluatePriority);
    }

    public void AdvanceSearch(
        TerrainDesiredSetContext context,
        IEnumerable<Vector2I> residentColumns,
        int workBudget,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        LastSearchMs = 0.0;

        if (_settledThisEpoch && !_selectedScoresDirty)
        {
            ThrottleState = DesiredSearchThrottleState.ThresholdLimited;
            LastSearchMs = stopwatch.Elapsed.TotalMilliseconds;
            return;
        }

        if (_selectedScoresDirty)
        {
            RebuildSelectedScores(context, evaluatePriority);
        }

        EnsureGuaranteedColumns(context, evaluatePriority);
        TrimSelectedToBudget(context, evaluatePriority);

        if (!_reseededThisEpoch)
        {
            ReseedFrontier(context, residentColumns, evaluatePriority);
        }

        CompactFrontierIfNeeded(context);

        if (workBudget <= 0)
        {
            ThrottleState = DesiredSearchThrottleState.BudgetLimited;
            LastSearchMs = stopwatch.Elapsed.TotalMilliseconds;
            return;
        }

        int work = 0;
        while (work < workBudget)
        {
            TrimSelectedToBudget(context, evaluatePriority);

            if (_frontier.Count == 0)
            {
                if (!_frontierExhaustedThisEpoch)
                {
                    _frontierExhaustedThisEpoch = true;
                    ReseedFrontier(context, residentColumns, evaluatePriority);
                }

                if (_frontier.Count == 0)
                {
                    ThrottleState = DesiredSearchThrottleState.FrontierLimited;
                    break;
                }
            }

            FrontierEntry entry = _frontier.Dequeue();
            work++;

            if (!_candidateStates.TryGetValue(entry.Key, out CandidateState state) || state.Token != entry.Token)
            {
                continue;
            }

            if (!context.Contains(entry.Key))
            {
                _candidateStates.Remove(entry.Key);
                continue;
            }

            ColumnPriorityInfo info = evaluatePriority(entry.Key);
            if (entry.Epoch != _epoch || !ScoresMatch(state.Score, info.TotalScore))
            {
                EnqueueCandidate(info);
                StaleRefreshCount++;
                continue;
            }

            if (_desiredColumns.Contains(entry.Key))
            {
                continue;
            }

            if (_desiredColumns.Count < context.MaxColumns)
            {
                SelectColumn(entry.Key, info, context, evaluatePriority);
                continue;
            }

            if (!TryGetLowestSelected(context, evaluatePriority, out SelectedEntry lowest))
            {
                SelectColumn(entry.Key, info, context, evaluatePriority);
                continue;
            }

            if (info.TotalScore <= lowest.Score && !info.IsGuaranteed)
            {
                _settledThisEpoch = true;
                ThrottleState = DesiredSearchThrottleState.ThresholdLimited;
                break;
            }

            RemoveSelected(lowest.Key);
            SelectColumn(entry.Key, info, context, evaluatePriority);
        }

        if (work >= workBudget && _frontier.Count > 0 && _desiredColumns.Count < context.MaxColumns)
        {
            ThrottleState = DesiredSearchThrottleState.BudgetLimited;
        }

        if (_desiredColumns.Count >= context.MaxColumns && _frontier.Count == 0)
        {
            ThrottleState = DesiredSearchThrottleState.ThresholdLimited;
        }
        else if (_desiredColumns.Count >= context.MaxColumns && work == 0)
        {
            ThrottleState = DesiredSearchThrottleState.ThresholdLimited;
        }

        LastSearchMs = stopwatch.Elapsed.TotalMilliseconds;
    }

    private void EnsureGuaranteedColumns(
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        int radius = Mathf.CeilToInt(Mathf.Max(context.GuaranteedRadius, 1.0f));
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2 offset = new(x, z);
                if (offset.Length() > context.GuaranteedRadius + 0.35f)
                {
                    continue;
                }

                Vector2I key = new(context.CenterChunk.X + x, context.CenterChunk.Y + z);
                if (_desiredColumns.Contains(key))
                {
                    continue;
                }

                SelectColumn(key, evaluatePriority(key), context, evaluatePriority);
            }
        }
    }

    private void SelectColumn(
        Vector2I key,
        ColumnPriorityInfo info,
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        if (!_desiredColumns.Add(key))
        {
            return;
        }

        _settledThisEpoch = false;
        _candidateStates.Remove(key);
        int token = _nextToken++;
        _selectedStates[key] = new SelectedState(token, info.TotalScore);
        _selectedColumns.Enqueue(new SelectedEntry(key, token, info.TotalScore), info.TotalScore);
        LastSelectedColumnInfo = info;

        foreach (Vector2I neighbor in EnumerateNeighbors(key))
        {
            if (!context.Contains(neighbor) || _desiredColumns.Contains(neighbor))
            {
                continue;
            }

            EnqueueCandidate(evaluatePriority(neighbor));
        }
    }

    private void RemoveSelected(Vector2I key)
    {
        _settledThisEpoch = false;
        _desiredColumns.Remove(key);
        _selectedStates.Remove(key);
    }

    private void TrimSelectedToBudget(
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        while (_desiredColumns.Count > context.MaxColumns &&
               TryGetLowestSelected(context, evaluatePriority, out SelectedEntry lowest))
        {
            RemoveSelected(lowest.Key);
        }
    }

    private bool TryGetLowestSelected(
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority,
        out SelectedEntry selected)
    {
        while (_selectedColumns.Count > 0)
        {
            SelectedEntry entry = _selectedColumns.Dequeue();
            if (!_selectedStates.TryGetValue(entry.Key, out SelectedState state) || state.Token != entry.Token)
            {
                continue;
            }

            if (!context.Contains(entry.Key))
            {
                RemoveSelected(entry.Key);
                continue;
            }

            ColumnPriorityInfo info = evaluatePriority(entry.Key);
            if (!ScoresMatch(state.Score, info.TotalScore))
            {
                int refreshedToken = _nextToken++;
                _selectedStates[entry.Key] = new SelectedState(refreshedToken, info.TotalScore);
                _selectedColumns.Enqueue(new SelectedEntry(entry.Key, refreshedToken, info.TotalScore), info.TotalScore);
                continue;
            }

            selected = new SelectedEntry(entry.Key, state.Token, state.Score);
            return true;
        }

        selected = default;
        return false;
    }

    private void RebuildSelectedScores(
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        _selectedColumns.Clear();
        _selectedStates.Clear();

        List<Vector2I> validColumns = new(_desiredColumns);
        foreach (Vector2I key in validColumns)
        {
            if (!context.Contains(key))
            {
                _desiredColumns.Remove(key);
                continue;
            }

            ColumnPriorityInfo info = evaluatePriority(key);
            int token = _nextToken++;
            _selectedStates[key] = new SelectedState(token, info.TotalScore);
            _selectedColumns.Enqueue(new SelectedEntry(key, token, info.TotalScore), info.TotalScore);
        }

        _selectedScoresDirty = false;
    }

    private void PruneOutOfRangeColumns(TerrainDesiredSetContext context)
    {
        List<Vector2I> desiredKeys = new(_desiredColumns);
        foreach (Vector2I key in desiredKeys)
        {
            if (context.Contains(key, extraMargin: 1.25f))
            {
                continue;
            }

            _desiredColumns.Remove(key);
            _selectedStates.Remove(key);
        }

        List<Vector2I> candidateKeys = new(_candidateStates.Keys);
        foreach (Vector2I key in candidateKeys)
        {
            if (context.Contains(key, extraMargin: 2.5f))
            {
                continue;
            }

            _candidateStates.Remove(key);
        }
    }

    private void ReseedFrontier(
        TerrainDesiredSetContext context,
        IEnumerable<Vector2I> residentColumns,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        _reseededThisEpoch = true;
        _frontierExhaustedThisEpoch = false;
        _settledThisEpoch = false;

        EnqueueSeed(context.CenterChunk, context, evaluatePriority);
        foreach (Vector2I neighbor in EnumerateNeighbors(context.CenterChunk))
        {
            EnqueueSeed(neighbor, context, evaluatePriority);
        }

        foreach (Vector2I key in EnumerateBoundaryColumns(_desiredColumns))
        {
            EnqueueSeed(key, context, evaluatePriority);
        }

        foreach (Vector2I key in EnumerateBoundaryColumns(residentColumns))
        {
            EnqueueSeed(key, context, evaluatePriority);
        }
    }

    private void EnqueueSeed(
        Vector2I key,
        TerrainDesiredSetContext context,
        System.Func<Vector2I, ColumnPriorityInfo> evaluatePriority)
    {
        if (!context.Contains(key))
        {
            return;
        }

        EnqueueCandidate(evaluatePriority(key));
        foreach (Vector2I neighbor in EnumerateNeighbors(key))
        {
            if (!context.Contains(neighbor))
            {
                continue;
            }

            EnqueueCandidate(evaluatePriority(neighbor));
        }
    }

    private void EnqueueCandidate(ColumnPriorityInfo info)
    {
        if (_candidateStates.TryGetValue(info.Key, out CandidateState existing) &&
            existing.Epoch == _epoch &&
            ScoresMatch(existing.Score, info.TotalScore))
        {
            return;
        }

        int token = _nextToken++;
        _candidateStates[info.Key] = new CandidateState(token, info.TotalScore, _epoch);
        _frontier.Enqueue(new FrontierEntry(info.Key, token, _epoch), -info.TotalScore);
    }

    private void CompactFrontierIfNeeded(TerrainDesiredSetContext context)
    {
        int maxReasonableCount = Mathf.Max(128, _candidateStates.Count * 4);
        if (_frontier.Count <= maxReasonableCount)
        {
            return;
        }

        List<(Vector2I Key, CandidateState State)> retained = new(_candidateStates.Count);
        foreach (KeyValuePair<Vector2I, CandidateState> entry in _candidateStates)
        {
            if (!context.Contains(entry.Key, extraMargin: 2.5f))
            {
                continue;
            }

            retained.Add((entry.Key, entry.Value));
        }

        _frontier.Clear();
        foreach ((Vector2I key, CandidateState state) in retained)
        {
            _frontier.Enqueue(new FrontierEntry(key, state.Token, state.Epoch), -state.Score);
        }

        FrontierCompactionCount++;
    }

    private static bool ScoresMatch(float a, float b)
    {
        return Mathf.Abs(a - b) <= 0.01f;
    }

    private static IEnumerable<Vector2I> EnumerateNeighbors(Vector2I key)
    {
        foreach (Vector2I offset in NeighborOffsets)
        {
            yield return key + offset;
        }
    }

    private static IEnumerable<Vector2I> EnumerateBoundaryColumns(IEnumerable<Vector2I> columns)
    {
        HashSet<Vector2I> set = new(columns);
        foreach (Vector2I key in set)
        {
            foreach (Vector2I offset in NeighborOffsets)
            {
                if (!set.Contains(key + offset))
                {
                    yield return key;
                    break;
                }
            }
        }
    }

    private readonly record struct FrontierEntry(Vector2I Key, int Token, int Epoch);
    private readonly record struct CandidateState(int Token, float Score, int Epoch);
    private readonly record struct SelectedState(int Token, float Score);
    private readonly record struct SelectedEntry(Vector2I Key, int Token, float Score);
}
