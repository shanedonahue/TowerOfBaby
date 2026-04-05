using Godot;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct TerrainMountainRangeDebugSample(
    float BeltMask,
    float SystemMask,
    float ShoulderMask,
    float PeakMask);

public readonly record struct TerrainWaterDebugSample(
    float SurfaceHeight,
    float ShoreMask,
    float WaterMask,
    float BasinMask);

public sealed class VoxelFieldGenerator
{
    private const float TerrainWarpFrequency = 0.0100f;
    private const float TerrainWarpStrength = 50.0f;
    private const float ContinentHeightScale = 0.80f;
    private const float ContinentBaseOffsetScale = 0.55f;
    private const float MountainHeightScale = 0.90f;
    private const float HillHeightScale = 0.32f;
    private const float DetailContributionScale = 0.90f;
    private const float SwampVegetationBias = 0.60f;
    private const float WaterShorelineFadeMultiplier = 1.85f;
    private const float WaterSubmergedFadeMultiplier = 1.75f;
    private const float WaterBasinThresholdMin = 0.56f;
    private const float WaterBasinThresholdMax = 0.82f;
    private const float WaterShelfBlendScale = 0.30f;
    private const float WaterShelfHeightScale = 0.12f;
    private const float WaterBasinBlendScale = 0.40f;
    private const float WaterBasinDepthScale = 0.56f;
    private const float WaterSwampFlattenScale = 0.40f;
    private const float WaterSwampNearWaterOffsetScale = 0.08f;
    private readonly FastNoiseLite _continentNoise;
    private readonly FastNoiseLite _shapeBiomeNoise;
    private readonly FastNoiseLite _mountainNoise;
    private readonly FastNoiseLite _hillNoise;
    private readonly FastNoiseLite _detailNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;
    private readonly FastNoiseLite _waterBasinNoise;
    private readonly TerrainBiomeClassifier _biomeClassifier;
    private readonly FastNoiseLite _caveNoise;
    private readonly float _terrainHeight;
    private readonly float _detailHeight;
    private readonly float _caveScale;
    private readonly float _waterLevel;
    private readonly float _shorelineFalloff;
    private readonly float _waterBasinInfluence;

    public VoxelFieldGenerator(
        int seed,
        float terrainHeight,
        float detailHeight,
        float caveScale,
        float caveThreshold,
        float waterLevel,
        float shorelineFalloff,
        float waterBasinInfluence)
    {
        _terrainHeight = terrainHeight;
        _detailHeight = detailHeight;
        _caveScale = caveScale;
        _waterLevel = waterLevel;
        _shorelineFalloff = Mathf.Max(0.4f, shorelineFalloff);
        _waterBasinInfluence = Mathf.Clamp(waterBasinInfluence, 0.0f, 1.0f);

        _continentNoise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0010f
        };

        _shapeBiomeNoise = new FastNoiseLite
        {
            Seed = seed + 19,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0009f
        };

        _mountainNoise = new FastNoiseLite
        {
            Seed = seed + 37,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 0.0100f
        };

        _hillNoise = new FastNoiseLite
        {
            Seed = seed + 59,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0500f
        };

        _detailNoise = new FastNoiseLite
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.2000f
        };

        _warpNoiseX = new FastNoiseLite
        {
            Seed = seed + 131,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = TerrainWarpFrequency
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 157,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = TerrainWarpFrequency
        };

        _waterBasinNoise = new FastNoiseLite
        {
            Seed = seed + 223,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00135f
        };

        _biomeClassifier = new TerrainBiomeClassifier(seed);

        _caveNoise = new FastNoiseLite
        {
            Seed = seed + 251,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            Frequency = 0.045f
        };
    }

    public void FillChunk(VoxelChunkData data)
    {
        for (int z = 0; z < data.PointsPerAxis; z++)
        {
            for (int x = 0; x < data.PointsPerAxis; x++)
            {
                Vector3 columnPosition = data.GetPointPosition(x, 0, z);
                SampleTerrainSurface(columnPosition.X, columnPosition.Z, out float terrain, out TerrainBiomeSample biome);
                float slope = SampleSlope(columnPosition.X, columnPosition.Z);

                for (int y = 0; y < data.PointsPerAxis; y++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float density = SampleDensity(position, terrain);
                    data.SetDensity(x, y, z, density);
                    data.SetMaterial(x, y, z, SampleMaterial(position, density, terrain, slope, biome));
                }
            }
        }
    }

    public float SampleDensity(Vector3 worldPosition)
    {
        return SampleDensity(worldPosition, SampleTerrainHeight(worldPosition.X, worldPosition.Z));
    }

    public float SampleSurfaceHeight(float worldX, float worldZ)
    {
        return SampleTerrainHeight(worldX, worldZ);
    }

    public VoxelMaterialId SampleMaterial(Vector3 worldPosition, float density)
    {
        if (density < 0.0f)
        {
            return VoxelMaterialId.Soil;
        }

        SampleTerrainSurface(worldPosition.X, worldPosition.Z, out float terrain, out TerrainBiomeSample biome);
        float slope = SampleSlope(worldPosition.X, worldPosition.Z);
        return SampleMaterial(worldPosition, density, terrain, slope, biome);
    }

    public TerrainBiomeSample SampleBiome(float worldX, float worldZ)
    {
        return _biomeClassifier.SampleWorldPosition(worldX, worldZ);
    }

    public float SampleSurfaceSlope(float worldX, float worldZ)
    {
        return SampleSlope(worldX, worldZ);
    }

    public TerrainMountainRangeDebugSample SampleMountainRangeDebug(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        float landPresence = Mathf.SmoothStep(0.18f, 0.72f, layers.Continent);
        float shoulderMask = Mathf.Clamp(landPresence * Mathf.SmoothStep(0.12f, 0.55f, layers.Mountain), 0.0f, 1.0f);
        float peakMask = Mathf.Clamp(shoulderMask * layers.Mountain * layers.MountainStrength, 0.0f, 1.0f);
        return new TerrainMountainRangeDebugSample(
            landPresence,
            layers.ShapeBiome,
            shoulderMask,
            peakMask);
    }

    public TerrainWaterDebugSample SampleWaterDebug(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        float terrain = BuildTerrainHeight(layers, biome);
        float basinMask = layers.BasinMask * layers.LowlandMask * _waterBasinInfluence;
        return new TerrainWaterDebugSample(
            terrain,
            ComputeShoreMask(terrain),
            ComputeWaterMask(terrain),
            basinMask);
    }

    private float SampleDensity(Vector3 worldPosition, float terrain)
    {
        float density = terrain - worldPosition.Y;
        density += SampleCaveContribution(worldPosition);
        return density;
    }

    private VoxelMaterialId SampleMaterial(
        Vector3 worldPosition,
        float density,
        float terrain,
        float slope,
        TerrainBiomeSample biome)
    {
        if (density < 0.0f)
        {
            return VoxelMaterialId.Soil;
        }

        float depthBelowSurface = terrain - worldPosition.Y;
        float vegetationSignal = biome.PlainsWeight + (biome.SwampWeight * SwampVegetationBias);
        float normalizedHeight = Mathf.Clamp((terrain + (_terrainHeight * 0.4f)) / (_terrainHeight * 1.7f), 0.0f, 1.0f);

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

            return vegetationSignal > 0.42f && slope < 0.24f
                ? VoxelMaterialId.Grass
                : VoxelMaterialId.Soil;
        }

        if (depthBelowSurface < 1.8f)
        {
            if (slope > 0.24f)
            {
                return VoxelMaterialId.Rock;
            }

            return vegetationSignal > 0.56f
                ? VoxelMaterialId.Grass
                : VoxelMaterialId.Soil;
        }

        return VoxelMaterialId.Soil;
    }

    private float SampleTerrainHeight(float worldX, float worldZ)
    {
        SampleTerrainSurface(worldX, worldZ, out float terrain, out _);
        return terrain;
    }

    private void SampleTerrainSurface(float worldX, float worldZ, out float terrain, out TerrainBiomeSample biome)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        terrain = BuildTerrainHeight(layers, biome);
    }

    private float BuildTerrainHeight(TerrainHeightLayers layers, TerrainBiomeSample biome)
    {
        // The surface silhouette is driven by a 2D height field, then caves perturb density beneath it.
        float terrain = 0.0f;
        terrain += layers.Continent * (_terrainHeight * ContinentHeightScale);
        terrain -= _terrainHeight * ContinentBaseOffsetScale;
        terrain += layers.Mountain * (_terrainHeight * MountainHeightScale * layers.MountainStrength);
        terrain += layers.Hills * (_terrainHeight * HillHeightScale * layers.HillStrength);
        terrain += layers.Detail * (_detailHeight * DetailContributionScale * layers.DetailStrength);
        terrain = ApplyWaterAwareTerrainShaping(terrain, biome, layers.LowlandMask, layers.BasinMask);
        return terrain;
    }

    private TerrainHeightLayers SampleHeightLayers(Vector2 warped, TerrainBiomeSample biome)
    {
        float continent = NoiseToUnit(SampleFbm2D(_continentNoise, warped.X, warped.Y, octaves: 4));
        continent = Mathf.SmoothStep(0.18f, 0.82f, continent);

        float lowlandMask = 1.0f - Mathf.SmoothStep(0.22f, 0.78f, continent);
        float shapeBiome = NoiseToUnit(SampleFbm2D(_shapeBiomeNoise, warped.X, warped.Y, octaves: 3));
        shapeBiome = Mathf.SmoothStep(0.30f, 0.75f, shapeBiome);

        float ruggedShapeBoost = Mathf.Clamp(
            (biome.RockyWeight * 0.75f) +
            (biome.CanyonWeight * 0.60f) +
            (biome.VolcanicWeight * 1.00f),
            0.0f,
            1.0f);
        float mountainStrength = Mathf.Lerp(0.0f, 1.10f, shapeBiome);
        mountainStrength *= Mathf.Lerp(0.85f, 1.25f, ruggedShapeBoost);
        mountainStrength = Mathf.Clamp(mountainStrength, 0.0f, 1.25f);
        float mountain = SampleRidge(_mountainNoise, warped.X, warped.Y);
        mountain = Mathf.Pow(mountain, 3.0f);
        mountain *= Mathf.SmoothStep(0.18f, 0.72f, continent);

        float hillStrength = Mathf.Lerp(1.00f, 0.55f, shapeBiome);
        hillStrength *= Mathf.Clamp(
            0.82f +
            (biome.PlainsWeight * 0.22f) -
            (biome.SwampWeight * 0.12f),
            0.65f,
            1.10f);
        float hills = SampleFbm2D(_hillNoise, warped.X, warped.Y, octaves: 3);
        hills *= 0.55f + (lowlandMask * 0.45f);

        float detailStrength = Mathf.Clamp(
            0.70f +
            (biome.RockyWeight * 0.20f) +
            (biome.VolcanicWeight * 0.35f) -
            (biome.SwampWeight * 0.12f),
            0.55f,
            1.20f);
        float detail = _detailNoise.GetNoise2D(warped.X, warped.Y);

        float basinMask = Mathf.SmoothStep(
            WaterBasinThresholdMin,
            WaterBasinThresholdMax,
            NoiseToUnit(_waterBasinNoise.GetNoise2D(warped.X, warped.Y)));

        return new TerrainHeightLayers(
            continent,
            mountain,
            hills,
            detail,
            lowlandMask,
            basinMask,
            shapeBiome,
            mountainStrength,
            hillStrength,
            detailStrength);
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

    private float ApplyWaterAwareTerrainShaping(
        float terrain,
        TerrainBiomeSample biome,
        float lowlandMask,
        float basinMask)
    {
        float shorelineMask = ComputeShoreMask(terrain);
        float submergedMask = ComputeWaterMask(terrain);

        float shelfBlend =
            shorelineMask *
            lowlandMask *
            Mathf.Clamp(
                WaterShelfBlendScale +
                (biome.PlainsWeight * 0.10f) +
                (biome.SwampWeight * 0.16f) -
                (biome.RockyWeight * 0.08f) -
                (biome.CanyonWeight * 0.10f),
                0.0f,
                1.0f);
        terrain = Mathf.Lerp(terrain, _waterLevel + (_shorelineFalloff * WaterShelfHeightScale), shelfBlend);

        float basinDepth =
            WaterBasinDepthScale +
            (biome.CanyonWeight * 0.18f) -
            (biome.SwampWeight * 0.10f);
        float basinBlend = basinMask * lowlandMask * _waterBasinInfluence * (WaterBasinBlendScale + (submergedMask * 0.18f));
        terrain = Mathf.Lerp(terrain, _waterLevel - (_shorelineFalloff * basinDepth), basinBlend);

        float swampFlattenBlend = biome.SwampWeight * lowlandMask * (WaterSwampFlattenScale + (shorelineMask * 0.20f));
        terrain = Mathf.Lerp(terrain, _waterLevel - (_shorelineFalloff * WaterSwampNearWaterOffsetScale), swampFlattenBlend);

        return terrain;
    }

    private float ComputeShoreMask(float terrain)
    {
        float shorelineDistance = Mathf.Abs(terrain - _waterLevel);
        return 1.0f - Mathf.SmoothStep(
            _shorelineFalloff,
            _shorelineFalloff * WaterShorelineFadeMultiplier,
            shorelineDistance);
    }

    private float ComputeWaterMask(float terrain)
    {
        return 1.0f - Mathf.SmoothStep(
            _waterLevel - (_shorelineFalloff * WaterSubmergedFadeMultiplier),
            _waterLevel + (_shorelineFalloff * 0.25f),
            terrain);
    }

    private float SampleCaveContribution(Vector3 worldPosition)
    {
        Vector2 warped = WarpXZ(worldPosition.X, worldPosition.Z);
        return _caveNoise.GetNoise3D(warped.X, worldPosition.Y, warped.Y) * _caveScale;
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }

    private static float SampleFbm2D(
        FastNoiseLite noise,
        float worldX,
        float worldZ,
        int octaves,
        float lacunarity = 2.0f,
        float gain = 0.5f)
    {
        float amplitude = 1.0f;
        float amplitudeSum = 0.0f;
        float total = 0.0f;
        float sampleX = worldX;
        float sampleZ = worldZ;

        for (int octave = 0; octave < octaves; octave++)
        {
            total += noise.GetNoise2D(sampleX, sampleZ) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= gain;
            sampleX *= lacunarity;
            sampleZ *= lacunarity;
        }

        return amplitudeSum > 0.0f
            ? total / amplitudeSum
            : 0.0f;
    }

    private static float SampleRidge(FastNoiseLite noise, float worldX, float worldZ)
    {
        return Mathf.Clamp(1.0f - Mathf.Abs(noise.GetNoise2D(worldX, worldZ)), 0.0f, 1.0f);
    }

    private Vector2 WarpXZ(float worldX, float worldZ)
    {
        float warpX = _warpNoiseX.GetNoise2D(worldX, worldZ) * TerrainWarpStrength;
        float warpZ = _warpNoiseZ.GetNoise2D(worldX, worldZ) * TerrainWarpStrength;
        return new Vector2(worldX + warpX, worldZ + warpZ);
    }

    private readonly record struct TerrainHeightLayers(
        float Continent,
        float Mountain,
        float Hills,
        float Detail,
        float LowlandMask,
        float BasinMask,
        float ShapeBiome,
        float MountainStrength,
        float HillStrength,
        float DetailStrength);
}
