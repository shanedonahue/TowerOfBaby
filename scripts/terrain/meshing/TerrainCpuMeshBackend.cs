using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainCpuMeshBackend : ITerrainMeshBackend
{
    public string BackendName => "cpu_async";

    public VoxelMeshBuildResult BuildMesh(VoxelChunkData data, VoxelMeshBuildOptions options)
    {
        return VoxelMesher.BuildMesh(data, options);
    }
}
