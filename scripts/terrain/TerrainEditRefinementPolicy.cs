using Godot;
using System;

namespace TowerOfBaby.Terrain;

public static class TerrainEditRefinementPolicy
{
    // These tables only affect local edit/detail bricks built around edited bounds.
    // They never change the global terrain voxel size or default LOD block density.
    public static readonly int[] EditRefinementScaleByLevel =
    {
        1,
        2,
        3,
        4,
        5
    };

    // Keep the most expensive refinement levels reserved for the smallest edit bounds so
    // close-range cuts get smoother without spreading the triangle/memory cost too far.
    private static readonly float[] MaxEditExtentInBaseVoxelsByLevel =
    {
        float.PositiveInfinity,
        float.PositiveInfinity,
        float.PositiveInfinity,
        12.0f,
        8.0f
    };

    public const int MinDetailBrickScale = 2;
    public static readonly int MaxEditRefinementLevel = EditRefinementScaleByLevel.Length - 1;
    public static readonly int DefaultPersistentEditRefinementLevel = MaxEditRefinementLevel;
    public static readonly int MaxLocalEditScale = EditRefinementScaleByLevel[^1];

    public static int ClampRequestedLevel(int requestedDetailLevel)
    {
        return Mathf.Clamp(requestedDetailLevel, 0, MaxEditRefinementLevel);
    }

    public static int ResolveScaleForLevel(int requestedDetailLevel)
    {
        return EditRefinementScaleByLevel[ClampRequestedLevel(requestedDetailLevel)];
    }

    public static int ResolveDetailBrickScaleForLevel(int requestedDetailLevel)
    {
        return Math.Max(MinDetailBrickScale, ResolveScaleForLevel(requestedDetailLevel));
    }

    public static int ResolveLevelForScale(int detailScale)
    {
        int normalizedScale = Math.Max(1, detailScale);
        for (int level = 0; level < EditRefinementScaleByLevel.Length; level++)
        {
            if (EditRefinementScaleByLevel[level] >= normalizedScale)
            {
                return level;
            }
        }

        return MaxEditRefinementLevel;
    }

    public static int ResolveRequestedLevelForLocalEdit(int requestedDetailLevel, Aabb worldBounds, float baseVoxelSize)
    {
        int resolvedLevel = Math.Max(1, ClampRequestedLevel(requestedDetailLevel));
        if (baseVoxelSize <= 0.0001f)
        {
            return resolvedLevel;
        }

        float maxExtentInBaseVoxels = GetMaxExtent(worldBounds) / baseVoxelSize;
        while (resolvedLevel > 1 && maxExtentInBaseVoxels > MaxEditExtentInBaseVoxelsByLevel[resolvedLevel])
        {
            resolvedLevel--;
        }

        return Math.Max(1, resolvedLevel);
    }

    private static float GetMaxExtent(Aabb bounds)
    {
        Vector3 size = bounds.Size;
        return Math.Max(Math.Abs(size.X), Math.Max(Math.Abs(size.Y), Math.Abs(size.Z)));
    }
}
