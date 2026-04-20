using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using TowerOfBaby.Debugging;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

// Retired from the active gameplay path. This preserves the old fixed-chunk runtime
// for reference while the replacement terrain runtime is built behind TerrainWorld.
public partial class TerrainLegacyWorldRuntime : Node3D
{
    [Signal] public delegate void InitialLoadCompletedEventHandler();
    private const string PlayerDetailRequestId = "__player_proximity";
    private const string BiomeDetailRequestId = "__biome_policy";
    private const int MinimumCoverageTriangleCount = 6;
    private const int TinyFragmentTriangleCount = 6;
    private const ulong LowValueDetailBackoffFrames = 12;
    private const ulong PressureThrottledDetailBackoffFrames = 8;
    private const float SoftSearchInvalidationCooldownSeconds = 0.30f;
    private const float VisibilityCacheHorizontalBucketSizeChunks = 0.40f;
    private const float VisibilityCacheVerticalBucketSizeChunks = 0.60f;
    private const int MaxCachedColumnPriorityEntries = 2048;
    private const int MaxCachedChunkPriorityEntries = 4096;
    private static readonly int RecommendedVisualMeshWorkerJobs = Math.Max(1, Math.Min(System.Environment.ProcessorCount / 2, 2));
    private static readonly Vector2[] ColumnSurfaceSamplePattern =
    {
        new(0.15f, 0.15f),
        new(0.50f, 0.15f),
        new(0.85f, 0.15f),
        new(0.15f, 0.50f),
        new(0.50f, 0.50f),
        new(0.85f, 0.50f),
        new(0.15f, 0.85f),
        new(0.50f, 0.85f),
        new(0.85f, 0.85f)
    };
    private readonly record struct TerrainDetailPromotionDeferDecision(
        TerrainDetailPromotionState State,
        string Reason,
        ulong NextEligibleFrame,
        double NextEligibleAtSeconds);
    private readonly record struct VisibilityCacheBucket(int X, int Y, int Z);
    private readonly record struct CachedColumnPriorityMetadata(
        BiomeId DominantBiome,
        int StructureCount,
        TerrainStructureType DominantStructureType,
        bool RequestsHigherTerrainDetail,
        float LoadCostBonus,
        TerrainChunkLoadSource EstimatedSource);
    private readonly record struct CachedChunkPriorityMetadata(
        BiomeId DominantBiome,
        int StructureCount,
        TerrainStructureType DominantStructureType,
        bool RequestsHigherTerrainDetail,
        float LoadCostBonus,
        TerrainChunkLoadSource EstimatedSource);
    private readonly record struct StagedVisualEnqueueEntry(Vector3I Key, int Token);
    private readonly record struct StagedVisualEnqueuePriority(int Lane, float NegativePriorityScore, int Token)
        : IComparable<StagedVisualEnqueuePriority>
    {
        public int CompareTo(StagedVisualEnqueuePriority other)
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
    [Export] public float WaterLevel = -6.0f;
    [Export] public float ShorelineFalloff = 3.4f;
    [Export] public float WaterBasinInfluence = 0.48f;
    [Export] public bool UseHorizonLoadPriority = true;
    [Export] public float OccludedPriorityScale = 0.3f;

    [ExportGroup("Search")]
    [Export] public int SearchRadius = 8;
    [Export] public int MaxActiveColumns = 72;
    [Export] public float GuaranteedColumnRadius = 1.6f;
    [Export] public int MaxDesiredSearchStepsPerFrame = 32;
    [Export] public int StartupDesiredSearchStepsPerFrame = 96;
    [Export] public int ForegroundCatchupSearchStepsPerFrame = 12;
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

    [ExportGroup("Local Detail")]
    [Export] public bool EnableLocalDetailRequests = true;
    [Export] public float PlayerDetailRequestRadius = 10.0f;
    [Export] public float BiomeDetailActivationRadius = 18.0f;
    [Export] public float BiomeDetailVerticalMargin = 3.8f;
    [Export] public float DetailRequestSnapStep = 2.4f;
    [Export] public int ChunkDetailWarmupFrames = 1;
    [Export] public float ChunkDetailWarmupSeconds = 0.10f;
    [Export] public float DetailRequestCooldownSeconds = 0.18f;
    [Export] public int MaxDetailPromotionsPerFrame = 1;
    [Export] public int MaxHighPriorityMeshEnqueuesPerFrame = 4;
    [Export] public int MaxNearCoarseMeshEnqueuesPerFrame = 3;
    [Export] public int MaxDetailPromotionActivationsPerFrame = 1;
    [Export] public int HighPriorityQueueSoftLimit = 18;
    [Export] public int HighPriorityQueueHardLimit = 36;

    [ExportGroup("Persistence")]
    [Export] public bool EnableStartupStatePersistence = true;
    [Export] public int VerticalChunkCount = 3;
    // Keep conservative headroom above the resident target so fast sweeps revisit RAM before falling back to generation.
    [Export] public int MaxLoadedChunks = 120;
    [Export] public int MaxChunkGenerationJobs = 2;
    [Export] public int MaxChunkActivationsPerFrame = 2;
    [Export] public int MaxChunkReleasesPerFrame = 4;
    [Export] public float MaxActivationMainThreadBudgetMs = 1.5f;
    [Export] public int MaxVisualMeshWorkerJobs = RecommendedVisualMeshWorkerJobs;
    [Export] public int MaxEditVisualMeshWorkerJobs = 1;
    [Export] public int MaxCoarseVisualMeshWorkerJobs = RecommendedVisualMeshWorkerJobs;
    [Export] public int MaxDetailVisualMeshWorkerJobs = 1;
    [Export] public int MaxBackgroundVisualMeshWorkerJobs = 1;
    [Export] public int MaxVisualChunkRebuildsPerFrame = 2;
    [Export] public float MaxVisualCommitMainThreadBudgetMs = 2.0f;
    [Export] public int MaxCollisionChunkRebuildsPerFrame = 1;
    [Export] public float MaxCollisionMainThreadBudgetMs = 2.5f;
    [Export] public double QueueWaitPressureMs = 400.0;
    [Export] public int QueueDepthPressureThreshold = 10;
    [Export] public int MaxDeferredLowPriorityBuilds = 12;

    [ExportGroup("Startup Boost")]
    [Export] public int StartupChunkGenerationJobs = 8;
    [Export] public int StartupChunkActivationsPerFrame = 8;
    [Export] public int StartupChunkReleasesPerFrame = 8;
    [Export] public float StartupActivationMainThreadBudgetMs = 3.5f;
    [Export] public int StartupVisualMeshWorkerJobs = RecommendedVisualMeshWorkerJobs;
    [Export] public int StartupCoarseVisualMeshWorkerJobs = RecommendedVisualMeshWorkerJobs;
    [Export] public int StartupVisualChunkRebuildsPerFrame = 8;
    [Export] public float StartupVisualCommitMainThreadBudgetMs = 4.0f;
    [Export] public int StartupCollisionChunkRebuildsPerFrame = 4;
    [Export] public float StartupCollisionMainThreadBudgetMs = 5.0f;
    [Export] public float CollisionRebuildDelaySeconds = 0.08f;

    [ExportGroup("Debug")]
    [Export] public bool EnableTerrainInstrumentation = true;
    [Export] public bool EnableTerrainVertexTint = true;
    [Export] public bool EnableBiomeDebugTint = false;
    [Export] public bool TerrainGenerateTangents = false;
    [Export] public TerrainVisualDebugMode TerrainDebugView = TerrainVisualDebugMode.Lit;
    [Export] public bool UseExperimentalComputeMeshing = false;

    private readonly Dictionary<Vector3I, TerrainChunk> _residentChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyRenderChunks = new();
    private readonly HashSet<TerrainChunk> _dirtyCollisionChunks = new();
    private readonly Dictionary<Vector2I, float> _columnRetention = new();
    private readonly Dictionary<Vector2I, float> _columnSurfaceMaxYCache = new();
    private readonly Dictionary<Vector2I, CachedColumnPriorityMetadata> _columnPriorityMetadataCache = new();
    private readonly Dictionary<Vector3I, CachedChunkPriorityMetadata> _chunkPriorityMetadataCache = new();
    private readonly HashSet<Vector3I> _desiredChunks = new();
    private readonly HashSet<Vector3I> _inFlightKeys = new();
    private readonly TerrainDesiredSetBuilder _desiredSetBuilder = new();
    private readonly TerrainResidencyManager _residencyManager = new();
    private readonly TerrainLoadScheduler _loadScheduler = new();
    private readonly TerrainMeshBuildScheduler _meshBuildScheduler = new();
    private readonly PriorityQueue<StagedVisualEnqueueEntry, StagedVisualEnqueuePriority> _stagedHighPriorityVisualEnqueues = new();
    private readonly Dictionary<Vector3I, StagedVisualEnqueueState> _stagedHighPriorityVisualEnqueueStates = new();
    private readonly Dictionary<Vector2I, float> _visibilityHeuristicCache = new();
    private readonly PriorityQueue<TerrainVisualBuildCompletedJob, TerrainWorkPriority> _pendingMeshCommits = new();
    private readonly PriorityQueue<CollisionQueueEntry, TerrainWorkPriority> _collisionQueue = new();
    private readonly Dictionary<Vector3I, CollisionQueueState> _collisionQueueStates = new();
    private readonly Dictionary<Vector3I, string> _coverageHoldLogReasons = new();
    private readonly Dictionary<Vector3I, string> _coverageReleaseBlockLogReasons = new();
    private readonly HashSet<Vector3I> _loggedEmptyVerticalChunkSkips = new();

    private VoxelFieldGenerator _prioritySampler = null!;
    private TerrainBiomeClassifier _biomeClassifier = null!;
    private TerrainStructureSource _structureSource = null!;
    private TerrainWorldSettings _settings = null!;
    private TerrainChunkStore _chunkStore = null!;
    private TerrainCacheManager _cacheManager = null!;
    private TerrainStatsTracker _terrainStats = null!;
    private Node3D _trackedCharacter = null!;
    private ITerrainMeshBackend _meshBackend = null!;
    private VoxelMeshBuildOptions _meshBuildOptions;
    private readonly HashSet<Vector3I> _startupLoadedChunks = new();

    private Vector2I _lastInvalidationCenterChunk = new(int.MinValue, int.MinValue);
    private Vector3 _lastInvalidationPosition = new(float.MinValue, float.MinValue, float.MinValue);
    private float _lastInvalidationYawDegrees = float.NaN;
    private float _lastInvalidationPitchDegrees = float.NaN;
    private Vector2 _lastInvalidationMovementDirection = Vector2.Zero;
    private VisibilityCacheBucket _visibilityCacheBucket = new(int.MinValue, int.MinValue, int.MinValue);
    private bool _terrainDesirabilityDirty;
    private bool _priorityMetadataCacheUsesStartupSnapshot;
    private double _lastSearchInvalidationAtSeconds = double.NegativeInfinity;

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
    private int _lastMeshWorkerBuildCount;
    private double _lastMeshWorkerBuildMs;
    private int _lastDeferredDetailPromotionCount;
    private int _lastDeferredPromotionReevaluationCount;
    private int _lastAvoidedDeferredReevaluationCount;
    private int _lastSuppressedDeferredLogRepeatCount;
    private int _lastRequestsReactivatedByMeshCompletionCount;
    private int _lastRequestsReactivatedByCooldownExpiryCount;
    private int _lastRequestsReactivatedByPressureExitCount;
    private int _lastCoalescedRebuildRequestCount;
    private int _lastSkippedLowPriorityBuildCount;
    private int _lastSuppressedDuplicateBuildCount;
    private int _lastHighPriorityEnqueueBudgetHitCount;
    private int _lastDeferredHighPriorityEnqueueCount;
    private int _lastSmoothedHighPriorityEnqueueCount;
    private int _lastPreventedCoverageGapReleaseCount;
    private int _lastReplacementCoverageWaitCount;
    private int _lastChunksHeldForCoverageSafetyCount;
    private int _lastNormalDebugMismatchCount;
    private int _lastTangentGenerationCount;
    private int _lastVertexTintEnabledFrameCount;
    private long _totalDeferredDetailPromotionCount;
    private long _totalDeferredPromotionReevaluationCount;
    private long _totalAvoidedDeferredReevaluationCount;
    private long _totalSuppressedDeferredLogRepeatCount;
    private long _requestsReactivatedByMeshCompletionCount;
    private long _requestsReactivatedByCooldownExpiryCount;
    private long _requestsReactivatedByPressureExitCount;
    private long _totalCoalescedRebuildRequestCount;
    private long _totalHighPriorityEnqueueBudgetHitCount;
    private long _totalDeferredHighPriorityEnqueueCount;
    private long _totalSmoothedHighPriorityEnqueueCount;
    private long _totalPreventedCoverageGapReleaseCount;
    private long _totalReplacementCoverageWaitCount;
    private long _totalChunksHeldForCoverageSafetyCount;
    private long _totalNormalDebugMismatchCount;
    private long _totalTangentGenerationCount;
    private long _totalVertexTintEnabledFrameCount;
    private long _residentReuseHits;
    private bool _initialLoadComplete;
    private bool _useStartupSnapshot;
    private bool _pressureModeActive;
    private bool _startupPriorityDetailDeferralActive;
    private long _pressureModeActiveFrameCount;
    private int _pressureModeActivationCount;
    private string _lastSelectedChunkSummary = "selected: n/a";
    private string _lastReleasedChunkSummary = "released: n/a";
    private string _lastChunkSourceSummary = "source: n/a";
    private SearchEvaluationContext _searchEvaluationContext;
    private int _rebuildPrioritySequence;
    private int _stagedVisualEnqueueSequence;
    private TerrainVisualDebugMode _activeTerrainDebugView = TerrainVisualDebugMode.Lit;
    private bool _activeBiomeDebugTintEnabled;

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
        _terrainStats = new TerrainStatsTracker(EnableTerrainInstrumentation);
        _biomeClassifier = new TerrainBiomeClassifier(Seed);
        _structureSource = new TerrainStructureSource(Seed, _settings);
        _prioritySampler = new VoxelFieldGenerator(
            Seed,
            TerrainHeight,
            DetailHeight,
            CaveScale,
            CaveThreshold,
            WaterLevel,
            ShorelineFalloff,
            WaterBasinInfluence);
        _chunkStore = new TerrainChunkStore(Seed);
        _cacheManager = new TerrainCacheManager(_chunkStore, _terrainStats);
        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;
        _meshBuildOptions = BuildCurrentMeshBuildOptions(ResolveTerrainDebugView());
        _activeTerrainDebugView = ResolveTerrainDebugView();
        _activeBiomeDebugTintEnabled = EnableBiomeDebugTint;
        _meshBackend = CreateMeshBackend();

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
        RefreshMeshBuildOptionsIfNeeded();
        ProcessCompletedChunkLoads();
        RefreshStreamingState(forceInvalidate: false);
        ProcessPreparedChunkReleases();
        ProcessQueuedChunkLoads();
        ProcessPendingChunkActivations();
        EvaluateResidentDetailRequests();
        ProcessDirtyChunks();
        _cacheManager.MaintainCapacity(MaxLoadedChunks, _residentChunks.Count, _loadScheduler.PreparedCount);
    }

    private void RefreshMeshBuildOptionsIfNeeded()
    {
        TerrainVisualDebugMode debugView = ResolveTerrainDebugView();
        VoxelMeshBuildOptions nextOptions = BuildCurrentMeshBuildOptions(debugView);
        bool meshRebuildRequired = !_meshBuildOptions.Equals(nextOptions) || _activeTerrainDebugView != debugView;
        bool biomeDebugTintChanged = _activeBiomeDebugTintEnabled != EnableBiomeDebugTint;
        if (!meshRebuildRequired && !biomeDebugTintChanged)
        {
            if (nextOptions.ColorMode == VoxelMeshColorMode.MaterialTint)
            {
                _lastVertexTintEnabledFrameCount = 1;
                _totalVertexTintEnabledFrameCount++;
            }

            return;
        }

        _meshBuildOptions = nextOptions;
        _activeTerrainDebugView = debugView;
        _activeBiomeDebugTintEnabled = EnableBiomeDebugTint;
        if (nextOptions.ColorMode == VoxelMeshColorMode.MaterialTint)
        {
            _lastVertexTintEnabledFrameCount = 1;
            _totalVertexTintEnabledFrameCount++;
        }

        foreach (TerrainChunk chunk in _residentChunks.Values)
        {
            if (!IsInstanceValid(chunk))
            {
                continue;
            }

            ApplyChunkVisualConfiguration(chunk);
            chunk.SetBiomeSample(chunk.BiomeSample, EnableBiomeDebugTint);
            if (!chunk.HasData)
            {
                continue;
            }

            if (!meshRebuildRequired)
            {
                continue;
            }

            chunk.MarkDirty(includeCollision: false, collisionDelaySeconds: 0.0);
            _dirtyRenderChunks.Add(chunk);
        }
    }

    private TerrainVisualDebugMode ResolveTerrainDebugView()
    {
        return OS.IsDebugBuild()
            ? TerrainDebugView
            : TerrainVisualDebugMode.Lit;
    }

    private VoxelMeshBuildOptions BuildCurrentMeshBuildOptions(TerrainVisualDebugMode debugView)
    {
        VoxelMeshColorMode colorMode = debugView switch
        {
            TerrainVisualDebugMode.VertexTint => VoxelMeshColorMode.MaterialTint,
            TerrainVisualDebugMode.Normals => VoxelMeshColorMode.NormalDebug,
            _ => VoxelMeshColorMode.MaterialTint
        };

        bool generateTangents =
            TerrainGenerateTangents &&
            debugView == TerrainVisualDebugMode.Lit &&
            TerrainMaterialRequiresTangents();
        return new VoxelMeshBuildOptions(generateTangents, colorMode);
    }

    private static bool TerrainMaterialRequiresTangents()
    {
        // The gameplay terrain material on this branch does not bind a normal map,
        // so tangents should stay disabled unless that changes.
        return false;
    }

    private void ApplyChunkVisualConfiguration(TerrainChunk chunk)
    {
        if (chunk == null || !IsInstanceValid(chunk))
        {
            return;
        }

        chunk.SetVisualConfiguration(EnableTerrainVertexTint, _activeTerrainDebugView);
    }

    private sealed class StagedVisualEnqueueState
    {
        public StagedVisualEnqueueState(TerrainVisualBuildRequest request, int token)
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

    public void ApplyBrush(Vector3 worldCenter, bool additive)
    {
        float strength = additive ? BuildStrength : CarveStrength;
        VoxelSphereEdit edit = new(
            worldCenter,
            BrushRadius,
            strength,
            0.0f,
            BrushRetextureMargin);
        ApplyDeform(
            additive ? "build_brush" : "carve_brush",
            worldCenter,
            BrushRadius,
            BrushRadius + Mathf.Max(BrushRetextureMargin, VoxelSize),
            strength,
            chunk => chunk.ApplySphereBrush(edit, ResolveEditedMaterial));
    }

    public void ApplySlash(VoxelSlashEdit edit)
    {
        ApplyDeform(
            "slash",
            edit.Center,
            edit.BoundingRadius,
            edit.BoundingRadius + Mathf.Max(edit.RetextureMargin, VoxelSize),
            edit.DensityDelta,
            chunk => chunk.ApplySlashBrush(edit, ResolveEditedMaterial));
    }

    private void ApplyDeform(
        string operation,
        Vector3 center,
        float boundsRadius,
        float dirtyBoundsRadius,
        float strength,
        System.Func<TerrainChunk, VoxelEditStats> applyEdit)
    {
        Stopwatch deformStopwatch = null;
        if (_terrainStats.Enabled)
        {
            _terrainStats.LogDeformBegin(operation, center, boundsRadius, strength);
            deformStopwatch = Stopwatch.StartNew();
        }

        int editedChunkCount = 0;
        int editedSampleCount = 0;
        double dirtyBoundsVolume = 0.0;
        int detailPromotionCount = 0;
        foreach (Vector3I key in GetChunkKeysIntersectingSphere(center, boundsRadius))
        {
            TerrainChunk chunk = GetOrCreateChunkForEdit(key);
            if (!chunk.IntersectsSphere(center, boundsRadius))
            {
                continue;
            }

            bool hasDetailBounds = chunk.TryGetLocalBoundsForSphere(center, dirtyBoundsRadius, out Aabb detailBounds);
            bool detailPromoted = false;
            Aabb remeshBounds = detailBounds;
            if (hasDetailBounds)
            {
                detailPromoted = chunk.EnsureDetailBrick(
                    detailBounds,
                    detailLevel: 2,
                    SampleTerrainDensity,
                    ResolveEditedMaterial,
                    persistentEdits: true,
                    preserveExistingCoverage: true);
                if (chunk.TryGetEditedDetailLocalBounds(out Aabb editedDetailBounds))
                {
                    RequestDetailOnChunk(
                        chunk,
                        editedDetailBounds,
                        2,
                        TerrainDetailRegionSource.Edit,
                        TerrainChunk.EditedDetailRegionReason,
                        priority: 100.0f,
                        sticky: true,
                        requestId: TerrainChunk.EditedDetailRegionRequestId);

                    if (detailPromoted)
                    {
                        remeshBounds = editedDetailBounds;
                    }
                }
            }

            VoxelEditStats editStats = applyEdit(chunk);
            if (!editStats.Modified)
            {
                continue;
            }

            if (hasDetailBounds)
            {
                chunk.MarkDirtyBounds(remeshBounds, includeCollision: true, CollisionRebuildDelaySeconds);
                dirtyBoundsVolume += remeshBounds.Size.X * remeshBounds.Size.Y * remeshBounds.Size.Z;
                if (detailPromoted)
                {
                    detailPromotionCount++;
                }

                _terrainStats.LogChunkDirtyBounds(chunk.ChunkKey, operation, remeshBounds, chunk.RenderDirtyBounds, detailPromoted);
            }
            else
            {
                chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
            }

            QueueChunkForRebuild(chunk, TerrainVisualBuildRequestKind.Edit, TerrainMeshDetailMode.IncludeTransientDetail, operation);
            RecordTerrainEditForSearch(key);
            editedChunkCount++;
            editedSampleCount += editStats.TotalSamplesTouched;
        }

        _terrainStats.RecordDeform(
            operation,
            deformStopwatch?.Elapsed.TotalMilliseconds ?? 0.0,
            editedChunkCount,
            editedSampleCount,
            dirtyBoundsVolume,
            detailPromotionCount);
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
        ResetSearchEvaluationCaches();
    }

    public void ClearAllPersistentCache()
    {
        _chunkStore?.ClearAllChunkData();
        _cacheManager?.ClearPersistedKnowledge();
        _startupLoadedChunks.Clear();
        _cacheManager?.ClearStartupSnapshotKnowledge();
        _cacheManager?.SetStartupSnapshotKeys(_startupLoadedChunks);
        _useStartupSnapshot = false;
        ResetSearchEvaluationCaches();
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
            $"Release: {snapshot.LastChunkReleaseCount} ({snapshot.LastChunkReleaseMs:0.00} ms) | worker {snapshot.LastMeshWorkerBuildCount} ({snapshot.LastMeshWorkerBuildMs:0.00} ms) | commit {snapshot.LastVisualRebuildCount} ({snapshot.LastVisualRebuildMs:0.00} ms) | collision {snapshot.LastCollisionRebuildCount} ({snapshot.LastCollisionRebuildMs:0.00} ms)\n" +
            $"Rebuild queue: backend {snapshot.MeshBackendName} | build {snapshot.PendingMeshBuildCount}/{snapshot.DeferredMeshBuildCount}/{snapshot.RunningMeshBuildCount} q/defer/run | hi/lo depth {snapshot.HighPriorityMeshQueueDepth}/{snapshot.LowPriorityMeshQueueDepth} | commit {snapshot.PendingMeshCommitCount} pending | wait {snapshot.LastMeshWorkerQueueWaitMs:0.00}/{snapshot.AverageMeshWorkerQueueWaitMs:0.00}/{snapshot.PeakMeshWorkerQueueWaitMs:0.00} ms last/avg/peak | hi-enqueue budget/defer/smoothed {snapshot.LastHighPriorityEnqueueBudgetHitCount}/{snapshot.LastDeferredHighPriorityEnqueueCount}/{snapshot.LastSmoothedHighPriorityEnqueueCount} total {snapshot.HighPriorityEnqueueBudgetHitCount}/{snapshot.DeferredHighPriorityEnqueueCount}/{snapshot.SmoothedHighPriorityEnqueueCount} | low-pri defer {snapshot.LowPriorityDeferredMeshBuildCount} skip {snapshot.LastSkippedLowPriorityMeshBuildCount}/{snapshot.SkippedLowPriorityMeshBuildCount} | suppress {snapshot.LastSuppressedDuplicateMeshBuildCount}/{snapshot.SuppressedDuplicateMeshBuildCount} | pressure {(snapshot.PressureModeActive ? "on" : "off")} {snapshot.PressureModeActiveFrameCount}/{snapshot.PressureModeActivationCount} | detail defer {snapshot.LastDeferredDetailPromotionCount}/{snapshot.DeferredDetailPromotionCount} reeval {snapshot.LastDeferredPromotionReevaluationCount}/{snapshot.DeferredPromotionReevaluationCount} avoid {snapshot.LastAvoidedDeferredReevaluationCount}/{snapshot.AvoidedDeferredReevaluationCount} log-suppress {snapshot.LastSuppressedDeferredLogRepeatCount}/{snapshot.SuppressedDeferredLogRepeatCount} | react mesh/cool/pressure {snapshot.LastRequestsReactivatedByMeshCompletionCount}/{snapshot.LastRequestsReactivatedByCooldownExpiryCount}/{snapshot.LastRequestsReactivatedByPressureExitCount} | coalesce {snapshot.LastCoalescedRebuildRequestCount}/{snapshot.CoalescedRebuildRequestCount}\n" +
            $"Biome: tracked {snapshot.TrackedBiomeId} | {snapshot.TrackedBiomeSummary}\n" +
            $"Structure: tracked {snapshot.TrackedStructureCount} {snapshot.TrackedStructureType} detail {(snapshot.TrackedStructureRequestsHigherDetail ? "high" : "normal")} | {snapshot.TrackedStructureSummary}\n" +
            $"Detail: tracked {snapshot.TrackedDetailRegionCount} max {snapshot.TrackedMaxDetailLevel} dirty {snapshot.TrackedDirtyDetailRegionCount} | {snapshot.TrackedDetailSummary} | promo {snapshot.TrackedDetailPromotionStateSummary} | coverage {snapshot.TrackedCoverageStateSummary}\n" +
            $"Detail hi: tracked {(snapshot.TrackedDetailBrickActive ? "on" : "off")} tris {snapshot.TrackedDetailBrickTriangleCount} replace {snapshot.TrackedDetailBrickReplaceCoarseCellCount} | {snapshot.TrackedDetailBrickSummary}\n" +
            $"Edit hi: tracked {(snapshot.TrackedEditedDetailActive ? "on" : "off")} tris {snapshot.TrackedEditedDetailTriangleCount} replace {snapshot.TrackedEditedReplaceCoarseCellCount} | {snapshot.TrackedEditedDetailSummary}\n" +
            $"Dirty bounds: render {snapshot.TrackedRenderDirtyBoundsSummary} | collision {snapshot.TrackedCollisionDirtyBoundsSummary}\n" +
            $"Coverage: hold {snapshot.LastChunksHeldForCoverageSafetyCount}/{snapshot.ChunksHeldForCoverageSafetyCount} wait {snapshot.LastReplacementCoverageWaitCount}/{snapshot.ReplacementCoverageWaitCount} prevent {snapshot.LastPreventedCoverageGapReleaseCount}/{snapshot.PreventedCoverageGapReleaseCount}\n" +
            $"Deform: ops {snapshot.DeformOperationCount} | last {snapshot.LastDeformKind} {snapshot.LastDeformMs:0.00} ms | chunks {snapshot.LastDeformEditedChunkCount}/{ComputeAverage(snapshot.TotalEditedChunkCount, snapshot.DeformOperationCount):0.0} avg | visible {snapshot.LastDeformVisibleBlockCount}/{snapshot.LastDeformVisibleFinestBlockCount} finest | queue {snapshot.LastDeformRequeuedBlockCount}/{snapshot.LastDeformQueuedVisibleBlockCount} hidden/visible | reg/enqueue/sync {snapshot.LastDeformRegistrationMs:0.00}/{snapshot.LastDeformEnqueueMs:0.00}/{snapshot.LastDeformSyncWorkMs:0.00} ms | async/apply/collision {snapshot.LastDeformAsyncRebuildMs:0.00}/{snapshot.LastDeformVisualApplyMs:0.00}/{snapshot.LastDeformCollisionRebuildMs:0.00} ms | commit/seam/converge {snapshot.LastDeformVisibleCommitCount}/{snapshot.LastDeformSeamRefreshCount}/{snapshot.LastDeformVisibleConvergenceMs:0.00} ms | samples {snapshot.LastDeformEditedSampleCount}/{ComputeAverage(snapshot.TotalEditedSampleCount, snapshot.DeformOperationCount):0.0} avg | dirty {snapshot.LastDeformDirtyBoundsVolume:0.0}/{ComputeAverage(snapshot.TotalEditedDirtyBoundsVolume, snapshot.DeformOperationCount):0.0} avg | promotions {snapshot.LastDeformEditDetailPromotionCount}/{ComputeAverage(snapshot.EditDetailPromotionCount, snapshot.DeformOperationCount):0.0} avg\n" +
            $"Terrain stats: worker {snapshot.MeshBuildWorkerCount} ({snapshot.MeshBuildWorkerMs:0.00} ms) | commit {snapshot.MeshRebuildCount} ({snapshot.MeshRebuildMs:0.00} ms) | collision {snapshot.CollisionRebuildCount} ({snapshot.CollisionRebuildMs:0.00} ms) | heap coarse/detail avg {snapshot.AverageCoarseMeshWorkerHeapDeltaKiB:0.0}/{snapshot.AverageDetailMeshWorkerHeapDeltaKiB:0.0} KiB max {snapshot.PeakCoarseMeshWorkerHeapDeltaKiB:0.0}/{snapshot.PeakDetailMeshWorkerHeapDeltaKiB:0.0} KiB | normal mismatch {snapshot.LastNormalDebugMismatchCount}/{snapshot.NormalDebugMismatchCount} | tangents {snapshot.LastTangentGenerationCount}/{snapshot.TangentGenerationCount} | tint frames {snapshot.LastVertexTintEnabledFrameCount}/{snapshot.VertexTintEnabledFrameCount} | persist load {snapshot.PersistenceLoadCount} ({snapshot.PersistenceLoadMs:0.00} ms) | save {snapshot.PersistenceSaveCount} ({snapshot.PersistenceSaveMs:0.00} ms)\n" +
            $"Cache: ram {snapshot.RamCacheHits} | startup {snapshot.StartupSnapshotHits} | db {snapshot.DatabaseHits} | gen {snapshot.GenerationFallbacks} | evicted {snapshot.EvictedChunks} | writes {snapshot.DirtyPersistWrites} | startup->db {snapshot.StartupPromotionWrites}\n" +
            $"Selected: {snapshot.LastSelectedChunkSummary}\n" +
            $"Released: {snapshot.LastReleasedChunkSummary}\n" +
            $"Source: {snapshot.LastChunkSourceSummary}";
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        TerrainTelemetryModeSnapshot telemetryMode = TerrainTelemetry.GetModeSnapshot();
        TerrainInstrumentationSnapshot terrainInstrumentation = _terrainStats == null
            ? TerrainInstrumentationSnapshot.Empty
            : _terrainStats.GetSnapshot();
        Vector3 trackedPosition = _trackedCharacter?.GlobalPosition ?? Vector3.Zero;
        Vector3I trackedChunkKey = GetChunkKeyAtWorldPosition(trackedPosition);
        TerrainBiomeSample trackedBiome = _trackedCharacter == null
            ? TerrainBiomeSample.Default
            : GetBiomeAtWorldPosition(trackedPosition);
        TerrainChunkStructureMetadata trackedStructure = GetStructureInfluenceForChunk(trackedChunkKey);
        int trackedDetailRegionCount = 0;
        int trackedDirtyDetailRegionCount = 0;
        int trackedMaxDetailLevel = 0;
        string trackedDetailSourceSummary = "none";
        string trackedDetailSummary = "none";
        string trackedDetailPromotionStateSummary = "n/a";
        bool trackedDetailBrickActive = false;
        string trackedDetailBrickSummary = "none";
        int trackedDetailBrickTriangleCount = 0;
        int trackedDetailBrickReplaceCoarseCellCount = 0;
        bool trackedEditedDetailActive = false;
        string trackedEditedDetailSummary = "none";
        int trackedEditedDetailTriangleCount = 0;
        int trackedEditedReplaceCoarseCellCount = 0;
        string trackedRenderDirtyBoundsSummary = "none";
        string trackedCollisionDirtyBoundsSummary = "none";
        string trackedCoverageStateSummary = "n/a";
        if (_residentChunks.TryGetValue(trackedChunkKey, out TerrainChunk trackedChunk))
        {
            trackedDetailRegionCount = trackedChunk.DetailRegionCount;
            trackedDirtyDetailRegionCount = trackedChunk.DirtyDetailRegionCount;
            trackedMaxDetailLevel = trackedChunk.MaxRequestedDetailLevel;
            trackedDetailSourceSummary = trackedChunk.DetailRegionSourceSummary;
            trackedDetailSummary = trackedChunk.DetailRegionSummary;
            trackedDetailPromotionStateSummary = trackedChunk.DetailPromotionStateSummary;
            trackedDetailBrickActive = trackedChunk.HasDetailBrick;
            trackedDetailBrickSummary = trackedChunk.DetailBrickSummary;
            trackedDetailBrickTriangleCount = trackedChunk.LastDetailTriangleCount;
            trackedDetailBrickReplaceCoarseCellCount = trackedChunk.LastReplacedCoarseCellCount;
            trackedEditedDetailActive = trackedChunk.HasEditedDetailBrick;
            trackedEditedDetailSummary = trackedChunk.HasEditedDetailBrick
                ? trackedChunk.EditedDetailBrickSummary
                : "none";
            trackedEditedDetailTriangleCount = trackedChunk.HasEditedDetailBrick
                ? trackedChunk.LastDetailTriangleCount
                : 0;
            trackedEditedReplaceCoarseCellCount = trackedChunk.HasEditedDetailBrick
                ? trackedChunk.LastReplacedCoarseCellCount
                : 0;
            trackedRenderDirtyBoundsSummary = trackedChunk.RenderDirtyBoundsSummary;
            trackedCollisionDirtyBoundsSummary = trackedChunk.CollisionDirtyBoundsSummary;
            trackedCoverageStateSummary = trackedChunk.CoverageStateSummary;
        }
        else if (trackedStructure.IsInInfluenceZone)
        {
            trackedDetailRegionCount = trackedStructure.StructureCount;
            trackedMaxDetailLevel = trackedStructure.RequestHigherTerrainDetail ? 2 : 1;
            trackedDetailSourceSummary = TerrainDetailRegionSource.Structure.ToString();
            trackedDetailSummary =
                $"{trackedDetailRegionCount} regions max {trackedMaxDetailLevel} dirty 0 src {trackedDetailSourceSummary} preview {trackedStructure.DominantStructureId}";
            trackedDetailPromotionStateSummary = "structure_preview";
        }

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
            TelemetryMode = telemetryMode.ModeLabel,
            CaptureSessionActive = telemetryMode.CaptureSessionActive,
            CaptureIntervalSeconds = telemetryMode.CaptureIntervalSeconds,
            ExpensiveMetricsEnabled = telemetryMode.ExpensiveMetricsEnabled,
            LodTransitionTraceEnabled = telemetryMode.LodTransitionProbeEnabled,
            GrassTraceEnabled = telemetryMode.GrassProbeEnabled,
            DeformTraceEnabled = telemetryMode.DeformProbeEnabled,
            PersistenceTraceEnabled = telemetryMode.PersistenceProbeEnabled,
            TerrainShapeTraceEnabled = telemetryMode.TerrainShapeProbeEnabled,
            TerrainStatsEnabled = terrainInstrumentation.Enabled,
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
            PendingMeshBuildCount = _meshBuildScheduler.QueuedCount,
            DeferredMeshBuildCount = _meshBuildScheduler.DeferredCount,
            RunningMeshBuildCount = _meshBuildScheduler.RunningCount,
            PendingMeshCommitCount = _pendingMeshCommits.Count,
            LastChunkLoadMs = _lastChunkLoadMs,
            LastChunkActivationMs = _lastChunkActivationMs,
            LastChunkReleaseMs = _lastChunkReleaseMs,
            LastVisualRebuildMs = _lastVisualRebuildMs,
            LastCollisionRebuildMs = _lastCollisionRebuildMs,
            LastMeshWorkerBuildCount = _lastMeshWorkerBuildCount,
            LastMeshWorkerBuildMs = _lastMeshWorkerBuildMs,
            MeshWorkerQueueWaitMs = _meshBuildScheduler.TotalQueueWaitMs,
            LastMeshWorkerQueueWaitMs = _meshBuildScheduler.LastQueueWaitMs,
            AverageMeshWorkerQueueWaitMs = _meshBuildScheduler.AverageQueueWaitMs,
            PeakMeshWorkerQueueWaitMs = _meshBuildScheduler.PeakQueueWaitMs,
            HighPriorityMeshQueueDepth = _meshBuildScheduler.HighPriorityQueueDepth,
            LowPriorityMeshQueueDepth = _meshBuildScheduler.LowPriorityQueueDepth,
            LowPriorityDeferredMeshBuildCount = _meshBuildScheduler.LowPriorityDeferredCount,
            SkippedLowPriorityMeshBuildCount = _meshBuildScheduler.SkippedLowPriorityCount,
            LastSkippedLowPriorityMeshBuildCount = _lastSkippedLowPriorityBuildCount,
            SuppressedDuplicateMeshBuildCount = _meshBuildScheduler.SuppressedDuplicateCount,
            LastSuppressedDuplicateMeshBuildCount = _lastSuppressedDuplicateBuildCount,
            HighPriorityEnqueueBudgetHitCount = _totalHighPriorityEnqueueBudgetHitCount,
            LastHighPriorityEnqueueBudgetHitCount = _lastHighPriorityEnqueueBudgetHitCount,
            DeferredHighPriorityEnqueueCount = _totalDeferredHighPriorityEnqueueCount,
            LastDeferredHighPriorityEnqueueCount = _lastDeferredHighPriorityEnqueueCount,
            SmoothedHighPriorityEnqueueCount = _totalSmoothedHighPriorityEnqueueCount,
            LastSmoothedHighPriorityEnqueueCount = _lastSmoothedHighPriorityEnqueueCount,
            PressureModeActive = _pressureModeActive,
            PressureModeActiveFrameCount = _pressureModeActiveFrameCount,
            PressureModeActivationCount = _pressureModeActivationCount,
            LastDeferredDetailPromotionCount = _lastDeferredDetailPromotionCount,
            LastDeferredPromotionReevaluationCount = _lastDeferredPromotionReevaluationCount,
            LastAvoidedDeferredReevaluationCount = _lastAvoidedDeferredReevaluationCount,
            LastSuppressedDeferredLogRepeatCount = _lastSuppressedDeferredLogRepeatCount,
            LastRequestsReactivatedByMeshCompletionCount = _lastRequestsReactivatedByMeshCompletionCount,
            LastRequestsReactivatedByCooldownExpiryCount = _lastRequestsReactivatedByCooldownExpiryCount,
            LastRequestsReactivatedByPressureExitCount = _lastRequestsReactivatedByPressureExitCount,
            LastCoalescedRebuildRequestCount = _lastCoalescedRebuildRequestCount,
            MeshBackendName = $"legacy::{_meshBackend?.BackendName ?? "n/a"}",
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
            DeformOperationCount = terrainInstrumentation.DeformOperationCount,
            TotalEditedChunkCount = terrainInstrumentation.TotalEditedChunkCount,
            TotalEditedSampleCount = terrainInstrumentation.TotalEditedSampleCount,
            TotalEditedDirtyBoundsVolume = terrainInstrumentation.TotalEditedDirtyBoundsVolume,
            EditDetailPromotionCount = terrainInstrumentation.EditDetailPromotionCount,
            LastDeformEditedChunkCount = terrainInstrumentation.LastDeformEditedChunkCount,
            LastDeformEditedSampleCount = terrainInstrumentation.LastDeformEditedSampleCount,
            LastDeformDirtyBoundsVolume = terrainInstrumentation.LastDeformDirtyBoundsVolume,
            LastDeformEditDetailPromotionCount = terrainInstrumentation.LastDeformEditDetailPromotionCount,
            LastDeformMs = terrainInstrumentation.LastDeformMs,
            LastDeformKind = terrainInstrumentation.LastDeformKind,
            MeshBuildWorkerCount = terrainInstrumentation.MeshBuildWorkerCount,
            MeshBuildWorkerMs = terrainInstrumentation.MeshBuildWorkerMs,
            LastMeshBuildWorkerMs = terrainInstrumentation.LastMeshBuildWorkerMs,
            AverageCoarseMeshWorkerHeapDeltaKiB = terrainInstrumentation.AverageCoarseMeshWorkerHeapDeltaKiB,
            PeakCoarseMeshWorkerHeapDeltaKiB = terrainInstrumentation.PeakCoarseMeshWorkerHeapDeltaKiB,
            AverageDetailMeshWorkerHeapDeltaKiB = terrainInstrumentation.AverageDetailMeshWorkerHeapDeltaKiB,
            PeakDetailMeshWorkerHeapDeltaKiB = terrainInstrumentation.PeakDetailMeshWorkerHeapDeltaKiB,
            MeshRebuildCount = terrainInstrumentation.MeshRebuildCount,
            MeshRebuildMs = terrainInstrumentation.MeshRebuildMs,
            LastMeshRebuildMs = terrainInstrumentation.LastMeshRebuildMs,
            CollisionRebuildCount = terrainInstrumentation.CollisionRebuildCount,
            CollisionRebuildMs = terrainInstrumentation.CollisionRebuildMs,
            LastCollisionChunkRebuildMs = terrainInstrumentation.LastCollisionRebuildMs,
            DeferredDetailPromotionCount = _totalDeferredDetailPromotionCount,
            DeferredPromotionReevaluationCount = _totalDeferredPromotionReevaluationCount,
            AvoidedDeferredReevaluationCount = _totalAvoidedDeferredReevaluationCount,
            SuppressedDeferredLogRepeatCount = _totalSuppressedDeferredLogRepeatCount,
            RequestsReactivatedByMeshCompletionCount = _requestsReactivatedByMeshCompletionCount,
            RequestsReactivatedByCooldownExpiryCount = _requestsReactivatedByCooldownExpiryCount,
            RequestsReactivatedByPressureExitCount = _requestsReactivatedByPressureExitCount,
            CoalescedRebuildRequestCount = terrainInstrumentation.CoalescedRebuildRequestCount,
            PreventedCoverageGapReleaseCount = _totalPreventedCoverageGapReleaseCount,
            LastPreventedCoverageGapReleaseCount = _lastPreventedCoverageGapReleaseCount,
            ReplacementCoverageWaitCount = _totalReplacementCoverageWaitCount,
            LastReplacementCoverageWaitCount = _lastReplacementCoverageWaitCount,
            ChunksHeldForCoverageSafetyCount = _totalChunksHeldForCoverageSafetyCount,
            LastChunksHeldForCoverageSafetyCount = _lastChunksHeldForCoverageSafetyCount,
            NormalDebugMismatchCount = _totalNormalDebugMismatchCount,
            LastNormalDebugMismatchCount = _lastNormalDebugMismatchCount,
            TangentGenerationCount = _totalTangentGenerationCount,
            LastTangentGenerationCount = _lastTangentGenerationCount,
            VertexTintEnabledFrameCount = _totalVertexTintEnabledFrameCount,
            LastVertexTintEnabledFrameCount = _lastVertexTintEnabledFrameCount,
            PersistenceLoadCount = terrainInstrumentation.PersistenceLoadCount,
            PersistenceLoadMs = terrainInstrumentation.PersistenceLoadMs,
            LastPersistenceLoadMs = terrainInstrumentation.LastPersistenceLoadMs,
            LastPersistenceLoadScope = terrainInstrumentation.LastPersistenceLoadScope,
            PersistenceSaveCount = terrainInstrumentation.PersistenceSaveCount,
            PersistenceSaveMs = terrainInstrumentation.PersistenceSaveMs,
            LastPersistenceSaveMs = terrainInstrumentation.LastPersistenceSaveMs,
            LastPersistenceSaveScope = terrainInstrumentation.LastPersistenceSaveScope,
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
            SearchThrottleState = "retired",
            SearchInvalidationReason = _desiredSetBuilder.LastInvalidationReason,
            TrackedBiomeId = trackedBiome.DominantBiome,
            TrackedBiomeSummary = trackedBiome.Summary,
            TrackedStructureCount = trackedStructure.StructureCount,
            TrackedStructureType = trackedStructure.DominantStructureType,
            TrackedStructureRequestsHigherDetail = trackedStructure.RequestHigherTerrainDetail,
            TrackedStructureSummary = trackedStructure.Summary,
            TrackedDetailRegionCount = trackedDetailRegionCount,
            TrackedDirtyDetailRegionCount = trackedDirtyDetailRegionCount,
            TrackedMaxDetailLevel = trackedMaxDetailLevel,
            TrackedDetailSourceSummary = trackedDetailSourceSummary,
            TrackedDetailSummary = trackedDetailSummary,
            TrackedDetailPromotionStateSummary = trackedDetailPromotionStateSummary,
            TrackedDetailBrickActive = trackedDetailBrickActive,
            TrackedDetailBrickSummary = trackedDetailBrickSummary,
            TrackedDetailBrickTriangleCount = trackedDetailBrickTriangleCount,
            TrackedDetailBrickReplaceCoarseCellCount = trackedDetailBrickReplaceCoarseCellCount,
            TrackedEditedDetailActive = trackedEditedDetailActive,
            TrackedEditedDetailSummary = trackedEditedDetailSummary,
            TrackedEditedDetailTriangleCount = trackedEditedDetailTriangleCount,
            TrackedEditedReplaceCoarseCellCount = trackedEditedReplaceCoarseCellCount,
            TrackedRenderDirtyBoundsSummary = trackedRenderDirtyBoundsSummary,
            TrackedCollisionDirtyBoundsSummary = trackedCollisionDirtyBoundsSummary,
            TrackedCoverageStateSummary = trackedCoverageStateSummary,
            LastSelectedChunkSummary = _lastSelectedChunkSummary,
            LastReleasedChunkSummary = _lastReleasedChunkSummary,
            LastChunkSourceSummary = _lastChunkSourceSummary,
            InitialLoadProgress = GetInitialLoadProgress(),
            InitialLoadComplete = _initialLoadComplete
        };
    }

    public TerrainBiomeSample GetBiomeForChunk(Vector3I chunkKey)
    {
        if (_residentChunks.TryGetValue(chunkKey, out TerrainChunk residentChunk))
        {
            return residentChunk.BiomeSample;
        }

        return _biomeClassifier.SampleChunk(chunkKey, _settings);
    }

    public TerrainBiomeSample GetBiomeAtWorldPosition(Vector3 worldPosition)
    {
        return _biomeClassifier.SampleWorldPosition(worldPosition);
    }

    public TerrainChunkStructureMetadata GetStructureInfluenceForChunk(Vector3I chunkKey)
    {
        if (_residentChunks.TryGetValue(chunkKey, out TerrainChunk residentChunk))
        {
            return residentChunk.StructureMetadata;
        }

        return _structureSource.GetChunkStructureMetadata(chunkKey);
    }

    public bool IsChunkNearStructure(Vector3I chunkKey)
    {
        return GetStructureInfluenceForChunk(chunkKey).IsInInfluenceZone;
    }

    public bool ShouldPromoteTerrainDetail(Vector3I chunkKey)
    {
        return GetStructureInfluenceForChunk(chunkKey).RequestHigherTerrainDetail;
    }

    public System.Collections.Generic.IReadOnlyList<TerrainStructureInstance> GetOverlappingStructuresForChunk(Vector3I chunkKey)
    {
        return GetStructureInfluenceForChunk(chunkKey).OverlappingStructures;
    }

    public bool TryRequestChunkDetail(
        Vector3I chunkKey,
        Aabb localBounds,
        int detailLevel,
        TerrainDetailRegionSource source,
        string reason,
        string requestId = "")
    {
        if (!_residentChunks.TryGetValue(chunkKey, out TerrainChunk chunk))
        {
            return false;
        }

        return RequestDetailOnChunk(chunk, localBounds, detailLevel, source, reason, priority: 0.0f, sticky: false, requestId: requestId);
    }

    public bool ChunkHasDetailRegions(Vector3I chunkKey)
    {
        return _residentChunks.TryGetValue(chunkKey, out TerrainChunk chunk) && chunk.HasDetailRegions;
    }

    public System.Collections.Generic.IReadOnlyList<TerrainDetailRegion> GetDetailRegionsForChunk(Vector3I chunkKey)
    {
        if (_residentChunks.TryGetValue(chunkKey, out TerrainChunk chunk))
        {
            return chunk.DetailRegionManager.Regions;
        }

        return System.Array.Empty<TerrainDetailRegion>();
    }

    public string GetDetailRegionSummaryForChunk(Vector3I chunkKey)
    {
        if (_residentChunks.TryGetValue(chunkKey, out TerrainChunk chunk))
        {
            return chunk.DetailRegionSummary;
        }

        TerrainChunkStructureMetadata structureMetadata = GetStructureInfluenceForChunk(chunkKey);
        if (!structureMetadata.IsInInfluenceZone)
        {
            return "none";
        }

        int previewLevel = structureMetadata.RequestHigherTerrainDetail ? 2 : 1;
        return
            $"{structureMetadata.StructureCount} regions max {previewLevel} dirty 0 src {TerrainDetailRegionSource.Structure} preview {structureMetadata.DominantStructureId}";
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
            if (_residentChunks.TryGetValue(key, out TerrainChunk chunk) && IsChunkInitialLoadReady(chunk))
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
        double nowSeconds = GetNowSeconds();
        int effectiveSearchRadius = GetEffectiveSearchRadius();
        _searchEvaluationContext = new SearchEvaluationContext(
            sample.CenterChunk,
            sample.StreamForward,
            sample.TrackedPosition,
            sample.CameraPosition,
            effectiveSearchRadius);
        TerrainDesiredSetContext searchContext = new(
            sample.CenterChunk,
            effectiveSearchRadius,
            Mathf.Max(MaxActiveColumns, 1),
            GuaranteedColumnRadius);
        HashSet<Vector2I> residentColumns = GetResidentColumns();

        string invalidationReason = GetSearchInvalidationReason(sample, searchContext, forceInvalidate, nowSeconds);
        bool invalidated = !string.IsNullOrEmpty(invalidationReason);
        RefreshSearchEvaluationCaches(sample.CameraPosition, invalidated);
        if (!string.IsNullOrEmpty(invalidationReason))
        {
            _desiredSetBuilder.Invalidate(invalidationReason, searchContext, residentColumns, EvaluateColumnPriority);
            _lastInvalidationCenterChunk = sample.CenterChunk;
            _lastInvalidationPosition = sample.TrackedPosition;
            _lastInvalidationYawDegrees = sample.YawDegrees;
            _lastInvalidationPitchDegrees = sample.PitchDegrees;
            _lastInvalidationMovementDirection = sample.MovementDirection;
            _lastSearchInvalidationAtSeconds = nowSeconds;
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
                _lastChunkSourceSummary = BuildChunkSourceSummary(key, TerrainChunkLoadSource.Resident);
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

    private string GetSearchInvalidationReason(
        SearchSample sample,
        TerrainDesiredSetContext searchContext,
        bool forceInvalidate,
        double nowSeconds)
    {
        if (forceInvalidate || _lastInvalidationPosition.X == float.MinValue)
        {
            return "startup";
        }

        bool softInvalidationReady = CanTriggerSoftSearchInvalidation(nowSeconds);
        if (sample.CenterChunk != _lastInvalidationCenterChunk)
        {
            return $"entered chunk {sample.CenterChunk}";
        }

        if (softInvalidationReady &&
            Mathf.Abs(DeltaAngleDegrees(sample.YawDegrees, _lastInvalidationYawDegrees)) >= SearchInvalidationYawDegrees)
        {
            return $"yaw {sample.YawDegrees:0.0}";
        }

        if (softInvalidationReady &&
            Mathf.Abs(sample.PitchDegrees - _lastInvalidationPitchDegrees) >= SearchInvalidationPitchDegrees)
        {
            return $"pitch {sample.PitchDegrees:0.0}";
        }

        float movementDistance = sample.TrackedPosition.DistanceTo(_lastInvalidationPosition);
        if (softInvalidationReady &&
            movementDistance >= _settings.ChunkSize * SearchInvalidationMoveDistanceFactor &&
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

        if (_terrainDesirabilityDirty && softInvalidationReady)
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
                Vector3I key = new(column.X, y, column.Y);
                if (ShouldSkipDesiredVerticalChunk(key, out float surfaceMaxY))
                {
                    RecordEmptyVerticalChunkSkip(key, surfaceMaxY);
                    continue;
                }

                _desiredChunks.Add(key);
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
        bool anyPendingDesiredCoverage = HasAnyPendingDesiredCoverage();
        foreach (KeyValuePair<Vector3I, TerrainChunk> entry in _residentChunks)
        {
            TerrainChunk chunk = entry.Value;
            bool desired = _desiredChunks.Contains(entry.Key);
            bool replacementCoveragePending = false;
            string coverageHoldReason = string.Empty;
            bool heldForCoverageSafety = !desired &&
                ShouldHoldChunkForCoverage(
                    chunk,
                    anyPendingDesiredCoverage,
                    out replacementCoveragePending,
                    out coverageHoldReason);
            bool continuityVisible = !desired &&
                !heldForCoverageSafety &&
                ShouldKeepChunkVisibleForContinuity(chunk);
            bool visible = chunk.HasSurface && (desired || heldForCoverageSafety || continuityVisible);
            chunk.SetCoverageRetention(
                heldForCoverageSafety,
                replacementCoveragePending,
                safeToRelease: !desired && !heldForCoverageSafety,
                coverageHoldReason);
            chunk.Visible = visible;
            chunk.ProcessMode = visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            if (heldForCoverageSafety)
            {
                _lastChunksHeldForCoverageSafetyCount++;
                _totalChunksHeldForCoverageSafetyCount++;
                LogCoverageHold(chunk, coverageHoldReason, replacementCoveragePending);
                if (replacementCoveragePending)
                {
                    _lastReplacementCoverageWaitCount++;
                    _totalReplacementCoverageWaitCount++;
                }
            }
            else
            {
                _coverageHoldLogReasons.Remove(chunk.ChunkKey);
                _coverageReleaseBlockLogReasons.Remove(chunk.ChunkKey);
            }
        }
    }

    private bool ShouldHoldChunkForCoverage(
        TerrainChunk chunk,
        bool anyPendingDesiredCoverage,
        out bool replacementCoveragePending,
        out string coverageHoldReason)
    {
        replacementCoveragePending = false;
        coverageHoldReason = string.Empty;
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            !CanChunkProvideCoverage(chunk))
        {
            return false;
        }

        if (!IsChunkCoverageRelevant(chunk))
        {
            return false;
        }

        replacementCoveragePending = HasPendingDesiredCoverageNeighbors(chunk.ChunkKey, out coverageHoldReason);
        if (replacementCoveragePending)
        {
            return true;
        }

        if (anyPendingDesiredCoverage && IsChunkInNearPlayerCoverageSafetyZone(chunk.ChunkKey))
        {
            coverageHoldReason = "near_player_pending";
            return true;
        }

        return false;
    }

    private bool HasPendingDesiredCoverageNeighbors(Vector3I key, out string reason)
    {
        reason = string.Empty;
        for (int z = -1; z <= 1; z++)
        {
            for (int x = -1; x <= 1; x++)
            {
                Vector3I neighborKey = new(key.X + x, key.Y, key.Z + z);
                if (!_desiredChunks.Contains(neighborKey))
                {
                    continue;
                }

                if (!CanVerticalChunkContainSurface(neighborKey))
                {
                    continue;
                }

                if (!_residentChunks.TryGetValue(neighborKey, out TerrainChunk neighborChunk))
                {
                    reason = $"replacement_missing:{neighborKey}";
                    return true;
                }

                if (!neighborChunk.HasCompletedInitialVisualBuild)
                {
                    reason = $"replacement_build_pending:{neighborKey}";
                    return true;
                }

                if (!CanChunkProvideCoverage(neighborChunk))
                {
                    reason = $"replacement_bad_mesh:{neighborKey}:tris={neighborChunk.LastTotalTriangleCount}";
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasAnyPendingDesiredCoverage()
    {
        foreach (Vector3I key in _desiredChunks)
        {
            if (!IsChunkInNearPlayerCoverageSafetyZone(key) || !CanVerticalChunkContainSurface(key))
            {
                continue;
            }

            if (!_residentChunks.TryGetValue(key, out TerrainChunk chunk) || !CanChunkProvideCoverage(chunk))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsChunkInNearPlayerCoverageSafetyZone(Vector3I key)
    {
        Vector2 offset = new(
            key.X - _searchEvaluationContext.CenterChunk.X,
            key.Z - _searchEvaluationContext.CenterChunk.Y);
        return offset.Length() <= GuaranteedColumnRadius + 0.85f;
    }

    private bool IsChunkCoverageRelevant(TerrainChunk chunk)
    {
        if (!CanVerticalChunkContainSurface(chunk.ChunkKey))
        {
            return false;
        }

        Vector2 offset = new(
            chunk.ChunkKey.X - _searchEvaluationContext.CenterChunk.X,
            chunk.ChunkKey.Z - _searchEvaluationContext.CenterChunk.Y);
        float distance = offset.Length();
        if (distance <= GuaranteedColumnRadius + 1.5f)
        {
            return true;
        }

        float forwardAlignment = 0.0f;
        if (_searchEvaluationContext.StreamForward != Vector2.Zero && offset.LengthSquared() > 0.0001f)
        {
            forwardAlignment = offset.Normalized().Dot(_searchEvaluationContext.StreamForward);
        }

        float visibility = MeasureVisibilityHeuristic(new Vector2I(chunk.ChunkKey.X, chunk.ChunkKey.Z));
        return
            distance <= _searchEvaluationContext.SearchRadius + 1.25f &&
            visibility > OccludedPriorityScale &&
            forwardAlignment > -0.55f;
    }

    private bool CanChunkProvideCoverage(TerrainChunk chunk)
    {
        return chunk != null &&
            IsInstanceValid(chunk) &&
            chunk.HasCompletedInitialVisualBuild &&
            chunk.HasSurface &&
            chunk.LastTotalTriangleCount >= MinimumCoverageTriangleCount &&
            CanVerticalChunkContainSurface(chunk.ChunkKey);
    }

    private bool CanVerticalChunkContainSurface(Vector3I key)
    {
        if (_settings == null || _prioritySampler == null)
        {
            return true;
        }

        if (_residentChunks.TryGetValue(key, out TerrainChunk residentChunk) &&
            residentChunk != null &&
            IsInstanceValid(residentChunk) &&
            residentChunk.HasSurface)
        {
            return true;
        }

        float surfaceMaxY = EstimateColumnSurfaceMaxY(new Vector2I(key.X, key.Z));
        float chunkMinY = _settings.GetChunkBounds(key).Position.Y;
        float surfaceMargin = Mathf.Max(VoxelSize * 2.0f, DetailHeight + VoxelSize);
        return chunkMinY <= surfaceMaxY + surfaceMargin;
    }

    private bool ShouldSkipDesiredVerticalChunk(Vector3I key, out float surfaceMaxY)
    {
        surfaceMaxY = float.PositiveInfinity;
        if (IsChunkInNearPlayerCoverageSafetyZone(key) || _settings == null || _prioritySampler == null)
        {
            return false;
        }

        if (_cacheManager != null)
        {
            TerrainChunkLoadSource estimatedSource = _cacheManager.EstimateSource(key, _useStartupSnapshot);
            if (estimatedSource is TerrainChunkLoadSource.StartupSnapshot or TerrainChunkLoadSource.PersistedChunk)
            {
                return false;
            }
        }

        surfaceMaxY = EstimateColumnSurfaceMaxY(new Vector2I(key.X, key.Z));
        float chunkMinY = _settings.GetChunkBounds(key).Position.Y;
        float surfaceMargin = Mathf.Max(VoxelSize * 2.0f, DetailHeight + VoxelSize);
        return chunkMinY > surfaceMaxY + surfaceMargin;
    }

    private float EstimateColumnSurfaceMaxY(Vector2I columnKey)
    {
        if (_columnSurfaceMaxYCache.TryGetValue(columnKey, out float cached))
        {
            return cached;
        }

        if (_settings == null || _prioritySampler == null)
        {
            return float.PositiveInfinity;
        }

        float minX = columnKey.X * _settings.ChunkSize;
        float minZ = columnKey.Y * _settings.ChunkSize;
        float maxSurfaceY = float.NegativeInfinity;
        foreach (Vector2 sample in ColumnSurfaceSamplePattern)
        {
            float sampleX = minX + (sample.X * _settings.ChunkSize);
            float sampleZ = minZ + (sample.Y * _settings.ChunkSize);
            maxSurfaceY = Mathf.Max(maxSurfaceY, _prioritySampler.SampleSurfaceHeight(sampleX, sampleZ));
        }

        _columnSurfaceMaxYCache[columnKey] = maxSurfaceY;
        return maxSurfaceY;
    }

    private void RecordEmptyVerticalChunkSkip(Vector3I key, float surfaceMaxY)
    {
        if (!_loggedEmptyVerticalChunkSkips.Add(key))
        {
            return;
        }

        float chunkMinY = _settings.GetChunkBounds(key).Position.Y;
        _terrainStats.RecordEmptyVerticalChunkSkipped(key, "above_surface_band", surfaceMaxY, chunkMinY);
    }

    private void LogCoverageHold(TerrainChunk chunk, string reason, bool replacementCoveragePending)
    {
        if (chunk == null || !IsInstanceValid(chunk))
        {
            return;
        }

        string effectiveReason = string.IsNullOrWhiteSpace(reason)
            ? chunk.CoverageStateSummary
            : reason;
        if (_coverageHoldLogReasons.TryGetValue(chunk.ChunkKey, out string previousReason) &&
            string.Equals(previousReason, effectiveReason, StringComparison.Ordinal))
        {
            return;
        }

        _coverageHoldLogReasons[chunk.ChunkKey] = effectiveReason;
        _terrainStats.RecordCoverageHold(chunk.ChunkKey, effectiveReason, replacementCoveragePending);
    }

    private void LogCoverageReleaseBlocked(TerrainChunk chunk)
    {
        if (chunk == null || !IsInstanceValid(chunk))
        {
            return;
        }

        string reason = string.IsNullOrWhiteSpace(chunk.CoverageHoldReason)
            ? chunk.CoverageStateSummary
            : chunk.CoverageHoldReason;
        if (_coverageReleaseBlockLogReasons.TryGetValue(chunk.ChunkKey, out string previousReason) &&
            string.Equals(previousReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _coverageReleaseBlockLogReasons[chunk.ChunkKey] = reason;
        _terrainStats.RecordCoverageReleaseBlocked(chunk.ChunkKey, reason);
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
            RegisterChunkLoadStats(result.Key, result.Source, result.LoadMs, "stream");

            if (_loadScheduler.IsTargetKey(result.Key) && _desiredChunks.Contains(result.Key))
            {
                _loadScheduler.RegisterPreparedChunk(result);
                continue;
            }

            _cacheManager.StorePreparedChunk(result.Key, result.Data, result.Source);
            InvalidatePriorityMetadataCaches(result.Key);
        }
    }

    private void RegisterChunkLoadStats(Vector3I key, TerrainChunkLoadSource source, double loadMs, string context)
    {
        _lastChunkLoadCount++;
        _lastChunkLoadMs += loadMs;
        _lastChunkSourceSummary = BuildChunkSourceSummary(key, source);
        _terrainStats.RecordChunkLoadSource(key, source, loadMs, context);

        switch (source)
        {
            case TerrainChunkLoadSource.RamCache:
                _lastRamCacheLoadCount++;
                _lastRamCacheLoadMs += loadMs;
                break;
            case TerrainChunkLoadSource.StartupSnapshot:
                _lastStartupChunkLoadCount++;
                _lastStartupChunkLoadMs += loadMs;
                break;
            case TerrainChunkLoadSource.PersistedChunk:
                _lastPersistedChunkLoadCount++;
                _lastPersistedChunkLoadMs += loadMs;
                break;
            default:
                _lastGeneratedChunkLoadCount++;
                _lastGeneratedChunkLoadMs += loadMs;
                break;
        }
    }

    private void ProcessPreparedChunkReleases()
    {
        foreach (PreparedChunkResult prepared in _loadScheduler.ExtractPreparedOutsideTargets())
        {
            _cacheManager.StorePreparedChunk(prepared.Key, prepared.Data, prepared.Source);
            InvalidatePriorityMetadataCaches(prepared.Key);
        }

        int budget = GetCurrentReleaseBudget();
        for (int i = 0; i < _residencyManager.ToRelease.Count && budget > 0; i++)
        {
            ChunkReleaseInfo release = _residencyManager.ToRelease[i];
            if (!_residentChunks.TryGetValue(release.Key, out TerrainChunk chunk))
            {
                continue;
            }

            if (!chunk.SafeToRelease)
            {
                _lastPreventedCoverageGapReleaseCount++;
                _totalPreventedCoverageGapReleaseCount++;
                LogCoverageReleaseBlocked(chunk);
                continue;
            }

            ulong start = Time.GetTicksUsec();
            _cacheManager.ReleaseResidentChunk(release.Key, chunk);
            InvalidatePriorityMetadataCaches(release.Key);
            if (chunk.IsDetailPromotionDeferred)
            {
                _terrainStats.RecordDetailPromotionEligible(release.Key, "chunk_released");
            }
            _dirtyRenderChunks.Remove(chunk);
            _dirtyCollisionChunks.Remove(chunk);
            _collisionQueueStates.Remove(release.Key);
            _coverageHoldLogReasons.Remove(release.Key);
            _coverageReleaseBlockLogReasons.Remove(release.Key);
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
        VoxelFieldGenerator generator = new(
            Seed,
            TerrainHeight,
            DetailHeight,
            CaveScale,
            CaveThreshold,
            WaterLevel,
            ShorelineFalloff,
            WaterBasinInfluence);
        generator.FillChunk(generated);
        return generated;
    }

    private void ProcessPendingChunkActivations()
    {
        int activationBudget = GetCurrentActivationBudget();
        float activationTimeBudgetMs = GetCurrentActivationTimeBudgetMs();
        ulong budgetStartUsec = Time.GetTicksUsec();
        while (activationBudget > 0)
        {
            if (HasExceededMainThreadBudget(budgetStartUsec, activationTimeBudgetMs) ||
                !_loadScheduler.TryTakeNextActivation(out PreparedChunkResult prepared))
            {
                break;
            }

            if (_residentChunks.ContainsKey(prepared.Key))
            {
                activationBudget--;
                continue;
            }

            ulong attachStart = Time.GetTicksUsec();
            TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
            AddChild(chunk);
            chunk.Initialize(prepared.Key, _settings);
            ApplyChunkVisualConfiguration(chunk);
            chunk.SetBiomeSample(GetBiomeForChunk(prepared.Key), EnableBiomeDebugTint);
            chunk.SetStructureMetadata(GetStructureInfluenceForChunk(prepared.Key));
            chunk.SetData(prepared.Data, prepared.Source);
            chunk.NotifyActivated(Engine.GetProcessFrames(), GetNowSeconds());
            chunk.Visible = _desiredChunks.Contains(prepared.Key);
            chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            _residentChunks[prepared.Key] = chunk;
            InvalidatePriorityMetadataCaches(prepared.Key);
            _lastChunkSourceSummary = BuildChunkSourceSummary(prepared.Key, prepared.Source);
            chunk.MarkDirty(includeCollision: false, collisionDelaySeconds: 0.0);
            QueueChunkForRebuild(chunk, TerrainVisualBuildRequestKind.InitialCoarse, TerrainMeshDetailMode.CoarseOnly, "activate");

            _lastChunkActivationCount++;
            _lastChunkActivationMs += (Time.GetTicksUsec() - attachStart) / 1000.0;
            activationBudget--;
        }
    }

    private void EvaluateResidentDetailRequests()
    {
        if (_residentChunks.Count == 0)
        {
            return;
        }

        if (!EnableLocalDetailRequests)
        {
            ClearAutomaticResidentDetailRequests();
            return;
        }

        bool hasTrackedCharacter = _trackedCharacter != null && IsInstanceValid(_trackedCharacter);
        Vector3 trackedPosition = hasTrackedCharacter
            ? _trackedCharacter.GlobalPosition
            : Vector3.Zero;
        double nowSeconds = GetNowSeconds();
        ulong currentFrame = Engine.GetProcessFrames();
        bool pressureModeActive = IsVisualMeshPressureActive();
        bool startupCoarsePriorityActive = ShouldPrioritizeStartupCoarseShell();
        ReactivateDeferredPressureDetailPromotionsOnExit(pressureModeActive);
        ReactivateDeferredStartupPriorityDetailPromotionsOnExit(startupCoarsePriorityActive);
        int remainingPromotions = Mathf.Max(MaxDetailPromotionsPerFrame, 0);
        List<TerrainChunk> chunks = new(_residentChunks.Values);
        foreach (TerrainChunk chunk in chunks)
        {
            if (!IsInstanceValid(chunk) || !chunk.HasData)
            {
                continue;
            }

            bool requestChanged = RefreshChunkDetailRequests(chunk, trackedPosition, hasTrackedCharacter);
            HandleAutomaticDetailRequestChange(chunk, requestChanged);
            bool hasDetailAggregate = TryBuildDetailAggregate(chunk, out Aabb localBounds, out int detailLevel);
            bool automaticPromotionCandidate = hasDetailAggregate && IsAutomaticDetailPromotionCandidate(chunk, requestChanged);
            if (!automaticPromotionCandidate)
            {
                if (!chunk.IsDetailPromotionMeshBlocked)
                {
                    if (chunk.IsDetailPromotionDeferred)
                    {
                        ClearDeferredDetailPromotionState(chunk, "request_cleared");
                    }

                    if (chunk.DetailPromotionState != TerrainDetailPromotionState.Applied)
                    {
                        chunk.MarkDetailPromotionApplied();
                    }
                }

                continue;
            }

            if (TrySkipBlockedDetailPromotionEvaluation(
                    chunk,
                    currentFrame,
                    nowSeconds,
                    pressureModeActive,
                    startupCoarsePriorityActive))
            {
                continue;
            }

            if (chunk.ConsumeDetailPromotionReevaluationPending(out _))
            {
                RecordDeferredPromotionReevaluation();
            }

            if (TryBuildLowValueDetailPromotionDeferDecision(
                    chunk,
                    currentFrame,
                    pressureModeActive,
                    out TerrainDetailPromotionDeferDecision lowValueDecision))
            {
                ApplyDeferredDetailPromotion(chunk, lowValueDecision);
                continue;
            }

            if (ShouldDeferAutomaticDetailPromotion(
                    chunk,
                    currentFrame,
                    nowSeconds,
                    pressureModeActive,
                    startupCoarsePriorityActive,
                    out TerrainDetailPromotionDeferDecision deferDecision))
            {
                ApplyDeferredDetailPromotion(chunk, deferDecision);
                continue;
            }

            if (remainingPromotions <= 0)
            {
                ApplyDeferredDetailPromotion(
                    chunk,
                    BuildPromotionBudgetDeferDecision(chunk, currentFrame, pressureModeActive));
                continue;
            }

            chunk.SetDetailPromotionEligible();
            TerrainDetailReconcileResult detailResult = ReconcileChunkDetailBrick(chunk, hasDetailAggregate, localBounds, detailLevel);
            if (detailResult.Changed)
            {
                if (detailResult.PromotedTransientDetail)
                {
                    remainingPromotions = Mathf.Max(0, remainingPromotions - 1);
                }

                chunk.MarkDirty(includeCollision: false, CollisionRebuildDelaySeconds);
                chunk.MarkDetailPromotionQueued();
                QueueChunkForRebuild(
                    chunk,
                    TerrainVisualBuildRequestKind.DetailPromotion,
                    TerrainMeshDetailMode.IncludeTransientDetail,
                    detailResult.PromotedTransientDetail ? "detail_promote" : "detail_refresh");
                continue;
            }

            if (requestChanged && !chunk.RenderDirty)
            {
                chunk.ClearDetailRegionDirtyFlags();
            }

            if (!chunk.RenderDirty)
            {
                chunk.MarkDetailPromotionApplied();
            }
        }

        _startupPriorityDetailDeferralActive = startupCoarsePriorityActive;
    }

    private void ClearAutomaticResidentDetailRequests()
    {
        List<TerrainChunk> chunks = new(_residentChunks.Values);
        foreach (TerrainChunk chunk in chunks)
        {
            if (!IsInstanceValid(chunk) || !chunk.HasData)
            {
                continue;
            }

            int removedPlayer = chunk.RemoveDetailRequestsBySource(TerrainDetailRegionSource.PlayerProximity);
            _terrainStats.LogDetailRegionRemoval(chunk.ChunkKey, TerrainDetailRegionSource.PlayerProximity, removedPlayer);
            int removedBiome = chunk.RemoveDetailRequestsBySource(TerrainDetailRegionSource.Biome);
            _terrainStats.LogDetailRegionRemoval(chunk.ChunkKey, TerrainDetailRegionSource.Biome, removedBiome);
            int removedStructure = chunk.RemoveDetailRequestsBySource(TerrainDetailRegionSource.Structure);
            _terrainStats.LogDetailRegionRemoval(chunk.ChunkKey, TerrainDetailRegionSource.Structure, removedStructure);

            bool requestChanged = removedPlayer > 0 || removedBiome > 0 || removedStructure > 0;
            Aabb localBounds = default;
            int detailLevel = 0;
            bool hasDetailAggregate = requestChanged && TryBuildDetailAggregate(chunk, out localBounds, out detailLevel);
            TerrainDetailReconcileResult detailResult = requestChanged
                ? ReconcileChunkDetailBrick(chunk, hasDetailAggregate, localBounds, detailLevel)
                : TerrainDetailReconcileResult.NoChange;
            if (detailResult.Changed)
            {
                chunk.MarkDirty(includeCollision: false, CollisionRebuildDelaySeconds);
                chunk.MarkDetailPromotionQueued();
                QueueChunkForRebuild(chunk, TerrainVisualBuildRequestKind.DetailPromotion, TerrainMeshDetailMode.IncludeTransientDetail, "detail_disable");
                continue;
            }

            if (requestChanged && !chunk.RenderDirty)
            {
                chunk.ClearDetailRegionDirtyFlags();
            }

            if (!chunk.RenderDirty)
            {
                if (chunk.IsDetailPromotionDeferred)
                {
                    ClearDeferredDetailPromotionState(chunk, "detail_requests_disabled");
                }

                chunk.MarkDetailPromotionApplied();
            }
        }
    }

    private void HandleAutomaticDetailRequestChange(TerrainChunk chunk, bool requestChanged)
    {
        if (!requestChanged)
        {
            return;
        }

        switch (chunk.DetailPromotionState)
        {
            case TerrainDetailPromotionState.DeferredPendingMesh:
            case TerrainDetailPromotionState.Queued:
            case TerrainDetailPromotionState.Running:
                chunk.RequestDetailPromotionFollowup();
                break;

            case TerrainDetailPromotionState.DeferredCooldown:
            case TerrainDetailPromotionState.DeferredWarmup:
            case TerrainDetailPromotionState.DeferredPressure:
            case TerrainDetailPromotionState.DeferredPromotionBudget:
            case TerrainDetailPromotionState.DeferredStartupPriority:
                ReactivateDetailPromotion(chunk, "request_changed");
                break;

            case TerrainDetailPromotionState.Applied:
                chunk.SetDetailPromotionEligible();
                break;
        }
    }

    private void ReactivateDeferredPressureDetailPromotionsOnExit(bool pressureModeActive)
    {
        if (!_pressureModeActive || pressureModeActive)
        {
            return;
        }

        foreach (TerrainChunk chunk in _residentChunks.Values)
        {
            if (!IsInstanceValid(chunk) || chunk.DetailPromotionState != TerrainDetailPromotionState.DeferredPressure)
            {
                continue;
            }

            ReactivateDetailPromotion(chunk, "pressure_exit", reactivatedByPressureExit: true);
        }
    }

    private void ReactivateDeferredStartupPriorityDetailPromotionsOnExit(bool startupCoarsePriorityActive)
    {
        if (!_startupPriorityDetailDeferralActive || startupCoarsePriorityActive)
        {
            return;
        }

        foreach (TerrainChunk chunk in _residentChunks.Values)
        {
            if (!IsInstanceValid(chunk) || chunk.DetailPromotionState != TerrainDetailPromotionState.DeferredStartupPriority)
            {
                continue;
            }

            ReactivateDetailPromotion(chunk, "startup_priority_clear");
        }
    }

    private bool TrySkipBlockedDetailPromotionEvaluation(
        TerrainChunk chunk,
        ulong currentFrame,
        double nowSeconds,
        bool pressureModeActive,
        bool startupCoarsePriorityActive)
    {
        switch (chunk.DetailPromotionState)
        {
            case TerrainDetailPromotionState.Queued:
            case TerrainDetailPromotionState.Running:
                RecordAvoidedDeferredReevaluation();
                return true;

            case TerrainDetailPromotionState.DeferredPendingMesh:
                if (_meshBuildScheduler.HasPendingWork(chunk.ChunkKey) || chunk.RenderDirty || !chunk.HasCompletedInitialVisualBuild)
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "mesh_completion", reactivatedByMeshCompletion: true);
                return false;

            case TerrainDetailPromotionState.DeferredWarmup:
                bool frameReady = chunk.DetailPromotionNextEligibleFrame == 0 ||
                                  currentFrame >= chunk.DetailPromotionNextEligibleFrame;
                bool timeReady = chunk.DetailPromotionNextEligibleAtSeconds <= double.NegativeInfinity ||
                                 nowSeconds >= chunk.DetailPromotionNextEligibleAtSeconds;
                if (!frameReady || !timeReady)
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "warmup_expiry");
                return false;

            case TerrainDetailPromotionState.DeferredCooldown:
                if (chunk.DetailPromotionNextEligibleAtSeconds > double.NegativeInfinity &&
                    nowSeconds < chunk.DetailPromotionNextEligibleAtSeconds)
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "cooldown_expiry", reactivatedByCooldownExpiry: true);
                return false;

            case TerrainDetailPromotionState.DeferredPressure:
                if (pressureModeActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(
                    chunk,
                    pressureModeActive ? "pressure_priority_upgrade" : "pressure_exit",
                    reactivatedByPressureExit: !pressureModeActive);
                return false;

            case TerrainDetailPromotionState.DeferredStartupPriority:
                if (startupCoarsePriorityActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "startup_priority_clear");
                return false;

            case TerrainDetailPromotionState.DeferredCollisionBootstrap:
                if (ShouldAwaitCollisionBootstrapBeforeAutomaticDetail(chunk))
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "collision_bootstrap_ready");
                return false;

            case TerrainDetailPromotionState.DeferredPromotionBudget:
                if (chunk.DetailPromotionNextEligibleFrame > 0 &&
                    currentFrame < chunk.DetailPromotionNextEligibleFrame)
                {
                    RecordAvoidedDeferredReevaluation();
                    return true;
                }

                ReactivateDetailPromotion(chunk, "promotion_budget_expiry");
                return false;
        }

        return false;
    }

    private bool ShouldKeepChunkVisibleForContinuity(TerrainChunk chunk)
    {
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            !chunk.HasSurface ||
            _trackedCharacter == null ||
            !IsInstanceValid(_trackedCharacter))
        {
            return false;
        }

        float continuityRange = GetImmediateCoarseVisibilityRange() + (_settings.ChunkSize * 0.5f);
        return ComputeTrackedChunkDistance(chunk) <= continuityRange;
    }

    private void ApplyDeferredDetailPromotion(TerrainChunk chunk, TerrainDetailPromotionDeferDecision decision)
    {
        chunk.DeferDetailPromotion(
            decision.State,
            decision.Reason,
            decision.NextEligibleFrame,
            decision.NextEligibleAtSeconds);
        if (TryGetDetailPromotionSchedulingCategory(decision.Reason, out string category))
        {
            _terrainStats.RecordMeshSchedulingDecision(
                chunk.ChunkKey,
                category,
                TerrainVisualBuildRequestKind.DetailPromotion,
                ClassifyVisualBuildQueue(chunk, TerrainVisualBuildRequestKind.DetailPromotion),
                chunk.LoadSource,
                chunk.LastTotalTriangleCount,
                decision.Reason);
        }

        RecordDeferredDetailPromotion(chunk, decision.Reason);
    }

    private static bool TryGetDetailPromotionSchedulingCategory(string reason, out string category)
    {
        if (string.Equals(reason, "pressure_mode", StringComparison.Ordinal))
        {
            category = "pressure_throttle";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(reason) &&
            reason.StartsWith("low_value", StringComparison.Ordinal))
        {
            category = "skip_promotion";
            return true;
        }

        category = string.Empty;
        return false;
    }

    private void ClearDeferredDetailPromotionState(TerrainChunk chunk, string trigger)
    {
        if (chunk.IsDetailPromotionDeferred)
        {
            _terrainStats.RecordDetailPromotionEligible(chunk.ChunkKey, trigger);
        }

        chunk.SetDetailPromotionEligible();
    }

    private void ReactivateDetailPromotion(
        TerrainChunk chunk,
        string trigger,
        bool reactivatedByMeshCompletion = false,
        bool reactivatedByCooldownExpiry = false,
        bool reactivatedByPressureExit = false)
    {
        if (!IsInstanceValid(chunk))
        {
            return;
        }

        if (chunk.IsDetailPromotionDeferred)
        {
            _terrainStats.RecordDetailPromotionEligible(chunk.ChunkKey, trigger);
        }

        chunk.ReactivateDetailPromotion(trigger);
        if (reactivatedByMeshCompletion)
        {
            RecordRequestReactivatedByMeshCompletion();
        }

        if (reactivatedByCooldownExpiry)
        {
            RecordRequestReactivatedByCooldownExpiry();
        }

        if (reactivatedByPressureExit)
        {
            RecordRequestReactivatedByPressureExit();
        }
    }

    private bool ShouldPrioritizeAutomaticDetailDuringPressure(TerrainChunk chunk)
    {
        if (chunk == null)
        {
            return false;
        }

        float distance = ComputeTrackedChunkDistance(chunk);
        float veryNearRange = Mathf.Max(PlayerDetailRequestRadius * 0.45f, _settings.ChunkSize * 0.75f);
        if (distance <= veryNearRange)
        {
            return true;
        }

        if (HasUrgentStickyDetailDemand(chunk))
        {
            return true;
        }

        return
            (chunk.DetailPromotionReevaluationPending || chunk.DetailPromotionFollowupRequested) &&
            distance <= GetImmediateCoarseVisibilityRange();
    }

    private static bool HasUrgentStickyDetailDemand(TerrainChunk chunk)
    {
        if (chunk == null)
        {
            return false;
        }

        if (chunk.HasEditedDetailBrick)
        {
            return true;
        }

        foreach (TerrainDetailRegion region in chunk.DetailRegionManager.Regions)
        {
            if (region.Sticky && region.Source == TerrainDetailRegionSource.Edit)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryBuildLowValueDetailPromotionDeferDecision(
        TerrainChunk chunk,
        ulong currentFrame,
        bool pressureModeActive,
        out TerrainDetailPromotionDeferDecision decision)
    {
        decision = default;
        bool forceLowValueSuppression =
            chunk != null &&
            IsInstanceValid(chunk) &&
            (!CanVerticalChunkContainSurface(chunk.ChunkKey) ||
             (chunk.HasCompletedInitialVisualBuild && !chunk.HasSurface));
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            (ShouldPrioritizeAutomaticDetailDuringPressure(chunk) && !forceLowValueSuppression))
        {
            return false;
        }

        ulong backoffFrames = pressureModeActive
            ? LowValueDetailBackoffFrames + 4UL
            : LowValueDetailBackoffFrames;
        if (!CanVerticalChunkContainSurface(chunk.ChunkKey))
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPromotionBudget,
                "low_value_empty_vertical",
                currentFrame + backoffFrames,
                double.NegativeInfinity);
            return true;
        }

        if (!chunk.HasCompletedInitialVisualBuild)
        {
            return false;
        }

        if (!chunk.HasSurface)
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPromotionBudget,
                "low_value_no_surface",
                currentFrame + backoffFrames,
                double.NegativeInfinity);
            return true;
        }

        if (chunk.LastTotalTriangleCount > 0 &&
            chunk.LastTotalTriangleCount <= TinyFragmentTriangleCount)
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPromotionBudget,
                "low_value_tiny_surface",
                currentFrame + backoffFrames,
                double.NegativeInfinity);
            return true;
        }

        return false;
    }

    private bool ShouldSuppressAutomaticDetailRequest(TerrainChunk chunk)
    {
        if (chunk == null || !IsInstanceValid(chunk))
        {
            return true;
        }

        if (!chunk.HasCompletedInitialVisualBuild)
        {
            return true;
        }

        if (!CanVerticalChunkContainSurface(chunk.ChunkKey))
        {
            return true;
        }

        return !chunk.HasSurface;
    }

    private static float QuantizeAutomaticDetailDistance(TerrainChunk chunk, float distance)
    {
        if (chunk == null)
        {
            return Mathf.Max(distance, 0.0f);
        }

        float step = Mathf.Max(0.5f, chunk.VoxelSize * 0.5f);
        if (step <= 0.0001f)
        {
            return Mathf.Max(distance, 0.0f);
        }

        return Mathf.Max(0.0f, Mathf.Round(distance / step) * step);
    }

    private TerrainDetailPromotionDeferDecision BuildPromotionBudgetDeferDecision(
        TerrainChunk chunk,
        ulong currentFrame,
        bool pressureModeActive)
    {
        ulong backoffFrames = ShouldPrioritizeAutomaticDetailDuringPressure(chunk)
            ? 1UL
            : (pressureModeActive ? 6UL : 3UL);
        return new TerrainDetailPromotionDeferDecision(
            TerrainDetailPromotionState.DeferredPromotionBudget,
            "promotion_budget",
            currentFrame + backoffFrames,
            double.NegativeInfinity);
    }

    private bool RefreshChunkDetailRequests(TerrainChunk chunk, Vector3 trackedPosition, bool hasTrackedCharacter)
    {
        if (ShouldPrioritizeStartupCoarseShell())
        {
            return false;
        }

        bool changed = false;
        bool pressureModeActive = IsVisualMeshPressureActive();
        if (!pressureModeActive)
        {
            changed |= hasTrackedCharacter
                ? UpdatePlayerProximityDetailRequest(chunk, trackedPosition)
                : RemoveDetailRequest(chunk, TerrainDetailRegionSource.PlayerProximity, PlayerDetailRequestId);
            changed |= hasTrackedCharacter
                ? UpdateBiomeDetailRequest(chunk, trackedPosition)
                : RemoveDetailRequest(chunk, TerrainDetailRegionSource.Biome, BiomeDetailRequestId);
        }

        changed |= UpdateStructureDetailRequests(chunk);
        return changed;
    }

    private bool UpdatePlayerProximityDetailRequest(TerrainChunk chunk, Vector3 trackedPosition)
    {
        if (PlayerDetailRequestRadius <= 0.01f)
        {
            return RemoveDetailRequest(chunk, TerrainDetailRegionSource.PlayerProximity, PlayerDetailRequestId);
        }

        if (ShouldSuppressAutomaticDetailRequest(chunk))
        {
            return RemoveDetailRequest(chunk, TerrainDetailRegionSource.PlayerProximity, PlayerDetailRequestId);
        }

        if (!chunk.TryGetLocalBoundsForSphere(trackedPosition, PlayerDetailRequestRadius, out Aabb localBounds))
        {
            return RemoveDetailRequest(chunk, TerrainDetailRegionSource.PlayerProximity, PlayerDetailRequestId);
        }

        Aabb snappedBounds = SnapLocalBounds(chunk, localBounds);
        float distance = DistanceToChunkBounds(chunk, trackedPosition);
        float quantizedDistance = QuantizeAutomaticDetailDistance(chunk, distance);
        int detailLevel = distance <= PlayerDetailRequestRadius * 0.45f ? 2 : 1;
        float priority = 80.0f - quantizedDistance;
        string reason = $"player_proximity dist {quantizedDistance:0.0}";
        return RequestDetailOnChunk(
            chunk,
            snappedBounds,
            detailLevel,
            TerrainDetailRegionSource.PlayerProximity,
            reason,
            priority,
            sticky: false,
            requestId: PlayerDetailRequestId);
    }

    private bool UpdateBiomeDetailRequest(TerrainChunk chunk, Vector3 trackedPosition)
    {
        if (ShouldSuppressAutomaticDetailRequest(chunk))
        {
            return RemoveDetailRequest(chunk, TerrainDetailRegionSource.Biome, BiomeDetailRequestId);
        }

        if (!TryBuildBiomeDetailRequest(chunk, trackedPosition, out Aabb localBounds, out int detailLevel, out float priority, out string reason))
        {
            return RemoveDetailRequest(chunk, TerrainDetailRegionSource.Biome, BiomeDetailRequestId);
        }

        return RequestDetailOnChunk(
            chunk,
            SnapLocalBounds(chunk, localBounds),
            detailLevel,
            TerrainDetailRegionSource.Biome,
            reason,
            priority,
            sticky: false,
            requestId: BiomeDetailRequestId);
    }

    private bool RemoveDetailRequest(TerrainChunk chunk, TerrainDetailRegionSource source, string requestId)
    {
        bool removed = chunk.RemoveDetailRequest(requestId);
        _terrainStats.LogDetailRegionRemoval(chunk.ChunkKey, source, removed ? 1 : 0);
        return removed;
    }

    private bool UpdateStructureDetailRequests(TerrainChunk chunk)
    {
        bool changed = false;
        foreach (TerrainStructureInstance structure in chunk.StructureMetadata.OverlappingStructures)
        {
            if (!chunk.TryGetLocalBoundsForWorldBounds(structure.InfluenceBounds, out Aabb localBounds))
            {
                continue;
            }

            string requestId = $"structure:{structure.Id}";
            string reason = $"structure:{structure.Type}:{structure.Id} p {structure.Priority:0.00}";
            changed |= RequestDetailOnChunk(
                chunk,
                localBounds,
                structure.RequestHigherTerrainDetail ? 2 : 1,
                TerrainDetailRegionSource.Structure,
                reason,
                60.0f + (structure.Priority * 10.0f),
                sticky: true,
                requestId: requestId);
        }

        return changed;
    }

    private TerrainDetailReconcileResult ReconcileChunkDetailBrick(
        TerrainChunk chunk,
        bool hasDetailAggregate,
        Aabb localBounds,
        int detailLevel)
    {
        if (!hasDetailAggregate)
        {
            return new TerrainDetailReconcileResult(chunk.RemoveTransientDetailBrick(), PromotedTransientDetail: false);
        }

        bool changed = chunk.EnsureDetailBrick(
            localBounds,
            detailLevel,
            SampleTerrainDensity,
            ResolveEditedMaterial,
            persistentEdits: false,
            preserveExistingCoverage: false);
        return new TerrainDetailReconcileResult(changed, PromotedTransientDetail: changed);
    }

    private bool TryBuildBiomeDetailRequest(
        TerrainChunk chunk,
        Vector3 trackedPosition,
        out Aabb localBounds,
        out int detailLevel,
        out float priority,
        out string reason)
    {
        float activationRadius = Mathf.Max(BiomeDetailActivationRadius, 0.0f);
        if (activationRadius <= 0.01f || DistanceToChunkBounds(chunk, trackedPosition) > activationRadius)
        {
            localBounds = default;
            detailLevel = 0;
            priority = 0.0f;
            reason = string.Empty;
            return false;
        }

        TerrainBiomeSample sample = chunk.BiomeSample;
        float policy =
            (sample.RockyWeight * 0.85f) +
            (sample.CanyonWeight * 1.0f) +
            (sample.VolcanicWeight * 0.95f) +
            (sample.SwampWeight * 0.28f) +
            (sample.PlainsWeight * 0.10f) +
            (sample.Ruggedness * 0.55f) +
            (sample.Activity * 0.38f);
        if (policy < 0.34f)
        {
            localBounds = default;
            detailLevel = 0;
            priority = 0.0f;
            reason = string.Empty;
            return false;
        }

        Vector3 center = chunk.Position + new Vector3(chunk.ChunkSize * 0.5f, 0.0f, chunk.ChunkSize * 0.5f);
        float surfaceY = _prioritySampler.SampleSurfaceHeight(center.X, center.Z);
        float horizontalRadius = Mathf.Lerp(chunk.ChunkSize * 0.18f, chunk.ChunkSize * 0.34f, Mathf.Clamp(policy, 0.0f, 1.0f));
        float verticalHalfExtent = Mathf.Lerp(
            Mathf.Max(chunk.VoxelSize * 1.5f, BiomeDetailVerticalMargin * 0.5f),
            Mathf.Max(BiomeDetailVerticalMargin, chunk.ChunkSize * 0.18f),
            Mathf.Clamp(policy, 0.0f, 1.0f));
        Aabb worldBounds = new(
            new Vector3(center.X - horizontalRadius, surfaceY - verticalHalfExtent, center.Z - horizontalRadius),
            new Vector3(horizontalRadius * 2.0f, verticalHalfExtent * 2.0f, horizontalRadius * 2.0f));
        if (!chunk.TryGetLocalBoundsForWorldBounds(worldBounds, out localBounds))
        {
            detailLevel = 0;
            priority = 0.0f;
            reason = string.Empty;
            return false;
        }

        detailLevel = policy >= 0.70f ? 2 : 1;
        priority = 34.0f + (policy * 18.0f);
        reason = $"biome:{sample.DominantBiome} pol {policy:0.00} rug {sample.Ruggedness:0.00} act {sample.Activity:0.00}";
        return true;
    }

    private bool TryBuildDetailAggregate(TerrainChunk chunk, out Aabb localBounds, out int detailLevel)
    {
        bool hasBounds = false;
        localBounds = default;
        detailLevel = 0;

        foreach (TerrainDetailRegion region in chunk.DetailRegionManager.Regions)
        {
            if (!hasBounds)
            {
                localBounds = region.LocalBounds;
                hasBounds = true;
            }
            else
            {
                localBounds = Union(localBounds, region.LocalBounds);
            }

            detailLevel = Mathf.Max(detailLevel, region.RequestedDetailLevel);
        }

        return hasBounds;
    }

    private void ProcessDirtyChunks()
    {
        RefreshVisualMeshSchedulerState();
        TrimStaleLowPriorityMeshRequests();
        DrainCompletedMeshBuilds();
        ProcessPendingMeshCommits();
        EnsureDirtyChunksHaveQueuedBuilds();
        FlushStagedHighPriorityVisualEnqueues();
        RefreshCollisionCoverage();
        ProcessQueuedCollisionRebuilds();
        StartVisualMeshBuildWorkers();
        UpdatePressureModeTelemetry();

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

    private void QueueChunkForRebuild(
        TerrainChunk chunk,
        TerrainVisualBuildRequestKind requestKind,
        TerrainMeshDetailMode detailMode,
        string reason)
    {
        if (chunk.RenderDirty)
        {
            _dirtyRenderChunks.Add(chunk);
            RefreshVisualMeshSchedulerState();
            TerrainVisualBuildQueueClass queueClass = ClassifyVisualBuildQueue(chunk, requestKind);
            if (string.Equals(reason, "dirty_retry", StringComparison.Ordinal) &&
                TrySuppressDirtyRetryEnqueue(
                    chunk,
                    requestKind,
                    queueClass,
                    Engine.GetProcessFrames(),
                    out string suppressReason,
                    out TerrainDetailPromotionDeferDecision deferredPromotionDecision))
            {
                if (requestKind == TerrainVisualBuildRequestKind.DetailPromotion)
                {
                    ApplyDeferredDetailPromotion(chunk, deferredPromotionDecision);
                }

                string schedulingCategory = requestKind == TerrainVisualBuildRequestKind.InitialCoarse
                    ? "skip_rebuild"
                    : "suppress_retry";
                _terrainStats.RecordMeshSchedulingDecision(
                    chunk.ChunkKey,
                    schedulingCategory,
                    requestKind,
                    queueClass,
                    chunk.LoadSource,
                    chunk.LastTotalTriangleCount,
                    suppressReason);
                return;
            }

            TerrainMeshQueueResult enqueueResult = QueueVisualBuildRequest(
                new TerrainVisualBuildRequest(
                    chunk,
                    chunk.ChunkKey,
                    requestKind,
                    queueClass,
                    ComputeVisualBuildPriority(chunk, requestKind),
                    detailMode,
                    ShouldBypassVisualMeshBackpressure(chunk, requestKind, queueClass),
                    reason));
            if (enqueueResult.Coalesced)
            {
                RecordCoalescedRebuildRequest(chunk.ChunkKey, "visual", reason);
            }

            if (enqueueResult.Suppressed)
            {
                RecordSuppressedDuplicateBuild(chunk.ChunkKey, "visual", reason);
            }

            if (enqueueResult.Skipped)
            {
                RecordSkippedLowPriorityBuild(chunk.ChunkKey, "visual", reason);
            }
        }

        if (chunk.CollisionDirty)
        {
            _dirtyCollisionChunks.Add(chunk);
            TerrainCollisionRequestKind collisionKind = requestKind == TerrainVisualBuildRequestKind.Edit
                ? TerrainCollisionRequestKind.Edit
                : TerrainCollisionRequestKind.NearPlayer;
            TerrainMeshQueueResult collisionResult = QueueCollisionRebuild(chunk, collisionKind, reason);
            if (collisionResult.Coalesced)
            {
                RecordCoalescedRebuildRequest(chunk.ChunkKey, "collision", reason);
            }
        }
    }

    private bool TrySuppressDirtyRetryEnqueue(
        TerrainChunk chunk,
        TerrainVisualBuildRequestKind requestKind,
        TerrainVisualBuildQueueClass queueClass,
        ulong currentFrame,
        out string suppressReason,
        out TerrainDetailPromotionDeferDecision deferredPromotionDecision)
    {
        suppressReason = string.Empty;
        deferredPromotionDecision = default;
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            queueClass != TerrainVisualBuildQueueClass.Background)
        {
            return false;
        }

        if (requestKind == TerrainVisualBuildRequestKind.InitialCoarse &&
            !_desiredChunks.Contains(chunk.ChunkKey) &&
            !CanVerticalChunkContainSurface(chunk.ChunkKey))
        {
            suppressReason = "predicted_empty_vertical";
            return true;
        }

        if (requestKind != TerrainVisualBuildRequestKind.DetailPromotion)
        {
            return false;
        }

        bool pressureModeActive = IsVisualMeshPressureActive();
        if (TryBuildLowValueDetailPromotionDeferDecision(
                chunk,
                currentFrame,
                pressureModeActive,
                out deferredPromotionDecision))
        {
            suppressReason = deferredPromotionDecision.Reason;
            return true;
        }

        if (pressureModeActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
        {
            deferredPromotionDecision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPressure,
                "pressure_mode",
                currentFrame + PressureThrottledDetailBackoffFrames,
                double.NegativeInfinity);
            suppressReason = deferredPromotionDecision.Reason;
            return true;
        }

        return false;
    }

    private TerrainMeshQueueResult QueueVisualBuildRequest(TerrainVisualBuildRequest request)
    {
        if (_stagedHighPriorityVisualEnqueueStates.TryGetValue(request.Key, out StagedVisualEnqueueState stagedState))
        {
            TerrainVisualBuildRequest merged = MergeVisualBuildRequests(stagedState.Request, request);
            if (!ShouldRefreshVisualBuildRequest(stagedState.Request, merged))
            {
                return new TerrainMeshQueueResult(Enqueued: false, Coalesced: true, Deferred: true, Suppressed: true);
            }

            int token = NextStagedVisualEnqueueToken();
            stagedState.Update(merged, token);
            _stagedHighPriorityVisualEnqueues.Enqueue(
                new StagedVisualEnqueueEntry(request.Key, token),
                ComposeStagedVisualEnqueuePriority(merged, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: true);
        }

        if (_meshBuildScheduler.HasPendingWork(request.Key))
        {
            return _meshBuildScheduler.Queue(request);
        }

        if (request.QueueClass != TerrainVisualBuildQueueClass.Background)
        {
            return StageHighPriorityVisualEnqueue(request);
        }

        return _meshBuildScheduler.Queue(request);
    }

    private TerrainMeshQueueResult StageHighPriorityVisualEnqueue(TerrainVisualBuildRequest request)
    {
        int token = NextStagedVisualEnqueueToken();
        _stagedHighPriorityVisualEnqueueStates[request.Key] = new StagedVisualEnqueueState(request, token);
        _stagedHighPriorityVisualEnqueues.Enqueue(
            new StagedVisualEnqueueEntry(request.Key, token),
            ComposeStagedVisualEnqueuePriority(request, token));
        _lastDeferredHighPriorityEnqueueCount++;
        _totalDeferredHighPriorityEnqueueCount++;
        return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: true);
    }

    private void FlushStagedHighPriorityVisualEnqueues()
    {
        if (_stagedHighPriorityVisualEnqueueStates.Count == 0)
        {
            return;
        }

        int remainingHighPriority = Mathf.Max(0, MaxHighPriorityMeshEnqueuesPerFrame);
        int remainingNearCoarse = Mathf.Clamp(MaxNearCoarseMeshEnqueuesPerFrame, 0, Mathf.Max(0, remainingHighPriority));
        int remainingDetailPromotions = Mathf.Max(0, MaxDetailPromotionActivationsPerFrame);
        bool budgetHitRecorded = false;
        while (TryPeekNextStagedHighPriorityRequest(out TerrainVisualBuildRequest request))
        {
            string blockReason = GetStagedHighPriorityDeferralReason(
                request,
                remainingHighPriority,
                remainingNearCoarse,
                remainingDetailPromotions);
            if (!string.IsNullOrEmpty(blockReason))
            {
                if (!budgetHitRecorded && blockReason.StartsWith("budget", StringComparison.Ordinal))
                {
                    _lastHighPriorityEnqueueBudgetHitCount++;
                    _totalHighPriorityEnqueueBudgetHitCount++;
                    budgetHitRecorded = true;
                }

                break;
            }

            if (!TryTakeNextStagedHighPriorityRequest(out StagedVisualEnqueueState stagedState))
            {
                continue;
            }

            TerrainMeshQueueResult enqueueResult = _meshBuildScheduler.Queue(stagedState.Request);
            if (enqueueResult.Coalesced)
            {
                RecordCoalescedRebuildRequest(stagedState.Request.Key, "visual", stagedState.Request.Reason);
            }

            if (enqueueResult.Suppressed)
            {
                RecordSuppressedDuplicateBuild(stagedState.Request.Key, "visual", stagedState.Request.Reason);
            }

            _lastSmoothedHighPriorityEnqueueCount++;
            _totalSmoothedHighPriorityEnqueueCount++;
            remainingHighPriority = Mathf.Max(0, remainingHighPriority - 1);
            if (stagedState.Request.Kind == TerrainVisualBuildRequestKind.InitialCoarse)
            {
                remainingNearCoarse = Mathf.Max(0, remainingNearCoarse - 1);
            }

            if (stagedState.Request.Kind == TerrainVisualBuildRequestKind.DetailPromotion)
            {
                remainingDetailPromotions = Mathf.Max(0, remainingDetailPromotions - 1);
            }
        }
    }

    private bool TryPeekNextStagedHighPriorityRequest(out TerrainVisualBuildRequest request)
    {
        while (_stagedHighPriorityVisualEnqueues.Count > 0)
        {
            StagedVisualEnqueueEntry entry = _stagedHighPriorityVisualEnqueues.Peek();
            if (!_stagedHighPriorityVisualEnqueueStates.TryGetValue(entry.Key, out StagedVisualEnqueueState state) ||
                state.Token != entry.Token)
            {
                _stagedHighPriorityVisualEnqueues.Dequeue();
                continue;
            }

            request = state.Request;
            return true;
        }

        request = default;
        return false;
    }

    private bool TryTakeNextStagedHighPriorityRequest(out StagedVisualEnqueueState state)
    {
        while (_stagedHighPriorityVisualEnqueues.Count > 0)
        {
            StagedVisualEnqueueEntry entry = _stagedHighPriorityVisualEnqueues.Dequeue();
            if (!_stagedHighPriorityVisualEnqueueStates.TryGetValue(entry.Key, out StagedVisualEnqueueState queuedState) ||
                queuedState.Token != entry.Token)
            {
                continue;
            }

            _stagedHighPriorityVisualEnqueueStates.Remove(entry.Key);
            state = queuedState;
            return true;
        }

        state = null!;
        return false;
    }

    private string GetStagedHighPriorityDeferralReason(
        TerrainVisualBuildRequest request,
        int remainingHighPriority,
        int remainingNearCoarse,
        int remainingDetailPromotions)
    {
        if (remainingHighPriority <= 0)
        {
            return "budget_high_priority";
        }

        if (request.Kind == TerrainVisualBuildRequestKind.InitialCoarse && remainingNearCoarse <= 0)
        {
            return "budget_near_coarse";
        }

        if (request.Kind == TerrainVisualBuildRequestKind.DetailPromotion && remainingDetailPromotions <= 0)
        {
            return "budget_detail_promotion";
        }

        int pressureDepth = GetHighPriorityPressureDepth();
        int softLimit = Mathf.Max(1, HighPriorityQueueSoftLimit);
        int hardLimit = Mathf.Max(softLimit, HighPriorityQueueHardLimit);
        if (request.Kind == TerrainVisualBuildRequestKind.DetailPromotion)
        {
            if (pressureDepth >= hardLimit)
            {
                return "pressure_hard_limit";
            }

            if (pressureDepth >= softLimit)
            {
                return "pressure_soft_limit";
            }
        }

        return string.Empty;
    }

    private int GetHighPriorityPressureDepth()
    {
        return
            _meshBuildScheduler.HighPriorityQueueDepth +
            _meshBuildScheduler.HighPriorityRunningCount +
            _stagedHighPriorityVisualEnqueueStates.Count;
    }

    private int NextStagedVisualEnqueueToken()
    {
        return ++_stagedVisualEnqueueSequence;
    }

    private static TerrainVisualBuildRequest MergeVisualBuildRequests(TerrainVisualBuildRequest current, TerrainVisualBuildRequest next)
    {
        TerrainVisualBuildRequestKind kind = GetVisualBuildKindPriorityLane(next.Kind) < GetVisualBuildKindPriorityLane(current.Kind)
            ? next.Kind
            : current.Kind;
        TerrainVisualBuildQueueClass queueClass = GetVisualBuildQueueClassPriorityLane(next.QueueClass) < GetVisualBuildQueueClassPriorityLane(current.QueueClass)
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

    private static bool ShouldRefreshVisualBuildRequest(TerrainVisualBuildRequest current, TerrainVisualBuildRequest merged)
    {
        return
            GetVisualBuildQueueClassPriorityLane(merged.QueueClass) < GetVisualBuildQueueClassPriorityLane(current.QueueClass) ||
            GetVisualBuildKindPriorityLane(merged.Kind) < GetVisualBuildKindPriorityLane(current.Kind) ||
            merged.DetailMode > current.DetailMode ||
            (merged.BypassBackpressure && !current.BypassBackpressure) ||
            merged.PriorityScore > (current.PriorityScore + 4.0f);
    }

    private static int GetVisualBuildKindPriorityLane(TerrainVisualBuildRequestKind kind)
    {
        return kind switch
        {
            TerrainVisualBuildRequestKind.Edit => 0,
            TerrainVisualBuildRequestKind.InitialCoarse => 1,
            _ => 2
        };
    }

    private static int GetVisualBuildQueueClassPriorityLane(TerrainVisualBuildQueueClass queueClass)
    {
        return queueClass switch
        {
            TerrainVisualBuildQueueClass.Critical => 0,
            TerrainVisualBuildQueueClass.NearCoarse => 1,
            _ => 2
        };
    }

    private static StagedVisualEnqueuePriority ComposeStagedVisualEnqueuePriority(TerrainVisualBuildRequest request, int token)
    {
        int lane = request.Kind switch
        {
            TerrainVisualBuildRequestKind.Edit => 0,
            TerrainVisualBuildRequestKind.InitialCoarse => 1,
            _ => 2
        };
        return new StagedVisualEnqueuePriority(lane, -request.PriorityScore, token);
    }

    private void DrainCompletedMeshBuilds()
    {
        foreach (TerrainVisualBuildCompletedJob completedJob in _meshBuildScheduler.DrainCompletedJobs())
        {
            _lastMeshWorkerBuildCount++;
            _lastMeshWorkerBuildMs += completedJob.WorkerBuildMs;
            _lastNormalDebugMismatchCount += completedJob.ExecutionResult.MeshResult.NormalDebugMismatchCount;
            _totalNormalDebugMismatchCount += completedJob.ExecutionResult.MeshResult.NormalDebugMismatchCount;
            if (completedJob.ExecutionResult.MeshResult.HasTangents)
            {
                _lastTangentGenerationCount++;
                _totalTangentGenerationCount++;
            }

            _terrainStats.RecordMeshBuildWorker(
                completedJob.Job.Key,
                completedJob.WorkerBuildMs,
                completedJob.QueueWaitMs,
                completedJob.QueueDepthOnStart,
                completedJob.Job.Kind,
                completedJob.Job.QueueClass,
                completedJob.Job.DirtyBounds,
                completedJob.ExecutionResult.ManagedHeapDeltaBytes,
                completedJob.ExecutionResult.Gen0Collections,
                completedJob.ExecutionResult.Gen1Collections,
                completedJob.ExecutionResult.Gen2Collections,
                completedJob.ExecutionResult.MeshResult.UsedDetailBrick,
                completedJob.ExecutionResult.MeshResult.UsedPersistentDetailEdits,
                completedJob.ExecutionResult.MeshResult.DetailTriangleCount,
                completedJob.ExecutionResult.MeshResult.ReplacedCoarseCellCount,
                completedJob.ExecutionResult.MeshResult.TotalTriangleCount);
            _pendingMeshCommits.Enqueue(
                completedJob,
                ComposeVisualWorkPriority(completedJob.Job.Kind, completedJob.Job.PriorityScore));
        }
    }

    private void ProcessPendingMeshCommits()
    {
        int visualBudget = GetCurrentVisualRebuildBudget();
        float visualTimeBudgetMs = GetCurrentVisualCommitTimeBudgetMs();
        ulong budgetStartUsec = Time.GetTicksUsec();
        while (visualBudget > 0 && _pendingMeshCommits.Count > 0)
        {
            if (HasExceededMainThreadBudget(budgetStartUsec, visualTimeBudgetMs))
            {
                break;
            }

            TerrainVisualBuildCompletedJob completedJob = _pendingMeshCommits.Dequeue();
            TerrainVisualBuildJob job = completedJob.Job;
            if (!IsInstanceValid(job.Chunk) ||
                !_residentChunks.TryGetValue(job.Key, out TerrainChunk residentChunk) ||
                !ReferenceEquals(residentChunk, job.Chunk))
            {
                _dirtyRenderChunks.Remove(job.Chunk);
                continue;
            }

            _terrainStats.LogChunkRemeshBegin(job.Key, "mesh_commit", job.DirtyBounds);
            VoxelMeshBuildResult meshResult = completedJob.ExecutionResult.MeshResult;
            bool hadSurfaceBeforeCommit = residentChunk.HasSurface;
            int previousTriangleCount = residentChunk.LastTotalTriangleCount;
            bool emptyMesh = !meshResult.HasGeometry || meshResult.TotalTriangleCount <= 0;
            bool tinyMesh = !emptyMesh && meshResult.TotalTriangleCount <= TinyFragmentTriangleCount;
            bool suppressTinyMesh = tinyMesh && ShouldSuppressTinyMesh(residentChunk, job, meshResult);
            if (emptyMesh)
            {
                _terrainStats.RecordMeshResultDecision(
                    residentChunk.ChunkKey,
                    "empty_cleared",
                    job.Kind,
                    job.QueueClass,
                    meshResult.TotalTriangleCount,
                    meshResult.DetailTriangleCount,
                    meshResult.ReplacedCoarseCellCount,
                    meshResult.UsedDetailBrick,
                    meshResult.UsedPersistentDetailEdits);
            }
            else if (tinyMesh)
            {
                _terrainStats.RecordMeshResultDecision(
                    residentChunk.ChunkKey,
                    suppressTinyMesh ? "tiny_suppressed" : "tiny_committed",
                    job.Kind,
                    job.QueueClass,
                    meshResult.TotalTriangleCount,
                    meshResult.DetailTriangleCount,
                    meshResult.ReplacedCoarseCellCount,
                    meshResult.UsedDetailBrick,
                    meshResult.UsedPersistentDetailEdits);
            }

            VoxelMeshBuildResult commitMeshResult = suppressTinyMesh
                ? VoxelMeshBuildResult.Empty
                : meshResult;
            bool committed = residentChunk.TryCommitRenderMesh(commitMeshResult, job.Revision);
            if (!committed)
            {
                HandleDetailPromotionAfterVisualCommit(residentChunk, job, committed: false);
                if (!residentChunk.RenderDirty)
                {
                    _dirtyRenderChunks.Remove(residentChunk);
                }

                continue;
            }

            HandleDetailPromotionAfterVisualCommit(residentChunk, job, committed: true);

            _lastVisualRebuildCount++;
            _lastVisualRebuildMs += residentChunk.LastRenderBuildMs;
            _terrainStats.RecordMeshCommit(
                residentChunk.ChunkKey,
                residentChunk.LastRenderBuildMs,
                job.DirtyBounds,
                residentChunk.LastUsedDetailBrick,
                residentChunk.LastUsedPersistentDetailEdits,
                residentChunk.LastDetailTriangleCount,
                residentChunk.LastReplacedCoarseCellCount,
                residentChunk.LastTotalTriangleCount);
            if (hadSurfaceBeforeCommit && !residentChunk.HasSurface)
            {
                string reason = suppressTinyMesh
                    ? "tiny_suppressed"
                    : "empty_rebuild";
                _terrainStats.RecordStaleMeshCleared(residentChunk.ChunkKey, reason, previousTriangleCount);
            }
            visualBudget--;
            if (!residentChunk.RenderDirty)
            {
                _dirtyRenderChunks.Remove(residentChunk);
            }

            if (!residentChunk.HasSurface && residentChunk.HasCollision && !residentChunk.CollisionDirty)
            {
                residentChunk.MarkCollisionDirty(CollisionRebuildDelaySeconds);
                _dirtyCollisionChunks.Add(residentChunk);
            }

            if (residentChunk.CollisionDirty)
            {
                TerrainMeshQueueResult collisionResult = QueueCollisionRebuild(
                    residentChunk,
                    job.Kind == TerrainVisualBuildRequestKind.Edit
                        ? TerrainCollisionRequestKind.Edit
                        : TerrainCollisionRequestKind.NearPlayer,
                    residentChunk.HasSurface
                        ? "visual_commit_followup"
                        : "empty_visual_clear");
                if (collisionResult.Coalesced)
                {
                    RecordCoalescedRebuildRequest(
                        residentChunk.ChunkKey,
                        "collision",
                        residentChunk.HasSurface
                            ? "visual_commit_followup"
                            : "empty_visual_clear");
                }
            }
            else if (residentChunk.IsInitialVisualReady &&
                     residentChunk.HasSurface &&
                     !residentChunk.HasCollision &&
                     ShouldEnsureCollision(residentChunk))
            {
                residentChunk.MarkCollisionDirty(CollisionRebuildDelaySeconds);
                _dirtyCollisionChunks.Add(residentChunk);
                TerrainMeshQueueResult collisionResult = QueueCollisionRebuild(
                    residentChunk,
                    TerrainCollisionRequestKind.NearPlayer,
                    "collision_bootstrap");
                if (collisionResult.Coalesced)
                {
                    RecordCoalescedRebuildRequest(residentChunk.ChunkKey, "collision", "collision_bootstrap");
                }
            }
        }
    }

    private bool ShouldSuppressTinyMesh(
        TerrainChunk chunk,
        TerrainVisualBuildJob job,
        VoxelMeshBuildResult meshResult)
    {
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            meshResult.TotalTriangleCount <= 0 ||
            meshResult.TotalTriangleCount > TinyFragmentTriangleCount)
        {
            return false;
        }

        if (job.QueueClass != TerrainVisualBuildQueueClass.Background)
        {
            return false;
        }

        return !IsChunkInNearPlayerCoverageSafetyZone(chunk.ChunkKey);
    }

    private void HandleDetailPromotionAfterVisualCommit(
        TerrainChunk chunk,
        TerrainVisualBuildJob job,
        bool committed)
    {
        if (!IsInstanceValid(chunk))
        {
            return;
        }

        if (!committed)
        {
            if (chunk.DetailPromotionState == TerrainDetailPromotionState.Running)
            {
                if (_meshBuildScheduler.HasPendingWork(chunk.ChunkKey) || chunk.RenderDirty)
                {
                    chunk.MarkDetailPromotionQueued();
                }
                else if (chunk.DetailPromotionFollowupRequested)
                {
                    ReactivateDetailPromotion(chunk, "mesh_completion", reactivatedByMeshCompletion: true);
                }
                else
                {
                    chunk.SetDetailPromotionEligible();
                }
            }

            if (chunk.DetailPromotionState == TerrainDetailPromotionState.DeferredPendingMesh &&
                !_meshBuildScheduler.HasPendingWork(chunk.ChunkKey) &&
                !chunk.RenderDirty)
            {
                ReactivateDetailPromotion(chunk, "mesh_completion", reactivatedByMeshCompletion: true);
            }

            return;
        }

        if (chunk.DetailPromotionState == TerrainDetailPromotionState.DeferredPendingMesh ||
            chunk.DetailPromotionFollowupRequested)
        {
            ReactivateDetailPromotion(chunk, "mesh_completion", reactivatedByMeshCompletion: true);
            return;
        }

        if (chunk.DetailPromotionState is TerrainDetailPromotionState.Queued or TerrainDetailPromotionState.Running)
        {
            if (job.DetailMode == TerrainMeshDetailMode.IncludeTransientDetail)
            {
                chunk.MarkDetailPromotionApplied();
            }
            else
            {
                ReactivateDetailPromotion(chunk, "mesh_completion", reactivatedByMeshCompletion: true);
            }
        }
    }

    private void StartVisualMeshBuildWorkers()
    {
        _meshBuildScheduler.StartJobs(
            new TerrainMeshBuildExecutionBudget(
                GetCurrentVisualMeshWorkerBudget(),
                GetCurrentEditVisualMeshWorkerBudget(),
                GetCurrentCoarseVisualMeshWorkerBudget(),
                GetCurrentDetailVisualMeshWorkerBudget(),
                GetCurrentBackgroundVisualMeshWorkerBudget()),
            PrepareVisualBuildJob,
            ExecuteVisualBuildJob);
    }

    private void EnsureDirtyChunksHaveQueuedBuilds()
    {
        List<TerrainChunk> chunks = new(_dirtyRenderChunks);
        foreach (TerrainChunk chunk in chunks)
        {
            if (!IsInstanceValid(chunk) || !chunk.RenderDirty)
            {
                _dirtyRenderChunks.Remove(chunk);
                continue;
            }

            if (_meshBuildScheduler.HasPendingWork(chunk.ChunkKey))
            {
                continue;
            }

            TerrainVisualBuildRequestKind requestKind = !chunk.HasCompletedInitialVisualBuild
                ? TerrainVisualBuildRequestKind.InitialCoarse
                : (chunk.HasEditedDetailBrick ? TerrainVisualBuildRequestKind.Edit : TerrainVisualBuildRequestKind.DetailPromotion);
            if (requestKind == TerrainVisualBuildRequestKind.DetailPromotion && chunk.IsDetailPromotionDeferred)
            {
                continue;
            }

            TerrainMeshDetailMode detailMode = chunk.HasDetailBrick || chunk.DetailRegionCount > 0
                ? TerrainMeshDetailMode.IncludeTransientDetail
                : TerrainMeshDetailMode.CoarseOnly;
            TerrainVisualBuildQueueClass queueClass = ClassifyVisualBuildQueue(chunk, requestKind);
            if (!ShouldAttemptDeferredVisualRetry(queueClass))
            {
                continue;
            }

            QueueChunkForRebuild(chunk, requestKind, detailMode, "dirty_retry");
        }
    }

    private TerrainVisualBuildJob? PrepareVisualBuildJob(TerrainVisualBuildRequest request)
    {
        if (!IsInstanceValid(request.Chunk) ||
            !_residentChunks.TryGetValue(request.Key, out TerrainChunk residentChunk) ||
            !ReferenceEquals(residentChunk, request.Chunk))
        {
            _dirtyRenderChunks.Remove(request.Chunk);
            return null;
        }

        if (!residentChunk.RenderDirty || !residentChunk.HasData)
        {
            _dirtyRenderChunks.Remove(residentChunk);
            return null;
        }

        if (TrySuppressPreparedVisualBuild(
                residentChunk,
                request,
                out string schedulingCategory,
                out string suppressReason,
                out TerrainDetailPromotionDeferDecision deferredPromotionDecision))
        {
            if (request.Kind == TerrainVisualBuildRequestKind.DetailPromotion)
            {
                ApplyDeferredDetailPromotion(residentChunk, deferredPromotionDecision);
            }

            _terrainStats.RecordMeshSchedulingDecision(
                request.Key,
                schedulingCategory,
                request.Kind,
                request.QueueClass,
                residentChunk.LoadSource,
                residentChunk.LastTotalTriangleCount,
                suppressReason);
            return null;
        }

        TerrainVisualBuildJob? preparedJob = residentChunk.TryCreateVisualBuildJob(request);
        if (preparedJob.HasValue)
        {
            if (residentChunk.DetailPromotionState == TerrainDetailPromotionState.Queued &&
                request.DetailMode == TerrainMeshDetailMode.IncludeTransientDetail)
            {
                residentChunk.MarkDetailPromotionRunning();
            }

            _terrainStats.LogChunkRemeshBegin(request.Key, "mesh_worker", preparedJob.Value.DirtyBounds);
        }

        return preparedJob;
    }

    private bool TrySuppressPreparedVisualBuild(
        TerrainChunk chunk,
        TerrainVisualBuildRequest request,
        out string schedulingCategory,
        out string suppressReason,
        out TerrainDetailPromotionDeferDecision deferredPromotionDecision)
    {
        schedulingCategory = string.Empty;
        suppressReason = string.Empty;
        deferredPromotionDecision = default;
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            request.QueueClass != TerrainVisualBuildQueueClass.Background)
        {
            return false;
        }

        if (request.Kind == TerrainVisualBuildRequestKind.InitialCoarse &&
            !_desiredChunks.Contains(request.Key) &&
            !CanVerticalChunkContainSurface(request.Key))
        {
            schedulingCategory = "skip_rebuild";
            suppressReason = "predicted_empty_vertical";
            return true;
        }

        if (request.Kind != TerrainVisualBuildRequestKind.DetailPromotion)
        {
            return false;
        }

        ulong currentFrame = Engine.GetProcessFrames();
        bool pressureModeActive = IsVisualMeshPressureActive();
        if (TryBuildLowValueDetailPromotionDeferDecision(
                chunk,
                currentFrame,
                pressureModeActive,
                out deferredPromotionDecision))
        {
            schedulingCategory = string.Equals(request.Reason, "dirty_retry", StringComparison.Ordinal)
                ? "suppress_retry"
                : "skip_rebuild";
            suppressReason = deferredPromotionDecision.Reason;
            return true;
        }

        if (pressureModeActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
        {
            deferredPromotionDecision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPressure,
                "pressure_mode",
                currentFrame + PressureThrottledDetailBackoffFrames,
                double.NegativeInfinity);
            schedulingCategory = string.Equals(request.Reason, "dirty_retry", StringComparison.Ordinal)
                ? "suppress_retry"
                : "pressure_throttle";
            suppressReason = deferredPromotionDecision.Reason;
            return true;
        }

        return false;
    }

    private TerrainVisualBuildExecutionResult ExecuteVisualBuildJob(TerrainVisualBuildJob job)
    {
        try
        {
            long heapBefore = GC.GetTotalMemory(forceFullCollection: false);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            VoxelMeshBuildResult meshResult = _meshBackend.BuildMesh(job.DataSnapshot, _meshBuildOptions);
            long heapAfter = GC.GetTotalMemory(forceFullCollection: false);
            return new TerrainVisualBuildExecutionResult(
                meshResult,
                heapAfter - heapBefore,
                GC.CollectionCount(0) - gen0Before,
                GC.CollectionCount(1) - gen1Before,
                GC.CollectionCount(2) - gen2Before);
        }
        finally
        {
            job.DataSnapshot.Dispose();
        }
    }

    private void RefreshCollisionCoverage()
    {
        foreach (TerrainChunk chunk in _residentChunks.Values)
        {
            if (!IsInstanceValid(chunk) || !chunk.HasData || !chunk.IsInitialVisualReady)
            {
                continue;
            }

            if (!ShouldEnsureCollision(chunk) || chunk.HasCollision || chunk.CollisionDirty || !chunk.HasSurface)
            {
                continue;
            }

            chunk.MarkCollisionDirty(CollisionRebuildDelaySeconds);
            _dirtyCollisionChunks.Add(chunk);
            TerrainMeshQueueResult collisionResult = QueueCollisionRebuild(chunk, TerrainCollisionRequestKind.NearPlayer, "proximity_collision");
            if (collisionResult.Coalesced)
            {
                RecordCoalescedRebuildRequest(chunk.ChunkKey, "collision", "proximity_collision");
            }
        }
    }

    private void ProcessQueuedCollisionRebuilds()
    {
        int collisionBudget = GetCurrentCollisionRebuildBudget();
        if (collisionBudget <= 0)
        {
            return;
        }

        double nowSeconds = GetNowSeconds();
        float collisionTimeBudgetMs = GetCurrentCollisionTimeBudgetMs();
        ulong budgetStartUsec = Time.GetTicksUsec();
        List<CollisionQueueState> blockedRequests = new();
        bool throttleNearPlayerCollision = ShouldThrottleNearPlayerCollision();
        while (collisionBudget > 0)
        {
            if (HasExceededMainThreadBudget(budgetStartUsec, collisionTimeBudgetMs) ||
                !TryTakeNextCollisionRequest(out CollisionQueueState queuedState))
            {
                break;
            }

            TerrainChunk chunk = queuedState.Chunk;
            if (!IsInstanceValid(chunk) ||
                !_residentChunks.TryGetValue(queuedState.Key, out TerrainChunk residentChunk) ||
                !ReferenceEquals(residentChunk, chunk))
            {
                _dirtyCollisionChunks.Remove(chunk);
                continue;
            }

            if (!chunk.CollisionDirty)
            {
                _dirtyCollisionChunks.Remove(chunk);
                continue;
            }

            if (throttleNearPlayerCollision && queuedState.Kind != TerrainCollisionRequestKind.Edit)
            {
                blockedRequests.Add(queuedState);
                continue;
            }

            if (chunk.RenderDirty || nowSeconds < chunk.CollisionReadyAtSeconds)
            {
                blockedRequests.Add(queuedState);
                continue;
            }

            TerrainChunkDirtyBoundsSnapshot collisionDirtyBounds = chunk.CollisionDirtyBounds;
            _terrainStats.LogChunkRemeshBegin(chunk.ChunkKey, "collision", collisionDirtyBounds);
            if (chunk.TryRebuildCollision(nowSeconds))
            {
                _lastCollisionRebuildCount++;
                _lastCollisionRebuildMs += chunk.LastCollisionBuildMs;
                _terrainStats.RecordCollisionRebuild(
                    chunk.ChunkKey,
                    chunk.LastCollisionBuildMs,
                    collisionDirtyBounds,
                    chunk.HasDetailBrick,
                    chunk.LastUsedPersistentDetailEdits,
                    chunk.LastDetailTriangleCount,
                    chunk.LastReplacedCoarseCellCount,
                    chunk.LastTotalTriangleCount);
                collisionBudget--;
                if (!chunk.CollisionDirty)
                {
                    _dirtyCollisionChunks.Remove(chunk);
                }
            }
            else if (chunk.CollisionDirty)
            {
                blockedRequests.Add(queuedState);
            }
        }

        foreach (CollisionQueueState blockedRequest in blockedRequests)
        {
            RequeueCollisionRequest(blockedRequest);
        }
    }

    private TerrainMeshQueueResult QueueCollisionRebuild(
        TerrainChunk chunk,
        TerrainCollisionRequestKind requestKind,
        string reason)
    {
        if (!chunk.CollisionDirty)
        {
            return new TerrainMeshQueueResult(Enqueued: false, Coalesced: false, Deferred: false);
        }

        Vector3I key = chunk.ChunkKey;
        float priorityScore = ComputeCollisionBuildPriority(chunk, requestKind);
        if (_collisionQueueStates.TryGetValue(key, out CollisionQueueState existingState))
        {
            int token = NextRebuildPriorityToken();
            TerrainCollisionRequestKind mergedKind = requestKind < existingState.Kind
                ? requestKind
                : existingState.Kind;
            float mergedPriority = Mathf.Max(existingState.PriorityScore, priorityScore);
            existingState.Update(chunk, mergedKind, mergedPriority, reason, token);
            _collisionQueue.Enqueue(new CollisionQueueEntry(key, token), ComposeCollisionWorkPriority(mergedKind, mergedPriority, token));
            return new TerrainMeshQueueResult(Enqueued: true, Coalesced: true, Deferred: false);
        }

        int newToken = NextRebuildPriorityToken();
        CollisionQueueState queuedState = new(chunk, requestKind, priorityScore, reason, newToken);
        _collisionQueueStates[key] = queuedState;
        _collisionQueue.Enqueue(new CollisionQueueEntry(key, newToken), ComposeCollisionWorkPriority(requestKind, priorityScore, newToken));
        return new TerrainMeshQueueResult(Enqueued: true, Coalesced: false, Deferred: false);
    }

    private bool TryTakeNextCollisionRequest(out CollisionQueueState state)
    {
        while (_collisionQueue.Count > 0)
        {
            CollisionQueueEntry entry = _collisionQueue.Dequeue();
            if (!_collisionQueueStates.TryGetValue(entry.Key, out CollisionQueueState queuedState) || queuedState.Token != entry.Token)
            {
                continue;
            }

            _collisionQueueStates.Remove(entry.Key);
            state = queuedState;
            return true;
        }

        state = null!;
        return false;
    }

    private void RequeueCollisionRequest(CollisionQueueState state)
    {
        int token = NextRebuildPriorityToken();
        state.Update(state.Chunk, state.Kind, state.PriorityScore, state.Reason, token);
        _collisionQueueStates[state.Key] = state;
        _collisionQueue.Enqueue(new CollisionQueueEntry(state.Key, token), ComposeCollisionWorkPriority(state.Kind, state.PriorityScore, token));
    }

    private float ComputeVisualBuildPriority(TerrainChunk chunk, TerrainVisualBuildRequestKind requestKind)
    {
        float distance = ComputeTrackedChunkDistance(chunk);
        return requestKind switch
        {
            TerrainVisualBuildRequestKind.Edit => 5000.0f - distance,
            TerrainVisualBuildRequestKind.InitialCoarse => 3200.0f - distance,
            _ => HasUrgentStickyDetailDemand(chunk)
                ? 2400.0f - distance
                : 1400.0f - distance
        };
    }

    private TerrainVisualBuildQueueClass ClassifyVisualBuildQueue(TerrainChunk chunk, TerrainVisualBuildRequestKind requestKind)
    {
        if (requestKind == TerrainVisualBuildRequestKind.Edit)
        {
            return TerrainVisualBuildQueueClass.Critical;
        }

        float distance = ComputeTrackedChunkDistance(chunk);
        float nearDetailRange = Mathf.Max(
            PlayerDetailRequestRadius * 1.15f,
            _settings.ChunkSize * (GuaranteedColumnRadius + 0.75f));
        float immediateVisibilityRange = GetImmediateCoarseVisibilityRange();
        if (requestKind == TerrainVisualBuildRequestKind.InitialCoarse)
        {
            // A chunk's very first coarse shell must not be demoted behind sticky-detail
            // demand, or startup can strand dirty chunks with no foreground mesh work.
            if (!_initialLoadComplete)
            {
                return TerrainVisualBuildQueueClass.NearCoarse;
            }

            return distance <= immediateVisibilityRange
                ? TerrainVisualBuildQueueClass.NearCoarse
                : TerrainVisualBuildQueueClass.Background;
        }

        if ((IsVisualMeshPressureActive() || ShouldPrioritizeStartupCoarseShell()) &&
            !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
        {
            return TerrainVisualBuildQueueClass.Background;
        }

        if (HasUrgentStickyDetailDemand(chunk))
        {
            return distance <= nearDetailRange
                ? TerrainVisualBuildQueueClass.Critical
                : TerrainVisualBuildQueueClass.Background;
        }

        return distance <= nearDetailRange
            ? TerrainVisualBuildQueueClass.Critical
            : TerrainVisualBuildQueueClass.Background;
    }

    private bool ShouldBypassVisualMeshBackpressure(
        TerrainChunk chunk,
        TerrainVisualBuildRequestKind requestKind,
        TerrainVisualBuildQueueClass queueClass)
    {
        if (queueClass == TerrainVisualBuildQueueClass.Critical)
        {
            return true;
        }

        if (queueClass != TerrainVisualBuildQueueClass.NearCoarse || requestKind != TerrainVisualBuildRequestKind.InitialCoarse)
        {
            return false;
        }

        if (_trackedCharacter == null || !IsInstanceValid(_trackedCharacter))
        {
            return true;
        }

        return ComputeTrackedChunkDistance(chunk) <= GetImmediateCoarseVisibilityRange();
    }

    private float ComputeCollisionBuildPriority(TerrainChunk chunk, TerrainCollisionRequestKind requestKind)
    {
        float distance = ComputeTrackedChunkDistance(chunk);
        return requestKind switch
        {
            TerrainCollisionRequestKind.Edit => 2600.0f - distance,
            _ => 900.0f - distance
        };
    }

    private float ComputeTrackedChunkDistance(TerrainChunk chunk)
    {
        if (_trackedCharacter == null || !IsInstanceValid(_trackedCharacter))
        {
            return 0.0f;
        }

        return DistanceToChunkBounds(chunk, _trackedCharacter.GlobalPosition);
    }

    private bool ShouldDeferAutomaticDetailPromotion(
        TerrainChunk chunk,
        ulong currentFrame,
        double nowSeconds,
        bool pressureModeActive,
        bool startupCoarsePriorityActive,
        out TerrainDetailPromotionDeferDecision decision)
    {
        ulong warmupFrame = 0;
        if (chunk.ActivatedFrame != ulong.MaxValue && currentFrame == chunk.ActivatedFrame)
        {
            warmupFrame = currentFrame + 1;
        }
        else if (chunk.ActivatedFrame != ulong.MaxValue &&
                 currentFrame > chunk.ActivatedFrame &&
                 (currentFrame - chunk.ActivatedFrame) <= (ulong)Mathf.Max(ChunkDetailWarmupFrames, 0))
        {
            warmupFrame = chunk.ActivatedFrame + (ulong)Mathf.Max(ChunkDetailWarmupFrames, 0) + 1UL;
        }

        double warmupTime = double.NegativeInfinity;
        if (ChunkDetailWarmupSeconds > 0.0f &&
            chunk.ActivatedAtSeconds > double.NegativeInfinity &&
            (nowSeconds - chunk.ActivatedAtSeconds) < ChunkDetailWarmupSeconds)
        {
            warmupTime = chunk.ActivatedAtSeconds + ChunkDetailWarmupSeconds;
        }

        if (warmupFrame > 0 || warmupTime > double.NegativeInfinity)
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredWarmup,
                "activation_warmup",
                warmupFrame,
                warmupTime);
            return true;
        }

        if (_meshBuildScheduler.HasPendingWork(chunk.ChunkKey) || chunk.RenderDirty || !chunk.HasCompletedInitialVisualBuild)
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPendingMesh,
                "pending_mesh_build",
                0,
                double.NegativeInfinity);
            return true;
        }

        if (DetailRequestCooldownSeconds > 0.0f &&
            chunk.LastVisualCommitAtSeconds > double.NegativeInfinity &&
            (nowSeconds - chunk.LastVisualCommitAtSeconds) < DetailRequestCooldownSeconds)
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredCooldown,
                "cooldown",
                0,
                chunk.LastVisualCommitAtSeconds + DetailRequestCooldownSeconds);
            return true;
        }

        if (ShouldAwaitCollisionBootstrapBeforeAutomaticDetail(chunk))
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredCollisionBootstrap,
                "collision_bootstrap_pending",
                0,
                double.NegativeInfinity);
            return true;
        }

        if (startupCoarsePriorityActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredStartupPriority,
                "startup_coarse_priority",
                0,
                double.NegativeInfinity);
            return true;
        }

        if (pressureModeActive && !ShouldPrioritizeAutomaticDetailDuringPressure(chunk))
        {
            decision = new TerrainDetailPromotionDeferDecision(
                TerrainDetailPromotionState.DeferredPressure,
                "pressure_mode",
                0,
                double.NegativeInfinity);
            return true;
        }

        decision = default;
        return false;
    }

    private static bool IsAutomaticDetailPromotionCandidate(TerrainChunk chunk, bool requestChanged)
    {
        return requestChanged || chunk.DirtyDetailRegionCount > 0 || !chunk.HasDetailBrick;
    }

    private void RecordDeferredDetailPromotion(TerrainChunk chunk, string reason)
    {
        _lastDeferredDetailPromotionCount++;
        _totalDeferredDetailPromotionCount++;
        if (_terrainStats.RecordDeferredDetailPromotion(chunk.ChunkKey, reason))
        {
            _lastSuppressedDeferredLogRepeatCount++;
            _totalSuppressedDeferredLogRepeatCount++;
        }
    }

    private void RecordDeferredPromotionReevaluation()
    {
        _lastDeferredPromotionReevaluationCount++;
        _totalDeferredPromotionReevaluationCount++;
    }

    private void RecordAvoidedDeferredReevaluation()
    {
        _lastAvoidedDeferredReevaluationCount++;
        _totalAvoidedDeferredReevaluationCount++;
    }

    private void RecordRequestReactivatedByMeshCompletion()
    {
        _lastRequestsReactivatedByMeshCompletionCount++;
        _requestsReactivatedByMeshCompletionCount++;
    }

    private void RecordRequestReactivatedByCooldownExpiry()
    {
        _lastRequestsReactivatedByCooldownExpiryCount++;
        _requestsReactivatedByCooldownExpiryCount++;
    }

    private void RecordRequestReactivatedByPressureExit()
    {
        _lastRequestsReactivatedByPressureExitCount++;
        _requestsReactivatedByPressureExitCount++;
    }

    private void RecordCoalescedRebuildRequest(Vector3I key, string queue, string reason)
    {
        _lastCoalescedRebuildRequestCount++;
        _totalCoalescedRebuildRequestCount++;
        _terrainStats.RecordCoalescedRebuildRequest(key, queue, reason);
    }

    private void RecordSkippedLowPriorityBuild(Vector3I key, string queue, string reason)
    {
        _lastSkippedLowPriorityBuildCount++;
        _terrainStats.RecordSkippedLowPriorityBuild(key, queue, reason);
    }

    private void RecordSuppressedDuplicateBuild(Vector3I key, string queue, string reason)
    {
        _lastSuppressedDuplicateBuildCount++;
        _terrainStats.RecordSuppressedRebuildRequest(key, queue, reason);
    }

    private bool ShouldThrottleNearPlayerCollision()
    {
        return
            IsVisualMeshPressureActive() ||
            _meshBuildScheduler.HasHighPriorityDemand ||
            _stagedHighPriorityVisualEnqueueStates.Count > 0;
    }

    private bool IsVisualMeshPressureActive()
    {
        int queueDepth =
            _meshBuildScheduler.QueuedCount +
            _meshBuildScheduler.HighPriorityRunningCount +
            _stagedHighPriorityVisualEnqueueStates.Count;
        int highPriorityPressureDepth = GetHighPriorityPressureDepth();
        return
            highPriorityPressureDepth >= Mathf.Max(1, HighPriorityQueueSoftLimit) ||
            queueDepth >= Mathf.Max(1, QueueDepthPressureThreshold) ||
            (queueDepth > 0 && _meshBuildScheduler.LastQueueWaitMs >= Math.Max(0.0, QueueWaitPressureMs));
    }

    private void UpdatePressureModeTelemetry()
    {
        bool active = IsVisualMeshPressureActive();
        if (active)
        {
            _pressureModeActiveFrameCount++;
            if (!_pressureModeActive)
            {
                _pressureModeActivationCount++;
            }
        }

        _pressureModeActive = active;
    }

    private void RefreshVisualMeshSchedulerState()
    {
        _meshBuildScheduler.SetActiveQueueLimit(GetCurrentVisualMeshBackpressureBudget());
        _meshBuildScheduler.SetLowPriorityLimits(GetCurrentLowPriorityActiveMeshQueueBudget(), MaxDeferredLowPriorityBuilds);
    }

    private static bool HasStickyDetailDemand(TerrainChunk chunk)
    {
        if (chunk == null)
        {
            return false;
        }

        if (chunk.HasEditedDetailBrick)
        {
            return true;
        }

        foreach (TerrainDetailRegion region in chunk.DetailRegionManager.Regions)
        {
            if (region.Sticky)
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldPrioritizeStartupCoarseShell()
    {
        if (_initialLoadComplete)
        {
            return false;
        }

        if (IsVisualMeshPressureActive())
        {
            return true;
        }

        if (_meshBuildScheduler.HasForegroundCoarseDemand || HasStagedForegroundCoarseDemand())
        {
            return true;
        }

        int pendingLoads =
            _loadScheduler.PendingLoadCount +
            _loadScheduler.RunningLoadCount +
            _loadScheduler.PreparedCount;
        return pendingLoads > Mathf.Max(24, MaxChunkGenerationJobs * 8);
    }

    private bool ShouldAttemptDeferredVisualRetry(TerrainVisualBuildQueueClass queueClass)
    {
        if (queueClass != TerrainVisualBuildQueueClass.Background)
        {
            return true;
        }

        if (IsVisualMeshPressureActive() || _meshBuildScheduler.HasHighPriorityDemand || _stagedHighPriorityVisualEnqueueStates.Count > 0)
        {
            return false;
        }

        return _meshBuildScheduler.LowPriorityQueueDepth < Mathf.Max(1, MaxDeferredLowPriorityBuilds);
    }

    private void TrimStaleLowPriorityMeshRequests()
    {
        if (!IsVisualMeshPressureActive() &&
            !_meshBuildScheduler.HasHighPriorityDemand &&
            _stagedHighPriorityVisualEnqueueStates.Count == 0)
        {
            return;
        }

        double maxQueuedLowPriorityWaitMs = Math.Max(500.0, QueueWaitPressureMs * 2.0);
        int trimmed = _meshBuildScheduler.TrimStaleBackgroundRequests(maxQueuedLowPriorityWaitMs);
        if (trimmed > 0)
        {
            _lastSkippedLowPriorityBuildCount += trimmed;
        }
    }

    private float GetImmediateCoarseVisibilityRange()
    {
        return Mathf.Max(
            PlayerDetailRequestRadius,
            _settings.ChunkSize * Mathf.Max(GuaranteedColumnRadius, 1.1f));
    }

    private bool ShouldEnsureCollision(TerrainChunk chunk)
    {
        if (_trackedCharacter == null || !IsInstanceValid(_trackedCharacter))
        {
            return true;
        }

        float activationRadius = Mathf.Max(
            PlayerDetailRequestRadius,
            _settings.ChunkSize * (GuaranteedColumnRadius + 0.75f));
        return DistanceToChunkBounds(chunk, _trackedCharacter.GlobalPosition) <= activationRadius;
    }

    private bool ShouldAwaitCollisionBootstrapBeforeAutomaticDetail(TerrainChunk chunk)
    {
        if (chunk == null ||
            !IsInstanceValid(chunk) ||
            !chunk.HasSurface ||
            chunk.HasCollision)
        {
            return false;
        }

        return ShouldEnsureCollision(chunk);
    }

    private bool IsChunkInitialLoadReady(TerrainChunk chunk)
    {
        // Startup progress should unblock once the chunk has a committed coarse visual.
        // Follow-up detail rebuilds can legitimately keep RenderDirty true for a while,
        // but they should not pin the loading overlay.
        if (!chunk.HasCompletedInitialVisualBuild)
        {
            return false;
        }

        if (!chunk.HasSurface)
        {
            return true;
        }

        return !ShouldEnsureCollision(chunk) || chunk.HasCollision;
    }

    private double GetNowSeconds()
    {
        return Time.GetTicksMsec() / 1000.0;
    }

    private ITerrainMeshBackend CreateMeshBackend()
    {
        if (!UseExperimentalComputeMeshing)
        {
            return new TerrainCpuMeshBackend();
        }

        if (!TerrainComputeMeshBackend.CanUseCurrentRenderer())
        {
            GD.PushWarning("Experimental compute terrain meshing requires the Forward+ or Mobile renderers. Falling back to the async CPU backend.");
        }
        else
        {
            GD.PushWarning("Experimental compute terrain meshing is a stub on this branch. Falling back to the async CPU backend.");
        }

        return new TerrainCpuMeshBackend();
    }

    private TerrainChunk GetOrCreateChunkForEdit(Vector3I key)
    {
        if (_residentChunks.TryGetValue(key, out TerrainChunk existingChunk))
        {
            return existingChunk;
        }

        ulong start = Time.GetTicksUsec();
        ChunkAcquisitionResult acquired = _cacheManager.AcquireChunk(key, _useStartupSnapshot, GenerateChunkData);
        double loadMs = (Time.GetTicksUsec() - start) / 1000.0;
        RegisterChunkLoadStats(key, acquired.Source, loadMs, "edit");
        TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
        AddChild(chunk);
        chunk.Initialize(key, _settings);
        ApplyChunkVisualConfiguration(chunk);
        chunk.SetBiomeSample(GetBiomeForChunk(key), EnableBiomeDebugTint);
        chunk.SetStructureMetadata(GetStructureInfluenceForChunk(key));
        chunk.SetData(acquired.Data, acquired.Source);
        chunk.NotifyActivated(Engine.GetProcessFrames(), GetNowSeconds());
        chunk.Visible = _desiredChunks.Contains(key);
        chunk.ProcessMode = chunk.Visible ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        _residentChunks[key] = chunk;
        InvalidatePriorityMetadataCaches(key);
        _lastChunkSourceSummary = BuildChunkSourceSummary(key, acquired.Source);
        chunk.MarkDirty(includeCollision: false, collisionDelaySeconds: 0.0);
        QueueChunkForRebuild(chunk, TerrainVisualBuildRequestKind.InitialCoarse, TerrainMeshDetailMode.CoarseOnly, "edit_attach");
        return chunk;
    }

    private void ResetSearchEvaluationCaches()
    {
        _columnSurfaceMaxYCache.Clear();
        _visibilityHeuristicCache.Clear();
        _columnPriorityMetadataCache.Clear();
        _chunkPriorityMetadataCache.Clear();
        _visibilityCacheBucket = new VisibilityCacheBucket(int.MinValue, int.MinValue, int.MinValue);
        _priorityMetadataCacheUsesStartupSnapshot = _useStartupSnapshot;
    }

    private void RefreshSearchEvaluationCaches(Vector3 cameraPosition, bool invalidated)
    {
        RefreshPriorityMetadataCacheState();

        VisibilityCacheBucket bucket = BuildVisibilityCacheBucket(cameraPosition);
        if (invalidated || !_visibilityCacheBucket.Equals(bucket))
        {
            _visibilityHeuristicCache.Clear();
            _visibilityCacheBucket = bucket;
        }
    }

    private VisibilityCacheBucket BuildVisibilityCacheBucket(Vector3 cameraPosition)
    {
        float horizontalBucketSize = Mathf.Max(
            VoxelSize * 8.0f,
            _settings.ChunkSize * VisibilityCacheHorizontalBucketSizeChunks);
        float verticalBucketSize = Mathf.Max(
            VoxelSize * 8.0f,
            _settings.ChunkSize * VisibilityCacheVerticalBucketSizeChunks);
        return new VisibilityCacheBucket(
            Mathf.FloorToInt(cameraPosition.X / horizontalBucketSize),
            Mathf.FloorToInt(cameraPosition.Y / verticalBucketSize),
            Mathf.FloorToInt(cameraPosition.Z / horizontalBucketSize));
    }

    private void RefreshPriorityMetadataCacheState()
    {
        if (_priorityMetadataCacheUsesStartupSnapshot == _useStartupSnapshot)
        {
            return;
        }

        _priorityMetadataCacheUsesStartupSnapshot = _useStartupSnapshot;
        _columnPriorityMetadataCache.Clear();
        _chunkPriorityMetadataCache.Clear();
    }

    private void InvalidatePriorityMetadataCaches(Vector3I key)
    {
        Vector2I columnKey = new(key.X, key.Z);
        _columnPriorityMetadataCache.Remove(columnKey);
        for (int y = 0; y < VerticalChunkCount; y++)
        {
            _chunkPriorityMetadataCache.Remove(new Vector3I(key.X, y, key.Z));
        }
    }

    private CachedColumnPriorityMetadata GetCachedColumnPriorityMetadata(Vector2I columnKey)
    {
        RefreshPriorityMetadataCacheState();
        if (_columnPriorityMetadataCache.TryGetValue(columnKey, out CachedColumnPriorityMetadata cached))
        {
            return cached;
        }

        if (_columnPriorityMetadataCache.Count >= MaxCachedColumnPriorityEntries)
        {
            _columnPriorityMetadataCache.Clear();
        }

        Vector3I representativeChunkKey = new(columnKey.X, 0, columnKey.Y);
        TerrainChunkStructureMetadata structureMetadata = GetStructureInfluenceForChunk(representativeChunkKey);
        TerrainChunkLoadSource estimatedSource = EstimateColumnSource(columnKey);
        cached = new CachedColumnPriorityMetadata(
            _biomeClassifier.SampleColumn(columnKey, _settings).DominantBiome,
            structureMetadata.StructureCount,
            structureMetadata.DominantStructureType,
            structureMetadata.RequestHigherTerrainDetail,
            ComputeColumnLoadCostBonus(columnKey),
            estimatedSource);
        _columnPriorityMetadataCache[columnKey] = cached;
        return cached;
    }

    private CachedChunkPriorityMetadata GetCachedChunkPriorityMetadata(Vector3I key)
    {
        RefreshPriorityMetadataCacheState();
        if (_chunkPriorityMetadataCache.TryGetValue(key, out CachedChunkPriorityMetadata cached))
        {
            return cached;
        }

        if (_chunkPriorityMetadataCache.Count >= MaxCachedChunkPriorityEntries)
        {
            _chunkPriorityMetadataCache.Clear();
        }

        TerrainChunkStructureMetadata structureMetadata = GetStructureInfluenceForChunk(key);
        TerrainChunkLoadSource estimatedSource = _cacheManager.EstimateSource(key, _useStartupSnapshot);
        cached = new CachedChunkPriorityMetadata(
            GetBiomeForChunk(key).DominantBiome,
            structureMetadata.StructureCount,
            structureMetadata.DominantStructureType,
            structureMetadata.RequestHigherTerrainDetail,
            GetLoadCostBonus(estimatedSource),
            estimatedSource);
        _chunkPriorityMetadataCache[key] = cached;
        return cached;
    }

    private static int BuildPreviewMaxDetailLevel(int structureCount, bool requestsHigherTerrainDetail)
    {
        if (structureCount <= 0)
        {
            return 0;
        }

        return requestsHigherTerrainDetail ? 2 : 1;
    }

    private bool CanTriggerSoftSearchInvalidation(double nowSeconds)
    {
        return
            double.IsNegativeInfinity(_lastSearchInvalidationAtSeconds) ||
            nowSeconds - _lastSearchInvalidationAtSeconds >= SoftSearchInvalidationCooldownSeconds;
    }

    private void RecordTerrainEditForSearch(Vector3I key)
    {
        _columnSurfaceMaxYCache.Remove(new Vector2I(key.X, key.Z));
        InvalidatePriorityMetadataCaches(key);

        if (_desiredChunks.Count == 0 ||
            !_desiredChunks.Contains(key) ||
            !IsChunkInNearPlayerCoverageSafetyZone(key))
        {
            _terrainDesirabilityDirty = true;
        }
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

    private float SampleTerrainDensity(Vector3 worldPosition)
    {
        return _prioritySampler.SampleDensity(worldPosition);
    }

    private ColumnPriorityInfo EvaluateColumnPriority(Vector2I columnKey)
    {
        ulong start = Time.GetTicksUsec();
        Vector3I representativeChunkKey = new(columnKey.X, 0, columnKey.Y);
        CachedColumnPriorityMetadata cached = GetCachedColumnPriorityMetadata(columnKey);
        TerrainChunk residentChunk = _residentChunks.GetValueOrDefault(representativeChunkKey);
        int detailRegionCount = residentChunk?.DetailRegionCount ?? cached.StructureCount;
        int maxDetailLevel = residentChunk?.MaxRequestedDetailLevel ?? BuildPreviewMaxDetailLevel(cached.StructureCount, cached.RequestsHigherTerrainDetail);

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
        float loadCostBonus = cached.LoadCostBonus;

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
            cached.EstimatedSource,
            cached.DominantBiome,
            cached.StructureCount,
            cached.DominantStructureType,
            cached.RequestsHigherTerrainDetail,
            detailRegionCount,
            maxDetailLevel,
            guaranteed);
    }

    private ChunkPriorityInfo EvaluateChunkPriority(Vector3I key)
    {
        ulong start = Time.GetTicksUsec();
        TerrainChunk residentChunk = _residentChunks.GetValueOrDefault(key);
        CachedChunkPriorityMetadata cached = residentChunk == null
            ? GetCachedChunkPriorityMetadata(key)
            : default;
        BiomeId dominantBiome = residentChunk?.BiomeSample.DominantBiome ?? cached.DominantBiome;
        int structureCount = residentChunk?.StructureMetadata.StructureCount ?? cached.StructureCount;
        TerrainStructureType dominantStructureType = residentChunk?.StructureMetadata.DominantStructureType ?? cached.DominantStructureType;
        bool requestsHigherTerrainDetail = residentChunk?.StructureMetadata.RequestHigherTerrainDetail ?? cached.RequestsHigherTerrainDetail;
        int detailRegionCount = residentChunk?.DetailRegionCount ?? cached.StructureCount;
        int maxDetailLevel = residentChunk?.MaxRequestedDetailLevel ?? BuildPreviewMaxDetailLevel(cached.StructureCount, cached.RequestsHigherTerrainDetail);

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
        TerrainChunkLoadSource estimatedSource = residentChunk?.LoadSource ?? cached.EstimatedSource;
        float loadCostBonus = residentChunk == null
            ? cached.LoadCostBonus
            : GetLoadCostBonus(estimatedSource);
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
            dominantBiome,
            structureCount,
            dominantStructureType,
            requestsHigherTerrainDetail,
            detailRegionCount,
            maxDetailLevel,
            guaranteed);
    }

    private ChunkReleaseInfo BuildReleaseInfo(Vector3I key)
    {
        ChunkPriorityInfo retain = EvaluateChunkPriority(key);
        TerrainChunkLoadSource source = _residentChunks.TryGetValue(key, out TerrainChunk chunk)
            ? chunk.LoadSource
            : TerrainChunkLoadSource.Resident;
        float retainScore = retain.TotalScore;
        string reason = $"not desired | {retain.Summary}";
        if (chunk != null && IsInstanceValid(chunk))
        {
            if (!chunk.HasSurface)
            {
                retainScore -= 260.0f;
                reason = $"not desired | no_surface | {retain.Summary}";
            }
            else if (!CanChunkProvideCoverage(chunk))
            {
                retainScore -= 120.0f;
                reason = $"not desired | weak_coverage tris={chunk.LastTotalTriangleCount} | {retain.Summary}";
            }
        }

        if (!CanVerticalChunkContainSurface(key))
        {
            retainScore -= 320.0f;
            reason = $"not desired | empty_vertical | {retain.Summary}";
        }

        return new ChunkReleaseInfo(
            key,
            retainScore,
            reason,
            source,
            retain.DominantBiome,
            retain.StructureCount,
            retain.DominantStructureType,
            retain.RequestsHigherTerrainDetail,
            retain.DetailRegionCount,
            retain.MaxDetailLevel);
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
        _lastMeshWorkerBuildCount = 0;
        _lastMeshWorkerBuildMs = 0.0;
        _lastDeferredDetailPromotionCount = 0;
        _lastDeferredPromotionReevaluationCount = 0;
        _lastAvoidedDeferredReevaluationCount = 0;
        _lastSuppressedDeferredLogRepeatCount = 0;
        _lastRequestsReactivatedByMeshCompletionCount = 0;
        _lastRequestsReactivatedByCooldownExpiryCount = 0;
        _lastRequestsReactivatedByPressureExitCount = 0;
        _lastCoalescedRebuildRequestCount = 0;
        _lastSkippedLowPriorityBuildCount = 0;
        _lastSuppressedDuplicateBuildCount = 0;
        _lastHighPriorityEnqueueBudgetHitCount = 0;
        _lastDeferredHighPriorityEnqueueCount = 0;
        _lastSmoothedHighPriorityEnqueueCount = 0;
        _lastPreventedCoverageGapReleaseCount = 0;
        _lastReplacementCoverageWaitCount = 0;
        _lastChunksHeldForCoverageSafetyCount = 0;
        _lastNormalDebugMismatchCount = 0;
        _lastTangentGenerationCount = 0;
        _lastVertexTintEnabledFrameCount = 0;
    }

    private void LoadStartupState()
    {
        if (_trackedCharacter == null)
        {
            return;
        }

        Stopwatch startupLoadStopwatch = _terrainStats.Enabled
            ? Stopwatch.StartNew()
            : null;
        bool loaded = _chunkStore.TryLoadStartupState(out TerrainStartupState startupState);
        if (_terrainStats.Enabled)
        {
            _terrainStats.RecordPersistenceLoad(
                "startup_state",
                startupLoadStopwatch?.Elapsed.TotalMilliseconds ?? 0.0,
                loaded,
                loaded ? startupState.Chunks.Count : 0);
        }

        if (!loaded)
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
        ResetSearchEvaluationCaches();
    }

    private bool IsStartupBoostActive()
    {
        return _useStartupSnapshot && !_initialLoadComplete;
    }

    private int GetCurrentSearchBudget()
    {
        int searchBudget = IsStartupBoostActive()
            ? Mathf.Max(MaxDesiredSearchStepsPerFrame, StartupDesiredSearchStepsPerFrame)
            : MaxDesiredSearchStepsPerFrame;
        if (!IsStartupBoostActive() && HasForegroundStreamingCatchupWork())
        {
            searchBudget = Mathf.Min(searchBudget, Mathf.Max(1, ForegroundCatchupSearchStepsPerFrame));
        }

        return Mathf.Max(1, searchBudget);
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

    private float GetCurrentActivationTimeBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxActivationMainThreadBudgetMs, StartupActivationMainThreadBudgetMs)
            : Mathf.Max(0.0f, MaxActivationMainThreadBudgetMs);
    }

    private int GetCurrentReleaseBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxChunkReleasesPerFrame, StartupChunkReleasesPerFrame)
            : MaxChunkReleasesPerFrame;
    }

    private int GetCurrentVisualMeshWorkerBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxVisualMeshWorkerJobs, StartupVisualMeshWorkerJobs)
            : MaxVisualMeshWorkerJobs;
    }

    private int GetCurrentEditVisualMeshWorkerBudget()
    {
        return Mathf.Clamp(MaxEditVisualMeshWorkerJobs, 1, Mathf.Max(1, GetCurrentVisualMeshWorkerBudget()));
    }

    private int GetCurrentCoarseVisualMeshWorkerBudget()
    {
        int totalBudget = Mathf.Max(1, GetCurrentVisualMeshWorkerBudget());
        int reservedDetailWorkers = GetReservedCriticalDetailWorkerBudget(totalBudget);
        if (ShouldPrioritizeStartupCoarseShell())
        {
            return Mathf.Max(1, totalBudget - reservedDetailWorkers);
        }

        int configured = IsStartupBoostActive()
            ? Mathf.Max(MaxCoarseVisualMeshWorkerJobs, StartupCoarseVisualMeshWorkerJobs)
            : MaxCoarseVisualMeshWorkerJobs;
        int coarseCap = Mathf.Max(1, totalBudget - reservedDetailWorkers);
        return Mathf.Clamp(configured, 0, coarseCap);
    }

    private int GetCurrentDetailVisualMeshWorkerBudget()
    {
        if (ShouldPrioritizeStartupCoarseShell())
        {
            return GetReservedCriticalDetailWorkerBudget(Mathf.Max(1, GetCurrentVisualMeshWorkerBudget()));
        }

        return Mathf.Clamp(MaxDetailVisualMeshWorkerJobs, 0, Mathf.Max(1, GetCurrentVisualMeshWorkerBudget()));
    }

    private int GetCurrentBackgroundVisualMeshWorkerBudget()
    {
        if (IsVisualMeshPressureActive() ||
            _meshBuildScheduler.HasHighPriorityDemand ||
            _stagedHighPriorityVisualEnqueueStates.Count > 0)
        {
            return 0;
        }

        return Mathf.Clamp(MaxBackgroundVisualMeshWorkerJobs, 0, Mathf.Max(1, GetCurrentVisualMeshWorkerBudget()));
    }

    private int GetCurrentLowPriorityActiveMeshQueueBudget()
    {
        if (IsVisualMeshPressureActive() ||
            _meshBuildScheduler.HasHighPriorityDemand ||
            _stagedHighPriorityVisualEnqueueStates.Count > 0)
        {
            return 0;
        }

        return Mathf.Max(0, MaxBackgroundVisualMeshWorkerJobs);
    }

    private int GetCurrentVisualMeshBackpressureBudget()
    {
        return Mathf.Max(4, GetCurrentVisualMeshWorkerBudget() * 2);
    }

    private int GetReservedCriticalDetailWorkerBudget(int totalBudget)
    {
        if (totalBudget <= 1 ||
            (!_meshBuildScheduler.HasCriticalDetailDemand && !HasStagedCriticalDetailDemand()))
        {
            return 0;
        }

        return 1;
    }

    private bool HasStagedForegroundCoarseDemand()
    {
        foreach (StagedVisualEnqueueState state in _stagedHighPriorityVisualEnqueueStates.Values)
        {
            if (state.Request.Kind == TerrainVisualBuildRequestKind.InitialCoarse)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasStagedCriticalDetailDemand()
    {
        foreach (StagedVisualEnqueueState state in _stagedHighPriorityVisualEnqueueStates.Values)
        {
            if (state.Request.Kind == TerrainVisualBuildRequestKind.DetailPromotion &&
                state.Request.QueueClass == TerrainVisualBuildQueueClass.Critical)
            {
                return true;
            }
        }

        return false;
    }

    private int GetCurrentVisualRebuildBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxVisualChunkRebuildsPerFrame, StartupVisualChunkRebuildsPerFrame)
            : MaxVisualChunkRebuildsPerFrame;
    }

    private float GetCurrentVisualCommitTimeBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxVisualCommitMainThreadBudgetMs, StartupVisualCommitMainThreadBudgetMs)
            : Mathf.Max(0.0f, MaxVisualCommitMainThreadBudgetMs);
    }

    private int GetCurrentCollisionRebuildBudget()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxCollisionChunkRebuildsPerFrame, StartupCollisionChunkRebuildsPerFrame)
            : MaxCollisionChunkRebuildsPerFrame;
    }

    private float GetCurrentCollisionTimeBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MaxCollisionMainThreadBudgetMs, StartupCollisionMainThreadBudgetMs)
            : Mathf.Max(0.0f, MaxCollisionMainThreadBudgetMs);
    }

    private bool HasForegroundStreamingCatchupWork()
    {
        return _loadScheduler.PreparedCount > 0 ||
            _pendingMeshCommits.Count > 0 ||
            _meshBuildScheduler.HasForegroundCoarseDemand ||
            _collisionQueueStates.Count > 0;
    }

    private static bool HasExceededMainThreadBudget(ulong startUsec, float budgetMs)
    {
        if (budgetMs <= 0.0f)
        {
            return false;
        }

        return ((Time.GetTicksUsec() - startUsec) / 1000.0) >= budgetMs;
    }

    private void HandleTreeExiting()
    {
        if (!EnableStartupStatePersistence || _trackedCharacter == null)
        {
            _terrainStats?.Close();
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
        if (_terrainStats.Enabled)
        {
            Stopwatch saveStopwatch = Stopwatch.StartNew();
            _chunkStore.SaveStartupState(_trackedCharacter.GlobalPosition, startupChunks);
            _terrainStats.RecordPersistenceSave(
                "startup_state",
                saveStopwatch.Elapsed.TotalMilliseconds,
                startupChunks.Count);
            _terrainStats.Close();
            return;
        }

        _chunkStore.SaveStartupState(_trackedCharacter.GlobalPosition, startupChunks);
        _terrainStats?.Close();
    }

    private void LogStreamingTuningSummary()
    {
        int estimatedResidentChunks = Mathf.Max(MaxActiveColumns, 1) * Mathf.Max(VerticalChunkCount, 1);
        int estimatedRamBudget = Mathf.Max(0, MaxLoadedChunks - estimatedResidentChunks);
        GD.Print(
            $"Terrain streaming tuning | desired cols {MaxActiveColumns} | est resident chunks {estimatedResidentChunks} | loaded cap {MaxLoadedChunks} | est ram cache {estimatedRamBudget} | search {MaxDesiredSearchStepsPerFrame}/{StartupDesiredSearchStepsPerFrame} catchup {ForegroundCatchupSearchStepsPerFrame} | mesh worker total {MaxVisualMeshWorkerJobs}/{StartupVisualMeshWorkerJobs} edit/coarse/detail/bg {MaxEditVisualMeshWorkerJobs}/{MaxCoarseVisualMeshWorkerJobs}/{MaxDetailVisualMeshWorkerJobs}/{MaxBackgroundVisualMeshWorkerJobs} startup_coarse {StartupCoarseVisualMeshWorkerJobs} | backpressure {GetCurrentVisualMeshBackpressureBudget()} depth pressure {QueueDepthPressureThreshold} wait pressure {QueueWaitPressureMs:0}ms hi-enqueue {MaxHighPriorityMeshEnqueuesPerFrame}/{MaxNearCoarseMeshEnqueuesPerFrame}/{MaxDetailPromotionActivationsPerFrame} hi-soft/hard {HighPriorityQueueSoftLimit}/{HighPriorityQueueHardLimit} low-pri defer {MaxDeferredLowPriorityBuilds} | main-thread activation {MaxActivationMainThreadBudgetMs:0.0}/{StartupActivationMainThreadBudgetMs:0.0}ms commit {MaxVisualCommitMainThreadBudgetMs:0.0}/{StartupVisualCommitMainThreadBudgetMs:0.0}ms collision {MaxCollisionMainThreadBudgetMs:0.0}/{StartupCollisionMainThreadBudgetMs:0.0}ms | mesh commit {MaxVisualChunkRebuildsPerFrame}/{StartupVisualChunkRebuildsPerFrame} | detail warmup {ChunkDetailWarmupFrames}f/{ChunkDetailWarmupSeconds:0.00}s cooldown {DetailRequestCooldownSeconds:0.00}s promos {MaxDetailPromotionsPerFrame} | tint {(EnableTerrainVertexTint ? "on" : "off")} debug {ResolveTerrainDebugView()} tangents {(TerrainGenerateTangents ? "requested" : "off")} | retain {RetentionPriorityWeight:0.0}/{RetentionDecayFactor:0.00} | shoulder {ShoulderHalfAngleDegrees:0.#}deg {ShoulderDistanceMultiplier:0.00}x {ShoulderPriorityMultiplier:0.00}x");
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

    private bool RequestDetailOnChunk(
        TerrainChunk chunk,
        Aabb localBounds,
        int detailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky,
        string requestId = "")
    {
        if (!chunk.RequestDetail(localBounds, detailLevel, source, reason, priority, sticky, requestId))
        {
            return false;
        }

        TerrainDetailRegion latestRegion = null;
        System.Collections.Generic.IReadOnlyList<TerrainDetailRegion> regions = chunk.DetailRegionManager.Regions;
        foreach (TerrainDetailRegion region in regions)
        {
            if (region.Source != source)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(requestId) &&
                !string.Equals(region.Id, requestId, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(reason) &&
                !string.Equals(region.Reason, reason, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (!region.Overlaps(localBounds))
            {
                continue;
            }

            if (latestRegion == null ||
                region.RequestedDetailLevel > latestRegion.RequestedDetailLevel ||
                (region.RequestedDetailLevel == latestRegion.RequestedDetailLevel && region.Priority >= latestRegion.Priority))
            {
                latestRegion = region;
            }
        }

        _terrainStats.LogDetailRegionRequest(chunk.ChunkKey, latestRegion);
        return true;
    }

    private Aabb SnapLocalBounds(TerrainChunk chunk, Aabb localBounds)
    {
        float snapStep = Mathf.Max(DetailRequestSnapStep, chunk.VoxelSize);
        if (snapStep <= 0.0001f)
        {
            return localBounds;
        }

        Vector3 start = localBounds.Position;
        Vector3 end = localBounds.Position + localBounds.Size;
        Vector3 snappedMin = new(
            Mathf.Floor(start.X / snapStep) * snapStep,
            Mathf.Floor(start.Y / snapStep) * snapStep,
            Mathf.Floor(start.Z / snapStep) * snapStep);
        Vector3 snappedMax = new(
            Mathf.Ceil(end.X / snapStep) * snapStep,
            Mathf.Ceil(end.Y / snapStep) * snapStep,
            Mathf.Ceil(end.Z / snapStep) * snapStep);
        Vector3 min = new(
            Mathf.Clamp(snappedMin.X, 0.0f, chunk.ChunkSize),
            Mathf.Clamp(snappedMin.Y, 0.0f, chunk.ChunkSize),
            Mathf.Clamp(snappedMin.Z, 0.0f, chunk.ChunkSize));
        Vector3 max = new(
            Mathf.Clamp(snappedMax.X, 0.0f, chunk.ChunkSize),
            Mathf.Clamp(snappedMax.Y, 0.0f, chunk.ChunkSize),
            Mathf.Clamp(snappedMax.Z, 0.0f, chunk.ChunkSize));
        return new Aabb(min, max - min);
    }

    private static float DistanceToChunkBounds(TerrainChunk chunk, Vector3 worldPosition)
    {
        Vector3 min = chunk.Position;
        Vector3 max = chunk.Position + (Vector3.One * chunk.ChunkSize);
        Vector3 clamped = new(
            Mathf.Clamp(worldPosition.X, min.X, max.X),
            Mathf.Clamp(worldPosition.Y, min.Y, max.Y),
            Mathf.Clamp(worldPosition.Z, min.Z, max.Z));
        return clamped.DistanceTo(worldPosition);
    }

    private static Aabb Union(Aabb a, Aabb b)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Min(a.Position.X, b.Position.X),
            Mathf.Min(a.Position.Y, b.Position.Y),
            Mathf.Min(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Max(aEnd.X, bEnd.X),
            Mathf.Max(aEnd.Y, bEnd.Y),
            Mathf.Max(aEnd.Z, bEnd.Z));
        return new Aabb(min, max - min);
    }

    private static double ComputeAverage(long total, long count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        return (double)total / count;
    }

    private static double ComputeAverage(double total, long count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        return total / count;
    }

    private int GetPreviewDetailRegionCount(Vector3I key, TerrainChunkStructureMetadata structureMetadata)
    {
        if (_residentChunks.TryGetValue(key, out TerrainChunk chunk))
        {
            return chunk.DetailRegionCount;
        }

        return structureMetadata.StructureCount;
    }

    private int GetPreviewMaxDetailLevel(Vector3I key, TerrainChunkStructureMetadata structureMetadata)
    {
        if (_residentChunks.TryGetValue(key, out TerrainChunk chunk))
        {
            return chunk.MaxRequestedDetailLevel;
        }

        if (!structureMetadata.IsInInfluenceZone)
        {
            return 0;
        }

        return structureMetadata.RequestHigherTerrainDetail ? 2 : 1;
    }

    private string BuildChunkSourceSummary(Vector3I key, TerrainChunkLoadSource source)
    {
        TerrainBiomeSample biomeSample = GetBiomeForChunk(key);
        TerrainChunkStructureMetadata structureMetadata = GetStructureInfluenceForChunk(key);
        int detailRegionCount = GetPreviewDetailRegionCount(key, structureMetadata);
        int maxDetailLevel = GetPreviewMaxDetailLevel(key, structureMetadata);
        bool hasDetailBrick = _residentChunks.TryGetValue(key, out TerrainChunk residentChunk) && residentChunk.HasDetailBrick;
        bool hasEditedDetail = residentChunk != null && residentChunk.HasEditedDetailBrick;
        return
            $"{key} <- {source} biome {biomeSample.DominantBiome} struct {structureMetadata.StructureCount}/{structureMetadata.DominantStructureType}/{(structureMetadata.RequestHigherTerrainDetail ? "hi" : "std")} detail {detailRegionCount}/{maxDetailLevel} detail_hi {(hasDetailBrick ? "on" : "off")} edit_hi {(hasEditedDetail ? "on" : "off")}";
    }

    private Vector3I GetChunkKeyAtWorldPosition(Vector3 worldPosition)
    {
        return new Vector3I(
            Mathf.FloorToInt(worldPosition.X / _settings.ChunkSize),
            Mathf.Clamp(Mathf.FloorToInt((worldPosition.Y - _settings.BaseY) / _settings.ChunkSize), 0, VerticalChunkCount - 1),
            Mathf.FloorToInt(worldPosition.Z / _settings.ChunkSize));
    }

    private static bool AreSetsEqual(HashSet<Vector3I> a, HashSet<Vector3I> b)
    {
        return a.Count == b.Count && a.SetEquals(b);
    }

    private int NextRebuildPriorityToken()
    {
        return ++_rebuildPrioritySequence;
    }

    private TerrainWorkPriority ComposeVisualWorkPriority(TerrainVisualBuildRequestKind requestKind, float priorityScore)
    {
        return new TerrainWorkPriority(
            requestKind switch
            {
                TerrainVisualBuildRequestKind.Edit => 0,
                TerrainVisualBuildRequestKind.InitialCoarse => 1,
                _ => 3
            },
            -priorityScore,
            NextRebuildPriorityToken());
    }

    private TerrainWorkPriority ComposeCollisionWorkPriority(
        TerrainCollisionRequestKind requestKind,
        float priorityScore,
        int token)
    {
        return new TerrainWorkPriority(
            requestKind == TerrainCollisionRequestKind.Edit ? 2 : 4,
            -priorityScore,
            token);
    }

    private enum TerrainCollisionRequestKind
    {
        Edit = 0,
        NearPlayer = 1
    }

    private readonly record struct TerrainDetailReconcileResult(bool Changed, bool PromotedTransientDetail)
    {
        public static TerrainDetailReconcileResult NoChange => new(false, false);
    }

    private readonly record struct TerrainWorkPriority(int Lane, float NegativePriorityScore, int Token)
        : System.IComparable<TerrainWorkPriority>
    {
        public int CompareTo(TerrainWorkPriority other)
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

    private readonly record struct CollisionQueueEntry(Vector3I Key, int Token);

    private sealed class CollisionQueueState
    {
        public CollisionQueueState(
            TerrainChunk chunk,
            TerrainCollisionRequestKind kind,
            float priorityScore,
            string reason,
            int token)
        {
            Key = chunk.ChunkKey;
            Chunk = chunk;
            Kind = kind;
            PriorityScore = priorityScore;
            Reason = reason;
            Token = token;
        }

        public Vector3I Key { get; }
        public TerrainChunk Chunk { get; private set; }
        public TerrainCollisionRequestKind Kind { get; private set; }
        public float PriorityScore { get; private set; }
        public string Reason { get; private set; }
        public int Token { get; private set; }

        public void Update(
            TerrainChunk chunk,
            TerrainCollisionRequestKind kind,
            float priorityScore,
            string reason,
            int token)
        {
            Chunk = chunk;
            Kind = kind;
            PriorityScore = priorityScore;
            Reason = reason;
            Token = token;
        }
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
