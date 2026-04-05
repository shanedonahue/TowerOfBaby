using Godot;

namespace TowerOfBaby.Terrain;

public sealed class TerrainBiomeClassifier
{
    private const float DomainWarpFrequency = 0.0100f;
    private const float DomainWarpStrength = 50.0f;

    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _ruggedNoise;
    private readonly FastNoiseLite _activityNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;
    private readonly FastNoiseLite _macroFertilityNoise;
    private readonly FastNoiseLite _macroAridityNoise;

    public TerrainBiomeClassifier(int seed)
    {
        _temperatureNoise = new FastNoiseLite
        {
            Seed = seed + 401,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0038f
        };

        _moistureNoise = new FastNoiseLite
        {
            Seed = seed + 433,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.0032f
        };

        _ruggedNoise = new FastNoiseLite
        {
            Seed = seed + 467,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 0.0062f
        };

        _activityNoise = new FastNoiseLite
        {
            Seed = seed + 503,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            Frequency = 0.0051f
        };

        _warpNoiseX = new FastNoiseLite
        {
            Seed = seed + 557,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = DomainWarpFrequency
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 593,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = DomainWarpFrequency
        };

        _macroFertilityNoise = new FastNoiseLite
        {
            Seed = seed + 619,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.00105f
        };

        _macroAridityNoise = new FastNoiseLite
        {
            Seed = seed + 653,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Frequency = 0.00118f
        };
    }

    public TerrainBiomeSample SampleChunk(Vector3I chunkKey, TerrainWorldSettings settings)
    {
        return SampleColumn(new Vector2I(chunkKey.X, chunkKey.Z), settings);
    }

    public TerrainBiomeSample SampleColumn(Vector2I columnKey, TerrainWorldSettings settings)
    {
        Vector3 worldCenter = new(
            (columnKey.X + 0.5f) * settings.ChunkSize,
            settings.BaseY,
            (columnKey.Y + 0.5f) * settings.ChunkSize);
        return SampleWorldPosition(worldCenter);
    }

    public TerrainBiomeSample SampleWorldPosition(Vector3 worldPosition)
    {
        return SampleWorldPosition(worldPosition.X, worldPosition.Z);
    }

    public TerrainBiomeSample SampleWorldPosition(float worldX, float worldZ)
    {
        float sampleX = worldX + (_warpNoiseX.GetNoise2D(worldX, worldZ) * DomainWarpStrength);
        float sampleZ = worldZ + (_warpNoiseZ.GetNoise2D(worldX, worldZ) * DomainWarpStrength);

        float heat = NoiseToUnit(_temperatureNoise.GetNoise2D(sampleX, sampleZ));
        float moisture = NoiseToUnit(_moistureNoise.GetNoise2D(sampleX, sampleZ));
        float ruggedness = Mathf.Pow(Mathf.Abs(_ruggedNoise.GetNoise2D(sampleX, sampleZ)), 0.82f);
        float activity = NoiseToUnit(_activityNoise.GetNoise2D(sampleX, sampleZ));
        float fertilityRegionNoise = NoiseToUnit(_macroFertilityNoise.GetNoise2D(sampleX, sampleZ));
        float aridityRegionNoise = NoiseToUnit(_macroAridityNoise.GetNoise2D(sampleX, sampleZ));

        float lowRuggedness = 1.0f - ruggedness;
        float dry = 1.0f - moisture;
        float fertileLowlandRegion =
            Mathf.SmoothStep(0.46f, 0.78f, fertilityRegionNoise) *
            (0.35f + (lowRuggedness * 0.65f));
        float aridUplandRegion =
            Mathf.SmoothStep(0.44f, 0.78f, aridityRegionNoise) *
            (0.35f + (dry * 0.65f));

        float climateMoisture = Mathf.Clamp(
            moisture +
            (fertileLowlandRegion * 0.18f) -
            (aridUplandRegion * 0.20f),
            0.0f,
            1.0f);
        float climateHeat = Mathf.Clamp(
            heat +
            (aridUplandRegion * 0.10f) -
            (fertileLowlandRegion * 0.05f),
            0.0f,
            1.0f);

        float temperateBand = Mathf.Clamp(1.0f - (Mathf.Abs(climateHeat - 0.55f) * 1.75f), 0.0f, 1.0f);
        dry = 1.0f - climateMoisture;
        float ruggedRegion = aridUplandRegion * (0.30f + (ruggedness * 0.70f));
        float plainsBias = Mathf.Clamp(0.78f + (fertileLowlandRegion * 0.92f) - (ruggedRegion * 0.18f), 0.25f, 1.90f);
        float rockyBias = Mathf.Clamp(0.82f + (ruggedRegion * 0.88f) - (fertileLowlandRegion * 0.18f), 0.30f, 2.05f);
        float canyonBias = Mathf.Clamp(0.74f + (aridUplandRegion * 1.10f) - (fertileLowlandRegion * 0.16f), 0.25f, 2.10f);
        float swampBias = Mathf.Clamp(0.72f + (fertileLowlandRegion * 0.80f) - (aridUplandRegion * 0.34f), 0.20f, 1.90f);
        float volcanicBias = Mathf.Clamp(0.86f + (ruggedRegion * 0.18f) + (aridUplandRegion * 0.14f), 0.35f, 1.50f);

        float plainsWeight =
            lowRuggedness *
            (0.35f + (climateMoisture * 0.65f)) *
            (0.30f + (temperateBand * 0.70f)) *
            plainsBias;

        float rockyWeight =
            ruggedness *
            (0.45f + (dry * 0.55f)) *
            (1.0f - (activity * 0.45f)) *
            rockyBias;

        float canyonWeight =
            ruggedness *
            dry *
            (0.30f + (climateHeat * 0.70f)) *
            canyonBias;

        float swampWeight =
            climateMoisture *
            lowRuggedness *
            (0.30f + ((1.0f - climateHeat) * 0.70f)) *
            swampBias;

        float volcanicWeight =
            activity *
            (0.30f + (ruggedness * 0.70f)) *
            (0.25f + (climateHeat * 0.75f)) *
            volcanicBias;

        return TerrainBiomeSample.CreateNormalized(
            plainsWeight,
            rockyWeight,
            canyonWeight,
            swampWeight,
            volcanicWeight,
            climateHeat,
            climateMoisture,
            ruggedness,
            activity);
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }
}
