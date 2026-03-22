using Godot;

public static class TerrainNoise
{
    public static float SampleHeight(float worldX, float worldZ, TerrainChunkConfig config, FastNoiseLite baseNoise, FastNoiseLite detailNoise)
    {
        float broadShape = baseNoise.GetNoise2D(worldX, worldZ) * config.HeightScale;
        float detail = detailNoise.GetNoise2D(worldX, worldZ) * config.DetailScale;
        float ridges = Mathf.Abs(detailNoise.GetNoise2D(worldX * 0.7f, worldZ * 0.7f)) * (config.DetailScale * 0.45f);

        return broadShape + detail - ridges;
    }
}
