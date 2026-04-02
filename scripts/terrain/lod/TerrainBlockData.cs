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
    public TerrainBlockData(TerrainBlockId id, TerrainRenderer renderer)
    {
        Id = id;
        Renderer = renderer;
    }

    public TerrainBlockId Id { get; }
    public TerrainRenderer Renderer { get; }
    public TerrainBlockState State { get; set; } = TerrainBlockState.Requested;
    public bool Desired { get; set; } = true;
    public int TriangleCount { get; private set; }
    public VoxelChunkData Field { get; private set; }
    public VoxelMeshBuildResult Mesh { get; private set; } = VoxelMeshBuildResult.Empty;

    public void SetField(VoxelChunkData field)
    {
        Field = field;
        State = TerrainBlockState.FieldReady;
    }

    public void SetMesh(VoxelMeshBuildResult mesh)
    {
        Mesh = mesh;
        TriangleCount = mesh.TotalTriangleCount;
        State = TerrainBlockState.MeshReady;
    }

    public void MarkVisible()
    {
        Field = null;
        Mesh = VoxelMeshBuildResult.Empty;
        State = TerrainBlockState.Visible;
    }

    public void MarkReleasable()
    {
        Desired = false;
        State = TerrainBlockState.Releasable;
    }

    public void CancelPendingData()
    {
        Field = null;
        Mesh = VoxelMeshBuildResult.Empty;
    }
}
