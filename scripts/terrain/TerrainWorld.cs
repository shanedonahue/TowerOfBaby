using Godot;
using System.Collections.Generic;
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

    [ExportGroup("Search")]
    [Export] public int SearchRadius = 8;
    [Export] public int MaxActiveColumns = 72;
    [Export] public float GuaranteedColumnRadius = 1.6f;
    [Export] public int MaxDesiredSearchStepsPerFrame = 32;
    [Export] public int StartupDesiredSearchStepsPerFrame = 96;
    [Export] public float SearchInvalidationYawDegrees = 12.0f;
    [Export] public float SearchInvalidationPitchDegrees = 8.0f;
    [Export] public float SearchInvalidationMovementDirectionDegrees = 26.0f;
    [Export] public float SearchInvalidationMoveDistanceFactor = 0.35f;
    [Export] public float ResidentPriorityBonus = 10.0f;
    [Export] public float RetentionPriorityWeight = 18.0f;
    [Export] public float RetentionDecayFactor = 0.86f;
    [Export] public float AdjacencyPriorityWeight = 4.5f;
    [Export] public float RamCacheLoadPriorityBonus = 14.0f;
    [Export] public float StartupSnapshotLoadPriorityBonus = 10.0f;
    [Export] public float PersistedChunkLoadPriorityBonus = 6.0f;
    [Export] public float GeneratedChunkLoadPriorityBonus = 0.0f;

    [ExportGroup("Visibility")]
    [Export] public float ChunkVisibilityInset = 0.18f;
    [Export] public float LargeOccluderAngleMargin = 0.055f;
    [Export] public float MinOcclusionDistanceChunks = 5.5f;
    [Export] public float ForwardPriorityWeight = 26.0f;
    [Export] public float BehindViewerPenalty = 36.0f;
    [Export] public float ShoulderHalfAngleDegrees = 45.0f;
    [Export] public float ShoulderDistanceMultiplier = 0.7f;
    [Export] public float ShoulderPriorityMultiplier = 0.45f;

    [ExportGroup("Legacy Warm Search")]
    [Export] public int WarmSearchRadiusPadding = 1;
    [Export] public int MaxWarmColumns = 64;
    [Export] public float StartupWarmPriorityBias = 220.0f;

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
    // Keep conservative headroom above the resident target so fast sweeps revisit RAM before falling back to generation.
    [Export] public int MaxLoadedChunks = 120;
    [Export] public int MaxChunkGenerationJobs = 2;
    [Export] public int MaxChunkActivationsPerFrame = 2;
    [Export] public int MaxChunkReleasesPerFrame = 4;
    [Export] public int MaxVisualChunkRebuildsPerFrame = 2;
    [Export] public int MaxCollisionChunkRebuildsPerFrame = 1;

    [ExportGroup("Startup Boost")]
    [Export] public int StartupChunkGenerationJobs = 8;
    [Export] public int StartupChunkActivationsPerFrame = 8;
    [Export] public int StartupChunkReleasesPerFrame = 8;
    [Export] public int StartupVisualChunkRebuildsPerFrame = 8;
    [Export] public int StartupCollisionChunkRebuildsPerFrame = 4;
    [Export] public float CollisionRebuildDelaySeconds = 0.08f;

    private readonly Dictionary<Vector3I, TerrainChunk> _residentChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyRenderChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyCollisionChunks = new();
    private readonly Dictionary<Vector2I, float> _columnRetention = new();
    private readonly HashSet<Vector3I> _desiredChunks = new();
    private readonly HashSet<Vector3I> _inFlightKeys = new();
    private readonly TerrainDesiredSetBuilder _desiredSetBuilder = new();
    private readonly TerrainResidencyManager _residencyManager = new();
    private readonly TerrainLoadScheduler _loadScheduler = new();
    private readonly Dictionary<Vector2I, float> _visibilityHeuristicCache = new();

    private VoxelFieldGenerator _prioritySampler = null!;
    private TerrainWorldSettings _settings = null!;
    private TerrainChunkStore _chunkStore = null!;
    private TerrainCacheManager _cacheManager = null!;
    private Node3D _trackedCharacter = null!;
    private readonly HashSet<Vector3I> _startupLoadedChunks = new();

    private Vector2I _lastInvalidationCenterChunk = new(int.MinValue, int.MinValue);
    private Vector3 _lastInvalidationPosition = new(float.MinValue, float.MinValue, float.MinValue);
    private float _lastInvalidationYawDegrees = float.NaN;
    private float _lastInvalidationPitchDegrees = float.NaN;
    private Vector2 _lastInvalidationMovementDirection = Vector2.Zero;
    private bool _terrainDesirabilityDirty;

    private int _lastVisualRebuildCount;
    private int _lastCollisionRebuildCount;
    private int _lastChunkLoadCount;
    private int _lastChunkActivationCount;
    private int _lastChunkReleaseCount;
    private int _lastStartupChunkLoadCount;
    private int _lastPersistedChunkLoadCount;
    private int _lastRamCacheLoadCount;
    private int _lastGeneratedChunkLoadCount;
    private double _lastVisualRebuildMs;
    private double _lastCollisionRebuildMs;
    private double _lastChunkLoadMs;
    private double _lastChunkActivationMs;
    private double _lastChunkReleaseMs;
    private double _lastStartupChunkLoadMs;
    private double _lastPersistedChunkLoadMs;
    private double _lastRamCacheLoadMs;
    private double _lastGeneratedChunkLoadMs;
    private double _lastPriorityEvaluationMs;
    private double _lastVisibilityHeuristicMs;
    private long _residentReuseHits;
    private bool _initialLoadComplete;
    private bool _useStartupSnapshot;
    private string _lastSelectedChunkSummary = "selected: n/a";
    private string _lastReleasedChunkSummary = "released: n/a";
    private string _lastChunkSourceSummary = "source: n/a";
    private SearchEvaluationContext _searchEvaluationContext;

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
        _cacheManager = new TerrainCacheManager(_chunkStore);
        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;

        if (EnableStartupStatePersistence)
        {
            LoadStartupState();
        }

        LogStreamingTuningSummary();
        TreeExiting += HandleTreeExiting;
        RefreshStreamingState(forceInvalidate: true);
    }

    public override void _Process(double delta)
    {
        ResetFrameStats();
        ProcessCompletedChunkLoads();
        RefreshStreamingState(forceInvalidate: false);
        ProcessPreparedChunkReleases();
        ProcessQueuedChunkLoads();
        ProcessPendingChunkActivations();
        ProcessDirtyChunks();
        _cacheManager.MaintainCapacity(MaxLoadedChunks, _residentChunks.Count, _loadScheduler.PreparedCount);
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

            if (!chunk.ApplySphereBrush(edit, ResolveEditedMaterial))
            {
                continue;
            }

            chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
            QueueChunkForRebuild(chunk);
            _terrainDesirabilityDirty = true;
        }
    }

    public void ApplySlash(VoxelSlashEdit edit)
    {
        float boundsRadius = edit.BoundingRadius;
        foreach (Vector3I key in GetChunkKeysIntersectingSphere(edit.Center, boundsRadius))
        {
            TerrainChunk chunk = GetOrCreateChunkForEdit(key);
            if (!chunk.IntersectsSphere(edit.Center, boundsRadius))
            {
                continue;
            }

            if (!chunk.ApplySlashBrush(edit, ResolveEditedMaterial))
            {
                continue;
            }

            chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
            QueueChunkForRebuild(chunk);
            _terrainDesirabilityDirty = true;
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

    public Vector3 ResolveSlashCenter(Vector3 hitPoint, Vector3 hitNormal, float slashDepth)
    {
        Vector3 normal = hitNormal.LengthSquared() > 0.0001f
            ? hitNormal.Normalized()
            : Vector3.Up;
        float inset = Mathf.Max(0.04f, slashDepth * 0.45f);
        return hitPoint - (normal * inset);
    }

    public void ClearStartupCache()
    {
        _chunkStore?.ClearStartupState();
        _startupLoadedChunks.Clear();
        _cacheManager?.ClearStartupSnapshotKnowledge();
        _cacheManager?.SetStartupSnapshotKeys(_startupLoadedChunks);
        _useStartupSnapshot = false;
    }

    public void ClearAllPersistentCache()
    {
        _chunkStore?.ClearAllChunkData();
        _cacheManager?.ClearPersistedKnowledge();
        _startupLoadedChunks.Clear();
        _cacheManager?.ClearStartupSnapshotKnowledge();
        _cacheManager?.SetStartupSnapshotKeys(_startupLoadedChunks);
        _useStartupSnapshot = false;
    }

    public bool IsColumnActiveAtPosition(Vector3 worldPosition)
    {
        Vector2I columnKey = new(
            Mathf.FloorToInt(worldPosition.X / _settings.ChunkSize),
            Mathf.FloorToInt(worldPosition.Z / _settings.ChunkSize));

        for (int y = 0; y < VerticalChunkCount; y++)
        {
            if (_residentChunks.TryGetValue(new Vector3I(columnKey.X, y, columnKey.Y), out TerrainChunk chunk) &&
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
            $"Search: {snapshot.SearchThrottleState} | invalidations {snapshot.SearchInvalidationCount} ({snapshot.SearchInvalidationReason}) | frontier {snapshot.FrontierSize} | visited {snapshot.VisitedCandidateCount} | desired cols {snapshot.DesiredColumnCount}\n" +
            $"Desired: {snapshot.DesiredChunkCount} | resident {snapshot.ResidentChunkCount} | ram {snapshot.RamCacheChunkCount} | in-flight {snapshot.InFlightChunkCount} | to add {snapshot.ToAddCount} | to release {snapshot.ToReleaseCount}\n" +
            $"Search time: {snapshot.LastDesiredSearchMs:0.00} ms | priority eval {snapshot.LastPriorityEvaluationMs:0.00} ms | visibility {snapshot.LastVisibilityHeuristicMs:0.00} ms | compactions {snapshot.FrontierCompactionCount}\n" +
            $"Startup: keys {snapshot.StartupSnapshotChunkCount} | desired coverage {snapshot.StartupDesiredCoverageCount}/{snapshot.DesiredChunkCount} | persisted records {snapshot.PersistedChunkRecordCount}\n" +
            $"Loads: running {snapshot.RunningLoadCount} | queued {snapshot.PendingLoadCount} | prepared {snapshot.PreparedChunkCount} | activate {snapshot.PendingActivationCount} | last {snapshot.LastChunkLoadCount} ({snapshot.LastChunkLoadMs:0.00} ms)\n" +
            $"Load source: resident {_residentReuseHits} | ram {snapshot.LastRamCacheLoadCount} | startup {snapshot.LastStartupChunkLoadCount} | db {snapshot.LastPersistedChunkLoadCount} | gen {snapshot.LastGeneratedChunkLoadCount}\n" +
            $"Release: {snapshot.LastChunkReleaseCount} ({snapshot.LastChunkReleaseMs:0.00} ms) | render {snapshot.LastVisualRebuildCount} ({snapshot.LastVisualRebuildMs:0.00} ms) | collision {snapshot.LastCollisionRebuildCount} ({snapshot.LastCollisionRebuildMs:0.00} ms)\n" +
            $"Cache: ram {snapshot.RamCacheHits} | startup {snapshot.StartupSnapshotHits} | db {snapshot.DatabaseHits} | gen {snapshot.GenerationFallbacks} | evicted {snapshot.EvictedChunks} | writes {snapshot.DirtyPersistWrites} | startup->db {snapshot.StartupPromotionWrites}\n" +
            $"Selected: {snapshot.LastSelectedChunkSummary}\n" +
            $"Released: {snapshot.LastReleasedChunkSummary}\n" +
            $"Source: {snapshot.LastChunkSourceSummary}";
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        int activeCount = 0;
        foreach (TerrainChunk chunk in _residentChunks.Values)
        {
            if (chunk.Visible)
            {
                activeCount++;
            }
        }

        long totalCacheHits =
            _residentReuseHits +
            _cacheManager.RamCacheHits +
            _cacheManager.StartupSnapshotHits +
            _cacheManager.DatabaseHits;
        long totalCacheMisses = _cacheManager.GeneratedFallbacks;

        return new TerrainWorldProfileSnapshot
        {
            ActiveChunkCount = activeCount,
            ResidentChunkCount = _residentChunks.Count,
            LoadedChunkCount = _residentChunks.Count + _cacheManager.RamCacheCount + _loadScheduler.PreparedCount,
            RamCacheChunkCount = _cacheManager.RamCacheCount,
            DesiredChunkCount = _desiredChunks.Count,
            DesiredColumnCount = _desiredSetBuilder.DesiredColumnCount,
            PendingLoadCount = _loadScheduler.PendingLoadCount,
            RunningLoadCount = _loadScheduler.RunningLoadCount,
            PendingActivationCount = _loadScheduler.PreparedCount,
            PreparedChunkCount = _loadScheduler.PreparedCount,
            InFlightChunkCount = _loadScheduler.PendingLoadCount + _loadScheduler.RunningLoadCount + _loadScheduler.PreparedCount,
            ToAddCount = _residencyManager.ToAdd.Count,
            ToReleaseCount = _residencyManager.ToRelease.Count,
            FrontierSize = _desiredSetBuilder.FrontierCount,
            VisitedCandidateCount = _desiredSetBuilder.VisitedCandidateCount,
            DirtyRenderCount = _dirtyRenderChunks.Count,
            DirtyCollisionCount = _dirtyCollisionChunks.Count,
            LastChunkLoadCount = _lastChunkLoadCount,
            LastChunkActivationCount = _lastChunkActivationCount,
            LastChunkReleaseCount = _lastChunkReleaseCount,
            LastVisualRebuildCount = _lastVisualRebuildCount,
            LastCollisionRebuildCount = _lastCollisionRebuildCount,
            LastChunkLoadMs = _lastChunkLoadMs,
            LastChunkActivationMs = _lastChunkActivationMs,
            LastChunkReleaseMs = _lastChunkReleaseMs,
            LastVisualRebuildMs = _lastVisualRebuildMs,
            LastCollisionRebuildMs = _lastCollisionRebuildMs,
            LastDesiredSearchMs = _desiredSetBuilder.LastSearchMs,
            LastPriorityEvaluationMs = _lastPriorityEvaluationMs,
            LastVisibilityHeuristicMs = _lastVisibilityHeuristicMs,
            LastStartupChunkLoadCount = _lastStartupChunkLoadCount,
            LastPersistedChunkLoadCount = _lastPersistedChunkLoadCount,
            LastRamCacheLoadCount = _lastRamCacheLoadCount,
            LastGeneratedChunkLoadCount = _lastGeneratedChunkLoadCount,
            LastStartupChunkLoadMs = _lastStartupChunkLoadMs,
            LastPersistedChunkLoadMs = _lastPersistedChunkLoadMs,
            LastRamCacheLoadMs = _lastRamCacheLoadMs,
            LastGeneratedChunkLoadMs = _lastGeneratedChunkLoadMs,
            ResidentReuseHits = _residentReuseHits,
            CacheHits = totalCacheHits,
            CacheMisses = totalCacheMisses,
            EvictedChunks = _cacheManager.RamCacheEvictions,
            RamCacheHits = _cacheManager.RamCacheHits,
            StartupSnapshotHits = _cacheManager.StartupSnapshotHits,
            DatabaseHits = _cacheManager.DatabaseHits,
            GenerationFallbacks = _cacheManager.GeneratedFallbacks,
            PersistedChunkRecordCount = _cacheManager.PersistedChunkRecordCount,
            StartupSnapshotChunkCount = _startupLoadedChunks.Count,
            StartupDesiredCoverageCount = ComputeStartupDesiredCoverageCount(),
            SearchInvalidationCount = _desiredSetBuilder.InvalidationCount,
            StalePriorityRefreshCount = _desiredSetBuilder.StaleRefreshCount,
            FrontierCompactionCount = _desiredSetBuilder.FrontierCompactionCount,
            DirtyPersistWrites = _cacheManager.DirtyPersistWrites,
            StartupPromotionWrites = _cacheManager.StartupPromotionWrites,
            SearchThrottleState = _desiredSetBuilder.ThrottleState.ToString(),
            SearchInvalidationReason = _desiredSetBuilder.LastInvalidationReason,
            LastSelectedChunkSummary = _lastSelectedChunkSummary,
            LastReleasedChunkSummary = _lastReleasedChunkSummary,
            LastChunkSourceSummary = _lastChunkSourceSummary,
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
            if (_residentChunks.TryGetValue(key, out TerrainChunk chunk) && chunk.IsInitialLoadReady)
            {
                readyCount++;
            }
        }

        return (float)readyCount / _desiredChunks.Count;
    }

    private void RefreshStreamingState(bool forceInvalidate)
    {
        HashSet<Vector3I> previousDesired = new(_desiredChunks);
        SearchSample sample = BuildSearchSample();
        int effectiveSearchRadius = GetEffectiveSearchRadius();
        _searchEvaluationContext = new SearchEvaluationContext(
            sample.CenterChunk,
            sample.StreamForward,
            sample.TrackedPosition,
            sample.CameraPosition,
            effectiveSearchRadius);
        _visibilityHeuristicCache.Clear();
        TerrainDesiredSetContext searchContext = new(
            sample.CenterChunk,
            effectiveSearchRadius,
            Mathf.Max(MaxActiveColumns, 1),
            GuaranteedColumnRadius);
        HashSet<Vector2I> residentColumns = GetResidentColumns();

        string invalidationReason = GetSearchInvalidationReason(sample, searchContext, forceInvalidate);
        bool invalidated = !string.IsNullOrEmpty(invalidationReason);
        if (!string.IsNullOrEmpty(invalidationReason))
        {
            _desiredSetBuilder.Invalidate(invalidationReason, searchContext, residentColumns, EvaluateColumnPriority);
            _lastInvalidationCenterChunk = sample.CenterChunk;
            _lastInvalidationPosition = sample.TrackedPosition;
            _lastInvalidationYawDegrees = sample.YawDegrees;
            _lastInvalidationPitchDegrees = sample.PitchDegrees;
            _lastInvalidationMovementDirection = sample.MovementDirection;
            _terrainDesirabilityDirty = false;
        }

        _desiredSetBuilder.AdvanceSearch(searchContext, residentColumns, GetCurrentSearchBudget(), EvaluateColumnPriority);
        RebuildDesiredChunkSet();
        bool desiredChanged = !AreSetsEqual(previousDesired, _desiredChunks);
        if (invalidated || desiredChanged)
        {
            UpdateColumnRetention(searchContext.CenterChunk);
        }
        UpdateResidentVisibility();

        foreach (Vector3I key in _desiredChunks)
        {
            if (_residentChunks.ContainsKey(key) && !previousDesired.Contains(key))
            {
                _residentReuseHits++;
                _lastChunkSourceSummary = $"{key} <- {TerrainChunkLoadSource.Resident}";
            }
        }

        _inFlightKeys.Clear();
        _loadScheduler.PopulateInFlightKeys(_inFlightKeys);
        _residencyManager.Recompute(_desiredChunks, _residentChunks.Keys, _inFlightKeys, EvaluateChunkPriority, BuildReleaseInfo);

        if (_residencyManager.ToAdd.Count > 0)
        {
            _lastSelectedChunkSummary = _residencyManager.ToAdd[0].Summary;
        }
        else if (_desiredSetBuilder.LastSelectedColumnInfo != null)
        {
            _lastSelectedChunkSummary = _desiredSetBuilder.LastSelectedColumnInfo.Summary;
        }

        _loadScheduler.SyncTargets(_residencyManager.DesiredSet, _residencyManager.ToAdd);
    }

    private SearchSample BuildSearchSample()
    {
        Vector3 trackedPosition = _trackedCharacter?.GlobalPosition ?? Vector3.Zero;
        Vector2I centerChunk = new(
            Mathf.FloorToInt(trackedPosition.X / _settings.ChunkSize),
            Mathf.FloorToInt(trackedPosition.Z / _settings.ChunkSize));

        Camera3D camera = GetViewport().GetCamera3D();
        Vector3 cameraForward3D = camera == null ? -Vector3.Forward : (-camera.GlobalTransform.Basis.Z).Normalized();
        Vector3 cameraPosition = camera?.GlobalPosition ?? trackedPosition;
        Vector2 streamForward = new(cameraForward3D.X, cameraForward3D.Z);
        streamForward = streamForward.LengthSquared() < 0.0001f ? Vector2.Zero : streamForward.Normalized();

        float yaw = Mathf.RadToDeg(Mathf.Atan2(cameraForward3D.X, cameraForward3D.Z));
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(cameraForward3D.Y, -1.0f, 1.0f)));

        Vector3 positionDelta = _lastInvalidationPosition.X == float.MinValue
            ? Vector3.Zero
            : trackedPosition - _lastInvalidationPosition;
        Vector2 movementDirection = new(positionDelta.X, positionDelta.Z);
        movementDirection = movementDirection.LengthSquared() < 0.0001f
            ? Vector2.Zero
            : movementDirection.Normalized();

        return new SearchSample(centerChunk, trackedPosition, streamForward, yaw, pitch, movementDirection, cameraPosition);
    }

    private string GetSearchInvalidationReason(SearchSample sample, TerrainDesiredSetContext searchContext, bool forceInvalidate)
    {
        if (forceInvalidate || _lastInvalidationPosition.X == float.MinValue)
        {
            return "startup";
        }

        if (sample.CenterChunk != _lastInvalidationCenterChunk)
        {
            return $"entered chunk {sample.CenterChunk}";
        }

        if (Mathf.Abs(DeltaAngleDegrees(sample.YawDegrees, _lastInvalidationYawDegrees)) >= SearchInvalidationYawDegrees)
        {
            return $"yaw {sample.YawDegrees:0.0}";
        }

        if (Mathf.Abs(sample.PitchDegrees - _lastInvalidationPitchDegrees) >= SearchInvalidationPitchDegrees)
        {
            return $"pitch {sample.PitchDegrees:0.0}";
        }

        float movementDistance = sample.TrackedPosition.DistanceTo(_lastInvalidationPosition);
        if (movementDistance >= _settings.ChunkSize * SearchInvalidationMoveDistanceFactor &&
            sample.MovementDirection != Vector2.Zero &&
            _lastInvalidationMovementDirection != Vector2.Zero)
        {
            float dot = Mathf.Clamp(sample.MovementDirection.Dot(_lastInvalidationMovementDirection), -1.0f, 1.0f);
            float angle = Mathf.RadToDeg(Mathf.Acos(dot));
            if (angle >= SearchInvalidationMovementDirectionDegrees)
            {
                return $"movement {angle:0.0}";
            }
        }

        if (_terrainDesirabilityDirty)
        {
            return "terrain edited";
        }

        if (_desiredSetBuilder.DesiredColumnCount < searchContext.MaxColumns && _desiredSetBuilder.FrontierCount == 0)
        {
            return "frontier exhausted";
        }

        return string.Empty;
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
        int budgetRadius = GetBudgetedColumnRadius(MaxActiveColumns);
        return Mathf.Clamp(farRadius, radius, Mathf.Max(radius, budgetRadius));
    }

    private static int GetBudgetedColumnRadius(int maxColumns)
    {
        if (maxColumns <= 0)
        {
            return 1;
        }

        float area = maxColumns / Mathf.Pi;
        return Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(area)) + 2);
    }

    private void RebuildDesiredChunkSet()
    {
        _desiredChunks.Clear();
        foreach (Vector2I column in _desiredSetBuilder.DesiredColumns)
        {
            for (int y = 0; y < VerticalChunkCount; y++)
            {
                _desiredChunks.Add(new Vector3I(column.X, y, column.Y));
            }
        }
    }

    private HashSet<Vector2I> GetResidentColumns()
    {
        HashSet<Vector2I> columns = new();
        foreach (Vector3I key in _residentChunks.Keys)
        {
            columns.Add(new Vector2I(key.X, key.Z));
        }

        return columns;
    }

    private void UpdateResidentVisibility()
    {
        foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _residentChunks)
        {
            bool desired = _desiredChunks.Contains(entry.Key);
            entry.Value.Visible = desired;
            entry.Value.ProcessMode = desired ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
    }

    private void UpdateColumnRetention(Vector2I centerChunk)
    {
        float decayFactor = Mathf.Clamp(RetentionDecayFactor, 0.5f, 0.995f);
        List<Vector2I> keys = new(_columnRetention.Keys);
        foreach (Vector2I key in keys)
        {
            float decayed = _columnRetention[key] * decayFactor;
            if (decayed < 0.05f)
            {
                _columnRetention.Remove(key);
                continue;
            }

            _columnRetention[key] = decayed;
        }

        foreach (Vector3I key in _desiredChunks)
        {
            _columnRetention[new Vector2I(key.X, key.Z)] = 1.0f;
        }

        int radius = Mathf.CeilToInt(Mathf.Max(GuaranteedColumnRadius, 1.0f));
        for (int z = -radius; z <= radius; z++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                Vector2 offset = new(x, z);
                if (offset.Length() > GuaranteedColumnRadius + 0.35f)
                {
                    continue;
                }

                _columnRetention[new Vector2I(centerChunk.X + x, centerChunk.Y + z)] = 1.0f;
            }
        }
    }

    private void ProcessCompletedChunkLoads()
    {
        foreach (PreparedChunkResult result in _loadScheduler.DrainCompletedLoads())
        {
            RegisterLoadStats(result);
            _lastChunkSourceSummary = $"{result.Key} <- {result.Source}";

            if (_loadScheduler.IsTargetKey(result.Key) && _desiredChunks.Contains(result.Key))
            {
                _loadScheduler.RegisterPreparedChunk(result);
                continue;
            }

            _cacheManager.StorePreparedChunk(result.Key, result.Data, result.Source);
        }
    }

    private void RegisterLoadStats(PreparedChunkResult result)
    {
        _lastChunkLoadCount++;
        _lastChunkLoadMs += result.LoadMs;

        switch (result.Source)
        {
            case TerrainChunkLoadSource.RamCache:
                _lastRamCacheLoadCount++;
                _lastRamCacheLoadMs += result.LoadMs;
                break;
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
    }

    private void ProcessPreparedChunkReleases()
    {
        foreach (PreparedChunkResult prepared in _loadScheduler.ExtractPreparedOutsideTargets())
        {
            _cacheManager.StorePreparedChunk(prepared.Key, prepared.Data, prepared.Source);
        }

        int budget = GetCurrentReleaseBudget();
        for (int i = 0; i < _residencyManager.ToRelease.Count && budget > 0; i++)
        {
            ChunkReleaseInfo release = _residencyManager.ToRelease[i];
            if (!_residentChunks.TryGetValue(release.Key, out TerrainChunk chunk))
            {
                continue;
            }

            ulong start = Time.GetTicksUsec();
            _cacheManager.ReleaseResidentChunk(release.Key, chunk);
            _dirtyRenderChunks.Remove(chunk);
            _dirtyCollisionChunks.Remove(chunk);
            _residentChunks.Remove(release.Key);
            chunk.QueueFree();

            _lastChunkReleaseCount++;
            _lastChunkReleaseMs += (Time.GetTicksUsec() - start) / 1000.0;
            _lastReleasedChunkSummary = release.Summary;
            budget--;
        }
    }

    private void ProcessQueuedChunkLoads()
    {
        _loadScheduler.StartLoads(GetCurrentLoadJobBudget(), LoadChunkData);
    }

    private PreparedChunkResult LoadChunkData(Vector3I key, float priorityScore)
    {
        ulong start = Time.GetTicksUsec();
        ChunkAcquisitionResult acquired = _cacheManager.AcquireChunk(key, _useStartupSnapshot, GenerateChunkData);
        double loadMs = (Time.GetTicksUsec() - start) / 1000.0;
        return new PreparedChunkResult(key, acquired.Data, acquired.Source, loadMs, priorityScore);
    }

    private VoxelChunkData GenerateChunkData(Vector3I key)
    {
        Vector3 origin = new(
            key.X * _settings.ChunkSize,
            _settings.BaseY + (key.Y * _settings.ChunkSize),
            key.Z * _settings.ChunkSize);

        VoxelChunkData generated = new(PointsPerAxis, VoxelSize, origin);
        VoxelFieldGenerator generator = new(Seed, TerrainHeight, DetailHeight, CaveScale, CaveThreshold);
        generator.FillChunk(generated);
        return generated;
    }

    private void ProcessPendingChunkActivations()
    {
        int activationBudget = GetCurrentActivationBudget();
        while (activationBudget > 0 && _loadScheduler.TryTakeNextActivation(out PreparedChunkResult prepared))
        {
            if (_residentChunks.ContainsKey(prepared.Key))
            {
                activationBudget--;
                continue;
            }

            ulong attachStart = Time.GetTicksUsec();
            TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
            AddChild(chunk);
            chunk.Initialize(prepared.Key, _settings);
            chunk.SetData(prepared.Data, prepared.Source, 0.0);
            chunk.Visible = _desiredChunks.Contains(prepared.Key);
            chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            _residentChunks[prepared.Key] = chunk;
            QueueChunkForRebuild(chunk);

            _lastChunkActivationCount++;
            _lastChunkActivationMs += (Time.GetTicksUsec() - attachStart) / 1000.0;
            activationBudget--;
        }
    }

    private void ProcessDirtyChunks()
    {
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

        if (!_initialLoadComplete &&
            _desiredChunks.Count > 0 &&
            _residencyManager.ToAdd.Count == 0 &&
            _loadScheduler.PendingLoadCount == 0 &&
            _loadScheduler.RunningLoadCount == 0 &&
            _loadScheduler.PreparedCount == 0 &&
            GetInitialLoadProgress() >= 0.999f)
        {
            _initialLoadComplete = true;
            _useStartupSnapshot = false;
            _startupLoadedChunks.Clear();
            _cacheManager.SetStartupSnapshotKeys(_startupLoadedChunks);
            EmitSignal(SignalName.InitialLoadCompleted);
        }
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
        if (_residentChunks.TryGetValue(key, out TerrainChunk existingChunk))
        {
            return existingChunk;
        }

        ChunkAcquisitionResult acquired = _cacheManager.AcquireChunk(key, _useStartupSnapshot, GenerateChunkData);
        TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
        AddChild(chunk);
        chunk.Initialize(key, _settings);
        chunk.SetData(acquired.Data, acquired.Source, 0.0);
        chunk.Visible = _desiredChunks.Contains(key);
        chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        _residentChunks[key] = chunk;
        QueueChunkForRebuild(chunk);
        _lastChunkSourceSummary = $"{key} <- {acquired.Source}";
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

    private ColumnPriorityInfo EvaluateColumnPriority(Vector2I columnKey)
    {
        ulong start = Time.GetTicksUsec();

        Vector2 offset = new(columnKey.X - _searchEvaluationContext.CenterChunk.X, columnKey.Y - _searchEvaluationContext.CenterChunk.Y);
        float distance = offset.Length();
        bool guaranteed = distance <= GuaranteedColumnRadius;

        float forwardAlignment = 0.0f;
        float shoulderBonus = 0.0f;
        if (_searchEvaluationContext.StreamForward != Vector2.Zero && offset.LengthSquared() > 0.0001f)
        {
            forwardAlignment = offset.Normalized().Dot(_searchEvaluationContext.StreamForward);
            shoulderBonus = ComputeShoulderPriorityBonus(distance, forwardAlignment);
        }

        float visibility = MeasureVisibilityHeuristic(columnKey);
        float residentBonus = IsColumnResident(columnKey) ? ResidentPriorityBonus : 0.0f;
        float retentionBonus = _columnRetention.GetValueOrDefault(columnKey, 0.0f) * RetentionPriorityWeight;
        float adjacencyBonus = CountAdjacentResidentOrDesiredColumns(columnKey) * AdjacencyPriorityWeight;
        float loadCostBonus = ComputeColumnLoadCostBonus(columnKey);

        float total = guaranteed
            ? 10000.0f - (distance * 10.0f)
            : 100.0f - (distance * 7.5f);
        total += (forwardAlignment + 1.0f) * ForwardPriorityWeight;
        if (forwardAlignment < 0.0f)
        {
            total += forwardAlignment * BehindViewerPenalty;
        }
        total += visibility * 28.0f;
        total += residentBonus + retentionBonus + adjacencyBonus + shoulderBonus + loadCostBonus;

        _lastPriorityEvaluationMs += (Time.GetTicksUsec() - start) / 1000.0;
        return new ColumnPriorityInfo(
            columnKey,
            total,
            distance,
            forwardAlignment,
            visibility,
            residentBonus,
            retentionBonus,
            adjacencyBonus,
            shoulderBonus,
            loadCostBonus,
            EstimateColumnSource(columnKey),
            guaranteed);
    }

    private ChunkPriorityInfo EvaluateChunkPriority(Vector3I key)
    {
        ulong start = Time.GetTicksUsec();

        Vector2 offset = new(key.X - _searchEvaluationContext.CenterChunk.X, key.Z - _searchEvaluationContext.CenterChunk.Y);
        float distance = offset.Length();
        bool guaranteed = distance <= GuaranteedColumnRadius;

        float forwardAlignment = 0.0f;
        float shoulderBonus = 0.0f;
        if (_searchEvaluationContext.StreamForward != Vector2.Zero && offset.LengthSquared() > 0.0001f)
        {
            forwardAlignment = offset.Normalized().Dot(_searchEvaluationContext.StreamForward);
            shoulderBonus = ComputeShoulderPriorityBonus(distance, forwardAlignment);
        }

        float visibility = MeasureVisibilityHeuristic(new Vector2I(key.X, key.Z));
        float retentionBonus = _columnRetention.GetValueOrDefault(new Vector2I(key.X, key.Z), 0.0f) * RetentionPriorityWeight;
        float adjacencyBonus = CountAdjacentResidentOrDesiredColumns(new Vector2I(key.X, key.Z)) * AdjacencyPriorityWeight;
        TerrainChunkLoadSource estimatedSource = _cacheManager.EstimateSource(key, _useStartupSnapshot);
        float loadCostBonus = GetLoadCostBonus(estimatedSource);
        float verticalBias = -key.Y * 2.5f;

        float total = guaranteed
            ? 1000.0f - (distance * 10.0f)
            : 100.0f - (distance * 9.0f);
        total += (forwardAlignment + 1.0f) * (ForwardPriorityWeight * 0.7f);
        if (forwardAlignment < 0.0f)
        {
            total += forwardAlignment * BehindViewerPenalty;
        }
        total += visibility * 22.0f;
        total += retentionBonus + adjacencyBonus + shoulderBonus + loadCostBonus + verticalBias;

        _lastPriorityEvaluationMs += (Time.GetTicksUsec() - start) / 1000.0;
        return new ChunkPriorityInfo(
            key,
            total,
            distance,
            forwardAlignment,
            visibility,
            retentionBonus,
            adjacencyBonus,
            shoulderBonus,
            loadCostBonus,
            verticalBias,
            estimatedSource,
            guaranteed);
    }

    private ChunkReleaseInfo BuildReleaseInfo(Vector3I key)
    {
        ChunkPriorityInfo retain = EvaluateChunkPriority(key);
        TerrainChunkLoadSource source = _residentChunks.TryGetValue(key, out TerrainChunk chunk)
            ? chunk.LoadSource
            : TerrainChunkLoadSource.Resident;
        string reason = $"not desired | {retain.Summary}";
        return new ChunkReleaseInfo(key, retain.TotalScore, reason, source);
    }

    private float MeasureVisibilityHeuristic(Vector2I columnKey)
    {
        if (_visibilityHeuristicCache.TryGetValue(columnKey, out float cached))
        {
            return cached;
        }

        ulong start = Time.GetTicksUsec();
        float visibility = EstimateHorizonVisibility(columnKey, _searchEvaluationContext.CameraPosition);
        _lastVisibilityHeuristicMs += (Time.GetTicksUsec() - start) / 1000.0;
        _visibilityHeuristicCache[columnKey] = visibility;
        return visibility;
    }

    private float ComputeColumnLoadCostBonus(Vector2I columnKey)
    {
        float total = 0.0f;
        for (int y = 0; y < VerticalChunkCount; y++)
        {
            total += GetLoadCostBonus(_cacheManager.EstimateSource(new Vector3I(columnKey.X, y, columnKey.Y), _useStartupSnapshot));
        }

        return VerticalChunkCount <= 0 ? 0.0f : total / VerticalChunkCount;
    }

    private TerrainChunkLoadSource EstimateColumnSource(Vector2I columnKey)
    {
        TerrainChunkLoadSource bestSource = TerrainChunkLoadSource.ProceduralGeneration;
        float bestBonus = float.NegativeInfinity;
        for (int y = 0; y < VerticalChunkCount; y++)
        {
            TerrainChunkLoadSource source = _cacheManager.EstimateSource(new Vector3I(columnKey.X, y, columnKey.Y), _useStartupSnapshot);
            float bonus = GetLoadCostBonus(source);
            if (bonus > bestBonus)
            {
                bestBonus = bonus;
                bestSource = source;
            }
        }

        return bestSource;
    }

    private float GetLoadCostBonus(TerrainChunkLoadSource source)
    {
        return source switch
        {
            TerrainChunkLoadSource.RamCache => RamCacheLoadPriorityBonus,
            TerrainChunkLoadSource.StartupSnapshot => StartupSnapshotLoadPriorityBonus,
            TerrainChunkLoadSource.PersistedChunk => PersistedChunkLoadPriorityBonus,
            TerrainChunkLoadSource.Resident => ResidentPriorityBonus,
            _ => GeneratedChunkLoadPriorityBonus
        };
    }

    private float ComputeShoulderPriorityBonus(float distance, float forwardAlignment)
    {
        if (ShoulderPriorityMultiplier <= 0.0f)
        {
            return 0.0f;
        }

        float clampedHalfAngle = Mathf.Clamp(ShoulderHalfAngleDegrees, 5.0f, 85.0f);
        float shoulderCos = Mathf.Cos(Mathf.DegToRad(clampedHalfAngle));
        if (shoulderCos <= 0.001f)
        {
            return 0.0f;
        }

        float shoulderBand = 1.0f - Mathf.Clamp(Mathf.Abs(forwardAlignment) / shoulderCos, 0.0f, 1.0f);
        if (shoulderBand <= 0.0f)
        {
            return 0.0f;
        }

        float shoulderRadius = Mathf.Max(
            GuaranteedColumnRadius + 1.0f,
            _searchEvaluationContext.SearchRadius * Mathf.Max(ShoulderDistanceMultiplier, 0.1f));
        float distanceWeight = 1.0f - Mathf.Clamp(
            (distance - GuaranteedColumnRadius) /
            Mathf.Max(0.001f, shoulderRadius - GuaranteedColumnRadius),
            0.0f,
            1.0f);

        return shoulderBand * distanceWeight * (ForwardPriorityWeight * ShoulderPriorityMultiplier);
    }

    private int CountAdjacentResidentOrDesiredColumns(Vector2I columnKey)
    {
        int count = 0;
        Vector2I[] neighbors =
        {
            Vector2I.Right,
            Vector2I.Left,
            Vector2I.Up,
            Vector2I.Down
        };

        foreach (Vector2I neighbor in neighbors)
        {
            Vector2I testKey = columnKey + neighbor;
            if (IsColumnResident(testKey) || _desiredSetBuilder.ContainsDesiredColumn(testKey))
            {
                count++;
            }
        }

        return count;
    }

    private float EstimateHorizonVisibility(Vector2I columnKey, Vector3 cameraPosition)
    {
        if (!UseHorizonLoadPriority || _prioritySampler == null)
        {
            return 1.0f;
        }
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
            if (_residentChunks.ContainsKey(new Vector3I(columnKey.X, y, columnKey.Y)))
            {
                return true;
            }
        }

        return false;
    }

    private void ResetFrameStats()
    {
        _lastChunkLoadCount = 0;
        _lastChunkActivationCount = 0;
        _lastChunkReleaseCount = 0;
        _lastStartupChunkLoadCount = 0;
        _lastPersistedChunkLoadCount = 0;
        _lastRamCacheLoadCount = 0;
        _lastGeneratedChunkLoadCount = 0;
        _lastVisualRebuildCount = 0;
        _lastCollisionRebuildCount = 0;
        _lastChunkLoadMs = 0.0;
        _lastChunkActivationMs = 0.0;
        _lastChunkReleaseMs = 0.0;
        _lastStartupChunkLoadMs = 0.0;
        _lastPersistedChunkLoadMs = 0.0;
        _lastRamCacheLoadMs = 0.0;
        _lastGeneratedChunkLoadMs = 0.0;
        _lastVisualRebuildMs = 0.0;
        _lastCollisionRebuildMs = 0.0;
        _lastPriorityEvaluationMs = 0.0;
        _lastVisibilityHeuristicMs = 0.0;
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

        foreach (TerrainStartupChunkDescriptor chunk in startupState.Chunks)
        {
            _startupLoadedChunks.Add(chunk.Key);
        }

        _cacheManager.SetStartupSnapshotKeys(_startupLoadedChunks);
        _useStartupSnapshot = _startupLoadedChunks.Count > 0;
        _lastInvalidationPosition = startupState.PlayerPosition;
    }

    private bool IsStartupBoostActive()
    {
        return _useStartupSnapshot && !_initialLoadComplete;
    }

    private int GetCurrentSearchBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxDesiredSearchStepsPerFrame, StartupDesiredSearchStepsPerFrame)
            : MaxDesiredSearchStepsPerFrame;
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

    private int GetCurrentReleaseBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxChunkReleasesPerFrame, StartupChunkReleasesPerFrame)
            : MaxChunkReleasesPerFrame;
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

        List<TerrainStartupChunkSnapshot> startupChunks = new(_residentChunks.Count + _cacheManager.RamCacheCount);
        foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _residentChunks)
        {
            TerrainChunk chunk = entry.Value;
            if (!chunk.HasData)
            {
                continue;
            }

            startupChunks.Add(new TerrainStartupChunkSnapshot(entry.Key, chunk.Visible, chunk.Data));
        }

        startupChunks.AddRange(_cacheManager.BuildStartupCacheSnapshots());
        _chunkStore.SaveStartupState(_trackedCharacter.GlobalPosition, startupChunks);
    }

    private void LogStreamingTuningSummary()
    {
        int estimatedResidentChunks = Mathf.Max(MaxActiveColumns, 1) * Mathf.Max(VerticalChunkCount, 1);
        int estimatedRamBudget = Mathf.Max(0, MaxLoadedChunks - estimatedResidentChunks);
        GD.Print(
            $"Terrain streaming tuning | desired cols {MaxActiveColumns} | est resident chunks {estimatedResidentChunks} | loaded cap {MaxLoadedChunks} | est ram cache {estimatedRamBudget} | retain {RetentionPriorityWeight:0.0}/{RetentionDecayFactor:0.00} | shoulder {ShoulderHalfAngleDegrees:0.#}deg {ShoulderDistanceMultiplier:0.00}x {ShoulderPriorityMultiplier:0.00}x");
    }

    private static float DeltaAngleDegrees(float current, float previous)
    {
        return Mathf.Wrap(current - previous, -180.0f, 180.0f);
    }

    private int ComputeStartupDesiredCoverageCount()
    {
        int count = 0;
        foreach (Vector3I key in _desiredChunks)
        {
            if (_startupLoadedChunks.Contains(key))
            {
                count++;
            }
        }

        return count;
    }

    private static bool AreSetsEqual(HashSet<Vector3I> a, HashSet<Vector3I> b)
    {
        return a.Count == b.Count && a.SetEquals(b);
    }

    private readonly record struct SearchSample(
        Vector2I CenterChunk,
        Vector3 TrackedPosition,
        Vector2 StreamForward,
        float YawDegrees,
        float PitchDegrees,
        Vector2 MovementDirection,
        Vector3 CameraPosition);
}
