using Godot;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainLoadScheduler
{
    private readonly PriorityQueue<QueuedLoadEntry, float> _queuedLoads = new();
    private readonly Dictionary<Vector3I, QueuedLoadState> _queuedLoadStates = new();
    private readonly ConcurrentQueue<PreparedChunkResult> _completedLoads = new();
    private readonly PriorityQueue<ActivationEntry, float> _activationQueue = new();
    private readonly Dictionary<Vector3I, PreparedChunkResult> _preparedChunks = new();
    private readonly Dictionary<Vector3I, int> _activationTokens = new();
    private readonly HashSet<Vector3I> _targetKeys = new();
    private readonly object _runningLock = new();
    private readonly HashSet<Vector3I> _runningLoads = new();

    private int _sequence;
    private int _activeLoadJobs;

    public int PendingLoadCount => _queuedLoadStates.Count;
    public int PreparedCount => _preparedChunks.Count;
    public int RunningLoadCount => Volatile.Read(ref _activeLoadJobs);

    public void SyncTargets(IReadOnlySet<Vector3I> desiredKeys, IReadOnlyList<ChunkPriorityInfo> toAdd)
    {
        _targetKeys.Clear();
        foreach (Vector3I key in desiredKeys)
        {
            _targetKeys.Add(key);
        }

        foreach (ChunkPriorityInfo info in toAdd)
        {
            if (_preparedChunks.ContainsKey(info.Key))
            {
                EnqueueActivation(info.Key, info.TotalScore);
                continue;
            }

            if (IsRunning(info.Key))
            {
                continue;
            }

            QueueLoad(info.Key, info.TotalScore);
        }

        List<Vector3I> staleKeys = new();
        foreach (Vector3I key in _queuedLoadStates.Keys)
        {
            if (_targetKeys.Contains(key))
            {
                continue;
            }

            staleKeys.Add(key);
        }

        foreach (Vector3I key in staleKeys)
        {
            _queuedLoadStates.Remove(key);
        }
    }

    public void PopulateInFlightKeys(HashSet<Vector3I> destination)
    {
        foreach (Vector3I key in _queuedLoadStates.Keys)
        {
            destination.Add(key);
        }

        foreach (Vector3I key in _preparedChunks.Keys)
        {
            destination.Add(key);
        }

        lock (_runningLock)
        {
            foreach (Vector3I key in _runningLoads)
            {
                destination.Add(key);
            }
        }
    }

    public void StartLoads(int loadBudget, System.Func<Vector3I, float, PreparedChunkResult> loadChunk)
    {
        while (RunningLoadCount < loadBudget && TryDequeueNextLoad(out Vector3I key, out float priority))
        {
            lock (_runningLock)
            {
                if (!_runningLoads.Add(key))
                {
                    continue;
                }
            }

            Interlocked.Increment(ref _activeLoadJobs);
            _ = Task.Run(() =>
            {
                try
                {
                    PreparedChunkResult result = loadChunk(key, priority);
                    _completedLoads.Enqueue(result);
                }
                finally
                {
                    lock (_runningLock)
                    {
                        _runningLoads.Remove(key);
                    }

                    Interlocked.Decrement(ref _activeLoadJobs);
                }
            });
        }
    }

    public List<PreparedChunkResult> DrainCompletedLoads()
    {
        List<PreparedChunkResult> results = new();
        while (_completedLoads.TryDequeue(out PreparedChunkResult result))
        {
            results.Add(result);
        }

        return results;
    }

    public bool IsTargetKey(Vector3I key)
    {
        return _targetKeys.Contains(key);
    }

    public void RegisterPreparedChunk(PreparedChunkResult result)
    {
        _preparedChunks[result.Key] = result;
        EnqueueActivation(result.Key, result.PriorityScore);
    }

    public List<PreparedChunkResult> ExtractPreparedOutsideTargets()
    {
        List<PreparedChunkResult> removed = new();
        List<Vector3I> staleKeys = new();
        foreach (KeyValuePair<Vector3I, PreparedChunkResult> entry in _preparedChunks)
        {
            if (_targetKeys.Contains(entry.Key))
            {
                continue;
            }

            staleKeys.Add(entry.Key);
            removed.Add(entry.Value);
        }

        foreach (Vector3I key in staleKeys)
        {
            _preparedChunks.Remove(key);
            _activationTokens.Remove(key);
        }

        return removed;
    }

    public bool TryTakeNextActivation(out PreparedChunkResult result)
    {
        while (_activationQueue.Count > 0)
        {
            ActivationEntry entry = _activationQueue.Dequeue();
            if (!_activationTokens.TryGetValue(entry.Key, out int token) || token != entry.Token)
            {
                continue;
            }

            if (!_preparedChunks.TryGetValue(entry.Key, out PreparedChunkResult prepared))
            {
                continue;
            }

            if (!_targetKeys.Contains(entry.Key))
            {
                continue;
            }

            _preparedChunks.Remove(entry.Key);
            _activationTokens.Remove(entry.Key);
            result = prepared;
            return true;
        }

        result = null!;
        return false;
    }

    private void EnqueueActivation(Vector3I key, float priority)
    {
        int token = ++_sequence;
        _activationTokens[key] = token;
        _activationQueue.Enqueue(new ActivationEntry(key, token), -priority);
    }

    private void QueueLoad(Vector3I key, float priority)
    {
        if (_queuedLoadStates.TryGetValue(key, out QueuedLoadState existing) &&
            Mathf.Abs(existing.Priority - priority) <= 0.01f)
        {
            return;
        }

        int token = ++_sequence;
        _queuedLoadStates[key] = new QueuedLoadState(token, priority);
        _queuedLoads.Enqueue(new QueuedLoadEntry(key, token), -priority);
    }

    private bool TryDequeueNextLoad(out Vector3I key, out float priority)
    {
        while (_queuedLoads.Count > 0)
        {
            QueuedLoadEntry entry = _queuedLoads.Dequeue();
            if (!_queuedLoadStates.TryGetValue(entry.Key, out QueuedLoadState state) || state.Token != entry.Token)
            {
                continue;
            }

            if (!_targetKeys.Contains(entry.Key))
            {
                _queuedLoadStates.Remove(entry.Key);
                continue;
            }

            _queuedLoadStates.Remove(entry.Key);
            key = entry.Key;
            priority = state.Priority;
            return true;
        }

        key = default;
        priority = 0.0f;
        return false;
    }

    private bool IsRunning(Vector3I key)
    {
        lock (_runningLock)
        {
            return _runningLoads.Contains(key);
        }
    }

    private readonly record struct QueuedLoadEntry(Vector3I Key, int Token);
    private readonly record struct QueuedLoadState(int Token, float Priority);
    private readonly record struct ActivationEntry(Vector3I Key, int Token);
}

internal sealed record PreparedChunkResult(
    Vector3I Key,
    TowerOfBaby.Terrain.Voxel.VoxelChunkData Data,
    TerrainChunkLoadSource Source,
    double LoadMs,
    float PriorityScore);
