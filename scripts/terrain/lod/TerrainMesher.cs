using Godot;
using System;
using System.Collections.Generic;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainMesher
{
    private const int MaxEditDetailScale = 32;
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

    public VoxelChunkData BuildField(TerrainBlockId blockId, IReadOnlyList<TerrainEditRegion> editRegions = null)
    {
        VoxelChunkData data = new(
            _config.PointsPerAxis,
            TerrainMetrics.GetVoxelSize(_config, blockId.Lod),
            TerrainMetrics.GetBlockOrigin(_config, blockId));
        // Field builds now run on worker threads, so each job gets its own generator instance
        // instead of sharing mutable noise state across threads.
        VoxelFieldGenerator fieldGenerator = CreateFieldGenerator();
        fieldGenerator.FillChunk(data);
        ApplyEditRegions(data, fieldGenerator, editRegions);
        return data;
    }

    public VoxelMeshBuildResult BuildMesh(VoxelChunkData data)
    {
        VoxelMeshBuildResult mesh = VoxelMesher.BuildMesh(data, _meshOptions);
        if (!mesh.HasGeometry)
        {
            return mesh;
        }

        TerrainSurfaceColorizer surfaceColorizer = CreateSurfaceColorizer();
        return surfaceColorizer.BuildLitMesh(mesh, data);
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

    private void ApplyEditRegions(
        VoxelChunkData data,
        VoxelFieldGenerator fieldGenerator,
        IReadOnlyList<TerrainEditRegion> editRegions)
    {
        if (data == null ||
            fieldGenerator == null ||
            editRegions == null ||
            editRegions.Count == 0)
        {
            return;
        }

        bool hasDetailBounds = false;
        Aabb combinedLocalBounds = default;
        int detailScale = 0;
        for (int i = 0; i < editRegions.Count; i++)
        {
            TerrainEditRegion region = editRegions[i];
            if (!region.TryBuildLocalRegion(data.Origin, data.ChunkSize, out TerrainPersistedDetailRegionData localRegion))
            {
                continue;
            }

            combinedLocalBounds = hasDetailBounds
                ? Union(combinedLocalBounds, localRegion.LocalBounds)
                : localRegion.LocalBounds;
            hasDetailBounds = true;
            detailScale = Math.Max(
                detailScale,
                region.ResolveDetailScale(_config.BaseVoxelSize, data.VoxelSize, MaxEditDetailScale));
        }

        if (hasDetailBounds)
        {
            data.EnsureDetailBrick(
                combinedLocalBounds,
                detailScale,
                paddingCoarseCells: 1,
                fieldGenerator.SampleDensity,
                fieldGenerator.SampleMaterial,
                persistentEdits: true,
                preserveExistingCoverage: false);
        }

        for (int i = 0; i < editRegions.Count; i++)
        {
            TerrainEditRegion region = editRegions[i];
            if (region.TryBuildLocalRegion(data.Origin, data.ChunkSize, out TerrainPersistedDetailRegionData localRegion) &&
                data.DetailBrick != null)
            {
                data.UpsertPersistedDetailRegion(localRegion);
            }

            for (int stampIndex = 0; stampIndex < region.Stamps.Count; stampIndex++)
            {
                TerrainEditStampData stamp = region.Stamps[stampIndex];
                stamp.Apply(data, fieldGenerator.SampleMaterial);
                if (data.DetailBrick != null)
                {
                    stamp.Apply(data.DetailBrick.Data, fieldGenerator.SampleMaterial);
                }
            }
        }
    }

    private static Aabb Union(Aabb a, Aabb b)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Min(a.Position.X, b.Position.X),
            Mathf.Min(a.Position.Y, b.Position.Y),
            Mathf.Min(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Max(aEnd.X, bEnd.X),
            Mathf.Max(aEnd.Y, bEnd.Y),
            Mathf.Max(aEnd.Z, bEnd.Z));
        return new Aabb(min, max - min);
    }
}
