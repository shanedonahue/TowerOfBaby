using Godot;

namespace TowerOfBaby.Terrain;

public sealed class TerrainBiomeClassifier
{
    private readonly FastNoiseLite _temperatureNoise;
    private readonly FastNoiseLite _moistureNoise;
    private readonly FastNoiseLite _ruggedNoise;
    private readonly FastNoiseLite _activityNoise;
    private readonly FastNoiseLite _warpNoiseX;
    private readonly FastNoiseLite _warpNoiseZ;

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
            Frequency = 0.0026f
        };

        _warpNoiseZ = new FastNoiseLite
        {
            Seed = seed + 593,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.0026f
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
        float warpStrength = 38.0f;
        float sampleX = worldX + (_warpNoiseX.GetNoise2D(worldX, worldZ) * warpStrength);
        float sampleZ = worldZ + (_warpNoiseZ.GetNoise2D(worldX, worldZ) * warpStrength);

        float heat = NoiseToUnit(_temperatureNoise.GetNoise2D(sampleX, sampleZ));
        float moisture = NoiseToUnit(_moistureNoise.GetNoise2D(sampleX, sampleZ));
        float ruggedness = Mathf.Pow(Mathf.Abs(_ruggedNoise.GetNoise2D(sampleX, sampleZ)), 0.82f);
        float activity = NoiseToUnit(_activityNoise.GetNoise2D(sampleX, sampleZ));

        float temperateBand = Mathf.Clamp(1.0f - (Mathf.Abs(heat - 0.55f) * 1.75f), 0.0f, 1.0f);
        float lowRuggedness = 1.0f - ruggedness;
        float dry = 1.0f - moisture;

        float plainsWeight =
            lowRuggedness *
            (0.35f + (moisture * 0.65f)) *
            (0.30f + (temperateBand * 0.70f));

        float rockyWeight =
            ruggedness *
            (0.45f + (dry * 0.55f)) *
            (1.0f - (activity * 0.45f));

        float canyonWeight =
            ruggedness *
            dry *
            (0.30f + (heat * 0.70f));

        float swampWeight =
            moisture *
            lowRuggedness *
            (0.30f + ((1.0f - heat) * 0.70f));

        float volcanicWeight =
            activity *
            (0.30f + (ruggedness * 0.70f)) *
            (0.25f + (heat * 0.75f));

        return TerrainBiomeSample.CreateNormalized(
            plainsWeight,
            rockyWeight,
            canyonWeight,
            swampWeight,
            volcanicWeight,
            heat,
            moisture,
            ruggedness,
            activity);
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }
}
