using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainSurfaceColorizer
{
    private const float HeightTintStrength = 0.055f;
    private const float LowlandTintStrength = 0.045f;
    private const float ShoreTintStrength = 0.085f;
    private const float ShoreBrightenStrength = 0.10f;
    private const float BiomeTintStrength = 0.075f;
    private const float SlopeDarkeningMax = 0.085f;
    private const bool EnableMeadowBoost = true;
    private const float MeadowTintStrength = 0.16f;
    private const float MeadowSaturationBoost = 0.12f;

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

    private static readonly Color WarmLowHeightTint = new(0.78f, 0.69f, 0.57f, 1.0f);
    private static readonly Color CoolHighHeightTint = new(0.72f, 0.82f, 0.93f, 1.0f);
    private static readonly Color LowlandTint = new(0.71f, 0.79f, 0.60f, 1.0f);
    private static readonly Color ShoreTint = new(0.91f, 0.85f, 0.69f, 1.0f);
    private static readonly Color MeadowBoostColor = new(0.56f, 0.81f, 0.34f, 1.0f);

    private static readonly Color PlainsBiomeTint = new(0.59f, 0.77f, 0.45f, 1.0f);
    private static readonly Color RockyBiomeTint = new(0.66f, 0.69f, 0.72f, 1.0f);
    private static readonly Color CanyonBiomeTint = new(0.87f, 0.64f, 0.44f, 1.0f);
    private static readonly Color SwampBiomeTint = new(0.47f, 0.72f, 0.63f, 1.0f);
    private static readonly Color VolcanicBiomeTint = new(0.77f, 0.58f, 0.54f, 1.0f);

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
    }

    public VoxelMeshBuildResult BuildLitMesh(VoxelMeshBuildResult mesh, VoxelChunkData data)
    {
        if (!mesh.HasGeometry)
        {
            return mesh;
        }

        Color[] baseMaterialColors = mesh.HasMaterialColors
            ? mesh.MaterialColors
            : mesh.Colors;
        Color[] litColors = new Color[mesh.Vertices.Length];
        float[] biomeWeights = new float[mesh.Vertices.Length * 4];
        Vector3 origin = data.Origin;
        for (int i = 0; i < litColors.Length; i++)
        {
            Vector3 worldPosition = origin + mesh.Vertices[i];
            TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldPosition.X, worldPosition.Z);
            litColors[i] = ResolveLitColor(worldPosition, mesh.Normals[i], baseMaterialColors[i], biome);
            WriteBiomeWeights(biomeWeights, i * 4, biome);
        }

        return new VoxelMeshBuildResult(
            mesh.Vertices,
            mesh.Normals,
            mesh.Uvs,
            litColors,
            baseMaterialColors,
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

    private Color ResolveLitColor(
        Vector3 worldPosition,
        Vector3 normal,
        Color baseMaterialColor,
        TerrainBiomeSample biome)
    {
        Vector3 safeNormal = normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;

        float slope = 1.0f - Mathf.Clamp(safeNormal.Dot(Vector3.Up), 0.0f, 1.0f);
        float flatness = 1.0f - Mathf.SmoothStep(0.10f, 0.42f, slope);
        float slopeDarkening = Mathf.SmoothStep(0.12f, 0.78f, slope);
        float height01 = NormalizeHeight(worldPosition.Y);
        float lowlandBlend = 1.0f - Mathf.SmoothStep(0.16f, 0.42f, height01);
        float shoreBlend = (1.0f - Mathf.SmoothStep(
            _shorelineFalloff * 0.35f,
            _shorelineFalloff * 1.7f,
            Mathf.Abs(worldPosition.Y - _waterLevel))) * (0.45f + (flatness * 0.55f));

        float dominantWeight = GetDominantBiomeWeight(biome);
        Color color = baseMaterialColor;

        Color heightTint = WarmLowHeightTint.Lerp(CoolHighHeightTint, height01);
        color = color.Lerp(heightTint, HeightTintStrength);
        color = color.Lerp(LowlandTint, lowlandBlend * LowlandTintStrength);
        color = color.Lerp(ShoreTint, shoreBlend * ShoreTintStrength);
        color = color.Lerp(
            BuildBiomeHueTint(biome),
            (0.35f + (dominantWeight * 0.65f)) * BiomeTintStrength);

        color = ScaleColor(color, 1.0f - (slopeDarkening * SlopeDarkeningMax));
        color = color.Lerp(Colors.White, shoreBlend * ShoreBrightenStrength);

        float meadowBlend = EnableMeadowBoost
            ? ComputeMeadowBoost(flatness, shoreBlend, biome)
            : 0.0f;
        if (meadowBlend > 0.0f)
        {
            color = color.Lerp(MeadowBoostColor, meadowBlend * MeadowTintStrength);
            color = SaturateColor(color, 1.0f + (meadowBlend * MeadowSaturationBoost));
        }

        return ClampColor(color);
    }

    private static float ComputeMeadowBoost(float flatness, float shoreBlend, TerrainBiomeSample biome)
    {
        float plainsBlend = Mathf.SmoothStep(0.35f, 0.85f, biome.PlainsWeight);
        float moistureBlend = Mathf.SmoothStep(0.38f, 0.72f, biome.Moisture);
        float dryPenalty = 1.0f - Mathf.SmoothStep(0.48f, 0.92f, biome.Ruggedness);
        float shorePenalty = 1.0f - (shoreBlend * 0.35f);
        return Mathf.Clamp(flatness * plainsBlend * moistureBlend * dryPenalty * shorePenalty, 0.0f, 1.0f);
    }

    private static void WriteBiomeWeights(float[] destination, int offset, TerrainBiomeSample biome)
    {
        destination[offset] = biome.PlainsWeight;
        destination[offset + 1] = biome.RockyWeight;
        destination[offset + 2] = biome.CanyonWeight;
        destination[offset + 3] = biome.SwampWeight;
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

    private static Color BuildBiomeHueTint(TerrainBiomeSample biome) => BlendBiomeColor(
        biome,
        PlainsBiomeTint,
        RockyBiomeTint,
        CanyonBiomeTint,
        SwampBiomeTint,
        VolcanicBiomeTint);

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
