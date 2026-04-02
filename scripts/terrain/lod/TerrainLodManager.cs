using Godot;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainLodManager : Node3D
{
    private const int MaxCreateBlocksPerFrame = 32;
    private const int MaxFieldWorkerJobs = 8;
    private const int MaxMeshWorkerJobs = 8;
    private const int MaxFieldResultAppliesPerFrame = 16;
    private const int MaxMeshResultAppliesPerFrame = 16;
    private const int MaxMeshCommitsPerFrame = 16;
    private const int MaxCollisionBuildsPerFrame = 16;
    private const int MaxReleasesPerFrame = 32;
    private const int MaxRefinedParentTransitionsPerFrame = 8;

    [Signal] public delegate void InitialLoadCompletedEventHandler();

    [ExportGroup("LOD Policy")]
    [Export(PropertyHint.Range, "1,8,1")] public int CoarseRadiusXZ = 1;
    [Export(PropertyHint.Range, "0,2,1")] public int VerticalRadius;
    [ExportGroup("Far Field")]
    [Export(PropertyHint.Range, "64,320,8")] public float TargetVisibleTerrainDistance = 224.0f;
    [Export(PropertyHint.Range, "0,64,4")] public float TargetVisibleTerrainPadding = 24.0f;

    [ExportGroup("Refinement Stability")]
    [Export(PropertyHint.Range, "0,2,1")] public int SameLodBubbleRadiusXZ = 1;
    [Export(PropertyHint.Range, "1,8,1")] public int CollisionSafetyRadiusXZ = 3;
    [Export(PropertyHint.Range, "0.00,0.49,0.01")] public float BubbleMovePaddingFraction = 0.20f;
    [Export(PropertyHint.Range, "0.00,3.00,0.05")] public float BlockReleaseHysteresisSeconds = 0.70f;
    [Export(PropertyHint.Range, "0.00,3.00,0.05")] public float RefinedBlockReleaseExtraSeconds = 0.45f;
    [Export(PropertyHint.Range, "0,8,1")] public int RefinedParentPromotionsPerFrame = 2;
    [Export(PropertyHint.Range, "0,8,1")] public int RefinedParentDemotionsPerFrame = 1;

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

    private readonly Dictionary<TerrainBlockId, TerrainBlockData> _blocks = new();
    private readonly HashSet<TerrainBlockId> _desiredBlocks = new();
    private readonly HashSet<TerrainBlockId> _refinedParents = new();
    private readonly HashSet<TerrainBlockId> _targetRefinedParents = new();
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
    private readonly Dictionary<TerrainBlockId, int> _createDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _fieldBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _meshBuildDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _commitDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _collisionDispatchTokens = new();
    private readonly Dictionary<TerrainBlockId, int> _releaseDispatchTokens = new();

    private TerrainConfig _config = null!;
    private TerrainMesher _mesher = null!;
    private TerrainSurfaceColorizer _surfaceColorizer = null!;
    private TerrainWorldProfileSnapshot _latestProfileSnapshot = null!;
    private TerrainWorld _terrainWorld = null!;
    private Node3D _trackedCharacter = null!;
    private TerrainBlockId _currentCenterParent;
    private TerrainBlockId _targetCenterParent;
    private TerrainBlockId _pendingBoundaryShiftCenterParent;
    private TerrainBlockId _currentViewerParent;
    private Vector3 _lastViewerPosition;
    private double _currentTimeSeconds;
    private string _lastSelectionSummary = "waiting_for_viewer";
    private string _lastRefinementHandoffSummary = "none";
    private string _lastReleaseSummary = "none";
    private string _lastCommitSummary = "none";
    private bool _selectionInitialized;
    private bool _initialLoadComplete;
    private bool _boundaryShiftStepActive;
    private bool _boundaryShiftAwaitingDemotions;
    private int _lastDesiredBlockCount;
    private int _lastDesiredSetChangeCount;
    private int _hysteresisRetainedBlockCount;
    private int _currentBubbleParentCount;
    private int _currentCoarseBorderRadius;
    private int _currentRefinedSameLodBlockCount;
    private int _lastPromotedParentCount;
    private int _lastDemotedParentCount;
    private int _pendingPromotionParentCount;
    private int _pendingDemotionParentCount;
    private int _lastCreateCount;
    private int _lastFieldBuildCount;
    private int _lastMeshBuildCount;
    private int _lastCommitCount;
    private int _lastCollisionCount;
    private int _lastReleaseCount;
    private int _lastReleaseHysteresisDeferralCount;
    private int _lastReleaseCoverageDeferralCount;
    private double _lastFieldBuildMs;
    private double _lastMeshBuildMs;
    private double _lastCommitMs;
    private double _lastCollisionMs;
    private double _lastReleaseMs;
    private double _blockCreateRatePerSecond;
    private double _blockReleaseRatePerSecond;
    private double _blockSetChangeRatePerSecond;
    private long _refinementHandoffCount;
    private long _releaseHysteresisDeferralCount;
    private long _releaseCoverageDeferralCount;
    private long _blockInstanceVersionSequence;
    private int _activeFieldWorkerJobs;
    private int _activeMeshWorkerJobs;
    private int _dispatchSequence;
    private TerrainVisualDebugMode _activeTerrainDebugView = TerrainVisualDebugMode.Lit;

    public bool InitialLoadComplete => _initialLoadComplete;
    public float InitialLoadProgress { get; private set; }
    public TerrainVisualDebugMode ActiveTerrainDebugView => _activeTerrainDebugView;

    public override void _Ready()
    {
        _terrainWorld = GetParent() as TerrainWorld;
        _config = BuildConfig();
        _mesher = new TerrainMesher(_config);
        _surfaceColorizer = new TerrainSurfaceColorizer(_config);
        _activeTerrainDebugView = ResolveTerrainDebugView(_terrainWorld?.TerrainDebugView ?? TerrainVisualDebugMode.Lit);
        _trackedCharacter = ResolveTrackedCharacter();
        _latestProfileSnapshot = BuildProfileSnapshot();
    }

    public override void _Process(double delta)
    {
        _currentTimeSeconds = Time.GetTicksUsec() / 1_000_000.0;
        _trackedCharacter ??= ResolveTrackedCharacter();
        if (_trackedCharacter == null)
        {
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
        RefreshLifecycleRates();
        UpdateInitialLoadState();
        _latestProfileSnapshot = BuildProfileSnapshot();
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        return _latestProfileSnapshot ??= BuildProfileSnapshot();
    }

    public string GetDebugSummary()
    {
        _debugBuilder.Clear();
        _debugBuilder.AppendLine("TerrainLodManager active.");
        _debugBuilder.AppendLine($"LOD0 span {TerrainMetrics.GetBlockSpan(_config, 0):0.0}  LOD1 span {TerrainMetrics.GetBlockSpan(_config, 1):0.0}");
        _debugBuilder.AppendLine($"Debug {_activeTerrainDebugView.GetDisplayName()}");
        _debugBuilder.AppendLine(_lastSelectionSummary);
        _debugBuilder.AppendLine($"Lifecycle {BuildLifecycleSummary()}");
        _debugBuilder.AppendLine($"Handoff {_lastRefinementHandoffSummary}");
        _debugBuilder.Append($"Latest {(_lastCommitSummary == string.Empty ? "none" : _lastCommitSummary)}");
        return _debugBuilder.ToString();
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
                WaterLevel = -2.6f,
                ShorelineFalloff = 3.4f,
                WaterBasinInfluence = 0.48f,
                CoarseRadiusXZ = Mathf.Max(1, CoarseRadiusXZ),
                VerticalRadius = Mathf.Max(0, VerticalRadius),
                FieldBuildsPerFrame = Mathf.Clamp(FieldWorkerJobs, 1, MaxFieldWorkerJobs),
                MeshBuildsPerFrame = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs),
                CommitsPerFrame = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame),
                ReleasesPerFrame = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame),
                GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods,
                MeshColorMode = VoxelMeshColorMode.MaterialTint
            };
        }

        return new TerrainConfig
        {
            PointsPerAxis = Mathf.Max(4, _terrainWorld.PointsPerAxis),
            BaseVoxelSize = Mathf.Max(0.1f, _terrainWorld.VoxelSize),
            BaseY = _terrainWorld.BaseY,
            Seed = _terrainWorld.Seed,
            TerrainHeight = _terrainWorld.TerrainHeight,
            DetailHeight = _terrainWorld.DetailHeight,
            CaveScale = _terrainWorld.CaveScale,
            CaveThreshold = _terrainWorld.CaveThreshold,
            WaterLevel = _terrainWorld.WaterLevel,
            ShorelineFalloff = Mathf.Max(0.4f, _terrainWorld.ShorelineFalloff),
            WaterBasinInfluence = Mathf.Clamp(_terrainWorld.WaterBasinInfluence, 0.0f, 1.0f),
            CoarseRadiusXZ = Mathf.Max(1, CoarseRadiusXZ),
            VerticalRadius = Mathf.Max(0, VerticalRadius),
            FieldBuildsPerFrame = Mathf.Clamp(FieldWorkerJobs, 1, MaxFieldWorkerJobs),
            MeshBuildsPerFrame = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs),
            CommitsPerFrame = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame),
            ReleasesPerFrame = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame),
            GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods,
            MeshColorMode = VoxelMeshColorMode.MaterialTint
        };
    }

    private static TerrainVisualDebugMode ResolveTerrainDebugView(TerrainVisualDebugMode debugView)
    {
        return OS.IsDebugBuild()
            ? debugView
            : TerrainVisualDebugMode.Lit;
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
        TerrainBlockId viewerParent = ComputeCenterParent(viewerPosition);
        TerrainBlockId targetCenterParent = ResolveSelectionCenterParent(viewerPosition, viewerParent);
        HashSet<TerrainBlockId> requestedRefinedParents = BuildRefinedParents(targetCenterParent);
        int coarseBorderRadius = GetEffectiveCoarseBorderRadius();

        _currentViewerParent = viewerParent;
        if (!_selectionInitialized)
        {
            _startupBlocks.Clear();
            _startupSatisfiedBlocks.Clear();
            _refinedParents.Clear();
            _targetRefinedParents.Clear();
            _currentCenterParent = targetCenterParent;
            _targetCenterParent = targetCenterParent;
            foreach (TerrainBlockId parent in requestedRefinedParents)
            {
                _refinedParents.Add(parent);
                _targetRefinedParents.Add(parent);
            }
            _selectionInitialized = true;
            _boundaryShiftStepActive = false;
            _boundaryShiftAwaitingDemotions = false;
            _pendingBoundaryShiftCenterParent = _currentCenterParent;
            _lastDesiredSetChangeCount = 0;
            _lastPromotedParentCount = 0;
            _lastDemotedParentCount = 0;
            _pendingPromotionParentCount = 0;
            _pendingDemotionParentCount = 0;
        }
        else if (!_targetCenterParent.Equals(targetCenterParent) || !_targetRefinedParents.SetEquals(requestedRefinedParents))
        {
            _targetCenterParent = targetCenterParent;
            _targetRefinedParents.Clear();
            foreach (TerrainBlockId parent in requestedRefinedParents)
            {
                _targetRefinedParents.Add(parent);
            }
        }

        ApplyRefinedParentTransitionBudget();

        HashSet<TerrainBlockId> desired = BuildDesiredSet(_currentCenterParent, _refinedParents, coarseBorderRadius);
        _lastDesiredBlockCount = desired.Count;
        _currentBubbleParentCount = _refinedParents.Count;
        _currentCoarseBorderRadius = coarseBorderRadius;
        _currentRefinedSameLodBlockCount = CountBlocksAtLod(desired, lod: 0);

        if (_startupBlocks.Count == 0)
        {
            foreach (TerrainBlockId blockId in desired)
            {
                _startupBlocks.Add(blockId);
            }
        }

        ApplyDesiredSetChanges(desired);

        _hysteresisRetainedBlockCount = CountHysteresisRetainedBlocks();
        _lastSelectionSummary = BuildSelectionSummary(
            _currentCenterParent,
            _targetCenterParent,
            viewerParent,
            desired.Count,
            _refinedParents,
            _currentRefinedSameLodBlockCount,
            _hysteresisRetainedBlockCount,
            _lastDesiredSetChangeCount);
    }

    private TerrainBlockId ComputeCenterParent(Vector3 viewerPosition)
    {
        float fineSpan = TerrainMetrics.GetBlockSpan(_config, 0);
        float surfaceY = _mesher.SampleSurfaceHeight(viewerPosition.X, viewerPosition.Z);
        float anchorY = Mathf.Max(_config.BaseY, surfaceY - (fineSpan * 0.5f));
        Vector3 anchor = new(viewerPosition.X, anchorY, viewerPosition.Z);
        return TerrainMetrics.GetBlockForWorldPosition(_config, 1, anchor);
    }

    private TerrainBlockId ResolveSelectionCenterParent(Vector3 viewerPosition, TerrainBlockId viewerParent)
    {
        if (!_selectionInitialized)
        {
            return viewerParent;
        }

        if (viewerParent.Index.Y != _currentCenterParent.Index.Y)
        {
            return viewerParent;
        }

        int bubbleRadius = Mathf.Max(0, SameLodBubbleRadiusXZ);
        int preferredCenterRadius = Mathf.Max(0, bubbleRadius - 1);
        Vector3I parentDelta = viewerParent.Index - _currentCenterParent.Index;
        if (Mathf.Abs(parentDelta.X) > preferredCenterRadius || Mathf.Abs(parentDelta.Z) > preferredCenterRadius)
        {
            // Treat the bubble's outer ring as a buffer instead of where the player lives. Recenter as soon as the
            // viewer enters that outer ring so the character stays closer to the middle while walking.
            return new TerrainBlockId(
                _currentCenterParent.Lod,
                _currentCenterParent.Index + new Vector3I(
                    ComputeCenterRecenteringDelta(parentDelta.X, preferredCenterRadius),
                    0,
                    ComputeCenterRecenteringDelta(parentDelta.Z, preferredCenterRadius)));
        }

        float parentSpan = TerrainMetrics.GetBlockSpan(_config, 1);
        float padding = Mathf.Clamp(BubbleMovePaddingFraction, 0.0f, 0.49f) * parentSpan;
        Vector3 minOrigin = TerrainMetrics.GetBlockOrigin(
            _config,
            new TerrainBlockId(1, _currentCenterParent.Index + new Vector3I(-bubbleRadius, 0, -bubbleRadius)));
        Vector3 maxOrigin = TerrainMetrics.GetBlockOrigin(
            _config,
            new TerrainBlockId(1, _currentCenterParent.Index + new Vector3I(bubbleRadius, 0, bubbleRadius)));
        float maxX = maxOrigin.X + parentSpan;
        float maxZ = maxOrigin.Z + parentSpan;
        // Let the same-LOD bubble trail the raw viewer parent until the player is meaningfully outside the
        // current bubble footprint. This keeps walking inside the neighborhood from constantly shifting blocks.
        bool outsideStableBounds =
            viewerPosition.X < (minOrigin.X - padding) ||
            viewerPosition.X > (maxX + padding) ||
            viewerPosition.Z < (minOrigin.Z - padding) ||
            viewerPosition.Z > (maxZ + padding);
        return outsideStableBounds ? viewerParent : _currentCenterParent;
    }

    private HashSet<TerrainBlockId> BuildRefinedParents(TerrainBlockId centerParent)
    {
        HashSet<TerrainBlockId> refinedParents = new();
        int bubbleRadius = Mathf.Max(0, SameLodBubbleRadiusXZ);
        for (int z = -bubbleRadius; z <= bubbleRadius; z++)
        {
            for (int x = -bubbleRadius; x <= bubbleRadius; x++)
            {
                AddRefinedParent(refinedParents, centerParent, x, z);
            }
        }

        return refinedParents;
    }

    private static void AddRefinedParent(HashSet<TerrainBlockId> refinedParents, TerrainBlockId centerParent, int xOffset, int zOffset)
    {
        refinedParents.Add(new TerrainBlockId(
            centerParent.Lod,
            centerParent.Index + new Vector3I(xOffset, 0, zOffset)));
    }

    private HashSet<TerrainBlockId> BuildDesiredSet(
        TerrainBlockId centerParent,
        IReadOnlySet<TerrainBlockId> refinedParents,
        int coarseBorderRadius)
    {
        HashSet<TerrainBlockId> desired = new();
        int outerRadius = Mathf.Max(SameLodBubbleRadiusXZ, 0) + coarseBorderRadius;

        // Phase-one policy: keep a stable same-LOD bubble around the player, then keep a simple coarse border
        // outside it. The coarse border gives the bubble room to move without exposing an unloaded edge first.
        for (int z = -outerRadius; z <= outerRadius; z++)
        {
            for (int y = -_config.VerticalRadius; y <= _config.VerticalRadius; y++)
            {
                for (int x = -outerRadius; x <= outerRadius; x++)
                {
                    TerrainBlockId coarseBlock = new(
                        1,
                        centerParent.Index + new Vector3I(x, y, z));
                    if (refinedParents.Contains(coarseBlock))
                    {
                        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(coarseBlock))
                        {
                            desired.Add(child);
                        }
                        continue;
                    }

                    desired.Add(coarseBlock);
                }
            }
        }

        return desired;
    }

    private int GetEffectiveCoarseBorderRadius()
    {
        int minimumBorderRadius = Mathf.Max(1, _config.CoarseRadiusXZ);
        float parentSpan = TerrainMetrics.GetBlockSpan(_config, 1);
        float targetDistance = Mathf.Max(parentSpan, TargetVisibleTerrainDistance);
        Camera3D camera = GetViewport().GetCamera3D();
        if (camera != null && camera.Far > 0.0f)
        {
            targetDistance = Mathf.Min(targetDistance, camera.Far);
        }

        targetDistance += Mathf.Max(0.0f, TargetVisibleTerrainPadding);
        int targetOuterRadius = Mathf.Max(1, Mathf.CeilToInt(targetDistance / parentSpan));
        return Mathf.Max(minimumBorderRadius, targetOuterRadius - Mathf.Max(0, SameLodBubbleRadiusXZ));
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
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                block.State != TerrainBlockState.Requested)
            {
                continue;
            }

            if (!block.Desired)
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
            Interlocked.Increment(ref _activeFieldWorkerJobs);
            _ = Task.Run(() =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                try
                {
                    VoxelChunkData field = _mesher.BuildField(blockId);
                    _completedFieldBuildResults.Enqueue(
                        new CompletedFieldBuildResult(blockId, instanceVersion, revision, field, stopwatch.Elapsed.TotalMilliseconds, Succeeded: true));
                }
                catch
                {
                    _completedFieldBuildResults.Enqueue(
                        new CompletedFieldBuildResult(blockId, instanceVersion, revision, null, stopwatch.Elapsed.TotalMilliseconds, Succeeded: false));
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
        }
    }

    private void StartMeshBuildWorkers()
    {
        int workerBudget = Mathf.Clamp(MeshWorkerJobs, 1, MaxMeshWorkerJobs);
        while (Volatile.Read(ref _activeMeshWorkerJobs) < workerBudget &&
               TryDequeueBlockDispatch(_meshBuildDispatcherQueue, _meshBuildDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                block.State != TerrainBlockState.FieldReady)
            {
                continue;
            }

            if (!block.Desired)
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
                        new CompletedMeshBuildResult(blockId, instanceVersion, revision, mesh, stopwatch.Elapsed.TotalMilliseconds, Succeeded: true));
                }
                catch
                {
                    _completedMeshBuildResults.Enqueue(
                        new CompletedMeshBuildResult(blockId, instanceVersion, revision, null, stopwatch.Elapsed.TotalMilliseconds, Succeeded: false));
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

    private void ProcessMeshCommitDispatch()
    {
        int commitBudget = Mathf.Clamp(MeshCommitsPerFrame, 1, MaxMeshCommitsPerFrame);
        while (_lastCommitCount < commitBudget &&
               TryDequeueBlockDispatch(_commitDispatcherQueue, _commitDispatchTokens, out TerrainBlockId blockId))
        {
            if (!_blocks.TryGetValue(blockId, out TerrainBlockData block) ||
                block.State != TerrainBlockState.MeshReady)
            {
                continue;
            }

            if (!block.Desired)
            {
                EnqueueReleaseDispatch(blockId);
                continue;
            }

            bool includeCollision = ShouldIncludeCollision(block.Id) && block.TriangleCount > 0;
            ulong commitStart = Time.GetTicksUsec();
            block.Renderer.ApplyVisualMesh(block.Mesh, _activeTerrainDebugView, _surfaceColorizer);
            block.MarkVisible(collisionPending: includeCollision);
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
    }

    private void RefreshCollisionCoverage()
    {
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (block.State != TerrainBlockState.Visible ||
                !block.Desired ||
                block.CollisionPending ||
                block.TriangleCount <= 0 ||
                !ShouldIncludeCollision(block.Id) ||
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
                block.State != TerrainBlockState.Visible)
            {
                continue;
            }

            if (!block.Desired)
            {
                continue;
            }

            bool includeCollision = ShouldIncludeCollision(block.Id);
            ulong collisionStart = Time.GetTicksUsec();
            block.Renderer.ApplyCollision(includeCollision && block.TriangleCount > 0);
            _lastCollisionMs += (Time.GetTicksUsec() - collisionStart) / 1000.0;
            _lastCollisionCount++;
            block.MarkCollisionReady();
        }
    }

    private void ProcessReleaseDispatch()
    {
        int releaseBudget = Mathf.Clamp(ReleasesPerFrame, 1, MaxReleasesPerFrame);
        while (_lastReleaseCount < releaseBudget &&
               TryDequeueBlockDispatch(_releaseDispatcherQueue, _releaseDispatchTokens, out TerrainBlockId blockId))
        {
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

            if (block.IsHeldForRelease(_currentTimeSeconds))
            {
                _lastReleaseHysteresisDeferralCount++;
                _releaseHysteresisDeferralCount++;
                EnqueueReleaseDispatch(blockId);
                break;
            }

            if (!HasReadySuccessorCoverage(block.Id))
            {
                _lastReleaseCoverageDeferralCount++;
                _releaseCoverageDeferralCount++;
                EnqueueReleaseDispatch(blockId);
                break;
            }

            ulong releaseStart = Time.GetTicksUsec();
            ReleaseBlock(block.Id, "fell_outside_desired_set");
            _lastReleaseMs += (Time.GetTicksUsec() - releaseStart) / 1000.0;
        }
    }

    private void ReleaseBlock(TerrainBlockId blockId, string reason)
    {
        if (!_blocks.Remove(blockId, out TerrainBlockData block))
        {
            return;
        }

        if (_startupBlocks.Contains(blockId))
        {
            _startupSatisfiedBlocks.Add(blockId);
        }

        block.CancelPendingData();
        block.Renderer.QueueFree();
        RemoveBlockFromDispatcherQueues(blockId);
        _lastReleaseCount++;
        _recentReleaseTimes.Enqueue(_currentTimeSeconds);
        _lastReleaseSummary = $"{blockId} {reason}";
    }

    private void RecordRefinementHandoff(
        TerrainBlockId previousCenterParent,
        TerrainBlockId nextCenterParent,
        TerrainBlockId viewerParent,
        IReadOnlySet<TerrainBlockId> nextRefinedParents)
    {
        HashSet<TerrainBlockId> previousRefinedParents = BuildRefinedParents(previousCenterParent);
        List<TerrainBlockId> added = new();
        foreach (TerrainBlockId parent in nextRefinedParents)
        {
            if (!previousRefinedParents.Contains(parent))
            {
                added.Add(parent);
            }
        }

        List<TerrainBlockId> removed = new();
        foreach (TerrainBlockId parent in previousRefinedParents)
        {
            if (!nextRefinedParents.Contains(parent))
            {
                removed.Add(parent);
            }
        }

        _refinementHandoffCount++;
        _lastRefinementHandoffSummary =
            $"handoff {_refinementHandoffCount} center {previousCenterParent} -> {nextCenterParent} raw {viewerParent} target {_targetCenterParent} " +
            $"+{BuildBlockListSummary(added)} -{BuildBlockListSummary(removed)}";
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
        double holdSeconds = Mathf.Max(0.0f, BlockReleaseHysteresisSeconds);
        if (blockId.Lod == 0)
        {
            holdSeconds += Mathf.Max(0.0f, RefinedBlockReleaseExtraSeconds);
        }

        return holdSeconds;
    }

    private void ApplyRefinedParentTransitionBudget()
    {
        _lastPromotedParentCount = 0;
        _lastDemotedParentCount = 0;

        if (!_boundaryShiftStepActive && !_currentCenterParent.Equals(_targetCenterParent))
        {
            _pendingBoundaryShiftCenterParent = ComputeNextCenterStep(_currentCenterParent, _targetCenterParent);
            _boundaryShiftStepActive = !_pendingBoundaryShiftCenterParent.Equals(_currentCenterParent);
            _boundaryShiftAwaitingDemotions = false;
        }

        if (_boundaryShiftStepActive)
        {
            HashSet<TerrainBlockId> pendingStepBubbleParents = BuildRefinedParents(_pendingBoundaryShiftCenterParent);
            if (!_boundaryShiftAwaitingDemotions)
            {
                List<TerrainBlockId> enteringBoundaryParents = GetSortedSetDifference(
                    pendingStepBubbleParents,
                    _refinedParents,
                    farthestFirst: false);
                if (enteringBoundaryParents.Count > 0)
                {
                    ApplyRefinedParentPromotions(enteringBoundaryParents);
                    _pendingPromotionParentCount = CountSetDifference(pendingStepBubbleParents, _refinedParents);
                    _pendingDemotionParentCount = 0;
                    return;
                }

                TerrainBlockId previousCenterParent = _currentCenterParent;
                _currentCenterParent = _pendingBoundaryShiftCenterParent;
                _boundaryShiftAwaitingDemotions = true;
                RecordRefinementHandoff(
                    previousCenterParent,
                    _currentCenterParent,
                    _currentViewerParent,
                    pendingStepBubbleParents);
                _pendingPromotionParentCount = 0;
                _pendingDemotionParentCount = CountSetDifference(_refinedParents, pendingStepBubbleParents);
                return;
            }

            List<TerrainBlockId> leavingBoundaryParents = GetSortedSetDifference(
                _refinedParents,
                pendingStepBubbleParents,
                farthestFirst: true);
            if (leavingBoundaryParents.Count > 0)
            {
                ApplyRefinedParentDemotions(leavingBoundaryParents);
                _pendingPromotionParentCount = 0;
                _pendingDemotionParentCount = CountSetDifference(_refinedParents, pendingStepBubbleParents);
                return;
            }

            _boundaryShiftStepActive = false;
            _boundaryShiftAwaitingDemotions = false;
        }

        HashSet<TerrainBlockId> activeBubbleParents = BuildRefinedParents(_currentCenterParent);
        _pendingPromotionParentCount = CountSetDifference(_targetRefinedParents, _refinedParents);
        _pendingDemotionParentCount = CountSetDifference(_refinedParents, activeBubbleParents);
    }

    private void ApplyRefinedParentPromotions(IReadOnlyList<TerrainBlockId> enteringBoundaryParents)
    {
        int promotionBudget = Mathf.Clamp(RefinedParentPromotionsPerFrame, 0, MaxRefinedParentTransitionsPerFrame);
        for (int i = 0; i < enteringBoundaryParents.Count && _lastPromotedParentCount < promotionBudget; i++)
        {
            _refinedParents.Add(enteringBoundaryParents[i]);
            _lastPromotedParentCount++;
        }
    }

    private void ApplyRefinedParentDemotions(IReadOnlyList<TerrainBlockId> leavingBoundaryParents)
    {
        int demotionBudget = Mathf.Clamp(RefinedParentDemotionsPerFrame, 0, MaxRefinedParentTransitionsPerFrame);
        for (int i = 0; i < leavingBoundaryParents.Count && _lastDemotedParentCount < demotionBudget; i++)
        {
            _refinedParents.Remove(leavingBoundaryParents[i]);
            _lastDemotedParentCount++;
        }
    }

    private List<TerrainBlockId> GetSortedSetDifference(
        IReadOnlySet<TerrainBlockId> sourceSet,
        IReadOnlySet<TerrainBlockId> excludeSet,
        bool farthestFirst)
    {
        List<TerrainBlockId> pending = new();
        foreach (TerrainBlockId parent in sourceSet)
        {
            if (!excludeSet.Contains(parent))
            {
                pending.Add(parent);
            }
        }

        pending.Sort((a, b) => CompareRefinedParentTransitionPriority(a, b, farthestFirst));
        return pending;
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

    private int CompareRefinedParentTransitionPriority(TerrainBlockId a, TerrainBlockId b, bool farthestFirst)
    {
        float aDistance = TerrainMetrics.DistanceSquaredToBlock(_config, a, _lastViewerPosition);
        float bDistance = TerrainMetrics.DistanceSquaredToBlock(_config, b, _lastViewerPosition);
        int distanceCompare = aDistance.CompareTo(bDistance);
        if (distanceCompare != 0)
        {
            return farthestFirst ? -distanceCompare : distanceCompare;
        }

        return CompareBlockIds(a, b);
    }

    private bool HasReadySuccessorCoverage(TerrainBlockId outgoingBlockId)
    {
        List<TerrainBlockId> successors = GetDesiredSuccessors(outgoingBlockId);
        if (successors.Count == 0)
        {
            return true;
        }

        foreach (TerrainBlockId successorId in successors)
        {
            if (!_blocks.TryGetValue(successorId, out TerrainBlockData successor) ||
                successor.State != TerrainBlockState.Visible)
            {
                return false;
            }

            if (successor.TriangleCount > 0 &&
                ShouldIncludeCollision(successorId) &&
                (successor.CollisionPending || !successor.Renderer.HasCollision))
            {
                return false;
            }
        }

        return true;
    }

    private List<TerrainBlockId> GetDesiredSuccessors(TerrainBlockId outgoingBlockId)
    {
        List<TerrainBlockId> successors = new();
        if (outgoingBlockId.Lod <= 0)
        {
            TerrainBlockId parent = GetParentBlock(outgoingBlockId);
            if (_desiredBlocks.Contains(parent))
            {
                successors.Add(parent);
            }

            return successors;
        }

        foreach (TerrainBlockId child in TerrainMetrics.GetChildren(outgoingBlockId))
        {
            if (_desiredBlocks.Contains(child))
            {
                successors.Add(child);
            }
        }

        return successors;
    }

    private static TerrainBlockId GetParentBlock(TerrainBlockId childBlockId)
    {
        Vector3I parentIndex = new(
            Mathf.FloorToInt(childBlockId.Index.X / 2.0f),
            Mathf.FloorToInt(childBlockId.Index.Y / 2.0f),
            Mathf.FloorToInt(childBlockId.Index.Z / 2.0f));
        return new TerrainBlockId(childBlockId.Lod + 1, parentIndex);
    }

    private static int CountBlocksAtLod(IReadOnlyCollection<TerrainBlockId> blocks, int lod)
    {
        int count = 0;
        foreach (TerrainBlockId block in blocks)
        {
            if (block.Lod == lod)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountSetDifference(IReadOnlySet<TerrainBlockId> minuend, IReadOnlySet<TerrainBlockId> subtrahend)
    {
        int count = 0;
        foreach (TerrainBlockId blockId in minuend)
        {
            if (!subtrahend.Contains(blockId))
            {
                count++;
            }
        }

        return count;
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

        InvalidateBlockDispatch(_fieldBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_meshBuildDispatchTokens, blockId);
        InvalidateBlockDispatch(_commitDispatchTokens, blockId);
        InvalidateBlockDispatch(_collisionDispatchTokens, blockId);
        if (block.State == TerrainBlockState.Visible)
        {
            // Desired-set transitions only enqueue state changes here; the actual renderer and mesh work is pulled
            // later through the dispatcher queues in small per-frame slices.
            block.MarkReleasable(_currentTimeSeconds + ComputeReleaseHoldSeconds(block.Id));
        }
        else
        {
            block.Desired = false;
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
                if (block.CollisionPending ||
                    (block.TriangleCount > 0 && ShouldIncludeCollision(block.Id) && !block.Renderer.HasCollision))
                {
                    block.MarkCollisionPending();
                    EnqueueCollisionDispatch(block.Id);
                }
                break;
            case TerrainBlockState.Releasable:
                EnqueueReleaseDispatch(block.Id);
                break;
        }
    }

    private void EnqueueCreateDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_createDispatcherQueue, _createDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueFieldBuildDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_fieldBuildDispatcherQueue, _fieldBuildDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueMeshBuildDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_meshBuildDispatcherQueue, _meshBuildDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueCommitDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_commitDispatcherQueue, _commitDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueCollisionDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_collisionDispatcherQueue, _collisionDispatchTokens, blockId, farthestFirst: false);
    }

    private void EnqueueReleaseDispatch(TerrainBlockId blockId)
    {
        EnqueueBlockDispatch(_releaseDispatcherQueue, _releaseDispatchTokens, blockId, farthestFirst: true);
    }

    private void EnqueueBlockDispatch(
        PriorityQueue<QueuedBlockDispatchEntry, BlockDispatchPriority> queue,
        Dictionary<TerrainBlockId, int> tokens,
        TerrainBlockId blockId,
        bool farthestFirst)
    {
        int token = ++_dispatchSequence;
        tokens[blockId] = token;
        queue.Enqueue(
            new QueuedBlockDispatchEntry(blockId, token),
            BuildDispatchPriority(blockId, farthestFirst, token));
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

    private BlockDispatchPriority BuildDispatchPriority(TerrainBlockId blockId, bool farthestFirst, int token)
    {
        float distance = TerrainMetrics.DistanceSquaredToBlock(_config, blockId, _lastViewerPosition);
        return new BlockDispatchPriority(
            blockId.Lod,
            farthestFirst ? -distance : distance,
            token);
    }

    private bool ShouldIncludeCollision(TerrainBlockId blockId)
    {
        TerrainBlockId parent = blockId.Lod == 0
            ? GetParentBlock(blockId)
            : blockId;
        int safetyRadius = Mathf.Max(SameLodBubbleRadiusXZ + 1, CollisionSafetyRadiusXZ);
        Vector3I delta = parent.Index - _currentCenterParent.Index;
        return Mathf.Abs(delta.X) <= safetyRadius &&
               Mathf.Abs(delta.Y) <= Mathf.Max(0, VerticalRadius) &&
               Mathf.Abs(delta.Z) <= safetyRadius;
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

    private static int CompareBlockIds(TerrainBlockId a, TerrainBlockId b)
    {
        int lodCompare = a.Lod.CompareTo(b.Lod);
        if (lodCompare != 0)
        {
            return lodCompare;
        }

        int xCompare = a.Index.X.CompareTo(b.Index.X);
        if (xCompare != 0)
        {
            return xCompare;
        }

        int yCompare = a.Index.Y.CompareTo(b.Index.Y);
        if (yCompare != 0)
        {
            return yCompare;
        }

        return a.Index.Z.CompareTo(b.Index.Z);
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

    private TerrainWorldProfileSnapshot BuildProfileSnapshot()
    {
        int requested = 0;
        int fieldReady = 0;
        int meshReady = 0;
        int visible = 0;
        int releasable = 0;

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
            $"lod0 span {TerrainMetrics.GetBlockSpan(_config, 0):0.0}  lod1 span {TerrainMetrics.GetBlockSpan(_config, 1):0.0}  " +
            $"raw {_currentViewerParent}  center {centerSummary}  bubble_r {Mathf.Max(0, SameLodBubbleRadiusXZ)}  border_r {_currentCoarseBorderRadius}  move_pad {BubbleMovePaddingFraction:0.00}  debug {_activeTerrainDebugView.GetDisplayName()}";

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
            TrackedBiomeSummary = viewerSummary,
            TrackedDetailSummary = BuildLifecycleSummary(),
            TrackedCoverageStateSummary = lodSummary,
            NearPlayerBubbleParentCount = _currentBubbleParentCount,
            RefinedParentCount = _refinedParents.Count,
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
            $"hold {_hysteresisRetainedBlockCount}  step +{_lastPromotedParentCount}/-{_lastDemotedParentCount}  " +
            $"dispatch c{_createDispatchTokens.Count}  fq/r/d {_fieldBuildDispatchTokens.Count}/{Volatile.Read(ref _activeFieldWorkerJobs)}/{_completedFieldBuildResults.Count}  " +
            $"mq/r/d {_meshBuildDispatchTokens.Count}/{Volatile.Read(ref _activeMeshWorkerJobs)}/{_completedMeshBuildResults.Count}  " +
            $"commit {_commitDispatchTokens.Count}  coll {_collisionDispatchTokens.Count}  release {_releaseDispatchTokens.Count}  " +
            $"pending +{_pendingPromotionParentCount}/-{_pendingDemotionParentCount}  set/s {_blockSetChangeRatePerSecond:0.0}  " +
            $"create/s {_blockCreateRatePerSecond:0.0}  release/s {_blockReleaseRatePerSecond:0.0}";
    }

    private string BuildSelectionSummary(
        TerrainBlockId centerParent,
        TerrainBlockId targetCenterParent,
        TerrainBlockId viewerParent,
        int desiredCount,
        IReadOnlyCollection<TerrainBlockId> refinedParents,
        int refinedSameLodBlockCount,
        int retainedBlocks,
        int desiredSetChangeCount)
    {
        string centerSummary = centerParent.Equals(targetCenterParent)
            ? centerParent.ToString()
            : $"{centerParent}->{targetCenterParent}";
        return
            $"raw {viewerParent}  center {centerSummary}  border {_currentCoarseBorderRadius}  desired {desiredCount}  " +
            $"bubble {refinedParents.Count}p/{refinedSameLodBlockCount} lod0:{BuildBlockListSummary(refinedParents)}  " +
            $"held {retainedBlocks}  changed {desiredSetChangeCount}  pending +{_pendingPromotionParentCount}/-{_pendingDemotionParentCount}  " +
            "policy stable-bubble + boundary-shift + successor-held-release.";
    }

    private static string BuildBlockListSummary(IReadOnlyCollection<TerrainBlockId> blockIds)
    {
        if (blockIds.Count == 0)
        {
            return "none";
        }

        List<TerrainBlockId> ordered = new(blockIds);
        ordered.Sort(CompareBlockIds);

        StringBuilder builder = new();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('|');
            }

            TerrainBlockId blockId = ordered[i];
            builder.Append(blockId.Index.X);
            builder.Append(',');
            builder.Append(blockId.Index.Y);
            builder.Append(',');
            builder.Append(blockId.Index.Z);
        }

        return builder.ToString();
    }

    private readonly record struct CompletedFieldBuildResult(
        TerrainBlockId BlockId,
        long InstanceVersion,
        int Revision,
        VoxelChunkData Field,
        double WorkerBuildMs,
        bool Succeeded);

    private readonly record struct CompletedMeshBuildResult(
        TerrainBlockId BlockId,
        long InstanceVersion,
        int Revision,
        VoxelMeshBuildResult? Mesh,
        double WorkerBuildMs,
        bool Succeeded);

    private readonly record struct QueuedBlockDispatchEntry(TerrainBlockId BlockId, int Token);

    private readonly record struct BlockDispatchPriority(int Lod, float DistanceMetric, int Token) : System.IComparable<BlockDispatchPriority>
    {
        public int CompareTo(BlockDispatchPriority other)
        {
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
