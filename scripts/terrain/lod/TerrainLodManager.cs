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

    [ExportGroup("Scheduler")]
    [Export(PropertyHint.Range, "1,16,1")] public int FieldBuildsPerFrame = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int MeshBuildsPerFrame = 4;
    [Export(PropertyHint.Range, "1,16,1")] public int CommitsPerFrame = 4;
    [Export(PropertyHint.Range, "1,32,1")] public int ReleasesPerFrame = 8;
    [Export] public bool GenerateCollisionForCoarseLods;

    private readonly Dictionary<TerrainBlockId, TerrainBlockData> _blocks = new();
    private readonly HashSet<TerrainBlockId> _startupBlocks = new();
    private readonly HashSet<TerrainBlockId> _startupSatisfiedBlocks = new();
    private readonly StringBuilder _debugBuilder = new();

    private TerrainConfig _config = null!;
    private TerrainMesher _mesher = null!;
    private TerrainWorldProfileSnapshot _latestProfileSnapshot = null!;
    private TerrainWorld _terrainWorld = null!;
    private Node3D _trackedCharacter = null!;
    private TerrainBlockId _currentCenterParent;
    private Vector3 _lastViewerPosition;
    private string _lastSelectionSummary = "waiting_for_viewer";
    private string _lastReleaseSummary = "none";
    private string _lastCommitSummary = "none";
    private bool _selectionInitialized;
    private bool _initialLoadComplete;
    private int _lastFieldBuildCount;
    private int _lastMeshBuildCount;
    private int _lastCommitCount;
    private int _lastReleaseCount;

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
        _trackedCharacter ??= ResolveTrackedCharacter();
        if (_trackedCharacter == null)
        {
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
        TerrainBlockId centerParent = ComputeCenterParent(viewerPosition);
        if (_selectionInitialized && centerParent.Equals(_currentCenterParent))
        {
            return;
        }

        _currentCenterParent = centerParent;
        HashSet<TerrainBlockId> desired = BuildDesiredSet(centerParent);
        if (!_selectionInitialized)
        {
            _startupBlocks.Clear();
            _startupSatisfiedBlocks.Clear();
            foreach (TerrainBlockId blockId in desired)
            {
                _startupBlocks.Add(blockId);
            }
            _selectionInitialized = true;
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
                    block.State = TerrainBlockState.Visible;
                }
                continue;
            }

            if (block.State == TerrainBlockState.Visible)
            {
                block.MarkReleasable();
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

        _lastSelectionSummary = BuildSelectionSummary(centerParent, desired.Count);
    }

    private TerrainBlockId ComputeCenterParent(Vector3 viewerPosition)
    {
        float fineSpan = TerrainMetrics.GetBlockSpan(_config, 0);
        float surfaceY = _mesher.SampleSurfaceHeight(viewerPosition.X, viewerPosition.Z);
        float anchorY = Mathf.Max(_config.BaseY, surfaceY - (fineSpan * 0.5f));
        Vector3 anchor = new(viewerPosition.X, anchorY, viewerPosition.Z);
        return TerrainMetrics.GetBlockForWorldPosition(_config, 1, anchor);
    }

    private HashSet<TerrainBlockId> BuildDesiredSet(TerrainBlockId centerParent)
    {
        HashSet<TerrainBlockId> desired = new();

        // Current policy: keep a tiny LOD1 ring and refine only the viewer's parent block into its eight LOD0 children.
        for (int z = -_config.CoarseRadiusXZ; z <= _config.CoarseRadiusXZ; z++)
        {
            for (int y = -_config.VerticalRadius; y <= _config.VerticalRadius; y++)
            {
                for (int x = -_config.CoarseRadiusXZ; x <= _config.CoarseRadiusXZ; x++)
                {
                    TerrainBlockId coarseBlock = new(
                        1,
                        centerParent.Index + new Vector3I(x, y, z));
                    if (coarseBlock.Equals(centerParent))
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
        _lastReleaseSummary = $"{blockId} {reason}";
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
            $"lod0 span {TerrainMetrics.GetBlockSpan(_config, 0):0.0}  lod1 span {TerrainMetrics.GetBlockSpan(_config, 1):0.0}  center {_currentCenterParent}";

        return new TerrainWorldProfileSnapshot
        {
            TerrainStatsEnabled = false,
            ActiveChunkCount = visible,
            ResidentChunkCount = _blocks.Count,
            LoadedChunkCount = fieldReady + meshReady + visible + releasable,
            DesiredChunkCount = _blocks.Count - releasable,
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
            MeshBackendName = "lod_blocks_v0",
            SearchThrottleState = "lod_blocks",
            TrackedBiomeSummary = viewerSummary,
            TrackedDetailSummary = BuildLifecycleSummary(),
            TrackedCoverageStateSummary = lodSummary,
            LastSelectedChunkSummary = _lastSelectionSummary,
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

        return $"requested {requested}  field {fieldReady}  mesh {meshReady}  visible {visible}  releasable {releasable}";
    }

    private string BuildSelectionSummary(TerrainBlockId centerParent, int desiredCount)
    {
        return
            $"center {centerParent}  ring {_config.CoarseRadiusXZ}  desired {desiredCount}  " +
            "policy center lod1 block -> 8 lod0 children, outer ring stays lod1.";
    }
}
