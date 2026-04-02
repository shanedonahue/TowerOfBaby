using Godot;
using System.Collections.Generic;
using System.Text;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainLodManager : Node3D
{
    [Signal] public delegate void InitialLoadCompletedEventHandler();

    [ExportGroup("LOD Policy")]
    [Export(PropertyHint.Range, "1,4,1")] public int CoarseRadiusXZ = 1;
    [Export(PropertyHint.Range, "0,2,1")] public int VerticalRadius;

    [ExportGroup("Refinement Stability")]
    [Export(PropertyHint.Range, "0.00,0.49,0.01")] public float RefinedParentOverlapFraction = 0.18f;
    [Export(PropertyHint.Range, "0.00,0.49,0.01")] public float CenterSwitchHysteresisFraction = 0.20f;
    [Export(PropertyHint.Range, "0.00,2.00,0.01")] public float BlockReleaseHysteresisSeconds = 0.40f;

    [ExportGroup("Scheduler")]
    [Export(PropertyHint.Range, "1,16,1")] public int FieldBuildsPerFrame = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int MeshBuildsPerFrame = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int CommitsPerFrame = 4;
    [Export(PropertyHint.Range, "1,32,1")] public int ReleasesPerFrame = 8;
    [Export] public bool GenerateCollisionForCoarseLods;

    private readonly Dictionary<TerrainBlockId, TerrainBlockData> _blocks = new();
    private readonly HashSet<TerrainBlockId> _refinedParents = new();
    private readonly HashSet<TerrainBlockId> _startupBlocks = new();
    private readonly HashSet<TerrainBlockId> _startupSatisfiedBlocks = new();
    private readonly StringBuilder _debugBuilder = new();
    private readonly Queue<double> _recentCreationTimes = new();
    private readonly Queue<double> _recentReleaseTimes = new();

    private TerrainConfig _config = null!;
    private TerrainMesher _mesher = null!;
    private TerrainWorldProfileSnapshot _latestProfileSnapshot = null!;
    private TerrainWorld _terrainWorld = null!;
    private Node3D _trackedCharacter = null!;
    private TerrainBlockId _currentCenterParent;
    private TerrainBlockId _currentViewerParent;
    private Vector3 _lastViewerPosition;
    private double _currentTimeSeconds;
    private string _lastSelectionSummary = "waiting_for_viewer";
    private string _lastRefinementHandoffSummary = "none";
    private string _lastReleaseSummary = "none";
    private string _lastCommitSummary = "none";
    private bool _selectionInitialized;
    private bool _initialLoadComplete;
    private int _lastDesiredBlockCount;
    private int _hysteresisRetainedBlockCount;
    private int _lastFieldBuildCount;
    private int _lastMeshBuildCount;
    private int _lastCommitCount;
    private int _lastReleaseCount;
    private double _blockCreateRatePerSecond;
    private double _blockReleaseRatePerSecond;
    private long _refinementHandoffCount;

    public bool InitialLoadComplete => _initialLoadComplete;
    public float InitialLoadProgress { get; private set; }

    public override void _Ready()
    {
        _terrainWorld = GetParent() as TerrainWorld;
        _config = BuildConfig();
        _mesher = new TerrainMesher(_config);
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

        _lastFieldBuildCount = 0;
        _lastMeshBuildCount = 0;
        _lastCommitCount = 0;
        _lastReleaseCount = 0;

        _lastViewerPosition = _trackedCharacter.GlobalTransform.Origin;
        UpdateDesiredBlocks(_lastViewerPosition);
        ProcessFieldBuilds();
        ProcessMeshBuilds();
        ProcessCommits();
        ProcessReleases();
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
        _debugBuilder.AppendLine(_lastSelectionSummary);
        _debugBuilder.AppendLine($"Lifecycle {BuildLifecycleSummary()}");
        _debugBuilder.AppendLine($"Handoff {_lastRefinementHandoffSummary}");
        _debugBuilder.Append($"Latest {(_lastCommitSummary == string.Empty ? "none" : _lastCommitSummary)}");
        return _debugBuilder.ToString();
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
                CoarseRadiusXZ = Mathf.Max(1, CoarseRadiusXZ),
                VerticalRadius = Mathf.Max(0, VerticalRadius),
                FieldBuildsPerFrame = Mathf.Max(1, FieldBuildsPerFrame),
                MeshBuildsPerFrame = Mathf.Max(1, MeshBuildsPerFrame),
                CommitsPerFrame = Mathf.Max(1, CommitsPerFrame),
                ReleasesPerFrame = Mathf.Max(1, ReleasesPerFrame),
                GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods
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
            CoarseRadiusXZ = Mathf.Max(1, CoarseRadiusXZ),
            VerticalRadius = Mathf.Max(0, VerticalRadius),
            FieldBuildsPerFrame = Mathf.Max(1, FieldBuildsPerFrame),
            MeshBuildsPerFrame = Mathf.Max(1, MeshBuildsPerFrame),
            CommitsPerFrame = Mathf.Max(1, CommitsPerFrame),
            ReleasesPerFrame = Mathf.Max(1, ReleasesPerFrame),
            GenerateCollisionForCoarseLods = GenerateCollisionForCoarseLods
        };
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
        TerrainBlockId centerParent = ResolveSelectionCenterParent(viewerPosition, viewerParent);
        HashSet<TerrainBlockId> refinedParents = BuildRefinedParents(viewerPosition, centerParent);
        HashSet<TerrainBlockId> desired = BuildDesiredSet(centerParent, refinedParents);

        _currentViewerParent = viewerParent;
        _currentCenterParent = centerParent;
        _lastDesiredBlockCount = desired.Count;
        if (!_selectionInitialized)
        {
            _startupBlocks.Clear();
            _startupSatisfiedBlocks.Clear();
            foreach (TerrainBlockId blockId in desired)
            {
                _startupBlocks.Add(blockId);
            }
            _refinedParents.Clear();
            foreach (TerrainBlockId parent in refinedParents)
            {
                _refinedParents.Add(parent);
            }
            _selectionInitialized = true;
        }
        else if (!_refinedParents.SetEquals(refinedParents))
        {
            RecordRefinementHandoff(refinedParents);
            _refinedParents.Clear();
            foreach (TerrainBlockId parent in refinedParents)
            {
                _refinedParents.Add(parent);
            }
        }

        List<TerrainBlockId> releaseNow = new();
        foreach (KeyValuePair<TerrainBlockId, TerrainBlockData> entry in _blocks)
        {
            TerrainBlockData block = entry.Value;
            if (desired.Contains(block.Id))
            {
                block.Desired = true;
                if (block.State == TerrainBlockState.Releasable)
                {
                    block.RestoreVisibility();
                }
                continue;
            }

            if (block.State == TerrainBlockState.Visible)
            {
                // Hold outgoing visuals briefly so the incoming side can become ready without immediate free/recreate churn.
                block.MarkReleasable(_currentTimeSeconds + Mathf.Max(0.0f, BlockReleaseHysteresisSeconds));
            }
            else if (block.State == TerrainBlockState.Releasable)
            {
                block.Desired = false;
            }
            else
            {
                releaseNow.Add(block.Id);
            }
        }

        foreach (TerrainBlockId blockId in releaseNow)
        {
            ReleaseBlock(blockId, "dropped_before_visible");
        }

        foreach (TerrainBlockId blockId in desired)
        {
            if (_blocks.ContainsKey(blockId))
            {
                continue;
            }

            CreateBlock(blockId);
        }

        _hysteresisRetainedBlockCount = CountHysteresisRetainedBlocks();
        _lastSelectionSummary = BuildSelectionSummary(
            centerParent,
            viewerParent,
            desired.Count,
            refinedParents,
            _hysteresisRetainedBlockCount);
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

        float parentSpan = TerrainMetrics.GetBlockSpan(_config, 1);
        float hysteresis = Mathf.Clamp(CenterSwitchHysteresisFraction, 0.0f, 0.49f) * parentSpan;
        Vector3 origin = TerrainMetrics.GetBlockOrigin(_config, _currentCenterParent);
        float maxX = origin.X + parentSpan;
        float maxZ = origin.Z + parentSpan;
        // Let the coarse-ring center trail the raw viewer parent slightly so parent-border crossings do not
        // swap the refined island on the exact boundary.
        bool outsideStableBounds =
            viewerPosition.X < (origin.X - hysteresis) ||
            viewerPosition.X > (maxX + hysteresis) ||
            viewerPosition.Z < (origin.Z - hysteresis) ||
            viewerPosition.Z > (maxZ + hysteresis);
        return outsideStableBounds ? viewerParent : _currentCenterParent;
    }

    private HashSet<TerrainBlockId> BuildRefinedParents(Vector3 viewerPosition, TerrainBlockId centerParent)
    {
        HashSet<TerrainBlockId> refinedParents = new() { centerParent };
        float parentSpan = TerrainMetrics.GetBlockSpan(_config, centerParent.Lod);
        float overlap = Mathf.Clamp(RefinedParentOverlapFraction, 0.0f, 0.49f) * parentSpan;
        if (overlap <= 0.0f)
        {
            return refinedParents;
        }

        Vector3 origin = TerrainMetrics.GetBlockOrigin(_config, centerParent);
        float maxX = origin.X + parentSpan;
        float maxZ = origin.Z + parentSpan;
        bool nearNegX = viewerPosition.X <= (origin.X + overlap);
        bool nearPosX = viewerPosition.X >= (maxX - overlap);
        bool nearNegZ = viewerPosition.Z <= (origin.Z + overlap);
        bool nearPosZ = viewerPosition.Z >= (maxZ - overlap);

        if (nearNegX)
        {
            AddRefinedParent(refinedParents, centerParent, -1, 0);
        }

        if (nearPosX)
        {
            AddRefinedParent(refinedParents, centerParent, 1, 0);
        }

        if (nearNegZ)
        {
            AddRefinedParent(refinedParents, centerParent, 0, -1);
        }

        if (nearPosZ)
        {
            AddRefinedParent(refinedParents, centerParent, 0, 1);
        }

        if (nearNegX && nearNegZ)
        {
            AddRefinedParent(refinedParents, centerParent, -1, -1);
        }

        if (nearNegX && nearPosZ)
        {
            AddRefinedParent(refinedParents, centerParent, -1, 1);
        }

        if (nearPosX && nearNegZ)
        {
            AddRefinedParent(refinedParents, centerParent, 1, -1);
        }

        if (nearPosX && nearPosZ)
        {
            AddRefinedParent(refinedParents, centerParent, 1, 1);
        }

        return refinedParents;
    }

    private static void AddRefinedParent(HashSet<TerrainBlockId> refinedParents, TerrainBlockId centerParent, int xOffset, int zOffset)
    {
        refinedParents.Add(new TerrainBlockId(
            centerParent.Lod,
            centerParent.Index + new Vector3I(xOffset, 0, zOffset)));
    }

    private HashSet<TerrainBlockId> BuildDesiredSet(TerrainBlockId centerParent, IReadOnlySet<TerrainBlockId> refinedParents)
    {
        HashSet<TerrainBlockId> desired = new();

        // Keep a small stable LOD1 ring around the viewer, then overlap neighboring parents near borders so
        // refinement hands off gradually instead of flipping from one parent block to the next in a single frame.
        for (int z = -_config.CoarseRadiusXZ; z <= _config.CoarseRadiusXZ; z++)
        {
            for (int y = -_config.VerticalRadius; y <= _config.VerticalRadius; y++)
            {
                for (int x = -_config.CoarseRadiusXZ; x <= _config.CoarseRadiusXZ; x++)
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

    private void CreateBlock(TerrainBlockId blockId)
    {
        TerrainRenderer renderer = new();
        renderer.Initialize(blockId, TerrainMetrics.GetBlockOrigin(_config, blockId));
        AddChild(renderer);
        _blocks[blockId] = new TerrainBlockData(blockId, renderer);
        _recentCreationTimes.Enqueue(_currentTimeSeconds);
    }

    private void ProcessFieldBuilds()
    {
        foreach (TerrainBlockData block in GetOrderedBlocks(TerrainBlockState.Requested))
        {
            if (_lastFieldBuildCount >= _config.FieldBuildsPerFrame)
            {
                break;
            }

            block.SetField(_mesher.BuildField(block.Id));
            _lastFieldBuildCount++;
        }
    }

    private void ProcessMeshBuilds()
    {
        foreach (TerrainBlockData block in GetOrderedBlocks(TerrainBlockState.FieldReady))
        {
            if (_lastMeshBuildCount >= _config.MeshBuildsPerFrame)
            {
                break;
            }

            block.SetMesh(_mesher.BuildMesh(block.Field));
            _lastMeshBuildCount++;
        }
    }

    private void ProcessCommits()
    {
        foreach (TerrainBlockData block in GetOrderedBlocks(TerrainBlockState.MeshReady))
        {
            if (_lastCommitCount >= _config.CommitsPerFrame)
            {
                break;
            }

            bool includeCollision = block.Id.Lod == 0 || _config.GenerateCollisionForCoarseLods;
            block.Renderer.ApplyMesh(block.Mesh, includeCollision);
            block.MarkVisible();
            _lastCommitCount++;
            _lastCommitSummary = $"{block.Id} tri {block.TriangleCount} {(includeCollision ? "collision" : "visual_only")}";
            if (_startupBlocks.Contains(block.Id))
            {
                _startupSatisfiedBlocks.Add(block.Id);
            }
        }
    }

    private void ProcessReleases()
    {
        foreach (TerrainBlockData block in GetOrderedBlocks(TerrainBlockState.Releasable, farthestFirst: true))
        {
            if (_lastReleaseCount >= _config.ReleasesPerFrame)
            {
                break;
            }

            if (block.IsHeldForRelease(_currentTimeSeconds))
            {
                continue;
            }

            ReleaseBlock(block.Id, "fell_outside_desired_set");
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
        _lastReleaseCount++;
        _recentReleaseTimes.Enqueue(_currentTimeSeconds);
        _lastReleaseSummary = $"{blockId} {reason}";
    }

    private void RecordRefinementHandoff(IReadOnlySet<TerrainBlockId> nextRefinedParents)
    {
        List<TerrainBlockId> added = new();
        foreach (TerrainBlockId parent in nextRefinedParents)
        {
            if (!_refinedParents.Contains(parent))
            {
                added.Add(parent);
            }
        }

        List<TerrainBlockId> removed = new();
        foreach (TerrainBlockId parent in _refinedParents)
        {
            if (!nextRefinedParents.Contains(parent))
            {
                removed.Add(parent);
            }
        }

        _refinementHandoffCount++;
        _lastRefinementHandoffSummary =
            $"handoff {_refinementHandoffCount} center {_currentCenterParent} -> raw {_currentViewerParent} " +
            $"+{BuildBlockListSummary(added)} -{BuildBlockListSummary(removed)}";
    }

    private void RefreshLifecycleRates()
    {
        PruneEventQueue(_recentCreationTimes);
        PruneEventQueue(_recentReleaseTimes);
        _blockCreateRatePerSecond = _recentCreationTimes.Count;
        _blockReleaseRatePerSecond = _recentReleaseTimes.Count;
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

    private List<TerrainBlockData> GetOrderedBlocks(TerrainBlockState state, bool farthestFirst = false)
    {
        List<TerrainBlockData> ordered = new();
        foreach (TerrainBlockData block in _blocks.Values)
        {
            if (block.State != state)
            {
                continue;
            }

            if (!block.Desired && state != TerrainBlockState.Releasable)
            {
                continue;
            }

            ordered.Add(block);
        }

        ordered.Sort((a, b) => CompareBlockPriority(a, b, farthestFirst));
        return ordered;
    }

    private int CompareBlockPriority(TerrainBlockData a, TerrainBlockData b, bool farthestFirst)
    {
        if (a.Id.Lod != b.Id.Lod)
        {
            return a.Id.Lod.CompareTo(b.Id.Lod);
        }

        float aDistance = TerrainMetrics.DistanceSquaredToBlock(_config, a.Id, _lastViewerPosition);
        float bDistance = TerrainMetrics.DistanceSquaredToBlock(_config, b.Id, _lastViewerPosition);
        int distanceCompare = aDistance.CompareTo(bDistance);
        if (distanceCompare != 0)
        {
            return farthestFirst ? -distanceCompare : distanceCompare;
        }

        return CompareBlockIds(a.Id, b.Id);
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
            : $"viewer {_lastViewerPosition.X:0.0},{_lastViewerPosition.Y:0.0},{_lastViewerPosition.Z:0.0}";
        string lodSummary =
            $"lod0 span {TerrainMetrics.GetBlockSpan(_config, 0):0.0}  lod1 span {TerrainMetrics.GetBlockSpan(_config, 1):0.0}  " +
            $"raw {_currentViewerParent}  center {_currentCenterParent}  overlap {RefinedParentOverlapFraction:0.00}  switch {CenterSwitchHysteresisFraction:0.00}";

        return new TerrainWorldProfileSnapshot
        {
            TerrainStatsEnabled = false,
            ActiveChunkCount = visible,
            ResidentChunkCount = _blocks.Count,
            LoadedChunkCount = fieldReady + meshReady + visible + releasable,
            DesiredChunkCount = _lastDesiredBlockCount,
            PendingLoadCount = requested,
            PreparedChunkCount = fieldReady,
            PendingMeshBuildCount = fieldReady,
            PendingMeshCommitCount = meshReady,
            ToReleaseCount = releasable,
            LastChunkLoadCount = _lastFieldBuildCount,
            LastMeshWorkerBuildCount = _lastMeshBuildCount,
            LastChunkActivationCount = _lastCommitCount,
            LastVisualRebuildCount = _lastCommitCount,
            LastChunkReleaseCount = _lastReleaseCount,
            MeshBackendName = "lod_blocks_v1",
            SearchThrottleState = "lod_blocks",
            TrackedBiomeSummary = viewerSummary,
            TrackedDetailSummary = BuildLifecycleSummary(),
            TrackedCoverageStateSummary = lodSummary,
            RefinedParentCount = _refinedParents.Count,
            HysteresisRetainedBlockCount = _hysteresisRetainedBlockCount,
            BlockCreateRatePerSecond = _blockCreateRatePerSecond,
            BlockReleaseRatePerSecond = _blockReleaseRatePerSecond,
            RefinementHandoffCount = _refinementHandoffCount,
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
            $"hold {_hysteresisRetainedBlockCount}  create/s {_blockCreateRatePerSecond:0.0}  release/s {_blockReleaseRatePerSecond:0.0}";
    }

    private string BuildSelectionSummary(
        TerrainBlockId centerParent,
        TerrainBlockId viewerParent,
        int desiredCount,
        IReadOnlyCollection<TerrainBlockId> refinedParents,
        int retainedBlocks)
    {
        return
            $"raw {viewerParent}  center {centerParent}  ring {_config.CoarseRadiusXZ}  desired {desiredCount}  " +
            $"refined {refinedParents.Count}:{BuildBlockListSummary(refinedParents)}  held {retainedBlocks}  " +
            "policy stable-center + border-overlap + release-hold.";
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
}
