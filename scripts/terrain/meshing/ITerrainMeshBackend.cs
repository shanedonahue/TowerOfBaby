using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal interface ITerrainMeshBackend
{
    string BackendName { get; }

    VoxelMeshBuildResult BuildMesh(VoxelChunkData data, VoxelMeshBuildOptions options);
}
