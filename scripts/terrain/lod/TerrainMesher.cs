using Godot;
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
        VoxelMeshBuildResult mesh = VoxelMesher.BuildMesh(data, _meshOptions);
        if (!mesh.HasGeometry)
        {
            return mesh;
        }

        return _meshOptions.ColorMode == VoxelMeshColorMode.MaterialTint
            ? CreateSurfaceColorizer().BuildLitMesh(mesh, data)
            : AttachBiomeWeights(mesh, data);
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

    private TerrainSurfaceColorizer CreateSurfaceColorizer()
    {
        return new TerrainSurfaceColorizer(_config);
    }

    private VoxelMeshBuildResult AttachBiomeWeights(VoxelMeshBuildResult mesh, VoxelChunkData data)
    {
        if (mesh.HasBiomeWeights)
        {
            return mesh;
        }

        // Mesh builds run on worker threads, so keep biome sampling thread-local just like the field generator.
        TerrainBiomeClassifier biomeClassifier = new(_seed);
        float[] biomeWeights = new float[mesh.Vertices.Length * 4];
        Vector3 origin = data.Origin;
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Vector3 worldPosition = origin + mesh.Vertices[i];
            TerrainBiomeSample biome = biomeClassifier.SampleWorldPosition(worldPosition.X, worldPosition.Z);
            WriteBiomeWeights(biomeWeights, i * 4, biome);
        }

        return new VoxelMeshBuildResult(
            mesh.Vertices,
            mesh.Normals,
            mesh.Uvs,
            mesh.Colors,
            mesh.MaterialColors,
            biomeWeights,
            mesh.Tangents,
            mesh.NormalDebugMismatchCount,
            mesh.TotalTriangleCount,
            mesh.UsedDetailBrick,
            mesh.UsedPersistentDetailEdits,
            mesh.DetailTriangleCount,
            mesh.ReplacedCoarseCellCount,
            mesh.DetailCellCount);
    }

    private static void WriteBiomeWeights(float[] destination, int offset, TerrainBiomeSample biome)
    {
        destination[offset] = biome.PlainsWeight;
        destination[offset + 1] = biome.RockyWeight;
        destination[offset + 2] = biome.CanyonWeight;
        destination[offset + 3] = biome.SwampWeight;
    }
}
