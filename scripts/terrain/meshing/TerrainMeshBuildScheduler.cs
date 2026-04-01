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

internal readonly record struct TerrainMeshQueueResult(bool Enqueued, bool Coalesced, bool Deferred);

internal readonly record struct TerrainVisualBuildRequest(
    TerrainChunk Chunk,
    Vector3I Key,
    TerrainVisualBuildRequestKind Kind,
    float PriorityScore,
    TerrainMeshDetailMode DetailMode,
    bool BypassBackpressure,
    string Reason);

internal readonly record struct TerrainVisualBuildJob(
    TerrainChunk Chunk,
    Vector3I Key,
    int Revision,
    TerrainVisualBuildRequestKind Kind,
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

    private readonly PriorityQueue<QueuedEntry, QueuePriority> _queuedRequests = new();
    private readonly Dictionary<Vector3I, QueuedState> _queuedStates = new();
    private readonly PriorityQueue<QueuedEntry, QueuePriority> _deferredRequests = new();
    private readonly Dictionary<Vector3I, QueuedState> _deferredStates = new();
    private readonly Dictionary<Vector3I, RunningState> _runningStates = new();
    private readonly ConcurrentQueue<TerrainVisualBuildCompletedJob> _completedJobs = new();

    private int _activeQueueLimit = int.MaxValue;
    private int _sequence;
    private int _activeJobs;
    private long _startedJobCount;
    private double _totalQueueWaitMs;
    private double _lastQueueWaitMs;
    private double _peakQueueWaitMs;
    private long _lowPriorityDeferredCount;

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

    public void SetActiveQueueLimit(int maxQueuedJobs)
    {
        _activeQueueLimit = Mathf.Max(1, maxQueuedJobs);
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
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: false);
            }

            int token = NextToken();
            queuedState.Update(merged, token, Stopwatch.GetTimestamp());
            _queuedRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
        }

        if (_deferredStates.TryGetValue(request.Key, out QueuedState deferredState))
        {
            TerrainVisualBuildRequest merged = MergeRequests(deferredState.Request, request);
            if (!ShouldRefreshQueueState(deferredState.Request, merged))
            {
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: true);
            }

            int token = NextToken();
            deferredState.Update(merged, token, Stopwatch.GetTimestamp());
            if (CanEnterActiveQueue(merged))
            {
                _deferredStates.Remove(request.Key);
                _queuedStates[request.Key] = deferredState;
                _queuedRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
                return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
            }

            _deferredRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: true);
        }

        if (_runningStates.TryGetValue(request.Key, out RunningState runningState))
        {
            runningState.MergePending(request);
            return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: false);
        }

        long queuedTimestamp = Stopwatch.GetTimestamp();
        int newToken = NextToken();
        if (CanEnterActiveQueue(request))
        {
            EnqueueActive(request, newToken, queuedTimestamp);
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: false);
        }

        EnqueueDeferred(request, newToken, queuedTimestamp);
        _lowPriorityDeferredCount++;
        return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: true);
    }

    public void StartJobs(
        int maxConcurrentJobs,
        System.Func<TerrainVisualBuildRequest, TerrainVisualBuildJob?> prepareJob,
        System.Func<TerrainVisualBuildJob, TerrainVisualBuildExecutionResult> executeJob)
    {
        PromoteDeferredRequests();
        while (RunningCount < maxConcurrentJobs &&
               TryTakeNextQueuedRequest(out TerrainVisualBuildRequest request, out double queueWaitMs, out int queueDepthOnStart))
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
        out TerrainVisualBuildRequest request,
        out double queueWaitMs,
        out int queueDepthOnStart)
    {
        while (_queuedRequests.Count > 0)
        {
            QueuedEntry entry = _queuedRequests.Dequeue();
            if (!_queuedStates.TryGetValue(entry.Key, out QueuedState queuedState) || queuedState.Token != entry.Token)
            {
                continue;
            }

            _queuedStates.Remove(entry.Key);
            request = queuedState.Request;
            queueWaitMs = GetElapsedMilliseconds(queuedState.EnqueuedTimestamp);
            queueDepthOnStart = _queuedStates.Count + _deferredStates.Count + _runningStates.Count + 1;
            return true;
        }

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
        TerrainVisualBuildRequestKind kind = GetPriorityLane(next.Kind) < GetPriorityLane(current.Kind)
            ? next.Kind
            : current.Kind;
        float priorityScore = Mathf.Max(current.PriorityScore, next.PriorityScore);
        TerrainMeshDetailMode detailMode = (TerrainMeshDetailMode)Mathf.Max((int)current.DetailMode, (int)next.DetailMode);
        bool bypassBackpressure = current.BypassBackpressure || next.BypassBackpressure;
        string reason = string.IsNullOrWhiteSpace(next.Reason)
            ? current.Reason
            : next.Reason;
        return new TerrainVisualBuildRequest(current.Chunk, current.Key, kind, priorityScore, detailMode, bypassBackpressure, reason);
    }

    private static QueuePriority ComposePriority(TerrainVisualBuildRequest request, int token)
    {
        return new QueuePriority(
            GetPriorityLane(request.Kind),
            -request.PriorityScore,
            token);
    }

    private static int GetPriorityLane(TerrainVisualBuildRequestKind kind)
    {
        return kind switch
        {
            TerrainVisualBuildRequestKind.Edit => 0,
            TerrainVisualBuildRequestKind.InitialCoarse => 1,
            _ => 2
        };
    }

    private bool CanEnterActiveQueue(TerrainVisualBuildRequest request)
    {
        return request.BypassBackpressure || GetActiveRequestCount() < _activeQueueLimit;
    }

    private int GetActiveRequestCount()
    {
        return _queuedStates.Count + _runningStates.Count;
    }

    private void EnqueueActive(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
    {
        _queuedStates[request.Key] = new QueuedState(request, token, enqueuedTimestamp);
        _queuedRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(request, token));
    }

    private void EnqueueDeferred(TerrainVisualBuildRequest request, int token, long enqueuedTimestamp)
    {
        _deferredStates[request.Key] = new QueuedState(request, token, enqueuedTimestamp);
        _deferredRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(request, token));
    }

    private void PromoteDeferredRequests()
    {
        while (GetActiveRequestCount() < _activeQueueLimit &&
               TryTakeNextDeferredRequest(out QueuedState deferredState))
        {
            int token = NextToken();
            deferredState.Update(deferredState.Request, token, deferredState.EnqueuedTimestamp);
            _queuedStates[deferredState.Request.Key] = deferredState;
            _queuedRequests.Enqueue(new QueuedEntry(deferredState.Request.Key, token), ComposePriority(deferredState.Request, token));
        }
    }

    private bool TryTakeNextDeferredRequest(out QueuedState state)
    {
        while (_deferredRequests.Count > 0)
        {
            QueuedEntry entry = _deferredRequests.Dequeue();
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

    private static double GetElapsedMilliseconds(long startTimestamp)
    {
        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        return elapsedTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static bool ShouldRefreshQueueState(TerrainVisualBuildRequest current, TerrainVisualBuildRequest merged)
    {
        return
            GetPriorityLane(merged.Kind) < GetPriorityLane(current.Kind) ||
            merged.DetailMode > current.DetailMode ||
            (merged.BypassBackpressure && !current.BypassBackpressure) ||
            merged.PriorityScore > (current.PriorityScore + MaterialPriorityUpgradeDelta);
    }

    private readonly record struct QueuedEntry(Vector3I Key, int Token);

    private readonly record struct QueuePriority(int Lane, float NegativePriorityScore, int Token)
        : System.IComparable<QueuePriority>
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

        public void MergePending(TerrainVisualBuildRequest request)
        {
            TerrainVisualBuildRequest baseline = PendingMergedRequest ?? Request;
            TerrainVisualBuildRequest merged = MergeRequests(baseline, request);
            if (!ShouldRefreshQueueState(baseline, merged))
            {
                return;
            }

            PendingMergedRequest = merged;
        }
    }
}
