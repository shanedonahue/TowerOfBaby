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

public readonly record struct TerrainSurfaceColumnSample(
    float TerrainHeight,
    TerrainBiomeSample Biome);

public readonly record struct TerrainShapeTelemetrySample(
    float TerrainHeight,
    float SurfaceSlope,
    float PlainsFlattenMask,
    float SwampSoftenMask,
    float MountainSystemMask,
    float MountainSystemLiftMask,
    float MountainShoulderMask,
    float MountainCoreMask,
    float MountainLiftDamping,
    float HillDamping,
    float CoreHillDamping,
    float CoreLocalReliefDamping,
    float RegionalContribution,
    float MountainLiftContribution,
    float MountainBackboneContribution,
    float HeroPeakContribution,
    float SecondaryRidgeContribution,
    float HillContribution,
    float LocalReliefContribution,
    float SurfaceBreakupContribution,
    float WaterShapingDelta);

public sealed class VoxelFieldGenerator
{
    // Terrain is authored in meters so a ~2 meter player reads against believable landform scales.
    private const float PlayerHeightMeters = 2.0f;
    private const float DefaultMajorReliefBudgetMeters = 10.0f;
    private const float DefaultDetailReliefBudgetMeters = 2.8f;
    private const float TerrainSlopeSampleOffsetMeters = PlayerHeightMeters;

    private const float TerrainWarpWavelengthMeters = 900.0f;
    private const float TerrainWarpStrengthMeters = 24.0f;

    private const float ContinentWavelengthMeters = 2800.0f;
    private const float ContinentLiftMeters = 7.0f;
    private const float ContinentBaseOffsetMeters = 6.0f;

    private const float RegionalPartitionWavelengthMeters = 1200.0f;
    private const float RegionalPartitionReliefMeters = 2.2f;
    private const float RegionalBasinThresholdMin = 0.58f;
    private const float RegionalBasinThresholdMax = 0.85f;

    private const float MountainSystemWavelengthMeters = 760.0f;
    private const float MountainSystemThresholdMin = 0.43f;
    private const float MountainSystemThresholdMax = 0.78f;
    private const float MountainSystemLiftThresholdMin = 0.18f;
    private const float MountainSystemLiftThresholdMax = 0.82f;
    private const float MountainSystemLiftMeters = 45.8f;
    private const float MountainShoulderThresholdMin = 0.16f;
    private const float MountainShoulderThresholdMax = 0.60f;
    private const float MountainCoreThresholdMin = 0.54f;
    private const float MountainCoreThresholdMax = 0.86f;
    private const float MountainCoreAmplificationPower = 1.65f;
    private const float MountainCoreAmplificationMax = 0.55f;
    private const float MountainShoulderFoothillBoost = 0.16f;
    private const float MountainShoulderLocalReliefBoost = 0.08f;
    private const float MountainShoulderRidgeBoost = 0.18f;
    private const float MountainCoreHillContributionMin = 0.74f;
    private const float MountainCoreLocalReliefContributionMin = 0.88f;

    private const float MountainBackboneWavelengthMeters = 210.0f;
    private const float MountainBackboneReliefMeters = 9.5f;

    private const float HeroPeakWavelengthMeters = 420.0f;
    private const float HeroPeakThresholdMin = 0.79f;
    private const float HeroPeakThresholdMax = 0.93f;
    private const float HeroPeakMaskPower = 0.60f;
    private const float HeroPeakReliefMeters = 5.0f;

    private const float SecondaryRidgeWavelengthMeters = 180.0f;
    private const float SecondaryRidgeReliefMeters = 4.0f;

    private const float HillWavelengthMeters = 108.0f;
    private const float HillReliefMeters = 3.0f;

    private const float LocalReliefWavelengthMeters = 34.0f;
    private const float LocalReliefMeters = 1.00f;

    private const float SurfaceBreakupWavelengthMeters = PlayerHeightMeters * 4.0f;
    private const float SurfaceBreakupMeters = 0.30f;

    private const float CaveWavelengthMeters = 22.0f;
    private const float CaveSurfaceFadeStartMeters = PlayerHeightMeters * 2.0f;
    private const float CaveSurfaceFadeEndMeters = PlayerHeightMeters * 4.5f;

    private const float PlainsMacroFlattenStrength = 0.55f;
    private const float RockyRidgeStrengthBoost = 0.30f;
    private const float CanyonPartitionStrength = 0.78f;
    private const float CanyonIncisionDepthMeters = 2.6f;
    private const float SwampLowlandFlattenStrength = 0.60f;
    private const float SwampBasinDepthMeters = 1.4f;
    private const float VolcanicPartitionStrength = 0.70f;
    private const float VolcanicPlateLiftMeters = 2.0f;
    private const float VolcanicRubbleReliefMeters = 0.55f;

    private const float SwampVegetationBias = 0.60f;
    private const float WaterShorelineFadeMultiplier = 1.85f;
    private const float WaterSubmergedFadeMultiplier = 1.75f;
    private const float WaterShelfBlendBase = 0.30f;
    private const float WaterShelfHeightOffsetMeters = 0.45f;
    private const float WaterBasinBlendBase = 0.38f;
    private const float WaterBasinDepthMeters = 2.2f;
    private const float WaterSwampFlattenBlendBase = 0.40f;
    private const float WaterSwampNearWaterOffsetMeters = 0.25f;
    private const float SurfaceRockSlopeThreshold = 0.58f;
    private const float SurfaceRockMidDepthSlopeThreshold = 0.34f;
    private const float SurfaceRockShallowSlopeThreshold = 0.28f;
    private const float AlpineSnowHeightThreshold = 0.82f;
    private const float AlpineRockHeightThreshold = 0.74f;
    private const float DeepRockDepthMeters = 4.8f;
    private const float MidSoilDepthMeters = 3.6f;
    private const float SurfaceCoverDepthMeters = 1.4f;
    private const float ShallowCoverDepthMeters = 3.4f;
    private const float SoilTransitionSlopeStart = 0.16f;
    private const float SoilTransitionSlopeEnd = 0.34f;
    private const float SoilTransitionHeightStart = 0.72f;
    private const float SoilTransitionHeightEnd = 0.88f;
    private const float SoilTransitionDepthStart = 0.60f;
    private const float SoilTransitionDepthEnd = 3.20f;
    private const float GrassCoverageBase = 0.68f;
    private const float GrassCoverageVegetationScale = 0.12f;
    private const float GrassCoverageMoistureScale = 0.06f;
    private const float GrassCoverageRockyPenalty = 0.12f;
    private const float GrassCoverageCanyonPenalty = 0.08f;
    private const float GrassCoverageVolcanicPenalty = 0.10f;
    private const float SoilTransitionSlopeScale = 0.82f;
    private const float SoilTransitionDepthScale = 0.34f;
    private const float SoilTransitionHeightScale = 0.08f;
    private const float SoilTransitionRockyScale = 0.10f;
    private const float SoilTransitionCanyonScale = 0.08f;
    private const float SoilTransitionVolcanicScale = 0.12f;
    private readonly FastNoiseLite _continentNoise;
    private readonly FastNoiseLite _regionalPartitionNoise;
    private readonly FastNoiseLite _mountainSystemNoise;
    private readonly FastNoiseLite _mountainBackboneNoise;
    private readonly FastNoiseLite _secondaryRidgeNoise;
    private readonly FastNoiseLite _heroPeakNoise;
    private readonly FastNoiseLite _hillNoise;
    private readonly FastNoiseLite _localReliefNoise;
    private readonly FastNoiseLite _surfaceBreakupNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;
    private readonly TerrainBiomeClassifier _biomeClassifier;
    private readonly FastNoiseLite _caveNoise;
    private readonly float _majorReliefScale;
    private readonly float _detailReliefScale;
    private readonly float _surfaceHeightMin;
    private readonly float _surfaceHeightMax;
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
        _majorReliefScale = Mathf.Max(0.2f, terrainHeight / DefaultMajorReliefBudgetMeters);
        _detailReliefScale = Mathf.Max(0.2f, detailHeight / DefaultDetailReliefBudgetMeters);
        _caveScale = caveScale;
        _caveThreshold = Mathf.Clamp(caveThreshold, 0.0f, 0.98f);
        _waterLevel = waterLevel;
        _shorelineFalloff = Mathf.Max(0.4f, shorelineFalloff);
        _waterBasinInfluence = Mathf.Clamp(waterBasinInfluence, 0.0f, 1.0f);

        _surfaceHeightMin =
            ((-ContinentBaseOffsetMeters - RegionalPartitionReliefMeters - CanyonIncisionDepthMeters - SwampBasinDepthMeters) * _majorReliefScale) -
            ((LocalReliefMeters + SurfaceBreakupMeters) * _detailReliefScale);

        // Surface material classification should react to the readable terrain band, not the absolute rare-peak ceiling.
        // Using full mountain-core amplification here collapses alpine/rock breakup back toward grass.
        _surfaceHeightMax =
            ((ContinentLiftMeters - ContinentBaseOffsetMeters) +
             RegionalPartitionReliefMeters +
             MountainSystemLiftMeters +
             MountainBackboneReliefMeters +
             HeroPeakReliefMeters +
             SecondaryRidgeReliefMeters +
             HillReliefMeters +
             VolcanicPlateLiftMeters) * _majorReliefScale +
            ((LocalReliefMeters + SurfaceBreakupMeters + VolcanicRubbleReliefMeters) * _detailReliefScale);

        _continentNoise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FrequencyFromWavelength(ContinentWavelengthMeters)
        };

        _regionalPartitionNoise = new FastNoiseLite
        {
            Seed = seed + 19,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            Frequency = FrequencyFromWavelength(RegionalPartitionWavelengthMeters)
        };

        _mountainSystemNoise = new FastNoiseLite
        {
            Seed = seed + 37,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FrequencyFromWavelength(MountainSystemWavelengthMeters)
        };

        _mountainBackboneNoise = new FastNoiseLite
        {
            Seed = seed + 59,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = FrequencyFromWavelength(MountainBackboneWavelengthMeters)
        };

        _secondaryRidgeNoise = new FastNoiseLite
        {
            Seed = seed + 71,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = FrequencyFromWavelength(SecondaryRidgeWavelengthMeters)
        };

        _hillNoise = new FastNoiseLite
        {
            Seed = seed + 83,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = FrequencyFromWavelength(HillWavelengthMeters)
        };

        _localReliefNoise = new FastNoiseLite
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = FrequencyFromWavelength(LocalReliefWavelengthMeters)
        };

        _heroPeakNoise = new FastNoiseLite
        {
            Seed = seed + 127,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FrequencyFromWavelength(HeroPeakWavelengthMeters)
        };

        _surfaceBreakupNoise = new FastNoiseLite
        {
            Seed = seed + 149,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = FrequencyFromWavelength(SurfaceBreakupWavelengthMeters)
        };

        _warpNoiseX = new FastNoiseLite
        {
            Seed = seed + 131,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FrequencyFromWavelength(TerrainWarpWavelengthMeters)
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 157,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FrequencyFromWavelength(TerrainWarpWavelengthMeters)
        };

        _biomeClassifier = new TerrainBiomeClassifier(seed);

        _caveNoise = new FastNoiseLite
        {
            Seed = seed + 251,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            Frequency = FrequencyFromWavelength(CaveWavelengthMeters)
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

    public TerrainSurfaceColumnSample SampleSurfaceColumn(float worldX, float worldZ)
    {
        SampleTerrainSurface(worldX, worldZ, out float terrain, out TerrainBiomeSample biome);
        return new TerrainSurfaceColumnSample(terrain, biome);
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

    public TerrainShapeTelemetrySample SampleTerrainShapeTelemetry(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        TerrainHeightDamping damping = ResolveTerrainDamping(layers, biome);
        float terrain = BuildTerrainHeight(layers, biome, damping, out TerrainHeightComposition composition);
        float slope = SampleSlope(worldX, worldZ);
        return new TerrainShapeTelemetrySample(
            terrain,
            slope,
            damping.PlainsFlattenMask,
            damping.SwampSoftenMask,
            layers.MountainSystemMask,
            layers.MountainSystemLiftMask,
            layers.MountainShoulderMask,
            layers.MountainCoreMask,
            damping.MountainLiftDamping,
            damping.HillDamping,
            damping.CoreHillDamping,
            damping.CoreLocalReliefDamping,
            composition.RegionalContribution,
            composition.MountainLiftContribution,
            composition.MountainBackboneContribution,
            composition.HeroPeakContribution,
            composition.SecondaryRidgeContribution,
            composition.HillContribution,
            composition.LocalReliefContribution,
            composition.SurfaceBreakupContribution,
            composition.WaterShapingDelta);
    }

    public Vector3 SampleSurfaceNormal(Vector3 worldPosition, float sampleStep)
    {
        float step = Mathf.Max(0.5f, sampleStep);
        float heightLeft = SampleTerrainHeight(worldPosition.X - step, worldPosition.Z);
        float heightRight = SampleTerrainHeight(worldPosition.X + step, worldPosition.Z);
        float heightBack = SampleTerrainHeight(worldPosition.X, worldPosition.Z - step);
        float heightForward = SampleTerrainHeight(worldPosition.X, worldPosition.Z + step);
        Vector3 tangentX = new(step * 2.0f, heightRight - heightLeft, 0.0f);
        Vector3 tangentZ = new(0.0f, heightForward - heightBack, step * 2.0f);
        Vector3 normal = tangentZ.Cross(tangentX);
        if (normal.LengthSquared() <= 0.000001f)
        {
            return Vector3.Up;
        }

        return normal.Normalized();
    }

    public TerrainMountainRangeDebugSample SampleMountainRangeDebug(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        float landPresence = Mathf.SmoothStep(0.18f, 0.72f, layers.Landmass);
        float shoulderMask = Mathf.Clamp(
            (layers.MountainShoulderMask * 0.68f) +
            (layers.SecondaryRidges * 0.32f),
            0.0f,
            1.0f);
        shoulderMask *= landPresence;
        float peakMask = Mathf.Clamp(
            layers.MountainCoreMask *
            layers.MountainBackbone *
            Mathf.Lerp(0.90f, 1.30f, layers.HeroPeakMask),
            0.0f,
            1.0f);
        return new TerrainMountainRangeDebugSample(
            landPresence,
            layers.MountainSystemMask,
            shoulderMask,
            peakMask);
    }

    public TerrainWaterDebugSample SampleWaterDebug(float worldX, float worldZ)
    {
        Vector2 warped = WarpXZ(worldX, worldZ);
        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldX, worldZ);
        TerrainHeightLayers layers = SampleHeightLayers(warped, biome);
        float terrain = BuildTerrainHeight(layers, biome);
        float basinMask =
            layers.BasinMask *
            layers.LowlandMask *
            _waterBasinInfluence *
            Mathf.Clamp(0.85f + (biome.CanyonWeight * 0.25f) + (biome.SwampWeight * 0.15f), 0.0f, 1.25f);
        return new TerrainWaterDebugSample(
            terrain,
            ComputeShoreMask(terrain),
            ComputeWaterMask(terrain),
            basinMask);
    }

    public float SampleDensity(Vector3 worldPosition, float terrain)
    {
        float density = terrain - worldPosition.Y;
        density += SampleCaveContribution(worldPosition, terrain);
        return density;
    }

    public VoxelMaterialId SampleMaterial(
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
        float normalizedHeight = NormalizeTerrainHeight(terrain);

        if (slope > SurfaceRockSlopeThreshold)
        {
            return normalizedHeight > 0.68f ? VoxelMaterialId.Cliff : VoxelMaterialId.Rock;
        }

        if (normalizedHeight > AlpineSnowHeightThreshold && slope < 0.4f)
        {
            return VoxelMaterialId.Snow;
        }

        if (depthBelowSurface > DeepRockDepthMeters)
        {
            return VoxelMaterialId.Rock;
        }

        if (depthBelowSurface > MidSoilDepthMeters)
        {
            return slope > SurfaceRockMidDepthSlopeThreshold
                ? VoxelMaterialId.Rock
                : ResolveGroundCoverMaterial(depthBelowSurface, slope, normalizedHeight, vegetationSignal, biome);
        }

        if (depthBelowSurface < SurfaceCoverDepthMeters)
        {
            if (normalizedHeight > AlpineRockHeightThreshold)
            {
                return slope > 0.22f ? VoxelMaterialId.Rock : VoxelMaterialId.Snow;
            }

            return ResolveGroundCoverMaterial(depthBelowSurface, slope, normalizedHeight, vegetationSignal, biome);
        }

        if (depthBelowSurface < ShallowCoverDepthMeters)
        {
            if (slope > SurfaceRockShallowSlopeThreshold)
            {
                return VoxelMaterialId.Rock;
            }

            return ResolveGroundCoverMaterial(depthBelowSurface, slope, normalizedHeight, vegetationSignal, biome);
        }

        return ResolveGroundCoverMaterial(depthBelowSurface, slope, normalizedHeight, vegetationSignal, biome);
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
        return BuildTerrainHeight(layers, biome, ResolveTerrainDamping(layers, biome), out _);
    }

    private float BuildTerrainHeight(
        TerrainHeightLayers layers,
        TerrainBiomeSample biome,
        TerrainHeightDamping damping,
        out TerrainHeightComposition composition)
    {
        float terrain = 0.0f;
        float regionalContribution =
            layers.RegionalRelief *
            RegionalPartitionReliefMeters *
            _majorReliefScale *
            (1.0f - (damping.PlainsFlattenMask * 0.30f));
        float mountainLiftContribution =
            layers.MountainSystemLiftMask *
            MountainSystemLiftMeters *
            _majorReliefScale *
            layers.MountainStrength *
            damping.MountainLiftDamping;
        float mountainBackboneContribution =
            layers.MountainBackbone *
            MountainBackboneReliefMeters *
            _majorReliefScale *
            layers.MountainStrength *
            layers.MountainAmplification;
        float heroPeakContribution =
            layers.HeroPeakMask *
            HeroPeakReliefMeters *
            _majorReliefScale *
            layers.MountainStrength *
            layers.MountainAmplification;
        float secondaryRidgeContribution =
            layers.SecondaryRidges *
            SecondaryRidgeReliefMeters *
            _majorReliefScale *
            layers.FoothillStrength *
            damping.RidgeDamping *
            damping.ShoulderRidgeBoost;
        float hillContribution =
            layers.Hills *
            HillReliefMeters *
            _majorReliefScale *
            layers.HillStrength *
            damping.HillDamping *
            damping.CoreHillDamping;
        float localReliefContribution =
            layers.LocalRelief *
            LocalReliefMeters *
            _detailReliefScale *
            layers.LocalReliefStrength *
            damping.LocalReliefDamping *
            damping.CoreLocalReliefDamping;
        float surfaceBreakupContribution =
            layers.SurfaceBreakup *
            SurfaceBreakupMeters *
            _detailReliefScale *
            layers.SurfaceBreakupStrength *
            damping.SurfaceBreakupDamping;

        // Continents establish the kilometer-scale landmass bias and broad shelf height.
        terrain += ((layers.Landmass * ContinentLiftMeters) - ContinentBaseOffsetMeters) * _majorReliefScale;

        // Regional cellular partitioning provides basin/plateau clustering without becoming the main height source.
        terrain += regionalContribution;

        // Broad system lift establishes the mountain mass before sharper backbones and peaks sit on top.
        terrain += mountainLiftContribution;
        terrain += mountainBackboneContribution;
        terrain += heroPeakContribution;

        // Shoulders keep foothills readable, while ordinary hills yield to the mountain silhouette in strong cores.
        terrain += secondaryRidgeContribution;
        terrain += hillContribution;

        // This mid-frequency relief layer is deliberate: it gives the adaptive mesher more curvature to capture.
        terrain += localReliefContribution;

        // Tiny breakup stays low amplitude so it helps read surface roughness without turning into jitter.
        terrain += surfaceBreakupContribution;

        // Biome-specific shaping stays blended by weights instead of hard switching.
        terrain -= layers.CanyonIncisionMask * CanyonIncisionDepthMeters * _majorReliefScale;
        terrain -= layers.SwampBasinMask * SwampBasinDepthMeters * _majorReliefScale;
        terrain += layers.VolcanicPartitionMask * VolcanicPlateLiftMeters * _majorReliefScale;
        terrain += layers.SurfaceBreakup * VolcanicRubbleReliefMeters * _detailReliefScale * layers.VolcanicPartitionMask;

        float terrainBeforeWater = terrain;
        terrain = ApplyWaterAwareTerrainShaping(terrain, biome, layers.LowlandMask, layers.BasinMask);
        composition = new TerrainHeightComposition(
            regionalContribution,
            mountainLiftContribution,
            mountainBackboneContribution,
            heroPeakContribution,
            secondaryRidgeContribution,
            hillContribution,
            localReliefContribution,
            surfaceBreakupContribution,
            terrain - terrainBeforeWater);
        return terrain;
    }

    private static TerrainHeightDamping ResolveTerrainDamping(TerrainHeightLayers layers, TerrainBiomeSample biome)
    {
        float plainsFlattenMask = Mathf.Clamp(biome.PlainsWeight * layers.LowlandMask * PlainsMacroFlattenStrength, 0.0f, 1.0f);
        float swampSoftenMask = Mathf.Clamp(biome.SwampWeight * layers.LowlandMask * SwampLowlandFlattenStrength, 0.0f, 1.0f);
        float mountainLiftDamping = Mathf.Clamp(1.0f - (plainsFlattenMask * 0.16f) - (swampSoftenMask * 0.10f), 0.68f, 1.0f);
        float ridgeDamping = Mathf.Clamp(1.0f - (plainsFlattenMask * 0.35f) - (swampSoftenMask * 0.18f), 0.42f, 1.0f);
        float hillDamping = Mathf.Clamp(1.0f - (swampSoftenMask * 0.10f), 0.80f, 1.0f);
        float localReliefDamping = Mathf.Clamp(1.0f - (swampSoftenMask * 0.28f), 0.50f, 1.0f);
        float surfaceBreakupDamping = Mathf.Clamp(1.0f - (plainsFlattenMask * 0.42f) - (swampSoftenMask * 0.18f), 0.30f, 1.0f);
        float shoulderRidgeBoost = Mathf.Lerp(1.0f, 1.0f + MountainShoulderRidgeBoost, layers.MountainShoulderMask);
        float coreHillDamping = Mathf.Lerp(1.0f, MountainCoreHillContributionMin, layers.MountainCoreMask);
        float coreLocalReliefDamping = Mathf.Lerp(1.0f, MountainCoreLocalReliefContributionMin, layers.MountainCoreMask);
        return new TerrainHeightDamping(
            plainsFlattenMask,
            swampSoftenMask,
            mountainLiftDamping,
            ridgeDamping,
            hillDamping,
            localReliefDamping,
            surfaceBreakupDamping,
            shoulderRidgeBoost,
            coreHillDamping,
            coreLocalReliefDamping);
    }

    private TerrainHeightLayers SampleHeightLayers(Vector2 warped, TerrainBiomeSample biome)
    {
        // Landmass: multi-kilometer simplex smooth mask for continental-scale presence.
        float landmass = NoiseToUnit(SampleFbm2D(_continentNoise, warped.X, warped.Y, octaves: 4, lacunarity: 1.92f, gain: 0.56f));
        landmass = Mathf.SmoothStep(0.18f, 0.82f, landmass);

        float landPresence = Mathf.SmoothStep(0.16f, 0.76f, landmass);
        float lowlandMask = 1.0f - Mathf.SmoothStep(0.32f, 0.78f, landmass);

        // Cellular partitioning clusters basins and broken regional blocks without driving the entire height field.
        float regionalPartitionSignal = NoiseToUnit(_regionalPartitionNoise.GetNoise2D(warped.X, warped.Y));
        float regionalPartition = Mathf.SmoothStep(0.18f, 0.84f, regionalPartitionSignal);
        float regionalRelief = SignedFromUnit(regionalPartition) * landPresence;
        float basinMask = Mathf.SmoothStep(RegionalBasinThresholdMin, RegionalBasinThresholdMax, regionalPartitionSignal);
        basinMask *= 0.65f + (lowlandMask * 0.35f);

        // Mountain systems: broad simplex masks decide where large ranges can exist.
        float mountainSystemMask = NoiseToUnit(SampleFbm2D(_mountainSystemNoise, warped.X, warped.Y, octaves: 3, lacunarity: 2.02f, gain: 0.58f));
        mountainSystemMask = Mathf.SmoothStep(MountainSystemThresholdMin, MountainSystemThresholdMax, mountainSystemMask);
        mountainSystemMask *= landPresence;
        mountainSystemMask *= Mathf.Lerp(0.82f, 1.16f, regionalPartition);
        mountainSystemMask = Mathf.Clamp(mountainSystemMask, 0.0f, 1.0f);

        // Derived mountain-zone masks reuse the existing system layout to separate foothills, shoulders, and core ranges.
        float mountainSystemLiftMask = Mathf.Lerp(
            mountainSystemMask,
            Mathf.SmoothStep(MountainSystemLiftThresholdMin, MountainSystemLiftThresholdMax, mountainSystemMask),
            0.35f);
        float mountainCoreMask = Mathf.SmoothStep(MountainCoreThresholdMin, MountainCoreThresholdMax, mountainSystemMask);

        // Ridged backbones and secondary ridges provide the sharp structure inside each mountain system.
        float mountainBackbone = SampleRidgedFbm2D(_mountainBackboneNoise, warped.X, warped.Y, octaves: 2, lacunarity: 2.05f, gain: 0.55f);
        mountainBackbone = Mathf.Pow(mountainBackbone, 1.85f) * mountainSystemMask;
        float secondaryRidges = SampleRidgedFbm2D(_secondaryRidgeNoise, warped.X, warped.Y, octaves: 2, lacunarity: 2.0f, gain: 0.58f);
        secondaryRidges *= Mathf.Lerp(0.35f, 1.0f, mountainSystemMask);
        secondaryRidges *= 0.55f + (regionalPartition * 0.20f) + ((1.0f - lowlandMask) * 0.25f);
        secondaryRidges = Mathf.Clamp(secondaryRidges, 0.0f, 1.0f);
        float mountainShoulderMask = Mathf.SmoothStep(MountainShoulderThresholdMin, MountainShoulderThresholdMax, mountainSystemMask);
        mountainShoulderMask *= 1.0f - (mountainCoreMask * 0.65f);
        mountainShoulderMask = Mathf.Clamp(mountainShoulderMask + (secondaryRidges * 0.18f), 0.0f, 1.0f);
        float mountainAmplification = 1.0f + (Mathf.Pow(mountainCoreMask, MountainCoreAmplificationPower) * MountainCoreAmplificationMax);

        // Hero peaks are sparse simplex masks layered over the backbone to create standout landmarks.
        float heroPeakMask = NoiseToUnit(SampleFbm2D(_heroPeakNoise, warped.X, warped.Y, octaves: 2, lacunarity: 2.10f, gain: 0.60f));
        heroPeakMask = Mathf.SmoothStep(HeroPeakThresholdMin, HeroPeakThresholdMax, heroPeakMask);
        heroPeakMask *= Mathf.SmoothStep(0.40f, 0.82f, mountainSystemMask);
        heroPeakMask *= Mathf.SmoothStep(0.44f, 0.84f, mountainBackbone);
        heroPeakMask = Mathf.Clamp(heroPeakMask, 0.0f, 1.0f);
        heroPeakMask = heroPeakMask <= 0.0f
            ? 0.0f
            : Mathf.Pow(heroPeakMask, HeroPeakMaskPower);

        // Perlin fBm keeps the softer layers readable between the sharper ridge systems.
        float hills = SampleFbm2D(_hillNoise, warped.X, warped.Y, octaves: 2, lacunarity: 1.95f, gain: 0.52f);
        hills *= 0.80f + (lowlandMask * 0.20f);
        float localRelief = SampleFbm2D(_localReliefNoise, warped.X, warped.Y, octaves: 2, lacunarity: 1.85f, gain: 0.58f);
        localRelief *= 0.80f;
        localRelief *= Mathf.Lerp(0.68f, 1.04f, (mountainSystemMask * 0.55f) + (regionalPartition * 0.45f));
        float surfaceBreakup = _surfaceBreakupNoise.GetNoise2D(warped.X, warped.Y) * 0.75f;

        float mountainStrength = Mathf.Clamp(
            0.78f +
            (biome.RockyWeight * RockyRidgeStrengthBoost) +
            (biome.CanyonWeight * 0.12f) +
            (biome.VolcanicWeight * 0.26f) +
            (biome.Ruggedness * 0.18f) -
            (biome.PlainsWeight * lowlandMask * 0.20f) -
            (biome.SwampWeight * lowlandMask * 0.24f),
            0.45f,
            1.45f);
        mountainStrength *= Mathf.Lerp(0.92f, 1.12f, heroPeakMask);
        mountainStrength = Mathf.Clamp(mountainStrength, 0.45f, 1.55f);

        float foothillStrength = Mathf.Clamp(
            0.72f +
            (biome.RockyWeight * 0.22f) +
            (biome.CanyonWeight * 0.18f) +
            (biome.VolcanicWeight * 0.20f) -
            (biome.SwampWeight * 0.18f),
            0.45f,
            1.25f);
        foothillStrength *= Mathf.Lerp(1.0f, 1.0f + MountainShoulderFoothillBoost, mountainShoulderMask);
        foothillStrength = Mathf.Clamp(foothillStrength, 0.45f, 1.35f);

        float hillStrength = Mathf.Clamp(
            0.66f +
            (biome.PlainsWeight * 0.26f) +
            (biome.CanyonWeight * 0.08f) -
            (biome.SwampWeight * 0.14f) -
            (biome.VolcanicWeight * 0.04f),
            0.40f,
            1.10f);

        float localReliefStrength = Mathf.Clamp(
            0.78f +
            (biome.RockyWeight * 0.16f) +
            (biome.CanyonWeight * CanyonPartitionStrength * 0.18f) +
            (biome.VolcanicWeight * 0.26f) -
            (biome.SwampWeight * 0.16f),
            0.55f,
            1.30f);
        localReliefStrength *= Mathf.Lerp(1.0f, 1.0f + MountainShoulderLocalReliefBoost, mountainShoulderMask);
        localReliefStrength = Mathf.Clamp(localReliefStrength, 0.55f, 1.38f);

        float surfaceBreakupStrength = Mathf.Clamp(
            0.55f +
            (biome.RockyWeight * 0.10f) +
            (biome.CanyonWeight * 0.12f) +
            (biome.VolcanicWeight * 0.25f) -
            (biome.PlainsWeight * 0.08f) -
            (biome.SwampWeight * 0.18f),
            0.28f,
            1.05f);

        float canyonIncisionMask =
            biome.CanyonWeight *
            basinMask *
            (0.35f + (regionalPartition * CanyonPartitionStrength * 0.35f) + (Mathf.Abs(localRelief) * 0.30f));
        canyonIncisionMask *= Mathf.Lerp(1.10f, 0.85f, mountainSystemMask);
        canyonIncisionMask = Mathf.Clamp(canyonIncisionMask, 0.0f, 1.0f);

        float swampBasinMask =
            biome.SwampWeight *
            lowlandMask *
            (0.45f + (basinMask * 0.55f));
        swampBasinMask = Mathf.Clamp(swampBasinMask, 0.0f, 1.0f);

        float volcanicPartitionMask =
            biome.VolcanicWeight *
            (0.30f + (biome.Activity * VolcanicPartitionStrength)) *
            Mathf.Lerp(regionalPartition, 1.0f - basinMask, 0.35f);
        volcanicPartitionMask = Mathf.Clamp(volcanicPartitionMask, 0.0f, 1.0f);

        return new TerrainHeightLayers(
            landmass,
            regionalPartition,
            regionalRelief,
            basinMask,
            mountainSystemMask,
            mountainSystemLiftMask,
            mountainShoulderMask,
            mountainCoreMask,
            mountainAmplification,
            mountainBackbone,
            secondaryRidges,
            hills,
            localRelief,
            surfaceBreakup,
            lowlandMask,
            heroPeakMask,
            mountainStrength,
            foothillStrength,
            hillStrength,
            localReliefStrength,
            surfaceBreakupStrength,
            canyonIncisionMask,
            swampBasinMask,
            volcanicPartitionMask);
    }

    private float SampleSlope(float worldX, float worldZ)
    {
        const float sampleOffset = TerrainSlopeSampleOffsetMeters;
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
                WaterShelfBlendBase +
                (biome.PlainsWeight * 0.10f) +
                (biome.SwampWeight * 0.16f) -
                (biome.RockyWeight * 0.08f) -
                (biome.CanyonWeight * 0.10f),
                0.0f,
                1.0f);
        terrain = Mathf.Lerp(terrain, _waterLevel + WaterShelfHeightOffsetMeters, shelfBlend);

        float basinDepth =
            WaterBasinDepthMeters +
            (biome.CanyonWeight * 0.70f) -
            (biome.SwampWeight * 0.40f);
        float basinBlend = basinMask * lowlandMask * _waterBasinInfluence * (WaterBasinBlendBase + (submergedMask * 0.18f));
        terrain = Mathf.Lerp(terrain, _waterLevel - basinDepth, basinBlend);

        float swampFlattenBlend = biome.SwampWeight * lowlandMask * (WaterSwampFlattenBlendBase + (shorelineMask * 0.20f));
        terrain = Mathf.Lerp(terrain, _waterLevel - WaterSwampNearWaterOffsetMeters, swampFlattenBlend);

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

    private float SampleCaveContribution(Vector3 worldPosition, float terrain)
    {
        if (_caveScale <= 0.0f)
        {
            return 0.0f;
        }

        float depthBelowSurface = terrain - worldPosition.Y;
        float surfaceFade = Mathf.SmoothStep(CaveSurfaceFadeStartMeters, CaveSurfaceFadeEndMeters, depthBelowSurface);
        if (surfaceFade <= 0.0f)
        {
            return 0.0f;
        }

        Vector2 warped = WarpXZ(worldPosition.X, worldPosition.Z);
        float caveNoise = _caveNoise.GetNoise3D(warped.X, worldPosition.Y, warped.Y);
        float carveSignal = 1.0f - NoiseToUnit(caveNoise);
        float carveMask = Mathf.SmoothStep(_caveThreshold, 1.0f, carveSignal);
        return caveNoise * _caveScale * carveMask * surfaceFade;
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }

    private static VoxelMaterialId ResolveGroundCoverMaterial(
        float depthBelowSurface,
        float slope,
        float normalizedHeight,
        float vegetationSignal,
        TerrainBiomeSample biome)
    {
        float grassCoverage = Mathf.Clamp(
            GrassCoverageBase +
            (vegetationSignal * GrassCoverageVegetationScale) +
            (biome.Moisture * GrassCoverageMoistureScale) -
            (biome.RockyWeight * GrassCoverageRockyPenalty) -
            (biome.CanyonWeight * GrassCoverageCanyonPenalty) -
            (biome.VolcanicWeight * GrassCoverageVolcanicPenalty),
            0.0f,
            1.0f);

        // Dirt now acts as the transition band near rockier, steeper, or slightly cut-in surfaces.
        float soilTransition = Mathf.Clamp(
            (Mathf.SmoothStep(SoilTransitionSlopeStart, SoilTransitionSlopeEnd, slope) * SoilTransitionSlopeScale) +
            (Mathf.SmoothStep(SoilTransitionDepthStart, SoilTransitionDepthEnd, depthBelowSurface) * SoilTransitionDepthScale) +
            (Mathf.SmoothStep(SoilTransitionHeightStart, SoilTransitionHeightEnd, normalizedHeight) * SoilTransitionHeightScale) +
            (biome.RockyWeight * SoilTransitionRockyScale) +
            (biome.CanyonWeight * SoilTransitionCanyonScale) +
            (biome.VolcanicWeight * SoilTransitionVolcanicScale),
            0.0f,
            1.0f);

        return grassCoverage >= soilTransition
            ? VoxelMaterialId.Grass
            : VoxelMaterialId.Soil;
    }

    private float NormalizeTerrainHeight(float terrain)
    {
        return Mathf.Clamp(Mathf.InverseLerp(_surfaceHeightMin, _surfaceHeightMax, terrain), 0.0f, 1.0f);
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

    private static float SampleRidgedFbm2D(
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
            float ridge = 1.0f - Mathf.Abs(noise.GetNoise2D(sampleX, sampleZ));
            ridge *= ridge;
            total += ridge * amplitude;
            amplitudeSum += amplitude;
            amplitude *= gain;
            sampleX *= lacunarity;
            sampleZ *= lacunarity;
        }

        return amplitudeSum > 0.0f
            ? total / amplitudeSum
            : 0.0f;
    }

    private Vector2 WarpXZ(float worldX, float worldZ)
    {
        float warpX = _warpNoiseX.GetNoise2D(worldX, worldZ) * TerrainWarpStrengthMeters;
        float warpZ = _warpNoiseZ.GetNoise2D(worldX, worldZ) * TerrainWarpStrengthMeters;
        return new Vector2(worldX + warpX, worldZ + warpZ);
    }

    private static float FrequencyFromWavelength(float wavelengthMeters)
    {
        return 1.0f / Mathf.Max(1.0f, wavelengthMeters);
    }

    private static float SignedFromUnit(float value)
    {
        return (value * 2.0f) - 1.0f;
    }

    private readonly record struct TerrainHeightLayers(
        float Landmass,
        float RegionalPartition,
        float RegionalRelief,
        float BasinMask,
        float MountainSystemMask,
        float MountainSystemLiftMask,
        float MountainShoulderMask,
        float MountainCoreMask,
        float MountainAmplification,
        float MountainBackbone,
        float SecondaryRidges,
        float Hills,
        float LocalRelief,
        float SurfaceBreakup,
        float LowlandMask,
        float HeroPeakMask,
        float MountainStrength,
        float FoothillStrength,
        float HillStrength,
        float LocalReliefStrength,
        float SurfaceBreakupStrength,
        float CanyonIncisionMask,
        float SwampBasinMask,
        float VolcanicPartitionMask);

    private readonly record struct TerrainHeightDamping(
        float PlainsFlattenMask,
        float SwampSoftenMask,
        float MountainLiftDamping,
        float RidgeDamping,
        float HillDamping,
        float LocalReliefDamping,
        float SurfaceBreakupDamping,
        float ShoulderRidgeBoost,
        float CoreHillDamping,
        float CoreLocalReliefDamping);

    private readonly record struct TerrainHeightComposition(
        float RegionalContribution,
        float MountainLiftContribution,
        float MountainBackboneContribution,
        float HeroPeakContribution,
        float SecondaryRidgeContribution,
        float HillContribution,
        float LocalReliefContribution,
        float SurfaceBreakupContribution,
        float WaterShapingDelta);
}
