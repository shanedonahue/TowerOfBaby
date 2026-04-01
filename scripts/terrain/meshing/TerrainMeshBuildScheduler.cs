using Godot;
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

internal readonly record struct TerrainMeshQueueResult(bool Enqueued, bool Coalesced);

internal readonly record struct TerrainVisualBuildRequest(
    TerrainChunk Chunk,
    Vector3I Key,
    TerrainVisualBuildRequestKind Kind,
    float PriorityScore,
    TerrainMeshDetailMode DetailMode,
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

internal readonly record struct TerrainVisualBuildCompletedJob(
    TerrainVisualBuildJob Job,
    VoxelMeshBuildResult MeshResult,
    double WorkerBuildMs);

internal sealed class TerrainMeshBuildScheduler
{
    private readonly PriorityQueue<QueuedEntry, QueuePriority> _queuedRequests = new();
    private readonly Dictionary<Vector3I, QueuedState> _queuedStates = new();
    private readonly Dictionary<Vector3I, RunningState> _runningStates = new();
    private readonly ConcurrentQueue<TerrainVisualBuildCompletedJob> _completedJobs = new();

    private int _sequence;
    private int _activeJobs;

    public int QueuedCount => _queuedStates.Count;
    public int RunningCount => Volatile.Read(ref _activeJobs);
    public int CompletedCount => _completedJobs.Count;

    public bool HasPendingWork(Vector3I key)
    {
        return _queuedStates.ContainsKey(key) || _runningStates.ContainsKey(key);
    }

    public TerrainMeshQueueResult Queue(TerrainVisualBuildRequest request)
    {
        if (_queuedStates.TryGetValue(request.Key, out QueuedState queuedState))
        {
            int token = NextToken();
            TerrainVisualBuildRequest merged = MergeRequests(queuedState.Request, request);
            queuedState.Update(merged, token);
            _queuedRequests.Enqueue(new QueuedEntry(request.Key, token), ComposePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true);
        }

        if (_runningStates.TryGetValue(request.Key, out RunningState runningState))
        {
            runningState.MergePending(request);
            return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true);
        }

        int newToken = NextToken();
        _queuedStates[request.Key] = new QueuedState(request, newToken);
        _queuedRequests.Enqueue(new QueuedEntry(request.Key, newToken), ComposePriority(request, newToken));
        return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false);
    }

    public void StartJobs(
        int maxConcurrentJobs,
        System.Func<TerrainVisualBuildRequest, TerrainVisualBuildJob?> prepareJob,
        System.Func<TerrainVisualBuildJob, VoxelMeshBuildResult> executeJob)
    {
        while (RunningCount < maxConcurrentJobs && TryTakeNextQueuedRequest(out TerrainVisualBuildRequest request))
        {
            TerrainVisualBuildJob? preparedJob = prepareJob(request);
            if (!preparedJob.HasValue)
            {
                continue;
            }

            TerrainVisualBuildJob job = preparedJob.Value;
            _runningStates[job.Key] = new RunningState(request);
            Interlocked.Increment(ref _activeJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    VoxelMeshBuildResult meshResult = executeJob(job);
                    _completedJobs.Enqueue(new TerrainVisualBuildCompletedJob(job, meshResult, stopwatch.Elapsed.TotalMilliseconds));
                }
                finally
                {
                    Interlocked.Decrement(ref _activeJobs);
                }
            });
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

        return results;
    }

    private bool TryTakeNextQueuedRequest(out TerrainVisualBuildRequest request)
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
            return true;
        }

        request = default;
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
        string reason = string.IsNullOrWhiteSpace(next.Reason)
            ? current.Reason
            : next.Reason;
        return new TerrainVisualBuildRequest(current.Chunk, current.Key, kind, priorityScore, detailMode, reason);
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
        public QueuedState(TerrainVisualBuildRequest request, int token)
        {
            Request = request;
            Token = token;
        }

        public TerrainVisualBuildRequest Request { get; private set; }
        public int Token { get; private set; }

        public void Update(TerrainVisualBuildRequest request, int token)
        {
            Request = request;
            Token = token;
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
            PendingMergedRequest = PendingMergedRequest.HasValue
                ? MergeRequests(PendingMergedRequest.Value, request)
                : request;
        }
    }
}
