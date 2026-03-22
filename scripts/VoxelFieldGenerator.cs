using Godot;

public sealed class VoxelFieldGenerator
{
    private readonly FastNoiseLite _continentNoise;
    private readonly FastNoiseLite _ridgeNoise;
    private readonly FastNoiseLite _hillNoise;
    private readonly FastNoiseLite _detailNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;
    private readonly FastNoiseLite _biomeNoise;
    private readonly FastNoiseLite _caveNoise;
    private readonly float _terrainHeight;
    private readonly float _detailHeight;
    private readonly float _caveScale;
    private readonly float _caveThreshold;

    public VoxelFieldGenerator(int seed, float terrainHeight, float detailHeight, float caveScale, float caveThreshold)
    {
        _terrainHeight = terrainHeight;
        _detailHeight = detailHeight;
        _caveScale = caveScale;
        _caveThreshold = caveThreshold;

        _continentNoise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0065f
        };

        _ridgeNoise = new FastNoiseLite
        {
            Seed = seed + 37,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 0.014f
        };

        _hillNoise = new FastNoiseLite
        {
            Seed = seed + 59,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.024f
        };

        _detailNoise = new FastNoiseLite
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.09f
        };

        _warpNoiseX = new FastNoiseLite
        {
            Seed = seed + 131,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.011f
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 157,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.011f
        };

        _biomeNoise = new FastNoiseLite
        {
            Seed = seed + 181,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.008f
        };

        _caveNoise = new FastNoiseLite
        {
            Seed = seed + 211,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            Frequency = 0.045f
        };
    }

    public void FillChunk(VoxelChunkData data)
    {
        for (int z = 0; z < data.PointsPerAxis; z++)
        {
            for (int y = 0; y < data.PointsPerAxis; y++)
            {
                for (int x = 0; x < data.PointsPerAxis; x++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float density = SampleDensity(position);
                    data.SetDensity(x, y, z, density);
                    data.SetMaterial(x, y, z, SampleMaterial(position, density));
                }
            }
        }
    }

    public float SampleDensity(Vector3 worldPosition)
    {
        float terrain = SampleTerrainHeight(worldPosition.X, worldPosition.Z);

        float density = terrain - worldPosition.Y;

        // Keep caves well below the surface so the terrain reads as continuous while
        // we validate the voxel/chunk pipeline.
        float depthBelowSurface = terrain - worldPosition.Y;
        if (depthBelowSurface > 4.0f)
        {
            float caveNoise = 1.0f - Mathf.Abs(_caveNoise.GetNoise3D(worldPosition.X, worldPosition.Y, worldPosition.Z));
            if (caveNoise > _caveThreshold)
            {
                float depthFactor = Mathf.Clamp((depthBelowSurface - 4.0f) / 6.0f, 0.0f, 1.0f);
                density -= (caveNoise - _caveThreshold) * _caveScale * depthFactor;
            }
        }

        return density;
    }

    public VoxelMaterialId SampleMaterial(Vector3 worldPosition, float density)
    {
        float terrain = SampleTerrainHeight(worldPosition.X, worldPosition.Z);
        float depthBelowSurface = terrain - worldPosition.Y;
        float biomeSignal = (_biomeNoise.GetNoise2D(worldPosition.X, worldPosition.Z) + 1.0f) * 0.5f;
        float slope = SampleSlope(worldPosition.X, worldPosition.Z);
        float normalizedHeight = Mathf.Clamp((terrain + (_terrainHeight * 0.4f)) / (_terrainHeight * 1.7f), 0.0f, 1.0f);

        if (density < 0.0f)
        {
            return VoxelMaterialId.Soil;
        }

        if (slope > 0.55f)
        {
            return normalizedHeight > 0.68f ? VoxelMaterialId.Cliff : VoxelMaterialId.Rock;
        }

        if (normalizedHeight > 0.82f && slope < 0.4f)
        {
            return VoxelMaterialId.Snow;
        }

        if (depthBelowSurface > 4.8f)
        {
            return VoxelMaterialId.Rock;
        }

        if (depthBelowSurface > 2.3f)
        {
            return slope > 0.3f ? VoxelMaterialId.Rock : VoxelMaterialId.Soil;
        }

        if (depthBelowSurface < 0.9f)
        {
            if (normalizedHeight > 0.74f)
            {
                return slope > 0.22f ? VoxelMaterialId.Rock : VoxelMaterialId.Snow;
            }

            return biomeSignal > 0.42f && slope < 0.24f
                ? VoxelMaterialId.Grass
                : VoxelMaterialId.Soil;
        }

        if (depthBelowSurface < 1.8f)
        {
            if (slope > 0.24f)
            {
                return VoxelMaterialId.Rock;
            }

            return biomeSignal > 0.56f
                ? VoxelMaterialId.Grass
                : VoxelMaterialId.Soil;
        }

        return VoxelMaterialId.Soil;
    }

    private float SampleTerrainHeight(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        float continent = (_continentNoise.GetNoise2D(warped.X, warped.Y) + 1.0f) * 0.5f;
        continent = Mathf.SmoothStep(0.18f, 0.92f, continent);

        float ridge = 1.0f - Mathf.Abs(_ridgeNoise.GetNoise2D(warped.X, warped.Y));
        ridge *= ridge;

        float hills = (_hillNoise.GetNoise2D(warped.X, warped.Y) + 1.0f) * 0.5f;
        float detail = _detailNoise.GetNoise2D(warped.X, warped.Y);

        float terrain = (continent - 0.45f) * (_terrainHeight * 1.25f);
        terrain += ridge * continent * (_terrainHeight * 0.95f);
        terrain += (hills - 0.5f) * (_terrainHeight * 0.32f);
        terrain += detail * _detailHeight * (0.35f + (continent * 0.65f));
        return terrain;
    }

    private float SampleSlope(float worldX, float worldZ)
    {
        const float sampleOffset = 1.75f;
        float heightLeft = SampleTerrainHeight(worldX - sampleOffset, worldZ);
        float heightRight = SampleTerrainHeight(worldX + sampleOffset, worldZ);
        float heightBack = SampleTerrainHeight(worldX, worldZ - sampleOffset);
        float heightForward = SampleTerrainHeight(worldX, worldZ + sampleOffset);

        Vector3 tangentX = new(sampleOffset * 2.0f, heightRight - heightLeft, 0.0f);
        Vector3 tangentZ = new(0.0f, heightForward - heightBack, sampleOffset * 2.0f);
        Vector3 normal = tangentZ.Cross(tangentX).Normalized();
        return 1.0f - Mathf.Clamp(normal.Dot(Vector3.Up), 0.0f, 1.0f);
    }

    private Vector2 WarpXZ(float worldX, float worldZ)
    {
        float warpStrength = 34.0f;
        float warpX = _warpNoiseX.GetNoise2D(worldX, worldZ) * warpStrength;
        float warpZ = _warpNoiseZ.GetNoise2D(worldX, worldZ) * warpStrength;
        return new Vector2(worldX + warpX, worldZ + warpZ);
    }
}
