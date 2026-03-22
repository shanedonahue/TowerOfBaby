using Godot;

public sealed class TerrainChunkConfig
{
    public float ChunkSize { get; init; }
    public int Resolution { get; init; }
    public int Seed { get; init; }
    public float BaseFrequency { get; init; }
    public float DetailFrequency { get; init; }
    public float HeightScale { get; init; }
    public float DetailScale { get; init; }

    public FastNoiseLite CreateBaseNoise()
    {
        return new FastNoiseLite
        {
            Seed = Seed,
            Frequency = BaseFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth
        };
    }

    public FastNoiseLite CreateDetailNoise()
    {
        return new FastNoiseLite
        {
            Seed = Seed + 17,
            Frequency = DetailFrequency,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin
        };
    }
}
