using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainMesher
{
    private readonly TerrainConfig _config;
    private readonly int _seed;
    private readonly float _terrainHeight;
    private readonly float _detailHeight;
    private readonly float _caveScale;
    private readonly float _caveThreshold;
    private readonly float _waterLevel;
    private readonly float _shorelineFalloff;
    private readonly float _waterBasinInfluence;
    private readonly VoxelFieldGenerator _surfaceSampler;
    private readonly VoxelMeshBuildOptions _meshOptions;

    public TerrainMesher(TerrainConfig config)
    {
        _config = config;
        _seed = config.Seed;
        _terrainHeight = config.TerrainHeight;
        _detailHeight = config.DetailHeight;
        _caveScale = config.CaveScale;
        _caveThreshold = config.CaveThreshold;
        _waterLevel = config.WaterLevel;
        _shorelineFalloff = config.ShorelineFalloff;
        _waterBasinInfluence = config.WaterBasinInfluence;
        _surfaceSampler = CreateFieldGenerator();
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
        // Field builds now run on worker threads, so each job gets its own generator instance
        // instead of sharing mutable noise state across threads.
        CreateFieldGenerator().FillChunk(data);
        return data;
    }

    public VoxelMeshBuildResult BuildMesh(VoxelChunkData data)
    {
        return VoxelMesher.BuildMesh(data, _meshOptions);
    }

    public float SampleSurfaceHeight(float worldX, float worldZ)
    {
        return _surfaceSampler.SampleSurfaceHeight(worldX, worldZ);
    }

    private VoxelFieldGenerator CreateFieldGenerator()
    {
        return new VoxelFieldGenerator(
            _seed,
            _terrainHeight,
            _detailHeight,
            _caveScale,
            _caveThreshold,
            _waterLevel,
            _shorelineFalloff,
            _waterBasinInfluence);
    }
}
