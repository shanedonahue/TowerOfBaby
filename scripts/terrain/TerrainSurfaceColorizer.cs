using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainSurfaceColorizer
{
    private const float FlatSlopeStart = 0.10f;
    private const float FlatSlopeEnd = 0.34f;
    private const float LowlandHeightStart = 0.38f;
    private const float LowlandHeightEnd = 0.66f;
    private const float PeakHeightStart = 0.72f;
    private const float PeakHeightEnd = 0.86f;
    private const float DryLandStartAboveWater = 0.15f;
    private const float DryLandEndAboveWater = 1.30f;

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

    private static readonly Color LowlandColor = new(0.44f, 0.62f, 0.31f, 1.0f);
    private static readonly Color SlopeColor = new(0.61f, 0.50f, 0.34f, 1.0f);
    private static readonly Color PeakColor = new(0.94f, 0.95f, 0.97f, 1.0f);
    private static readonly Color ExposedSoilColor = new(0.50f, 0.37f, 0.24f, 1.0f);
    private static readonly Color ExposedRockColor = new(0.54f, 0.53f, 0.50f, 1.0f);
    private static readonly Color ExposedCliffColor = new(0.60f, 0.52f, 0.39f, 1.0f);

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

        VoxelMaterialId[] surfaceMaterials = mesh.HasSurfaceMaterials
            ? mesh.SurfaceMaterials
            : System.Array.Empty<VoxelMaterialId>();
        Color[] materialColors = mesh.HasMaterialColors
            ? mesh.MaterialColors
            : mesh.Colors;
        Color[] litColors = new Color[mesh.Vertices.Length];
        float[] biomeWeights = new float[mesh.Vertices.Length * 4];
        Vector3 origin = data.Origin;
        for (int i = 0; i < litColors.Length; i++)
        {
            Vector3 worldPosition = origin + mesh.Vertices[i];
            TerrainBiomeSample biome = _biomeClassifier.SampleWorldPosition(worldPosition.X, worldPosition.Z);
            VoxelMaterialId materialId = i < surfaceMaterials.Length
                ? surfaceMaterials[i]
                : VoxelMaterialPalette.ResolveMaterial(materialColors[i]);
            litColors[i] = ResolveLitColor(worldPosition, mesh.Normals[i], materialId);
            WriteBiomeWeights(biomeWeights, i * 4, biome);
        }

        return new VoxelMeshBuildResult(
            mesh.Vertices,
            mesh.Normals,
            mesh.Uvs,
            litColors,
            materialColors,
            surfaceMaterials,
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
        VoxelMaterialId materialId)
    {
        Vector3 safeNormal = normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;

        float slope = 1.0f - Mathf.Clamp(safeNormal.Dot(Vector3.Up), 0.0f, 1.0f);
        float flatness = 1.0f - Mathf.SmoothStep(FlatSlopeStart, FlatSlopeEnd, slope);
        float height01 = NormalizeHeight(worldPosition.Y);
        float lowlandWeight = flatness *
            (1.0f - Mathf.SmoothStep(LowlandHeightStart, LowlandHeightEnd, height01)) *
            Mathf.SmoothStep(_waterLevel + DryLandStartAboveWater, _waterLevel + DryLandEndAboveWater, worldPosition.Y);
        float peakWeight = flatness * Mathf.SmoothStep(PeakHeightStart, PeakHeightEnd, height01);
        float slopeWeight = Mathf.Clamp(1.0f - Mathf.Max(lowlandWeight, peakWeight), 0.0f, 1.0f);

        float weightSum = lowlandWeight + slopeWeight + peakWeight;
        if (weightSum <= 0.0001f)
        {
            return EncodeSurfaceColor(VoxelMaterialPalette.GetNeutralColor(materialId), materialId);
        }

        Color terrainPaletteColor = new Color(
            ((LowlandColor.R * lowlandWeight) + (SlopeColor.R * slopeWeight) + (PeakColor.R * peakWeight)) / weightSum,
            ((LowlandColor.G * lowlandWeight) + (SlopeColor.G * slopeWeight) + (PeakColor.G * peakWeight)) / weightSum,
            ((LowlandColor.B * lowlandWeight) + (SlopeColor.B * slopeWeight) + (PeakColor.B * peakWeight)) / weightSum,
            1.0f);
        return ResolveSurfaceColor(terrainPaletteColor, materialId, slope, flatness);
    }

    private static Color ResolveSurfaceColor(
        Color terrainPaletteColor,
        VoxelMaterialId materialId,
        float slope,
        float flatness)
    {
        Color baseMaterialColor = VoxelMaterialPalette.GetNeutralColor(materialId);
        baseMaterialColor = materialId switch
        {
            VoxelMaterialId.Soil => baseMaterialColor.Lerp(ExposedSoilColor, 0.18f + (flatness * 0.04f)),
            VoxelMaterialId.Rock => baseMaterialColor.Lerp(ExposedRockColor, 0.16f + (slope * 0.05f)),
            VoxelMaterialId.Cliff => baseMaterialColor.Lerp(ExposedCliffColor, 0.24f + (slope * 0.05f)),
            _ => baseMaterialColor
        };
        float paletteBlend = materialId switch
        {
            VoxelMaterialId.Grass => 0.18f + (flatness * 0.08f),
            VoxelMaterialId.Soil => 0.10f + (flatness * 0.04f) + (slope * 0.02f),
            VoxelMaterialId.Rock => 0.06f + (slope * 0.05f),
            VoxelMaterialId.Cliff => 0.04f + (slope * 0.04f),
            VoxelMaterialId.Snow => 0.06f,
            VoxelMaterialId.Scorched => 0.02f,
            _ => 0.06f
        };
        return EncodeSurfaceColor(
            baseMaterialColor.Lerp(terrainPaletteColor, Mathf.Clamp(paletteBlend, 0.0f, 0.26f)),
            materialId);
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

    private static Color EncodeSurfaceColor(Color color, VoxelMaterialId materialId)
    {
        return VoxelMaterialPalette.EncodeMaterialColor(
            new Color(
                Mathf.Clamp(color.R, 0.0f, 1.0f),
                Mathf.Clamp(color.G, 0.0f, 1.0f),
                Mathf.Clamp(color.B, 0.0f, 1.0f),
                1.0f),
            materialId);
    }
}
