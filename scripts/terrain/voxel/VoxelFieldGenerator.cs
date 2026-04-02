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
    private const float TerrainWarpStrength = 18.0f;
    private const float MountainLandThresholdMin = 0.32f;
    private const float MountainLandThresholdMax = 0.90f;
    private const float MountainRangeUpliftHeightScale = 0.18f;
    private const float MountainFoothillGateFloor = 0.24f;
    private const float MountainHillRangeBlend = 0.72f;

    private const float MountainRangeDirectionX = 0.866f;
    private const float MountainRangeDirectionZ = 0.500f;
    private const float MountainRangeBeltAlongScale = 0.40f;
    private const float MountainRangeBeltAcrossScale = 1.85f;
    private const float MountainRangeSystemAlongScale = 0.24f;
    private const float MountainRangeSystemAcrossScale = 0.72f;
    private const float MountainRangeBeltThresholdMin = 0.46f;
    private const float MountainRangeBeltThresholdMax = 0.78f;
    private const float MountainRangeSystemThresholdMin = 0.42f;
    private const float MountainRangeSystemThresholdMax = 0.76f;
    private const float MountainRangeSystemFloor = 0.26f;
    private const float MountainRangePeakPower = 1.25f;

    private const float WaterShorelineFadeMultiplier = 1.85f;
    private const float WaterSubmergedFadeMultiplier = 1.75f;
    private const float WaterBasinThresholdMin = 0.56f;
    private const float WaterBasinThresholdMax = 0.82f;
    private const float WaterShelfHeightScale = 0.12f;
    private const float WaterShelfBlendScale = 0.34f;
    private const float WaterBasinBlendScale = 0.40f;
    private const float WaterBasinDepthScale = 0.52f;
    private const float WaterSubmergedBasinBoostScale = 0.20f;
    private const float WaterSwampShelfBoostScale = 0.34f;
    private const float WaterPlainsShelfBoostScale = 0.10f;
    private const float WaterRockyShelfReductionScale = 0.14f;
    private const float WaterCanyonShelfReductionScale = 0.24f;
    private const float WaterCanyonBasinDepthScale = 0.38f;
    private const float WaterSwampBasinDepthReductionScale = 0.18f;
    private const float WaterSwampNearWaterBlendScale = 0.48f;
    private const float WaterSwampNearWaterOffsetScale = 0.08f;
    private const float WaterCanyonDryBasinBlendScale = 0.22f;
    private const float WaterCanyonDryBasinDepthScale = 0.72f;

    private const float MacroReliefRegionThresholdMin = 0.44f;
    private const float MacroReliefRegionThresholdMax = 0.74f;
    private const float MacroQuietRegionThresholdMin = 0.42f;
    private const float MacroQuietRegionThresholdMax = 0.78f;
    private const float MacroRidgeStrengthFloor = 0.14f;
    private const float MacroRidgeWallStrengthFloor = 0.22f;
    private const float MacroHillStrengthFloor = 0.34f;
    private const float MacroRollingStrengthFloor = 0.48f;
    private const float MacroDetailStrengthFloor = 0.10f;
    private const float MacroValleyStrengthFloor = 0.52f;
    private const float MacroLowlandFlattenBoostScale = 0.26f;
    private const float MacroQuietTerrainBlendScale = 0.34f;
    private const float MacroQuietTerrainScale = 0.56f;
    private const float MacroQuietTerrainOffsetScale = -0.06f;
    private const float MacroDramaticRidgeBoostScale = 0.22f;
    private const float MacroDramaticDetailBoostScale = 0.26f;

    private const float ContinentHeightScale = 1.45f;
    private const float MountainRidgeHeightScale = 1.15f;
    private const float RidgeWallHeightScale = 0.26f;
    private const float BaseHillHeightScale = 0.11f;
    private const float MountainHillHeightScale = 0.14f;
    private const float PlainsHillHeightScale = 0.06f;
    private const float RollingHeightScale = 0.09f;
    private const float DetailContributionScale = 0.16f;
    private const float ValleyCarveHeightScale = 0.10f;
    private const float LowlandDropHeightScale = 0.08f;

    private const float RockyRuggednessReliefBoost = 0.26f;
    private const float CanyonRuggednessReliefBoost = 0.16f;
    private const float VolcanicRuggednessReliefBoost = 0.20f;
    private const float VolcanicActivityMicroBoost = 0.40f;

    private const float PlainsBroadRollHeightScale = 0.04f;
    private const float RockyCliffHeightScale = 0.15f;
    private const float CanyonValleyCarveHeightScale = 0.17f;
    private const float CanyonTerraceStepScale = 0.16f;
    private const float CanyonTerraceBlendScale = 0.34f;
    private const float SwampFloorHeightScale = 0.78f;
    private const float SwampFloorOffsetScale = -0.18f;
    private const float SwampFlattenBlendScale = 0.55f;
    private const float SwampMoistureFlattenBoostScale = 0.14f;
    private const float VolcanicPeakHeightScale = 0.18f;
    private const float VolcanicMicroReliefScale = 0.52f;
    private const float SwampVegetationBias = 0.60f;

    private static readonly BiomeTerrainParameters PlainsTerrainParameters = new(
        BaseElevationOffset: -0.04f,
        RidgeGain: 0.72f,
        RidgeWallGain: 0.74f,
        HillGain: 0.72f,
        RollingGain: 1.28f,
        ValleyGain: 0.72f,
        MicroGain: 0.48f,
        LowlandFlattenGain: 0.92f);

    private static readonly BiomeTerrainParameters RockyTerrainParameters = new(
        BaseElevationOffset: 0.03f,
        RidgeGain: 1.28f,
        RidgeWallGain: 1.36f,
        HillGain: 1.12f,
        RollingGain: 0.72f,
        ValleyGain: 0.92f,
        MicroGain: 1.12f,
        LowlandFlattenGain: 0.52f);

    private static readonly BiomeTerrainParameters CanyonTerrainParameters = new(
        BaseElevationOffset: -0.20f,
        RidgeGain: 0.92f,
        RidgeWallGain: 1.10f,
        HillGain: 0.82f,
        RollingGain: 0.56f,
        ValleyGain: 1.50f,
        MicroGain: 0.72f,
        LowlandFlattenGain: 1.08f);

    private static readonly BiomeTerrainParameters SwampTerrainParameters = new(
        BaseElevationOffset: -0.18f,
        RidgeGain: 0.36f,
        RidgeWallGain: 0.42f,
        HillGain: 0.42f,
        RollingGain: 0.46f,
        ValleyGain: 0.90f,
        MicroGain: 0.28f,
        LowlandFlattenGain: 1.55f);

    private static readonly BiomeTerrainParameters VolcanicTerrainParameters = new(
        BaseElevationOffset: 0.05f,
        RidgeGain: 1.18f,
        RidgeWallGain: 1.24f,
        HillGain: 1.02f,
        RollingGain: 0.50f,
        ValleyGain: 0.80f,
        MicroGain: 1.55f,
        LowlandFlattenGain: 0.38f);

    private readonly FastNoiseLite _continentNoise;
    private readonly FastNoiseLite _ridgeNoise;
    private readonly FastNoiseLite _hillNoise;
    private readonly FastNoiseLite _detailNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;
    private readonly FastNoiseLite _mountainRangeBeltNoise;
    private readonly FastNoiseLite _mountainRangeSystemNoise;
    private readonly FastNoiseLite _macroReliefNoise;
    private readonly FastNoiseLite _waterBasinNoise;
    private readonly TerrainBiomeClassifier _biomeClassifier;
    private readonly FastNoiseLite _caveNoise;
    private readonly float _terrainHeight;
    private readonly float _detailHeight;
    private readonly float _caveScale;
    private readonly float _caveThreshold;
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
        _caveThreshold = caveThreshold;
        _waterLevel = waterLevel;
        _shorelineFalloff = Mathf.Max(0.4f, shorelineFalloff);
        _waterBasinInfluence = Mathf.Clamp(waterBasinInfluence, 0.0f, 1.0f);

        _continentNoise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0032f
        };

        _ridgeNoise = new FastNoiseLite
        {
            Seed = seed + 37,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 0.0068f
        };

        _hillNoise = new FastNoiseLite
        {
            Seed = seed + 59,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0105f
        };

        _detailNoise = new FastNoiseLite
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.038f
        };

        _warpNoiseX = new FastNoiseLite
        {
            Seed = seed + 131,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0042f
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 157,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0042f
        };

        _mountainRangeBeltNoise = new FastNoiseLite
        {
            Seed = seed + 181,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00165f
        };

        _mountainRangeSystemNoise = new FastNoiseLite
        {
            Seed = seed + 197,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.00105f
        };

        _macroReliefNoise = new FastNoiseLite
        {
            Seed = seed + 211,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00092f
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
        float continent = NoiseToUnit(_continentNoise.GetNoise2D(warped.X, warped.Y));
        continent = Mathf.SmoothStep(0.22f, 0.90f, continent);
        float landPresence = Mathf.SmoothStep(MountainLandThresholdMin, MountainLandThresholdMax, continent);
        MountainRangeMaskSample mask = SampleMountainRangeMask(warped);
        return new TerrainMountainRangeDebugSample(
            mask.BeltMask,
            mask.SystemMask,
            landPresence * mask.ShoulderMask,
            landPresence * mask.PeakMask);
    }

    public TerrainWaterDebugSample SampleWaterDebug(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        float basinNoise = NoiseToUnit(_waterBasinNoise.GetNoise2D(warped.X, warped.Y));
        float terrain = BuildTerrainHeight(warped, biome);
        float shorelineDistance = Mathf.Abs(terrain - _waterLevel);
        float shoreMask = 1.0f - Mathf.SmoothStep(
            _shorelineFalloff,
            _shorelineFalloff * WaterShorelineFadeMultiplier,
            shorelineDistance);
        float waterMask = 1.0f - Mathf.SmoothStep(
            _waterLevel - (_shorelineFalloff * WaterSubmergedFadeMultiplier),
            _waterLevel + (_shorelineFalloff * 0.25f),
            terrain);
        float basinMask = Mathf.SmoothStep(WaterBasinThresholdMin, WaterBasinThresholdMax, basinNoise) * _waterBasinInfluence;
        return new TerrainWaterDebugSample(terrain, shoreMask, waterMask, basinMask);
    }

    private float SampleDensity(Vector3 worldPosition, float terrain)
    {
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
        terrain = BuildTerrainHeight(warped, biome);
    }

    private float BuildTerrainHeight(Vector2 warped, TerrainBiomeSample biome)
    {
        float continent = NoiseToUnit(_continentNoise.GetNoise2D(warped.X, warped.Y));
        continent = Mathf.SmoothStep(0.22f, 0.90f, continent);

        float continentalMountainPotential = Mathf.Pow(continent, 1.55f);
        float plains = 1.0f - continentalMountainPotential;
        float foothills = Mathf.SmoothStep(0.24f, 0.68f, continent);
        MountainRangeMaskSample mountainRanges = SampleMountainRangeMask(warped);
        float landPresence = Mathf.SmoothStep(MountainLandThresholdMin, MountainLandThresholdMax, continent);
        float rangeShoulderMask = landPresence * mountainRanges.ShoulderMask;
        float rangePeakMask = landPresence * mountainRanges.PeakMask;
        float mountains = continentalMountainPotential * rangePeakMask;
        float foothillMountainMask = foothills * Mathf.Lerp(MountainFoothillGateFloor, 1.0f, rangeShoulderMask);
        float macroMountainBias = Mathf.Lerp(continentalMountainPotential, mountains, MountainHillRangeBlend);

        float ridge = 1.0f - Mathf.Abs(_ridgeNoise.GetNoise2D(warped.X, warped.Y));
        ridge *= ridge;
        ridge *= ridge;
        float ridgeWalls = Mathf.SmoothStep(0.68f, 0.95f, ridge);

        float hills = NoiseToUnit(_hillNoise.GetNoise2D(warped.X, warped.Y));
        float detail = _detailNoise.GetNoise2D(warped.X, warped.Y);
        float harshDetail = detail * Mathf.Abs(detail);

        float valleyMask = 1.0f - Mathf.Abs((hills * 2.0f) - 1.0f);
        valleyMask = Mathf.Pow(Mathf.Clamp(valleyMask, 0.0f, 1.0f), 1.65f);
        float rollingMask = Mathf.SmoothStep(0.15f, 0.72f, hills);
        float lowlandMask = Mathf.SmoothStep(0.18f, 0.88f, plains);
        float peakMask = ridgeWalls * mountains;
        float basinNoise = NoiseToUnit(_waterBasinNoise.GetNoise2D(warped.X, warped.Y));
        MacroTerrainRegionSample macroRegion = SampleMacroTerrainRegion(
            warped,
            biome,
            landPresence,
            plains,
            lowlandMask,
            rangeShoulderMask,
            basinNoise);

        BiomeTerrainParameters shape = BlendBiomeTerrainParameters(biome);
        float ruggednessBoost = 1.0f + (biome.Ruggedness * (
            (biome.RockyWeight * RockyRuggednessReliefBoost) +
            (biome.CanyonWeight * CanyonRuggednessReliefBoost) +
            (biome.VolcanicWeight * VolcanicRuggednessReliefBoost)));
        float activityBoost = 1.0f + (biome.Activity * biome.VolcanicWeight * VolcanicActivityMicroBoost);

        float hillAmplitude =
            (BaseHillHeightScale + (macroMountainBias * MountainHillHeightScale) + (plains * PlainsHillHeightScale)) *
            shape.HillGain *
            macroRegion.HillStrength;

        float terrain = (continent - 0.5f) * (_terrainHeight * ContinentHeightScale);
        terrain += _terrainHeight * shape.BaseElevationOffset;
        terrain += foothills * rangeShoulderMask * (_terrainHeight * MountainRangeUpliftHeightScale);
        terrain += ridge * mountains * (_terrainHeight * MountainRidgeHeightScale * shape.RidgeGain * ruggednessBoost * macroRegion.RidgeStrength);
        terrain += ridgeWalls * foothillMountainMask * (_terrainHeight * RidgeWallHeightScale * shape.RidgeWallGain * ruggednessBoost * macroRegion.RidgeWallStrength);
        terrain += (hills - 0.5f) * (_terrainHeight * hillAmplitude);
        terrain += rollingMask * plains * (_terrainHeight * RollingHeightScale * shape.RollingGain * macroRegion.RollingStrength);
        terrain += detail * _detailHeight * (DetailContributionScale * shape.MicroGain * activityBoost * macroRegion.DetailStrength);
        terrain -= valleyMask * lowlandMask * (_terrainHeight * ValleyCarveHeightScale * shape.ValleyGain * macroRegion.ValleyStrength);
        terrain -= lowlandMask * (_terrainHeight * LowlandDropHeightScale * shape.LowlandFlattenGain * macroRegion.LowlandFlattenStrength);

        float quietTerrainTarget =
            ((continent - 0.5f) * (_terrainHeight * MacroQuietTerrainScale)) +
            (_terrainHeight * MacroQuietTerrainOffsetScale);
        terrain = Mathf.Lerp(terrain, quietTerrainTarget, macroRegion.QuietTerrainBlend);

        terrain += rollingMask * plains * (_terrainHeight * PlainsBroadRollHeightScale * biome.PlainsWeight * macroRegion.RollingStrength);
        terrain += ridgeWalls * mountains * (_terrainHeight * RockyCliffHeightScale * biome.RockyWeight * (0.65f + (biome.Ruggedness * 0.35f)) * macroRegion.RidgeStrength);
        terrain -= valleyMask * foothillMountainMask * (_terrainHeight * CanyonValleyCarveHeightScale * biome.CanyonWeight * macroRegion.ValleyStrength);
        terrain += harshDetail * _detailHeight * (VolcanicMicroReliefScale * biome.VolcanicWeight * activityBoost * macroRegion.DetailStrength);
        terrain += peakMask * (_terrainHeight * VolcanicPeakHeightScale * biome.VolcanicWeight * (0.6f + (biome.Activity * 0.4f)) * macroRegion.RidgeStrength);

        float canyonTerraceStep = Mathf.Max(_terrainHeight * CanyonTerraceStepScale, 1.0f);
        float canyonTerraced = Mathf.Round(terrain / canyonTerraceStep) * canyonTerraceStep;
        float canyonShelfMask = Mathf.Max(valleyMask, ridgeWalls * 0.55f);
        terrain = Mathf.Lerp(
            terrain,
            canyonTerraced,
            biome.CanyonWeight * canyonShelfMask * CanyonTerraceBlendScale * Mathf.Lerp(0.72f, 1.0f, macroRegion.DramaticRegion));

        float swampFloor = ((continent - 0.5f) * (_terrainHeight * SwampFloorHeightScale)) + (_terrainHeight * SwampFloorOffsetScale);
        float swampFlattenBlend = biome.SwampWeight * lowlandMask * (SwampFlattenBlendScale + (biome.Moisture * SwampMoistureFlattenBoostScale));
        terrain = Mathf.Lerp(terrain, swampFloor, swampFlattenBlend);
        terrain = ApplyWaterAwareTerrainShaping(terrain, biome, lowlandMask, foothills, valleyMask, basinNoise);

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

    private float ApplyWaterAwareTerrainShaping(
        float terrain,
        TerrainBiomeSample biome,
        float lowlandMask,
        float foothills,
        float valleyMask,
        float basinNoise)
    {
        float shorelineDistance = Mathf.Abs(terrain - _waterLevel);
        float shorelineMask = 1.0f - Mathf.SmoothStep(
            _shorelineFalloff,
            _shorelineFalloff * WaterShorelineFadeMultiplier,
            shorelineDistance);
        float submergedMask = 1.0f - Mathf.SmoothStep(
            _waterLevel - (_shorelineFalloff * WaterSubmergedFadeMultiplier),
            _waterLevel + (_shorelineFalloff * 0.25f),
            terrain);

        float basinMask = Mathf.SmoothStep(WaterBasinThresholdMin, WaterBasinThresholdMax, basinNoise);
        float basinStrength = lowlandMask * basinMask * _waterBasinInfluence;

        float shorelineShelfBlend =
            shorelineMask *
            lowlandMask *
            (
                WaterShelfBlendScale +
                (biome.SwampWeight * WaterSwampShelfBoostScale) +
                (biome.PlainsWeight * WaterPlainsShelfBoostScale) -
                (biome.RockyWeight * WaterRockyShelfReductionScale) -
                (biome.CanyonWeight * WaterCanyonShelfReductionScale));
        shorelineShelfBlend = Mathf.Clamp(shorelineShelfBlend, 0.0f, 1.0f);
        float shorelineShelfTarget = _waterLevel + (_shorelineFalloff * WaterShelfHeightScale);
        terrain = Mathf.Lerp(terrain, shorelineShelfTarget, shorelineShelfBlend);

        float basinDepthScale =
            WaterBasinDepthScale +
            (biome.CanyonWeight * WaterCanyonBasinDepthScale) -
            (biome.SwampWeight * WaterSwampBasinDepthReductionScale);
        float basinFloorTarget = _waterLevel - (_shorelineFalloff * basinDepthScale);
        float basinBlend = basinStrength * (WaterBasinBlendScale + (submergedMask * WaterSubmergedBasinBoostScale));
        terrain = Mathf.Lerp(terrain, basinFloorTarget, basinBlend);

        float swampNearWaterBlend = biome.SwampWeight * shorelineMask * lowlandMask * WaterSwampNearWaterBlendScale;
        float swampNearWaterTarget = _waterLevel - (_shorelineFalloff * WaterSwampNearWaterOffsetScale);
        terrain = Mathf.Lerp(terrain, swampNearWaterTarget, swampNearWaterBlend);

        float canyonDryBasinBlend =
            biome.CanyonWeight *
            basinStrength *
            foothills *
            (0.45f + (valleyMask * 0.55f)) *
            (1.0f - (shorelineMask * 0.65f)) *
            WaterCanyonDryBasinBlendScale;
        float canyonDryBasinTarget = _waterLevel - (_shorelineFalloff * WaterCanyonDryBasinDepthScale);
        terrain = Mathf.Lerp(terrain, canyonDryBasinTarget, canyonDryBasinBlend);

        return terrain;
    }

    private MacroTerrainRegionSample SampleMacroTerrainRegion(
        Vector2 warped,
        TerrainBiomeSample biome,
        float landPresence,
        float plains,
        float lowlandMask,
        float rangeShoulderMask,
        float basinNoise)
    {
        float macroReliefNoise = NoiseToUnit(_macroReliefNoise.GetNoise2D(warped.X, warped.Y));
        float ruggedBiome = biome.RockyWeight + biome.CanyonWeight + biome.VolcanicWeight;
        float softBiome = biome.PlainsWeight + biome.SwampWeight;

        float dramaticRegion = Mathf.SmoothStep(MacroReliefRegionThresholdMin, MacroReliefRegionThresholdMax, macroReliefNoise);
        dramaticRegion = Mathf.Clamp(
            (dramaticRegion * 0.58f) +
            (rangeShoulderMask * 0.34f) +
            (ruggedBiome * 0.22f) +
            (landPresence * 0.08f) -
            (lowlandMask * softBiome * 0.18f),
            0.0f,
            1.0f);

        float quietRegion = 1.0f - Mathf.SmoothStep(MacroQuietRegionThresholdMin, MacroQuietRegionThresholdMax, macroReliefNoise);
        quietRegion *= lowlandMask * (0.55f + (softBiome * 0.45f)) * (0.60f + (plains * 0.40f));
        quietRegion *= Mathf.Lerp(0.55f, 1.0f, basinNoise);
        quietRegion *= 1.0f - (rangeShoulderMask * 0.80f);
        quietRegion = Mathf.Clamp(quietRegion, 0.0f, 1.0f);

        float contrastRegion = Mathf.Clamp(dramaticRegion - (quietRegion * 0.30f), 0.0f, 1.0f);
        float quietReduction = quietRegion * (0.55f + (softBiome * 0.25f));

        float ridgeStrength = Mathf.Clamp(
            Mathf.Lerp(MacroRidgeStrengthFloor, 1.0f + MacroDramaticRidgeBoostScale, contrastRegion) *
            Mathf.Lerp(1.0f, 0.36f, quietReduction),
            MacroRidgeStrengthFloor,
            1.35f);

        float ridgeWallStrength = Mathf.Clamp(
            Mathf.Lerp(MacroRidgeWallStrengthFloor, 1.12f, contrastRegion) *
            Mathf.Lerp(1.0f, 0.50f, quietReduction),
            MacroRidgeWallStrengthFloor,
            1.18f);

        float hillStrength = Mathf.Clamp(
            Mathf.Lerp(MacroHillStrengthFloor, 1.0f, contrastRegion) *
            Mathf.Lerp(1.0f, 0.58f, quietReduction),
            MacroHillStrengthFloor,
            1.05f);

        float rollingStrength = Mathf.Clamp(
            Mathf.Lerp(MacroRollingStrengthFloor, 0.94f, contrastRegion) *
            Mathf.Lerp(1.0f, 0.72f, quietReduction),
            MacroRollingStrengthFloor,
            1.0f);

        float detailStrength = Mathf.Clamp(
            Mathf.Lerp(MacroDetailStrengthFloor, 1.0f + MacroDramaticDetailBoostScale, contrastRegion) *
            Mathf.Lerp(1.0f, 0.28f, quietReduction),
            MacroDetailStrengthFloor,
            1.35f);

        float valleyStrength = Mathf.Clamp(
            Mathf.Lerp(MacroValleyStrengthFloor, 1.08f, (contrastRegion * 0.65f) + (biome.CanyonWeight * 0.35f)),
            MacroValleyStrengthFloor,
            1.12f);

        float lowlandFlattenStrength = 1.0f + (quietRegion * MacroLowlandFlattenBoostScale);
        float quietTerrainBlend = quietRegion * lowlandMask * MacroQuietTerrainBlendScale;

        return new MacroTerrainRegionSample(
            dramaticRegion,
            quietRegion,
            ridgeStrength,
            ridgeWallStrength,
            hillStrength,
            rollingStrength,
            detailStrength,
            valleyStrength,
            lowlandFlattenStrength,
            quietTerrainBlend);
    }

    private MountainRangeMaskSample SampleMountainRangeMask(Vector2 warped)
    {
        float alongAxis = (warped.X * MountainRangeDirectionX) + (warped.Y * MountainRangeDirectionZ);
        float acrossAxis = (warped.X * -MountainRangeDirectionZ) + (warped.Y * MountainRangeDirectionX);

        float beltNoise = 1.0f - Mathf.Abs(_mountainRangeBeltNoise.GetNoise2D(
            alongAxis * MountainRangeBeltAlongScale,
            acrossAxis * MountainRangeBeltAcrossScale));
        beltNoise = Mathf.Clamp(beltNoise, 0.0f, 1.0f);
        float beltMask = Mathf.SmoothStep(MountainRangeBeltThresholdMin, MountainRangeBeltThresholdMax, beltNoise);

        float systemNoise = NoiseToUnit(_mountainRangeSystemNoise.GetNoise2D(
            alongAxis * MountainRangeSystemAlongScale,
            acrossAxis * MountainRangeSystemAcrossScale));
        float systemMask = Mathf.SmoothStep(MountainRangeSystemThresholdMin, MountainRangeSystemThresholdMax, systemNoise);

        float shoulderMask = beltMask * Mathf.Lerp(MountainRangeSystemFloor, 1.0f, systemMask);
        float peakMask = Mathf.Pow(shoulderMask, MountainRangePeakPower);
        return new MountainRangeMaskSample(beltMask, systemMask, shoulderMask, peakMask);
    }

    private static BiomeTerrainParameters BlendBiomeTerrainParameters(TerrainBiomeSample biome)
    {
        return new BiomeTerrainParameters(
            BaseElevationOffset:
                (PlainsTerrainParameters.BaseElevationOffset * biome.PlainsWeight) +
                (RockyTerrainParameters.BaseElevationOffset * biome.RockyWeight) +
                (CanyonTerrainParameters.BaseElevationOffset * biome.CanyonWeight) +
                (SwampTerrainParameters.BaseElevationOffset * biome.SwampWeight) +
                (VolcanicTerrainParameters.BaseElevationOffset * biome.VolcanicWeight),
            RidgeGain:
                (PlainsTerrainParameters.RidgeGain * biome.PlainsWeight) +
                (RockyTerrainParameters.RidgeGain * biome.RockyWeight) +
                (CanyonTerrainParameters.RidgeGain * biome.CanyonWeight) +
                (SwampTerrainParameters.RidgeGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.RidgeGain * biome.VolcanicWeight),
            RidgeWallGain:
                (PlainsTerrainParameters.RidgeWallGain * biome.PlainsWeight) +
                (RockyTerrainParameters.RidgeWallGain * biome.RockyWeight) +
                (CanyonTerrainParameters.RidgeWallGain * biome.CanyonWeight) +
                (SwampTerrainParameters.RidgeWallGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.RidgeWallGain * biome.VolcanicWeight),
            HillGain:
                (PlainsTerrainParameters.HillGain * biome.PlainsWeight) +
                (RockyTerrainParameters.HillGain * biome.RockyWeight) +
                (CanyonTerrainParameters.HillGain * biome.CanyonWeight) +
                (SwampTerrainParameters.HillGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.HillGain * biome.VolcanicWeight),
            RollingGain:
                (PlainsTerrainParameters.RollingGain * biome.PlainsWeight) +
                (RockyTerrainParameters.RollingGain * biome.RockyWeight) +
                (CanyonTerrainParameters.RollingGain * biome.CanyonWeight) +
                (SwampTerrainParameters.RollingGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.RollingGain * biome.VolcanicWeight),
            ValleyGain:
                (PlainsTerrainParameters.ValleyGain * biome.PlainsWeight) +
                (RockyTerrainParameters.ValleyGain * biome.RockyWeight) +
                (CanyonTerrainParameters.ValleyGain * biome.CanyonWeight) +
                (SwampTerrainParameters.ValleyGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.ValleyGain * biome.VolcanicWeight),
            MicroGain:
                (PlainsTerrainParameters.MicroGain * biome.PlainsWeight) +
                (RockyTerrainParameters.MicroGain * biome.RockyWeight) +
                (CanyonTerrainParameters.MicroGain * biome.CanyonWeight) +
                (SwampTerrainParameters.MicroGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.MicroGain * biome.VolcanicWeight),
            LowlandFlattenGain:
                (PlainsTerrainParameters.LowlandFlattenGain * biome.PlainsWeight) +
                (RockyTerrainParameters.LowlandFlattenGain * biome.RockyWeight) +
                (CanyonTerrainParameters.LowlandFlattenGain * biome.CanyonWeight) +
                (SwampTerrainParameters.LowlandFlattenGain * biome.SwampWeight) +
                (VolcanicTerrainParameters.LowlandFlattenGain * biome.VolcanicWeight));
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }

    private Vector2 WarpXZ(float worldX, float worldZ)
    {
        float warpX = _warpNoiseX.GetNoise2D(worldX, worldZ) * TerrainWarpStrength;
        float warpZ = _warpNoiseZ.GetNoise2D(worldX, worldZ) * TerrainWarpStrength;
        return new Vector2(worldX + warpX, worldZ + warpZ);
    }

    private readonly record struct BiomeTerrainParameters(
        float BaseElevationOffset,
        float RidgeGain,
        float RidgeWallGain,
        float HillGain,
        float RollingGain,
        float ValleyGain,
        float MicroGain,
        float LowlandFlattenGain);

    private readonly record struct MountainRangeMaskSample(
        float BeltMask,
        float SystemMask,
        float ShoulderMask,
        float PeakMask);

    private readonly record struct MacroTerrainRegionSample(
        float DramaticRegion,
        float QuietRegion,
        float RidgeStrength,
        float RidgeWallStrength,
        float HillStrength,
        float RollingStrength,
        float DetailStrength,
        float ValleyStrength,
        float LowlandFlattenStrength,
        float QuietTerrainBlend);
}
