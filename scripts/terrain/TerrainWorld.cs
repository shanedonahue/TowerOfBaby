using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

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
    [Export] public int WarmSearchRadiusPadding = 1;
    [Export] public int MaxWarmColumns = 64;
    [Export] public float GuaranteedColumnRadius = 1.6f;
    [Export] public float ChunkVisibilityInset = 0.18f;
    [Export] public float LargeOccluderAngleMargin = 0.055f;
    [Export] public float MinOcclusionDistanceChunks = 5.5f;
    [Export] public float ForwardPriorityWeight = 26.0f;
    [Export] public float BehindViewerPenalty = 36.0f;
    [ExportGroup("Brush")]
    [Export] public float BrushRadius = 2.4f;
    [Export] public float BrushRadiusMin = 0.8f;
    [Export] public float BrushRadiusMax = 8.0f;
    [Export] public float BrushSurfaceInset = 0.55f;
    [Export] public float BrushBuildSurfaceOffset = 0.3f;
    [Export] public float CarveStrength = -3.4f;
    [Export] public float BuildStrength = 2.8f;
    [Export] public float BrushRetextureMargin = 1.6f;
    [ExportGroup("Persistence")]
    [Export] public bool EnableStartupStatePersistence = true;
    [Export] public int VerticalChunkCount = 3;
    [Export] public int MaxLoadedChunks = 120;
    [Export] public int MaxChunkGenerationJobs = 2;
    [Export] public int MaxChunkActivationsPerFrame = 2;
    [Export] public int MaxVisualChunkRebuildsPerFrame = 2;
    [Export] public int MaxCollisionChunkRebuildsPerFrame = 1;
    [ExportGroup("Startup Boost")]
    [Export] public int StartupChunkGenerationJobs = 8;
    [Export] public int StartupChunkActivationsPerFrame = 8;
    [Export] public int StartupVisualChunkRebuildsPerFrame = 8;
    [Export] public int StartupCollisionChunkRebuildsPerFrame = 4;
    [Export] public float StartupWarmPriorityBias = 220.0f;
    [Export] public float CollisionRebuildDelaySeconds = 0.08f;

    private readonly Dictionary<Vector3I, TerrainChunk> _chunks = new();
    private readonly HashSet<TerrainChunk> _dirtyRenderChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyCollisionChunks = new();
    private readonly Dictionary<Vector3I, ulong> _chunkTouchTicks = new();
    private readonly Dictionary<Vector2I, float> _columnRetention = new();
    private readonly List<Vector3I> _pendingActivationQueue = new();
    private readonly List<Vector3I> _pendingLoadQueue = new();
    private readonly Dictionary<Vector3I, float> _pendingLoadPriority = new();
    private readonly HashSet<Vector3I> _queuedActivationKeys = new();
    private readonly object _loadStateLock = new();
    private readonly HashSet<Vector3I> _queuedLoadKeys = new();
    private readonly HashSet<Vector3I> _runningLoadKeys = new();
    private readonly ConcurrentQueue<PreparedChunkResult> _completedLoadQueue = new();

    private VoxelFieldGenerator _prioritySampler = null!;
    private TerrainWorldSettings _settings = null!;
    private TerrainChunkStore _chunkStore = null!;
    private Node3D _trackedCharacter = null!;
    private Vector2I _lastCenterChunk = new(int.MinValue, int.MinValue);
    private Vector2 _lastStreamForward = Vector2.Zero;
    private HashSet<Vector3I> _desiredChunks = new();
    private HashSet<Vector3I> _warmChunks = new();
    private HashSet<Vector3I> _startupLoadedChunks = new();
    private Vector3 _lastTrackedCharacterPosition = new(float.MinValue, float.MinValue, float.MinValue);
    private Vector3 _lastCameraForward = new(float.MinValue, float.MinValue, float.MinValue);
    private int _activeLoadJobs;
    private int _lastVisualRebuildCount;
    private int _lastCollisionRebuildCount;
    private int _lastChunkLoadCount;
    private int _lastChunkActivationCount;
    private int _lastStartupChunkLoadCount;
    private int _lastPersistedChunkLoadCount;
    private int _lastGeneratedChunkLoadCount;
    private double _lastVisualRebuildMs;
    private double _lastCollisionRebuildMs;
    private double _lastChunkLoadMs;
    private double _lastChunkActivationMs;
    private double _lastStartupChunkLoadMs;
    private double _lastPersistedChunkLoadMs;
    private double _lastGeneratedChunkLoadMs;
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictedChunks;
    private bool _initialLoadComplete;
    private bool _searchDirty = true;
    private bool _useStartupSnapshot;

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
        _chunkStore = new TerrainChunkStore(Seed);

        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;
        if (EnableStartupStatePersistence)
        {
            LoadStartupState();
        }
        TreeExiting += HandleTreeExiting;

        RefreshChunks(force: true);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion or InputEventKey or InputEventJoypadMotion or InputEventJoypadButton)
        {
            _searchDirty = true;
        }
    }

    public override void _Process(double delta)
    {
        ResetFrameStats();
        if (ShouldRefreshChunkSearch())
        {
            RefreshChunks(force: false);
        }

        ProcessCompletedChunkLoads();
        ProcessQueuedChunkLoads();
        ProcessPendingChunkActivations();
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
            _searchDirty = false;
            return;
        }

        HashSet<Vector3I> previousDesired = _desiredChunks;
        HashSet<Vector3I> previousWarm = _warmChunks;
        _lastCenterChunk = centerChunk;
        _lastStreamForward = streamForward;
        UpdateColumnRetention(centerChunk);
        int activeRadius = GetEffectiveSearchRadius();
        HashSet<Vector3I> desired = BuildChunkSet(centerChunk, streamForward, activeRadius, MaxActiveColumns);
        HashSet<Vector3I> warm = BuildChunkSet(
            centerChunk,
            streamForward,
            activeRadius + Mathf.Max(0, WarmSearchRadiusPadding),
            Mathf.Max(MaxWarmColumns, MaxActiveColumns));

        if (_useStartupSnapshot)
        {
            warm.UnionWith(_startupLoadedChunks);
        }

        _desiredChunks = desired;
        _warmChunks = warm;
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

        QueueChunkLoads(desired, previousDesired, centerChunk, streamForward, 0.0f, activateOnReady: true);

        int loadJobBudget = GetCurrentLoadJobBudget();
        bool canQueueWarmLoads =
            _pendingLoadQueue.Count < loadJobBudget &&
            _activeLoadJobs < loadJobBudget &&
            _pendingActivationQueue.Count == 0;

        if (canQueueWarmLoads)
        {
            float warmPriorityBias = _useStartupSnapshot ? StartupWarmPriorityBias : -240.0f;
            QueueChunkLoads(warm, previousWarm, centerChunk, streamForward, warmPriorityBias, activateOnReady: false);
        }
        _searchDirty = false;
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

    private HashSet<Vector3I> BuildChunkSet(Vector2I centerChunk, Vector2 streamForward, int radius, int maxColumns)
    {
        List<ColumnCandidate> candidates = new();

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

        int columnBudget = Mathf.Max(maxColumns, selectedColumns.Count);
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
        VoxelSphereEdit edit = new(
            worldCenter,
            BrushRadius,
            strength,
            BrushRetextureMargin);

        foreach (Vector3I key in GetChunkKeysIntersectingSphere(worldCenter, BrushRadius))
        {
            TerrainChunk chunk = GetOrCreateChunkForEdit(key);
            if (!chunk.IntersectsSphere(worldCenter, BrushRadius))
            {
                continue;
            }

            if (chunk.ApplySphereBrush(edit, ResolveEditedMaterial))
            {
                chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
                QueueChunkForRebuild(chunk);
            }
        }
    }

    public void AdjustBrushRadius(float delta)
    {
        BrushRadius = Mathf.Clamp(BrushRadius + delta, BrushRadiusMin, BrushRadiusMax);
    }

    public Vector3 ResolveBrushCenter(Vector3 hitPoint, Vector3 hitNormal, bool additive)
    {
        Vector3 normal = hitNormal.LengthSquared() > 0.0001f
            ? hitNormal.Normalized()
            : Vector3.Up;
        float offset = additive ? BrushBuildSurfaceOffset : -BrushSurfaceInset;
        return hitPoint + (normal * offset);
    }

    public void ClearStartupCache()
    {
        _chunkStore?.ClearStartupState();
    }

    public void ClearAllPersistentCache()
    {
        _chunkStore?.ClearAllChunkData();
    }

    public bool IsColumnActiveAtPosition(Vector3 worldPosition)
    {
        Vector2I columnKey = new(
            Mathf.FloorToInt(worldPosition.X / _settings.ChunkSize),
            Mathf.FloorToInt(worldPosition.Z / _settings.ChunkSize));

        for (int y = 0; y < VerticalChunkCount; y++)
        {
            if (_chunks.TryGetValue(new Vector3I(columnKey.X, y, columnKey.Y), out TerrainChunk chunk) &&
                chunk.Visible)
            {
                return true;
            }
        }

        return false;
    }

    public string GetDebugStats()
    {
        TerrainWorldProfileSnapshot snapshot = GetProfileSnapshot();

        return
            $"Chunks: {snapshot.ActiveChunkCount} active / {snapshot.LoadedChunkCount} loaded / {snapshot.DesiredChunkCount} desired\n" +
            $"Loads: {snapshot.RunningLoadCount} running / {snapshot.PendingLoadCount} queued / {snapshot.PendingActivationCount} waiting activate\n" +
            $"Last load: {snapshot.LastChunkLoadCount} ({snapshot.LastChunkLoadMs:0.00} ms) | startup {snapshot.LastStartupChunkLoadCount} ({snapshot.LastStartupChunkLoadMs:0.00} ms) | saved {snapshot.LastPersistedChunkLoadCount} ({snapshot.LastPersistedChunkLoadMs:0.00} ms) | generated {snapshot.LastGeneratedChunkLoadCount} ({snapshot.LastGeneratedChunkLoadMs:0.00} ms)\n" +
            $"Attach: {snapshot.LastChunkActivationCount} ({snapshot.LastChunkActivationMs:0.00} ms)\n" +
            $"Dirty: render {snapshot.DirtyRenderCount} | collision {snapshot.DirtyCollisionCount}\n" +
            $"Rebuilds: render {snapshot.LastVisualRebuildCount} ({snapshot.LastVisualRebuildMs:0.00} ms) | collision {snapshot.LastCollisionRebuildCount} ({snapshot.LastCollisionRebuildMs:0.00} ms)\n" +
            $"Cache: {_cacheHits} hits / {_cacheMisses} misses | evicted {snapshot.EvictedChunks}";
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        int activeCount = 0;
        foreach (TerrainChunk chunk in _chunks.Values)
        {
            if (chunk.Visible)
            {
                activeCount++;
            }
        }

        return new TerrainWorldProfileSnapshot
        {
            ActiveChunkCount = activeCount,
            LoadedChunkCount = _chunks.Count,
            DesiredChunkCount = _desiredChunks.Count,
            PendingLoadCount = _pendingLoadQueue.Count,
            RunningLoadCount = _activeLoadJobs,
            PendingActivationCount = _pendingActivationQueue.Count,
            DirtyRenderCount = _dirtyRenderChunks.Count,
            DirtyCollisionCount = _dirtyCollisionChunks.Count,
            LastChunkLoadCount = _lastChunkLoadCount,
            LastChunkActivationCount = _lastChunkActivationCount,
            LastVisualRebuildCount = _lastVisualRebuildCount,
            LastCollisionRebuildCount = _lastCollisionRebuildCount,
            LastChunkLoadMs = _lastChunkLoadMs,
            LastChunkActivationMs = _lastChunkActivationMs,
            LastVisualRebuildMs = _lastVisualRebuildMs,
            LastCollisionRebuildMs = _lastCollisionRebuildMs,
            LastStartupChunkLoadCount = _lastStartupChunkLoadCount,
            LastPersistedChunkLoadCount = _lastPersistedChunkLoadCount,
            LastGeneratedChunkLoadCount = _lastGeneratedChunkLoadCount,
            LastStartupChunkLoadMs = _lastStartupChunkLoadMs,
            LastPersistedChunkLoadMs = _lastPersistedChunkLoadMs,
            LastGeneratedChunkLoadMs = _lastGeneratedChunkLoadMs,
            CacheHits = _cacheHits,
            CacheMisses = _cacheMisses,
            EvictedChunks = _evictedChunks,
            InitialLoadProgress = GetInitialLoadProgress(),
            InitialLoadComplete = _initialLoadComplete
        };
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

    private TerrainChunk GetOrCreateChunkForEdit(Vector3I key)
    {
        if (_chunks.TryGetValue(key, out TerrainChunk existingChunk))
        {
            return existingChunk;
        }

        LoadedChunkData loadedChunk = LoadChunkData(key);
        TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
        AddChild(chunk);
        chunk.Initialize(key, _settings);
        chunk.SetData(loadedChunk.Data, 0.0);
        chunk.Visible = _desiredChunks.Contains(key);
        chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        _chunks[key] = chunk;
        TouchChunk(key);
        QueueChunkForRebuild(chunk);
        return chunk;
    }

    private IEnumerable<Vector3I> GetChunkKeysIntersectingSphere(Vector3 worldCenter, float radius)
    {
        int minX = Mathf.FloorToInt((worldCenter.X - radius) / _settings.ChunkSize);
        int maxX = Mathf.FloorToInt((worldCenter.X + radius) / _settings.ChunkSize);
        int minY = Mathf.FloorToInt((worldCenter.Y - _settings.BaseY - radius) / _settings.ChunkSize);
        int maxY = Mathf.FloorToInt((worldCenter.Y - _settings.BaseY + radius) / _settings.ChunkSize);
        int minZ = Mathf.FloorToInt((worldCenter.Z - radius) / _settings.ChunkSize);
        int maxZ = Mathf.FloorToInt((worldCenter.Z + radius) / _settings.ChunkSize);

        minY = Mathf.Clamp(minY, 0, VerticalChunkCount - 1);
        maxY = Mathf.Clamp(maxY, 0, VerticalChunkCount - 1);

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    yield return new Vector3I(x, y, z);
                }
            }
        }
    }

    private VoxelMaterialId ResolveEditedMaterial(Vector3 worldPosition, float density)
    {
        return _prioritySampler.SampleMaterial(worldPosition, density);
    }

    private void ProcessDirtyChunks()
    {
        _lastVisualRebuildCount = 0;
        _lastCollisionRebuildCount = 0;
        _lastVisualRebuildMs = 0.0;
        _lastCollisionRebuildMs = 0.0;

        int visualBudget = GetCurrentVisualRebuildBudget();
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

        int collisionBudget = GetCurrentCollisionRebuildBudget();
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
            _useStartupSnapshot = false;
            _startupLoadedChunks.Clear();
            EmitSignal(SignalName.InitialLoadCompleted);
        }
    }

    private void ResetFrameStats()
    {
        _lastChunkLoadCount = 0;
        _lastChunkActivationCount = 0;
        _lastStartupChunkLoadCount = 0;
        _lastPersistedChunkLoadCount = 0;
        _lastGeneratedChunkLoadCount = 0;
        _lastChunkLoadMs = 0.0;
        _lastChunkActivationMs = 0.0;
        _lastStartupChunkLoadMs = 0.0;
        _lastPersistedChunkLoadMs = 0.0;
        _lastGeneratedChunkLoadMs = 0.0;
    }

    private void ProcessPendingChunkActivations()
    {
        int configuredBudget = GetCurrentActivationBudget();
        int activationBudget = configuredBudget <= 0 ? int.MaxValue : configuredBudget;
        while (activationBudget > 0 && _pendingActivationQueue.Count > 0)
        {
            Vector3I key = _pendingActivationQueue[0];
            _pendingActivationQueue.RemoveAt(0);
            _queuedActivationKeys.Remove(key);

            if (!_desiredChunks.Contains(key))
            {
                continue;
            }

            if (!_chunks.TryGetValue(key, out TerrainChunk chunk))
            {
                continue;
            }

            if (chunk.Visible)
            {
                activationBudget--;
                continue;
            }

            ulong activateStart = Time.GetTicksUsec();
            chunk.Visible = true;
            chunk.ProcessMode = ProcessModeEnum.Inherit;
            TouchChunk(key);
            _lastChunkActivationCount++;
            _lastChunkActivationMs += (Time.GetTicksUsec() - activateStart) / 1000.0;
            activationBudget--;
        }
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

    private void ProcessQueuedChunkLoads()
    {
        int loadJobBudget = GetCurrentLoadJobBudget();
        while (_activeLoadJobs < loadJobBudget && _pendingLoadQueue.Count > 0)
        {
            Vector3I key = DequeueHighestPriorityLoad();
            lock (_loadStateLock)
            {
                _queuedLoadKeys.Remove(key);
            }
            _pendingLoadPriority.Remove(key);

            bool alreadyRunning;
            lock (_loadStateLock)
            {
                alreadyRunning = _runningLoadKeys.Contains(key);
            }

            if (_chunks.ContainsKey(key) || alreadyRunning || (!_desiredChunks.Contains(key) && !_warmChunks.Contains(key)))
            {
                continue;
            }

            lock (_loadStateLock)
            {
                _runningLoadKeys.Add(key);
            }
            Interlocked.Increment(ref _activeLoadJobs);

            _ = Task.Run(() =>
            {
                try
                {
                    ulong start = Time.GetTicksUsec();
                    LoadedChunkData loadedChunk = LoadChunkData(key);
                    double loadMs = (Time.GetTicksUsec() - start) / 1000.0;
                    _completedLoadQueue.Enqueue(new PreparedChunkResult(key, loadedChunk.Data, loadedChunk.Source, loadMs));
                }
                finally
                {
                    lock (_loadStateLock)
                    {
                        _runningLoadKeys.Remove(key);
                    }
                    Interlocked.Decrement(ref _activeLoadJobs);
                }
            });
        }
    }

    private void ProcessCompletedChunkLoads()
    {
        while (_completedLoadQueue.TryDequeue(out PreparedChunkResult result))
        {
            if (_chunks.ContainsKey(result.Key))
            {
                continue;
            }

            EvictInactiveChunks(requiredFreeSlots: 1);

            ulong attachStart = Time.GetTicksUsec();
            TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
            AddChild(chunk);
            chunk.Initialize(result.Key, _settings);
            chunk.SetData(result.Data, 0.0);
            chunk.Visible = false;
            chunk.ProcessMode = ProcessModeEnum.Disabled;
            _chunks[result.Key] = chunk;
            TouchChunk(result.Key);
            QueueChunkForRebuild(chunk);

            _lastChunkLoadCount++;
            _lastChunkLoadMs += result.LoadMs;
            switch (result.Source)
            {
                case TerrainChunkLoadSource.StartupSnapshot:
                    _lastStartupChunkLoadCount++;
                    _lastStartupChunkLoadMs += result.LoadMs;
                    break;
                case TerrainChunkLoadSource.PersistedChunk:
                    _lastPersistedChunkLoadCount++;
                    _lastPersistedChunkLoadMs += result.LoadMs;
                    break;
                default:
                    _lastGeneratedChunkLoadCount++;
                    _lastGeneratedChunkLoadMs += result.LoadMs;
                    break;
            }

            if (_desiredChunks.Contains(result.Key) && !_queuedActivationKeys.Contains(result.Key))
            {
                _pendingActivationQueue.Add(result.Key);
                _queuedActivationKeys.Add(result.Key);
            }

            _lastChunkActivationMs += (Time.GetTicksUsec() - attachStart) / 1000.0;
        }
    }

    private void QueueChunkLoads(HashSet<Vector3I> targetSet, HashSet<Vector3I> previousSet, Vector2I centerChunk, Vector2 streamForward, float priorityBias, bool activateOnReady)
    {
        List<Vector3I> enteringKeys = new();
        foreach (Vector3I key in targetSet)
        {
            if (!previousSet.Contains(key))
            {
                enteringKeys.Add(key);
            }
        }

        enteringKeys.Sort((a, b) =>
            (ComputeChunkPriority(b, centerChunk, streamForward) + priorityBias)
            .CompareTo(ComputeChunkPriority(a, centerChunk, streamForward) + priorityBias));
        foreach (Vector3I key in enteringKeys)
        {
            if (_queuedActivationKeys.Contains(key))
            {
                continue;
            }

            if (_chunks.TryGetValue(key, out TerrainChunk residentChunk))
            {
                _cacheHits++;
                if (activateOnReady && !residentChunk.Visible)
                {
                    _pendingActivationQueue.Insert(0, key);
                    _queuedActivationKeys.Add(key);
                }
                continue;
            }

            _cacheMisses++;
            float priority = ComputeChunkPriority(key, centerChunk, streamForward) + priorityBias;
            if (_useStartupSnapshot && _startupLoadedChunks.Contains(key))
            {
                priority += 5000.0f;
            }
            bool loadAlreadyTracked;
            lock (_loadStateLock)
            {
                loadAlreadyTracked = _queuedLoadKeys.Contains(key) || _runningLoadKeys.Contains(key);
            }

            if (loadAlreadyTracked)
            {
                _pendingLoadPriority[key] = priority;
                continue;
            }

            _pendingLoadQueue.Add(key);
            _pendingLoadPriority[key] = priority;
            lock (_loadStateLock)
            {
                _queuedLoadKeys.Add(key);
            }
        }
    }

    private LoadedChunkData LoadChunkData(Vector3I key)
    {
        if (EnableStartupStatePersistence &&
            _chunkStore.TryLoadStartupChunk(key, out VoxelChunkData startupData))
        {
            return new LoadedChunkData(startupData, TerrainChunkLoadSource.StartupSnapshot);
        }

        if (_chunkStore.TryLoad(key, out VoxelChunkData persistedData))
        {
            return new LoadedChunkData(persistedData, TerrainChunkLoadSource.PersistedChunk);
        }

        Vector3 origin = new(
            key.X * _settings.ChunkSize,
            _settings.BaseY + (key.Y * _settings.ChunkSize),
            key.Z * _settings.ChunkSize);

        VoxelChunkData generated = new(PointsPerAxis, VoxelSize, origin);
        VoxelFieldGenerator generator = new(Seed, TerrainHeight, DetailHeight, CaveScale, CaveThreshold);
        generator.FillChunk(generated);
        return new LoadedChunkData(generated, TerrainChunkLoadSource.ProceduralGeneration);
    }

    private Vector3I DequeueHighestPriorityLoad()
    {
        int bestIndex = 0;
        float bestPriority = float.NegativeInfinity;
        for (int i = 0; i < _pendingLoadQueue.Count; i++)
        {
            Vector3I key = _pendingLoadQueue[i];
            float priority = _pendingLoadPriority.GetValueOrDefault(key, 0.0f);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestIndex = i;
            }
        }

        Vector3I selected = _pendingLoadQueue[bestIndex];
        _pendingLoadQueue.RemoveAt(bestIndex);
        return selected;
    }

    private void TouchChunk(Vector3I key)
    {
        _chunkTouchTicks[key] = Time.GetTicksUsec();
    }

    private void EvictInactiveChunks()
    {
        EvictInactiveChunks(requiredFreeSlots: 0);
    }

    private void EvictInactiveChunks(int requiredFreeSlots)
    {
        if (MaxLoadedChunks <= 0)
        {
            return;
        }

        while ((_chunks.Count + requiredFreeSlots) > MaxLoadedChunks)
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
            if (chunk.PersistenceDirty)
            {
                _chunkStore.Save(key, chunk.Data);
                chunk.MarkPersisted();
            }
            _dirtyRenderChunks.Remove(chunk);
            _dirtyCollisionChunks.Remove(chunk);
            _chunkTouchTicks.Remove(key);
            _chunks.Remove(key);
            chunk.QueueFree();
            _evictedChunks++;
        }
    }

    private void LoadStartupState()
    {
        if (_trackedCharacter == null || !_chunkStore.TryLoadStartupState(out TerrainStartupState startupState))
        {
            return;
        }

        Transform3D transform = _trackedCharacter.GlobalTransform;
        transform.Origin = startupState.PlayerPosition;
        _trackedCharacter.GlobalTransform = transform;
        _lastTrackedCharacterPosition = startupState.PlayerPosition;

        foreach (TerrainStartupChunkDescriptor chunk in startupState.Chunks)
        {
            _startupLoadedChunks.Add(chunk.Key);
        }

        _useStartupSnapshot = _startupLoadedChunks.Count > 0;
    }

    private bool IsStartupBoostActive()
    {
        return _useStartupSnapshot && !_initialLoadComplete;
    }

    private int GetCurrentLoadJobBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxChunkGenerationJobs, StartupChunkGenerationJobs)
            : MaxChunkGenerationJobs;
    }

    private int GetCurrentActivationBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxChunkActivationsPerFrame, StartupChunkActivationsPerFrame)
            : MaxChunkActivationsPerFrame;
    }

    private int GetCurrentVisualRebuildBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxVisualChunkRebuildsPerFrame, StartupVisualChunkRebuildsPerFrame)
            : MaxVisualChunkRebuildsPerFrame;
    }

    private int GetCurrentCollisionRebuildBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxCollisionChunkRebuildsPerFrame, StartupCollisionChunkRebuildsPerFrame)
            : MaxCollisionChunkRebuildsPerFrame;
    }

    private void HandleTreeExiting()
    {
        if (!EnableStartupStatePersistence || _trackedCharacter == null)
        {
            return;
        }

        List<TerrainStartupChunkSnapshot> startupChunks = new(_chunks.Count);
        foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _chunks)
        {
            TerrainChunk chunk = entry.Value;
            if (!chunk.HasData)
            {
                continue;
            }

            startupChunks.Add(new TerrainStartupChunkSnapshot(entry.Key, chunk.Visible, chunk.Data));
        }

        _chunkStore.SaveStartupState(_trackedCharacter.GlobalPosition, startupChunks);
    }

    private sealed record ColumnCandidate(Vector2I Key, float Priority, bool Mandatory);
    private sealed record LoadedChunkData(VoxelChunkData Data, TerrainChunkLoadSource Source);
    private sealed record PreparedChunkResult(Vector3I Key, VoxelChunkData Data, TerrainChunkLoadSource Source, double LoadMs);

    private bool ShouldRefreshChunkSearch()
    {
        Camera3D camera = GetViewport().GetCamera3D();
        Vector3 trackedPosition = _trackedCharacter?.GlobalPosition ?? Vector3.Zero;
        Vector3 cameraForward = camera == null ? Vector3.Zero : (-camera.GlobalTransform.Basis.Z);

        bool firstSample = _lastTrackedCharacterPosition.X == float.MinValue;
        bool movedEnough = firstSample ||
            trackedPosition.DistanceSquaredTo(_lastTrackedCharacterPosition) >= (_settings.ChunkSize * _settings.ChunkSize * 0.09f) ||
            cameraForward.Dot(_lastCameraForward) < 0.992f;

        if (!movedEnough && !_searchDirty)
        {
            return false;
        }

        _lastTrackedCharacterPosition = trackedPosition;
        _lastCameraForward = cameraForward;
        return true;
    }
}
