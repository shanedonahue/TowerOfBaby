using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainSurfaceColorizer
{
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

    private static readonly Color PlainsSurfaceColor = new(0.44f, 0.57f, 0.31f, 1.0f);
    private static readonly Color RockySurfaceColor = new(0.53f, 0.53f, 0.50f, 1.0f);
    private static readonly Color CanyonSurfaceColor = new(0.67f, 0.42f, 0.24f, 1.0f);
    private static readonly Color SwampSurfaceColor = new(0.33f, 0.45f, 0.28f, 1.0f);
    private static readonly Color VolcanicSurfaceColor = new(0.41f, 0.25f, 0.22f, 1.0f);

    private static readonly Color PlainsRockColor = new(0.47f, 0.47f, 0.43f, 1.0f);
    private static readonly Color RockyRockColor = new(0.45f, 0.47f, 0.51f, 1.0f);
    private static readonly Color CanyonRockColor = new(0.61f, 0.49f, 0.37f, 1.0f);
    private static readonly Color SwampRockColor = new(0.43f, 0.47f, 0.40f, 1.0f);
    private static readonly Color VolcanicRockColor = new(0.31f, 0.29f, 0.29f, 1.0f);

    private static readonly Color HighlandDustColor = new(0.76f, 0.77f, 0.75f, 1.0f);
    private static readonly Color SnowDustColor = new(0.88f, 0.89f, 0.91f, 1.0f);
    private static readonly Color ShoreColor = new(0.64f, 0.56f, 0.38f, 1.0f);
    private static readonly Color MacroWarmColor = new(0.64f, 0.56f, 0.40f, 1.0f);
    private static readonly Color MacroCoolColor = new(0.38f, 0.46f, 0.55f, 1.0f);

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
        float groundBlend = 1.0f - Mathf.SmoothStep(0.14f, 0.52f, slope);
        float steepBlend = Mathf.SmoothStep(0.18f, 0.62f, slope);
        float cliffBlend = Mathf.SmoothStep(0.30f, 0.80f, slope);
        float height01 = NormalizeHeight(worldPosition.Y);
        float highlandBlend = Mathf.SmoothStep(0.58f, 0.88f, height01);
        float snowDustBlend = Mathf.SmoothStep(0.82f, 0.96f, height01) * groundBlend;
        float shoreBlend = (1.0f - Mathf.SmoothStep(
            _shorelineFalloff * 0.35f,
            _shorelineFalloff * 1.15f,
            Mathf.Abs(worldPosition.Y - _waterLevel))) * groundBlend;

        TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldPosition.X, worldPosition.Z);
        Color biomeSurfaceColor = BuildBiomeSurfaceColor(biome);
        Color biomeRockColor = BuildBiomeRockColor(biome);
        Color color = baseMaterialColor.Lerp(
            biomeSurfaceColor,
            Mathf.Clamp(0.34f + (groundBlend * 0.18f) + (biome.VolcanicWeight * 0.06f), 0.0f, 0.60f));

        color = color.Lerp(
            biomeRockColor,
            Mathf.Clamp((steepBlend * 0.46f) + (cliffBlend * 0.34f), 0.0f, 0.82f));
        color = color.Lerp(ShoreColor, shoreBlend * 0.18f);
        color = color.Lerp(HighlandDustColor, highlandBlend * 0.16f);
        color = color.Lerp(SnowDustColor, snowDustBlend * 0.48f);

        float macroTint = NoiseToUnit(_macroTintNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float macroShade = NoiseToUnit(_macroShadeNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        Color macroTarget = MacroCoolColor.Lerp(MacroWarmColor, macroTint);
        color = color.Lerp(macroTarget, 0.08f + (groundBlend * 0.04f));
        color = ScaleColor(color, Mathf.Lerp(0.94f, 1.07f, macroShade));

        float flatLift = Mathf.SmoothStep(0.0f, 0.18f, groundBlend) * 0.08f;
        color = color.Lerp(SnowDustColor, flatLift * 0.12f);
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

    private static Color BuildBiomeSurfaceColor(TerrainBiomeSample biome)
    {
        return new Color(
            (PlainsSurfaceColor.R * biome.PlainsWeight) +
            (RockySurfaceColor.R * biome.RockyWeight) +
            (CanyonSurfaceColor.R * biome.CanyonWeight) +
            (SwampSurfaceColor.R * biome.SwampWeight) +
            (VolcanicSurfaceColor.R * biome.VolcanicWeight),
            (PlainsSurfaceColor.G * biome.PlainsWeight) +
            (RockySurfaceColor.G * biome.RockyWeight) +
            (CanyonSurfaceColor.G * biome.CanyonWeight) +
            (SwampSurfaceColor.G * biome.SwampWeight) +
            (VolcanicSurfaceColor.G * biome.VolcanicWeight),
            (PlainsSurfaceColor.B * biome.PlainsWeight) +
            (RockySurfaceColor.B * biome.RockyWeight) +
            (CanyonSurfaceColor.B * biome.CanyonWeight) +
            (SwampSurfaceColor.B * biome.SwampWeight) +
            (VolcanicSurfaceColor.B * biome.VolcanicWeight),
            1.0f);
    }

    private static Color BuildBiomeRockColor(TerrainBiomeSample biome)
    {
        return new Color(
            (PlainsRockColor.R * biome.PlainsWeight) +
            (RockyRockColor.R * biome.RockyWeight) +
            (CanyonRockColor.R * biome.CanyonWeight) +
            (SwampRockColor.R * biome.SwampWeight) +
            (VolcanicRockColor.R * biome.VolcanicWeight),
            (PlainsRockColor.G * biome.PlainsWeight) +
            (RockyRockColor.G * biome.RockyWeight) +
            (CanyonRockColor.G * biome.CanyonWeight) +
            (SwampRockColor.G * biome.SwampWeight) +
            (VolcanicRockColor.G * biome.VolcanicWeight),
            (PlainsRockColor.B * biome.PlainsWeight) +
            (RockyRockColor.B * biome.RockyWeight) +
            (CanyonRockColor.B * biome.CanyonWeight) +
            (SwampRockColor.B * biome.SwampWeight) +
            (VolcanicRockColor.B * biome.VolcanicWeight),
            1.0f);
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
}
