using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public enum TerrainBlockState
{
    Requested = 0,
    FieldReady = 1,
    MeshReady = 2,
    Visible = 3,
    Releasable = 4
}

public sealed class TerrainBlockData
{
    public TerrainBlockData(TerrainBlockId id, TerrainRenderer renderer, long instanceVersion)
    {
        Id = id;
        Renderer = renderer;
        InstanceVersion = instanceVersion;
    }

    public TerrainBlockId Id { get; }
    public TerrainRenderer Renderer { get; }
    public long InstanceVersion { get; }
    public TerrainBlockState State { get; set; } = TerrainBlockState.Requested;
    public bool Desired { get; set; } = true;
    public int TriangleCount { get; private set; }
    public VoxelChunkData Field { get; private set; }
    public VoxelMeshBuildResult Mesh { get; private set; } = VoxelMeshBuildResult.Empty;
    public TerrainSeamBuildResult SeamBuild { get; private set; } = TerrainSeamBuildResult.None;
    public double ReleaseEligibleAtSeconds { get; private set; }
    public int FieldBuildRevision { get; private set; }
    public bool FieldBuildRunning { get; private set; }
    public int MeshBuildRevision { get; private set; }
    public bool MeshBuildRunning { get; private set; }
    public bool CollisionPending { get; private set; }

    public int BeginFieldBuild()
    {
        FieldBuildRunning = true;
        return ++FieldBuildRevision;
    }

    public bool MatchesFieldBuild(long instanceVersion, int revision)
    {
        return InstanceVersion == instanceVersion && FieldBuildRevision == revision;
    }

    public void SetField(VoxelChunkData field)
    {
        FieldBuildRunning = false;
        Field = field;
        State = TerrainBlockState.FieldReady;
    }

    public void ClearFieldBuildRunning(int revision)
    {
        if (FieldBuildRevision == revision)
        {
            FieldBuildRunning = false;
        }
    }

    public int BeginMeshBuild()
    {
        MeshBuildRunning = true;
        return ++MeshBuildRevision;
    }

    public bool MatchesMeshBuild(long instanceVersion, int revision)
    {
        return InstanceVersion == instanceVersion && MeshBuildRevision == revision;
    }

    public void SetMesh(VoxelMeshBuildResult mesh)
    {
        MeshBuildRunning = false;
        Mesh = mesh;
        TriangleCount = mesh.TotalTriangleCount;
        State = TerrainBlockState.MeshReady;
    }

    public void SetSeamBuild(TerrainSeamBuildResult seamBuild)
    {
        SeamBuild = seamBuild;
    }

    public void ClearMeshBuildRunning(int revision)
    {
        if (MeshBuildRevision == revision)
        {
            MeshBuildRunning = false;
        }
    }

    public void MarkVisible(bool collisionPending = false)
    {
        Field = null;
        Mesh = VoxelMeshBuildResult.Empty;
        Desired = true;
        ReleaseEligibleAtSeconds = 0.0;
        CollisionPending = collisionPending;
        State = TerrainBlockState.Visible;
    }

    public void RestoreVisibility()
    {
        Desired = true;
        ReleaseEligibleAtSeconds = 0.0;
        CollisionPending = false;
        State = TerrainBlockState.Visible;
    }

    public void MarkReleasable(double releaseEligibleAtSeconds)
    {
        Desired = false;
        ReleaseEligibleAtSeconds = releaseEligibleAtSeconds;
        CollisionPending = false;
        State = TerrainBlockState.Releasable;
    }

    public void MarkCollisionPending()
    {
        CollisionPending = true;
    }

    public void MarkCollisionReady()
    {
        CollisionPending = false;
    }

    public bool IsHeldForRelease(double nowSeconds)
    {
        return State == TerrainBlockState.Releasable && nowSeconds < ReleaseEligibleAtSeconds;
    }

    public void CancelPendingData()
    {
        Field = null;
        Mesh = VoxelMeshBuildResult.Empty;
        SeamBuild = TerrainSeamBuildResult.None;
        FieldBuildRunning = false;
        MeshBuildRunning = false;
        CollisionPending = false;
    }
}
