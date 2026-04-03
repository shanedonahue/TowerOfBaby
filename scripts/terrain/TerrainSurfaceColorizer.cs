using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainSurfaceColorizer
{
    private const float ShadeFactorMin = 0.88f;
    private const float ShadeFactorMax = 1.06f;
    private const float ShadeFactorFlatBonus = 0.02f;
    private const float ShadeFactorPeakBonus = 0.03f;
    private const float ShadeFactorWetPenalty = 0.06f;
    // Keep cliffs distinctly darker than flats for now; revisit these only after an in-engine visual check.
    private const float ShadeFactorCliffBasePenalty = 0.04f;
    private const float ShadeFactorCliffBreakupPenalty = 0.12f;
    private const float PlateauLightBlendStrength = 0.06f;
    private const float SnowDustHighlightBlendStrength = 0.08f;
    private const float ShoreColorBlendStrength = 0.16f;
    private const float SaturationBoostBase = 1.02f;
    private const float DominantBiomeSaturationBoost = 0.06f;
    private const float CanyonSaturationBoost = 0.03f;
    private const float SwampSaturationBoost = 0.02f;
    private const float CliffSaturationBoost = 0.03f;
    private const float PeakSaturationReduction = 0.04f;

    private static readonly Color[] SlopeBandPalette =
    {
        new(0.17f, 0.42f, 0.16f, 1.0f),
        new(0.46f, 0.68f, 0.20f, 1.0f),
        new(0.90f, 0.78f, 0.24f, 1.0f),
        new(0.93f, 0.49f, 0.18f, 1.0f),
        new(0.74f, 0.16f, 0.13f, 1.0f)
    };

    private static readonly Color[] HeightBandPalette =
    {
        new(0.16f, 0.28f, 0.53f, 1.0f),
        new(0.23f, 0.53f, 0.75f, 1.0f),
        new(0.25f, 0.66f, 0.44f, 1.0f),
        new(0.75f, 0.78f, 0.30f, 1.0f),
        new(0.88f, 0.59f, 0.26f, 1.0f),
        new(0.81f, 0.44f, 0.25f, 1.0f),
        new(0.92f, 0.92f, 0.96f, 1.0f)
    };

    private static readonly Color PlainsSurfaceColor = new(0.24f, 0.34f, 0.16f, 1.0f);
    private static readonly Color RockySurfaceColor = new(0.40f, 0.39f, 0.32f, 1.0f);
    private static readonly Color CanyonSurfaceColor = new(0.55f, 0.34f, 0.20f, 1.0f);
    private static readonly Color SwampSurfaceColor = new(0.16f, 0.27f, 0.22f, 1.0f);
    private static readonly Color VolcanicSurfaceColor = new(0.25f, 0.18f, 0.16f, 1.0f);

    private static readonly Color PlainsRockColor = new(0.34f, 0.28f, 0.20f, 1.0f);
    private static readonly Color RockyRockColor = new(0.31f, 0.33f, 0.35f, 1.0f);
    private static readonly Color CanyonRockColor = new(0.43f, 0.31f, 0.24f, 1.0f);
    private static readonly Color SwampRockColor = new(0.22f, 0.27f, 0.24f, 1.0f);
    private static readonly Color VolcanicRockColor = new(0.19f, 0.19f, 0.21f, 1.0f);

    private static readonly Color PlainsAccentColor = new(0.42f, 0.50f, 0.22f, 1.0f);
    private static readonly Color RockyAccentColor = new(0.50f, 0.60f, 0.64f, 1.0f);
    private static readonly Color CanyonAccentColor = new(0.86f, 0.50f, 0.28f, 1.0f);
    private static readonly Color SwampAccentColor = new(0.22f, 0.57f, 0.48f, 1.0f);
    private static readonly Color VolcanicAccentColor = new(0.72f, 0.31f, 0.24f, 1.0f);

    private static readonly Color PlainsWetColor = new(0.20f, 0.29f, 0.18f, 1.0f);
    private static readonly Color RockyWetColor = new(0.29f, 0.37f, 0.34f, 1.0f);
    private static readonly Color CanyonWetColor = new(0.43f, 0.35f, 0.24f, 1.0f);
    private static readonly Color SwampWetColor = new(0.18f, 0.42f, 0.35f, 1.0f);
    private static readonly Color VolcanicWetColor = new(0.22f, 0.23f, 0.25f, 1.0f);

    private static readonly Color HighlandDustColor = new(0.60f, 0.56f, 0.50f, 1.0f);
    private static readonly Color SnowDustColor = new(0.95f, 0.97f, 1.0f, 1.0f);
    private static readonly Color ShoreColor = new(0.63f, 0.57f, 0.42f, 1.0f);
    private static readonly Color MacroWarmColor = new(0.60f, 0.42f, 0.26f, 1.0f);
    private static readonly Color MacroCoolColor = new(0.25f, 0.38f, 0.49f, 1.0f);
    private static readonly Color MacroLushColor = new(0.20f, 0.38f, 0.26f, 1.0f);
    private static readonly Color MacroDryColor = new(0.58f, 0.42f, 0.24f, 1.0f);
    private static readonly Color SlopeMeadowColor = new(0.56f, 0.66f, 0.31f, 1.0f);
    private static readonly Color WetLowlandColor = new(0.22f, 0.39f, 0.34f, 1.0f);
    private static readonly Color CliffShadowColor = new(0.15f, 0.17f, 0.19f, 1.0f);
    private static readonly Color CliffHighlightColor = new(0.42f, 0.38f, 0.34f, 1.0f);
    private static readonly Color PeakRockColor = new(0.62f, 0.64f, 0.67f, 1.0f);
    private static readonly Color PeakSnowColor = new(0.93f, 0.95f, 0.97f, 1.0f);
    private static readonly Color FlatWarmLightColor = new(0.72f, 0.67f, 0.56f, 1.0f);

    private readonly int _seed;
    private readonly float _terrainHeight;
    private readonly float _detailHeight;
    private readonly float _caveScale;
    private readonly float _caveThreshold;
    private readonly float _waterLevel;
    private readonly float _shorelineFalloff;
    private readonly float _waterBasinInfluence;
    private readonly float _baseY;
    private readonly TerrainBiomeClassifier _biomeClassifier;
    private readonly FastNoiseLite _macroTintNoise;
    private readonly FastNoiseLite _macroShadeNoise;
    private readonly FastNoiseLite _macroBiomeNoise;
    private readonly FastNoiseLite _macroAccentNoise;
    private readonly FastNoiseLite _wetPatchNoise;
    private readonly FastNoiseLite _cliffBreakupNoise;
    private VoxelFieldGenerator _fieldGenerator = null!;

    public TerrainSurfaceColorizer(TerrainConfig config)
    {
        _seed = config.Seed;
        _terrainHeight = Mathf.Max(1.0f, config.TerrainHeight);
        _detailHeight = config.DetailHeight;
        _caveScale = config.CaveScale;
        _caveThreshold = config.CaveThreshold;
        _waterLevel = config.WaterLevel;
        _shorelineFalloff = Mathf.Max(0.4f, config.ShorelineFalloff);
        _waterBasinInfluence = config.WaterBasinInfluence;
        _baseY = config.BaseY;

        _biomeClassifier = new TerrainBiomeClassifier(_seed);
        _macroTintNoise = new FastNoiseLite
        {
            Seed = _seed + 641,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00185f
        };
        _macroShadeNoise = new FastNoiseLite
        {
            Seed = _seed + 683,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0036f
        };
        _macroBiomeNoise = new FastNoiseLite
        {
            Seed = _seed + 719,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00105f
        };
        _macroAccentNoise = new FastNoiseLite
        {
            Seed = _seed + 743,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0064f
        };
        _wetPatchNoise = new FastNoiseLite
        {
            Seed = _seed + 761,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0044f
        };
        _cliffBreakupNoise = new FastNoiseLite
        {
            Seed = _seed + 797,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.017f
        };
    }

    public VoxelMeshBuildResult BuildLitMesh(VoxelMeshBuildResult mesh, VoxelChunkData data)
    {
        if (!mesh.HasGeometry)
        {
            return mesh;
        }

        Color[] litColors = new Color[mesh.Colors.Length];
        Vector3 origin = data.Origin;
        for (int i = 0; i < litColors.Length; i++)
        {
            Vector3 worldPosition = origin + mesh.Vertices[i];
            litColors[i] = ResolveLitColor(worldPosition, mesh.Normals[i], mesh.Colors[i]);
        }

        return new VoxelMeshBuildResult(
            mesh.Vertices,
            mesh.Normals,
            mesh.Uvs,
            litColors,
            mesh.Tangents,
            mesh.NormalDebugMismatchCount,
            mesh.TotalTriangleCount,
            mesh.UsedDetailBrick,
            mesh.UsedPersistentDetailEdits,
            mesh.DetailTriangleCount,
            mesh.ReplacedCoarseCellCount,
            mesh.DetailCellCount);
    }

    public Color ResolveDebugColor(
        TerrainVisualDebugMode mode,
        Vector3 worldPosition,
        Vector3 normal,
        Color litColor)
    {
        return mode switch
        {
            TerrainVisualDebugMode.FinalBiomeColor => GetFieldGenerator().SampleBiome(worldPosition.X, worldPosition.Z).DebugColor,
            TerrainVisualDebugMode.SlopeBands => ResolveSlopeBandColor(normal),
            TerrainVisualDebugMode.HeightBands => ResolveHeightBandColor(worldPosition.Y),
            TerrainVisualDebugMode.MountainRangeMask => ResolveMountainRangeMaskColor(worldPosition),
            TerrainVisualDebugMode.WaterShoreMask => ResolveWaterShoreMaskColor(worldPosition),
            TerrainVisualDebugMode.Normals => ResolveNormalColor(normal),
            _ => litColor
        };
    }

    private Color ResolveLitColor(Vector3 worldPosition, Vector3 normal, Color baseMaterialColor)
    {
        Vector3 safeNormal = normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;
        float slope = 1.0f - Mathf.Clamp(safeNormal.Dot(Vector3.Up), 0.0f, 1.0f);
        float flatBlend = 1.0f - Mathf.SmoothStep(0.10f, 0.34f, slope);
        float slopeBlend = Mathf.SmoothStep(0.08f, 0.42f, slope) *
                           (1.0f - (Mathf.SmoothStep(0.34f, 0.72f, slope) * 0.45f));
        float cliffBlend = Mathf.SmoothStep(0.24f, 0.72f, slope);
        float sheerBlend = Mathf.SmoothStep(0.44f, 0.88f, slope);
        float height01 = NormalizeHeight(worldPosition.Y);
        float lowlandBlend = 1.0f - Mathf.SmoothStep(0.16f, 0.42f, height01);
        float uplandBlend = Mathf.SmoothStep(0.56f, 0.84f, height01);
        float peakBlend = Mathf.SmoothStep(0.76f, 0.96f, height01);
        float snowDustBlend = peakBlend * flatBlend;
        float shoreBlend = (1.0f - Mathf.SmoothStep(
            _shorelineFalloff * 0.35f,
            _shorelineFalloff * 1.65f,
            Mathf.Abs(worldPosition.Y - _waterLevel))) * flatBlend;

        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldPosition.X, worldPosition.Z);
        float dominantWeight = GetDominantBiomeWeight(biome);
        float macroTint = NoiseToUnit(_macroTintNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float macroShade = NoiseToUnit(_macroShadeNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float macroBiome = NoiseToUnit(_macroBiomeNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float macroAccent = NoiseToUnit(_macroAccentNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float wetPatch = NoiseToUnit(_wetPatchNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float cliffBreakup = NoiseToUnit(_cliffBreakupNoise.GetNoise3D(
            worldPosition.X,
            worldPosition.Y * 1.8f,
            worldPosition.Z));

        float wetLowlandBlend = flatBlend * Mathf.Clamp(
            (lowlandBlend * (0.28f + (biome.Moisture * 0.36f) + (biome.SwampWeight * 0.34f))) +
            (shoreBlend * 0.55f) +
            (wetPatch * lowlandBlend * 0.12f),
            0.0f,
            1.0f);

        Color biomeSurfaceColor = BuildBiomeSurfaceColor(biome);
        Color biomeRockColor = BuildBiomeRockColor(biome);
        Color biomeAccentColor = BuildBiomeAccentColor(biome);
        Color biomeWetColor = BuildBiomeWetColor(biome);
        Color macroGroundTint = MacroCoolColor.Lerp(MacroWarmColor, macroTint);
        Color macroRegionTint = MacroLushColor.Lerp(MacroDryColor, macroBiome);
        Color cliffStrataColor = CliffShadowColor.Lerp(
            CliffHighlightColor,
            Mathf.SmoothStep(0.18f, 0.86f, cliffBreakup));

        Color flatColor = baseMaterialColor.Lerp(biomeSurfaceColor, 0.58f);
        flatColor = flatColor.Lerp(biomeAccentColor, 0.18f + (macroBiome * 0.12f));
        flatColor = flatColor.Lerp(macroGroundTint, 0.10f + (flatBlend * 0.08f));
        flatColor = flatColor.Lerp(biome.DebugColor, dominantWeight * 0.10f);

        Color slopeColor = baseMaterialColor.Lerp(
            biomeSurfaceColor.Lerp(biomeRockColor, 0.42f + (biome.Ruggedness * 0.20f)),
            0.72f);
        slopeColor = slopeColor.Lerp(
            SlopeMeadowColor,
            biome.PlainsWeight * 0.18f * (1.0f - cliffBlend));
        slopeColor = slopeColor.Lerp(biomeAccentColor, 0.12f + (macroAccent * 0.08f));
        slopeColor = slopeColor.Lerp(macroRegionTint, 0.08f);

        Color cliffColor = baseMaterialColor.Lerp(biomeRockColor, 0.84f);
        cliffColor = cliffColor.Lerp(CliffShadowColor, 0.28f + (sheerBlend * 0.18f));
        cliffColor = cliffColor.Lerp(cliffStrataColor, 0.22f + (cliffBlend * 0.18f));
        cliffColor = cliffColor.Lerp(
            biomeAccentColor,
            (biome.CanyonWeight * 0.12f) + (biome.VolcanicWeight * 0.10f));
        cliffColor = cliffColor.Lerp(PeakRockColor, uplandBlend * (0.08f + (peakBlend * 0.18f)));

        Color wetColor = biomeSurfaceColor.Lerp(biomeWetColor, 0.68f);
        wetColor = wetColor.Lerp(WetLowlandColor, 0.26f + (wetPatch * 0.18f));
        wetColor = wetColor.Lerp(MacroCoolColor, 0.18f + (biome.Moisture * 0.12f));
        wetColor = wetColor.Lerp(ShoreColor, shoreBlend * 0.12f);

        Color peakColor = biomeRockColor.Lerp(PeakRockColor, 0.42f + (peakBlend * 0.18f));
        peakColor = peakColor.Lerp(HighlandDustColor, uplandBlend * 0.22f);
        peakColor = peakColor.Lerp(PeakSnowColor, snowDustBlend * 0.72f);

        Color color = flatColor;
        color = color.Lerp(slopeColor, slopeBlend * 0.82f);
        color = color.Lerp(cliffColor, cliffBlend);
        color = color.Lerp(wetColor, wetLowlandBlend * (0.60f + (wetPatch * 0.25f)));
        color = color.Lerp(ShoreColor, shoreBlend * ShoreColorBlendStrength);
        color = color.Lerp(HighlandDustColor, uplandBlend * 0.18f);
        color = color.Lerp(peakColor, peakBlend * (0.35f + (flatBlend * 0.20f)));

        Color macroCompositeTint = macroGroundTint.Lerp(macroRegionTint, 0.5f + (macroAccent * 0.2f));
        color = color.Lerp(macroCompositeTint, 0.06f + (flatBlend * 0.08f) + (cliffBlend * 0.03f));

        float shadeFactor = Mathf.Lerp(ShadeFactorMin, ShadeFactorMax, macroShade);
        shadeFactor += flatBlend * ShadeFactorFlatBonus;
        shadeFactor += peakBlend * ShadeFactorPeakBonus;
        shadeFactor -= wetLowlandBlend * ShadeFactorWetPenalty;
        shadeFactor -= cliffBlend * (ShadeFactorCliffBasePenalty + ((1.0f - cliffBreakup) * ShadeFactorCliffBreakupPenalty));
        color = ScaleColor(color, shadeFactor);

        float plateauLift = flatBlend * (0.06f + (uplandBlend * 0.06f));
        color = color.Lerp(FlatWarmLightColor, plateauLift * PlateauLightBlendStrength);
        color = color.Lerp(PeakSnowColor, snowDustBlend * SnowDustHighlightBlendStrength);

        float saturationBoost = SaturationBoostBase +
                                (dominantWeight * DominantBiomeSaturationBoost) +
                                (biome.CanyonWeight * CanyonSaturationBoost) +
                                (biome.SwampWeight * SwampSaturationBoost) +
                                (cliffBlend * CliffSaturationBoost) -
                                (peakBlend * PeakSaturationReduction);
        color = SaturateColor(color, saturationBoost);
        return ClampColor(color);
    }

    private static Color ResolveSlopeBandColor(Vector3 normal)
    {
        Vector3 safeNormal = normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;
        float slope = 1.0f - Mathf.Clamp(safeNormal.Dot(Vector3.Up), 0.0f, 1.0f);
        int bandIndex = Mathf.Clamp(
            Mathf.FloorToInt(slope * SlopeBandPalette.Length),
            0,
            SlopeBandPalette.Length - 1);
        return SlopeBandPalette[bandIndex];
    }

    private Color ResolveHeightBandColor(float worldY)
    {
        float normalizedHeight = NormalizeHeight(worldY);
        float scaledBand = normalizedHeight * HeightBandPalette.Length;
        int bandIndex = Mathf.Clamp(
            Mathf.FloorToInt(scaledBand),
            0,
            HeightBandPalette.Length - 1);
        float bandBlend = scaledBand - Mathf.Floor(scaledBand);
        return HeightBandPalette[bandIndex].Lerp(Colors.White, Mathf.SmoothStep(0.94f, 1.0f, bandBlend) * 0.12f);
    }

    private Color ResolveMountainRangeMaskColor(Vector3 worldPosition)
    {
        TerrainMountainRangeDebugSample sample = GetFieldGenerator().SampleMountainRangeDebug(worldPosition.X, worldPosition.Z);
        Color foothills = new(0.15f, 0.18f, 0.20f, 1.0f);
        Color ranges = new(0.84f, 0.61f, 0.20f, 1.0f);
        Color peaks = new(0.98f, 0.96f, 0.84f, 1.0f);
        return foothills
            .Lerp(ranges, sample.ShoulderMask)
            .Lerp(peaks, sample.PeakMask * 0.85f);
    }

    private Color ResolveWaterShoreMaskColor(Vector3 worldPosition)
    {
        TerrainWaterDebugSample sample = GetFieldGenerator().SampleWaterDebug(worldPosition.X, worldPosition.Z);
        Color dry = new(0.14f, 0.14f, 0.15f, 1.0f);
        Color shore = new(0.93f, 0.82f, 0.43f, 1.0f);
        Color shallow = new(0.22f, 0.69f, 0.92f, 1.0f);
        Color deep = new(0.08f, 0.20f, 0.48f, 1.0f);
        Color color = dry.Lerp(shore, sample.ShoreMask);
        color = color.Lerp(shallow, sample.WaterMask * 0.75f);
        color = color.Lerp(deep, sample.WaterMask * (0.45f + (sample.BasinMask * 0.55f)));
        return color;
    }

    private static Color ResolveNormalColor(Vector3 normal)
    {
        Vector3 safeNormal = normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;
        return new Color(
            (safeNormal.X * 0.5f) + 0.5f,
            (safeNormal.Y * 0.5f) + 0.5f,
            (safeNormal.Z * 0.5f) + 0.5f,
            1.0f);
    }

    private float NormalizeHeight(float worldY)
    {
        float minHeight = Mathf.Min(_baseY, _waterLevel - (_shorelineFalloff * 1.25f));
        float maxHeight = _waterLevel + (_terrainHeight * 1.65f);
        return Mathf.Clamp((worldY - minHeight) / Mathf.Max(1.0f, maxHeight - minHeight), 0.0f, 1.0f);
    }

    private static Color BuildBiomeSurfaceColor(TerrainBiomeSample biome) => BlendBiomeColor(
        biome,
        PlainsSurfaceColor,
        RockySurfaceColor,
        CanyonSurfaceColor,
        SwampSurfaceColor,
        VolcanicSurfaceColor);

    private static Color BuildBiomeRockColor(TerrainBiomeSample biome) => BlendBiomeColor(
        biome,
        PlainsRockColor,
        RockyRockColor,
        CanyonRockColor,
        SwampRockColor,
        VolcanicRockColor);

    private static Color BuildBiomeAccentColor(TerrainBiomeSample biome) => BlendBiomeColor(
        biome,
        PlainsAccentColor,
        RockyAccentColor,
        CanyonAccentColor,
        SwampAccentColor,
        VolcanicAccentColor);

    private static Color BuildBiomeWetColor(TerrainBiomeSample biome) => BlendBiomeColor(
        biome,
        PlainsWetColor,
        RockyWetColor,
        CanyonWetColor,
        SwampWetColor,
        VolcanicWetColor);

    private static Color BlendBiomeColor(
        TerrainBiomeSample biome,
        Color plainsColor,
        Color rockyColor,
        Color canyonColor,
        Color swampColor,
        Color volcanicColor)
    {
        return new Color(
            (plainsColor.R * biome.PlainsWeight) +
            (rockyColor.R * biome.RockyWeight) +
            (canyonColor.R * biome.CanyonWeight) +
            (swampColor.R * biome.SwampWeight) +
            (volcanicColor.R * biome.VolcanicWeight),
            (plainsColor.G * biome.PlainsWeight) +
            (rockyColor.G * biome.RockyWeight) +
            (canyonColor.G * biome.CanyonWeight) +
            (swampColor.G * biome.SwampWeight) +
            (volcanicColor.G * biome.VolcanicWeight),
            (plainsColor.B * biome.PlainsWeight) +
            (rockyColor.B * biome.RockyWeight) +
            (canyonColor.B * biome.CanyonWeight) +
            (swampColor.B * biome.SwampWeight) +
            (volcanicColor.B * biome.VolcanicWeight),
            1.0f);
    }

    private static float GetDominantBiomeWeight(TerrainBiomeSample biome)
    {
        return biome.DominantBiome switch
        {
            BiomeId.Plains => biome.PlainsWeight,
            BiomeId.Rocky => biome.RockyWeight,
            BiomeId.Canyon => biome.CanyonWeight,
            BiomeId.Swamp => biome.SwampWeight,
            BiomeId.Volcanic => biome.VolcanicWeight,
            _ => 0.0f
        };
    }

    private VoxelFieldGenerator GetFieldGenerator()
    {
        return _fieldGenerator ??= new VoxelFieldGenerator(
            _seed,
            _terrainHeight,
            _detailHeight,
            _caveScale,
            _caveThreshold,
            _waterLevel,
            _shorelineFalloff,
            _waterBasinInfluence);
    }

    private static Color ScaleColor(Color color, float factor)
    {
        return new Color(
            color.R * factor,
            color.G * factor,
            color.B * factor,
            color.A);
    }

    private static Color ClampColor(Color color)
    {
        return new Color(
            Mathf.Clamp(color.R, 0.0f, 1.0f),
            Mathf.Clamp(color.G, 0.0f, 1.0f),
            Mathf.Clamp(color.B, 0.0f, 1.0f),
            Mathf.Clamp(color.A, 0.0f, 1.0f));
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }

    private static Color SaturateColor(Color color, float saturation)
    {
        float grayscale = (color.R + color.G + color.B) / 3.0f;
        return new Color(
            grayscale + ((color.R - grayscale) * saturation),
            grayscale + ((color.G - grayscale) * saturation),
            grayscale + ((color.B - grayscale) * saturation),
            color.A);
    }
}
