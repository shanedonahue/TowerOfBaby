using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TowerOfBaby.Debugging;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainLodManager : Node3D
{
    private readonly record struct TerrainEditInvalidationStats(
        int IntersectedBlockCount,
        int VisibleBlockCount,
        int VisibleFinestBlockCount,
        int VisibleFieldOnlyBlockCount,
        int RequeuedBlockCount,
        int QueuedVisibleBlockCount,
        double EnqueueMs,
        double SyncWorkMs)
    {
        public string Summary =>
            $"blocks {IntersectedBlockCount} vis_refresh {VisibleBlockCount}/{VisibleFinestBlockCount} finest field_only {VisibleFieldOnlyBlockCount} requeued {RequeuedBlockCount} " +
            $"visible_queued {QueuedVisibleBlockCount} enqueue_ms {EnqueueMs:0.00} sync_ms {SyncWorkMs:0.00}";
    }

    private enum TerrainBlockBuildPurpose
    {
        RequestedContent = 0,
        DisplayedRefresh = 1
    }

    private enum TerrainPersistenceSaveKind
    {
        StartupPromotion = 0,
        DirtyPersist = 1
    }

    private const int FinestTerrainLod = 0;
    private const string LodTransitionTracePrefix = "[TerrainLodTransition]";
    private const string DeformTracePrefix = "[TerrainDeform]";
    private const string PersistenceTracePrefix = "[TerrainPersistence]";
    private const double ProfileSnapshotRefreshIntervalSeconds = 0.25;
    private const int MaxCreateBlocksPerFrame = 32;
    private const int MaxFieldWorkerJobs = 8;
    private const int MaxMeshWorkerJobs = 8;
    private const int MaxFieldResultAppliesPerFrame = 16;
    private const int MaxMeshResultAppliesPerFrame = 16;
    private const int MaxMeshCommitsPerFrame = 16;
    private const int MaxCollisionBuildsPerFrame = 16;
    private const int MaxReleasesPerFrame = 32;
    private const int MaxCoherentPromotionBatchSuccessors = 8;
    private const int MaxPersistenceSaveWorkerJobs = 1;
    private const int MaxDisplayedRefreshWorkerBurstJobs = 2;
    private const int MaxDisplayedRefreshCommitBurstPerFrame = 6;
    private const int DisplayedRefreshFollowThroughPasses = 2;
    private const double StartupStallRecoveryIntervalSeconds = 0.25;
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
    [Export] public int[] TierSplitRadiiXZ = { 0, 2, 2 };
    [Export(PropertyHint.Range, "1,12,1")] public int CoarsestRadiusXZ = 2;
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
    [Export(PropertyHint.Range, "0.00,8.00,0.05")] public float CreateMainThreadBudgetMs = 0.75f;
    [Export(PropertyHint.Range, "0.00,8.00,0.05")] public float MeshCommitMainThreadBudgetMs = 1.50f;
    [Export] public bool GenerateCollisionForCoarseLods;

    [ExportGroup("Edit Refresh")]
    [Export(PropertyHint.Range, "0,250,5")] public float DisplayedRefreshCollisionDelayMs = 90.0f;
    [Export(PropertyHint.Range, "0,512,8")] public int MaxPooledRenderers = 128;

    [ExportGroup("Persistence")]
    [Export(PropertyHint.Range, "0.00,2.00,0.05")] public float PersistenceDispatchBudgetMs = 0.15f;
    [Export(PropertyHint.Range, "0.00,2.00,0.05")] public float StartupPersistenceDispatchBudgetMs = 0.30f;
    [Export(PropertyHint.Range, "-1,16,1")] public int PersistableFieldRetentionRadiusXZ = -1;

    [ExportGroup("Startup")]
    [Export] public bool RestorePlayerPositionFromStartupState = true;
    [Export] public bool EnableStartupStatePersistence = true;
    [Export(PropertyHint.Range, "-1,16,1")] public int StartupCriticalRadiusXZ = -1;
    [Export(PropertyHint.Range, "1,32,1")] public int StartupCreateBlocksPerFrame = 8;
    [Export(PropertyHint.Range, "1,8,1")] public int StartupFieldWorkerJobs = 4;
    [Export(PropertyHint.Range, "1,8,1")] public int StartupMeshWorkerJobs = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int StartupFieldResultAppliesPerFrame = 8;
    [Export(PropertyHint.Range, "1,16,1")] public int StartupMeshResultAppliesPerFrame = 8;
    [Export(PropertyHint.Range, "1,16,1")] public int StartupMeshCommitsPerFrame = 8;
    [Export(PropertyHint.Range, "1,16,1")] public int StartupCollisionBuildsPerFrame = 4;
    [Export(PropertyHint.Range, "0.00,8.00,0.05")] public float StartupCreateMainThreadBudgetMs = 2.50f;
    [Export(PropertyHint.Range, "0.00,8.00,0.05")] public float StartupMeshCommitMainThreadBudgetMs = 3.00f;

    [ExportGroup("Shutdown")]
    [Export(PropertyHint.Range, "0,256,1")] public int ShutdownStartupSnapshotBlockCap = 96;

    [ExportGroup("Seams")]
    [Export] public TerrainMixedLodSeamMode MixedLodSeamMode = TerrainMixedLodSeamMode.SkirtsOnly;

    private readonly Dictionary<TerrainBlockId, TerrainBlockData> _blocks = new();
    private readonly HashSet<TerrainBlockId> _desiredBlocks = new();
    private readonly Dictionary<int, HashSet<TerrainBlockId>> _activeSplitParentsByLod = new();
    private readonly Dictionary<int, TerrainBlockId> _currentStableCentersByLod = new();
    private readonly Dictionary<int, TerrainBlockId> _targetStableCentersByLod = new();
    private readonly HashSet<TerrainBlockId> _startupBlocks = new();
    private readonly HashSet<TerrainBlockId> _startupSatisfiedBlocks = new();
    private readonly HashSet<TerrainBlockId> _startupSnapshotBlocks = new();
    private readonly HashSet<TerrainBlockId> _persistedLodBlocks = new();
    private readonly StringBuilder _debugBuilder = new();
    private readonly Queue<double> _recentCreationTimes = new();
    private readonly Queue<double> _recentReleaseTimes = new();
    private readonly Queue<double> _recentDesiredSetChangeTimes = new();
    private readonly ConcurrentQueue<CompletedFieldBuildResult> _completedFieldBuildResults = new();
    private readonly ConcurrentQueue<CompletedFieldBuildResult> _completedDisplayedRefreshFieldBuildResults = new();
    private readonly ConcurrentQueue<CompletedMeshBuildResult> _completedMeshBuildResults = new();
    private readonly ConcurrentQueue<CompletedMeshBuildResult> _completedDisplayedRefreshMeshBuildResults = new();
    private readonly ConcurrentQueue<CompletedPersistenceSaveResult> _completedPersistenceSaveResults = new();
    private readonly Queue<QueuedPersistenceSaveEntry> _persistenceSaveQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _createDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _fieldBuildDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _meshBuildDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _commitDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _collisionDispatcherQueue = new();
    private readonly PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> _releaseDispatcherQueue = new();
    private readonly Queue<TerrainRenderer> _rendererPool = new();
    private readonly Dictionary<TerrainBlockId, TerrainLodSupersededBlockTransition> _supersededBlockTransitions = new();
    private readonly Dictionary<TerrainBlockId, int> _createDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _fieldBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _meshBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _commitDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _collisionDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _releaseDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, PendingPersistenceSaveState> _pendingPersistenceSaves = new();
    private readonly ConcurrentDictionary<TerrainBlockId, int> _persistenceWriteVersions = new();
    private readonly object _persistenceWriteLock = new();
    private readonly HashSet<TerrainBlockId> _dirtyVisibleMixedLodSeamBlocks = new();

    private TerrainConfig _config = null!;
    private TerrainChunkStore _chunkStore = null!;
    private TerrainEditRegionManager _editRegionManager = null!;
    private TerrainMesher _mesher = null!;
    private TerrainSurfaceColorizer _surfaceColorizer = null!;
    private TerrainWorldProfileSnapshot _latestProfileSnapshot = null!;
    private int _latestProfileTelemetryConfigurationVersion;
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
    private bool _startupRestoreStateInitialized;
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
    private int _lastStartupChunkLoadCount;
    private int _lastPersistedChunkLoadCount;
    private int _lastGeneratedChunkLoadCount;
    private int _lastReleaseHysteresisDeferralCount;
    private int _lastReleaseCoverageDeferralCount;
    private int _lastReleaseRequeueCount;
    private int _lastReleaseHeadOfLineAvoidedCount;
    private int _lastReleaseDeferredAgeSampleCount;
    private int _lastPersistenceSaveCount;
    private int _activePersistenceSaveJobs;
    private double _lastFieldBuildMs;
    private double _lastMeshBuildMs;
    private double _lastCommitMs;
    private double _lastCollisionMs;
    private double _lastReleaseMs;
    private double _lastStartupChunkLoadMs;
    private double _lastPersistedChunkLoadMs;
    private double _lastGeneratedChunkLoadMs;
    private double _lastReleaseDeferredAgeMsTotal;
    private double _lastPersistenceSaveMs;
    private double _lastPersistenceSerializationMs;
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
    private int _lastDeformVisibleCommitCount;
    private int _lastDeformSeamRefreshCount;
    private double _lastDeformRegistrationMs;
    private double _lastDeformEnqueueMs;
    private double _lastDeformSyncWorkMs;
    private double _lastDeformAsyncRebuildMs;
    private double _lastDeformVisualApplyMs;
    private double _lastDeformCollisionRebuildMs;
    private double _lastDeformVisibleConvergenceMs;
    private long _lastDeformOperationSequence;
    private ulong _lastDeformRegistrationStartUsec;
    private int _pendingDeformVisibleCommitCount;
    private string _lastEditOperationPrefix = "none";
    private long _refinementHandoffCount;
    private long _releaseHysteresisDeferralCount;
    private long _releaseCoverageDeferralCount;
    private long _releaseRequeueCount;
    private long _releaseHeadOfLineAvoidedCount;
    private long _releaseDeferredAgeSampleCount;
    private double _releaseDeferredAgeMsTotal;
    private long _blockInstanceVersionSequence;
    private long _startupRestoredBlockCount;
    private long _persistedRestoredBlockCount;
    private long _procedurallyGeneratedBlockCount;
    private long _persistenceSaveCount;
    private double _persistenceSaveMsTotal;
    private double _persistenceSerializationMsTotal;
    private long _dirtyPersistWrites;
    private long _startupPromotionWrites;
    private int _activeFieldWorkerJobs;
    private int _activeMeshWorkerJobs;
    private int _dispatchSequence;
    private int _persistenceSaveSequence;
    private TerrainMixedLodSeamMode _appliedMixedLodSeamMode;
    private TerrainVisualDebugMode _activeTerrainDebugView = TerrainVisualDebugMode.Lit;
    private int[] _currentLodBlockCounts = System.Array.Empty<int>();
    private int[] _currentSplitParentCounts = System.Array.Empty<int>();
    private string _lastSupersededTransitionSummary = "none";
    private string _lastMixedLodSeamSummary = "none";
    private bool _allVisibleMixedLodSeamsDirty;
    private float _lastConfiguredSurfaceWaterLevel = float.NaN;
    private double _nextProfileSnapshotRefreshAtSeconds;
    private bool _profileSnapshotDirty = true;
    private double _startupSelectionStartSeconds = -1.0;
    private double _startupFirstVisibleTerrainMs = -1.0;
    private double _startupCompleteMs = -1.0;
    private double _nextStartupStallRecoveryAtSeconds;
    private int _shutdownState;
    private string _lastShutdownSaveSummary = "not_run";
    private string _lastPersistenceSaveScope = "none";
    private int _retainedPersistableFieldCount;

    public bool InitialLoadComplete => _initialLoadComplete;
    public float InitialLoadProgress { get; private set; }
    public TerrainVisualDebugMode ActiveTerrainDebugView => _activeTerrainDebugView;
    private bool IsShuttingDown => Volatile.Read(ref _shutdownState) != 0;

    public float SampleSurfaceHeight(float worldX, float worldZ)
    {
        return _mesher?.SampleSurfaceHeight(worldX, worldZ) ?? (_terrainWorld?.BaseY ?? 0.0f);
    }

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
        LoadPersistedLodRestoreKeys();
        TryInitializeStartupRestoreState();
        RefreshProfileSnapshotIfNeeded(force: true);
    }

    public override void _ExitTree()
    {
        BeginShutdown();
        SaveStartupState();
    }

    public override void _Process(double delta)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _currentTimeSeconds = Time.GetTicksUsec() / 1_000_000.0;
        ConfigureSharedSurfaceWaterLevel();
        _trackedCharacter ??= ResolveTrackedCharacter();
        TryInitializeStartupRestoreState();
        if (_trackedCharacter == null)
        {
            RefreshVisibleMixedLodSeamsIfNeeded();
            RefreshLifecycleRates();
            RefreshProfileSnapshotIfNeeded();
            return;
        }

        _lastCreateCount = 0;
        _lastFieldBuildCount = 0;
        _lastMeshBuildCount = 0;
        _lastCommitCount = 0;
        _lastCollisionCount = 0;
        _lastReleaseCount = 0;
        _lastStartupChunkLoadCount = 0;
        _lastPersistedChunkLoadCount = 0;
        _lastGeneratedChunkLoadCount = 0;
        _lastFieldBuildMs = 0.0;
        _lastMeshBuildMs = 0.0;
        _lastCommitMs = 0.0;
        _lastCollisionMs = 0.0;
        _lastReleaseMs = 0.0;
        _lastStartupChunkLoadMs = 0.0;
        _lastPersistedChunkLoadMs = 0.0;
        _lastGeneratedChunkLoadMs = 0.0;
        _lastPersistenceSaveCount = 0;
        _lastPersistenceSaveMs = 0.0;
        _lastPersistenceSerializationMs = 0.0;
        _lastPersistenceSaveScope = "none";

        _lastViewerPosition = _trackedCharacter.GlobalTransform.Origin;
        UpdateDesiredBlocks(_lastViewerPosition);
        DispatchRuntimeWork();
        RefreshVisibleMixedLodSeamsIfNeeded();
        RefreshLifecycleRates();
        UpdateInitialLoadState();
        RefreshProfileSnapshotIfNeeded();
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

    private void InvalidateProfileSnapshot()
    {
        _profileSnapshotDirty = true;
    }

    private void RefreshProfileSnapshotIfNeeded(bool force = false)
    {
        int telemetryConfigurationVersion = TerrainTelemetry.ConfigurationVersion;
        double nowSeconds = Time.GetTicksUsec() / 1_000_000.0;
        if (!force &&
            _latestProfileSnapshot != null &&
            !_profileSnapshotDirty &&
            telemetryConfigurationVersion == _latestProfileTelemetryConfigurationVersion &&
            nowSeconds < _nextProfileSnapshotRefreshAtSeconds)
        {
            return;
        }

        _currentTimeSeconds = nowSeconds;
        _latestProfileSnapshot = BuildProfileSnapshot();
        _latestProfileTelemetryConfigurationVersion = telemetryConfigurationVersion;
        _nextProfileSnapshotRefreshAtSeconds = nowSeconds + ProfileSnapshotRefreshIntervalSeconds;
        _profileSnapshotDirty = false;
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        RefreshProfileSnapshotIfNeeded();
        return _latestProfileSnapshot;
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
        _debugBuilder.AppendLine($"Startup {BuildStartupSummary()}");
        _debugBuilder.AppendLine($"Shutdown {_lastShutdownSaveSummary}");
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
        if (IsShuttingDown)
        {
            return;
        }

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
        if (IsShuttingDown)
        {
            return;
        }

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
        if (IsShuttingDown)
        {
            return;
        }

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
            registrationStartUsec,
            (Time.GetTicksUsec() - registrationStartUsec) / 1000.0);
    }

    public void ClearPersistedEditRegions()
    {
        if (IsShuttingDown)
        {
            return;
        }

        ulong operationStartUsec = Time.GetTicksUsec();
        ulong registrationStartUsec = Time.GetTicksUsec();
        TerrainEditRegionMutationResult mutation = _editRegionManager.ClearAll();
        ApplyEditMutation(
            "clear_edits",
            mutation,
            null,
            operationStartUsec,
            registrationStartUsec,
            (Time.GetTicksUsec() - registrationStartUsec) / 1000.0);
    }

    public bool SetTerrainDebugView(TerrainVisualDebugMode debugView)
    {
        if (IsShuttingDown)
        {
            return false;
        }

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
        InvalidateProfileSnapshot();
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
                WaterLevel = -6.0f,
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
        ulong registrationStartUsec = 0,
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
            _lastDeformVisibleCommitCount = 0;
            _lastDeformSeamRefreshCount = 0;
            _lastDeformRegistrationMs = registrationMs;
            _lastDeformEnqueueMs = 0.0;
            _lastDeformSyncWorkMs = 0.0;
            _lastDeformAsyncRebuildMs = 0.0;
            _lastDeformVisualApplyMs = 0.0;
            _lastDeformCollisionRebuildMs = 0.0;
            _lastDeformVisibleConvergenceMs = 0.0;
            _lastDeformRegistrationStartUsec = registrationStartUsec;
            _pendingDeformVisibleCommitCount = 0;
            _lastEditOperationPrefix = $"{operation} none";
            RefreshLastEditOperationSummary();
            _lastEditRegionSummary = _lastEditOperationSummary;
            WriteDeformTrace(
                operation,
                changed: false,
                deformMs: _lastDeformMs,
                invalidatedPersistedBlockCount: 0,
                editedBlockCount: 0,
                estimatedEditedSamples: 0,
                detailPromotions: 0,
                visibleBlockCount: 0,
                visibleFinestBlockCount: 0,
                requeuedBlockCount: 0,
                queuedVisibleBlockCount: 0,
                registrationMs: registrationMs,
                enqueueMs: 0.0,
                syncWorkMs: 0.0);
            InvalidateProfileSnapshot();
            return;
        }

        long operationSequence = ++_deformOperationSequence;
        int invalidatedPersistedBlockCount = InvalidatePersistedLodCoverage(mutation.DirtyWorldBounds);
        TerrainEditInvalidationStats invalidation = InvalidateBlocksForEditMutation(mutation.DirtyWorldBounds, stamp, operationSequence);
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
        _lastDeformVisibleCommitCount = 0;
        _lastDeformSeamRefreshCount = 0;
        _lastDeformRegistrationMs = registrationMs;
        _lastDeformEnqueueMs = invalidation.EnqueueMs;
        _lastDeformSyncWorkMs = invalidation.SyncWorkMs;
        _lastDeformAsyncRebuildMs = 0.0;
        _lastDeformVisualApplyMs = 0.0;
        _lastDeformCollisionRebuildMs = 0.0;
        _lastDeformVisibleConvergenceMs = 0.0;
        _lastDeformRegistrationStartUsec = registrationStartUsec;
        _pendingDeformVisibleCommitCount = invalidation.VisibleBlockCount;
        _lastEditOperationPrefix =
            $"{operation} {mutation.Summary} {invalidation.Summary} persist_invalidate {invalidatedPersistedBlockCount} est_samples {estimatedEditedSamples}";
        RefreshLastEditOperationSummary();
        _lastEditRegionSummary = _lastEditOperationSummary;
        WriteDeformTrace(
            operation,
            changed: true,
            deformMs: deformMs,
            invalidatedPersistedBlockCount: invalidatedPersistedBlockCount,
            editedBlockCount: invalidation.IntersectedBlockCount,
            estimatedEditedSamples: estimatedEditedSamples,
            detailPromotions: detailPromotions,
            visibleBlockCount: invalidation.VisibleBlockCount,
            visibleFinestBlockCount: invalidation.VisibleFinestBlockCount,
            requeuedBlockCount: invalidation.RequeuedBlockCount,
            queuedVisibleBlockCount: invalidation.QueuedVisibleBlockCount,
            registrationMs: registrationMs,
            enqueueMs: invalidation.EnqueueMs,
            syncWorkMs: invalidation.SyncWorkMs);
        InvalidateProfileSnapshot();
    }

    private TerrainEditInvalidationStats InvalidateBlocksForEditMutation(
        Aabb dirtyWorldBounds,
        TerrainEditStampData? stamp,
        long operationSequence)
    {
        ulong enqueueStartUsec = Time.GetTicksUsec();
        List<TerrainBlockData> displayedVisualRefreshBlocks = new();
        List<TerrainBlockData> displayedFieldOnlyBlocks = new();
        List<TerrainBlockId> requeuedBlocks = new();
        int intersectedBlockCount = 0;
        int visibleFinestBlockCount = 0;
        int visibleFieldOnlyBlockCount = 0;
        int queuedVisibleBlockCount = 0;

        foreach (TerrainBlockData block in _blocks.Values)
        {
            Aabb blockBounds = TerrainMetrics.GetBlockBounds(_config, block.Id);
            bool intersectsDirtyBounds = blockBounds.Intersects(dirtyWorldBounds);
            bool intersectsDirectEdit = stamp?.OverlapsPrecisely(blockBounds) ?? intersectsDirtyBounds;
            if (!intersectsDirtyBounds && !intersectsDirectEdit)
            {
                continue;
            }

            if (IsBlockDisplayingVisuals(block))
            {
                if (!intersectsDirectEdit)
                {
                    continue;
                }

                intersectedBlockCount++;
                bool requiresVisualRefresh =
                    !stamp.HasValue ||
                    block.DisplayedRefreshRequiresVisualRefresh ||
                    ShouldRefreshDisplayedBlockVisuals(block, stamp.Value);
                if (requiresVisualRefresh)
                {
                    displayedVisualRefreshBlocks.Add(block);
                    if (block.Id.Lod == FinestTerrainLod)
                    {
                        visibleFinestBlockCount++;
                    }
                }
                else
                {
                    displayedFieldOnlyBlocks.Add(block);
                    visibleFieldOnlyBlockCount++;
                }

                continue;
            }

            if (!intersectsDirtyBounds)
            {
                continue;
            }

            intersectedBlockCount++;
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

        foreach (TerrainBlockData displayedBlock in displayedVisualRefreshBlocks)
        {
            if (QueueDisplayedBlockRefresh(
                displayedBlock,
                dirtyWorldBounds,
                stamp,
                operationSequence,
                requiresVisualRefresh: true))
            {
                queuedVisibleBlockCount++;
            }
        }

        foreach (TerrainBlockData displayedBlock in displayedFieldOnlyBlocks)
        {
            QueueDisplayedBlockRefresh(
                displayedBlock,
                dirtyWorldBounds,
                stamp,
                operationSequence,
                requiresVisualRefresh: false);
        }

        return new TerrainEditInvalidationStats(
            intersectedBlockCount,
            displayedVisualRefreshBlocks.Count,
            visibleFinestBlockCount,
            visibleFieldOnlyBlockCount,
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
            $"commit {_lastDeformVisibleCommitCount} seam {_lastDeformSeamRefreshCount} converge_ms {_lastDeformVisibleConvergenceMs:0.00} " +
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

    private void AccumulateDisplayedRefreshCommit(long operationSequence)
    {
        if (operationSequence == 0 || operationSequence != _lastDeformOperationSequence)
        {
            return;
        }

        _lastDeformVisibleCommitCount++;
        _pendingDeformVisibleCommitCount = Math.Max(0, _pendingDeformVisibleCommitCount - 1);
        RefreshLastEditOperationSummary();
    }

    private void AccumulateDisplayedRefreshSeamRefresh(long operationSequence, int seamRefreshCount)
    {
        if (operationSequence == 0 || operationSequence != _lastDeformOperationSequence || seamRefreshCount <= 0)
        {
            return;
        }

        _lastDeformSeamRefreshCount += seamRefreshCount;
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

    private void TryFinalizeDisplayedRefreshConvergence(long operationSequence)
    {
        if (operationSequence == 0 ||
            operationSequence != _lastDeformOperationSequence ||
            _lastDeformVisibleBlockCount <= 0 ||
            _pendingDeformVisibleCommitCount > 0 ||
            _lastDeformVisibleConvergenceMs > 0.0 ||
            _lastDeformRegistrationStartUsec == 0)
        {
            return;
        }

        _lastDeformVisibleConvergenceMs = (Time.GetTicksUsec() - _lastDeformRegistrationStartUsec) / 1000.0;
        RefreshLastEditOperationSummary();
    }

    private bool EnqueueDisplayedRefreshForCurrentState(TerrainBlockData block)
    {
        if (IsShuttingDown || !block.DisplayedRefreshDirty || !IsBlockDisplayingVisuals(block))
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
            if (!block.DisplayedRefreshRequiresVisualRefresh)
            {
                return false;
            }

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

    private TerrainBlockFieldLoadResult AcquireRequestedField(TerrainBlockId blockId, IReadOnlyList<TerrainEditRegion> editRegions)
    {
        if (_startupSnapshotBlocks.Contains(blockId) &&
            _chunkStore.TryLoadStartupLodBlock(blockId, out VoxelChunkData startupField))
        {
            return new TerrainBlockFieldLoadResult(startupField, TerrainChunkLoadSource.StartupSnapshot);
        }

        if (_persistedLodBlocks.Contains(blockId) &&
            _chunkStore.TryLoadLodBlock(blockId, out VoxelChunkData persistedField))
        {
            return new TerrainBlockFieldLoadResult(persistedField, TerrainChunkLoadSource.PersistedChunk);
        }

        return new TerrainBlockFieldLoadResult(
            _mesher.BuildField(blockId, editRegions),
            TerrainChunkLoadSource.ProceduralGeneration);
    }

    private void RecordFieldLoad(TerrainBlockId blockId, TerrainChunkLoadSource source, double workerMs)
    {
        switch (source)
        {
            case TerrainChunkLoadSource.StartupSnapshot:
                _lastStartupChunkLoadCount++;
                _lastStartupChunkLoadMs += workerMs;
                _startupRestoredBlockCount++;
                WritePersistenceTrace(
                    "field_load",
                    $"block={blockId} source=startup_snapshot worker_ms={workerMs:0.000}");
                break;
            case TerrainChunkLoadSource.PersistedChunk:
                _lastPersistedChunkLoadCount++;
                _lastPersistedChunkLoadMs += workerMs;
                _persistedRestoredBlockCount++;
                WritePersistenceTrace(
                    "field_load",
                    $"block={blockId} source=persisted_block worker_ms={workerMs:0.000}");
                break;
            case TerrainChunkLoadSource.ProceduralGeneration:
                _lastGeneratedChunkLoadCount++;
                _lastGeneratedChunkLoadMs += workerMs;
                _procedurallyGeneratedBlockCount++;
                break;
        }
    }

    private void QueuePersistenceSaveForLoadedBlock(TerrainBlockData block, TerrainChunkLoadSource source)
    {
        if (block == null)
        {
            return;
        }

        switch (source)
        {
            case TerrainChunkLoadSource.StartupSnapshot:
                if (!_persistedLodBlocks.Contains(block.Id))
                {
                    QueuePersistenceSave(block, TerrainPersistenceSaveKind.StartupPromotion);
                }

                break;
            case TerrainChunkLoadSource.ProceduralGeneration:
                QueuePersistenceSave(block, TerrainPersistenceSaveKind.DirtyPersist);
                break;
        }
    }

    private void QueuePersistenceSave(TerrainBlockData block, TerrainPersistenceSaveKind kind)
    {
        if (block == null || !block.TryGetPersistableField(out VoxelChunkData field))
        {
            return;
        }

        if (kind == TerrainPersistenceSaveKind.StartupPromotion &&
            _persistedLodBlocks.Contains(block.Id))
        {
            return;
        }

        if (_pendingPersistenceSaves.TryGetValue(block.Id, out PendingPersistenceSaveState existing))
        {
            kind = MergePersistenceSaveKinds(existing.Kind, kind);
        }

        int token = ++_persistenceSaveSequence;
        _pendingPersistenceSaves[block.Id] = new PendingPersistenceSaveState(
            token,
            kind,
            field,
            GetPersistenceWriteVersion(block.Id));
        _persistenceSaveQueue.Enqueue(new QueuedPersistenceSaveEntry(block.Id, token));
        WritePersistenceTrace(
            "save_queued",
            $"block={block.Id} kind={BuildPersistenceSaveScope(kind)} queue_depth={ComputePersistenceQueueDepth()}");
    }

    private static TerrainPersistenceSaveKind MergePersistenceSaveKinds(
        TerrainPersistenceSaveKind existing,
        TerrainPersistenceSaveKind incoming)
    {
        return existing >= incoming ? existing : incoming;
    }

    private int CountShutdownSaveCandidates()
    {
        return _blocks.Count;
    }

    private ShutdownStartupSaveSummary BuildStartupStateSnapshots(Vector3 viewerPosition, int blockCap)
    {
        List<ShutdownSaveCandidate> candidates = BuildShutdownSaveCandidates(viewerPosition);
        List<TerrainLodStartupBlockSnapshot> snapshots = new(Mathf.Min(blockCap, candidates.Count));
        int consideredCount = 0;
        int skippedCount = 0;

        if (blockCap <= 0)
        {
            skippedCount = candidates.Count;
            return new ShutdownStartupSaveSummary(snapshots, consideredCount, skippedCount, HitCap: false);
        }

        foreach (ShutdownSaveCandidate candidate in candidates)
        {
            consideredCount++;
            if (!TryBuildStartupStateSnapshot(candidate.BlockId, out TerrainLodStartupBlockSnapshot snapshot))
            {
                skippedCount++;
                continue;
            }

            snapshots.Add(snapshot);
            if (snapshots.Count >= blockCap)
            {
                return new ShutdownStartupSaveSummary(snapshots, consideredCount, skippedCount, HitCap: true);
            }
        }

        return new ShutdownStartupSaveSummary(snapshots, consideredCount, skippedCount, HitCap: false);
    }

    private List<ShutdownSaveCandidate> BuildShutdownSaveCandidates(Vector3 viewerPosition)
    {
        List<ShutdownSaveCandidate> candidates = new(_blocks.Count);
        foreach (TerrainBlockData block in _blocks.Values)
        {
            bool visible = IsBlockDisplayingVisuals(block);
            bool collisionCritical = ShouldIncludeCollision(block.Id, viewerPosition);
            float distance = TerrainMetrics.DistanceSquaredToBlock(_config, block.Id, viewerPosition);
            candidates.Add(new ShutdownSaveCandidate(block.Id, visible, collisionCritical, distance));
        }

        candidates.Sort((a, b) =>
        {
            int visibleCompare = CompareTrueFirst(a.Visible, b.Visible);
            if (visibleCompare != 0)
            {
                return visibleCompare;
            }

            int collisionCompare = CompareTrueFirst(a.CollisionCritical, b.CollisionCritical);
            if (collisionCompare != 0)
            {
                return collisionCompare;
            }

            int distanceCompare = a.DistanceSquared.CompareTo(b.DistanceSquared);
            if (distanceCompare != 0)
            {
                return distanceCompare;
            }

            return CompareTerrainBlockIds(a.BlockId, b.BlockId);
        });
        return candidates;
    }

    private bool TryBuildStartupStateSnapshot(TerrainBlockId blockId, out TerrainLodStartupBlockSnapshot snapshot)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
            !CanSaveBlockForShutdown(block) ||
            !block.TryGetPersistableField(out VoxelChunkData field))
        {
            snapshot = default;
            return false;
        }

        snapshot = new TerrainLodStartupBlockSnapshot(
            blockId,
            WasVisible: IsBlockDisplayingVisuals(block),
            field);
        return true;
    }

    private bool CanSaveBlockForShutdown(TerrainBlockData block)
    {
        return block != null &&
               !block.FieldBuildRunning &&
               !block.MeshBuildRunning &&
               !block.DisplayedRefreshDirty &&
               block.State is TerrainBlockState.FieldReady or TerrainBlockState.MeshReady or TerrainBlockState.Visible or TerrainBlockState.Releasable;
    }

    private static int CompareTrueFirst(bool a, bool b)
    {
        if (a == b)
        {
            return 0;
        }

        return a ? -1 : 1;
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

    private void LoadPersistedLodRestoreKeys()
    {
        _persistedLodBlocks.Clear();
        foreach (TerrainBlockId blockId in _chunkStore.LoadPersistedLodBlockKeys())
        {
            _persistedLodBlocks.Add(blockId);
        }
    }

    private int InvalidatePersistedLodCoverage(Aabb dirtyWorldBounds)
    {
        if (_persistedLodBlocks.Count == 0)
        {
            return 0;
        }

        Aabb expandedBounds = ExpandBounds(dirtyWorldBounds, Mathf.Max(_config.BaseVoxelSize, TerrainMetrics.GetVoxelSize(_config, FinestTerrainLod)));
        HashSet<TerrainBlockId> invalidatedBlocks = new();
        for (int lod = FinestTerrainLod; lod <= GetCoarsestLod(); lod++)
        {
            foreach (TerrainBlockId blockId in EnumerateBlocksOverlappingBounds(expandedBounds, lod))
            {
                if (_persistedLodBlocks.Contains(blockId))
                {
                    invalidatedBlocks.Add(blockId);
                }
            }
        }

        if (invalidatedBlocks.Count == 0)
        {
            return 0;
        }

        foreach (TerrainBlockId blockId in invalidatedBlocks)
        {
            _persistedLodBlocks.Remove(blockId);
            CancelPendingPersistenceSave(blockId);
        }

        lock (_persistenceWriteLock)
        {
            foreach (TerrainBlockId blockId in invalidatedBlocks)
            {
                int currentVersion = _persistenceWriteVersions.TryGetValue(blockId, out int version)
                    ? version
                    : 0;
                _persistenceWriteVersions[blockId] = currentVersion + 1;
            }

            _chunkStore.DeleteLodBlocks(invalidatedBlocks);
        }

        return invalidatedBlocks.Count;
    }

    private void TryInitializeStartupRestoreState()
    {
        if (IsShuttingDown || _startupRestoreStateInitialized || _trackedCharacter == null)
        {
            return;
        }

        _startupRestoreStateInitialized = true;
        if (!_chunkStore.TryLoadLodStartupState(out TerrainLodStartupState startupState))
        {
            return;
        }

        if (RestorePlayerPositionFromStartupState)
        {
            Transform3D transform = _trackedCharacter.GlobalTransform;
            transform.Origin = startupState.PlayerPosition;
            _trackedCharacter.GlobalTransform = transform;
        }

        _startupSnapshotBlocks.Clear();
        foreach (TerrainLodStartupBlockDescriptor block in startupState.Blocks)
        {
            _startupSnapshotBlocks.Add(block.BlockId);
        }
    }

    private void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
        {
            return;
        }

        _currentTimeSeconds = Time.GetTicksUsec() / 1_000_000.0;
        FlushPersistenceSavesForShutdown();
        CancelPendingRuntimeWorkForShutdown();
    }

    private void FlushPersistenceSavesForShutdown()
    {
        while (Volatile.Read(ref _activePersistenceSaveJobs) > 0)
        {
            Thread.Sleep(1);
        }

        ApplyCompletedPersistenceSaveResults();
        while (TryDequeuePersistenceSave(out QueuedPersistenceSaveWorkItem saveWork))
        {
            if (!CanPersistQueuedSave(saveWork))
            {
                continue;
            }

            TerrainChunkStore.SerializedLodBlockSaveData serialized = _chunkStore.SerializeLodBlock(saveWork.Field);
            if (!TryPersistQueuedSave(saveWork, serialized, out double writeMs))
            {
                continue;
            }

            _persistedLodBlocks.Add(saveWork.BlockId);
            _persistenceSaveCount++;
            _persistenceSaveMsTotal += serialized.SerializationMs + writeMs;
            _persistenceSerializationMsTotal += serialized.SerializationMs;
            if (saveWork.Kind == TerrainPersistenceSaveKind.DirtyPersist)
            {
                _dirtyPersistWrites++;
            }
            else
            {
                _startupPromotionWrites++;
            }

            WritePersistenceTrace(
                "shutdown_save",
                $"block={saveWork.BlockId} kind={BuildPersistenceSaveScope(saveWork.Kind)} save_ms={(serialized.SerializationMs + writeMs):0.000} serialize_ms={serialized.SerializationMs:0.000}");
        }
    }

    private void CancelPendingRuntimeWorkForShutdown()
    {
        _createDispatcherQueue.Clear();
        _fieldBuildDispatcherQueue.Clear();
        _meshBuildDispatcherQueue.Clear();
        _commitDispatcherQueue.Clear();
        _collisionDispatcherQueue.Clear();
        _releaseDispatcherQueue.Clear();
        _persistenceSaveQueue.Clear();
        _createDispatchTokens.Clear();
        _fieldBuildDispatchTokens.Clear();
        _meshBuildDispatchTokens.Clear();
        _commitDispatchTokens.Clear();
        _collisionDispatchTokens.Clear();
        _releaseDispatchTokens.Clear();
        _pendingPersistenceSaves.Clear();

        while (_completedFieldBuildResults.TryDequeue(out _))
        {
        }

        while (_completedDisplayedRefreshFieldBuildResults.TryDequeue(out _))
        {
        }

        while (_completedMeshBuildResults.TryDequeue(out _))
        {
        }

        while (_completedDisplayedRefreshMeshBuildResults.TryDequeue(out _))
        {
        }

        while (_completedPersistenceSaveResults.TryDequeue(out _))
        {
        }

        _persistenceWriteVersions.Clear();
    }

    private void SaveStartupState()
    {
        if (!EnableStartupStatePersistence)
        {
            _lastShutdownSaveSummary = "persistence_disabled";
            return;
        }

        _trackedCharacter ??= ResolveTrackedCharacter();
        if (_trackedCharacter == null)
        {
            _lastShutdownSaveSummary = "viewer_missing";
            return;
        }

        Vector3 viewerPosition = _trackedCharacter.GlobalPosition;
        int candidateCount = CountShutdownSaveCandidates();
        int blockCap = Mathf.Max(0, ShutdownStartupSnapshotBlockCap);
        GD.Print(
            $"Terrain LOD shutdown save start | candidates {candidateCount} | cap {blockCap} | active_workers field/mesh {Volatile.Read(ref _activeFieldWorkerJobs)}/{Volatile.Read(ref _activeMeshWorkerJobs)}");

        Stopwatch stopwatch = Stopwatch.StartNew();
        ShutdownStartupSaveSummary summary = BuildStartupStateSnapshots(viewerPosition, blockCap);
        _chunkStore.SaveLodStartupState(viewerPosition, summary.Blocks);
        stopwatch.Stop();

        _startupSnapshotBlocks.Clear();
        foreach (TerrainLodStartupBlockSnapshot block in summary.Blocks)
        {
            _startupSnapshotBlocks.Add(block.BlockId);
        }

        _lastShutdownSaveSummary =
            $"considered {summary.ConsideredCount}/{candidateCount} saved {summary.Blocks.Count} skipped {summary.SkippedCount} " +
            $"cap {blockCap} {(summary.HitCap ? "hit" : "not_hit")} persist_ms {stopwatch.Elapsed.TotalMilliseconds:0.00}";
        GD.Print(
            $"Terrain LOD shutdown save end | considered {summary.ConsideredCount}/{candidateCount} | saved {summary.Blocks.Count} | skipped {summary.SkippedCount} | cap {blockCap} | hit_cap {(summary.HitCap ? "yes" : "no")} | ms {stopwatch.Elapsed.TotalMilliseconds:0.00}");
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
            _startupSelectionStartSeconds = _currentTimeSeconds;
            _startupFirstVisibleTerrainMs = -1.0;
            _startupCompleteMs = -1.0;
            _nextStartupStallRecoveryAtSeconds = 0.0;
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
            foreach (TerrainBlockId blockId in BuildStartupCriticalSet(desired, viewerPosition))
            {
                _startupBlocks.Add(blockId);
            }

            RefreshStartupSatisfiedBlocks();
        }

        ApplyDesiredSetChanges(desired);

        _hysteresisRetainedBlockCount = CountHysteresisRetainedBlocks();
        _lastSelectionSummary = BuildSelectionSummary(_currentCenterParent, _targetCenterParent, viewerParent, desired.Count);
        _lastTierSelectionSummary = BuildTierSelectionSummary();
    }

    private HashSet<TerrainBlockId> BuildStartupCriticalSet(IEnumerable<TerrainBlockId> desired, Vector3 viewerPosition)
    {
        HashSet<TerrainBlockId> critical = new();
        TerrainBlockId nearestBlock = default;
        float nearestDistance = float.MaxValue;
        bool hasNearest = false;

        foreach (TerrainBlockId blockId in desired)
        {
            float distance = TerrainMetrics.DistanceSquaredToBlock(_config, blockId, viewerPosition);
            if (!hasNearest || distance < nearestDistance)
            {
                nearestBlock = blockId;
                nearestDistance = distance;
                hasNearest = true;
            }

            if (IsStartupCriticalBlock(blockId, viewerPosition))
            {
                critical.Add(blockId);
            }
        }

        if (critical.Count == 0 && hasNearest)
        {
            critical.Add(nearestBlock);
        }

        return critical;
    }

    private bool IsStartupCriticalBlock(TerrainBlockId blockId, Vector3 viewerPosition)
    {
        if (ShouldIncludeCollision(blockId, viewerPosition))
        {
            return true;
        }

        float referenceSpan = GetLocalCoverageReferenceSpan();
        float horizontalRadius = Mathf.Max(1, GetStartupCriticalRadius()) * referenceSpan;
        float verticalRadius = Mathf.Max(referenceSpan, (Mathf.Max(0, VerticalRadius) + 1) * referenceSpan);
        return IsBlockWithinCoverageRadius(blockId, viewerPosition, horizontalRadius, verticalRadius);
    }

    private int GetStartupCriticalRadius()
    {
        if (StartupCriticalRadiusXZ >= 0)
        {
            return Mathf.Max(0, StartupCriticalRadiusXZ);
        }

        return Mathf.Max(1, GetCollisionCoverageRadiusInReferenceBlocks() - 1);
    }

    private void RefreshStartupSatisfiedBlocks()
    {
        foreach (TerrainBlockId blockId in _startupBlocks)
        {
            UpdateStartupSatisfiedState(blockId);
        }
    }

    private void UpdateStartupSatisfiedState(TerrainBlockId blockId)
    {
        if (!_startupBlocks.Contains(blockId))
        {
            return;
        }

        if (IsStartupBlockReady(blockId))
        {
            _startupSatisfiedBlocks.Add(blockId);
        }
    }

    private bool IsStartupBlockReady(TerrainBlockId blockId)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
            block.Renderer == null ||
            !IsInstanceValid(block.Renderer))
        {
            return false;
        }

        if (block.State != TerrainBlockState.Visible && block.State != TerrainBlockState.Releasable)
        {
            return false;
        }

        if (block.TriangleCount <= 0)
        {
            return true;
        }

        return !ShouldIncludeCollision(blockId) || block.Renderer.HasCollision;
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
                if (!ShouldRefineParentOffset(centerParent.Lod, x, z, bubbleRadius))
                {
                    continue;
                }

                AddRefinedParent(refinedParents, centerParent, x, z);
            }
        }

        return refinedParents;
    }

    private static bool ShouldRefineParentOffset(int parentLod, int xOffset, int zOffset, int bubbleRadius)
    {
        if (bubbleRadius <= 0)
        {
            return xOffset == 0 && zOffset == 0;
        }

        // Keep the near-player refinement bubble square so close seams remain stable, but trim far-tier corners
        // where they mostly add residency cost rather than noticeable quality.
        if (parentLod <= FinestTerrainLod + 1 || bubbleRadius <= 2)
        {
            return true;
        }

        int absX = Mathf.Abs(xOffset);
        int absZ = Mathf.Abs(zOffset);
        int innerSquareRadius = Mathf.Max(1, bubbleRadius - 1);
        if (Mathf.Max(absX, absZ) <= innerSquareRadius)
        {
            return true;
        }

        int cornerAllowance = Mathf.Max(1, bubbleRadius / 2);
        return (absX + absZ) <= bubbleRadius + cornerAllowance;
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

    private TerrainChunkDirtyBoundsSnapshot BuildLocalDirtyBoundsSnapshot(TerrainBlockId blockId, Aabb dirtyWorldBounds)
    {
        TerrainChunkDirtyBoundsTracker tracker = CreateDirtyBoundsTracker(blockId);
        Aabb blockBounds = TerrainMetrics.GetBlockBounds(_config, blockId);
        if (TryIntersectBounds(blockBounds, dirtyWorldBounds, out Aabb overlapWorldBounds))
        {
            tracker.Include(new Aabb(overlapWorldBounds.Position - blockBounds.Position, overlapWorldBounds.Size));
        }

        return tracker.Snapshot;
    }

    private TerrainChunkDirtyBoundsSnapshot BuildFullChunkDirtyBoundsSnapshot(TerrainBlockId blockId)
    {
        TerrainChunkDirtyBoundsTracker tracker = CreateDirtyBoundsTracker(blockId);
        tracker.IncludeFullChunk();
        return tracker.Snapshot;
    }

    private TerrainChunkDirtyBoundsSnapshot CombineLocalDirtyBoundsSnapshots(
        TerrainBlockId blockId,
        TerrainChunkDirtyBoundsSnapshot current,
        TerrainChunkDirtyBoundsSnapshot incoming,
        bool fallbackToFullChunk)
    {
        if (!current.HasBounds || !incoming.HasBounds)
        {
            return fallbackToFullChunk
                ? BuildFullChunkDirtyBoundsSnapshot(blockId)
                : current.HasBounds
                    ? current
                    : incoming;
        }

        TerrainChunkDirtyBoundsTracker tracker = CreateDirtyBoundsTracker(blockId);
        tracker.Include(current.LocalBounds);
        tracker.Include(incoming.LocalBounds);
        return tracker.Snapshot;
    }

    private bool QueueDisplayedBlockRefresh(
        TerrainBlockData displayedBlock,
        Aabb dirtyWorldBounds,
        TerrainEditStampData? stamp,
        long operationSequence,
        bool requiresVisualRefresh)
    {
        Aabb localDirtyWorldBounds = stamp?.WorldBounds ?? dirtyWorldBounds;
        TerrainChunkDirtyBoundsSnapshot localDirtyBounds = BuildLocalDirtyBoundsSnapshot(displayedBlock.Id, localDirtyWorldBounds);
        bool requiresFullFieldRebuild = !stamp.HasValue || displayedBlock.DisplayedRefreshDirty;
        TerrainEditStampData? latestStamp = requiresFullFieldRebuild ? null : stamp;
        bool combinedRequiresVisualRefresh =
            requiresVisualRefresh ||
            displayedBlock.DisplayedRefreshRequiresVisualRefresh;
        if (displayedBlock.DisplayedRefreshDirty)
        {
            localDirtyBounds = CombineLocalDirtyBoundsSnapshots(
                displayedBlock.Id,
                displayedBlock.DisplayedRefreshDirtyBounds,
                localDirtyBounds,
                fallbackToFullChunk: displayedBlock.DisplayedRefreshRequiresFullFieldRebuild);
        }

        if (!localDirtyBounds.HasBounds)
        {
            localDirtyBounds = BuildFullChunkDirtyBoundsSnapshot(displayedBlock.Id);
            requiresFullFieldRebuild = true;
            latestStamp = null;
        }

        displayedBlock.MarkDisplayedRefreshDirty(
            operationSequence,
            localDirtyBounds,
            latestStamp,
            requiresFullFieldRebuild,
            combinedRequiresVisualRefresh);
        InvalidateBlockDispatch(_fieldBuildDispatchTokens, displayedBlock.Id);
        InvalidateBlockDispatch(_meshBuildDispatchTokens, displayedBlock.Id);
        InvalidateBlockDispatch(_commitDispatchTokens, displayedBlock.Id);
        InvalidateBlockDispatch(_collisionDispatchTokens, displayedBlock.Id);
        return EnqueueDisplayedRefreshForCurrentState(displayedBlock);
    }

    private bool ShouldRefreshDisplayedBlockVisuals(TerrainBlockData block, TerrainEditStampData stamp)
    {
        TerrainRenderer renderer = block?.Renderer;
        if (renderer == null || !IsInstanceValid(renderer))
        {
            return true;
        }

        float surfaceBandPadding = ResolveDisplayedRefreshSurfaceBandPadding(block.Id, stamp);
        return !renderer.TryGetVisualSurfaceWorldBounds(surfaceBandPadding, out Aabb surfaceWorldBounds) ||
               stamp.OverlapsPrecisely(surfaceWorldBounds);
    }

    private float ResolveDisplayedRefreshSurfaceBandPadding(TerrainBlockId blockId, TerrainEditStampData stamp)
    {
        float voxelSize = TerrainMetrics.GetVoxelSize(_config, blockId.Lod);
        float basePadding = Mathf.Max(voxelSize * 1.5f, _config.BaseVoxelSize);
        return stamp.Kind switch
        {
            TerrainEditStampKind.Sphere => Mathf.Max(
                basePadding,
                Mathf.Max(stamp.Radius, voxelSize) + Mathf.Max(stamp.RetextureMargin, voxelSize * 0.5f)),
            TerrainEditStampKind.Slash => Mathf.Max(
                basePadding,
                Mathf.Max(stamp.Depth * 0.5f, voxelSize * 0.5f) + Mathf.Max(stamp.RetextureMargin, voxelSize * 0.75f)),
            _ => basePadding
        };
    }

    private TerrainChunkDirtyBoundsTracker CreateDirtyBoundsTracker(TerrainBlockId blockId)
    {
        return new TerrainChunkDirtyBoundsTracker(
            TerrainMetrics.GetBlockSpan(_config, blockId.Lod),
            TerrainMetrics.GetVoxelSize(_config, blockId.Lod),
            _config.PointsPerAxis);
    }

    private TerrainSeamFace ResolveDisplayedRefreshSeamFaces(TerrainBlockData block)
    {
        if (block == null)
        {
            return TerrainSeamFace.None;
        }

        TerrainChunkDirtyBoundsSnapshot dirtyBounds = block.DisplayedRefreshDirtyBounds;
        if (!dirtyBounds.HasBounds)
        {
            return TerrainSeamFace.None;
        }

        if (block.DisplayedRefreshRequiresFullFieldRebuild || dirtyBounds.Coverage >= 0.85)
        {
            return TerrainSeamFace.NegativeX |
                   TerrainSeamFace.PositiveX |
                   TerrainSeamFace.NegativeY |
                   TerrainSeamFace.PositiveY |
                   TerrainSeamFace.NegativeZ |
                   TerrainSeamFace.PositiveZ;
        }

        float blockSpan = TerrainMetrics.GetBlockSpan(_config, block.Id.Lod);
        float faceMargin = Mathf.Max(TerrainMetrics.GetVoxelSize(_config, block.Id.Lod) * 2.0f, 0.001f);
        Vector3 dirtyEnd = dirtyBounds.LocalBounds.Position + dirtyBounds.LocalBounds.Size;
        TerrainSeamFace faces = TerrainSeamFace.None;
        if (dirtyBounds.LocalBounds.Position.X <= faceMargin)
        {
            faces |= TerrainSeamFace.NegativeX;
        }

        if (dirtyEnd.X >= blockSpan - faceMargin)
        {
            faces |= TerrainSeamFace.PositiveX;
        }

        if (dirtyBounds.LocalBounds.Position.Y <= faceMargin)
        {
            faces |= TerrainSeamFace.NegativeY;
        }

        if (dirtyEnd.Y >= blockSpan - faceMargin)
        {
            faces |= TerrainSeamFace.PositiveY;
        }

        if (dirtyBounds.LocalBounds.Position.Z <= faceMargin)
        {
            faces |= TerrainSeamFace.NegativeZ;
        }

        if (dirtyEnd.Z >= blockSpan - faceMargin)
        {
            faces |= TerrainSeamFace.PositiveZ;
        }

        return faces;
    }

    private double ResolveDisplayedRefreshCollisionDelaySeconds(TerrainChunkDirtyBoundsSnapshot dirtyBounds)
    {
        if (DisplayedRefreshCollisionDelayMs <= 0.0f || !dirtyBounds.HasBounds)
        {
            return 0.0;
        }

        if (dirtyBounds.Coverage >= 0.40)
        {
            return 0.0;
        }

        return DisplayedRefreshCollisionDelayMs / 1000.0;
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
                    if (!ShouldIncludeCoarsestCoverageOffset(x, z, outerRadius))
                    {
                        continue;
                    }

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

    private static bool ShouldIncludeCoarsestCoverageOffset(int xOffset, int zOffset, int bubbleRadius)
    {
        if (bubbleRadius <= 1)
        {
            return true;
        }

        int absX = Mathf.Abs(xOffset);
        int absZ = Mathf.Abs(zOffset);
        int innerSquareRadius = Mathf.Max(0, bubbleRadius - 1);
        if (Mathf.Max(absX, absZ) <= innerSquareRadius)
        {
            return true;
        }

        return (absX + absZ) <= bubbleRadius + Mathf.Max(1, bubbleRadius / 2);
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
        TerrainRenderer renderer = RentTerrainRenderer();
        renderer.Initialize(blockId, TerrainMetrics.GetBlockOrigin(_config, blockId));
        long instanceVersion = Interlocked.Increment(ref _blockInstanceVersionSequence);
        _blocks[blockId] = new TerrainBlockData(blockId, renderer, instanceVersion);
        _recentCreationTimes.Enqueue(_currentTimeSeconds);
    }

    private TerrainRenderer RentTerrainRenderer()
    {
        while (_rendererPool.Count > 0)
        {
            TerrainRenderer pooledRenderer = _rendererPool.Dequeue();
            if (pooledRenderer == null || !IsInstanceValid(pooledRenderer))
            {
                continue;
            }

            pooledRenderer.Visible = true;
            return pooledRenderer;
        }

        TerrainRenderer renderer = new();
        AddChild(renderer);
        return renderer;
    }

    private void RecycleTerrainRenderer(TerrainRenderer renderer)
    {
        if (renderer == null || !IsInstanceValid(renderer))
        {
            return;
        }

        int maxPooledRenderers = Mathf.Max(0, MaxPooledRenderers);
        if (maxPooledRenderers == 0 || _rendererPool.Count >= maxPooledRenderers)
        {
            renderer.QueueFree();
            return;
        }

        renderer.ResetForPool();
        _rendererPool.Enqueue(renderer);
    }

    private bool IsStartupBoostActive()
    {
        return _selectionInitialized && !_initialLoadComplete;
    }

    private int GetCurrentCreateBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(CreateBlocksPerFrame, StartupCreateBlocksPerFrame)
            : CreateBlocksPerFrame;
        return Mathf.Clamp(configured, 1, MaxCreateBlocksPerFrame);
    }

    private int GetCurrentFieldWorkerBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(FieldWorkerJobs, StartupFieldWorkerJobs)
            : FieldWorkerJobs;
        configured += Mathf.Min(MaxDisplayedRefreshWorkerBurstJobs, CountPendingDisplayedRefreshDispatches(_fieldBuildDispatchTokens));
        return Mathf.Clamp(configured, 1, MaxFieldWorkerJobs);
    }

    private int GetCurrentMeshWorkerBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(MeshWorkerJobs, StartupMeshWorkerJobs)
            : MeshWorkerJobs;
        configured += Mathf.Min(MaxDisplayedRefreshWorkerBurstJobs, CountPendingDisplayedRefreshDispatches(_meshBuildDispatchTokens));
        return Mathf.Clamp(configured, 1, MaxMeshWorkerJobs);
    }

    private int GetCurrentFieldResultApplyBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(FieldResultAppliesPerFrame, StartupFieldResultAppliesPerFrame)
            : FieldResultAppliesPerFrame;
        return Mathf.Clamp(configured, 1, MaxFieldResultAppliesPerFrame);
    }

    private int GetCurrentMeshResultApplyBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(MeshResultAppliesPerFrame, StartupMeshResultAppliesPerFrame)
            : MeshResultAppliesPerFrame;
        return Mathf.Clamp(configured, 1, MaxMeshResultAppliesPerFrame);
    }

    private int GetCurrentMeshCommitBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(MeshCommitsPerFrame, StartupMeshCommitsPerFrame)
            : MeshCommitsPerFrame;
        configured += Mathf.Min(MaxDisplayedRefreshCommitBurstPerFrame, CountPendingDisplayedRefreshDispatches(_commitDispatchTokens));
        return Mathf.Clamp(configured, 1, MaxMeshCommitsPerFrame);
    }

    private int GetCurrentCollisionBudget()
    {
        int configured = IsStartupBoostActive()
            ? Mathf.Max(CollisionBuildsPerFrame, StartupCollisionBuildsPerFrame)
            : CollisionBuildsPerFrame;
        return Mathf.Clamp(configured, 1, MaxCollisionBuildsPerFrame);
    }

    private float GetCurrentCreateTimeBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(CreateMainThreadBudgetMs, StartupCreateMainThreadBudgetMs)
            : Mathf.Max(0.0f, CreateMainThreadBudgetMs);
    }

    private float GetCurrentMeshCommitTimeBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(MeshCommitMainThreadBudgetMs, StartupMeshCommitMainThreadBudgetMs)
            : Mathf.Max(0.0f, MeshCommitMainThreadBudgetMs);
    }

    private float GetCurrentPersistenceDispatchBudgetMs()
    {
        return IsStartupBoostActive()
            ? Mathf.Max(PersistenceDispatchBudgetMs, StartupPersistenceDispatchBudgetMs)
            : Mathf.Max(0.0f, PersistenceDispatchBudgetMs);
    }

    private void DispatchRuntimeWork()
    {
        if (IsShuttingDown)
        {
            return;
        }

        _lastReleaseHysteresisDeferralCount = 0;
        _lastReleaseCoverageDeferralCount = 0;
        _lastReleaseRequeueCount = 0;
        _lastReleaseHeadOfLineAvoidedCount = 0;
        _lastReleaseDeferredAgeSampleCount = 0;
        _lastReleaseDeferredAgeMsTotal = 0.0;
        ulong createBudgetStartUsec = Time.GetTicksUsec();
        float createTimeBudgetMs = GetCurrentCreateTimeBudgetMs();
        ProcessCreateDispatch(createBudgetStartUsec, createTimeBudgetMs);
        StartFieldBuildWorkers();
        ApplyCompletedFieldBuildResults();
        StartMeshBuildWorkers();
        ApplyCompletedMeshBuildResults();
        ulong commitBudgetStartUsec = Time.GetTicksUsec();
        float commitTimeBudgetMs = GetCurrentMeshCommitTimeBudgetMs();
        ProcessMeshCommitDispatch(commitBudgetStartUsec, commitTimeBudgetMs);
        ProcessDisplayedRefreshFollowThroughPasses(commitBudgetStartUsec, commitTimeBudgetMs);
        RefreshCollisionCoverage();
        DispatchPendingCollisionRefreshes();
        ProcessCollisionDispatch();
        ProcessPersistenceSaves();
        TrimPersistableFields();
        ProcessReleaseDispatch();
        RecoverStalledStartupBlocks();
    }

    private void ProcessPersistenceSaves()
    {
        ApplyCompletedPersistenceSaveResults();
        if (Volatile.Read(ref _activePersistenceSaveJobs) >= MaxPersistenceSaveWorkerJobs)
        {
            return;
        }

        float dispatchBudgetMs = GetCurrentPersistenceDispatchBudgetMs();
        ulong dispatchBudgetStartUsec = Time.GetTicksUsec();
        while (Volatile.Read(ref _activePersistenceSaveJobs) < MaxPersistenceSaveWorkerJobs &&
               !HasExceededMainThreadBudget(dispatchBudgetStartUsec, dispatchBudgetMs) &&
               TryDequeuePersistenceSave(out QueuedPersistenceSaveWorkItem saveWork))
        {
            StartPersistenceSaveWorker(saveWork);
        }
    }

    private void StartPersistenceSaveWorker(QueuedPersistenceSaveWorkItem saveWork)
    {
        Interlocked.Increment(ref _activePersistenceSaveJobs);
        _ = Task.Run(() =>
        {
            try
            {
                if (!CanPersistQueuedSave(saveWork))
                {
                    _completedPersistenceSaveResults.Enqueue(
                        new CompletedPersistenceSaveResult(
                            saveWork.BlockId,
                            saveWork.Kind,
                            SaveMs: 0.0,
                            SerializationMs: 0.0,
                            Succeeded: false,
                            Skipped: true,
                            FailureMessage: string.Empty));
                    return;
                }

                TerrainChunkStore.SerializedLodBlockSaveData serialized = _chunkStore.SerializeLodBlock(saveWork.Field);
                if (!TryPersistQueuedSave(saveWork, serialized, out double writeMs))
                {
                    _completedPersistenceSaveResults.Enqueue(
                        new CompletedPersistenceSaveResult(
                            saveWork.BlockId,
                            saveWork.Kind,
                            SaveMs: 0.0,
                            SerializationMs: 0.0,
                            Succeeded: false,
                            Skipped: true,
                            FailureMessage: string.Empty));
                    return;
                }

                _completedPersistenceSaveResults.Enqueue(
                    new CompletedPersistenceSaveResult(
                        saveWork.BlockId,
                        saveWork.Kind,
                        serialized.SerializationMs + writeMs,
                        serialized.SerializationMs,
                        Succeeded: true,
                        Skipped: false,
                        FailureMessage: string.Empty));
            }
            catch (Exception ex)
            {
                _completedPersistenceSaveResults.Enqueue(
                    new CompletedPersistenceSaveResult(
                        saveWork.BlockId,
                        saveWork.Kind,
                        SaveMs: 0.0,
                        SerializationMs: 0.0,
                        Succeeded: false,
                        Skipped: false,
                        FailureMessage: ex.Message));
            }
            finally
            {
                Interlocked.Decrement(ref _activePersistenceSaveJobs);
            }
        });
    }

    private void ApplyCompletedPersistenceSaveResults()
    {
        while (_completedPersistenceSaveResults.TryDequeue(out CompletedPersistenceSaveResult result))
        {
            if (result.Skipped)
            {
                continue;
            }

            if (!result.Succeeded)
            {
                WritePersistenceTrace(
                    "save_failed",
                    $"block={result.BlockId} kind={BuildPersistenceSaveScope(result.Kind)} reason=\"{Sanitize(result.FailureMessage)}\"");
                GD.PushWarning(
                    $"Terrain LOD persistence save failed | block {result.BlockId} | kind {result.Kind} | {result.FailureMessage}");
                continue;
            }

            _persistedLodBlocks.Add(result.BlockId);
            _lastPersistenceSaveCount++;
            _lastPersistenceSaveMs += result.SaveMs;
            _lastPersistenceSerializationMs += result.SerializationMs;
            _persistenceSaveCount++;
            _persistenceSaveMsTotal += result.SaveMs;
            _persistenceSerializationMsTotal += result.SerializationMs;
            if (result.Kind == TerrainPersistenceSaveKind.DirtyPersist)
            {
                _dirtyPersistWrites++;
            }
            else
            {
                _startupPromotionWrites++;
            }

            _lastPersistenceSaveScope = BuildPersistenceSaveScope(result.Kind);
            WritePersistenceTrace(
                "save_completed",
                $"block={result.BlockId} kind={_lastPersistenceSaveScope} save_ms={result.SaveMs:0.000} serialize_ms={result.SerializationMs:0.000} queue_depth={ComputePersistenceQueueDepth()}");
        }
    }

    private bool CanPersistQueuedSave(QueuedPersistenceSaveWorkItem saveWork)
    {
        return (_persistenceWriteVersions.TryGetValue(saveWork.BlockId, out int version) ? version : 0) == saveWork.Version;
    }

    private bool TryPersistQueuedSave(
        QueuedPersistenceSaveWorkItem saveWork,
        TerrainChunkStore.SerializedLodBlockSaveData serialized,
        out double writeMs)
    {
        lock (_persistenceWriteLock)
        {
            int currentVersion = _persistenceWriteVersions.TryGetValue(saveWork.BlockId, out int version)
                ? version
                : 0;
            if (currentVersion != saveWork.Version)
            {
                writeMs = 0.0;
                return false;
            }

            writeMs = _chunkStore.SaveSerializedLodBlock(saveWork.BlockId, serialized);
            return true;
        }
    }

    private static string BuildPersistenceSaveScope(TerrainPersistenceSaveKind kind)
    {
        return kind == TerrainPersistenceSaveKind.DirtyPersist
            ? "lod_block_dirty"
            : "lod_block_startup_promotion";
    }

    private int ComputePersistenceQueueDepth()
    {
        return _pendingPersistenceSaves.Count + Volatile.Read(ref _activePersistenceSaveJobs);
    }

    private void TrimPersistableFields()
    {
        int retainedFieldCount = 0;
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (!block.TryGetPersistableField(out _))
            {
                continue;
            }

            if (ShouldRetainPersistableField(block))
            {
                retainedFieldCount++;
                continue;
            }

            block.ReleasePersistableField();
        }

        _retainedPersistableFieldCount = retainedFieldCount;
    }

    private bool ShouldRetainPersistableField(TerrainBlockData block)
    {
        if (block == null)
        {
            return false;
        }

        if (block.FieldBuildRunning ||
            block.MeshBuildRunning ||
            block.DisplayedRefreshDirty ||
            block.State is TerrainBlockState.Requested or TerrainBlockState.FieldReady or TerrainBlockState.MeshReady)
        {
            return true;
        }

        if (IsStartupBoostActive() &&
            _startupBlocks.Contains(block.Id) &&
            !_startupSatisfiedBlocks.Contains(block.Id))
        {
            return true;
        }

        int retentionRadius = GetPersistableFieldRetentionRadius();
        if (retentionRadius <= 0)
        {
            return false;
        }

        float referenceSpan = GetLocalCoverageReferenceSpan();
        float horizontalRadius = retentionRadius * referenceSpan;
        float verticalRadius = Mathf.Max(referenceSpan, (Mathf.Max(0, VerticalRadius) + 1) * referenceSpan);
        return IsBlockWithinCoverageRadius(block.Id, _lastViewerPosition, horizontalRadius, verticalRadius);
    }

    private int GetPersistableFieldRetentionRadius()
    {
        if (PersistableFieldRetentionRadiusXZ >= 0)
        {
            return Mathf.Max(0, PersistableFieldRetentionRadiusXZ);
        }

        return IsStartupBoostActive()
            ? Mathf.Max(1, GetStartupCriticalRadius())
            : Mathf.Max(1, GetEffectiveLod0NearFieldRadius());
    }

    private void ProcessCreateDispatch(ulong budgetStartUsec, float createTimeBudgetMs)
    {
        int createBudget = GetCurrentCreateBudget();
        while (_lastCreateCount < createBudget &&
               !HasExceededMainThreadBudget(budgetStartUsec, createTimeBudgetMs) &&
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
        int workerBudget = GetCurrentFieldWorkerBudget();
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
            VoxelChunkData persistableField = null;
            bool canIncrementallyRefreshDisplayedField =
                buildPurpose == TerrainBlockBuildPurpose.DisplayedRefresh &&
                block.CanIncrementallyRefreshDisplayedField &&
                block.TryGetPersistableField(out persistableField);
            TerrainChunkDirtyBoundsSnapshot displayedRefreshDirtyBounds = block.DisplayedRefreshDirtyBounds;
            TerrainEditStampData? displayedRefreshLatestStamp = block.DisplayedRefreshLatestStamp;
            Interlocked.Increment(ref _activeFieldWorkerJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    TerrainBlockFieldLoadResult fieldResult = buildPurpose == TerrainBlockBuildPurpose.RequestedContent
                        ? AcquireRequestedField(blockId, editRegions)
                        : new TerrainBlockFieldLoadResult(
                            canIncrementallyRefreshDisplayedField
                                ? _mesher.BuildDisplayedRefreshField(
                                    blockId,
                                    persistableField,
                                    displayedRefreshDirtyBounds,
                                    displayedRefreshLatestStamp,
                                    editRegions)
                                : _mesher.BuildField(blockId, editRegions),
                            TerrainChunkLoadSource.Resident);
                    if (IsShuttingDown)
                    {
                        return;
                    }

                    EnqueueCompletedFieldBuildResult(
                        new CompletedFieldBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            fieldResult.Field,
                            stopwatch.Elapsed.TotalMilliseconds,
                            fieldResult.Source,
                            buildPurpose,
                            displayedRefreshRevision,
                            displayedRefreshOperationSequence,
                            Succeeded: true));
                }
                catch
                {
                    if (IsShuttingDown)
                    {
                        return;
                    }

                    EnqueueCompletedFieldBuildResult(
                        new CompletedFieldBuildResult(
                            blockId,
                            instanceVersion,
                            revision,
                            null,
                            stopwatch.Elapsed.TotalMilliseconds,
                            TerrainChunkLoadSource.Resident,
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
        ApplyCompletedDisplayedRefreshFieldBuildResults();
        ApplyCompletedRequestedFieldBuildResults();
    }

    private void ApplyCompletedRequestedFieldBuildResults()
    {
        int applyBudget = GetCurrentFieldResultApplyBudget();
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
            RecordFieldLoad(block.Id, result.Source, result.WorkerBuildMs);
            QueuePersistenceSaveForLoadedBlock(block, result.Source);
            EnqueueMeshBuildDispatch(block.Id);
        }
    }

    private void ApplyCompletedDisplayedRefreshFieldBuildResults()
    {
        while (_completedDisplayedRefreshFieldBuildResults.TryDequeue(out CompletedFieldBuildResult result))
        {
            if (!_blocks.TryGetValue(result.BlockId, out TerrainBlockData block) ||
                !block.MatchesFieldBuild(result.InstanceVersion, result.Revision))
            {
                continue;
            }

            block.ClearFieldBuildRunning(result.Revision);
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

            if (!block.DisplayedRefreshRequiresVisualRefresh)
            {
                block.CommitDisplayedRefreshFieldOnly(result.Field);
                QueuePersistenceSave(block, TerrainPersistenceSaveKind.DirtyPersist);
                AccumulateDisplayedRefreshAsyncRebuild(result.DisplayedRefreshOperationSequence, result.WorkerBuildMs);
                continue;
            }

            block.SetDisplayedRefreshField(result.Field);
            QueuePersistenceSave(block, TerrainPersistenceSaveKind.DirtyPersist);
            AccumulateDisplayedRefreshAsyncRebuild(result.DisplayedRefreshOperationSequence, result.WorkerBuildMs);
            EnqueueMeshBuildDispatch(block.Id, urgent: true);
        }
    }

    private void StartMeshBuildWorkers()
    {
        int workerBudget = GetCurrentMeshWorkerBudget();
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
            else if (block.HasDisplayedRefreshFieldReady &&
                     block.DisplayedRefreshRequiresVisualRefresh &&
                     IsBlockDisplayingVisuals(block))
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
                    if (IsShuttingDown)
                    {
                        return;
                    }

                    EnqueueCompletedMeshBuildResult(
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
                    if (IsShuttingDown)
                    {
                        return;
                    }

                    EnqueueCompletedMeshBuildResult(
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
        ApplyCompletedDisplayedRefreshMeshBuildResults();
        ApplyCompletedRequestedMeshBuildResults();
    }

    private void ApplyCompletedRequestedMeshBuildResults()
    {
        int applyBudget = GetCurrentMeshResultApplyBudget();
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
        }
    }

    private void ApplyCompletedDisplayedRefreshMeshBuildResults()
    {
        while (_completedDisplayedRefreshMeshBuildResults.TryDequeue(out CompletedMeshBuildResult result))
        {
            if (!_blocks.TryGetValue(result.BlockId, out TerrainBlockData block) ||
                !block.MatchesMeshBuild(result.InstanceVersion, result.Revision))
            {
                continue;
            }

            block.ClearMeshBuildRunning(result.Revision);
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

    private void ProcessMeshCommitDispatch(ulong budgetStartUsec, float commitTimeBudgetMs)
    {
        int commitBudget = GetCurrentMeshCommitBudget();
        List<TerrainBlockId> deferredCommitRequeues = new();
        Dictionary<TerrainBlockId, TerrainSeamFace> displayedRefreshSeamRoots = new();
        Dictionary<TerrainBlockId, TerrainSeamFace> currentDeformSeamRoots = new();
        while (_lastCommitCount < commitBudget &&
               !HasExceededMainThreadBudget(budgetStartUsec, commitTimeBudgetMs) &&
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

                    int remainingCommitBudget = Mathf.Max(1, commitBudget - _lastCommitCount);
                    int committedCount = CommitVisibleMeshBatch(
                        promotionBatch,
                        outgoingParent,
                        remainingCommitBudget,
                        budgetStartUsec,
                        commitTimeBudgetMs);
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

            CommitDisplayedRefreshBlock(block, displayedRefreshSeamRoots, currentDeformSeamRoots);
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

        if (displayedRefreshSeamRoots.Count > 0)
        {
            HashSet<TerrainBlockId> refreshedSeamBlocks = RefreshVisibleMixedLodSeamsImmediately(
                CollectDisplayedRefreshSeamCandidates(displayedRefreshSeamRoots));
            if (currentDeformSeamRoots.Count > 0)
            {
                HashSet<TerrainBlockId> currentDeformCandidates = CollectDisplayedRefreshSeamCandidates(currentDeformSeamRoots);
                int currentDeformRefreshCount = 0;
                foreach (TerrainBlockId refreshedBlockId in refreshedSeamBlocks)
                {
                    if (currentDeformCandidates.Contains(refreshedBlockId))
                    {
                        currentDeformRefreshCount++;
                    }
                }

                AccumulateDisplayedRefreshSeamRefresh(_lastDeformOperationSequence, currentDeformRefreshCount);
            }
        }

        TryFinalizeDisplayedRefreshConvergence(_lastDeformOperationSequence);
    }

    private void ProcessDisplayedRefreshFollowThroughPasses(ulong budgetStartUsec, float commitTimeBudgetMs)
    {
        if (!HasPendingDisplayedRefreshVisualConvergence())
        {
            return;
        }

        for (int pass = 0; pass < DisplayedRefreshFollowThroughPasses && HasPendingDisplayedRefreshVisualConvergence(); pass++)
        {
            if (HasExceededMainThreadBudget(budgetStartUsec, commitTimeBudgetMs))
            {
                break;
            }

            StartFieldBuildWorkers();
            ApplyCompletedDisplayedRefreshFieldBuildResults();
            StartMeshBuildWorkers();
            ApplyCompletedDisplayedRefreshMeshBuildResults();
            ProcessMeshCommitDispatch(budgetStartUsec, commitTimeBudgetMs);
        }
    }

    private void CommitDisplayedRefreshBlock(
        TerrainBlockData block,
        Dictionary<TerrainBlockId, TerrainSeamFace> displayedRefreshSeamRoots,
        Dictionary<TerrainBlockId, TerrainSeamFace> currentDeformSeamRoots)
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
        bool queueCollisionRefresh = shouldMaintainCollision;
        long operationSequence = block.DisplayedRefreshOperationSequence;
        TerrainChunkDirtyBoundsSnapshot dirtyBounds = block.DisplayedRefreshDirtyBounds;
        TerrainSeamFace dirtySeamFaces = ResolveDisplayedRefreshSeamFaces(block);
        ulong applyStartUsec = Time.GetTicksUsec();
        block.Renderer.ApplyVisualMesh(mesh, _activeTerrainDebugView, _surfaceColorizer);
        block.RefreshDisplayedContent(mesh, collisionPending: queueCollisionRefresh);
        if (dirtySeamFaces != TerrainSeamFace.None)
        {
            AddDisplayedRefreshSeamRoot(displayedRefreshSeamRoots, block.Id, dirtySeamFaces);
            if (operationSequence == _lastDeformOperationSequence)
            {
                AddDisplayedRefreshSeamRoot(currentDeformSeamRoots, block.Id, dirtySeamFaces);
            }
        }

        double applyMs = (Time.GetTicksUsec() - applyStartUsec) / 1000.0;
        _lastCommitMs += applyMs;
        _lastCommitCount++;
        AccumulateDisplayedRefreshVisualApply(operationSequence, applyMs, mesh.TotalTriangleCount);
        AccumulateDisplayedRefreshCommit(operationSequence);
        if (queueCollisionRefresh)
        {
            double collisionDelaySeconds = ResolveDisplayedRefreshCollisionDelaySeconds(dirtyBounds);
            block.SetPendingCollisionRefreshOperation(
                operationSequence,
                _currentTimeSeconds + collisionDelaySeconds);
            if (collisionDelaySeconds <= 0.0)
            {
                EnqueueCollisionDispatch(block.Id);
            }
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

    private void DispatchPendingCollisionRefreshes()
    {
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (!block.CollisionPending ||
                !block.IsCollisionDispatchEligible(_currentTimeSeconds) ||
                _collisionDispatchTokens.ContainsKey(block.Id) ||
                block.State is not (TerrainBlockState.Visible or TerrainBlockState.Releasable))
            {
                continue;
            }

            TerrainLodSuccessorCoverageStatus? coverage = block.State == TerrainBlockState.Releasable
                ? EvaluateSuccessorCoverage(block.Id)
                : null;
            if (!ShouldMaintainCollisionCoverage(block, coverage))
            {
                block.MarkCollisionReady();
                continue;
            }

            EnqueueCollisionDispatch(block.Id);
        }
    }

    private void ProcessCollisionDispatch()
    {
        int collisionBudget = GetCurrentCollisionBudget();
        while (_lastCollisionCount < collisionBudget &&
               TryDequeueBlockDispatch(_collisionDispatcherQueue, _collisionDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                (block.State != TerrainBlockState.Visible && block.State != TerrainBlockState.Releasable) ||
                !block.IsCollisionDispatchEligible(_currentTimeSeconds))
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
            UpdateStartupSatisfiedState(block.Id);
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
        RecycleTerrainRenderer(block.Renderer);
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

    private void AddDisplayedRefreshSeamRoot(
        Dictionary<TerrainBlockId, TerrainSeamFace> roots,
        TerrainBlockId blockId,
        TerrainSeamFace affectedFaces)
    {
        if (affectedFaces == TerrainSeamFace.None)
        {
            return;
        }

        if (roots.TryGetValue(blockId, out TerrainSeamFace existingFaces))
        {
            roots[blockId] = existingFaces | affectedFaces;
            return;
        }

        roots[blockId] = affectedFaces;
    }

    private HashSet<TerrainBlockId> CollectDisplayedRefreshSeamCandidates(
        IReadOnlyDictionary<TerrainBlockId, TerrainSeamFace> rootBlockFaces)
    {
        HashSet<TerrainBlockId> candidates = new();
        foreach (KeyValuePair<TerrainBlockId, TerrainSeamFace> pair in rootBlockFaces)
        {
            AddDisplayedRefreshSeamCandidates(candidates, pair.Key, pair.Value);
        }

        return candidates;
    }

    private void AddDisplayedRefreshSeamCandidates(
        HashSet<TerrainBlockId> candidates,
        TerrainBlockId blockId,
        TerrainSeamFace affectedFaces)
    {
        if (affectedFaces == TerrainSeamFace.None)
        {
            return;
        }

        candidates.Add(blockId);

        foreach ((TerrainSeamFace face, Vector3I offset) in SeamNeighborDirections)
        {
            if ((affectedFaces & face) == 0)
            {
                continue;
            }

            if (TryGetVisibleDisplayedRefreshCoarseNeighbor(blockId, face, out TerrainBlockId coarseNeighbor))
            {
                candidates.Add(coarseNeighbor);
            }
        }

        if (blockId.Lod <= FinestTerrainLod)
        {
            return;
        }

        foreach ((TerrainSeamFace face, Vector3I offset) in SeamNeighborDirections)
        {
            if ((affectedFaces & face) == 0)
            {
                continue;
            }

            TerrainBlockId neighborParent = new(blockId.Lod, blockId.Index - offset);
            foreach (TerrainBlockId child in TerrainMetrics.GetChildren(neighborParent))
            {
                if (IsChildOnParentOuterFace(child, face) && HasVisibleSameLodCoverage(child))
                {
                    candidates.Add(child);
                }
            }
        }
    }

    private bool TryGetVisibleDisplayedRefreshCoarseNeighbor(
        TerrainBlockId blockId,
        TerrainSeamFace face,
        out TerrainBlockId coarseNeighbor)
    {
        coarseNeighbor = default;
        if (blockId.Lod >= GetCoarsestLod() || !IsChildOnParentOuterFace(blockId, face))
        {
            return false;
        }

        TerrainBlockId parent = GetParentBlock(blockId);
        coarseNeighbor = new TerrainBlockId(parent.Lod, parent.Index + GetSeamFaceOffset(face));
        return HasVisibleSameLodCoverage(coarseNeighbor);
    }

    private HashSet<TerrainBlockId> RefreshVisibleMixedLodSeamsImmediately(IReadOnlyCollection<TerrainBlockId> candidateBlockIds)
    {
        HashSet<TerrainBlockId> refreshed = new();
        if (_allVisibleMixedLodSeamsDirty || candidateBlockIds.Count == 0)
        {
            return refreshed;
        }

        List<TerrainBlockId> orderedCandidates = new(candidateBlockIds);
        orderedCandidates.Sort(CompareTerrainBlockIds);
        foreach (TerrainBlockId blockId in orderedCandidates)
        {
            _dirtyVisibleMixedLodSeamBlocks.Remove(blockId);
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                !IsBlockDisplayingVisuals(block) ||
                block.Renderer == null ||
                !IsInstanceValid(block.Renderer))
            {
                continue;
            }

            RefreshVisibleMixedLodSeam(blockId);
            refreshed.Add(blockId);
        }

        return refreshed;
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
        if (seamBuild.FaceDiagnostics == null ||
            seamBuild.FaceDiagnostics.Length == 0 ||
            !TerrainTelemetry.IsProbeEnabled(TerrainTelemetryProbe.LodTransition))
        {
            return;
        }

        TerrainTelemetry.AppendProbeLine(
            TerrainTelemetryProbe.LodTransition,
            $"{LodTransitionTracePrefix} event=seam_build block={blockId} requested={TerrainSeamMesher.DescribeFaces(seamBuild.RequestedFaces)} " +
            $"generated={TerrainSeamMesher.DescribeFaces(seamBuild.GeneratedFaces)} strategy={seamBuild.Strategy} " +
            $"transition_faces={seamBuild.TransitionFaceCount} skirt_faces={seamBuild.SkirtFaceCount} skipped_faces={seamBuild.ExplicitSkipFaceCount} suppressed_faces={seamBuild.SuppressedFaceCount} " +
            $"triangles={seamBuild.Mesh.TotalTriangleCount}");

        foreach (TerrainSeamFaceDiagnostic diagnostic in seamBuild.FaceDiagnostics)
        {
            string neighborId = diagnostic.TransitionNeighborId?.ToString() ?? "none";
            TerrainTelemetry.AppendProbeLine(
                TerrainTelemetryProbe.LodTransition,
                $"{LodTransitionTracePrefix} event=seam_face block={blockId} face={TerrainSeamMesher.DescribeFaces(diagnostic.Face)} " +
                $"requested={FormatBool(diagnostic.Requested)} suppressed={FormatBool(diagnostic.Suppressed)} transition_neighbor={neighborId} " +
                $"transition_attempted={FormatBool(diagnostic.TransitionAttempted)} transition_succeeded={FormatBool(diagnostic.TransitionSucceeded)} " +
                $"skirt_fallback={FormatBool(diagnostic.SkirtFallbackEnabled)} final_mode={TerrainSeamMesher.GetDisplayName(diagnostic.FinalMode)}");
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

        if (!ShouldApplyReleaseHysteresis(blockId))
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

        if (!TryGetDesiredDirectPromotionBatch(outgoingParent, out List<TerrainBlockId> successors))
        {
            return false;
        }

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
                continue;
            }

            meshReadyBatch.Add(successorId);
        }

        if (meshReadyBatch.Count <= 1)
        {
            meshReadyBatch = null;
            return false;
        }

        return true;
    }

    private bool TryGetDesiredDirectPromotionBatch(
        TerrainBlockId outgoingParent,
        out List<TerrainBlockId> directSuccessors)
    {
        directSuccessors = new();
        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(outgoingParent))
        {
            if (_desiredBlocks.Contains(child))
            {
                directSuccessors.Add(child);
                continue;
            }

            // Only true direct-sibling promotions should stall for a coherent batch. When coverage under the
            // outgoing parent is mixed-depth, waiting for every deeper descendant can strand ready child commits.
            if (HasDesiredDescendantCoverage(child))
            {
                directSuccessors.Clear();
                return false;
            }
        }

        return directSuccessors.Count > 0;
    }

    private static Aabb ExpandBounds(Aabb bounds, float margin)
    {
        float safeMargin = Mathf.Max(0.0f, margin);
        Vector3 marginVector = Vector3.One * safeMargin;
        return new Aabb(
            bounds.Position - marginVector,
            bounds.Size + (marginVector * 2.0f));
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

            UpdateStartupSatisfiedState(block.Id);
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
                    if (!block.CollisionPending)
                    {
                        block.MarkCollisionPending();
                    }

                    if (block.IsCollisionDispatchEligible(_currentTimeSeconds))
                    {
                        EnqueueCollisionDispatch(block.Id);
                    }
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
        if (IsShuttingDown)
        {
            return;
        }

        EnqueueBlockDispatch(_createDispatcherQueue, _createDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueFieldBuildDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        if (IsShuttingDown)
        {
            return;
        }

        EnqueueBlockDispatch(_fieldBuildDispatcherQueue, _fieldBuildDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueMeshBuildDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        if (IsShuttingDown)
        {
            return;
        }

        EnqueueBlockDispatch(_meshBuildDispatcherQueue, _meshBuildDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueCommitDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        if (IsShuttingDown)
        {
            return;
        }

        EnqueueBlockDispatch(_commitDispatcherQueue, _commitDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueCollisionDispatch(TerrainBlockId blockId, bool urgent = false)
    {
        if (IsShuttingDown)
        {
            return;
        }

        EnqueueBlockDispatch(_collisionDispatcherQueue, _collisionDispatchTokens, blockId, farthestFirst: false, urgent);
    }

    private void EnqueueReleaseDispatch(TerrainBlockId blockId)
    {
        if (IsShuttingDown)
        {
            return;
        }

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

    private bool TryDequeuePersistenceSave(out QueuedPersistenceSaveWorkItem saveWork)
    {
        while (_persistenceSaveQueue.Count > 0)
        {
            QueuedPersistenceSaveEntry entry = _persistenceSaveQueue.Dequeue();
            if (!_pendingPersistenceSaves.TryGetValue(entry.BlockId, out PendingPersistenceSaveState state) ||
                state.Token != entry.Token)
            {
                continue;
            }

            _pendingPersistenceSaves.Remove(entry.BlockId);
            saveWork = new QueuedPersistenceSaveWorkItem(
                entry.BlockId,
                state.Kind,
                state.Field,
                state.Version);
            return true;
        }

        saveWork = default;
        return false;
    }

    private int GetPersistenceWriteVersion(TerrainBlockId blockId)
    {
        return _persistenceWriteVersions.TryGetValue(blockId, out int version)
            ? version
            : 0;
    }

    private static bool HasExceededMainThreadBudget(ulong startUsec, float budgetMs)
    {
        if (budgetMs <= 0.0f)
        {
            return false;
        }

        return ((Time.GetTicksUsec() - startUsec) / 1000.0) >= budgetMs;
    }

    private BlockDispatchPriority BuildDispatchPriority(TerrainBlockId blockId, bool farthestFirst, bool urgent, int token)
    {
        float distance = TerrainMetrics.DistanceSquaredToBlock(_config, blockId, _lastViewerPosition);
        return new BlockDispatchPriority(
            urgent ? 0 : (_startupBlocks.Contains(blockId) && IsStartupBoostActive() && !farthestFirst ? 1 : 2),
            blockId.Lod,
            farthestFirst ? -distance : distance,
            token);
    }

    private bool ShouldIncludeCollision(TerrainBlockId blockId)
    {
        return ShouldIncludeCollision(blockId, _lastViewerPosition);
    }

    private bool ShouldIncludeCollision(TerrainBlockId blockId, Vector3 viewerPosition)
    {
        if (blockId.Lod > GetMaxCollisionLod())
        {
            return false;
        }

        float referenceSpan = GetLocalCoverageReferenceSpan();
        float horizontalSafetyRadius = GetCollisionCoverageRadiusInReferenceBlocks() * referenceSpan;
        float verticalSafetyRadius = Mathf.Max(referenceSpan, Mathf.Max(0, VerticalRadius) * referenceSpan);
        return IsBlockWithinCoverageRadius(blockId, viewerPosition, horizontalSafetyRadius, verticalSafetyRadius);
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

    private float GetLocalCoverageReferenceSpan()
    {
        return TerrainMetrics.GetBlockSpan(_config, GetLocalCoverageReferenceLod());
    }

    private int GetLocalCoverageReferenceLod()
    {
        return Mathf.Min(GetSelectionCenterLod(), FinestTerrainLod + 1);
    }

    private int GetCollisionCoverageRadiusInReferenceBlocks()
    {
        return Mathf.Max(GetEffectiveLod0NearFieldRadius() + 1, CollisionSafetyRadiusXZ);
    }

    private bool ShouldApplyReleaseHysteresis(TerrainBlockId blockId)
    {
        float referenceSpan = GetLocalCoverageReferenceSpan();
        float horizontalRadius = Mathf.Max(1, GetEffectiveLod0NearFieldRadius() + 1) * referenceSpan;
        float verticalRadius = Mathf.Max(referenceSpan, (Mathf.Max(0, VerticalRadius) + 1) * referenceSpan);
        return IsBlockWithinCoverageRadius(blockId, _lastViewerPosition, horizontalRadius, verticalRadius);
    }

    private bool IsBlockWithinCoverageRadius(
        TerrainBlockId blockId,
        Vector3 viewerPosition,
        float horizontalRadius,
        float verticalRadius)
    {
        Aabb bounds = TerrainMetrics.GetBlockBounds(_config, blockId);
        Vector3 min = bounds.Position;
        Vector3 max = bounds.End;
        Vector3 clamped = new(
            Mathf.Clamp(viewerPosition.X, min.X, max.X),
            Mathf.Clamp(viewerPosition.Y, min.Y, max.Y),
            Mathf.Clamp(viewerPosition.Z, min.Z, max.Z));
        Vector3 delta = viewerPosition - clamped;
        Vector2 horizontalDelta = new(delta.X, delta.Z);
        return horizontalDelta.Length() <= horizontalRadius &&
               Mathf.Abs(delta.Y) <= verticalRadius;
    }

    private void RemoveBlockFromDispatcherQueues(TerrainBlockId blockId)
    {
        InvalidateBlockDispatch(_createDispatchTokens, blockId);
        InvalidateBlockDispatch(_fieldBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_meshBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_commitDispatchTokens, blockId);
        InvalidateBlockDispatch(_collisionDispatchTokens, blockId);
        InvalidateBlockDispatch(_releaseDispatchTokens, blockId);
        CancelPendingPersistenceSave(blockId);
    }

    private static void InvalidateBlockDispatch(Dictionary<TerrainBlockId, int> tokens, TerrainBlockId blockId)
    {
        tokens.Remove(blockId);
    }

    private void CancelPendingPersistenceSave(TerrainBlockId blockId)
    {
        _pendingPersistenceSaves.Remove(blockId);
    }

    private void EnqueueCompletedFieldBuildResult(CompletedFieldBuildResult result)
    {
        if (result.BuildPurpose == TerrainBlockBuildPurpose.DisplayedRefresh)
        {
            _completedDisplayedRefreshFieldBuildResults.Enqueue(result);
            return;
        }

        _completedFieldBuildResults.Enqueue(result);
    }

    private void EnqueueCompletedMeshBuildResult(CompletedMeshBuildResult result)
    {
        if (result.BuildPurpose == TerrainBlockBuildPurpose.DisplayedRefresh)
        {
            _completedDisplayedRefreshMeshBuildResults.Enqueue(result);
            return;
        }

        _completedMeshBuildResults.Enqueue(result);
    }

    private int CountPendingDisplayedRefreshDispatches(IReadOnlyDictionary<TerrainBlockId, int> dispatchTokens)
    {
        int count = 0;
        foreach (TerrainBlockId blockId in dispatchTokens.Keys)
        {
            if (_blocks.TryGetValue(blockId, out TerrainBlockData block) &&
                block.DisplayedRefreshDirty &&
                IsBlockDisplayingVisuals(block))
            {
                count++;
            }
        }

        return count;
    }

    private bool HasPendingDisplayedRefreshVisualConvergence()
    {
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (block.DisplayedRefreshDirty && IsBlockDisplayingVisuals(block))
            {
                return true;
            }
        }

        return false;
    }

    private int GetCompletedFieldBuildResultCount()
    {
        return _completedFieldBuildResults.Count + _completedDisplayedRefreshFieldBuildResults.Count;
    }

    private int GetCompletedMeshBuildResultCount()
    {
        return _completedMeshBuildResults.Count + _completedDisplayedRefreshMeshBuildResults.Count;
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
                _startupCompleteMs = 0.0;
                EmitSignal(SignalName.InitialLoadCompleted);
            }
            return;
        }

        RefreshStartupSatisfiedBlocks();
        InitialLoadProgress = Mathf.Clamp(
            (float)_startupSatisfiedBlocks.Count / _startupBlocks.Count,
            0.0f,
            1.0f);
        if (!_initialLoadComplete && _startupSatisfiedBlocks.Count >= _startupBlocks.Count)
        {
            _initialLoadComplete = true;
            _startupCompleteMs = _startupSelectionStartSeconds >= 0.0
                ? Math.Max(0.0, (_currentTimeSeconds - _startupSelectionStartSeconds) * 1000.0)
                : 0.0;
            EmitSignal(SignalName.InitialLoadCompleted);
        }
    }

    private void RecoverStalledStartupBlocks()
    {
        if (!_selectionInitialized ||
            _initialLoadComplete ||
            _startupBlocks.Count == 0 ||
            _currentTimeSeconds < _nextStartupStallRecoveryAtSeconds)
        {
            return;
        }

        _nextStartupStallRecoveryAtSeconds = _currentTimeSeconds + StartupStallRecoveryIntervalSeconds;
        foreach (TerrainBlockId blockId in _startupBlocks)
        {
            if (IsStartupBlockReady(blockId))
            {
                _startupSatisfiedBlocks.Add(blockId);
                continue;
            }

            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
            {
                if (_desiredBlocks.Contains(blockId) && !_createDispatchTokens.ContainsKey(blockId))
                {
                    EnqueueCreateDispatch(blockId);
                }

                continue;
            }

            if (!block.Desired)
            {
                continue;
            }

            switch (block.State)
            {
                case TerrainBlockState.Requested:
                    if (!block.FieldBuildRunning && !_fieldBuildDispatchTokens.ContainsKey(block.Id))
                    {
                        EnqueueFieldBuildDispatch(block.Id, urgent: true);
                    }
                    break;
                case TerrainBlockState.FieldReady:
                    if (!block.MeshBuildRunning && !_meshBuildDispatchTokens.ContainsKey(block.Id))
                    {
                        EnqueueMeshBuildDispatch(block.Id, urgent: true);
                    }
                    break;
                case TerrainBlockState.MeshReady:
                    if (!_commitDispatchTokens.ContainsKey(block.Id))
                    {
                        EnqueueCommitDispatch(block.Id, urgent: true);
                    }
                    break;
                case TerrainBlockState.Visible:
                case TerrainBlockState.Releasable:
                    if (block.Renderer == null ||
                        !IsInstanceValid(block.Renderer) ||
                        block.TriangleCount <= 0 ||
                        !ShouldIncludeCollision(block.Id) ||
                        block.Renderer.HasCollision)
                    {
                        break;
                    }

                    if (!block.CollisionPending)
                    {
                        block.MarkCollisionPending();
                    }

                    if (block.IsCollisionDispatchEligible(_currentTimeSeconds) &&
                        !_collisionDispatchTokens.ContainsKey(block.Id))
                    {
                        EnqueueCollisionDispatch(block.Id, urgent: true);
                    }
                    break;
            }
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

    private void RecordReplacementVisualsReady(TerrainBlockId blockId)
    {
        if (_startupFirstVisibleTerrainMs < 0.0 &&
            _startupSelectionStartSeconds >= 0.0 &&
            _blocks.TryGetValue(blockId, out TerrainBlockData block) &&
            IsBlockDisplayingVisuals(block))
        {
            _startupFirstVisibleTerrainMs = Math.Max(0.0, (_currentTimeSeconds - _startupSelectionStartSeconds) * 1000.0);
        }

        UpdateStartupSatisfiedState(blockId);
        RefreshSupersededBlockTransitionTelemetry();
    }

    private void RecordReplacementCollisionReady(TerrainBlockId blockId)
    {
        UpdateStartupSatisfiedState(blockId);
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

        if (!TerrainTelemetry.IsProbeEnabled(TerrainTelemetryProbe.LodTransition))
        {
            return;
        }

        string line =
            $"{LodTransitionTracePrefix} event={eventName} out={transition.OutgoingBlockId} out_lod={transition.OutgoingBlockId.Lod} " +
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
        TerrainTelemetry.AppendProbeLine(TerrainTelemetryProbe.LodTransition, line);
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

    private void WriteDeformTrace(
        string operation,
        bool changed,
        double deformMs,
        int invalidatedPersistedBlockCount,
        int editedBlockCount,
        int estimatedEditedSamples,
        int detailPromotions,
        int visibleBlockCount,
        int visibleFinestBlockCount,
        int requeuedBlockCount,
        int queuedVisibleBlockCount,
        double registrationMs,
        double enqueueMs,
        double syncWorkMs)
    {
        if (!TerrainTelemetry.IsProbeEnabled(TerrainTelemetryProbe.Deform))
        {
            return;
        }

        TerrainTelemetry.AppendProbeLine(
            TerrainTelemetryProbe.Deform,
            $"{DeformTracePrefix} event=apply op={operation} changed={FormatBool(changed)} ms={deformMs:0.000} " +
            $"persist_invalidated={invalidatedPersistedBlockCount} blocks={editedBlockCount} samples={estimatedEditedSamples} detail_promotions={detailPromotions} " +
            $"visible={visibleBlockCount}/{visibleFinestBlockCount} requeued={requeuedBlockCount} queued_visible={queuedVisibleBlockCount} " +
            $"reg_ms={registrationMs:0.000} enqueue_ms={enqueueMs:0.000} sync_ms={syncWorkMs:0.000} summary=\"{Sanitize(_lastEditOperationSummary)}\"");
    }

    private void WritePersistenceTrace(string eventName, string detail)
    {
        if (!TerrainTelemetry.IsProbeEnabled(TerrainTelemetryProbe.Persistence))
        {
            return;
        }

        TerrainTelemetry.AppendProbeLine(
            TerrainTelemetryProbe.Persistence,
            $"{PersistenceTracePrefix} event={eventName} {detail}");
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

    private static string Sanitize(string value)
    {
        return (value ?? string.Empty).Replace('"', '\'');
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
        TerrainTelemetryModeSnapshot telemetryMode = TerrainTelemetry.GetModeSnapshot();
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
        int persistenceQueueDepth = ComputePersistenceQueueDepth();

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
            LastChunkLoadCount = _lastStartupChunkLoadCount + _lastPersistedChunkLoadCount + _lastGeneratedChunkLoadCount,
            LastChunkLoadMs = _lastStartupChunkLoadMs + _lastPersistedChunkLoadMs + _lastGeneratedChunkLoadMs,
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
            LastStartupChunkLoadCount = _lastStartupChunkLoadCount,
            LastPersistedChunkLoadCount = _lastPersistedChunkLoadCount,
            LastGeneratedChunkLoadCount = _lastGeneratedChunkLoadCount,
            LastStartupChunkLoadMs = _lastStartupChunkLoadMs,
            LastPersistedChunkLoadMs = _lastPersistedChunkLoadMs,
            LastGeneratedChunkLoadMs = _lastGeneratedChunkLoadMs,
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
            LastDeformVisibleCommitCount = _lastDeformVisibleCommitCount,
            LastDeformSeamRefreshCount = _lastDeformSeamRefreshCount,
            LastDeformSyncRefreshMs = _lastDeformSyncWorkMs,
            LastDeformRegistrationMs = _lastDeformRegistrationMs,
            LastDeformEnqueueMs = _lastDeformEnqueueMs,
            LastDeformSyncWorkMs = _lastDeformSyncWorkMs,
            LastDeformAsyncRebuildMs = _lastDeformAsyncRebuildMs,
            LastDeformVisualApplyMs = _lastDeformVisualApplyMs,
            LastDeformCollisionRebuildMs = _lastDeformCollisionRebuildMs,
            LastDeformVisibleConvergenceMs = _lastDeformVisibleConvergenceMs,
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
            PersistedChunkRecordCount = _persistedLodBlocks.Count,
            StartupSnapshotChunkCount = _startupSnapshotBlocks.Count,
            StartupDesiredCoverageCount = _startupSatisfiedBlocks.Count,
            StartupCriticalChunkCount = _startupBlocks.Count,
            StartupCriticalSatisfiedCount = _startupSatisfiedBlocks.Count,
            StartupFullDesiredChunkCount = _lastDesiredBlockCount,
            StartupBoostActive = IsStartupBoostActive(),
            TimeToFirstVisibleTerrainMs = _startupFirstVisibleTerrainMs,
            TimeToStartupCompleteMs = _startupCompleteMs,
            RestoredFromStartupSnapshotCount = _startupRestoredBlockCount,
            RestoredFromPersistedBlockCount = _persistedRestoredBlockCount,
            ProcedurallyGeneratedBlockCount = _procedurallyGeneratedBlockCount,
            PersistenceSaveCount = _persistenceSaveCount,
            PersistenceSaveMs = _persistenceSaveMsTotal,
            PersistenceSerializationMs = _persistenceSerializationMsTotal,
            LastPersistenceSaveCount = _lastPersistenceSaveCount,
            LastPersistenceSaveMs = _lastPersistenceSaveMs,
            LastPersistenceSerializationMs = _lastPersistenceSerializationMs,
            LastPersistenceSaveScope = _lastPersistenceSaveScope,
            StartupSnapshotHits = _startupRestoredBlockCount,
            DatabaseHits = _persistedRestoredBlockCount,
            GenerationFallbacks = _procedurallyGeneratedBlockCount,
            DirtyPersistWrites = _dirtyPersistWrites,
            StartupPromotionWrites = _startupPromotionWrites,
            RetainedFieldChunkCount = _retainedPersistableFieldCount,
            PendingPersistenceSaveCount = _pendingPersistenceSaves.Count,
            PersistenceQueueDepth = persistenceQueueDepth,
            PooledRendererCount = _rendererPool.Count,
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
            StartupPendingSummary = BuildStartupPendingSummary(maxBlocks: 4),
            PendingMeshCommitSummary = BuildPendingMeshCommitSummary(maxBlocks: 6),
            InitialLoadProgress = InitialLoadProgress,
            InitialLoadComplete = _initialLoadComplete
        };
    }

    private string BuildStartupPendingSummary(int maxBlocks)
    {
        if (_startupBlocks.Count == 0)
        {
            return "none";
        }

        List<TerrainBlockId> pendingBlocks = new();
        foreach (TerrainBlockId blockId in _startupBlocks)
        {
            if (!_startupSatisfiedBlocks.Contains(blockId))
            {
                pendingBlocks.Add(blockId);
            }
        }

        if (pendingBlocks.Count == 0)
        {
            return "none";
        }

        pendingBlocks.Sort(CompareTerrainBlockIds);
        return BuildBlockSummary(pendingBlocks, maxBlocks);
    }

    private string BuildPendingMeshCommitSummary(int maxBlocks)
    {
        if (_commitDispatchTokens.Count == 0)
        {
            return "none";
        }

        List<TerrainBlockId> pendingBlocks = new(_commitDispatchTokens.Keys);
        pendingBlocks.Sort(CompareTerrainBlockIds);
        return BuildBlockSummary(pendingBlocks, maxBlocks);
    }

    private string BuildBlockSummary(IReadOnlyList<TerrainBlockId> blockIds, int maxBlocks)
    {
        if (blockIds.Count == 0)
        {
            return "none";
        }

        int limit = Mathf.Clamp(maxBlocks, 1, blockIds.Count);
        List<string> entries = new(limit);
        for (int i = 0; i < limit; i++)
        {
            entries.Add(DescribeBlockRuntimeState(blockIds[i]));
        }

        if (blockIds.Count > limit)
        {
            entries.Add($"+{blockIds.Count - limit}_more");
        }

        return string.Join(" | ", entries);
    }

    private string DescribeBlockRuntimeState(TerrainBlockId blockId)
    {
        if (!_blocks.TryGetValue(blockId, out TerrainBlockData block))
        {
            return $"{blockId}:missing";
        }

        bool hasValidRenderer = block.Renderer != null && IsInstanceValid(block.Renderer);
        bool displayingVisuals = IsBlockDisplayingVisuals(block);
        bool collisionRequired = block.TriangleCount > 0 && ShouldIncludeCollision(blockId);
        string collisionState = !collisionRequired
            ? "skip"
            : !hasValidRenderer
                ? "renderer_missing"
                : block.CollisionPending
                    ? "pending"
                    : block.Renderer.HasCollision
                        ? "ready"
                        : "missing";
        string queueState =
            $"{(_fieldBuildDispatchTokens.ContainsKey(blockId) ? 'f' : '-')}" +
            $"{(_meshBuildDispatchTokens.ContainsKey(blockId) ? 'm' : '-')}" +
            $"{(_commitDispatchTokens.ContainsKey(blockId) ? 'c' : '-')}" +
            $"{(_collisionDispatchTokens.ContainsKey(blockId) ? 'p' : '-')}";
        string runState =
            $"{(block.FieldBuildRunning ? 'f' : '-')}" +
            $"{(block.MeshBuildRunning ? 'm' : '-')}";
        return
            $"{blockId}:{SanitizeState(block.State)} tri{block.TriangleCount} desired{(block.Desired ? 1 : 0)} " +
            $"vis{(displayingVisuals ? 1 : 0)} coll{collisionState} q{queueState} run{runState}";
    }

    private string BuildLifecycleSummary()
    {
        int requested = 0;
        int fieldReady = 0;
        int meshReady = 0;
        int visible = 0;
        int releasable = 0;
        int persistenceQueueDepth = ComputePersistenceQueueDepth();
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
            $"startup {_startupSatisfiedBlocks.Count}/{_startupBlocks.Count} boost {(IsStartupBoostActive() ? "on" : "off")}  " +
            $"hold {_hysteresisRetainedBlockCount}  blocks {BuildTierCountSummary(_currentLodBlockCounts, 'l', FinestTerrainLod)}  " +
            $"split {BuildTierCountSummary(_currentSplitParentCounts, 'p', FinestTerrainLod + 1)}  " +
            $"seam {MixedLodSeamMode.GetDisplayName()} t/s/k/sup {seamSummary.TransitionFaceCount}/{seamSummary.SkirtFaceCount}/{seamSummary.ExplicitSkipFaceCount}/{seamSummary.SuppressedFaceCount}  " +
            $"sup a/r/v/h/p/f {supersededSummary.ActiveCount}/{supersededSummary.WaitingForMarkReleasableCount}/{supersededSummary.WaitingForVisualCoverageCount}/{supersededSummary.WaitingForHideCount}/{supersededSummary.WaitingForPhysicsCoverageCount}/{supersededSummary.WaitingForReleaseCount}  " +
            $"dispatch c{_createDispatchTokens.Count}  fq/r/d {_fieldBuildDispatchTokens.Count}/{Volatile.Read(ref _activeFieldWorkerJobs)}/{GetCompletedFieldBuildResultCount()}  " +
            $"mq/r/d {_meshBuildDispatchTokens.Count}/{Volatile.Read(ref _activeMeshWorkerJobs)}/{GetCompletedMeshBuildResultCount()}  " +
            $"commit {_commitDispatchTokens.Count}  coll {_collisionDispatchTokens.Count}  release {_releaseDispatchTokens.Count}  persist {persistenceQueueDepth} active {Volatile.Read(ref _activePersistenceSaveJobs)}  retain {_retainedPersistableFieldCount}  pool {_rendererPool.Count}  " +
            $"set/s {_blockSetChangeRatePerSecond:0.0}  create/s {_blockCreateRatePerSecond:0.0}  release/s {_blockReleaseRatePerSecond:0.0}";
    }

    private string BuildStartupSummary()
    {
        string firstVisible = _startupFirstVisibleTerrainMs < 0.0
            ? "pending"
            : $"{_startupFirstVisibleTerrainMs:0.0}ms";
        string startupComplete = _startupCompleteMs < 0.0
            ? "pending"
            : $"{_startupCompleteMs:0.0}ms";
        return
            $"critical {_startupSatisfiedBlocks.Count}/{_startupBlocks.Count}  desired {_lastDesiredBlockCount}  " +
            $"boost {(IsStartupBoostActive() ? "on" : "off")}  first_visible {firstVisible}  complete {startupComplete}  " +
            $"restore snapshot/persisted/generated {_startupRestoredBlockCount}/{_persistedRestoredBlockCount}/{_procedurallyGeneratedBlockCount}  " +
            $"persist_q {ComputePersistenceQueueDepth()}  retain {_retainedPersistableFieldCount}";
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
            $"raw {viewerParent}  center {centerSummary}  desired {desiredCount}  startup {_startupSatisfiedBlocks.Count}/{_startupBlocks.Count}  " +
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

    private int CommitVisibleMeshBatch(
        IReadOnlyList<TerrainBlockId> batchBlockIds,
        TerrainBlockId outgoingParent,
        int maxCommits,
        ulong budgetStartUsec,
        float commitTimeBudgetMs)
    {
        int allowedCommits = Mathf.Max(1, maxCommits);
        int committedCount = 0;
        foreach (TerrainBlockId batchBlockId in batchBlockIds)
        {
            if (committedCount >= allowedCommits ||
                HasExceededMainThreadBudget(budgetStartUsec, commitTimeBudgetMs))
            {
                break;
            }

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
        UpdateStartupSatisfiedState(block.Id);
    }

    private void WritePromotionBatchDiagnosticsLog(
        TerrainBlockId outgoingParent,
        IReadOnlyList<TerrainBlockId> batchBlockIds,
        int committedCount)
    {
        if (committedCount <= 1 || !TerrainTelemetry.IsProbeEnabled(TerrainTelemetryProbe.LodTransition))
        {
            return;
        }

        TerrainTelemetry.AppendProbeLine(
            TerrainTelemetryProbe.LodTransition,
            $"{LodTransitionTracePrefix} event=promotion_batch parent={outgoingParent} count={committedCount} " +
            $"successors={string.Join("|", batchBlockIds)}");
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

    private readonly record struct TerrainBlockFieldLoadResult(
        VoxelChunkData Field,
        TerrainChunkLoadSource Source);

    private readonly record struct ShutdownSaveCandidate(
        TerrainBlockId BlockId,
        bool Visible,
        bool CollisionCritical,
        float DistanceSquared);

    private readonly record struct ShutdownStartupSaveSummary(
        List<TerrainLodStartupBlockSnapshot> Blocks,
        int ConsideredCount,
        int SkippedCount,
        bool HitCap);

    private readonly record struct CompletedFieldBuildResult(
        TerrainBlockId BlockId,
        long InstanceVersion,
        int Revision,
        VoxelChunkData Field,
        double WorkerBuildMs,
        TerrainChunkLoadSource Source,
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

    private readonly record struct PendingPersistenceSaveState(
        int Token,
        TerrainPersistenceSaveKind Kind,
        VoxelChunkData Field,
        int Version);
    private readonly record struct QueuedPersistenceSaveWorkItem(
        TerrainBlockId BlockId,
        TerrainPersistenceSaveKind Kind,
        VoxelChunkData Field,
        int Version);
    private readonly record struct CompletedPersistenceSaveResult(
        TerrainBlockId BlockId,
        TerrainPersistenceSaveKind Kind,
        double SaveMs,
        double SerializationMs,
        bool Succeeded,
        bool Skipped,
        string FailureMessage);
    private readonly record struct QueuedPersistenceSaveEntry(TerrainBlockId BlockId, int Token);
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
