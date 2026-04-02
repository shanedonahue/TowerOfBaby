using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainMesher
{
    private readonly TerrainConfig _config;
    private readonly VoxelFieldGenerator _fieldGenerator;
    private readonly VoxelMeshBuildOptions _meshOptions;

    public TerrainMesher(TerrainConfig config)
    {
        _config = config;
        _fieldGenerator = new VoxelFieldGenerator(
            config.Seed,
            config.TerrainHeight,
            config.DetailHeight,
            config.CaveScale,
            config.CaveThreshold);
        _meshOptions = new VoxelMeshBuildOptions(
            config.GenerateTangents,
            config.MeshColorMode);
    }

    public VoxelChunkData BuildField(TerrainBlockId blockId)
    {
        VoxelChunkData data = new(
            _config.PointsPerAxis,
            TerrainMetrics.GetVoxelSize(_config, blockId.Lod),
            TerrainMetrics.GetBlockOrigin(_config, blockId));
        _fieldGenerator.FillChunk(data);
        return data;
    }

    public VoxelMeshBuildResult BuildMesh(VoxelChunkData data)
    {
        return VoxelMesher.BuildMesh(data, _meshOptions);
    }

    public float SampleSurfaceHeight(float worldX, float worldZ)
    {
        return _fieldGenerator.SampleSurfaceHeight(worldX, worldZ);
    }
}
