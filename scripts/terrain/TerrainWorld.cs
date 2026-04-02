using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainWorld : Node3D
{
    [Signal] public delegate void InitialLoadCompletedEventHandler();

    private const string TerrainWorldGroup = "terrain_world";

    // TerrainWorld is now a thin facade around the new LOD block runtime so the
    // rest of the scene can keep the same entry point while visibility authority moves to TerrainLodManager.
    [Export] public NodePath TrackedCharacterPath = new();
    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;
    [Export] public float BaseY = -12.0f;
    [Export] public int Seed = 12345;
    [Export] public float TerrainHeight = 10.0f;
    [Export] public float DetailHeight = 2.8f;
    [Export] public float CaveScale = 9.0f;
    [Export] public float CaveThreshold = 0.63f;

    [ExportGroup("Brush")]
    [Export] public float BrushRadius = 2.4f;
    [Export] public float BrushRadiusMin = 0.8f;
    [Export] public float BrushRadiusMax = 8.0f;
    [Export] public float BrushSurfaceInset = 0.55f;
    [Export] public float BrushBuildSurfaceOffset = 0.3f;
    [Export] public float CarveStrength = -3.4f;
    [Export] public float BuildStrength = 2.8f;
    [Export] public float BrushRetextureMargin = 1.6f;

    private TerrainChunkStore _chunkStore = null!;
    private TerrainLodManager _lodManager = null!;
    private TerrainWorldProfileSnapshot _fallbackProfileSnapshot = null!;

    public bool InitialLoadComplete => _lodManager?.InitialLoadComplete ?? true;

    public override void _Ready()
    {
        AddToGroup(TerrainWorldGroup);
        _lodManager = GetNodeOrNull<TerrainLodManager>("TerrainLodManager");
        if (_lodManager == null)
        {
            GD.PushWarning("TerrainLodManager child missing under TerrainWorld. Terrain will stay inactive.");
            CallDeferred(nameof(EmitInitialLoadCompletedDeferred));
            return;
        }

        _lodManager.InitialLoadCompleted += HandleRuntimeInitialLoadCompleted;
    }

    public float GetInitialLoadProgress()
    {
        return _lodManager?.InitialLoadProgress ?? 1.0f;
    }

    public void ApplyBrush(Vector3 worldCenter, bool additive)
    {
        // Intentionally stubbed for phase one: the new LOD runtime owns visibility, but edits/persistence are still parked.
    }

    public void ApplySlash(VoxelSlashEdit edit)
    {
        // Intentionally stubbed for phase one: slash-driven terrain edits will be reintroduced after the LOD skeleton settles.
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
        GetChunkStore().ClearStartupState();
    }

    public void ClearAllPersistentCache()
    {
        GetChunkStore().ClearAllChunkData();
    }

    public TerrainWorldProfileSnapshot GetProfileSnapshot()
    {
        return _lodManager?.GetProfileSnapshot() ?? (_fallbackProfileSnapshot ??= BuildFallbackProfileSnapshot());
    }

    public string GetDebugStats()
    {
        return _lodManager?.GetDebugSummary() ?? "TerrainLodManager missing under TerrainWorld.";
    }

    private void HandleRuntimeInitialLoadCompleted()
    {
        EmitSignal(SignalName.InitialLoadCompleted);
    }

    private void EmitInitialLoadCompletedDeferred()
    {
        EmitSignal(SignalName.InitialLoadCompleted);
    }

    private TerrainChunkStore GetChunkStore()
    {
        return _chunkStore ??= new TerrainChunkStore(Seed);
    }

    private TerrainWorldProfileSnapshot BuildFallbackProfileSnapshot()
    {
        return new TerrainWorldProfileSnapshot
        {
            TerrainStatsEnabled = false,
            MeshBackendName = "lod_blocks_missing",
            SearchThrottleState = "lod_blocks",
            TrackedBiomeSummary = "Waiting for TerrainLodManager.",
            TrackedDetailSummary = "No field/mesh work is running because the runtime child is missing.",
            TrackedCoverageStateSummary = "Scene should contain TerrainLodManager under TerrainWorld.",
            LastSelectedChunkSummary = "none",
            LastReleasedChunkSummary = "none",
            LastChunkSourceSummary = "none",
            InitialLoadProgress = 1.0f,
            InitialLoadComplete = true
        };
    }
}
