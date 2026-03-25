using Godot;
using System;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct VoxelSphereEdit(
    Vector3 Center,
    float Radius,
    float DeltaDensity,
    float RetextureMargin);

public static class VoxelTerrainEditing
{
    public static bool ApplySphere(
        VoxelChunkData data,
        VoxelSphereEdit edit,
        Func<Vector3, float, VoxelMaterialId> materialResolver)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (materialResolver == null)
        {
            throw new ArgumentNullException(nameof(materialResolver));
        }

        if (edit.Radius <= 0.0f || Mathf.IsZeroApprox(edit.DeltaDensity))
        {
            return false;
        }

        Vector3 localCenter = edit.Center - data.Origin;
        IndexBounds densityBounds = ComputeBounds(data, localCenter, edit.Radius);
        if (!densityBounds.IsValid)
        {
            return false;
        }

        bool modified = false;
        float radiusSquared = edit.Radius * edit.Radius;
        for (int z = densityBounds.Min.Z; z <= densityBounds.Max.Z; z++)
        {
            for (int y = densityBounds.Min.Y; y <= densityBounds.Max.Y; y++)
            {
                for (int x = densityBounds.Min.X; x <= densityBounds.Max.X; x++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float distanceSquared = position.DistanceSquaredTo(edit.Center);
                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    float distance = Mathf.Sqrt(distanceSquared);
                    float falloff = 1.0f - Mathf.Clamp(distance / edit.Radius, 0.0f, 1.0f);
                    data.SetDensity(x, y, z, data.GetDensity(x, y, z) + (edit.DeltaDensity * falloff));
                    modified = true;
                }
            }
        }

        if (!modified)
        {
            return false;
        }

        float retextureRadius = edit.Radius + Mathf.Max(edit.RetextureMargin, data.VoxelSize);
        IndexBounds materialBounds = ComputeBounds(data, localCenter, retextureRadius);
        for (int z = materialBounds.Min.Z; z <= materialBounds.Max.Z; z++)
        {
            for (int y = materialBounds.Min.Y; y <= materialBounds.Max.Y; y++)
            {
                for (int x = materialBounds.Min.X; x <= materialBounds.Max.X; x++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float density = data.GetDensity(x, y, z);
                    data.SetMaterial(x, y, z, materialResolver(position, density));
                }
            }
        }

        return true;
    }

    private static IndexBounds ComputeBounds(VoxelChunkData data, Vector3 localCenter, float radius)
    {
        float inverseVoxelSize = 1.0f / data.VoxelSize;
        Vector3 min = (localCenter - Vector3.One * radius) * inverseVoxelSize;
        Vector3 max = (localCenter + Vector3.One * radius) * inverseVoxelSize;

        Vector3I minIndex = new(
            Mathf.Clamp(Mathf.FloorToInt(min.X), 0, data.PointsPerAxis - 1),
            Mathf.Clamp(Mathf.FloorToInt(min.Y), 0, data.PointsPerAxis - 1),
            Mathf.Clamp(Mathf.FloorToInt(min.Z), 0, data.PointsPerAxis - 1));
        Vector3I maxIndex = new(
            Mathf.Clamp(Mathf.CeilToInt(max.X), 0, data.PointsPerAxis - 1),
            Mathf.Clamp(Mathf.CeilToInt(max.Y), 0, data.PointsPerAxis - 1),
            Mathf.Clamp(Mathf.CeilToInt(max.Z), 0, data.PointsPerAxis - 1));

        bool isValid =
            minIndex.X <= maxIndex.X &&
            minIndex.Y <= maxIndex.Y &&
            minIndex.Z <= maxIndex.Z;
        return new IndexBounds(minIndex, maxIndex, isValid);
    }

    private readonly record struct IndexBounds(Vector3I Min, Vector3I Max, bool IsValid);
}
