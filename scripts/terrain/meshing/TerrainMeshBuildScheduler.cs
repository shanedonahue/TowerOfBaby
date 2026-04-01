using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal enum TerrainVisualBuildRequestKind
{
    Edit = 0,
    InitialCoarse = 1,
    DetailPromotion = 2
}

internal enum TerrainMeshDetailMode
{
    CoarseOnly = 0,
    IncludeTransientDetail = 1
}

internal enum TerrainVisualBuildQueueClass
{
    Critical = 0,
    NearCoarse = 1,
    Background = 2
}

internal readonly record struct TerrainMeshQueueResult(
    bool Enqueued,
    bool Coalesced,
    bool Deferred,
    bool Skipped = false,
    bool Suppressed = false);

internal readonly record struct TerrainMeshBuildExecutionBudget(
    int MaxConcurrentJobs,
    int MaxEditJobs,
    int MaxCoarseJobs,
    int MaxDetailJobs,
    int MaxBackgroundJobs);

internal readonly record struct TerrainVisualBuildRequest(
    TerrainChunk Chunk,
    Vector3I Key,
    TerrainVisualBuildRequestKind Kind,
    TerrainVisualBuildQueueClass QueueClass,
    float PriorityScore,
    TerrainMeshDetailMode DetailMode,
    bool BypassBackpressure,
    string Reason);

internal readonly record struct TerrainVisualBuildJob(
    TerrainChunk Chunk,
    Vector3I Key,
    int Revision,
    TerrainVisualBuildRequestKind Kind,
    TerrainVisualBuildQueueClass QueueClass,
    float PriorityScore,
    TerrainMeshDetailMode DetailMode,
    string Reason,
    TerrainChunkDirtyBoundsSnapshot DirtyBounds,
    VoxelChunkData DataSnapshot);

internal readonly record struct TerrainVisualBuildExecutionResult(
    VoxelMeshBuildResult MeshResult,
    long ManagedHeapDeltaBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal readonly record struct TerrainVisualBuildCompletedJob(
    TerrainVisualBuildJob Job,
    TerrainVisualBuildExecutionResult ExecutionResult,
    double WorkerBuildMs,
    double QueueWaitMs,
    int QueueDepthOnStart);

internal sealed class TerrainMeshBuildScheduler
{
    private const float MaterialPriorityUpgradeDelta = 4.0f;
    private static readonly TerrainVisualBuildQueueClass[] QueueClassOrder =
    {
        TerrainVisualBuildQueueClass.Critical,
        TerrainVisualBuildQueueClass.NearCoarse,
        TerrainVisualBuildQueueClass.Background
    };

    private readonly PriorityQueue<QueuedEntry, QueuePriority>[] _queuedRequests = CreateQueueBuckets();
    private readonly Dictionary<Vector3I, QueuedState> _queuedStates = new();
    private readonly PriorityQueue<QueuedEntry, QueuePriority>[] _deferredRequests = CreateQueueBuckets();
    private readonly Dictionary<Vector3I, QueuedState> _deferredStates = new();
    private readonly Dictionary<Vector3I, RunningState> _runningStates = new();
    private readonly ConcurrentQueue<TerrainVisualBuildCompletedJob> _completedJobs = new();

    private int _activeQueueLimit = int.MaxValue;
    private int _lowPriorityActiveQueueLimit = 1;
    private int _maxDeferredLowPriorityBuilds = 16;
    private int _sequence;
    private int _activeJobs;
    private long _startedJobCount;
    private double _totalQueueWaitMs;
    private double _lastQueueWaitMs;
    private double _peakQueueWaitMs;
    private long _lowPriorityDeferredCount;
    private long _skippedLowPriorityCount;
    private long _suppressedDuplicateCount;

    public int QueuedCount => _queuedStates.Count;
    public int DeferredCount => _deferredStates.Count;
    public int RunningCount => Volatile.Read(ref _activeJobs);
    public int CompletedCount => _completedJobs.Count;
    public long StartedJobCount => _startedJobCount;
    public double TotalQueueWaitMs => _totalQueueWaitMs;
    public double LastQueueWaitMs => _lastQueueWaitMs;
    public double PeakQueueWaitMs => _peakQueueWaitMs;
    public double AverageQueueWaitMs => _startedJobCount > 0
        ? _totalQueueWaitMs / _startedJobCount
        : 0.0;
    public long LowPriorityDeferredCount => _lowPriorityDeferredCount;
    public long SkippedLowPriorityCount => _skippedLowPriorityCount;
    public long SuppressedDuplicateCount => _suppressedDuplicateCount;
    public int HighPriorityQueueDepth => CountRequests(static request => request.QueueClass != TerrainVisualBuildQueueClass.Background, includeDeferred: true);
    public int LowPriorityQueueDepth => CountRequests(static request => request.QueueClass == TerrainVisualBuildQueueClass.Background, includeDeferred: true);
    public int HighPriorityRunningCount => CountRunningRequests(static request => request.QueueClass != TerrainVisualBuildQueueClass.Background);
    public int ForegroundCoarseQueueDepth => CountRequests(
        static request => request.Kind == TerrainVisualBuildRequestKind.InitialCoarse &&
            request.QueueClass != TerrainVisualBuildQueueClass.Background,
        includeDeferred: true);
    public int ForegroundCoarseRunningCount => CountRunningRequests(
        static request => request.Kind == TerrainVisualBuildRequestKind.InitialCoarse &&
            request.QueueClass != TerrainVisualBuildQueueClass.Background);
    public int CriticalDetailQueueDepth => CountRequests(
        static request => request.Kind == TerrainVisualBuildRequestKind.DetailPromotion &&
            request.QueueClass == TerrainVisualBuildQueueClass.Critical,
        includeDeferred: true);
    public int CriticalDetailRunningCount => CountRunningRequests(
        static request => request.Kind == TerrainVisualBuildRequestKind.DetailPromotion &&
            request.QueueClass == TerrainVisualBuildQueueClass.Critical);
    public bool HasHighPriorityDemand => HighPriorityQueueDepth > 0 || HighPriorityRunningCount > 0;
    public bool HasForegroundCoarseDemand => ForegroundCoarseQueueDepth > 0 || ForegroundCoarseRunningCount > 0;
    public bool HasCriticalDetailDemand => CriticalDetailQueueDepth > 0 || CriticalDetailRunningCount > 0;

    public void SetActiveQueueLimit(int maxQueuedJobs)
    {
        _activeQueueLimit = Mathf.Max(1, maxQueuedJobs);
    }

    public void SetLowPriorityLimits(int maxActiveLowPriorityRequests, int maxDeferredLowPriorityBuilds)
    {
        _lowPriorityActiveQueueLimit = Mathf.Max(0, maxActiveLowPriorityRequests);
        _maxDeferredLowPriorityBuilds = Mathf.Max(0, maxDeferredLowPriorityBuilds);
    }

    public bool HasPendingWork(Vector3I key)
    {
        return
            _queuedStates.ContainsKey(key) ||
            _deferredStates.ContainsKey(key) ||
            _runningStates.ContainsKey(key);
    }

    public TerrainMeshQueueResult Queue(TerrainVisualBuildRequest request)
    {
        if (_queuedStates.TryGetValue(request.Key, out QueuedState queuedState))
        {
            TerrainVisualBuildRequest merged = MergeRequests(queuedState.Request, request);
            if (!ShouldRefreshQueueState(queuedState.Request, merged))
            {
                _suppressedDuplicateCount++;
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: false, Suppressed: true);
            }

            int token = NextToken();
            queuedState.Update(merged, token, Stopwatch.GetTimestamp());
            _queuedRequests[(int)merged.QueueClass].Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
        }

        if (_deferredStates.TryGetValue(request.Key, out QueuedState deferredState))
        {
            TerrainVisualBuildRequest merged = MergeRequests(deferredState.Request, request);
            if (!ShouldRefreshQueueState(deferredState.Request, merged))
            {
                _suppressedDuplicateCount++;
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: true, Suppressed: true);
            }

            int token = NextToken();
            deferredState.Update(merged, token, Stopwatch.GetTimestamp());
            if (CanEnterActiveQueue(merged))
            {
                _deferredStates.Remove(request.Key);
                _queuedStates[request.Key] = deferredState;
                _queuedRequests[(int)merged.QueueClass].Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
                return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
            }

            _deferredRequests[(int)merged.QueueClass].Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: true);
        }

        if (_runningStates.TryGetValue(request.Key, out RunningState runningState))
        {
            if (!runningState.MergePending(request))
            {
                _suppressedDuplicateCount++;
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: false, Suppressed: true);
            }

            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
        }

        if (ShouldSkipLowPriorityRequest(request))
        {
            _skippedLowPriorityCount++;
            return new TerrainMeshQueueResult(Enqueued: false, Coalesced: false, Deferred: true, Skipped: true);
        }

        long queuedTimestamp = Stopwatch.GetTimestamp();
        int newToken = NextToken();
        if (CanEnterActiveQueue(request))
        {
            EnqueueActive(request, newToken, queuedTimestamp);
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: false);
        }

        EnqueueDeferred(request, newToken, queuedTimestamp);
        if (request.QueueClass == TerrainVisualBuildQueueClass.Background)
        {
            _lowPriorityDeferredCount++;
        }

        return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: true);
    }

    public void StartJobs(
        TerrainMeshBuildExecutionBudget budget,
        Func<TerrainVisualBuildRequest, TerrainVisualBuildJob?> prepareJob,
        Func<TerrainVisualBuildJob, TerrainVisualBuildExecutionResult> executeJob)
    {
        PromoteDeferredRequests();
        while (RunningCount < budget.MaxConcurrentJobs &&
               TryTakeNextQueuedRequest(budget, out TerrainVisualBuildRequest request, out double queueWaitMs, out int queueDepthOnStart))
        {
            TerrainVisualBuildJob? preparedJob = prepareJob(request);
            if (!preparedJob.HasValue)
            {
                PromoteDeferredRequests();
                continue;
            }

            TerrainVisualBuildJob job = preparedJob.Value;
            _runningStates[job.Key] = new RunningState(request);
            RecordQueueWait(queueWaitMs);
            Interlocked.Increment(ref _activeJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    TerrainVisualBuildExecutionResult executionResult = executeJob(job);
                    _completedJobs.Enqueue(
                        new TerrainVisualBuildCompletedJob(
                            job,
                            executionResult,
                            stopwatch.Elapsed.TotalMilliseconds,
                            queueWaitMs,
                            queueDepthOnStart));
                }
                finally
                {
                    Interlocked.Decrement(ref _activeJobs);
                }
            });
            PromoteDeferredRequests();
        }
    }

    public int TrimStaleBackgroundRequests(double maxWaitMs)
    {
        if (maxWaitMs <= 0.0)
        {
            return 0;
        }

        long cutoffTimestamp = Stopwatch.GetTimestamp() - (long)(maxWaitMs * Stopwatch.Frequency / 1000.0);
        int dropped = RemoveStaleRequests(_queuedStates, cutoffTimestamp);
        dropped += RemoveStaleRequests(_deferredStates, cutoffTimestamp);
        _skippedLowPriorityCount += dropped;
        return dropped;
    }

    public List<TerrainVisualBuildCompletedJob> DrainCompletedJobs()
    {
        List<TerrainVisualBuildCompletedJob> results = new();
        while (_completedJobs.TryDequeue(out TerrainVisualBuildCompletedJob completedJob))
        {
            if (_runningStates.TryGetValue(completedJob.Job.Key, out RunningState runningState))
            {
                _runningStates.Remove(completedJob.Job.Key);
                if (runningState.PendingMergedRequest.HasValue)
                {
                    Queue(runningState.PendingMergedRequest.Value);
                }
            }

            results.Add(completedJob);
        }

        PromoteDeferredRequests();
        return results;
    }

    private bool TryTakeNextQueuedRequest(
        TerrainMeshBuildExecutionBudget budget,
        out TerrainVisualBuildRequest request,
        out double queueWaitMs,
        out int queueDepthOnStart)
    {
        foreach (TerrainVisualBuildQueueClass queueClass in QueueClassOrder)
        {
            if (TryTakeNextQueuedRequest(queueClass, budget, out request, out queueWaitMs, out queueDepthOnStart))
            {
                return true;
            }
        }

        request = default;
        queueWaitMs = 0.0;
        queueDepthOnStart = 0;
        return false;
    }

    private bool TryTakeNextQueuedRequest(
        TerrainVisualBuildQueueClass queueClass,
        TerrainMeshBuildExecutionBudget budget,
        out TerrainVisualBuildRequest request,
        out double queueWaitMs,
        out int queueDepthOnStart)
    {
        PriorityQueue<QueuedEntry, QueuePriority> queue = _queuedRequests[(int)queueClass];
        List<(QueuedEntry Entry, QueuePriority Priority)> blockedEntries = new();
        while (queue.Count > 0)
        {
            QueuedEntry entry = queue.Dequeue();
            if (!_queuedStates.TryGetValue(entry.Key, out QueuedState queuedState) || queuedState.Token != entry.Token)
            {
                continue;
            }

            QueuePriority priority = ComposePriority(queuedState.Request, queuedState.Token);
            if (!CanStartRequest(queuedState.Request, budget))
            {
                blockedEntries.Add((entry, priority));
                continue;
            }

            _queuedStates.Remove(entry.Key);
            RequeueBlockedEntries(queue, blockedEntries);
            request = queuedState.Request;
            queueWaitMs = GetElapsedMilliseconds(queuedState.EnqueuedTimestamp);
            queueDepthOnStart = _queuedStates.Count + _deferredStates.Count + _runningStates.Count + 1;
            return true;
        }

        RequeueBlockedEntries(queue, blockedEntries);
        request = default;
        queueWaitMs = 0.0;
        queueDepthOnStart = 0;
        return false;
    }

    private int NextToken()
    {
        return ++_sequence;
    }

    private static TerrainVisualBuildRequest MergeRequests(TerrainVisualBuildRequest current, TerrainVisualBuildRequest next)
    {
        TerrainVisualBuildRequestKind kind = GetKindPriorityLane(next.Kind) < GetKindPriorityLane(current.Kind)
            ? next.Kind
            : current.Kind;
        TerrainVisualBuildQueueClass queueClass = GetQueueClassPriorityLane(next.QueueClass) < GetQueueClassPriorityLane(current.QueueClass)
            ? next.QueueClass
            : current.QueueClass;
        float priorityScore = Mathf.Max(current.PriorityScore, next.PriorityScore);
        TerrainMeshDetailMode detailMode = (TerrainMeshDetailMode)Mathf.Max((int)current.DetailMode, (int)next.DetailMode);
        bool bypassBackpressure = current.BypassBackpressure || next.BypassBackpressure;
        string reason = string.IsNullOrWhiteSpace(next.Reason)
            ? current.Reason
            : next.Reason;
        return new TerrainVisualBuildRequest(current.Chunk, current.Key, kind, queueClass, priorityScore, detailMode, bypassBackpressure, reason);
    }

    private static QueuePriority ComposePriority(TerrainVisualBuildRequest request, int token)
    {
        return new QueuePriority(
            GetIntraQueuePriorityLane(request.Kind),
            -request.PriorityScore,
            token);
    }

    private static int GetKindPriorityLane(TerrainVisualBuildRequestKind kind)
    {
        return kind switch
        {
            TerrainVisualBuildRequestKind.Edit => 0,
            TerrainVisualBuildRequestKind.InitialCoarse => 1,
            _ => 2
        };
    }

    private static int GetQueueClassPriorityLane(TerrainVisualBuildQueueClass queueClass)
    {
        return queueClass switch
        {
            TerrainVisualBuildQueueClass.Critical => 0,
            TerrainVisualBuildQueueClass.NearCoarse => 1,
            _ => 2
        };
    }

    private static int GetIntraQueuePriorityLane(TerrainVisualBuildRequestKind kind)
    {
        return kind switch
        {
            TerrainVisualBuildRequestKind.DetailPromotion => 1,
            _ => 0
        };
    }

    private bool CanEnterActiveQueue(TerrainVisualBuildRequest request)
    {
        if (request.BypassBackpressure)
        {
            return true;
        }

        if (request.QueueClass == TerrainVisualBuildQueueClass.Critical)
        {
            return true;
        }

        if (GetActiveRequestCount() >= _activeQueueLimit)
        {
            return false;
        }

        if (request.QueueClass == TerrainVisualBuildQueueClass.NearCoarse)
        {
            return true;
        }

        return
            GetActiveLowPriorityRequestCount() < _lowPriorityActiveQueueLimit &&
            !HasHighPriorityDemand;
    }

    private bool CanStartRequest(TerrainVisualBuildRequest request, TerrainMeshBuildExecutionBudget budget)
    {
        if (budget.MaxConcurrentJobs <= 0 || RunningCount >= budget.MaxConcurrentJobs)
        {
            return false;
        }

        if (request.Kind == TerrainVisualBuildRequestKind.Edit)
        {
            int editLimit = Mathf.Max(1, budget.MaxEditJobs);
            if (CountRunningRequests(static running => running.Kind == TerrainVisualBuildRequestKind.Edit) >= editLimit)
            {
                return false;
            }
        }

        if (request.Kind == TerrainVisualBuildRequestKind.InitialCoarse)
        {
            int coarseLimit = Mathf.Max(0, budget.MaxCoarseJobs);
            if (coarseLimit == 0 ||
                CountRunningRequests(static running => running.Kind == TerrainVisualBuildRequestKind.InitialCoarse) >= coarseLimit)
            {
                return false;
            }
        }

        if (request.Kind == TerrainVisualBuildRequestKind.DetailPromotion)
        {
            int detailLimit = Mathf.Max(0, budget.MaxDetailJobs);
            if (detailLimit == 0 ||
                CountRunningRequests(static running => running.Kind == TerrainVisualBuildRequestKind.DetailPromotion) >= detailLimit)
            {
                return false;
            }
        }

        if (request.QueueClass == TerrainVisualBuildQueueClass.Background &&
            CountRunningRequests(static running => running.QueueClass == TerrainVisualBuildQueueClass.Background) >= Mathf.Max(0, budget.MaxBackgroundJobs))
        {
            return false;
        }

        return true;
    }

    private bool ShouldSkipLowPriorityRequest(TerrainVisualBuildRequest request)
    {
        if (request.QueueClass != TerrainVisualBuildQueueClass.Background || request.BypassBackpressure)
        {
            return false;
        }

        return CountRequests(static queued => queued.QueueClass == TerrainVisualBuildQueueClass.Background, includeDeferred: true, includeRunning: true) >= _maxDeferredLowPriorityBuilds;
    }

    private int GetActiveRequestCount()
    {
        return _queuedStates.Count + _runningStates.Count;
    }

    private int GetActiveLowPriorityRequestCount()
    {
        return
            CountRequests(static request => request.QueueClass == TerrainVisualBuildQueueClass.Background, includeDeferred: false) +
            CountRunningRequests(static request => request.QueueClass == TerrainVisualBuildQueueClass.Background);
    }

    private void EnqueueActive(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
    {
        _queuedStates[request.Key] = new QueuedState(request, token, enqueuedTimestamp);
        _queuedRequests[(int)request.QueueClass].Enqueue(new QueuedEntry(request.Key, token), ComposePriority(request, token));
    }

    private void EnqueueDeferred(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
    {
        _deferredStates[request.Key] = new QueuedState(request, token, enqueuedTimestamp);
        _deferredRequests[(int)request.QueueClass].Enqueue(new QueuedEntry(request.Key, token), ComposePriority(request, token));
    }

    private void PromoteDeferredRequests()
    {
        bool promoted;
        do
        {
            promoted = false;
            foreach (TerrainVisualBuildQueueClass queueClass in QueueClassOrder)
            {
                if (!TryPeekNextDeferredRequest(queueClass, out TerrainVisualBuildRequest request))
                {
                    continue;
                }

                if (!CanEnterActiveQueue(request))
                {
                    continue;
                }

                if (!TryTakeNextDeferredRequest(queueClass, out QueuedState deferredState))
                {
                    continue;
                }

                int token = NextToken();
                deferredState.Update(deferredState.Request, token, deferredState.EnqueuedTimestamp);
                _queuedStates[deferredState.Request.Key] = deferredState;
                _queuedRequests[(int)deferredState.Request.QueueClass].Enqueue(
                    new QueuedEntry(deferredState.Request.Key, token),
                    ComposePriority(deferredState.Request, token));
                promoted = true;
                break;
            }
        }
        while (promoted);
    }

    private bool TryPeekNextDeferredRequest(TerrainVisualBuildQueueClass queueClass, out TerrainVisualBuildRequest request)
    {
        return TryPeekNextRequest(_deferredRequests[(int)queueClass], _deferredStates, out request);
    }

    private static bool TryPeekNextRequest(
        PriorityQueue<QueuedEntry, QueuePriority> queue,
        Dictionary<Vector3I, QueuedState> states,
        out TerrainVisualBuildRequest request)
    {
        while (queue.Count > 0)
        {
            QueuedEntry entry = queue.Peek();
            if (!states.TryGetValue(entry.Key, out QueuedState queuedState) || queuedState.Token != entry.Token)
            {
                queue.Dequeue();
                continue;
            }

            request = queuedState.Request;
            return true;
        }

        request = default;
        return false;
    }

    private bool TryTakeNextDeferredRequest(TerrainVisualBuildQueueClass queueClass, out QueuedState state)
    {
        PriorityQueue<QueuedEntry, QueuePriority> queue = _deferredRequests[(int)queueClass];
        while (queue.Count > 0)
        {
            QueuedEntry entry = queue.Dequeue();
            if (!_deferredStates.TryGetValue(entry.Key, out QueuedState deferredState) || deferredState.Token != entry.Token)
            {
                continue;
            }

            _deferredStates.Remove(entry.Key);
            state = deferredState;
            return true;
        }

        state = null!;
        return false;
    }

    private void RecordQueueWait(double queueWaitMs)
    {
        _startedJobCount++;
        _totalQueueWaitMs += queueWaitMs;
        _lastQueueWaitMs = queueWaitMs;
        _peakQueueWaitMs = Math.Max(_peakQueueWaitMs, queueWaitMs);
    }

    private int CountRequests(
        Func<TerrainVisualBuildRequest, bool> predicate,
        bool includeDeferred,
        bool includeRunning = false)
    {
        int count = 0;
        foreach (QueuedState state in _queuedStates.Values)
        {
            if (predicate(state.Request))
            {
                count++;
            }
        }

        if (includeDeferred)
        {
            foreach (QueuedState state in _deferredStates.Values)
            {
                if (predicate(state.Request))
                {
                    count++;
                }
            }
        }

        if (includeRunning)
        {
            foreach (RunningState state in _runningStates.Values)
            {
                if (predicate(state.Request))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private int CountRunningRequests(Func<TerrainVisualBuildRequest, bool> predicate)
    {
        int count = 0;
        foreach (RunningState state in _runningStates.Values)
        {
            if (predicate(state.Request))
            {
                count++;
            }
        }

        return count;
    }

    private static double GetElapsedMilliseconds(long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static int RemoveStaleRequests(Dictionary<Vector3I, QueuedState> states, long cutoffTimestamp)
    {
        if (states.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        List<Vector3I> keysToRemove = new();
        foreach ((Vector3I key, QueuedState state) in states)
        {
            if (state.Request.QueueClass != TerrainVisualBuildQueueClass.Background ||
                state.EnqueuedTimestamp > cutoffTimestamp)
            {
                continue;
            }

            keysToRemove.Add(key);
        }

        foreach (Vector3I key in keysToRemove)
        {
            if (states.Remove(key))
            {
                removed++;
            }
        }

        return removed;
    }

    private static void RequeueBlockedEntries(
        PriorityQueue<QueuedEntry, QueuePriority> queue,
        List<(QueuedEntry Entry, QueuePriority Priority)> blockedEntries)
    {
        foreach ((QueuedEntry entry, QueuePriority priority) in blockedEntries)
        {
            queue.Enqueue(entry, priority);
        }
    }

    private static bool ShouldRefreshQueueState(TerrainVisualBuildRequest current, TerrainVisualBuildRequest merged)
    {
        return
            GetQueueClassPriorityLane(merged.QueueClass) < GetQueueClassPriorityLane(current.QueueClass) ||
            GetKindPriorityLane(merged.Kind) < GetKindPriorityLane(current.Kind) ||
            merged.DetailMode > current.DetailMode ||
            (merged.BypassBackpressure && !current.BypassBackpressure) ||
            merged.PriorityScore > (current.PriorityScore + MaterialPriorityUpgradeDelta);
    }

    private static PriorityQueue<QueuedEntry, QueuePriority>[] CreateQueueBuckets()
    {
        return new[]
        {
            new PriorityQueue<QueuedEntry, QueuePriority>(),
            new PriorityQueue<QueuedEntry, QueuePriority>(),
            new PriorityQueue<QueuedEntry, QueuePriority>()
        };
    }

    private readonly record struct QueuedEntry(Vector3I Key, int Token);

    private readonly record struct QueuePriority(int Lane, float NegativePriorityScore, int Token)
        : IComparable<QueuePriority>
    {
        public int CompareTo(QueuePriority other)
        {
            int laneCompare = Lane.CompareTo(other.Lane);
            if (laneCompare != 0)
            {
                return laneCompare;
            }

            int scoreCompare = NegativePriorityScore.CompareTo(other.NegativePriorityScore);
            if (scoreCompare != 0)
            {
                return scoreCompare;
            }

            return Token.CompareTo(other.Token);
        }
    }

    private sealed class QueuedState
    {
        public QueuedState(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
        {
            Request = request;
            Token = token;
            EnqueuedTimestamp = enqueuedTimestamp;
        }

        public TerrainVisualBuildRequest Request { get; private set; }
        public int Token { get; private set; }
        public long EnqueuedTimestamp { get; private set; }

        public void Update(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
        {
            Request = request;
            Token = token;
            EnqueuedTimestamp = enqueuedTimestamp;
        }
    }

    private sealed class RunningState
    {
        public RunningState(TerrainVisualBuildRequest request)
        {
            Request = request;
        }

        public TerrainVisualBuildRequest Request { get; }
        public TerrainVisualBuildRequest? PendingMergedRequest { get; private set; }

        public bool MergePending(TerrainVisualBuildRequest request)
        {
            TerrainVisualBuildRequest baseline = PendingMergedRequest ?? Request;
            TerrainVisualBuildRequest merged = MergeRequests(baseline, request);
            if (!ShouldRefreshQueueState(baseline, merged))
            {
                return false;
            }

            PendingMergedRequest = merged;
            return true;
        }
    }
}
