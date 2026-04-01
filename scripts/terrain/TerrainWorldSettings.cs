using Godot;

namespace TowerOfBaby.Terrain;

public sealed class TerrainWorldSettings
{
    public int PointsPerAxis { get; init; }
    public float VoxelSize { get; init; }
    public float BaseY { get; init; }

    public float ChunkSize => (PointsPerAxis - 1) * VoxelSize;

    public Vector3 GetChunkOrigin(Vector3I key)
    {
        return new Vector3(
            key.X * ChunkSize,
            BaseY + (key.Y * ChunkSize),
            key.Z * ChunkSize);
    }

    public Vector3 GetChunkCenter(Vector3I key)
    {
        return GetChunkOrigin(key) + (Vector3.One * (ChunkSize * 0.5f));
    }

    public Aabb GetChunkBounds(Vector3I key)
    {
        return new Aabb(GetChunkOrigin(key), Vector3.One * ChunkSize);
    }
}
