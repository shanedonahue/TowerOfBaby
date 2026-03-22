public sealed class VoxelTerrainWorldSettings
{
    public int PointsPerAxis { get; init; }
    public float VoxelSize { get; init; }
    public float BaseY { get; init; }

    public float ChunkSize => (PointsPerAxis - 1) * VoxelSize;
}
