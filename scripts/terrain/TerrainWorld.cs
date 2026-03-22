using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public partial class TerrainWorld : Node3D
{
    [Signal] public delegate void InitialLoadCompletedEventHandler();

    [Export] public PackedScene ChunkScene = null!;
    [Export] public NodePath TrackedCharacterPath = new();
    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;
    [Export] public float BaseY = -12.0f;
    [Export] public int Seed = 12345;
    [Export] public float TerrainHeight = 10.0f;
    [Export] public float DetailHeight = 2.8f;
    [Export] public float CaveScale = 9.0f;
    [Export] public float CaveThreshold = 0.63f;
    [Export] public bool UseHorizonLoadPriority = true;
    [Export] public float OccludedPriorityScale = 0.3f;
    [Export] public int SearchRadius = 8;
    [Export] public int MaxActiveColumns = 72;
    [Export] public float GuaranteedColumnRadius = 1.6f;
    [Export] public float ChunkVisibilityInset = 0.18f;
    [Export] public float LargeOccluderAngleMargin = 0.055f;
    [Export] public float MinOcclusionDistanceChunks = 5.5f;
    [Export] public float ForwardPriorityWeight = 26.0f;
    [Export] public float BehindViewerPenalty = 36.0f;
    [Export] public float BrushRadius = 2.4f;
    [Export] public float CarveStrength = -3.4f;
    [Export] public float BuildStrength = 2.8f;
    [Export] public int VerticalChunkCount = 3;
    [Export] public int MaxLoadedChunks = 120;
    [Export] public int MaxChunkGenerationJobs = 2;
    [Export] public int MaxChunkActivationsPerFrame = 2;
    [Export] public int MaxVisualChunkRebuildsPerFrame = 2;
    [Export] public int MaxCollisionChunkRebuildsPerFrame = 1;
    [Export] public float CollisionRebuildDelaySeconds = 0.08f;

    private readonly Dictionary<Vector3I, TerrainChunk> _chunks = new();
    private readonly HashSet<TerrainChunk> _dirtyRenderChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyCollisionChunks = new();
    private readonly Dictionary<Vector3I, ulong> _chunkTouchTicks = new();
    private readonly Dictionary<Vector2I, float> _columnRetention = new();
    private readonly List<Vector3I> _generationRequestQueue = new();
    private readonly Dictionary<Vector3I, float> _generationPriority = new();
    private readonly HashSet<Vector3I> _queuedGenerationKeys = new();
    private readonly HashSet<Vector3I> _runningGenerationKeys = new();
    private readonly ConcurrentQueue<GeneratedChunkResult> _completedGenerationQueue = new();

    private VoxelFieldGenerator _prioritySampler = null!;
    private TerrainWorldSettings _settings = null!;
    private Node3D _trackedCharacter = null!;
    private Vector2I _lastCenterChunk = new(int.MinValue, int.MinValue);
    private Vector2 _lastStreamForward = Vector2.Zero;
    private HashSet<Vector3I> _desiredChunks = new();
    private int _activeGenerationJobs;
    private int _lastVisualRebuildCount;
    private int _lastCollisionRebuildCount;
    private int _lastGenerationCompleteCount;
    private int _lastChunkActivationCount;
    private double _lastVisualRebuildMs;
    private double _lastCollisionRebuildMs;
    private double _lastGenerationMs;
    private double _lastChunkActivationMs;
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictedChunks;
    private bool _initialLoadComplete;

    public bool InitialLoadComplete => _initialLoadComplete;

    public override void _Ready()
    {
        AddToGroup("terrain_world");
        _settings = new TerrainWorldSettings
        {
            PointsPerAxis = PointsPerAxis,
            VoxelSize = VoxelSize,
            BaseY = BaseY
        };
        _prioritySampler = new VoxelFieldGenerator(Seed, TerrainHeight, DetailHeight, CaveScale, CaveThreshold);

        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;

        RefreshChunks(force: true);
    }

    public override void _Process(double delta)
    {
        ResetFrameStats();
        ProcessCompletedChunkGenerations();
        RefreshChunks(force: false);
        ProcessQueuedChunkGenerations();
        ProcessDirtyChunks();
        EvictInactiveChunks();
    }

    private void RefreshChunks(bool force)
    {
        Vector2I centerChunk = _trackedCharacter == null
            ? Vector2I.Zero
            : new Vector2I(
                Mathf.FloorToInt(_trackedCharacter.GlobalPosition.X / _settings.ChunkSize),
                Mathf.FloorToInt(_trackedCharacter.GlobalPosition.Z / _settings.ChunkSize));
        Vector2 streamForward = GetStreamingForward2D();

        bool forwardChanged = _lastStreamForward == Vector2.Zero
            ? streamForward != Vector2.Zero
            : (streamForward != Vector2.Zero && _lastStreamForward.Dot(streamForward) < 0.94f);

        if (!force && centerChunk == _lastCenterChunk && !forwardChanged)
        {
            return;
        }

        _lastCenterChunk = centerChunk;
        _lastStreamForward = streamForward;
        UpdateColumnRetention(centerChunk);
        HashSet<Vector3I> desired = BuildDesiredChunkSet(centerChunk, streamForward);

        _desiredChunks = desired;
        foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _chunks)
        {
            bool active = desired.Contains(entry.Key);
            entry.Value.Visible = active;
            entry.Value.ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            if (active)
            {
                TouchChunk(entry.Key);
            }
        }
    }

    private Vector2 GetStreamingForward2D()
    {
        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return Vector2.Zero;
        }

        Vector3 cameraForward = -camera.GlobalTransform.Basis.Z;
        Vector2 planarForward = new(cameraForward.X, cameraForward.Z);
        return planarForward.LengthSquared() < 0.0001f
            ? Vector2.Zero
            : planarForward.Normalized();
    }

    private void EnsureChunkRequested(Vector3I key, Vector2I centerChunk, Vector2 streamForward)
    {
        if (_chunks.ContainsKey(key))
        {
            _cacheHits++;
            TouchChunk(key);
            return;
        }

        float priority = ComputeChunkPriority(key, centerChunk, streamForward);

        if (_queuedGenerationKeys.Contains(key) || _runningGenerationKeys.Contains(key))
        {
            _generationPriority[key] = priority;
            return;
        }

        _cacheMisses++;
        _generationRequestQueue.Add(key);
        _generationPriority[key] = priority;
        _queuedGenerationKeys.Add(key);
    }

    private HashSet<Vector3I> BuildDesiredChunkSet(Vector2I centerChunk, Vector2 streamForward)
    {
        List<ColumnCandidate> candidates = new();
        int radius = GetEffectiveSearchRadius();

        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2I columnKey = new(centerChunk.X + x, centerChunk.Y + z);
                Vector2 offset = new(x, z);
                float distance = offset.Length();
                if (distance > radius + 0.35f)
                {
                    continue;
                }

                bool mandatory = distance <= GuaranteedColumnRadius;
                float priority = mandatory
                    ? 10000.0f - (distance * 10.0f)
                    : ComputeColumnPriority(columnKey, centerChunk, streamForward);
                candidates.Add(new ColumnCandidate(columnKey, priority, mandatory));
            }
        }

        candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        HashSet<Vector2I> selectedColumns = new();
        foreach (ColumnCandidate candidate in candidates)
        {
            if (candidate.Mandatory)
            {
                selectedColumns.Add(candidate.Key);
            }
        }

        int columnBudget = Mathf.Max(MaxActiveColumns, selectedColumns.Count);
        foreach (ColumnCandidate candidate in candidates)
        {
            if (selectedColumns.Count >= columnBudget)
            {
                break;
            }

            selectedColumns.Add(candidate.Key);
        }

        HashSet<Vector3I> desired = new();
        foreach (Vector2I column in selectedColumns)
        {
            for (int y = 0; y < VerticalChunkCount; y++)
            {
                Vector3I key = new(column.X, y, column.Y);
                desired.Add(key);
                EnsureChunkRequested(key, centerChunk, streamForward);
            }
        }

        return desired;
    }

    private int GetEffectiveSearchRadius()
    {
        int radius = Mathf.Max(SearchRadius, 1);
        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return radius;
        }

        int farRadius = Mathf.CeilToInt(camera.Far / _settings.ChunkSize);
        return Mathf.Max(radius, farRadius);
    }

    public void ApplyBrush(Vector3 worldCenter, bool additive)
    {
        float strength = additive ? BuildStrength : CarveStrength;

        foreach (TerrainChunk chunk in _chunks.Values)
        {
            if (!chunk.IntersectsSphere(worldCenter, BrushRadius))
            {
                continue;
            }

            if (chunk.ApplySphereBrush(worldCenter, BrushRadius, strength))
            {
                chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
                QueueChunkForRebuild(chunk);
            }
        }
    }

    public string GetDebugStats()
    {
        int activeCount = 0;
        foreach (TerrainChunk chunk in _chunks.Values)
        {
            if (chunk.Visible)
            {
                activeCount++;
            }
        }

        return
            $"Chunks: {activeCount} active / {_chunks.Count} loaded\n" +
            $"Gen jobs: {_activeGenerationJobs} running / {_queuedGenerationKeys.Count} queued | evicted: {_evictedChunks}\n" +
            $"Last gen: {_lastGenerationCompleteCount} ({_lastGenerationMs:0.00} ms) | attach {_lastChunkActivationCount} ({_lastChunkActivationMs:0.00} ms)\n" +
            $"Dirty render: {_dirtyRenderChunks.Count} | dirty collision: {_dirtyCollisionChunks.Count}\n" +
            $"Last rebuilds: render {_lastVisualRebuildCount} ({_lastVisualRebuildMs:0.00} ms) | " +
            $"collision {_lastCollisionRebuildCount} ({_lastCollisionRebuildMs:0.00} ms)\n" +
            $"Cache: {_cacheHits} hits / {_cacheMisses} misses";
    }

    public float GetInitialLoadProgress()
    {
        if (_desiredChunks.Count == 0)
        {
            return 0.0f;
        }

        int readyCount = 0;
        foreach (Vector3I key in _desiredChunks)
        {
            if (_chunks.TryGetValue(key, out TerrainChunk chunk) &&
                chunk.IsInitialLoadReady)
            {
                readyCount++;
            }
        }

        return (float)readyCount / _desiredChunks.Count;
    }

    private void QueueChunkForRebuild(TerrainChunk chunk)
    {
        if (chunk.RenderDirty)
        {
            _dirtyRenderChunks.Add(chunk);
        }

        if (chunk.CollisionDirty)
        {
            _dirtyCollisionChunks.Add(chunk);
        }
    }

    private void ProcessDirtyChunks()
    {
        _lastVisualRebuildCount = 0;
        _lastCollisionRebuildCount = 0;
        _lastVisualRebuildMs = 0.0;
        _lastCollisionRebuildMs = 0.0;

        int visualBudget = MaxVisualChunkRebuildsPerFrame;
        if (visualBudget > 0)
        {
            List<TerrainChunk> renderQueue = new(_dirtyRenderChunks);
            foreach (TerrainChunk chunk in renderQueue)
            {
                if (visualBudget <= 0)
                {
                    break;
                }

                if (!IsInstanceValid(chunk))
                {
                    _dirtyRenderChunks.Remove(chunk);
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (!chunk.RenderDirty)
                {
                    _dirtyRenderChunks.Remove(chunk);
                    continue;
                }

                chunk.RebuildRenderMesh();
                _lastVisualRebuildCount++;
                _lastVisualRebuildMs += chunk.LastRenderBuildMs;
                visualBudget--;
                if (!chunk.RenderDirty)
                {
                    _dirtyRenderChunks.Remove(chunk);
                }
            }
        }

        int collisionBudget = MaxCollisionChunkRebuildsPerFrame;
        if (collisionBudget > 0)
        {
            double nowSeconds = Time.GetTicksMsec() / 1000.0;
            List<TerrainChunk> collisionQueue = new(_dirtyCollisionChunks);
            foreach (TerrainChunk chunk in collisionQueue)
            {
                if (collisionBudget <= 0)
                {
                    break;
                }

                if (!IsInstanceValid(chunk))
                {
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (!chunk.CollisionDirty)
                {
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (chunk.TryRebuildCollision(nowSeconds))
                {
                    _lastCollisionRebuildCount++;
                    _lastCollisionRebuildMs += chunk.LastCollisionBuildMs;
                    collisionBudget--;
                    if (!chunk.CollisionDirty)
                    {
                        _dirtyCollisionChunks.Remove(chunk);
                    }
                }
            }
        }

        if (!_initialLoadComplete && _desiredChunks.Count > 0 && GetInitialLoadProgress() >= 0.999f)
        {
            _initialLoadComplete = true;
            EmitSignal(SignalName.InitialLoadCompleted);
        }
    }

    private void ResetFrameStats()
    {
        _lastGenerationCompleteCount = 0;
        _lastChunkActivationCount = 0;
        _lastGenerationMs = 0.0;
        _lastChunkActivationMs = 0.0;
    }

    private void ProcessQueuedChunkGenerations()
    {
        while (_activeGenerationJobs < MaxChunkGenerationJobs && _generationRequestQueue.Count > 0)
        {
            Vector3I key = DequeueHighestPriorityChunk();
            _queuedGenerationKeys.Remove(key);
            _generationPriority.Remove(key);

            if (_chunks.ContainsKey(key) || _runningGenerationKeys.Contains(key))
            {
                continue;
            }

            if (!_desiredChunks.Contains(key))
            {
                continue;
            }

            _runningGenerationKeys.Add(key);
            Interlocked.Increment(ref _activeGenerationJobs);

            _ = Task.Run(() =>
            {
                try
                {
                    GeneratedChunkResult result = GenerateChunkData(key);
                    _completedGenerationQueue.Enqueue(result);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeGenerationJobs);
                }
            });
        }
    }

    private Vector3I DequeueHighestPriorityChunk()
    {
        int bestIndex = 0;
        float bestPriority = float.NegativeInfinity;
        for (int i = 0; i < _generationRequestQueue.Count; i++)
        {
            Vector3I key = _generationRequestQueue[i];
            float priority = _generationPriority.GetValueOrDefault(key, 0.0f);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestIndex = i;
            }
        }

        Vector3I selected = _generationRequestQueue[bestIndex];
        _generationRequestQueue.RemoveAt(bestIndex);
        return selected;
    }

    private float ComputeChunkPriority(Vector3I key, Vector2I centerChunk, Vector2 streamForward)
    {
        Vector2 offset = new(key.X - centerChunk.X, key.Z - centerChunk.Y);
        float distance = offset.Length();
        if (distance <= 1.5f)
        {
            return 1000.0f - (distance * 10.0f) - (key.Y * 2.0f);
        }

        float forwardAlignment = 0.0f;
        if (streamForward != Vector2.Zero && offset.LengthSquared() > 0.0001f)
        {
            forwardAlignment = offset.Normalized().Dot(streamForward);
        }

        float horizonVisibility = EstimateHorizonVisibility(key);
        float priority = 100.0f - (distance * 9.0f);
        priority += (forwardAlignment + 1.0f) * (ForwardPriorityWeight * 0.7f);
        if (forwardAlignment < 0.0f)
        {
            priority += forwardAlignment * BehindViewerPenalty;
        }
        priority += horizonVisibility * 22.0f;
        priority -= key.Y * 2.5f;
        return priority;
    }

    private float ComputeColumnPriority(Vector2I columnKey, Vector2I centerChunk, Vector2 streamForward)
    {
        Vector2 offset = new(columnKey.X - centerChunk.X, columnKey.Y - centerChunk.Y);
        float distance = offset.Length();

        float forwardAlignment = 0.0f;
        if (streamForward != Vector2.Zero && offset.LengthSquared() > 0.0001f)
        {
            forwardAlignment = offset.Normalized().Dot(streamForward);
        }

        float horizonVisibility = EstimateHorizonVisibility(columnKey);
        bool resident = IsColumnResident(columnKey);
        float retention = _columnRetention.GetValueOrDefault(columnKey, 0.0f);

        float priority = 100.0f - (distance * 7.5f);
        priority += (forwardAlignment + 1.0f) * ForwardPriorityWeight;
        if (forwardAlignment < 0.0f)
        {
            priority += forwardAlignment * BehindViewerPenalty;
        }
        priority += horizonVisibility * 28.0f;
        if (resident)
        {
            priority += 10.0f;
        }
        priority += retention * 18.0f;

        return priority;
    }

    private float EstimateHorizonVisibility(Vector3I key)
    {
        return EstimateHorizonVisibility(new Vector2I(key.X, key.Z));
    }

    private float EstimateHorizonVisibility(Vector2I columnKey)
    {
        if (!UseHorizonLoadPriority || _prioritySampler == null)
        {
            return 1.0f;
        }

        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return 1.0f;
        }

        Vector3 cameraPosition = camera.GlobalPosition;
        float inset = _settings.ChunkSize * ChunkVisibilityInset;
        float minX = (columnKey.X * _settings.ChunkSize) + inset;
        float maxX = ((columnKey.X + 1) * _settings.ChunkSize) - inset;
        float minZ = (columnKey.Y * _settings.ChunkSize) + inset;
        float maxZ = ((columnKey.Y + 1) * _settings.ChunkSize) - inset;

        Vector2[] samplePoints =
        {
            new Vector2((columnKey.X + 0.5f) * _settings.ChunkSize, (columnKey.Y + 0.5f) * _settings.ChunkSize),
            new Vector2(minX, minZ),
            new Vector2(maxX, minZ),
            new Vector2(minX, maxZ),
            new Vector2(maxX, maxZ)
        };

        int visibleSamples = 0;
        float strongestOcclusion = 0.0f;
        foreach (Vector2 samplePoint in samplePoints)
        {
            float occlusionMargin = GetChunkSampleOcclusionMargin(cameraPosition, samplePoint);
            if (occlusionMargin <= 0.0f)
            {
                visibleSamples++;
                continue;
            }

            strongestOcclusion = Mathf.Max(strongestOcclusion, occlusionMargin);
        }

        float planarDistanceChunks = new Vector2(
            ((columnKey.X + 0.5f) * _settings.ChunkSize) - cameraPosition.X,
            ((columnKey.Y + 0.5f) * _settings.ChunkSize) - cameraPosition.Z).Length() / _settings.ChunkSize;

        if (visibleSamples == 0 &&
            strongestOcclusion >= LargeOccluderAngleMargin &&
            planarDistanceChunks >= MinOcclusionDistanceChunks)
        {
            return OccludedPriorityScale;
        }

        if (visibleSamples <= 2)
        {
            return 0.94f;
        }

        return 1.0f;
    }

    private float GetChunkSampleOcclusionMargin(Vector3 cameraPosition, Vector2 samplePoint)
    {
        Vector2 planarDelta = new(samplePoint.X - cameraPosition.X, samplePoint.Y - cameraPosition.Z);
        float planarDistance = planarDelta.Length();
        if (planarDistance <= _settings.ChunkSize * 1.5f)
        {
            return 0.0f;
        }

        float targetHeight = _prioritySampler.SampleSurfaceHeight(samplePoint.X, samplePoint.Y);
        float targetAngle = (targetHeight - cameraPosition.Y) / planarDistance;
        float maxHorizonAngle = float.NegativeInfinity;
        int samples = Mathf.Clamp(Mathf.RoundToInt(planarDistance / (_settings.ChunkSize * 0.8f)), 2, 10);

        for (int step = 1; step < samples; step++)
        {
            float t = (float)step / samples;
            Vector2 sampleXZ = new Vector2(cameraPosition.X, cameraPosition.Z).Lerp(samplePoint, t);
            float sampleDistance = planarDistance * t;
            if (sampleDistance <= 0.001f)
            {
                continue;
            }

            float sampleHeight = _prioritySampler.SampleSurfaceHeight(sampleXZ.X, sampleXZ.Y) + (_settings.ChunkSize * 0.08f);
            float sampleAngle = (sampleHeight - cameraPosition.Y) / sampleDistance;
            if (sampleAngle > maxHorizonAngle)
            {
                maxHorizonAngle = sampleAngle;
            }
        }

        return (maxHorizonAngle - 0.012f) - targetAngle;
    }

    private bool IsColumnResident(Vector2I columnKey)
    {
        for (int y = 0; y < VerticalChunkCount; y++)
        {
            if (_chunks.ContainsKey(new Vector3I(columnKey.X, y, columnKey.Y)))
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateColumnRetention(Vector2I centerChunk)
    {
        List<Vector2I> keys = new(_columnRetention.Keys);
        foreach (Vector2I key in keys)
        {
            float decayed = _columnRetention[key] * 0.86f;
            if (decayed < 0.05f)
            {
                _columnRetention.Remove(key);
                continue;
            }

            _columnRetention[key] = decayed;
        }

        foreach (Vector3I key in _desiredChunks)
        {
            Vector2I columnKey = new(key.X, key.Z);
            _columnRetention[columnKey] = 1.0f;
        }

        float guaranteedRadius = Mathf.Max(GuaranteedColumnRadius, 1.0f);
        int radius = Mathf.CeilToInt(guaranteedRadius);
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2 offset = new(x, z);
                if (offset.Length() > guaranteedRadius + 0.35f)
                {
                    continue;
                }

                Vector2I key = new(centerChunk.X + x, centerChunk.Y + z);
                _columnRetention[key] = 1.0f;
            }
        }
    }

    private void ProcessCompletedChunkGenerations()
    {
        int activationBudget = MaxChunkActivationsPerFrame <= 0 ? int.MaxValue : MaxChunkActivationsPerFrame;
        while (activationBudget > 0 && _completedGenerationQueue.TryDequeue(out GeneratedChunkResult result))
        {
            _runningGenerationKeys.Remove(result.Key);
            _lastGenerationCompleteCount++;
            _lastGenerationMs += result.GenerationMs;

            if (_chunks.ContainsKey(result.Key))
            {
                activationBudget--;
                continue;
            }

            ulong start = Time.GetTicksUsec();
            TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
            AddChild(chunk);
            chunk.Initialize(result.Key, _settings);
            chunk.SetData(result.Data, 0.0);
            _chunks[result.Key] = chunk;
            chunk.Visible = _desiredChunks.Contains(result.Key);
            chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            TouchChunk(result.Key);
            QueueChunkForRebuild(chunk);
            _lastChunkActivationCount++;
            _lastChunkActivationMs += (Time.GetTicksUsec() - start) / 1000.0;
            activationBudget--;
        }
    }

    private GeneratedChunkResult GenerateChunkData(Vector3I key)
    {
        ulong start = Time.GetTicksUsec();
        Vector3 origin = new(
            key.X * _settings.ChunkSize,
            _settings.BaseY + (key.Y * _settings.ChunkSize),
            key.Z * _settings.ChunkSize);

        VoxelChunkData data = new(PointsPerAxis, VoxelSize, origin);
        VoxelFieldGenerator generator = new(Seed, TerrainHeight, DetailHeight, CaveScale, CaveThreshold);
        generator.FillChunk(data);
        double generationMs = (Time.GetTicksUsec() - start) / 1000.0;
        return new GeneratedChunkResult(key, data, generationMs);
    }

    private void TouchChunk(Vector3I key)
    {
        _chunkTouchTicks[key] = Time.GetTicksUsec();
    }

    private void EvictInactiveChunks()
    {
        if (MaxLoadedChunks <= 0)
        {
            return;
        }

        while (_chunks.Count > MaxLoadedChunks)
        {
            Vector3I? oldestKey = null;
            ulong oldestTick = ulong.MaxValue;

            foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _chunks)
            {
                TerrainChunk candidateChunk = entry.Value;
                if (candidateChunk.Visible || candidateChunk.RenderDirty || candidateChunk.CollisionDirty)
                {
                    continue;
                }

                ulong touchTick = _chunkTouchTicks.GetValueOrDefault(entry.Key, 0UL);
                if (touchTick < oldestTick)
                {
                    oldestTick = touchTick;
                    oldestKey = entry.Key;
                }
            }

            if (oldestKey == null)
            {
                break;
            }

            Vector3I key = oldestKey.Value;
            TerrainChunk chunk = _chunks[key];
            _dirtyRenderChunks.Remove(chunk);
            _dirtyCollisionChunks.Remove(chunk);
            _chunkTouchTicks.Remove(key);
            _chunks.Remove(key);
            chunk.QueueFree();
            _evictedChunks++;
        }
    }

    private sealed record GeneratedChunkResult(Vector3I Key, VoxelChunkData Data, double GenerationMs);
    private sealed record ColumnCandidate(Vector2I Key, float Priority, bool Mandatory);
}
