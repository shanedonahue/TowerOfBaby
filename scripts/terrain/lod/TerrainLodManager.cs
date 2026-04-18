using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainLodManager : Node3D
{
    private readonly record struct TerrainEditInvalidationStats(
        int IntersectedBlockCount,
        int VisibleBlockCount,
        int VisibleFinestBlockCount,
        int RequeuedBlockCount,
        int QueuedVisibleBlockCount,
        double EnqueueMs,
        double SyncWorkMs)
    {
        public string Summary =>
            $"blocks {IntersectedBlockCount} visible {VisibleBlockCount} finest {VisibleFinestBlockCount} requeued {RequeuedBlockCount} " +
            $"visible_queued {QueuedVisibleBlockCount} enqueue_ms {EnqueueMs:0.00} sync_ms {SyncWorkMs:0.00}";
    }

    private enum TerrainBlockBuildPurpose
    {
        RequestedContent = 0,
        DisplayedRefresh = 1
    }

    private const int FinestTerrainLod = 0;
    private const string TransitionLogPrefix = "[TerrainLodTransition]";
    private const string TransitionLogRelativePath = "user://profiling/terrain_lod_transition_latest.log";
    private const int MaxCreateBlocksPerFrame = 32;
    private const int MaxFieldWorkerJobs = 8;
    private const int MaxMeshWorkerJobs = 8;
    private const int MaxFieldResultAppliesPerFrame = 16;
    private const int MaxMeshResultAppliesPerFrame = 16;
    private const int MaxMeshCommitsPerFrame = 16;
    private const int MaxCollisionBuildsPerFrame = 16;
    private const int MaxReleasesPerFrame = 32;
    private const int MaxCoherentPromotionBatchSuccessors = 8;
    private static readonly (TerrainSeamFace Face, Vector3I Offset)[] SeamNeighborDirections =
    {
        (TerrainSeamFace.NegativeX, new Vector3I(-1, 0, 0)),
        (TerrainSeamFace.PositiveX, new Vector3I(1, 0, 0)),
        (TerrainSeamFace.NegativeY, new Vector3I(0, -1, 0)),
        (TerrainSeamFace.PositiveY, new Vector3I(0, 1, 0)),
        (TerrainSeamFace.NegativeZ, new Vector3I(0, 0, -1)),
        (TerrainSeamFace.PositiveZ, new Vector3I(0, 0, 1))
    };

    [Signal] public delegate void InitialLoadCompletedEventHandler();

    [ExportGroup("LOD Policy")]
    [Export(PropertyHint.Range, "3,6,1")] public int TierCount = 4;
    [Export(PropertyHint.Range, "-1,8,1")] public int Lod0NearFieldRadiusXZ = -1;
    [Export] public int[] TierSplitRadiiXZ = { 0, 4, 5 };
    [Export(PropertyHint.Range, "1,12,1")] public int CoarsestRadiusXZ = 3;
    [Export(PropertyHint.Range, "0,2,1")] public int VerticalRadius;

    [ExportGroup("Refinement Stability")]
    [Export(PropertyHint.Range, "1,8,1")] public int CollisionSafetyRadiusXZ = 4;
    [Export(PropertyHint.Range, "0.00,0.49,0.01")] public float BubbleMovePaddingFraction = 0.20f;
    [Export(PropertyHint.Range, "0.00,3.00,0.05")] public float BlockReleaseHysteresisSeconds = 0.70f;
    [Export(PropertyHint.Range, "0.00,3.00,0.05")] public float RefinedBlockReleaseExtraSeconds = 0.45f;

    [ExportGroup("Worker Scheduler")]
    [Export(PropertyHint.Range, "1,8,1")] public int FieldWorkerJobs = 2;
    [Export(PropertyHint.Range, "1,8,1")] public int MeshWorkerJobs = 2;
    [ExportGroup("Main Thread Scheduler")]
    [Export(PropertyHint.Range, "1,32,1")] public int CreateBlocksPerFrame = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int FieldResultAppliesPerFrame = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int MeshResultAppliesPerFrame = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int MeshCommitsPerFrame = 2;
    [Export(PropertyHint.Range, "1,16,1")] public int CollisionBuildsPerFrame = 1;
    [Export(PropertyHint.Range, "1,32,1")] public int ReleasesPerFrame = 2;
    [Export] public bool GenerateCollisionForCoarseLods;

    [ExportGroup("Seams")]
    [Export] public TerrainMixedLodSeamMode MixedLodSeamMode = TerrainMixedLodSeamMode.SkirtsOnly;

    private readonly Dictionary<TerrainBlockId, TerrainBlockData> _blocks = new();
    private readonly HashSet<TerrainBlockId> _desiredBlocks = new();
    private readonly Dictionary<int, HashSet<TerrainBlockId>> _activeSplitParentsByLod = new();
    private readonly Dictionary<int, TerrainBlockId> _currentStableCentersByLod = new();
    private readonly Dictionary<int, TerrainBlockId> _targetStableCentersByLod = new();
    private readonly HashSet<TerrainBlockId> _startupBlocks = new();
    private readonly HashSet<TerrainBlockId> _startupSatisfiedBlocks = new();
    private readonly StringBuilder _debugBuilder = new();
    private readonly Queue<double> _recentCreationTimes = new();
    private readonly Queue<double> _recentReleaseTimes = new();
    private readonly Queue<double> _recentDesiredSetChangeTimes = new();
    private readonly ConcurrentQueue<CompletedFieldBuildResult> _completedFieldBuildResults = new();
    private readonly ConcurrentQueue<CompletedMeshBuildResult> _completedMeshBuildResults = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _createDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _fieldBuildDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _meshBuildDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _commitDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _collisionDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _releaseDispatcherQueue = new();
    private readonly Dictionary<TerrainBlockId, TerrainLodSupersededBlockTransition> _supersededBlockTransitions = new();
    private readonly Dictionary<TerrainBlockId, int> _createDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _fieldBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _meshBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _commitDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _collisionDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _releaseDispatchTokens = new();
    private readonly object _transitionLogLock = new();
    private readonly HashSet<TerrainBlockId> _dirtyVisibleMixedLodSeamBlocks = new();

    private TerrainConfig _config = null!;
    private TerrainChunkStore _chunkStore = null!;
    private TerrainEditRegionManager _editRegionManager = null!;
    private TerrainMesher _mesher = null!;
    private TerrainSurfaceColorizer _surfaceColorizer = null!;
    private TerrainWorldProfileSnapshot _latestProfileSnapshot = null!;
    private TerrainWorld _terrainWorld = null!;
    private Node3D _trackedCharacter = null!;
    private TerrainBlockId _currentCenterParent;
    private TerrainBlockId _targetCenterParent;
    private TerrainBlockId _currentViewerParent;
    private Vector3 _lastViewerPosition;
    private double _currentTimeSeconds;
    private string _lastSelectionSummary = "waiting_for_viewer";
    private string _lastTierSelectionSummary = "Tier summary waiting_for_viewer.";
    private string _lastRefinementHandoffSummary = "none";
    private string _lastReleaseSummary = "none";
    private string _lastCommitSummary = "none";
    private string _lastEditRegionSummary = "none";
    private bool _selectionInitialized;
    private bool _initialLoadComplete;
    private int _lastDesiredBlockCount;
    private int _lastDesiredSetChangeCount;
    private int _hysteresisRetainedBlockCount;
    private int _currentBubbleParentCount;
    private int _currentRefinedSameLodBlockCount;
    private int _currentCoarsestRadius;
    private int _lastCreateCount;
    private int _lastFieldBuildCount;
    private int _lastMeshBuildCount;
    private int _lastCommitCount;
    private int _lastCollisionCount;
    private int _lastReleaseCount;
    private int _lastReleaseHysteresisDeferralCount;
    private int _lastReleaseCoverageDeferralCount;
    private int _lastReleaseRequeueCount;
    private int _lastReleaseHeadOfLineAvoidedCount;
    private int _lastReleaseDeferredAgeSampleCount;
    private double _lastFieldBuildMs;
    private double _lastMeshBuildMs;
    private double _lastCommitMs;
    private double _lastCollisionMs;
    private double _lastReleaseMs;
    private double _lastReleaseDeferredAgeMsTotal;
    private double _blockCreateRatePerSecond;
    private double _blockReleaseRatePerSecond;
    private double _blockSetChangeRatePerSecond;
    private long _deformOperationCount;
    private long _totalEditedChunkCount;
    private long _totalEditedSampleCount;
    private double _totalEditedDirtyBoundsVolume;
    private long _editDetailPromotionCount;
    private long _deformOperationSequence;
    private int _lastDeformEditedChunkCount;
    private int _lastDeformEditedSampleCount;
    private double _lastDeformDirtyBoundsVolume;
    private int _lastDeformEditDetailPromotionCount;
    private double _lastDeformMs;
    private string _lastDeformKind = "n/a";
    private string _lastEditOperationSummary = "none";
    private int _lastDeformVisibleBlockCount;
    private int _lastDeformVisibleFinestBlockCount;
    private int _lastDeformRequeuedBlockCount;
    private int _lastDeformQueuedVisibleBlockCount;
    private int _lastDeformRefreshedTriangleCount;
    private double _lastDeformRegistrationMs;
    private double _lastDeformEnqueueMs;
    private double _lastDeformSyncWorkMs;
    private double _lastDeformAsyncRebuildMs;
    private double _lastDeformVisualApplyMs;
    private double _lastDeformCollisionRebuildMs;
    private long _lastDeformOperationSequence;
    private string _lastEditOperationPrefix = "none";
    private long _refinementHandoffCount;
    private long _releaseHysteresisDeferralCount;
    private long _releaseCoverageDeferralCount;
    private long _releaseRequeueCount;
    private long _releaseHeadOfLineAvoidedCount;
    private long _releaseDeferredAgeSampleCount;
    private double _releaseDeferredAgeMsTotal;
    private long _blockInstanceVersionSequence;
    private int _activeFieldWorkerJobs;
    private int _activeMeshWorkerJobs;
    private int _dispatchSequence;
    private TerrainMixedLodSeamMode _appliedMixedLodSeamMode;
    private TerrainVisualDebugMode _activeTerrainDebugView = TerrainVisualDebugMode.Lit;
    private int[] _currentLodBlockCounts = System.Array.Empty<int>();
    private int[] _currentSplitParentCounts = System.Array.Empty<int>();
    private StreamWriter _transitionLogWriter = null!;
    private bool _warnedTransitionLogFailure;
    private string _lastSupersededTransitionSummary = "none";
    private string _lastMixedLodSeamSummary = "none";
    private bool _allVisibleMixedLodSeamsDirty;
    private float _lastConfiguredSurfaceWaterLevel = float.NaN;

    public bool InitialLoadComplete => _initialLoadComplete;
    public float InitialLoadProgress { get; private set; }
    public TerrainVisualDebugMode ActiveTerrainDebugView => _activeTerrainDebugView;

    public override void _Ready()
    {
        _terrainWorld = GetParent() as TerrainWorld;
        _config = BuildConfig();
        _chunkStore = new TerrainChunkStore(_terrainWorld?.Seed ?? _config.Seed);
        _editRegionManager = new TerrainEditRegionManager(_chunkStore, _config.BaseVoxelSize);
        _mesher = new TerrainMesher(_config);
        _surfaceColorizer = new TerrainSurfaceColorizer(_config);
        _lastEditRegionSummary = _editRegionManager.BuildDebugSummary();
        _appliedMixedLodSeamMode = MixedLodSeamMode;
        _activeTerrainDebugView = ResolveTerrainDebugView(_terrainWorld?.TerrainDebugView ?? TerrainVisualDebugMode.Lit);
        ConfigureSharedSurfaceWaterLevel();
        _trackedCharacter = ResolveTrackedCharacter();
        _latestProfileSnapshot = BuildProfileSnapshot();
    }

    public override void _ExitTree()
    {
        CloseTransitionLogWriter();
    }

    public override void _Process(double delta)
    {
        _currentTimeSeconds = Time.GetTicksUsec() / 1_000_000.0;
        ConfigureSharedSurfaceWaterLevel();
        _trackedCharacter ??= ResolveTrackedCharacter();
        if (_trackedCharacter == null)
        {
            RefreshVisibleMixedLodSeamsIfNeeded();
            RefreshLifecycleRates();
            _latestProfileSnapshot = BuildProfileSnapshot();
            return;
        }

        _lastCreateCount = 0;
        _lastFieldBuildCount = 0;
        _lastMeshBuildCount = 0;
        _lastCommitCount = 0;
        _lastCollisionCount = 0;
        _lastReleaseCount = 0;
        _lastFieldBuildMs = 0.0;
        _lastMeshBuildMs = 0.0;
        _lastCommitMs = 0.0;
        _lastCollisionMs = 0.0;
        _lastReleaseMs = 0.0;

        _lastViewerPosition = _trackedCharacter.GlobalTransform.Origin;
        UpdateDesiredBlocks(_lastViewerPosition);
        DispatchRuntimeWork();
        RefreshVisibleMixedLodSeamsIfNeeded();
        RefreshLifecycleRates();
        UpdateInitialLoadState();
        _latestProfileSnapshot = BuildProfileSnapshot();
    }

    private void RefreshVisibleMixedLodSeamsIfNeeded()
    {
        if (_appliedMixedLodSeamMode != MixedLodSeamMode)
        {
            _appliedMixedLodSeamMode = MixedLodSeamMode;
            MarkAllVisibleMixedLodSeamsDirty();
        }

        if (_allVisibleMixedLodSeamsDirty)
        {
            _allVisibleMixedLodSeamsDirty = false;
            _dirtyVisibleMixedLodSeamBlocks.Clear();
            RefreshAllVisibleMixedLodSeams();
            return;
        }

        if (_dirtyVisibleMixedLodSeamBlocks.Count == 0)
        {
            return;
        }

        List<TerrainBlockId> dirtyBlockIds = new(_dirtyVisibleMixedLodSeamBlocks);
        _dirtyVisibleMixedLodSeamBlocks.Clear();
        dirtyBlockIds.Sort(CompareTerrainBlockIds);
        foreach (TerrainBlockId blockId in dirtyBlockIds)
        {
            RefreshVisibleMixedLodSeam(blockId);
        }
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        return _latestProfileSnapshot ??= BuildProfileSnapshot();
    }

    public string GetDebugSummary()
    {
        string supersededSummary = BuildSupersededTransitionSummary();
        MixedLodSeamProfileSummary seamSummary = BuildMixedLodSeamProfileSummary();
        _debugBuilder.Clear();
        _debugBuilder.AppendLine("TerrainLodManager active.");
        _debugBuilder.AppendLine(BuildLodSpanSummary());
        _debugBuilder.AppendLine($"Debug {_activeTerrainDebugView.GetDisplayName()}");
        _debugBuilder.AppendLine(_lastSelectionSummary);
        _debugBuilder.AppendLine(_lastTierSelectionSummary);
        _debugBuilder.AppendLine($"Lifecycle {BuildLifecycleSummary()}");
        _debugBuilder.AppendLine($"Edits {_lastEditRegionSummary}");
        _debugBuilder.AppendLine($"Handoff {_lastRefinementHandoffSummary}");
        _debugBuilder.AppendLine($"Seams {BuildMixedLodSeamSummary(seamSummary)}");
        _debugBuilder.AppendLine($"Supersede {supersededSummary}");
        _debugBuilder.Append($"Latest {(_lastCommitSummary == string.Empty ? "none" : _lastCommitSummary)}");
        return _debugBuilder.ToString();
    }

    public void ApplyBrush(Vector3 worldCenter, bool additive)
    {
        float strength = additive
            ? (_terrainWorld?.BuildStrength ?? 2.8f)
            : (_terrainWorld?.CarveStrength ?? -3.4f);
        float radius = _terrainWorld?.BrushRadius ?? 2.4f;
        float retextureMargin = _terrainWorld?.BrushRetextureMargin ?? 1.6f;
        ApplyImpact(TerrainImpactProfiles.CreateBrush(
            worldCenter,
            additive,
            radius,
            strength,
            retextureMargin));
    }

    public void ApplySlash(VoxelSlashEdit edit)
    {
        ApplyImpact(TerrainImpactRequest.CreateSlash(
            TerrainImpactKind.Custom,
            edit.Center,
            edit.Direction,
            edit.SurfaceNormal,
            edit.Length,
            edit.Width,
            edit.Depth,
            edit.DensityDelta,
            edit.PaintStrength,
            edit.RetextureMargin,
            2,
            TerrainDetailRegionSource.Edit,
            TerrainChunk.EditedDetailRegionReason,
            100.0f,
            true));
    }

    public void ApplyImpact(TerrainImpactRequest impact)
    {
        ulong operationStartUsec = Time.GetTicksUsec();
        TerrainEditStampData stamp = impact.ToStamp(_config.BaseVoxelSize);
        ulong registrationStartUsec = Time.GetTicksUsec();
        TerrainEditRegionMutationResult mutation = _editRegionManager.RegisterStamp(
            stamp,
            Mathf.Max(1, impact.RequestedDetailLevel),
            impact.Source,
            impact.RegionReason,
            impact.Priority,
            impact.Sticky);
        ApplyEditMutation(
            impact.OperationName,
            mutation,
            stamp,
            operationStartUsec,
            (Time.GetTicksUsec() - registrationStartUsec) / 1000.0);
    }

    public void ClearPersistedEditRegions()
    {
        ulong operationStartUsec = Time.GetTicksUsec();
        ulong registrationStartUsec = Time.GetTicksUsec();
        TerrainEditRegionMutationResult mutation = _editRegionManager.ClearAll();
        ApplyEditMutation(
            "clear_edits",
            mutation,
            null,
            operationStartUsec,
            (Time.GetTicksUsec() - registrationStartUsec) / 1000.0);
    }

    public bool SetTerrainDebugView(TerrainVisualDebugMode debugView)
    {
        TerrainVisualDebugMode resolvedDebugView = ResolveTerrainDebugView(debugView);
        if (_activeTerrainDebugView == resolvedDebugView)
        {
            return false;
        }

        _activeTerrainDebugView = resolvedDebugView;
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (block.Renderer == null || !IsInstanceValid(block.Renderer))
            {
                continue;
            }

            block.Renderer.SetDebugView(_activeTerrainDebugView, _surfaceColorizer);
        }

        MarkAllVisibleMixedLodSeamsDirty();
        RefreshVisibleMixedLodSeamsIfNeeded();
        _latestProfileSnapshot = BuildProfileSnapshot();
        return true;
    }

    private TerrainConfig BuildConfig()
    {
        if (_terrainWorld == null)
        {
            return new TerrainConfig
            {
                PointsPerAxis = 18,
                BaseVoxelSize = 1.2f,
                BaseY = -12.0f,
                Seed = 12345,
                TerrainHeight = 10.0f,
                DetailHeight = 2.8f,
                CaveScale = 9.0f,
                CaveThreshold = 0.63f,
                WaterLevel = -3.4f,
                ShorelineFalloff = 3.4f,
                WaterBasinInfluence = 0.48f,
                CoarseRadiusXZ = Mathf.Max(1, CoarsestRadiusXZ),
                VerticalRadius = Mathf.Max(0, VerticalRadius),
                FieldBuildsPerFrame = Mathf.Clamp(FieldWorkerJobs, 1, MaxFieldWorkerJobs),
                MeshBuildsPerFrame = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs),
                CommitsPerFrame = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame),
                ReleasesPerFrame = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame),
                GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods,
                MeshColorMode = VoxelMeshColorMode.Neutral
            };
        }

        return new TerrainConfig
        {
            PointsPerAxis = Mathf.Max(4, _terrainWorld.PointsPerAxis),
            BaseVoxelSize = Mathf.Max(0.1f, _terrainWorld.MinCellWorldSize),
            BaseY = _terrainWorld.BaseY,
            Seed = _terrainWorld.Seed,
            TerrainHeight = _terrainWorld.TerrainHeight,
            DetailHeight = _terrainWorld.DetailHeight,
            CaveScale = _terrainWorld.CaveScale,
            CaveThreshold = _terrainWorld.CaveThreshold,
            WaterLevel = _terrainWorld.WaterLevel,
            ShorelineFalloff = Mathf.Max(0.4f, _terrainWorld.ShorelineFalloff),
            WaterBasinInfluence = Mathf.Clamp(_terrainWorld.WaterBasinInfluence, 0.0f, 1.0f),
            CoarseRadiusXZ = Mathf.Max(1, CoarsestRadiusXZ),
            VerticalRadius = Mathf.Max(0, VerticalRadius),
            FieldBuildsPerFrame = Mathf.Clamp(FieldWorkerJobs, 1, MaxFieldWorkerJobs),
            MeshBuildsPerFrame = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs),
            CommitsPerFrame = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame),
            ReleasesPerFrame = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame),
            GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods,
            MeshColorMode = VoxelMeshColorMode.Neutral
        };
    }

    private static TerrainVisualDebugMode ResolveTerrainDebugView(TerrainVisualDebugMode debugView)
    {
        return OS.IsDebugBuild()
            ? debugView
            : TerrainVisualDebugMode.Lit;
    }

    private void ApplyEditMutation(
        string operation,
        TerrainEditRegionMutationResult mutation,
        TerrainEditStampData? stamp = null,
        ulong operationStartUsec = 0,
        double registrationMs = 0.0)
    {
        if (!mutation.Changed)
        {
            _lastDeformOperationSequence = ++_deformOperationSequence;
            _lastDeformKind = operation;
            _lastDeformMs = operationStartUsec == 0
                ? registrationMs
                : (Time.GetTicksUsec() - operationStartUsec) / 1000.0;
            _lastDeformEditedChunkCount = 0;
            _lastDeformEditedSampleCount = 0;
            _lastDeformDirtyBoundsVolume = 0.0;
            _lastDeformEditDetailPromotionCount = 0;
            _lastDeformVisibleBlockCount = 0;
            _lastDeformVisibleFinestBlockCount = 0;
            _lastDeformRequeuedBlockCount = 0;
            _lastDeformQueuedVisibleBlockCount = 0;
            _lastDeformRefreshedTriangleCount = 0;
            _lastDeformRegistrationMs = registrationMs;
            _lastDeformEnqueueMs = 0.0;
            _lastDeformSyncWorkMs = 0.0;
            _lastDeformAsyncRebuildMs = 0.0;
            _lastDeformVisualApplyMs = 0.0;
            _lastDeformCollisionRebuildMs = 0.0;
            _lastEditOperationPrefix = $"{operation} none";
            RefreshLastEditOperationSummary();
            _lastEditRegionSummary = _lastEditOperationSummary;
            _latestProfileSnapshot = BuildProfileSnapshot();
            return;
        }

        long operationSequence = ++_deformOperationSequence;
        TerrainEditInvalidationStats invalidation = InvalidateBlocksForEditMutation(mutation.DirtyWorldBounds, operationSequence);
        double dirtyBoundsVolume = ComputeBoundsVolume(mutation.DirtyWorldBounds);
        int estimatedEditedSamples = EstimateEditedSampleCount(stamp, mutation.DirtyWorldBounds);
        int detailPromotions = invalidation.VisibleFinestBlockCount;
        double deformMs = operationStartUsec == 0
            ? registrationMs + invalidation.EnqueueMs + invalidation.SyncWorkMs
            : (Time.GetTicksUsec() - operationStartUsec) / 1000.0;

        _deformOperationCount++;
        _totalEditedChunkCount += invalidation.IntersectedBlockCount;
        _totalEditedSampleCount += estimatedEditedSamples;
        _totalEditedDirtyBoundsVolume += dirtyBoundsVolume;
        _editDetailPromotionCount += detailPromotions;
        _lastDeformOperationSequence = operationSequence;
        _lastDeformEditedChunkCount = invalidation.IntersectedBlockCount;
        _lastDeformEditedSampleCount = estimatedEditedSamples;
        _lastDeformDirtyBoundsVolume = dirtyBoundsVolume;
        _lastDeformEditDetailPromotionCount = detailPromotions;
        _lastDeformMs = deformMs;
        _lastDeformKind = operation;
        _lastDeformVisibleBlockCount = invalidation.VisibleBlockCount;
        _lastDeformVisibleFinestBlockCount = invalidation.VisibleFinestBlockCount;
        _lastDeformRequeuedBlockCount = invalidation.RequeuedBlockCount;
        _lastDeformQueuedVisibleBlockCount = invalidation.QueuedVisibleBlockCount;
        _lastDeformRefreshedTriangleCount = 0;
        _lastDeformRegistrationMs = registrationMs;
        _lastDeformEnqueueMs = invalidation.EnqueueMs;
        _lastDeformSyncWorkMs = invalidation.SyncWorkMs;
        _lastDeformAsyncRebuildMs = 0.0;
        _lastDeformVisualApplyMs = 0.0;
        _lastDeformCollisionRebuildMs = 0.0;
        _lastEditOperationPrefix = $"{operation} {mutation.Summary} {invalidation.Summary} est_samples {estimatedEditedSamples}";
        RefreshLastEditOperationSummary();
        _lastEditRegionSummary = _lastEditOperationSummary;
        _latestProfileSnapshot = BuildProfileSnapshot();
    }

    private TerrainEditInvalidationStats InvalidateBlocksForEditMutation(Aabb dirtyWorldBounds, long operationSequence)
    {
        ulong enqueueStartUsec = Time.GetTicksUsec();
        List<TerrainBlockData> displayedBlocks = new();
        List<TerrainBlockId> requeuedBlocks = new();
        int intersectedBlockCount = 0;
        int visibleFinestBlockCount = 0;
        int queuedVisibleBlockCount = 0;

        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (!TerrainMetrics.GetBlockBounds(_config, block.Id).Intersects(dirtyWorldBounds))
            {
                continue;
            }

            intersectedBlockCount++;
            if (IsBlockDisplayingVisuals(block))
            {
                displayedBlocks.Add(block);
                if (block.Id.Lod == FinestTerrainLod)
                {
                    visibleFinestBlockCount++;
                }

                continue;
            }

            if (block.State is TerrainBlockState.Requested or TerrainBlockState.FieldReady or TerrainBlockState.MeshReady)
            {
                block.InvalidatePendingBuildData();
                RemoveBlockFromDispatcherQueues(block.Id);
                if (block.Desired)
                {
                    requeuedBlocks.Add(block.Id);
                }
            }
        }

        foreach (TerrainBlockId blockId in requeuedBlocks)
        {
            EnqueueFieldBuildDispatch(blockId);
        }

        foreach (TerrainBlockData displayedBlock in displayedBlocks)
        {
            displayedBlock.MarkDisplayedRefreshDirty(operationSequence);
            InvalidateBlockDispatch(_fieldBuildDispatchTokens, displayedBlock.Id);
            InvalidateBlockDispatch(_meshBuildDispatchTokens, displayedBlock.Id);
            InvalidateBlockDispatch(_commitDispatchTokens, displayedBlock.Id);
            InvalidateBlockDispatch(_collisionDispatchTokens, displayedBlock.Id);
            if (EnqueueDisplayedRefreshForCurrentState(displayedBlock))
            {
                queuedVisibleBlockCount++;
            }
        }

        return new TerrainEditInvalidationStats(
            intersectedBlockCount,
            displayedBlocks.Count,
            visibleFinestBlockCount,
            requeuedBlocks.Count,
            queuedVisibleBlockCount,
            (Time.GetTicksUsec() - enqueueStartUsec) / 1000.0,
            0.0);
    }

    private void RefreshLastEditOperationSummary()
    {
        _lastEditOperationSummary =
            $"{_lastEditOperationPrefix} reg_ms {_lastDeformRegistrationMs:0.00} enqueue_ms {_lastDeformEnqueueMs:0.00} " +
            $"sync_ms {_lastDeformSyncWorkMs:0.00} async_ms {_lastDeformAsyncRebuildMs:0.00} " +
            $"apply_ms {_lastDeformVisualApplyMs:0.00} collision_ms {_lastDeformCollisionRebuildMs:0.00} " +
            $"tri {_lastDeformRefreshedTriangleCount} total_ms {_lastDeformMs:0.00}";
    }

    private void AccumulateDisplayedRefreshAsyncRebuild(long operationSequence, double workerMs)
    {
        if (operationSequence == 0 || operationSequence != _lastDeformOperationSequence)
        {
            return;
        }

        _lastDeformAsyncRebuildMs += workerMs;
        RefreshLastEditOperationSummary();
    }

    private void AccumulateDisplayedRefreshVisualApply(long operationSequence, double applyMs, int triangleCount)
    {
        if (operationSequence == 0 || operationSequence != _lastDeformOperationSequence)
        {
            return;
        }

        _lastDeformVisualApplyMs += applyMs;
        _lastDeformRefreshedTriangleCount += triangleCount;
        RefreshLastEditOperationSummary();
    }

    private void AccumulateDisplayedRefreshCollisionRebuild(long operationSequence, double collisionMs)
    {
        if (operationSequence == 0 || operationSequence != _lastDeformOperationSequence)
        {
            return;
        }

        _lastDeformCollisionRebuildMs += collisionMs;
        RefreshLastEditOperationSummary();
    }

    private bool EnqueueDisplayedRefreshForCurrentState(TerrainBlockData block)
    {
        if (!block.DisplayedRefreshDirty || !IsBlockDisplayingVisuals(block))
        {
            return false;
        }

        if (block.HasDisplayedRefreshMeshReady)
        {
            EnqueueCommitDispatch(block.Id, urgent: true);
            return true;
        }

        if (block.HasDisplayedRefreshFieldReady)
        {
            EnqueueMeshBuildDispatch(block.Id, urgent: true);
            return true;
        }

        if (!block.FieldBuildRunning && !block.MeshBuildRunning)
        {
            EnqueueFieldBuildDispatch(block.Id, urgent: true);
            return true;
        }

        return false;
    }

    private TerrainEditRegion[] GetEditRegionsForBlock(TerrainBlockId blockId)
    {
        return _editRegionManager?.QueryOverlapping(TerrainMetrics.GetBlockBounds(_config, blockId))
            ?? Array.Empty<TerrainEditRegion>();
    }

    private void ConfigureSharedSurfaceWaterLevel()
    {
        float waterLevel = _terrainWorld?.WaterLevel ?? _config.WaterLevel;
        if (Mathf.IsEqualApprox(_lastConfiguredSurfaceWaterLevel, waterLevel))
        {
            return;
        }

        TerrainSurfaceMaterialLibrary.ConfigureSharedWaterLevel(waterLevel);
        _lastConfiguredSurfaceWaterLevel = waterLevel;
    }

    private int EstimateEditedSampleCount(TerrainEditStampData? stamp, Aabb dirtyWorldBounds)
    {
        if (!stamp.HasValue)
        {
            return 0;
        }

        double voxelVolume = Math.Max(0.0001, Math.Pow(Math.Max(0.01f, _config.BaseVoxelSize), 3.0));
        TerrainEditStampData editStamp = stamp.Value;
        double shapeVolume = editStamp.Kind switch
        {
            TerrainEditStampKind.Sphere => (4.0 / 3.0) * Math.PI * Math.Pow(Math.Max(0.05f, editStamp.Radius), 3.0),
            TerrainEditStampKind.Slash => Math.PI * 0.25 *
                                         Math.Max(0.05f, editStamp.Length) *
                                         Math.Max(0.05f, editStamp.Width) *
                                         Math.Max(0.05f, editStamp.Depth),
            _ => ComputeBoundsVolume(dirtyWorldBounds)
        };
        double dirtyVolume = ComputeBoundsVolume(dirtyWorldBounds);
        double effectiveVolume = Math.Max(shapeVolume, Math.Min(dirtyVolume, shapeVolume * 1.35));
        return Math.Max(1, (int)Math.Round(effectiveVolume / voxelVolume));
    }

    private static double ComputeBoundsVolume(Aabb bounds)
    {
        Vector3 size = bounds.Size;
        return Math.Max(0.0, size.X) * Math.Max(0.0, size.Y) * Math.Max(0.0, size.Z);
    }

    private Node3D ResolveTrackedCharacter()
    {
        if (_terrainWorld != null && !_terrainWorld.TrackedCharacterPath.IsEmpty)
        {
            return _terrainWorld.GetNodeOrNull<Node3D>(_terrainWorld.TrackedCharacterPath);
        }

        return null;
    }

    private void UpdateDesiredBlocks(Vector3 viewerPosition)
    {
        TerrainBlockId viewerParent = ComputeSelectionCenterParent(viewerPosition);
        _currentViewerParent = viewerParent;
        if (!_selectionInitialized)
        {
            _startupBlocks.Clear();
            _startupSatisfiedBlocks.Clear();
            _activeSplitParentsByLod.Clear();
            _currentStableCentersByLod.Clear();
            _targetStableCentersByLod.Clear();
            UpdateStableCenterParents(viewerPosition, viewerParent);
            _selectionInitialized = true;
            _lastDesiredSetChangeCount = 0;
        }
        else
        {
            UpdateStableCenterParents(viewerPosition, viewerParent);
        }

        int selectionCenterLod = GetSelectionCenterLod();
        _currentCenterParent = GetCurrentStableCenterParent(viewerPosition, selectionCenterLod);
        _targetCenterParent = GetTargetStableCenterParent(viewerPosition, selectionCenterLod);

        Dictionary<int, HashSet<TerrainBlockId>> activeSplitParentsByLod = BuildActiveSplitParentsByLod(viewerPosition);
        ReplaceSetMap(_activeSplitParentsByLod, activeSplitParentsByLod);
        HashSet<TerrainBlockId> desired = BuildDesiredSet(_activeSplitParentsByLod);
        _lastDesiredBlockCount = desired.Count;
        _currentLodBlockCounts = CountBlocksByLod(desired);
        _currentSplitParentCounts = CountSplitParentsByLod(_activeSplitParentsByLod);
        _currentBubbleParentCount = GetSplitParentCount(FinestTerrainLod + 1);
        _currentRefinedSameLodBlockCount = GetLodBlockCount(FinestTerrainLod);

        if (_startupBlocks.Count == 0)
        {
            foreach (TerrainBlockId blockId in desired)
            {
                _startupBlocks.Add(blockId);
            }
        }

        ApplyDesiredSetChanges(desired);

        _hysteresisRetainedBlockCount = CountHysteresisRetainedBlocks();
        _lastSelectionSummary = BuildSelectionSummary(_currentCenterParent, _targetCenterParent, viewerParent, desired.Count);
        _lastTierSelectionSummary = BuildTierSelectionSummary();
    }

    private TerrainBlockId ComputeSelectionCenterParent(Vector3 viewerPosition)
    {
        return ComputeTrackedParent(viewerPosition, GetSelectionCenterLod());
    }

    private void UpdateStableCenterParents(Vector3 viewerPosition, TerrainBlockId selectionViewerParent)
    {
        int selectionCenterLod = GetSelectionCenterLod();
        for (int parentLod = FinestTerrainLod + 1; parentLod <= selectionCenterLod; parentLod++)
        {
            TerrainBlockId rawParent = parentLod == selectionCenterLod
                ? selectionViewerParent
                : ComputeTrackedParent(viewerPosition, parentLod);
            if (!_selectionInitialized ||
                !_currentStableCentersByLod.TryGetValue(parentLod, out TerrainBlockId currentCenterParent))
            {
                _currentStableCentersByLod[parentLod] = rawParent;
                _targetStableCentersByLod[parentLod] = rawParent;
                continue;
            }

            TerrainBlockId targetCenterParent = ResolveStableCenterParent(
                viewerPosition,
                rawParent,
                currentCenterParent,
                parentLod);
            TerrainBlockId nextCenterParent = currentCenterParent.Equals(targetCenterParent)
                ? currentCenterParent
                : ComputeNextCenterStep(currentCenterParent, targetCenterParent);
            if (parentLod == selectionCenterLod && !nextCenterParent.Equals(currentCenterParent))
            {
                RecordRefinementHandoff(currentCenterParent, nextCenterParent, selectionViewerParent, targetCenterParent);
            }

            _currentStableCentersByLod[parentLod] = nextCenterParent;
            _targetStableCentersByLod[parentLod] = targetCenterParent;
        }
    }

    private TerrainBlockId ComputeTrackedParent(Vector3 viewerPosition, int parentLod)
    {
        int resolvedParentLod = Mathf.Clamp(parentLod, FinestTerrainLod, GetCoarsestLod());
        int anchorChildLod = Mathf.Max(FinestTerrainLod, resolvedParentLod - 1);
        float ultraFineSpan = TerrainMetrics.GetBlockSpan(_config, anchorChildLod);
        float surfaceY = _mesher.SampleSurfaceHeight(viewerPosition.X, viewerPosition.Z);
        float anchorY = Mathf.Max(_config.BaseY, surfaceY - (ultraFineSpan * 0.5f));
        Vector3 anchor = new(viewerPosition.X, anchorY, viewerPosition.Z);
        return TerrainMetrics.GetBlockForWorldPosition(_config, resolvedParentLod, anchor);
    }

    private TerrainBlockId GetCurrentStableCenterParent(Vector3 viewerPosition, int parentLod)
    {
        return _currentStableCentersByLod.TryGetValue(parentLod, out TerrainBlockId centerParent)
            ? centerParent
            : ComputeTrackedParent(viewerPosition, parentLod);
    }

    private TerrainBlockId GetTargetStableCenterParent(Vector3 viewerPosition, int parentLod)
    {
        return _targetStableCentersByLod.TryGetValue(parentLod, out TerrainBlockId centerParent)
            ? centerParent
            : GetCurrentStableCenterParent(viewerPosition, parentLod);
    }

    private TerrainBlockId ResolveStableCenterParent(
        Vector3 viewerPosition,
        TerrainBlockId viewerParent,
        TerrainBlockId currentCenterParent,
        int parentLod)
    {
        if (!_selectionInitialized)
        {
            return viewerParent;
        }

        if (viewerParent.Index.Y != currentCenterParent.Index.Y)
        {
            return viewerParent;
        }

        int bubbleRadius = GetStableCenterRadius(parentLod);
        int preferredCenterRadius = Mathf.Max(0, bubbleRadius - 1);
        Vector3I parentDelta = viewerParent.Index - currentCenterParent.Index;
        if (Mathf.Abs(parentDelta.X) > preferredCenterRadius || Mathf.Abs(parentDelta.Z) > preferredCenterRadius)
        {
            // Treat the bubble's outer ring as a buffer instead of where the player lives. Recenter as soon as the
            // viewer enters that outer ring so the character stays closer to the middle while walking.
            return new TerrainBlockId(
                currentCenterParent.Lod,
                currentCenterParent.Index + new Vector3I(
                    ComputeCenterRecenteringDelta(parentDelta.X, preferredCenterRadius),
                    0,
                    ComputeCenterRecenteringDelta(parentDelta.Z, preferredCenterRadius)));
        }

        float parentSpan = TerrainMetrics.GetBlockSpan(_config, parentLod);
        float padding = Mathf.Clamp(BubbleMovePaddingFraction, 0.0f, 0.49f) * parentSpan;
        Vector3 minOrigin = TerrainMetrics.GetBlockOrigin(
            _config,
            new TerrainBlockId(currentCenterParent.Lod, currentCenterParent.Index + new Vector3I(-bubbleRadius, 0, -bubbleRadius)));
        Vector3 maxOrigin = TerrainMetrics.GetBlockOrigin(
            _config,
            new TerrainBlockId(currentCenterParent.Lod, currentCenterParent.Index + new Vector3I(bubbleRadius, 0, bubbleRadius)));
        float maxX = maxOrigin.X + parentSpan;
        float maxZ = maxOrigin.Z + parentSpan;
        // Let the same-LOD bubble trail the raw viewer parent until the player is meaningfully outside the
        // current bubble footprint. This keeps walking inside the neighborhood from constantly shifting blocks.
        bool outsideStableBounds =
            viewerPosition.X < (minOrigin.X - padding) ||
            viewerPosition.X > (maxX + padding) ||
            viewerPosition.Z < (minOrigin.Z - padding) ||
            viewerPosition.Z > (maxZ + padding);
        return outsideStableBounds ? viewerParent : currentCenterParent;
    }

    private Dictionary<int, HashSet<TerrainBlockId>> BuildActiveSplitParentsByLod(Vector3 viewerPosition)
    {
        Dictionary<int, HashSet<TerrainBlockId>> splitParentsByLod = new();
        int coarsestLod = GetCoarsestLod();
        if (coarsestLod <= FinestTerrainLod)
        {
            return splitParentsByLod;
        }

        HashSet<TerrainBlockId> outerCoverageBlocks = BuildParentBubble(_currentCenterParent, GetSplitRadiusForParentLod(coarsestLod));
        HashSet<TerrainBlockId> outerSplitParents = new();
        foreach (TerrainBlockId childCoverageBlock in outerCoverageBlocks)
        {
            outerSplitParents.Add(GetParentBlock(childCoverageBlock));
        }

        splitParentsByLod[coarsestLod] = outerSplitParents;
        for (int parentLod = GetSelectionCenterLod(); parentLod >= FinestTerrainLod + 1; parentLod--)
        {
            TerrainBlockId centerParent = GetCurrentStableCenterParent(viewerPosition, parentLod);
            HashSet<TerrainBlockId> splitParents = BuildParentBubble(centerParent, GetSplitRadiusForParentLod(parentLod));
            if (splitParentsByLod.TryGetValue(parentLod + 1, out HashSet<TerrainBlockId> activeCoarseParents))
            {
                splitParents = FilterSplitParentsToActiveParents(splitParents, activeCoarseParents);
            }

            splitParentsByLod[parentLod] = splitParents;
        }

        ApplyStickyEditRegionSplitParents(splitParentsByLod);
        return splitParentsByLod;
    }

    private static HashSet<TerrainBlockId> BuildParentBubble(TerrainBlockId centerParent, int bubbleRadius)
    {
        HashSet<TerrainBlockId> refinedParents = new();
        for (int z = -bubbleRadius; z <= bubbleRadius; z++)
        {
            for (int x = -bubbleRadius; x <= bubbleRadius; x++)
            {
                if (!ShouldRefineParentOffset(x, z, bubbleRadius))
                {
                    continue;
                }

                AddRefinedParent(refinedParents, centerParent, x, z);
            }
        }

        return refinedParents;
    }

    private static bool ShouldRefineParentOffset(int xOffset, int zOffset, int bubbleRadius)
    {
        if (bubbleRadius <= 0)
        {
            return xOffset == 0 && zOffset == 0;
        }

        // With the current stylized terrain, keeping the close bubble square makes the mixed-LOD boundary
        // less noticeable and keeps seams farther away from the player.
        return true;
    }

    private static void AddRefinedParent(HashSet<TerrainBlockId> refinedParents, TerrainBlockId centerParent, int xOffset, int zOffset)
    {
        refinedParents.Add(new TerrainBlockId(
            centerParent.Lod,
            centerParent.Index + new Vector3I(xOffset, 0, zOffset)));
    }

    private static HashSet<TerrainBlockId> FilterSplitParentsToActiveParents(
        IReadOnlySet<TerrainBlockId> requestedSplitParents,
        IReadOnlySet<TerrainBlockId> activeCoarseParents)
    {
        HashSet<TerrainBlockId> filtered = new();
        foreach (TerrainBlockId splitParent in requestedSplitParents)
        {
            if (activeCoarseParents.Contains(GetParentBlock(splitParent)))
            {
                filtered.Add(splitParent);
            }
        }

        return filtered;
    }

    private void ApplyStickyEditRegionSplitParents(Dictionary<int, HashSet<TerrainBlockId>> splitParentsByLod)
    {
        if (_editRegionManager == null || _editRegionManager.RegionCount == 0 || GetCoarsestLod() <= FinestTerrainLod)
        {
            return;
        }

        Aabb visibleCoverageBounds = BuildBaseDesiredCoverageBounds();
        TerrainEditRegion[] candidateRegions = _editRegionManager.QueryOverlapping(visibleCoverageBounds);
        foreach (TerrainEditRegion region in candidateRegions)
        {
            if (!IsStickyEditRegion(region) ||
                !TryIntersectBounds(region.WorldBounds, visibleCoverageBounds, out Aabb overlapBounds))
            {
                continue;
            }

            // Sticky edit regions keep their visible area refined to the finest block LOD even after the
            // player bubble drifts away, without expanding the coarse residency footprint.
            foreach (TerrainBlockId finestBlock in EnumerateBlocksOverlappingBounds(overlapBounds, FinestTerrainLod))
            {
                for (int parentLod = FinestTerrainLod + 1; parentLod <= GetCoarsestLod(); parentLod++)
                {
                    GetOrCreateSplitParentSet(splitParentsByLod, parentLod).Add(GetAncestorBlock(finestBlock, parentLod));
                }
            }
        }
    }

    private Aabb BuildBaseDesiredCoverageBounds()
    {
        int coarsestLod = GetCoarsestLod();
        int outerRadius = Mathf.Max(1, CoarsestRadiusXZ);
        int verticalRadius = Mathf.Max(0, _config.VerticalRadius);
        TerrainBlockId coarsestCenter = GetAncestorBlock(_currentCenterParent, coarsestLod);
        TerrainBlockId minBlock = new(
            coarsestLod,
            coarsestCenter.Index + new Vector3I(-outerRadius, -verticalRadius, -outerRadius));
        TerrainBlockId maxBlock = new(
            coarsestLod,
            coarsestCenter.Index + new Vector3I(outerRadius, verticalRadius, outerRadius));
        float span = TerrainMetrics.GetBlockSpan(_config, coarsestLod);
        Vector3 minOrigin = TerrainMetrics.GetBlockOrigin(_config, minBlock);
        Vector3 maxEnd = TerrainMetrics.GetBlockOrigin(_config, maxBlock) + (Vector3.One * span);
        return new Aabb(minOrigin, maxEnd - minOrigin);
    }

    private static HashSet<TerrainBlockId> GetOrCreateSplitParentSet(
        Dictionary<int, HashSet<TerrainBlockId>> splitParentsByLod,
        int parentLod)
    {
        if (!splitParentsByLod.TryGetValue(parentLod, out HashSet<TerrainBlockId> splitParents))
        {
            splitParents = new HashSet<TerrainBlockId>();
            splitParentsByLod[parentLod] = splitParents;
        }

        return splitParents;
    }

    private IEnumerable<TerrainBlockId> EnumerateBlocksOverlappingBounds(Aabb worldBounds, int lod)
    {
        Vector3 min = worldBounds.Position;
        Vector3 maxExclusive = new(
            Mathf.Max(min.X, worldBounds.End.X - 0.001f),
            Mathf.Max(min.Y, worldBounds.End.Y - 0.001f),
            Mathf.Max(min.Z, worldBounds.End.Z - 0.001f));
        TerrainBlockId minBlock = TerrainMetrics.GetBlockForWorldPosition(_config, lod, min);
        TerrainBlockId maxBlock = TerrainMetrics.GetBlockForWorldPosition(_config, lod, maxExclusive);
        for (int z = minBlock.Index.Z; z <= maxBlock.Index.Z; z++)
        {
            for (int y = minBlock.Index.Y; y <= maxBlock.Index.Y; y++)
            {
                for (int x = minBlock.Index.X; x <= maxBlock.Index.X; x++)
                {
                    yield return new TerrainBlockId(lod, new Vector3I(x, y, z));
                }
            }
        }
    }

    private static bool TryIntersectBounds(Aabb a, Aabb b, out Aabb intersection)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Max(a.Position.X, b.Position.X),
            Mathf.Max(a.Position.Y, b.Position.Y),
            Mathf.Max(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Min(aEnd.X, bEnd.X),
            Mathf.Min(aEnd.Y, bEnd.Y),
            Mathf.Min(aEnd.Z, bEnd.Z));
        Vector3 size = max - min;
        if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
        {
            intersection = default;
            return false;
        }

        intersection = new Aabb(min, size);
        return true;
    }

    private static bool IsStickyEditRegion(TerrainEditRegion region)
    {
        return region != null &&
               region.Sticky &&
               region.Source == TerrainDetailRegionSource.Edit;
    }

    private HashSet<TerrainBlockId> BuildDesiredSet(IReadOnlyDictionary<int, HashSet<TerrainBlockId>> splitParentsByLod)
    {
        HashSet<TerrainBlockId> desired = new();
        TerrainBlockId coarsestCenter = GetAncestorBlock(_currentCenterParent, GetCoarsestLod());
        int outerRadius = GetEffectiveCoarsestRadius(coarsestCenter, splitParentsByLod);
        int verticalRadius = Mathf.Max(0, _config.VerticalRadius);
        for (int z = -outerRadius; z <= outerRadius; z++)
        {
            for (int y = -verticalRadius; y <= verticalRadius; y++)
            {
                for (int x = -outerRadius; x <= outerRadius; x++)
                {
                    TerrainBlockId coarsestBlock = new(
                        GetCoarsestLod(),
                        coarsestCenter.Index + new Vector3I(x, y, z));
                    AddDesiredLeaves(coarsestBlock, splitParentsByLod, desired);
                }
            }
        }

        _currentCoarsestRadius = outerRadius;
        return desired;
    }

    private void AddDesiredLeaves(
        TerrainBlockId blockId,
        IReadOnlyDictionary<int, HashSet<TerrainBlockId>> splitParentsByLod,
        HashSet<TerrainBlockId> desired)
    {
        if (blockId.Lod <= FinestTerrainLod ||
            !splitParentsByLod.TryGetValue(blockId.Lod, out HashSet<TerrainBlockId> splitParents) ||
            !splitParents.Contains(blockId))
        {
            desired.Add(blockId);
            return;
        }

        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(blockId))
        {
            AddDesiredLeaves(child, splitParentsByLod, desired);
        }
    }

    private int GetEffectiveCoarsestRadius(
        TerrainBlockId coarsestCenter,
        IReadOnlyDictionary<int, HashSet<TerrainBlockId>> splitParentsByLod)
    {
        int radius = Mathf.Max(1, CoarsestRadiusXZ);
        if (!splitParentsByLod.TryGetValue(GetCoarsestLod(), out HashSet<TerrainBlockId> splitParents))
        {
            return radius;
        }

        foreach (TerrainBlockId splitParent in splitParents)
        {
            Vector3I delta = splitParent.Index - coarsestCenter.Index;
            radius = Mathf.Max(radius, Mathf.Max(Mathf.Abs(delta.X), Mathf.Abs(delta.Z)));
        }

        return radius;
    }

    private void CreateBlock(TerrainBlockId blockId)
    {
        TerrainRenderer renderer = new();
        renderer.Initialize(blockId, TerrainMetrics.GetBlockOrigin(_config, blockId));
        AddChild(renderer);
        long instanceVersion = Interlocked.Increment(ref _blockInstanceVersionSequence);
        _blocks[blockId] = new TerrainBlockData(blockId, renderer, instanceVersion);
        _recentCreationTimes.Enqueue(_currentTimeSeconds);
    }

    private void DispatchRuntimeWork()
    {
        _lastReleaseHysteresisDeferralCount = 0;
        _lastReleaseCoverageDeferralCount = 0;
        _lastReleaseRequeueCount = 0;
        _lastReleaseHeadOfLineAvoidedCount = 0;
        _lastReleaseDeferredAgeSampleCount = 0;
        _lastReleaseDeferredAgeMsTotal = 0.0;
        ProcessCreateDispatch();
        StartFieldBuildWorkers();
        ApplyCompletedFieldBuildResults();
        StartMeshBuildWorkers();
        ApplyCompletedMeshBuildResults();
        ProcessMeshCommitDispatch();
        RefreshCollisionCoverage();
        ProcessCollisionDispatch();
        ProcessReleaseDispatch();
    }

    private void ProcessCreateDispatch()
    {
        int createBudget = Mathf.Clamp(CreateBlocksPerFrame, 1, MaxCreateBlocksPerFrame);
        while (_lastCreateCount < createBudget &&
               TryDequeueBlockDispatch(_createDispatcherQueue, _createDispatchTokens, out TerrainBlockId blockId))
        {
            if (_blocks.ContainsKey(blockId) || !_desiredBlocks.Contains(blockId))
            {
                continue;
            }

            CreateBlock(blockId);
            _lastCreateCount++;
            EnqueueFieldBuildDispatch(blockId);
        }
    }

    private void StartFieldBuildWorkers()
    {
        int workerBudget = Mathf.Clamp(FieldWorkerJobs, 1, MaxFieldWorkerJobs);
        while (Volatile.Read(ref _activeFieldWorkerJobs) < workerBudget &&
               TryDequeueBlockDispatch(_fieldBuildDispatcherQueue, _fieldBuildDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
            {
                continue;
            }

            TerrainBlockBuildPurpose buildPurpose;
            int displayedRefreshRevision = 0;
            long displayedRefreshOperationSequence = 0;
            if (block.State == TerrainBlockState.Requested)
            {
                buildPurpose = TerrainBlockBuildPurpose.RequestedContent;
            }
            else if (block.DisplayedRefreshDirty && IsBlockDisplayingVisuals(block))
            {
                buildPurpose = TerrainBlockBuildPurpose.DisplayedRefresh;
                displayedRefreshRevision = block.DisplayedRefreshRevision;
                displayedRefreshOperationSequence = block.DisplayedRefreshOperationSequence;
            }
            else
            {
                continue;
            }

            if (!block.Desired && buildPurpose == TerrainBlockBuildPurpose.RequestedContent)
            {
                EnqueueReleaseDispatch(blockId);
                continue;
            }

            if (block.FieldBuildRunning)
            {
                continue;
            }

            int revision = block.BeginFieldBuild();
            long instanceVersion = block.InstanceVersion;
            TerrainEditRegion[] editRegions = GetEditRegionsForBlock(blockId);
            Interlocked.Increment(ref _activeFieldWorkerJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    VoxelChunkData field = _mesher.BuildField(blockId, editRegions);
                    _completedFieldBuildResults.Enqueue(
                        new CompletedFieldBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            field,
                            stopwatch.Elapsed.TotalMilliseconds,
                            buildPurpose,
                            displayedRefreshRevision,
                            displayedRefreshOperationSequence,
                            Succeeded: true));
                }
                catch
                {
                    _completedFieldBuildResults.Enqueue(
                        new CompletedFieldBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            null,
                            stopwatch.Elapsed.TotalMilliseconds,
                            buildPurpose,
                            displayedRefreshRevision,
                            displayedRefreshOperationSequence,
                            Succeeded: false));
                }
                finally
                {
                    Interlocked.Decrement(ref _activeFieldWorkerJobs);
                }
            });
        }
    }

    private void ApplyCompletedFieldBuildResults()
    {
        int applyBudget = Mathf.Clamp(FieldResultAppliesPerFrame, 1, MaxFieldResultAppliesPerFrame);
        while (_completedFieldBuildResults.TryPeek(out CompletedFieldBuildResult nextResult))
        {
            bool needsBudget = IsCurrentFieldBuildResult(nextResult);
            if (needsBudget && _lastFieldBuildCount >= applyBudget)
            {
                break;
            }

            if (!_completedFieldBuildResults.TryDequeue(out CompletedFieldBuildResult result))
            {
                continue;
            }

            if (!_blocks.TryGetValue(result.BlockId, out TerrainBlockData block) ||
                !block.MatchesFieldBuild(result.InstanceVersion, result.Revision))
            {
                continue;
            }

            block.ClearFieldBuildRunning(result.Revision);
            if (result.BuildPurpose == TerrainBlockBuildPurpose.RequestedContent)
            {
                if (!block.Desired || block.State != TerrainBlockState.Requested)
                {
                    continue;
                }

                if (!result.Succeeded || result.Field == null)
                {
                    EnqueueFieldBuildDispatch(block.Id);
                    continue;
                }

                block.SetField(result.Field);
                _lastFieldBuildCount++;
                _lastFieldBuildMs += result.WorkerBuildMs;
                EnqueueMeshBuildDispatch(block.Id);
                continue;
            }

            if (!block.DisplayedRefreshDirty || !IsBlockDisplayingVisuals(block))
            {
                continue;
            }

            if (result.DisplayedRefreshRevision != block.DisplayedRefreshRevision)
            {
                EnqueueDisplayedRefreshForCurrentState(block);
                continue;
            }

            if (!result.Succeeded || result.Field == null)
            {
                EnqueueFieldBuildDispatch(block.Id, urgent: true);
                continue;
            }

            block.SetDisplayedRefreshField(result.Field);
            AccumulateDisplayedRefreshAsyncRebuild(result.DisplayedRefreshOperationSequence, result.WorkerBuildMs);
            EnqueueMeshBuildDispatch(block.Id, urgent: true);
        }
    }

    private void StartMeshBuildWorkers()
    {
        int workerBudget = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs);
        while (Volatile.Read(ref _activeMeshWorkerJobs) < workerBudget &&
               TryDequeueBlockDispatch(_meshBuildDispatcherQueue, _meshBuildDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
            {
                continue;
            }

            TerrainBlockBuildPurpose buildPurpose;
            int displayedRefreshRevision = 0;
            long displayedRefreshOperationSequence = 0;
            if (block.State == TerrainBlockState.FieldReady)
            {
                buildPurpose = TerrainBlockBuildPurpose.RequestedContent;
            }
            else if (block.HasDisplayedRefreshFieldReady && IsBlockDisplayingVisuals(block))
            {
                buildPurpose = TerrainBlockBuildPurpose.DisplayedRefresh;
                displayedRefreshRevision = block.DisplayedRefreshRevision;
                displayedRefreshOperationSequence = block.DisplayedRefreshOperationSequence;
            }
            else
            {
                continue;
            }

            if (!block.Desired && buildPurpose == TerrainBlockBuildPurpose.RequestedContent)
            {
                EnqueueReleaseDispatch(blockId);
                continue;
            }

            if (block.MeshBuildRunning || block.Field == null)
            {
                continue;
            }

            VoxelChunkData field = block.Field;
            int revision = block.BeginMeshBuild();
            long instanceVersion = block.InstanceVersion;
            Interlocked.Increment(ref _activeMeshWorkerJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    VoxelMeshBuildResult mesh = _mesher.BuildMesh(field);
                    _completedMeshBuildResults.Enqueue(
                        new CompletedMeshBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            mesh,
                            stopwatch.Elapsed.TotalMilliseconds,
                            buildPurpose,
                            displayedRefreshRevision,
                            displayedRefreshOperationSequence,
                            Succeeded: true));
                }
                catch
                {
                    _completedMeshBuildResults.Enqueue(
                        new CompletedMeshBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            null,
                            stopwatch.Elapsed.TotalMilliseconds,
                            buildPurpose,
                            displayedRefreshRevision,
                            displayedRefreshOperationSequence,
                            Succeeded: false));
                }
                finally
                {
                    Interlocked.Decrement(ref _activeMeshWorkerJobs);
                }
            });
        }
    }

    private void ApplyCompletedMeshBuildResults()
    {
        int applyBudget = Mathf.Clamp(MeshResultAppliesPerFrame, 1, MaxMeshResultAppliesPerFrame);
        while (_completedMeshBuildResults.TryPeek(out CompletedMeshBuildResult nextResult))
        {
            bool needsBudget = IsCurrentMeshBuildResult(nextResult);
            if (needsBudget && _lastMeshBuildCount >= applyBudget)
            {
                break;
            }

            if (!_completedMeshBuildResults.TryDequeue(out CompletedMeshBuildResult result))
            {
                continue;
            }

            if (!_blocks.TryGetValue(result.BlockId, out TerrainBlockData block) ||
                !block.MatchesMeshBuild(result.InstanceVersion, result.Revision))
            {
                continue;
            }

            block.ClearMeshBuildRunning(result.Revision);
            if (result.BuildPurpose == TerrainBlockBuildPurpose.RequestedContent)
            {
                if (!block.Desired || block.State != TerrainBlockState.FieldReady)
                {
                    continue;
                }

                if (!result.Succeeded || !result.Mesh.HasValue)
                {
                    EnqueueMeshBuildDispatch(block.Id);
                    continue;
                }

                block.SetMesh(result.Mesh.Value);
                _lastMeshBuildCount++;
                _lastMeshBuildMs += result.WorkerBuildMs;
                EnqueueCommitDispatch(block.Id);
                continue;
            }

            if (!block.DisplayedRefreshDirty || !IsBlockDisplayingVisuals(block))
            {
                continue;
            }

            if (result.DisplayedRefreshRevision != block.DisplayedRefreshRevision)
            {
                EnqueueDisplayedRefreshForCurrentState(block);
                continue;
            }

            if (!result.Succeeded || !result.Mesh.HasValue)
            {
                EnqueueMeshBuildDispatch(block.Id, urgent: true);
                continue;
            }

            block.SetDisplayedRefreshMesh(result.Mesh.Value);
            AccumulateDisplayedRefreshAsyncRebuild(result.DisplayedRefreshOperationSequence, result.WorkerBuildMs);
            EnqueueCommitDispatch(block.Id, urgent: true);
        }
    }

    private void ProcessMeshCommitDispatch()
    {
        int commitBudget = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame);
        List<TerrainBlockId> deferredCommitRequeues = new();
        while (_lastCommitCount < commitBudget &&
               TryDequeueBlockDispatch(_commitDispatcherQueue, _commitDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
            {
                continue;
            }

            if (block.State == TerrainBlockState.MeshReady)
            {
                if (!block.Desired)
                {
                    EnqueueReleaseDispatch(blockId);
                    continue;
                }

                if (TryResolveCoherentPromotionBatch(block.Id, out TerrainBlockId outgoingParent, out List<TerrainBlockId> promotionBatch, out bool waitForBatch))
                {
                    if (waitForBatch)
                    {
                        deferredCommitRequeues.Add(block.Id);
                        continue;
                    }

                    int committedCount = CommitVisibleMeshBatch(promotionBatch, outgoingParent);
                    if (committedCount > 0)
                    {
                        continue;
                    }
                }

                CommitVisibleMeshBlock(block);
                continue;
            }

            if (!block.HasDisplayedRefreshMeshReady || !IsBlockDisplayingVisuals(block))
            {
                continue;
            }

            CommitDisplayedRefreshBlock(block);
        }

        foreach (TerrainBlockId deferredBlockId in deferredCommitRequeues)
        {
            if (_blocks.TryGetValue(deferredBlockId, out TerrainBlockData deferredBlock) &&
                deferredBlock.Desired &&
                deferredBlock.State == TerrainBlockState.MeshReady)
            {
                EnqueueCommitDispatch(deferredBlockId);
            }
        }
    }

    private void CommitDisplayedRefreshBlock(TerrainBlockData block)
    {
        if (block.Renderer == null || !IsInstanceValid(block.Renderer))
        {
            block.CancelPendingData();
            return;
        }

        VoxelMeshBuildResult mesh = block.Mesh;
        TerrainLodSuccessorCoverageStatus? coverage = block.State == TerrainBlockState.Releasable
            ? EvaluateSuccessorCoverage(block.Id)
            : null;
        bool shouldMaintainCollision = ShouldMaintainCollisionCoverage(block, mesh.TotalTriangleCount, coverage);
        bool queueCollisionRefresh = shouldMaintainCollision || block.Renderer.HasCollision;
        long operationSequence = block.DisplayedRefreshOperationSequence;
        ulong applyStartUsec = Time.GetTicksUsec();
        block.Renderer.ApplyVisualMesh(mesh, _activeTerrainDebugView, _surfaceColorizer);
        block.RefreshDisplayedContent(mesh, collisionPending: queueCollisionRefresh);
        MarkVisibleMixedLodSeamsDirtyAround(block.Id);
        double applyMs = (Time.GetTicksUsec() - applyStartUsec) / 1000.0;
        _lastCommitMs += applyMs;
        _lastCommitCount++;
        AccumulateDisplayedRefreshVisualApply(operationSequence, applyMs, mesh.TotalTriangleCount);
        if (queueCollisionRefresh)
        {
            block.SetPendingCollisionRefreshOperation(operationSequence);
            EnqueueCollisionDispatch(block.Id);
        }

        _lastCommitSummary =
            $"{block.Id} edit_refresh_async tri {mesh.TotalTriangleCount} {(queueCollisionRefresh ? "collision_queued" : "visual_only")}";
    }

    private void RefreshCollisionCoverage()
    {
        foreach (TerrainBlockData block in _blocks.Values)
        {
            TerrainLodSuccessorCoverageStatus? coverage = block.State == TerrainBlockState.Releasable
                ? EvaluateSuccessorCoverage(block.Id)
                : null;
            if (block.Renderer == null ||
                !IsInstanceValid(block.Renderer) ||
                block.DisplayedRefreshDirty ||
                block.CollisionPending ||
                !ShouldMaintainCollisionCoverage(block, coverage) ||
                block.Renderer.HasCollision)
            {
                continue;
            }

            block.MarkCollisionPending();
            EnqueueCollisionDispatch(block.Id);
        }
    }

    private void ProcessCollisionDispatch()
    {
        int collisionBudget = Mathf.Clamp(CollisionBuildsPerFrame, 1, MaxCollisionBuildsPerFrame);
        while (_lastCollisionCount < collisionBudget &&
               TryDequeueBlockDispatch(_collisionDispatcherQueue, _collisionDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                (block.State != TerrainBlockState.Visible && block.State != TerrainBlockState.Releasable))
            {
                continue;
            }

            TerrainLodSuccessorCoverageStatus? coverage = block.State == TerrainBlockState.Releasable
                ? EvaluateSuccessorCoverage(block.Id)
                : null;
            bool includeCollision = ShouldMaintainCollisionCoverage(block, coverage);
            ulong collisionStart = Time.GetTicksUsec();
            block.Renderer.ApplyCollision(includeCollision);
            double collisionMs = (Time.GetTicksUsec() - collisionStart) / 1000.0;
            _lastCollisionMs += collisionMs;
            _lastCollisionCount++;
            long refreshOperationSequence = block.ConsumePendingCollisionRefreshOperation();
            block.MarkCollisionReady();
            AccumulateDisplayedRefreshCollisionRebuild(refreshOperationSequence, collisionMs);
            if (block.State == TerrainBlockState.Visible && block.Desired)
            {
                RecordReplacementCollisionReady(block.Id);
                TryHideSupersededCoverageAround(block.Id);
            }
        }
    }

    private void ProcessReleaseDispatch()
    {
        int releaseBudget = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame);
        int remainingAttempts = _releaseDispatchTokens.Count;
        if (_lastReleaseCount >= releaseBudget || remainingAttempts <= 0)
        {
            return;
        }

        // Hold deferred blocks until the end of this pass so a single blocked head entry cannot
        // immediately resurface and consume the bounded scan budget again.
        List<TerrainBlockId> deferredRequeues = new(remainingAttempts);
        int bypassedDeferredBlockCount = 0;

        while (_lastReleaseCount < releaseBudget &&
               remainingAttempts > 0 &&
               TryDequeueBlockDispatch(_releaseDispatcherQueue, _releaseDispatchTokens, out TerrainBlockId blockId))
        {
            remainingAttempts--;
            if (bypassedDeferredBlockCount > 0)
            {
                _lastReleaseHeadOfLineAvoidedCount += bypassedDeferredBlockCount;
                _releaseHeadOfLineAvoidedCount += bypassedDeferredBlockCount;
                bypassedDeferredBlockCount = 0;
            }

            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
            {
                continue;
            }

            if (block.Desired)
            {
                if (block.State == TerrainBlockState.Releasable)
                {
                    block.RestoreVisibility();
                }

                continue;
            }

            if (block.State != TerrainBlockState.Releasable)
            {
                ReleaseBlock(blockId, "dropped_before_visible");
                continue;
            }

            TerrainLodSuccessorCoverageStatus coverage = EvaluateSuccessorCoverage(block.Id);
            bool visualCoverageReady = HasReadyVisualSuccessorCoverage(block.Id, coverage);
            if (visualCoverageReady)
            {
                TryHideSupersededBlock(block.Id, coverage);
            }

            if (block.IsHeldForRelease(_currentTimeSeconds))
            {
                _lastReleaseHysteresisDeferralCount++;
                _releaseHysteresisDeferralCount++;
                ObserveSupersededBlockTransition(
                    block.Id,
                    block,
                    ResolveReleaseDeferralReason(block, coverage),
                    coverage);
                QueueDeferredRelease(blockId, deferredRequeues);
                bypassedDeferredBlockCount++;
                continue;
            }

            bool physicsCoverageReady = HasReadyPhysicsSuccessorCoverage(block.Id, coverage);
            if (!visualCoverageReady || !physicsCoverageReady)
            {
                _lastReleaseCoverageDeferralCount++;
                _releaseCoverageDeferralCount++;
                ObserveSupersededBlockTransition(
                    block.Id,
                    block,
                    ResolveReleaseDeferralReason(block, coverage),
                    coverage);
                QueueDeferredRelease(blockId, deferredRequeues);
                bypassedDeferredBlockCount++;
                continue;
            }

            ObserveSupersededBlockTransition(block.Id, block, "release_ready", coverage);
            ulong releaseStart = Time.GetTicksUsec();
            ReleaseBlock(block.Id, "fell_outside_desired_set");
            _lastReleaseMs += (Time.GetTicksUsec() - releaseStart) / 1000.0;
        }

        foreach (TerrainBlockId deferredBlockId in deferredRequeues)
        {
            EnqueueReleaseDispatch(deferredBlockId);
        }
    }

    private void ReleaseBlock(TerrainBlockId blockId, string reason)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            return;
        }

        RecordSupersededBlockReleased(blockId, block, reason);
        _blocks.Remove(blockId);

        if (_startupBlocks.Contains(blockId))
        {
            _startupSatisfiedBlocks.Add(blockId);
        }

        block.CancelPendingData();
        block.Renderer.QueueFree();
        RemoveBlockFromDispatcherQueues(blockId);
        MarkVisibleMixedLodSeamsDirtyAround(blockId);
        _lastReleaseCount++;
        _recentReleaseTimes.Enqueue(_currentTimeSeconds);
        _lastReleaseSummary = $"{blockId} {reason}";
    }

    private void RefreshAllVisibleMixedLodSeams()
    {
        List<TerrainBlockId> blockIds = new();
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (IsBlockDisplayingVisuals(block))
            {
                blockIds.Add(block.Id);
            }
        }

        blockIds.Sort(CompareTerrainBlockIds);
        foreach (TerrainBlockId blockId in blockIds)
        {
            RefreshVisibleMixedLodSeam(blockId);
        }
    }

    private void MarkAllVisibleMixedLodSeamsDirty()
    {
        _allVisibleMixedLodSeamsDirty = true;
        _dirtyVisibleMixedLodSeamBlocks.Clear();
    }

    private void MarkVisibleMixedLodSeamsDirtyAround(TerrainBlockId changedBlockId)
    {
        if (_allVisibleMixedLodSeamsDirty)
        {
            return;
        }

        foreach (TerrainBlockId candidate in EnumeratePotentialSeamBlocks(changedBlockId))
        {
            _dirtyVisibleMixedLodSeamBlocks.Add(candidate);
        }
    }

    private IEnumerable<TerrainBlockId> EnumeratePotentialSeamBlocks(TerrainBlockId blockId)
    {
        HashSet<TerrainBlockId> candidates = new();
        AddSeamCandidatesAtLevel(candidates, blockId);

        if (blockId.Lod < GetCoarsestLod())
        {
            AddSeamCandidatesAtLevel(candidates, GetParentBlock(blockId));
        }

        foreach (TerrainBlockId candidate in candidates)
        {
            yield return candidate;
        }
    }

    private void RefreshVisibleMixedLodSeam(TerrainBlockId blockId)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
            !IsBlockDisplayingVisuals(block) ||
            block.Renderer == null ||
            !IsInstanceValid(block.Renderer))
        {
            return;
        }

        TerrainSeamFace requestedFaces = ResolveRequestedSeamFaces(blockId);
        if (requestedFaces == TerrainSeamFace.None)
        {
            ApplyMixedLodSeamResult(block, TerrainSeamBuildResult.None with { RequestedFaces = TerrainSeamFace.None });
            return;
        }

        VoxelMeshBuildResult baseMesh = block.Renderer.BuildVisualMeshSnapshot(_surfaceColorizer);
        ResolveMixedLodSeamInputs(
            blockId,
            requestedFaces,
            out TerrainSeamFace skirtFaces,
            out Dictionary<TerrainSeamFace, TerrainSeamNeighborData> transitionNeighbors);
        TerrainSeamBuildResult seamBuild = TerrainSeamMesher.BuildMixedLodSeams(
            _config,
            blockId,
            TerrainMetrics.GetBlockOrigin(_config, blockId),
            baseMesh,
            requestedFaces,
            skirtFaces,
            transitionNeighbors);
        ApplyMixedLodSeamResult(block, seamBuild);
    }

    private TerrainSeamFace ResolveRequestedSeamFaces(TerrainBlockId blockId)
    {
        TerrainSeamFace faces = TerrainSeamFace.None;
        foreach ((TerrainSeamFace face, Vector3I offset) in SeamNeighborDirections)
        {
            TerrainBlockId sameLodNeighbor = new(blockId.Lod, blockId.Index + offset);
            if (HasVisibleSameLodCoverage(sameLodNeighbor))
            {
                continue;
            }

            bool needsSeam = false;
            if (blockId.Lod > FinestTerrainLod &&
                HasVisibleDirectFinerCoverage(sameLodNeighbor) &&
                !ShouldSuppressMixedLodSeamInsideStableRings(blockId, sameLodNeighbor))
            {
                needsSeam = true;
            }

            if (!needsSeam &&
                TryGetVisibleCoarseNeighborForChildFace(blockId, face, out TerrainBlockId coarseNeighbor) &&
                !ShouldSuppressMixedLodSeamInsideStableRings(blockId, coarseNeighbor))
            {
                needsSeam = true;
            }

            if (needsSeam)
            {
                faces |= face;
            }
        }

        return faces;
    }

    private void ResolveMixedLodSeamInputs(
        TerrainBlockId blockId,
        TerrainSeamFace requestedFaces,
        out TerrainSeamFace skirtFaces,
        out Dictionary<TerrainSeamFace, TerrainSeamNeighborData> transitionNeighbors)
    {
        skirtFaces = TerrainSeamFace.None;
        transitionNeighbors = new Dictionary<TerrainSeamFace, TerrainSeamNeighborData>();
        bool preferTransitionMeshes = MixedLodSeamMode.PrefersTransitionMeshes();

        foreach ((TerrainSeamFace face, _) in SeamNeighborDirections)
        {
            if ((requestedFaces & face) == 0)
            {
                continue;
            }

            // Requested mixed-LOD faces keep skirt fallback unless a conservative direct coarse-neighbor
            // transition succeeds later in the build.
            skirtFaces |= face;

            if (preferTransitionMeshes &&
                TerrainSeamMesher.SupportsTransitionFace(face) &&
                TryGetVisibleCoarseNeighborForChildFace(blockId, face, out TerrainBlockId coarseNeighbor) &&
                TryBuildVisibleSeamNeighborData(coarseNeighbor, out TerrainSeamNeighborData neighborData))
            {
                transitionNeighbors[face] = neighborData;
            }
        }
    }

    private bool TryBuildVisibleSeamNeighborData(TerrainBlockId blockId, out TerrainSeamNeighborData neighborData)
    {
        neighborData = default;
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
            !IsBlockDisplayingVisuals(block) ||
            block.Renderer == null ||
            !IsInstanceValid(block.Renderer))
        {
            return false;
        }

        VoxelMeshBuildResult mesh = block.Renderer.BuildVisualMeshSnapshot(_surfaceColorizer);
        if (!mesh.HasGeometry)
        {
            return false;
        }

        neighborData = new TerrainSeamNeighborData(
            blockId,
            TerrainMetrics.GetBlockOrigin(_config, blockId),
            mesh);
        return true;
    }

    private void ApplyMixedLodSeamResult(TerrainBlockData block, TerrainSeamBuildResult seamBuild)
    {
        block.Renderer.UpdateSeamMesh(seamBuild.Mesh);
        block.SetSeamBuild(seamBuild);
        _lastMixedLodSeamSummary = BuildMixedLodSeamSummary(block.Id, seamBuild);
        WriteMixedLodSeamDiagnosticsLog(block.Id, seamBuild);
    }

    private void WriteMixedLodSeamDiagnosticsLog(TerrainBlockId blockId, TerrainSeamBuildResult seamBuild)
    {
        if (seamBuild.FaceDiagnostics == null || seamBuild.FaceDiagnostics.Length == 0 || !EnsureTransitionLogWriter())
        {
            return;
        }

        lock (_transitionLogLock)
        {
            _transitionLogWriter!.WriteLine(
                $"{TransitionLogPrefix} event=seam_build block={blockId} requested={TerrainSeamMesher.DescribeFaces(seamBuild.RequestedFaces)} " +
                $"generated={TerrainSeamMesher.DescribeFaces(seamBuild.GeneratedFaces)} strategy={seamBuild.Strategy} " +
                $"transition_faces={seamBuild.TransitionFaceCount} skirt_faces={seamBuild.SkirtFaceCount} skipped_faces={seamBuild.ExplicitSkipFaceCount} suppressed_faces={seamBuild.SuppressedFaceCount} " +
                $"triangles={seamBuild.Mesh.TotalTriangleCount}");

            foreach (TerrainSeamFaceDiagnostic diagnostic in seamBuild.FaceDiagnostics)
            {
                string neighborId = diagnostic.TransitionNeighborId?.ToString() ?? "none";
                _transitionLogWriter.WriteLine(
                    $"{TransitionLogPrefix} event=seam_face block={blockId} face={TerrainSeamMesher.DescribeFaces(diagnostic.Face)} " +
                    $"requested={FormatBool(diagnostic.Requested)} suppressed={FormatBool(diagnostic.Suppressed)} transition_neighbor={neighborId} " +
                    $"transition_attempted={FormatBool(diagnostic.TransitionAttempted)} transition_succeeded={FormatBool(diagnostic.TransitionSucceeded)} " +
                    $"skirt_fallback={FormatBool(diagnostic.SkirtFallbackEnabled)} final_mode={TerrainSeamMesher.GetDisplayName(diagnostic.FinalMode)}");
            }
        }
    }

    private bool ShouldSuppressMixedLodSeamInsideStableRings(TerrainBlockId blockId, TerrainBlockId mixedNeighborParent)
    {
        return false;
    }

    private bool HasVisibleSameLodCoverage(TerrainBlockId blockId)
    {
        return _blocks.TryGetValue(blockId, out TerrainBlockData block) &&
               IsBlockDisplayingVisuals(block);
    }

    private bool HasVisibleDirectFinerCoverage(TerrainBlockId coarseBlockId)
    {
        if (coarseBlockId.Lod <= FinestTerrainLod)
        {
            return false;
        }

        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(coarseBlockId))
        {
            if (HasVisibleSameLodCoverage(child))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetVisibleCoarseNeighborForChildFace(
        TerrainBlockId childBlockId,
        TerrainSeamFace face,
        out TerrainBlockId coarseNeighbor)
    {
        coarseNeighbor = default;
        if (childBlockId.Lod <= FinestTerrainLod || !IsChildOnParentOuterFace(childBlockId, face))
        {
            return false;
        }

        TerrainBlockId parent = GetParentBlock(childBlockId);
        coarseNeighbor = new TerrainBlockId(parent.Lod, parent.Index + GetSeamFaceOffset(face));
        return HasVisibleSameLodCoverage(coarseNeighbor);
    }

    private static Vector3I GetSeamFaceOffset(TerrainSeamFace face)
    {
        return face switch
        {
            TerrainSeamFace.NegativeX => new Vector3I(-1, 0, 0),
            TerrainSeamFace.PositiveX => new Vector3I(1, 0, 0),
            TerrainSeamFace.NegativeY => new Vector3I(0, -1, 0),
            TerrainSeamFace.PositiveY => new Vector3I(0, 1, 0),
            TerrainSeamFace.NegativeZ => new Vector3I(0, 0, -1),
            TerrainSeamFace.PositiveZ => new Vector3I(0, 0, 1),
            _ => Vector3I.Zero
        };
    }

    private static bool IsChildOnParentOuterFace(TerrainBlockId childBlockId, TerrainSeamFace face)
    {
        TerrainBlockId parent = GetParentBlock(childBlockId);
        Vector3I childLocalIndex = childBlockId.Index - (parent.Index * 2);
        return face switch
        {
            TerrainSeamFace.NegativeX => childLocalIndex.X == 0,
            TerrainSeamFace.PositiveX => childLocalIndex.X == 1,
            TerrainSeamFace.NegativeY => childLocalIndex.Y == 0,
            TerrainSeamFace.PositiveY => childLocalIndex.Y == 1,
            TerrainSeamFace.NegativeZ => childLocalIndex.Z == 0,
            TerrainSeamFace.PositiveZ => childLocalIndex.Z == 1,
            _ => false
        };
    }

    private void AddSeamCandidatesAtLevel(HashSet<TerrainBlockId> candidates, TerrainBlockId blockId)
    {
        candidates.Add(blockId);
        foreach ((_, Vector3I offset) in SeamNeighborDirections)
        {
            TerrainBlockId sameLodNeighbor = new(blockId.Lod, blockId.Index + offset);
            candidates.Add(sameLodNeighbor);
        }

        if (blockId.Lod <= FinestTerrainLod)
        {
            return;
        }

        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(blockId))
        {
            candidates.Add(child);
        }
    }

    private void RecordRefinementHandoff(
        TerrainBlockId previousCenterParent,
        TerrainBlockId nextCenterParent,
        TerrainBlockId viewerParent,
        TerrainBlockId targetCenterParent)
    {
        _refinementHandoffCount++;
        Vector3I delta = nextCenterParent.Index - previousCenterParent.Index;
        _lastRefinementHandoffSummary =
            $"handoff {_refinementHandoffCount} center {previousCenterParent} -> {nextCenterParent} raw {viewerParent} target {targetCenterParent} " +
            $"delta {delta.X},{delta.Y},{delta.Z}";
    }

    private void RefreshLifecycleRates()
    {
        PruneEventQueue(_recentCreationTimes);
        PruneEventQueue(_recentReleaseTimes);
        PruneEventQueue(_recentDesiredSetChangeTimes);
        _blockCreateRatePerSecond = _recentCreationTimes.Count;
        _blockReleaseRatePerSecond = _recentReleaseTimes.Count;
        _blockSetChangeRatePerSecond = _recentDesiredSetChangeTimes.Count;
        _hysteresisRetainedBlockCount = CountHysteresisRetainedBlocks();
    }

    private void PruneEventQueue(Queue<double> timestamps)
    {
        while (timestamps.Count > 0 && (_currentTimeSeconds - timestamps.Peek()) > 1.0)
        {
            timestamps.Dequeue();
        }
    }

    private int CountHysteresisRetainedBlocks()
    {
        int retained = 0;
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (block.IsHeldForRelease(_currentTimeSeconds))
            {
                retained++;
            }
        }

        return retained;
    }

    private void ApplyDesiredSetChanges(IReadOnlySet<TerrainBlockId> desired)
    {
        if (_desiredBlocks.Count == 0)
        {
            foreach (TerrainBlockId blockId in desired)
            {
                _desiredBlocks.Add(blockId);
                HandleDesiredBlockAdded(blockId);
            }

            _lastDesiredSetChangeCount = 0;
            return;
        }

        int desiredSetChangeCount = 0;
        foreach (TerrainBlockId blockId in desired)
        {
            if (_desiredBlocks.Contains(blockId))
            {
                continue;
            }

            _desiredBlocks.Add(blockId);
            HandleDesiredBlockAdded(blockId);
            desiredSetChangeCount++;
            _recentDesiredSetChangeTimes.Enqueue(_currentTimeSeconds);
        }

        List<TerrainBlockId> removedBlocks = new();
        foreach (TerrainBlockId blockId in _desiredBlocks)
        {
            if (!desired.Contains(blockId))
            {
                removedBlocks.Add(blockId);
            }
        }

        foreach (TerrainBlockId blockId in removedBlocks)
        {
            _desiredBlocks.Remove(blockId);
            HandleDesiredBlockRemoved(blockId);
            desiredSetChangeCount++;
            _recentDesiredSetChangeTimes.Enqueue(_currentTimeSeconds);
        }

        _lastDesiredSetChangeCount = desiredSetChangeCount;
    }

    private double ComputeReleaseHoldSeconds(TerrainBlockId blockId)
    {
        if (HasDesiredDescendantCoverage(blockId))
        {
            return 0.0f;
        }

        double holdSeconds = Mathf.Max(0.0f, BlockReleaseHysteresisSeconds);
        if (blockId.Lod <= FinestTerrainLod)
        {
            holdSeconds += Mathf.Max(0.0f, RefinedBlockReleaseExtraSeconds);
        }

        return holdSeconds;
    }

    private static TerrainBlockId ComputeNextCenterStep(TerrainBlockId currentCenterParent, TerrainBlockId targetCenterParent)
    {
        if (currentCenterParent.Equals(targetCenterParent))
        {
            return currentCenterParent;
        }

        Vector3I delta = targetCenterParent.Index - currentCenterParent.Index;
        Vector3I step = new(
            Mathf.Clamp(delta.X, -1, 1),
            Mathf.Clamp(delta.Y, -1, 1),
            Mathf.Clamp(delta.Z, -1, 1));
        return new TerrainBlockId(currentCenterParent.Lod, currentCenterParent.Index + step);
    }

    private bool HasReadyVisualSuccessorCoverage(
        TerrainBlockId outgoingBlockId,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        TerrainLodSuccessorCoverageStatus coverage = coverageOverride ?? EvaluateSuccessorCoverage(outgoingBlockId);
        return coverage.VisualCoverageReady;
    }

    private bool HasReadyPhysicsSuccessorCoverage(
        TerrainBlockId outgoingBlockId,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        TerrainLodSuccessorCoverageStatus coverage = coverageOverride ?? EvaluateSuccessorCoverage(outgoingBlockId);
        return coverage.PhysicsCoverageReady;
    }

    private List<TerrainBlockId> GetDesiredSuccessors(TerrainBlockId outgoingBlockId)
    {
        List<TerrainBlockId> successors = new();
        if (TryGetDesiredAncestor(outgoingBlockId, out TerrainBlockId ancestor))
        {
            successors.Add(ancestor);
            return successors;
        }

        CollectDesiredDescendants(outgoingBlockId, successors);

        return successors;
    }

    private bool TryGetDesiredAncestor(TerrainBlockId blockId, out TerrainBlockId ancestor)
    {
        TerrainBlockId current = blockId;
        while (current.Lod < GetCoarsestLod())
        {
            current = GetParentBlock(current);
            if (_desiredBlocks.Contains(current))
            {
                ancestor = current;
                return true;
            }
        }

        ancestor = default;
        return false;
    }

    private void CollectDesiredDescendants(TerrainBlockId blockId, List<TerrainBlockId> successors)
    {
        if (blockId.Lod <= FinestTerrainLod)
        {
            return;
        }

        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(blockId))
        {
            if (_desiredBlocks.Contains(child))
            {
                successors.Add(child);
                continue;
            }

            CollectDesiredDescendants(child, successors);
        }
    }

    private bool HasDesiredDescendantCoverage(TerrainBlockId blockId)
    {
        if (blockId.Lod <= FinestTerrainLod)
        {
            return false;
        }

        List<TerrainBlockId> descendants = new();
        CollectDesiredDescendants(blockId, descendants);
        return descendants.Count > 0;
    }

    private bool TryResolveCoherentPromotionBatch(
        TerrainBlockId blockId,
        out TerrainBlockId outgoingParent,
        out List<TerrainBlockId> meshReadyBatch,
        out bool waitForBatch)
    {
        outgoingParent = default;
        meshReadyBatch = null;
        waitForBatch = false;
        if (!TryGetVisibleOutgoingDirectParent(blockId, out outgoingParent))
        {
            return false;
        }

        List<TerrainBlockId> successors = GetDesiredSuccessors(outgoingParent);
        if (successors.Count <= 1 || successors.Count > MaxCoherentPromotionBatchSuccessors)
        {
            return false;
        }

        meshReadyBatch = new List<TerrainBlockId>(successors.Count);
        foreach (TerrainBlockId successorId in successors)
        {
            if (!_blocks.TryGetValue(successorId, out TerrainBlockData successor) || !successor.Desired)
            {
                meshReadyBatch = null;
                return false;
            }

            if (successor.State == TerrainBlockState.Visible)
            {
                continue;
            }

            if (successor.State != TerrainBlockState.MeshReady)
            {
                meshReadyBatch = null;
                waitForBatch = true;
                return true;
            }

            meshReadyBatch.Add(successorId);
        }

        if (meshReadyBatch.Count == 0)
        {
            meshReadyBatch = null;
            return false;
        }

        return true;
    }

    private bool TryGetVisibleOutgoingDirectParent(TerrainBlockId childBlockId, out TerrainBlockId outgoingParent)
    {
        outgoingParent = default;
        if (childBlockId.Lod >= GetCoarsestLod())
        {
            return false;
        }

        TerrainBlockId parentId = GetParentBlock(childBlockId);
        if (!_blocks.TryGetValue(parentId, out TerrainBlockData parent) ||
            parent.Desired ||
            parent.State != TerrainBlockState.Releasable ||
            !IsBlockDisplayingVisuals(parent))
        {
            return false;
        }

        outgoingParent = parentId;
        return true;
    }

    private static TerrainBlockId GetParentBlock(TerrainBlockId childBlockId)
    {
        Vector3I parentIndex = new(
            Mathf.FloorToInt(childBlockId.Index.X / 2.0f),
            Mathf.FloorToInt(childBlockId.Index.Y / 2.0f),
            Mathf.FloorToInt(childBlockId.Index.Z / 2.0f));
        return new TerrainBlockId(childBlockId.Lod + 1, parentIndex);
    }

    private static TerrainBlockId GetAncestorBlock(TerrainBlockId childBlockId, int ancestorLod)
    {
        TerrainBlockId current = childBlockId;
        while (current.Lod < ancestorLod)
        {
            current = GetParentBlock(current);
        }

        return current;
    }

    private int[] CountBlocksByLod(IReadOnlyCollection<TerrainBlockId> blocks)
    {
        int[] counts = new int[GetCoarsestLod() + 1];
        foreach (TerrainBlockId block in blocks)
        {
            if (block.Lod >= FinestTerrainLod && block.Lod < counts.Length)
            {
                counts[block.Lod]++;
            }
        }

        return counts;
    }

    private int[] CountSplitParentsByLod(IReadOnlyDictionary<int, HashSet<TerrainBlockId>> splitParentsByLod)
    {
        int[] counts = new int[GetCoarsestLod() + 1];
        foreach (KeyValuePair<int, HashSet<TerrainBlockId>> pair in splitParentsByLod)
        {
            if (pair.Key >= FinestTerrainLod && pair.Key < counts.Length)
            {
                counts[pair.Key] = pair.Value.Count;
            }
        }

        return counts;
    }

    private int GetLodBlockCount(int lod)
    {
        return lod >= FinestTerrainLod && lod < _currentLodBlockCounts.Length
            ? _currentLodBlockCounts[lod]
            : 0;
    }

    private int GetSplitParentCount(int parentLod)
    {
        return parentLod >= FinestTerrainLod && parentLod < _currentSplitParentCounts.Length
            ? _currentSplitParentCounts[parentLod]
            : 0;
    }

    private static void ReplaceSetMap(
        Dictionary<int, HashSet<TerrainBlockId>> destination,
        IReadOnlyDictionary<int, HashSet<TerrainBlockId>> source)
    {
        destination.Clear();
        foreach (KeyValuePair<int, HashSet<TerrainBlockId>> pair in source)
        {
            destination[pair.Key] = new HashSet<TerrainBlockId>(pair.Value);
        }
    }

    private static int ComputeCenterRecenteringDelta(int delta, int bubbleRadius)
    {
        int magnitude = Mathf.Abs(delta) - bubbleRadius;
        if (magnitude <= 0)
        {
            return 0;
        }

        return delta < 0 ? -magnitude : magnitude;
    }

    private bool IsCurrentFieldBuildResult(CompletedFieldBuildResult result)
    {
        return _blocks.TryGetValue(result.BlockId, out TerrainBlockData block) &&
               block.MatchesFieldBuild(result.InstanceVersion, result.Revision) &&
               block.Desired &&
               block.State == TerrainBlockState.Requested;
    }

    private bool IsCurrentMeshBuildResult(CompletedMeshBuildResult result)
    {
        return _blocks.TryGetValue(result.BlockId, out TerrainBlockData block) &&
               block.MatchesMeshBuild(result.InstanceVersion, result.Revision) &&
               block.Desired &&
               block.State == TerrainBlockState.FieldReady;
    }

    private void HandleDesiredBlockAdded(TerrainBlockId blockId)
    {
        CancelSupersededBlockTransition(blockId, "desired_restored");
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            EnqueueCreateDispatch(blockId);
            return;
        }

        block.Desired = true;
        InvalidateBlockDispatch(_releaseDispatchTokens, blockId);
        if (block.State == TerrainBlockState.Releasable)
        {
            block.RestoreVisibility();
            if (block.Renderer != null &&
                IsInstanceValid(block.Renderer) &&
                !block.Renderer.HasVisuals &&
                block.Renderer.HasCachedVisualData)
            {
                block.Renderer.RestoreCachedVisuals(_activeTerrainDebugView, _surfaceColorizer);
            }

            if (block.Renderer != null &&
                IsInstanceValid(block.Renderer) &&
                block.Renderer.HasVisuals)
            {
                RecordReplacementVisualsReady(block.Id);
                TryHideSupersededCoverageAround(block.Id);
            }

            MarkVisibleMixedLodSeamsDirtyAround(blockId);
            EnqueueDispatcherForCurrentState(block);
            return;
        }

        EnqueueDispatcherForCurrentState(block);
    }

    private void HandleDesiredBlockRemoved(TerrainBlockId blockId)
    {
        InvalidateBlockDispatch(_createDispatchTokens, blockId);
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            return;
        }

        BeginSupersededBlockTransition(block.Id, block.State);
        InvalidateBlockDispatch(_fieldBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_meshBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_commitDispatchTokens, blockId);
        InvalidateBlockDispatch(_collisionDispatchTokens, blockId);
        if (block.State == TerrainBlockState.Visible)
        {
            // Desired-set transitions only enqueue state changes here; the actual renderer and mesh work is pulled
            // later through the dispatcher queues in small per-frame slices.
            block.MarkReleasable(_currentTimeSeconds + ComputeReleaseHoldSeconds(block.Id));
            RecordSupersededBlockMarkedReleasable(block.Id, block);
            TryHideSupersededBlock(block.Id);
        }
        else
        {
            block.Desired = false;
            ObserveSupersededBlockTransition(block.Id, block, $"removed_while_{SanitizeState(block.State)}");
        }

        EnqueueReleaseDispatch(blockId);
    }

    private void EnqueueDispatcherForCurrentState(TerrainBlockData block)
    {
        switch (block.State)
        {
            case TerrainBlockState.Requested:
                EnqueueFieldBuildDispatch(block.Id);
                break;
            case TerrainBlockState.FieldReady:
                EnqueueMeshBuildDispatch(block.Id);
                break;
            case TerrainBlockState.MeshReady:
                EnqueueCommitDispatch(block.Id);
                break;
            case TerrainBlockState.Visible:
                EnqueueDisplayedRefreshForCurrentState(block);
                if (!block.DisplayedRefreshDirty &&
                    (block.CollisionPending ||
                     (block.TriangleCount > 0 && ShouldIncludeCollision(block.Id) && !block.Renderer.HasCollision)))
                {
                    block.MarkCollisionPending();
                    EnqueueCollisionDispatch(block.Id);
                }
                break;
            case TerrainBlockState.Releasable:
                EnqueueDisplayedRefreshForCurrentState(block);
                EnqueueReleaseDispatch(block.Id);
                break;
        }
    }

    private void EnqueueCreateDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_createDispatcherQueue, _createDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueFieldBuildDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        EnqueueBlockDispatch(_fieldBuildDispatcherQueue, _fieldBuildDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueMeshBuildDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        EnqueueBlockDispatch(_meshBuildDispatcherQueue, _meshBuildDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueCommitDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        EnqueueBlockDispatch(_commitDispatcherQueue, _commitDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueCollisionDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        EnqueueBlockDispatch(_collisionDispatcherQueue, _collisionDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueReleaseDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_releaseDispatcherQueue, _releaseDispatchTokens, blockId, farthestFirst: true);
    }

    private void EnqueueBlockDispatch(
        PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> queue,
        Dictionary<TerrainBlockId, int> tokens,
        TerrainBlockId blockId,
        bool farthestFirst,
        bool urgent = false)
    {
        int token = ++_dispatchSequence;
        tokens[blockId] = token;
        queue.Enqueue(
            new QueuedBlockDispatchEntry(blockId, token),
            BuildDispatchPriority(blockId, farthestFirst, urgent, token));
    }

    private bool TryDequeueBlockDispatch(
        PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> queue,
        Dictionary<TerrainBlockId, int> tokens,
        out TerrainBlockId blockId)
    {
        while (queue.Count > 0)
        {
            QueuedBlockDispatchEntry entry = queue.Dequeue();
            if (!tokens.TryGetValue(entry.BlockId, out int token) || token != entry.Token)
            {
                continue;
            }
            tokens.Remove(entry.BlockId);
            blockId = entry.BlockId;
            return true;
        }

        blockId = default;
        return false;
    }

    private BlockDispatchPriority BuildDispatchPriority(TerrainBlockId blockId, bool farthestFirst, bool urgent, int token)
    {
        float distance = TerrainMetrics.DistanceSquaredToBlock(_config, blockId, _lastViewerPosition);
        return new BlockDispatchPriority(
            urgent ? 0 : 1,
            blockId.Lod,
            farthestFirst ? -distance : distance,
            token);
    }

    private bool ShouldIncludeCollision(TerrainBlockId blockId)
    {
        if (blockId.Lod > GetMaxCollisionLod())
        {
            return false;
        }

        float referenceSpan = TerrainMetrics.GetBlockSpan(_config, GetSelectionCenterLod());
        float horizontalSafetyRadius = Mathf.Max(GetSelectionCenterRadius() + 1, CollisionSafetyRadiusXZ) * referenceSpan;
        float verticalSafetyRadius = Mathf.Max(referenceSpan, Mathf.Max(0, VerticalRadius) * referenceSpan);
        Aabb bounds = TerrainMetrics.GetBlockBounds(_config, blockId);
        Vector3 min = bounds.Position;
        Vector3 max = bounds.End;
        Vector3 clamped = new(
            Mathf.Clamp(_lastViewerPosition.X, min.X, max.X),
            Mathf.Clamp(_lastViewerPosition.Y, min.Y, max.Y),
            Mathf.Clamp(_lastViewerPosition.Z, min.Z, max.Z));
        Vector3 delta = _lastViewerPosition - clamped;
        Vector2 horizontalDelta = new(delta.X, delta.Z);
        return horizontalDelta.Length() <= horizontalSafetyRadius &&
               Mathf.Abs(delta.Y) <= verticalSafetyRadius;
    }

    private bool ShouldMaintainCollisionCoverage(
        TerrainBlockData block,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        return ShouldMaintainCollisionCoverage(block, block.TriangleCount, coverageOverride);
    }

    private bool ShouldMaintainCollisionCoverage(
        TerrainBlockData block,
        int triangleCount,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        if (triangleCount <= 0 || !ShouldIncludeCollision(block.Id))
        {
            return false;
        }

        return block.State switch
        {
            TerrainBlockState.Visible => block.Desired,
            TerrainBlockState.Releasable => !HasReadyPhysicsSuccessorCoverage(block.Id, coverageOverride),
            _ => false
        };
    }

    private void RemoveBlockFromDispatcherQueues(TerrainBlockId blockId)
    {
        InvalidateBlockDispatch(_createDispatchTokens, blockId);
        InvalidateBlockDispatch(_fieldBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_meshBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_commitDispatchTokens, blockId);
        InvalidateBlockDispatch(_collisionDispatchTokens, blockId);
        InvalidateBlockDispatch(_releaseDispatchTokens, blockId);
    }

    private static void InvalidateBlockDispatch(Dictionary<TerrainBlockId, int> tokens, TerrainBlockId blockId)
    {
        tokens.Remove(blockId);
    }

    private void UpdateInitialLoadState()
    {
        if (!_selectionInitialized)
        {
            InitialLoadProgress = 0.0f;
            return;
        }

        if (_startupBlocks.Count == 0)
        {
            InitialLoadProgress = 1.0f;
            if (!_initialLoadComplete)
            {
                _initialLoadComplete = true;
                EmitSignal(SignalName.InitialLoadCompleted);
            }
            return;
        }

        InitialLoadProgress = Mathf.Clamp(
            (float)_startupSatisfiedBlocks.Count / _startupBlocks.Count,
            0.0f,
            1.0f);
        if (!_initialLoadComplete && _startupSatisfiedBlocks.Count >= _startupBlocks.Count)
        {
            _initialLoadComplete = true;
            EmitSignal(SignalName.InitialLoadCompleted);
        }
    }

    private void RefreshSupersededBlockTransitionTelemetry()
    {
        foreach (KeyValuePair<TerrainBlockId, TerrainLodSupersededBlockTransition> pair in _supersededBlockTransitions)
        {
            if (!_blocks.TryGetValue(pair.Key, out TerrainBlockData block))
            {
                continue;
            }

            ObserveSupersededBlockTransition(pair.Key, block);
        }
    }

    private void BeginSupersededBlockTransition(TerrainBlockId blockId, TerrainBlockState initialState)
    {
        TerrainLodSupersededBlockTransition transition = new(blockId, _currentTimeSeconds);
        _supersededBlockTransitions[blockId] = transition;

        if (_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            ObserveSupersededBlockTransition(blockId, block, $"desired_removed_{SanitizeState(initialState)}");
        }
    }

    private void CancelSupersededBlockTransition(TerrainBlockId blockId, string reason)
    {
        if (!_supersededBlockTransitions.Remove(blockId, out TerrainLodSupersededBlockTransition transition))
        {
            return;
        }

        TerrainLodSuccessorCoverageStatus coverage = EvaluateSuccessorCoverage(blockId);
        TerrainBlockState? state = _blocks.TryGetValue(blockId, out TerrainBlockData block)
            ? block.State
            : null;
        bool hasVisuals = block?.Renderer != null &&
                          IsInstanceValid(block.Renderer) &&
                          block.Renderer.HasVisuals;
        WriteSupersededBlockTransitionLog(
            "cancelled",
            transition,
            state,
            hasVisuals,
            coverage,
            reason);
    }

    private void RecordSupersededBlockMarkedReleasable(TerrainBlockId blockId, TerrainBlockData block)
    {
        if (!_supersededBlockTransitions.TryGetValue(blockId, out TerrainLodSupersededBlockTransition transition))
        {
            return;
        }

        if (!transition.MarkReleasableAtSeconds.HasValue)
        {
            transition.MarkReleasableAtSeconds = _currentTimeSeconds;
        }

        ObserveSupersededBlockTransition(blockId, block, "marked_releasable");
    }

    private void RecordReplacementVisualsReady(TerrainBlockId _)
    {
        RefreshSupersededBlockTransitionTelemetry();
    }

    private void RecordReplacementCollisionReady(TerrainBlockId _)
    {
        RefreshSupersededBlockTransitionTelemetry();
    }

    private void RecordSupersededBlockHidden(
        TerrainBlockId blockId,
        TerrainBlockData block,
        TerrainLodSuccessorCoverageStatus coverage)
    {
        if (!_supersededBlockTransitions.TryGetValue(blockId, out TerrainLodSupersededBlockTransition transition))
        {
            return;
        }

        CaptureSupersededCoverageMilestones(transition, block, coverage);
        if (!transition.HiddenAtSeconds.HasValue)
        {
            transition.HiddenAtSeconds = _currentTimeSeconds;
        }

        WriteSupersededBlockTransitionLog(
            "visuals_hidden",
            transition,
            block.State,
            outgoingHasVisuals: false,
            coverage,
            "visuals_hidden");
    }

    private void RecordSupersededBlockReleased(TerrainBlockId blockId, TerrainBlockData block, string releaseReason)
    {
        if (!_supersededBlockTransitions.Remove(blockId, out TerrainLodSupersededBlockTransition transition))
        {
            return;
        }

        TerrainLodSuccessorCoverageStatus coverage = EvaluateSuccessorCoverage(blockId);
        CaptureSupersededCoverageMilestones(transition, block, coverage);
        if (!transition.ReleasedAtSeconds.HasValue)
        {
            transition.ReleasedAtSeconds = _currentTimeSeconds;
        }

        WriteSupersededBlockTransitionLog(
            "released",
            transition,
            block.State,
            block.Renderer != null && IsInstanceValid(block.Renderer) && block.Renderer.HasVisuals,
            coverage,
            releaseReason);
    }

    private void ObserveSupersededBlockTransition(
        TerrainBlockId blockId,
        TerrainBlockData block,
        string reasonHint = null,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        if (!_supersededBlockTransitions.TryGetValue(blockId, out TerrainLodSupersededBlockTransition transition))
        {
            return;
        }

        TerrainLodSuccessorCoverageStatus coverage = coverageOverride ?? EvaluateSuccessorCoverage(blockId);
        CaptureSupersededCoverageMilestones(transition, block, coverage);

        string reason = string.IsNullOrWhiteSpace(reasonHint)
            ? ResolveSupersededBlockTransitionReason(block, coverage)
            : reasonHint;
        bool outgoingHasVisuals = block.Renderer != null &&
                                  IsInstanceValid(block.Renderer) &&
                                  block.Renderer.HasVisuals;
        bool changed =
            transition.LastObservedState != block.State ||
            transition.LastOutgoingHasVisuals != outgoingHasVisuals ||
            transition.LastVisualCoverageReady != coverage.VisualCoverageReady ||
            transition.LastPhysicsCoverageReady != coverage.PhysicsCoverageReady ||
            !string.Equals(transition.LastReason, reason, StringComparison.Ordinal) ||
            !string.Equals(transition.LastSuccessorIdsSummary, coverage.SuccessorIdsSummary, StringComparison.Ordinal) ||
            !string.Equals(transition.LastSuccessorLodsSummary, coverage.SuccessorLodsSummary, StringComparison.Ordinal) ||
            !string.Equals(transition.LastSuccessorStatesSummary, coverage.SuccessorStatesSummary, StringComparison.Ordinal);
        if (!changed)
        {
            return;
        }

        WriteSupersededBlockTransitionLog(
            "status",
            transition,
            block.State,
            outgoingHasVisuals,
            coverage,
            reason);
    }

    private void CaptureSupersededCoverageMilestones(
        TerrainLodSupersededBlockTransition transition,
        TerrainBlockData block,
        TerrainLodSuccessorCoverageStatus coverage)
    {
        bool outgoingHasVisuals = block.Renderer != null &&
                                  IsInstanceValid(block.Renderer) &&
                                  block.Renderer.HasVisuals;
        if (coverage.VisualCoverageReady && !transition.ReplacementVisualsReadyAtSeconds.HasValue)
        {
            transition.ReplacementVisualsReadyAtSeconds = _currentTimeSeconds;
            WriteSupersededBlockTransitionLog(
                "replacement_visuals_ready",
                transition,
                block.State,
                outgoingHasVisuals,
                coverage,
                "replacement_visuals_ready");
        }

        if (coverage.PhysicsCoverageReady && !transition.ReplacementCollisionReadyAtSeconds.HasValue)
        {
            transition.ReplacementCollisionReadyAtSeconds = _currentTimeSeconds;
            WriteSupersededBlockTransitionLog(
                "replacement_collision_ready",
                transition,
                block.State,
                outgoingHasVisuals,
                coverage,
                "replacement_collision_ready");
        }
    }

    private string ResolveSupersededBlockTransitionReason(
        TerrainBlockData block,
        TerrainLodSuccessorCoverageStatus coverage)
    {
        if (block.State != TerrainBlockState.Releasable)
        {
            return $"state_{SanitizeState(block.State)}";
        }

        if (block.Renderer == null || !IsInstanceValid(block.Renderer))
        {
            return "renderer_missing";
        }

        if (block.Renderer.HasVisuals)
        {
            if (!coverage.VisualCoverageReady)
            {
                return coverage.VisualDeferralReason;
            }

            return "ready_for_hide";
        }

        if (!coverage.PhysicsCoverageReady)
        {
            return $"visuals_hidden_{coverage.PhysicsDeferralReason}";
        }

        if (block.IsHeldForRelease(_currentTimeSeconds))
        {
            return "visuals_hidden_hysteresis_hold";
        }

        return "visuals_hidden_waiting_release";
    }

    private TerrainLodSuccessorCoverageStatus EvaluateSuccessorCoverage(TerrainBlockId outgoingBlockId)
    {
        List<TerrainBlockId> successors = GetDesiredSuccessors(outgoingBlockId);
        if (successors.Count == 0)
        {
            return new TerrainLodSuccessorCoverageStatus(
                SuccessorIdsSummary: "none",
                SuccessorLodsSummary: "none",
                SuccessorStatesSummary: "none",
                VisualCoverageReady: true,
                PhysicsCoverageReady: true,
                VisualDeferralReason: "none",
                PhysicsDeferralReason: "none");
        }

        StringBuilder successorIdsBuilder = new();
        StringBuilder successorLodsBuilder = new();
        StringBuilder successorStatesBuilder = new();
        bool visualReady = true;
        bool physicsReady = true;
        string visualReason = "ready";
        string physicsReason = "ready";

        foreach (TerrainBlockId successorId in successors)
        {
            if (successorIdsBuilder.Length > 0)
            {
                successorIdsBuilder.Append('|');
                successorLodsBuilder.Append('|');
                successorStatesBuilder.Append('|');
            }

            successorIdsBuilder.Append(successorId);
            successorLodsBuilder.Append(successorId.Lod);

            if (!_blocks.TryGetValue(successorId, out TerrainBlockData successor))
            {
                successorStatesBuilder.Append("missing");
                if (visualReady)
                {
                    visualReady = false;
                    visualReason = "successor_missing";
                }

                if (physicsReady)
                {
                    physicsReady = false;
                    physicsReason = "successor_missing";
                }

                continue;
            }

            bool hasValidRenderer = successor.Renderer != null && IsInstanceValid(successor.Renderer);
            bool requiresCollision = successor.TriangleCount > 0 && ShouldIncludeCollision(successorId);
            string collisionState = !requiresCollision
                ? "collision_skipped"
                : !hasValidRenderer
                    ? "collision_renderer_missing"
                    : successor.CollisionPending
                        ? "collision_pending"
                        : successor.Renderer.HasCollision
                            ? "collision_ready"
                            : "collision_missing";
            successorStatesBuilder.Append($"{SanitizeState(successor.State)}:{collisionState}");

            if (successor.State != TerrainBlockState.Visible)
            {
                if (visualReady)
                {
                    visualReady = false;
                    visualReason = "successor_not_visible";
                }

                if (physicsReady)
                {
                    physicsReady = false;
                    physicsReason = "successor_not_visible";
                }

                continue;
            }

            if (!requiresCollision)
            {
                continue;
            }

            if (!hasValidRenderer)
            {
                if (physicsReady)
                {
                    physicsReady = false;
                    physicsReason = "successor_collision_renderer_missing";
                }

                continue;
            }

            if (successor.CollisionPending)
            {
                if (physicsReady)
                {
                    physicsReady = false;
                    physicsReason = "successor_collision_pending";
                }

                continue;
            }

            if (!successor.Renderer.HasCollision && physicsReady)
            {
                physicsReady = false;
                physicsReason = "successor_collision_missing";
            }
        }

        return new TerrainLodSuccessorCoverageStatus(
            successorIdsBuilder.ToString(),
            successorLodsBuilder.ToString(),
            successorStatesBuilder.ToString(),
            visualReady,
            physicsReady,
            visualReason,
            physicsReason);
    }

    private SupersededTransitionProfileSummary BuildSupersededTransitionProfileSummary()
    {
        int activeCount = 0;
        int waitingForMarkReleasableCount = 0;
        int waitingForVisualCoverageCount = 0;
        int waitingForHideCount = 0;
        int waitingForPhysicsCoverageCount = 0;
        int waitingForReleaseCount = 0;
        foreach (TerrainLodSupersededBlockTransition transition in _supersededBlockTransitions.Values)
        {
            activeCount++;
            switch (ClassifySupersededTransitionWaitState(transition))
            {
                case SupersededTransitionWaitState.MarkReleasable:
                    waitingForMarkReleasableCount++;
                    break;
                case SupersededTransitionWaitState.VisualCoverage:
                    waitingForVisualCoverageCount++;
                    break;
                case SupersededTransitionWaitState.Hide:
                    waitingForHideCount++;
                    break;
                case SupersededTransitionWaitState.PhysicsCoverage:
                    waitingForPhysicsCoverageCount++;
                    break;
                case SupersededTransitionWaitState.Release:
                    waitingForReleaseCount++;
                    break;
            }
        }

        return new SupersededTransitionProfileSummary(
            activeCount,
            waitingForMarkReleasableCount,
            waitingForVisualCoverageCount,
            waitingForHideCount,
            waitingForPhysicsCoverageCount,
            waitingForReleaseCount,
            _lastSupersededTransitionSummary);
    }

    private string BuildSupersededTransitionSummary()
    {
        SupersededTransitionProfileSummary summary = BuildSupersededTransitionProfileSummary();
        return
            $"active {summary.ActiveCount}  wait r/v/h/p/f {summary.WaitingForMarkReleasableCount}/{summary.WaitingForVisualCoverageCount}/{summary.WaitingForHideCount}/{summary.WaitingForPhysicsCoverageCount}/{summary.WaitingForReleaseCount}  " +
            $"last {summary.LastSummary}";
    }

    private string BuildSupersededTransitionSummary(TerrainLodSupersededBlockTransition transition)
    {
        double ageMs = ElapsedMilliseconds(transition.RemovedAtSeconds, _currentTimeSeconds);
        return
            $"{transition.OutgoingBlockId} age {ageMs:0.0} ms  wait {transition.LastReason}  out_vis {(transition.LastOutgoingHasVisuals ? "y" : "n")}  succ {transition.LastSuccessorIdsSummary}  " +
            $"lods {transition.LastSuccessorLodsSummary}  ready v/p {(transition.LastVisualCoverageReady ? "y" : "n")}/{(transition.LastPhysicsCoverageReady ? "y" : "n")}";
    }

    private SupersededTransitionWaitState ClassifySupersededTransitionWaitState(TerrainLodSupersededBlockTransition transition)
    {
        if (!_blocks.TryGetValue(transition.OutgoingBlockId, out TerrainBlockData block) ||
            transition.MarkReleasableAtSeconds == null ||
            block.State != TerrainBlockState.Releasable)
        {
            return SupersededTransitionWaitState.MarkReleasable;
        }

        TerrainLodSuccessorCoverageStatus coverage = EvaluateSuccessorCoverage(block.Id);
        bool outgoingHasVisuals = block.Renderer != null &&
                                  IsInstanceValid(block.Renderer) &&
                                  block.Renderer.HasVisuals;
        if (outgoingHasVisuals)
        {
            return HasReadyVisualSuccessorCoverage(block.Id, coverage)
                ? SupersededTransitionWaitState.Hide
                : SupersededTransitionWaitState.VisualCoverage;
        }

        if (!HasReadyPhysicsSuccessorCoverage(block.Id, coverage))
        {
            return SupersededTransitionWaitState.PhysicsCoverage;
        }

        return SupersededTransitionWaitState.Release;
    }

    private void WriteSupersededBlockTransitionLog(
        string eventName,
        TerrainLodSupersededBlockTransition transition,
        TerrainBlockState? state,
        bool outgoingHasVisuals,
        TerrainLodSuccessorCoverageStatus coverage,
        string reason)
    {
        transition.LastObservedState = state;
        transition.LastOutgoingHasVisuals = outgoingHasVisuals;
        transition.LastVisualCoverageReady = coverage.VisualCoverageReady;
        transition.LastPhysicsCoverageReady = coverage.PhysicsCoverageReady;
        transition.LastReason = reason;
        transition.LastSuccessorIdsSummary = coverage.SuccessorIdsSummary;
        transition.LastSuccessorLodsSummary = coverage.SuccessorLodsSummary;
        transition.LastSuccessorStatesSummary = coverage.SuccessorStatesSummary;
        _lastSupersededTransitionSummary = BuildSupersededTransitionSummary(transition);

        if (!EnsureTransitionLogWriter())
        {
            return;
        }

        string line =
            $"{TransitionLogPrefix} event={eventName} out={transition.OutgoingBlockId} out_lod={transition.OutgoingBlockId.Lod} " +
            $"out_state={(state.HasValue ? SanitizeState(state.Value) : "missing")} out_has_visuals={FormatBool(outgoingHasVisuals)} " +
            $"succ={coverage.SuccessorIdsSummary} succ_lods={coverage.SuccessorLodsSummary} succ_states={coverage.SuccessorStatesSummary} " +
            $"visual_ready={FormatBool(coverage.VisualCoverageReady)} physics_ready={FormatBool(coverage.PhysicsCoverageReady)} reason={reason} " +
            $"removed_to_releasable_ms={FormatTimestamp(transition.RemovedAtSeconds, transition.MarkReleasableAtSeconds)} " +
            $"removed_to_visual_ms={FormatTimestamp(transition.RemovedAtSeconds, transition.ReplacementVisualsReadyAtSeconds)} " +
            $"removed_to_collision_ms={FormatTimestamp(transition.RemovedAtSeconds, transition.ReplacementCollisionReadyAtSeconds)} " +
            $"visual_to_hide_ms={FormatTimestamp(transition.ReplacementVisualsReadyAtSeconds, transition.HiddenAtSeconds)} " +
            $"visual_to_release_ms={FormatTimestamp(transition.ReplacementVisualsReadyAtSeconds, transition.ReleasedAtSeconds)} " +
            $"removed_to_hide_ms={FormatTimestamp(transition.RemovedAtSeconds, transition.HiddenAtSeconds)} " +
            $"removed_to_release_ms={FormatTimestamp(transition.RemovedAtSeconds, transition.ReleasedAtSeconds)} " +
            $"age_ms={ElapsedMilliseconds(transition.RemovedAtSeconds, _currentTimeSeconds).ToString("0.000", CultureInfo.InvariantCulture)}";
        lock (_transitionLogLock)
        {
            _transitionLogWriter!.WriteLine(line);
        }
    }

    private bool EnsureTransitionLogWriter()
    {
        if (!OS.IsDebugBuild())
        {
            return false;
        }

        if (_transitionLogWriter != null)
        {
            return true;
        }

        try
        {
            string rootPath = ProjectSettings.GlobalizePath("user://profiling");
            Directory.CreateDirectory(rootPath);
            string logPath = ProjectSettings.GlobalizePath(TransitionLogRelativePath);
            _transitionLogWriter = new StreamWriter(
                new FileStream(logPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            lock (_transitionLogLock)
            {
                _transitionLogWriter.WriteLine(
                    $"{TransitionLogPrefix} event=session_begin utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} path=\"{logPath}\"");
            }

            _warnedTransitionLogFailure = false;
            return true;
        }
        catch (Exception exception)
        {
            _transitionLogWriter?.Dispose();
            _transitionLogWriter = null;
            if (!_warnedTransitionLogFailure)
            {
                GD.PushWarning(
                    $"TerrainLodManager could not open transition telemetry log at {TransitionLogRelativePath}: {exception.Message}");
                _warnedTransitionLogFailure = true;
            }

            return false;
        }
    }

    private void CloseTransitionLogWriter()
    {
        if (_transitionLogWriter == null)
        {
            return;
        }

        try
        {
            lock (_transitionLogLock)
            {
                _transitionLogWriter.WriteLine(
                    $"{TransitionLogPrefix} event=session_end utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
                _transitionLogWriter.Dispose();
            }
        }
        finally
        {
            _transitionLogWriter = null;
        }
    }

    private static double ElapsedMilliseconds(double startedAtSeconds, double finishedAtSeconds)
    {
        return Math.Max(0.0, (finishedAtSeconds - startedAtSeconds) * 1000.0);
    }

    private void QueueDeferredRelease(TerrainBlockId blockId, List<TerrainBlockId> deferredRequeues)
    {
        _lastReleaseRequeueCount++;
        _releaseRequeueCount++;

        double deferredAgeMs = GetDeferredReleaseAgeMs(blockId);
        _lastReleaseDeferredAgeSampleCount++;
        _lastReleaseDeferredAgeMsTotal += deferredAgeMs;
        _releaseDeferredAgeSampleCount++;
        _releaseDeferredAgeMsTotal += deferredAgeMs;

        deferredRequeues.Add(blockId);
    }

    private double GetDeferredReleaseAgeMs(TerrainBlockId blockId)
    {
        return _supersededBlockTransitions.TryGetValue(blockId, out TerrainLodSupersededBlockTransition transition)
            ? ElapsedMilliseconds(transition.RemovedAtSeconds, _currentTimeSeconds)
            : 0.0;
    }

    private double ComputeAverageDeferredReleaseAgeMs(long sampleCount, double totalAgeMs)
    {
        return sampleCount > 0
            ? totalAgeMs / sampleCount
            : 0.0;
    }

    private static string FormatTimestamp(double? startedAtSeconds, double? finishedAtSeconds)
    {
        return startedAtSeconds.HasValue && finishedAtSeconds.HasValue
            ? ElapsedMilliseconds(startedAtSeconds.Value, finishedAtSeconds.Value).ToString("0.000", CultureInfo.InvariantCulture)
            : "na";
    }

    private static string FormatTimestamp(double startedAtSeconds, double? finishedAtSeconds)
    {
        return FormatTimestamp((double?)startedAtSeconds, finishedAtSeconds);
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string SanitizeState(TerrainBlockState state)
    {
        return state switch
        {
            TerrainBlockState.Requested => "requested",
            TerrainBlockState.FieldReady => "field_ready",
            TerrainBlockState.MeshReady => "mesh_ready",
            TerrainBlockState.Visible => "visible",
            TerrainBlockState.Releasable => "releasable",
            _ => state.ToString().ToLowerInvariant()
        };
    }

    private TerrainWorldProfileSnapshot BuildProfileSnapshot()
    {
        int requested = 0;
        int fieldReady = 0;
        int meshReady = 0;
        int visible = 0;
        int releasable = 0;
        SupersededTransitionProfileSummary supersededSummary = BuildSupersededTransitionProfileSummary();
        MixedLodSeamProfileSummary seamSummary = BuildMixedLodSeamProfileSummary();

        foreach (TerrainBlockData block in _blocks.Values)
        {
            switch (block.State)
            {
                case TerrainBlockState.Requested:
                    requested++;
                    break;
                case TerrainBlockState.FieldReady:
                    fieldReady++;
                    break;
                case TerrainBlockState.MeshReady:
                    meshReady++;
                    break;
                case TerrainBlockState.Visible:
                    visible++;
                    break;
                case TerrainBlockState.Releasable:
                    releasable++;
                    break;
            }
        }

        string viewerSummary = _trackedCharacter == null
            ? "viewer missing"
            : $"viewer {_lastViewerPosition.X:0.0},{_lastViewerPosition.Y:0.0},{_lastViewerPosition.Z:0.0}  debug {_activeTerrainDebugView.GetDisplayName()}";
        string centerSummary = _currentCenterParent.Equals(_targetCenterParent)
            ? _currentCenterParent.ToString()
            : $"{_currentCenterParent}->{_targetCenterParent}";
        string lodSummary =
            $"{BuildLodSpanSummary()}  raw {_currentViewerParent}  center {centerSummary}  split_r {BuildSplitRadiusSummary()}  " +
            $"coarse_r {_currentCoarsestRadius}  move_pad {BubbleMovePaddingFraction:0.00}  debug {_activeTerrainDebugView.GetDisplayName()}  seam {MixedLodSeamMode.GetDisplayName()}";
        int activeEditRegionCount = _editRegionManager?.RegionCount ?? 0;
        int activeEditStampCount = _editRegionManager?.StampCount ?? 0;
        int maxEditRegionDetailLevel = _editRegionManager?.MaxDetailLevel ?? 0;
        string activeEditRegionSummary = _editRegionManager?.BuildDebugSummary() ?? "none";
        VisibleEditedBlockProfileSummary visibleEditedBlocks = BuildVisibleEditedBlockProfileSummary();

        return new TerrainWorldProfileSnapshot
        {
            TerrainStatsEnabled = false,
            ActiveChunkCount = visible,
            ResidentChunkCount = _blocks.Count,
            LoadedChunkCount = fieldReady + meshReady + visible + releasable,
            DesiredChunkCount = _lastDesiredBlockCount,
            PendingLoadCount = requested,
            RunningLoadCount = Volatile.Read(ref _activeFieldWorkerJobs),
            PreparedChunkCount = fieldReady,
            PendingMeshBuildCount = fieldReady,
            RunningMeshBuildCount = Volatile.Read(ref _activeMeshWorkerJobs),
            PendingMeshCommitCount = meshReady,
            ToReleaseCount = releasable,
            LastChunkLoadCount = _lastFieldBuildCount,
            LastChunkLoadMs = _lastFieldBuildMs,
            LastMeshWorkerBuildCount = _lastMeshBuildCount,
            LastMeshWorkerBuildMs = _lastMeshBuildMs,
            LastChunkActivationCount = _lastCommitCount,
            LastChunkActivationMs = _lastCommitMs,
            LastVisualRebuildCount = _lastCommitCount,
            LastVisualRebuildMs = _lastCommitMs,
            LastCollisionRebuildCount = _lastCollisionCount,
            LastCollisionRebuildMs = _lastCollisionMs,
            LastChunkReleaseCount = _lastReleaseCount,
            LastChunkReleaseMs = _lastReleaseMs,
            MeshBackendName = "lod_blocks_v1",
            SearchThrottleState = "lod_blocks",
            DeformOperationCount = _deformOperationCount,
            TotalEditedChunkCount = _totalEditedChunkCount,
            TotalEditedSampleCount = _totalEditedSampleCount,
            TotalEditedDirtyBoundsVolume = _totalEditedDirtyBoundsVolume,
            EditDetailPromotionCount = _editDetailPromotionCount,
            LastDeformEditedChunkCount = _lastDeformEditedChunkCount,
            LastDeformEditedSampleCount = _lastDeformEditedSampleCount,
            LastDeformDirtyBoundsVolume = _lastDeformDirtyBoundsVolume,
            LastDeformEditDetailPromotionCount = _lastDeformEditDetailPromotionCount,
            LastDeformMs = _lastDeformMs,
            LastDeformKind = _lastDeformKind,
            LastEditOperationSummary = _lastEditOperationSummary,
            LastDeformVisibleBlockCount = _lastDeformVisibleBlockCount,
            LastDeformVisibleFinestBlockCount = _lastDeformVisibleFinestBlockCount,
            LastDeformRequeuedBlockCount = _lastDeformRequeuedBlockCount,
            LastDeformQueuedVisibleBlockCount = _lastDeformQueuedVisibleBlockCount,
            LastDeformRefreshedTriangleCount = _lastDeformRefreshedTriangleCount,
            LastDeformSyncRefreshMs = _lastDeformSyncWorkMs,
            LastDeformRegistrationMs = _lastDeformRegistrationMs,
            LastDeformEnqueueMs = _lastDeformEnqueueMs,
            LastDeformSyncWorkMs = _lastDeformSyncWorkMs,
            LastDeformAsyncRebuildMs = _lastDeformAsyncRebuildMs,
            LastDeformVisualApplyMs = _lastDeformVisualApplyMs,
            LastDeformCollisionRebuildMs = _lastDeformCollisionRebuildMs,
            TrackedBiomeSummary = viewerSummary,
            TrackedDetailSummary = BuildLifecycleSummary(),
            TrackedCoverageStateSummary = lodSummary,
            ActiveEditRegionCount = activeEditRegionCount,
            ActiveEditStampCount = activeEditStampCount,
            MaxEditRegionDetailLevel = maxEditRegionDetailLevel,
            ActiveEditRegionSummary = activeEditRegionSummary,
            VisibleEditedStickyRegionCount = visibleEditedBlocks.StickyRegionCount,
            VisibleEditedBlockCount = visibleEditedBlocks.VisibleBlockCount,
            VisibleEditedFinestBlockCount = visibleEditedBlocks.FinestBlockCount,
            VisibleEditedLaggingBlockCount = visibleEditedBlocks.LaggingBlockCount,
            VisibleEditedBlockSummary = visibleEditedBlocks.Summary,
            NearPlayerBubbleParentCount = _currentBubbleParentCount,
            RefinedParentCount = CountTotalSplitParents(),
            RefinedSameLodBlockCount = _currentRefinedSameLodBlockCount,
            HysteresisRetainedBlockCount = _hysteresisRetainedBlockCount,
            BlockCreateRatePerSecond = _blockCreateRatePerSecond,
            BlockReleaseRatePerSecond = _blockReleaseRatePerSecond,
            BlockSetChangeRatePerSecond = _blockSetChangeRatePerSecond,
            RefinementHandoffCount = _refinementHandoffCount,
            ReleaseDeferralsHysteresisCount = _releaseHysteresisDeferralCount,
            LastReleaseDeferralsHysteresisCount = _lastReleaseHysteresisDeferralCount,
            ReleaseDeferralsCoverageCount = _releaseCoverageDeferralCount,
            LastReleaseDeferralsCoverageCount = _lastReleaseCoverageDeferralCount,
            ReleaseRequeueCount = _releaseRequeueCount,
            LastReleaseRequeueCount = _lastReleaseRequeueCount,
            ReleaseHeadOfLineAvoidedCount = _releaseHeadOfLineAvoidedCount,
            LastReleaseHeadOfLineAvoidedCount = _lastReleaseHeadOfLineAvoidedCount,
            AverageReleaseDeferredAgeMs = ComputeAverageDeferredReleaseAgeMs(
                _releaseDeferredAgeSampleCount,
                _releaseDeferredAgeMsTotal),
            LastAverageReleaseDeferredAgeMs = ComputeAverageDeferredReleaseAgeMs(
                _lastReleaseDeferredAgeSampleCount,
                _lastReleaseDeferredAgeMsTotal),
            ActiveSupersededBlockTransitionCount = supersededSummary.ActiveCount,
            WaitingForMarkReleasableSupersededBlockCount = supersededSummary.WaitingForMarkReleasableCount,
            WaitingForVisualCoverageSupersededBlockCount = supersededSummary.WaitingForVisualCoverageCount,
            WaitingForPhysicsCoverageSupersededBlockCount = supersededSummary.WaitingForPhysicsCoverageCount,
            WaitingForHideSupersededBlockCount = supersededSummary.WaitingForHideCount,
            WaitingForReleaseSupersededBlockCount = supersededSummary.WaitingForReleaseCount,
            MixedLodSeamMode = MixedLodSeamMode,
            MixedLodSeamBlockCount = seamSummary.BlockCount,
            MixedLodTransitionFaceCount = seamSummary.TransitionFaceCount,
            MixedLodSkirtFaceCount = seamSummary.SkirtFaceCount,
            MixedLodSkippedFaceCount = seamSummary.ExplicitSkipFaceCount,
            MixedLodSuppressedFaceCount = seamSummary.SuppressedFaceCount,
            MixedLodSeamTriangleCount = seamSummary.TriangleCount,
            LastMixedLodSeamSummary = seamSummary.LastSummary,
            LastSupersededBlockTransitionSummary = supersededSummary.LastSummary,
            LastSelectedChunkSummary = _lastSelectionSummary,
            LastRefinementHandoffSummary = _lastRefinementHandoffSummary,
            LastReleasedChunkSummary = _lastReleaseSummary,
            LastChunkSourceSummary = _lastCommitSummary,
            InitialLoadProgress = InitialLoadProgress,
            InitialLoadComplete = _initialLoadComplete
        };
    }

    private string BuildLifecycleSummary()
    {
        int requested = 0;
        int fieldReady = 0;
        int meshReady = 0;
        int visible = 0;
        int releasable = 0;
        SupersededTransitionProfileSummary supersededSummary = BuildSupersededTransitionProfileSummary();
        MixedLodSeamProfileSummary seamSummary = BuildMixedLodSeamProfileSummary();

        foreach (TerrainBlockData block in _blocks.Values)
        {
            switch (block.State)
            {
                case TerrainBlockState.Requested:
                    requested++;
                    break;
                case TerrainBlockState.FieldReady:
                    fieldReady++;
                    break;
                case TerrainBlockState.MeshReady:
                    meshReady++;
                    break;
                case TerrainBlockState.Visible:
                    visible++;
                    break;
                case TerrainBlockState.Releasable:
                    releasable++;
                    break;
            }
        }

        return
            $"requested {requested}  field {fieldReady}  mesh {meshReady}  visible {visible}  releasable {releasable}  " +
            $"hold {_hysteresisRetainedBlockCount}  blocks {BuildTierCountSummary(_currentLodBlockCounts, 'l', FinestTerrainLod)}  " +
            $"split {BuildTierCountSummary(_currentSplitParentCounts, 'p', FinestTerrainLod + 1)}  " +
            $"seam {MixedLodSeamMode.GetDisplayName()} t/s/k/sup {seamSummary.TransitionFaceCount}/{seamSummary.SkirtFaceCount}/{seamSummary.ExplicitSkipFaceCount}/{seamSummary.SuppressedFaceCount}  " +
            $"sup a/r/v/h/p/f {supersededSummary.ActiveCount}/{supersededSummary.WaitingForMarkReleasableCount}/{supersededSummary.WaitingForVisualCoverageCount}/{supersededSummary.WaitingForHideCount}/{supersededSummary.WaitingForPhysicsCoverageCount}/{supersededSummary.WaitingForReleaseCount}  " +
            $"dispatch c{_createDispatchTokens.Count}  fq/r/d {_fieldBuildDispatchTokens.Count}/{Volatile.Read(ref _activeFieldWorkerJobs)}/{_completedFieldBuildResults.Count}  " +
            $"mq/r/d {_meshBuildDispatchTokens.Count}/{Volatile.Read(ref _activeMeshWorkerJobs)}/{_completedMeshBuildResults.Count}  " +
            $"commit {_commitDispatchTokens.Count}  coll {_collisionDispatchTokens.Count}  release {_releaseDispatchTokens.Count}  " +
            $"set/s {_blockSetChangeRatePerSecond:0.0}  create/s {_blockCreateRatePerSecond:0.0}  release/s {_blockReleaseRatePerSecond:0.0}";
    }

    private MixedLodSeamProfileSummary BuildMixedLodSeamProfileSummary()
    {
        int blockCount = 0;
        int transitionFaceCount = 0;
        int skirtFaceCount = 0;
        int explicitSkipFaceCount = 0;
        int suppressedFaceCount = 0;
        int triangleCount = 0;

        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (!IsBlockDisplayingVisuals(block))
            {
                continue;
            }

            TerrainSeamBuildResult seamBuild = block.SeamBuild;
            if (seamBuild.RequestedFaces == TerrainSeamFace.None &&
                seamBuild.GeneratedFaces == TerrainSeamFace.None)
            {
                continue;
            }

            blockCount++;
            transitionFaceCount += seamBuild.TransitionFaceCount;
            skirtFaceCount += seamBuild.SkirtFaceCount;
            explicitSkipFaceCount += seamBuild.ExplicitSkipFaceCount;
            suppressedFaceCount += seamBuild.SuppressedFaceCount;
            triangleCount += seamBuild.Mesh.TotalTriangleCount;
        }

        return new MixedLodSeamProfileSummary(
            blockCount,
            transitionFaceCount,
            skirtFaceCount,
            explicitSkipFaceCount,
            suppressedFaceCount,
            triangleCount,
            _lastMixedLodSeamSummary);
    }

    private VisibleEditedBlockProfileSummary BuildVisibleEditedBlockProfileSummary()
    {
        if (_editRegionManager == null || _editRegionManager.RegionCount == 0)
        {
            return new VisibleEditedBlockProfileSummary(0, 0, 0, 0, "none");
        }

        HashSet<string> stickyRegionIds = new();
        List<TerrainBlockId> affectedBlockIds = new();
        int finestBlockCount = 0;
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (!IsBlockDisplayingVisuals(block))
            {
                continue;
            }

            bool affectedByStickyEdit = false;
            foreach (TerrainEditRegion region in GetEditRegionsForBlock(block.Id))
            {
                if (!IsStickyEditRegion(region))
                {
                    continue;
                }

                stickyRegionIds.Add(region.Id);
                affectedByStickyEdit = true;
            }

            if (!affectedByStickyEdit)
            {
                continue;
            }

            affectedBlockIds.Add(block.Id);
            if (block.Id.Lod == FinestTerrainLod)
            {
                finestBlockCount++;
            }
        }

        if (affectedBlockIds.Count == 0)
        {
            return new VisibleEditedBlockProfileSummary(stickyRegionIds.Count, 0, 0, 0, "none");
        }

        affectedBlockIds.Sort(CompareTerrainBlockIds);
        int laggingBlockCount = affectedBlockIds.Count - finestBlockCount;
        StringBuilder summary = new();
        summary.Append("sticky ");
        summary.Append(stickyRegionIds.Count);
        summary.Append(" vis ");
        summary.Append(affectedBlockIds.Count);
        summary.Append(" finest ");
        summary.Append(finestBlockCount);
        if (laggingBlockCount > 0)
        {
            summary.Append(" lag ");
            summary.Append(laggingBlockCount);
        }

        summary.Append(" top ");
        int previewCount = Math.Min(3, affectedBlockIds.Count);
        for (int i = 0; i < previewCount; i++)
        {
            if (i > 0)
            {
                summary.Append(" | ");
            }

            summary.Append(affectedBlockIds[i]);
        }

        return new VisibleEditedBlockProfileSummary(
            stickyRegionIds.Count,
            affectedBlockIds.Count,
            finestBlockCount,
            laggingBlockCount,
            summary.ToString());
    }

    private string BuildMixedLodSeamSummary(MixedLodSeamProfileSummary summary)
    {
        return
            $"{MixedLodSeamMode.GetDisplayName()}  blocks {summary.BlockCount}  faces t/s/k/sup {summary.TransitionFaceCount}/{summary.SkirtFaceCount}/{summary.ExplicitSkipFaceCount}/{summary.SuppressedFaceCount}  " +
            $"tri {summary.TriangleCount}  last {summary.LastSummary}";
    }

    private static string BuildMixedLodSeamSummary(TerrainBlockId blockId, TerrainSeamBuildResult seamBuild)
    {
        return
            $"{blockId} mode {seamBuild.Strategy} req {TerrainSeamMesher.DescribeFaces(seamBuild.RequestedFaces)}  " +
            $"gen {TerrainSeamMesher.DescribeFaces(seamBuild.GeneratedFaces)}  t/s/k/sup {seamBuild.TransitionFaceCount}/{seamBuild.SkirtFaceCount}/{seamBuild.ExplicitSkipFaceCount}/{seamBuild.SuppressedFaceCount}  " +
            $"tri {seamBuild.Mesh.TotalTriangleCount}  faces {TerrainSeamMesher.DescribeFaceDiagnostics(seamBuild.FaceDiagnostics)}";
    }

    private string BuildSelectionSummary(
        TerrainBlockId centerParent,
        TerrainBlockId targetCenterParent,
        TerrainBlockId viewerParent,
        int desiredCount)
    {
        string centerSummary = centerParent.Equals(targetCenterParent)
            ? centerParent.ToString()
            : $"{centerParent}->{targetCenterParent}";
        return
            $"raw {viewerParent}  center {centerSummary}  desired {desiredCount}  " +
            $"split_r {BuildSplitRadiusSummary()}  " +
            $"split {BuildTierCountSummary(_currentSplitParentCounts, 'p', FinestTerrainLod + 1)}  " +
            $"blocks {BuildTierCountSummary(_currentLodBlockCounts, 'l', FinestTerrainLod)}  " +
            $"coarse_r {_currentCoarsestRadius}  held {_hysteresisRetainedBlockCount}  changed {_lastDesiredSetChangeCount}  " +
            "policy stable-center + tiered-coverage + successor-held-release.";
    }

    private int GetCoarsestLod()
    {
        return Mathf.Max(3, TierCount) - 1;
    }

    private int GetSelectionCenterLod()
    {
        return Mathf.Max(FinestTerrainLod + 1, GetCoarsestLod() - 1);
    }

    private int GetSelectionCenterRadius()
    {
        return GetStableCenterRadius(GetSelectionCenterLod());
    }

    private int GetStableCenterRadius(int parentLod)
    {
        return Mathf.Max(0, GetSplitRadiusForParentLod(parentLod));
    }

    private int GetSplitRadiusForParentLod(int parentLod)
    {
        if (parentLod <= FinestTerrainLod)
        {
            return 0;
        }

        if (parentLod == FinestTerrainLod + 1)
        {
            return GetEffectiveLod0NearFieldRadius();
        }

        // Keep the broader tier cascade intact, but once an explicit lod0 bubble is configured it should stop
        // implicitly inflating every coarser tier radius.
        int radius = UsesExplicitLod0NearFieldRadius()
            ? 0
            : GetLegacySplitRadiusForParentLod(FinestTerrainLod + 1);
        for (int currentParentLod = FinestTerrainLod + 2; currentParentLod <= parentLod; currentParentLod++)
        {
            radius = Mathf.Max(radius, GetLegacySplitRadiusForParentLod(currentParentLod));
        }

        return radius;
    }

    private int GetEffectiveLod0NearFieldRadius()
    {
        return UsesExplicitLod0NearFieldRadius()
            ? Mathf.Max(0, Lod0NearFieldRadiusXZ)
            : GetLegacySplitRadiusForParentLod(FinestTerrainLod + 1);
    }

    private bool UsesExplicitLod0NearFieldRadius()
    {
        return Lod0NearFieldRadiusXZ >= 0;
    }

    private int GetLegacySplitRadiusForParentLod(int parentLod)
    {
        int[] configuredRadii = TierSplitRadiiXZ ?? System.Array.Empty<int>();
        int configuredIndex = parentLod - 1;
        return configuredIndex >= 0 && configuredIndex < configuredRadii.Length
            ? Mathf.Max(0, configuredRadii[configuredIndex])
            : 0;
    }

    private int GetMaxCollisionLod()
    {
        return Mathf.Min(GetCoarsestLod(), FinestTerrainLod + 2);
    }

    private int CountTotalSplitParents()
    {
        int total = 0;
        for (int lod = FinestTerrainLod + 1; lod < _currentSplitParentCounts.Length; lod++)
        {
            total += _currentSplitParentCounts[lod];
        }

        return total;
    }

    private string BuildLodSpanSummary()
    {
        StringBuilder builder = new();
        for (int lod = FinestTerrainLod; lod <= GetCoarsestLod(); lod++)
        {
            if (lod > FinestTerrainLod)
            {
                builder.Append("  ");
            }

            builder.Append("lod");
            builder.Append(lod);
            builder.Append(" span ");
            builder.Append(TerrainMetrics.GetBlockSpan(_config, lod).ToString("0.0"));
        }

        return builder.ToString();
    }

    private string BuildSplitRadiusSummary()
    {
        StringBuilder builder = new();
        for (int parentLod = FinestTerrainLod + 1; parentLod <= GetCoarsestLod(); parentLod++)
        {
            if (parentLod > FinestTerrainLod + 1)
            {
                builder.Append(' ');
            }

            builder.Append('p');
            builder.Append(parentLod);
            builder.Append(':');
            builder.Append(GetSplitRadiusForParentLod(parentLod));
        }

        return builder.ToString();
    }

    private string BuildTierSelectionSummary()
    {
        StringBuilder builder = new();
        int coarsestLod = GetCoarsestLod();
        for (int leafLod = FinestTerrainLod; leafLod <= coarsestLod; leafLod++)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append("Tier lod");
            builder.Append(leafLod);
            if (leafLod >= coarsestLod)
            {
                builder.Append(" cover center ");
                builder.Append(GetAncestorBlock(_currentCenterParent, coarsestLod));
                builder.Append(" radius ");
                builder.Append(_currentCoarsestRadius);
                builder.Append(" leaves ");
                builder.Append(GetLodBlockCount(leafLod));
                continue;
            }

            int parentLod = leafLod + 1;
            builder.Append(" via p");
            builder.Append(parentLod);
            builder.Append(" center ");
            builder.Append(GetTierSplitCenterParent(parentLod));
            builder.Append(" radius ");
            builder.Append(GetSplitRadiusForParentLod(parentLod));
            builder.Append(" split ");
            builder.Append(GetSplitParentCount(parentLod));
            builder.Append(" leaves ");
            builder.Append(GetLodBlockCount(leafLod));
        }

        return builder.ToString();
    }

    private TerrainBlockId GetTierSplitCenterParent(int parentLod)
    {
        return GetEffectiveSplitBubbleCenter(parentLod);
    }

    private TerrainBlockId GetEffectiveSplitBubbleCenter(int parentLod)
    {
        return parentLod <= GetSelectionCenterLod()
            ? GetCurrentStableCenterParent(_lastViewerPosition, parentLod)
            : GetAncestorBlock(_currentCenterParent, parentLod);
    }

    private static string BuildTierCountSummary(int[] counts, char labelPrefix, int startLod)
    {
        if (counts.Length <= startLod)
        {
            return "none";
        }

        StringBuilder builder = new();
        for (int lod = startLod; lod < counts.Length; lod++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(labelPrefix);
            builder.Append(lod);
            builder.Append(':');
            builder.Append(counts[lod]);
        }

        return builder.ToString();
    }

    private bool IsBlockDisplayingVisuals(TerrainBlockData block)
    {
        return (block.State == TerrainBlockState.Visible || block.State == TerrainBlockState.Releasable) &&
               block.Renderer != null &&
               IsInstanceValid(block.Renderer) &&
               block.Renderer.HasVisuals;
    }

    private void TryHideSupersededCoverageAround(TerrainBlockId blockId)
    {
        TryHideSupersededBlock(blockId);

        TerrainBlockId current = blockId;
        while (current.Lod < GetCoarsestLod())
        {
            current = GetParentBlock(current);
            TryHideSupersededBlock(current);
        }
    }

    private bool TryHideSupersededBlock(
        TerrainBlockId blockId,
        TerrainLodSuccessorCoverageStatus? coverageOverride = null)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            return false;
        }

        TerrainLodSuccessorCoverageStatus coverage = coverageOverride ?? EvaluateSuccessorCoverage(blockId);
        if (block.State != TerrainBlockState.Releasable)
        {
            ObserveSupersededBlockTransition(blockId, block, $"hide_deferred_{SanitizeState(block.State)}", coverage);
            return false;
        }

        if (block.Renderer == null || !IsInstanceValid(block.Renderer))
        {
            ObserveSupersededBlockTransition(blockId, block, "hide_deferred_renderer_missing", coverage);
            return false;
        }

        if (!block.Renderer.HasVisuals)
        {
            ObserveSupersededBlockTransition(blockId, block, "hide_not_needed_visuals_already_hidden", coverage);
            return false;
        }

        if (!HasReadyVisualSuccessorCoverage(blockId, coverage))
        {
            ObserveSupersededBlockTransition(
                blockId,
                block,
                coverage.VisualDeferralReason,
                coverage);
            return false;
        }

        block.Renderer.HideVisuals();
        RecordSupersededBlockHidden(blockId, block, coverage);
        MarkVisibleMixedLodSeamsDirtyAround(blockId);
        return true;
    }

    private string ResolveReleaseDeferralReason(
        TerrainBlockData block,
        TerrainLodSuccessorCoverageStatus coverage)
    {
        if (block.Renderer != null &&
            IsInstanceValid(block.Renderer) &&
            !block.Renderer.HasVisuals)
        {
            return null;
        }

        return coverage.VisualCoverageReady
            ? coverage.PhysicsDeferralReason
            : coverage.VisualDeferralReason;
    }

    private int CommitVisibleMeshBatch(IReadOnlyList<TerrainBlockId> batchBlockIds, TerrainBlockId outgoingParent)
    {
        int committedCount = 0;
        foreach (TerrainBlockId batchBlockId in batchBlockIds)
        {
            if (!_blocks.TryGetValue(batchBlockId, out TerrainBlockData batchBlock) ||
                !batchBlock.Desired ||
                batchBlock.State != TerrainBlockState.MeshReady)
            {
                continue;
            }

            CommitVisibleMeshBlock(batchBlock);
            committedCount++;
        }

        if (committedCount > 1)
        {
            _lastCommitSummary = $"promotion batch {outgoingParent} count {committedCount}";
            WritePromotionBatchDiagnosticsLog(outgoingParent, batchBlockIds, committedCount);
        }

        return committedCount;
    }

    private void CommitVisibleMeshBlock(TerrainBlockData block)
    {
        bool includeCollision = ShouldIncludeCollision(block.Id) && block.TriangleCount > 0;
        ulong commitStart = Time.GetTicksUsec();
        block.Renderer.ApplyVisualMesh(block.Mesh, _activeTerrainDebugView, _surfaceColorizer);
        block.MarkVisible(collisionPending: includeCollision);
        RecordReplacementVisualsReady(block.Id);
        TryHideSupersededCoverageAround(block.Id);
        MarkVisibleMixedLodSeamsDirtyAround(block.Id);
        _lastCommitMs += (Time.GetTicksUsec() - commitStart) / 1000.0;
        _lastCommitCount++;
        if (includeCollision)
        {
            EnqueueCollisionDispatch(block.Id);
        }

        _lastCommitSummary = $"{block.Id} tri {block.TriangleCount} {(includeCollision ? "collision_queued" : "visual_only")}";
        if (_startupBlocks.Contains(block.Id))
        {
            _startupSatisfiedBlocks.Add(block.Id);
        }
    }

    private void WritePromotionBatchDiagnosticsLog(
        TerrainBlockId outgoingParent,
        IReadOnlyList<TerrainBlockId> batchBlockIds,
        int committedCount)
    {
        if (committedCount <= 1 || !EnsureTransitionLogWriter())
        {
            return;
        }

        lock (_transitionLogLock)
        {
            _transitionLogWriter!.WriteLine(
                $"{TransitionLogPrefix} event=promotion_batch parent={outgoingParent} count={committedCount} " +
                $"successors={string.Join("|", batchBlockIds)}");
        }
    }

    private readonly record struct SupersededTransitionProfileSummary(
        int ActiveCount,
        int WaitingForMarkReleasableCount,
        int WaitingForVisualCoverageCount,
        int WaitingForHideCount,
        int WaitingForPhysicsCoverageCount,
        int WaitingForReleaseCount,
        string LastSummary);

    private readonly record struct MixedLodSeamProfileSummary(
        int BlockCount,
        int TransitionFaceCount,
        int SkirtFaceCount,
        int ExplicitSkipFaceCount,
        int SuppressedFaceCount,
        int TriangleCount,
        string LastSummary);

    private readonly record struct VisibleEditedBlockProfileSummary(
        int StickyRegionCount,
        int VisibleBlockCount,
        int FinestBlockCount,
        int LaggingBlockCount,
        string Summary);

    private enum SupersededTransitionWaitState
    {
        MarkReleasable = 0,
        VisualCoverage = 1,
        Hide = 2,
        PhysicsCoverage = 3,
        Release = 4
    }

    private static int CompareTerrainBlockIds(TerrainBlockId a, TerrainBlockId b)
    {
        int lod = a.Lod.CompareTo(b.Lod);
        if (lod != 0)
        {
            return lod;
        }

        int x = a.Index.X.CompareTo(b.Index.X);
        if (x != 0)
        {
            return x;
        }

        int y = a.Index.Y.CompareTo(b.Index.Y);
        if (y != 0)
        {
            return y;
        }

        return a.Index.Z.CompareTo(b.Index.Z);
    }

    private readonly record struct CompletedFieldBuildResult(
        TerrainBlockId BlockId,
        long InstanceVersion,
        int Revision,
        VoxelChunkData Field,
        double WorkerBuildMs,
        TerrainBlockBuildPurpose BuildPurpose,
        int DisplayedRefreshRevision,
        long DisplayedRefreshOperationSequence,
        bool Succeeded);

    private readonly record struct CompletedMeshBuildResult(
        TerrainBlockId BlockId,
        long InstanceVersion,
        int Revision,
        VoxelMeshBuildResult? Mesh,
        double WorkerBuildMs,
        TerrainBlockBuildPurpose BuildPurpose,
        int DisplayedRefreshRevision,
        long DisplayedRefreshOperationSequence,
        bool Succeeded);

    private readonly record struct QueuedBlockDispatchEntry(TerrainBlockId BlockId, int Token);

    private readonly record struct BlockDispatchPriority(int PriorityClass, int Lod, float DistanceMetric, int Token) : System.IComparable<BlockDispatchPriority>
    {
        public int CompareTo(BlockDispatchPriority other)
        {
            int priorityCompare = PriorityClass.CompareTo(other.PriorityClass);
            if (priorityCompare != 0)
            {
                return priorityCompare;
            }

            int lodCompare = Lod.CompareTo(other.Lod);
            if (lodCompare != 0)
            {
                return lodCompare;
            }

            int distanceCompare = DistanceMetric.CompareTo(other.DistanceMetric);
            if (distanceCompare != 0)
            {
                return distanceCompare;
            }

            return Token.CompareTo(other.Token);
        }
    }
}
