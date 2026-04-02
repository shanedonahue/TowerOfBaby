using Godot;

namespace TowerOfBaby.Terrain;

public static class TerrainMetrics
{
    public static float GetVoxelSize(TerrainConfig config, int lod)
    {
        return config.BaseVoxelSize * Mathf.Pow(2.0f, lod);
    }

    public static float GetBlockSpan(TerrainConfig config, int lod)
    {
        return (config.PointsPerAxis - 1) * GetVoxelSize(config, lod);
    }

    public static Vector3 GetBlockOrigin(TerrainConfig config, TerrainBlockId blockId)
    {
        float span = GetBlockSpan(config, blockId.Lod);
        return new Vector3(
            blockId.Index.X * span,
            config.BaseY + (blockId.Index.Y * span),
            blockId.Index.Z * span);
    }

    public static Vector3 GetBlockCenter(TerrainConfig config, TerrainBlockId blockId)
    {
        float span = GetBlockSpan(config, blockId.Lod);
        return GetBlockOrigin(config, blockId) + (Vector3.One * (span * 0.5f));
    }

    public static Aabb GetBlockBounds(TerrainConfig config, TerrainBlockId blockId)
    {
        float span = GetBlockSpan(config, blockId.Lod);
        return new Aabb(GetBlockOrigin(config, blockId), Vector3.One * span);
    }

    public static TerrainBlockId GetBlockForWorldPosition(TerrainConfig config, int lod, Vector3 worldPosition)
    {
        float span = GetBlockSpan(config, lod);
        return new TerrainBlockId(
            lod,
            new Vector3I(
                Mathf.FloorToInt(worldPosition.X / span),
                Mathf.FloorToInt((worldPosition.Y - config.BaseY) / span),
                Mathf.FloorToInt(worldPosition.Z / span)));
    }

    public static TerrainBlockId[] GetChildren(TerrainBlockId parent)
    {
        if (parent.Lod <= 0)
        {
            return new[] { parent };
        }

        Vector3I childBase = parent.Index * 2;
        return new[]
        {
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(0, 0, 0)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(1, 0, 0)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(0, 1, 0)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(1, 1, 0)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(0, 0, 1)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(1, 0, 1)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(0, 1, 1)),
            new TerrainBlockId(parent.Lod - 1, childBase + new Vector3I(1, 1, 1))
        };
    }

    public static float DistanceSquaredToBlock(TerrainConfig config, TerrainBlockId blockId, Vector3 worldPosition)
    {
        Aabb bounds = GetBlockBounds(config, blockId);
        Vector3 min = bounds.Position;
        Vector3 max = bounds.End;
        Vector3 clamped = new(
            Mathf.Clamp(worldPosition.X, min.X, max.X),
            Mathf.Clamp(worldPosition.Y, min.Y, max.Y),
            Mathf.Clamp(worldPosition.Z, min.Z, max.Z));
        return clamped.DistanceSquaredTo(worldPosition);
    }
}
