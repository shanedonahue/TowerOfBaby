using Godot;
using System;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct VoxelSphereEdit(
    Vector3 Center,
    float Radius,
    float DeltaDensity,
    float PaintStrength,
    float RetextureMargin);

public readonly record struct VoxelSlashEdit(
    Vector3 Center,
    Vector3 Direction,
    Vector3 SurfaceNormal,
    float Length,
    float Width,
    float Depth,
    float DensityDelta,
    float PaintStrength,
    float RetextureMargin)
{
    public float BoundingRadius
    {
        get
        {
            float halfLength = Length * 0.5f;
            float paintWidth = (Width * 0.5f) + Mathf.Max(RetextureMargin, 0.0f);
            float paintDepth = Depth + Mathf.Max(RetextureMargin, 0.0f);
            return Mathf.Sqrt((halfLength * halfLength) + (paintWidth * paintWidth) + (paintDepth * paintDepth));
        }
    }
}

public readonly record struct VoxelEditStats(
    bool Modified,
    int DensitySamplesEdited,
    int MaterialSamplesTouched)
{
    public static VoxelEditStats None => new(false, 0, 0);

    public int TotalSamplesTouched => DensitySamplesEdited + MaterialSamplesTouched;
}

public static class VoxelTerrainEditing
{
    private const float NearSurfaceMaterialBias = 0.55f;
    private const float FlatExposedUpDot = 0.74f;
    private const float SlopedExposedUpDot = 0.42f;

    public static VoxelEditStats ApplySphere(
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

        if (edit.Radius <= 0.0f)
        {
            return VoxelEditStats.None;
        }

        if (Mathf.IsZeroApprox(edit.DeltaDensity) && edit.PaintStrength <= 0.0f)
        {
            return VoxelEditStats.None;
        }

        Vector3 localCenter = edit.Center - data.Origin;
        IndexBounds densityBounds = ComputeBounds(data, localCenter, edit.Radius);
        if (!densityBounds.IsValid)
        {
            return VoxelEditStats.None;
        }

        bool modified = false;
        int densitySamplesEdited = 0;
        if (!Mathf.IsZeroApprox(edit.DeltaDensity))
        {
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
                        densitySamplesEdited++;
                    }
                }
            }
        }

        float retextureRadius = edit.Radius + Mathf.Max(edit.RetextureMargin, data.VoxelSize);
        IndexBounds materialBounds = ComputeBounds(data, localCenter, retextureRadius);
        int materialSamplesTouched = 0;
        for (int z = materialBounds.Min.Z; z <= materialBounds.Max.Z; z++)
        {
            for (int y = materialBounds.Min.Y; y <= materialBounds.Max.Y; y++)
            {
                for (int x = materialBounds.Min.X; x <= materialBounds.Max.X; x++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float density = data.GetDensity(x, y, z);
                    float distance = position.DistanceTo(edit.Center);
                    float paintInfluence = 1.0f - Mathf.Clamp(distance / retextureRadius, 0.0f, 1.0f);
                    VoxelMaterialId terrainMaterial = materialResolver(position, density);
                    VoxelMaterialId nextMaterial = terrainMaterial;
                    if ((paintInfluence * edit.PaintStrength) >= 0.18f &&
                        density >= data.IsoLevel - (data.VoxelSize * NearSurfaceMaterialBias))
                    {
                        nextMaterial = ResolveEditedSurfaceMaterial(
                            terrainMaterial,
                            position - edit.Center,
                            edit.DeltaDensity);
                    }

                    if (data.GetMaterial(x, y, z) != nextMaterial)
                    {
                        data.SetMaterial(x, y, z, nextMaterial);
                        modified = true;
                    }

                    materialSamplesTouched++;
                }
            }
        }

        return new VoxelEditStats(modified, densitySamplesEdited, materialSamplesTouched);
    }

    public static VoxelEditStats ApplySlash(
        VoxelChunkData data,
        VoxelSlashEdit edit,
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

        if (edit.Length <= 0.0f || edit.Width <= 0.0f || edit.Depth <= 0.0f)
        {
            return VoxelEditStats.None;
        }

        if (Mathf.IsZeroApprox(edit.DensityDelta) && edit.PaintStrength <= 0.0f)
        {
            return VoxelEditStats.None;
        }

        Vector3 normal = SafeNormalized(edit.SurfaceNormal, Vector3.Up);
        Vector3 direction = SafeNormalized(ProjectOntoPlane(edit.Direction, normal), Vector3.Forward);
        Vector3 across = normal.Cross(direction);
        across = across.LengthSquared() > 0.0001f
            ? across.Normalized()
            : SafeNormalized(normal.Cross(Vector3.Forward), Vector3.Right);

        float halfLength = Mathf.Max(edit.Length * 0.5f, data.VoxelSize);
        float halfWidth = Mathf.Max(edit.Width * 0.5f, data.VoxelSize * 0.5f);
        float halfDepth = Mathf.Max(edit.Depth * 0.5f, data.VoxelSize * 0.35f);
        float retexturePadding = Mathf.Max(edit.RetextureMargin, data.VoxelSize);

        Vector3 localCenter = edit.Center - data.Origin;
        IndexBounds densityBounds = ComputeBounds(data, localCenter, edit.BoundingRadius);
        if (!densityBounds.IsValid)
        {
            return VoxelEditStats.None;
        }

        bool modified = false;
        int densitySamplesEdited = 0;
        if (!Mathf.IsZeroApprox(edit.DensityDelta))
        {
            for (int z = densityBounds.Min.Z; z <= densityBounds.Max.Z; z++)
            {
                for (int y = densityBounds.Min.Y; y <= densityBounds.Max.Y; y++)
                {
                    for (int x = densityBounds.Min.X; x <= densityBounds.Max.X; x++)
                    {
                        Vector3 position = data.GetPointPosition(x, y, z);
                        float influence = ComputeSlashInfluence(position, edit.Center, direction, across, normal, halfLength, halfWidth, halfDepth);
                        if (influence <= 0.0f)
                        {
                            continue;
                        }

                        data.SetDensity(x, y, z, data.GetDensity(x, y, z) + (edit.DensityDelta * influence));
                        modified = true;
                        densitySamplesEdited++;
                    }
                }
            }
        }

        float paintHalfLength = halfLength + retexturePadding;
        float paintHalfWidth = halfWidth + retexturePadding;
        float paintHalfDepth = halfDepth + (retexturePadding * 0.75f);
        IndexBounds materialBounds = ComputeBounds(data, localCenter, edit.BoundingRadius + retexturePadding);
        int materialSamplesTouched = 0;
        for (int z = materialBounds.Min.Z; z <= materialBounds.Max.Z; z++)
        {
            for (int y = materialBounds.Min.Y; y <= materialBounds.Max.Y; y++)
            {
                for (int x = materialBounds.Min.X; x <= materialBounds.Max.X; x++)
                {
                    Vector3 position = data.GetPointPosition(x, y, z);
                    float paintInfluence = ComputeSlashInfluence(position, edit.Center, direction, across, normal, paintHalfLength, paintHalfWidth, paintHalfDepth);
                    if (paintInfluence <= 0.0f)
                    {
                        continue;
                    }

                    materialSamplesTouched++;
                    float density = data.GetDensity(x, y, z);
                    VoxelMaterialId terrainMaterial = materialResolver(position, density);
                    VoxelMaterialId nextMaterial = terrainMaterial;
                    if ((paintInfluence * edit.PaintStrength) >= 0.16f &&
                        density >= data.IsoLevel - (data.VoxelSize * NearSurfaceMaterialBias))
                    {
                        Vector3 exposureNormal = normal;
                        if ((position - edit.Center).Dot(normal) < 0.0f)
                        {
                            exposureNormal = -normal;
                        }

                        nextMaterial = ResolveEditedSurfaceMaterial(
                            terrainMaterial,
                            exposureNormal,
                            edit.DensityDelta);
                    }

                    if (data.GetMaterial(x, y, z) != nextMaterial)
                    {
                        data.SetMaterial(x, y, z, nextMaterial);
                        modified = true;
                    }
                }
            }
        }

        return new VoxelEditStats(modified, densitySamplesEdited, materialSamplesTouched);
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

    private static float ComputeSlashInfluence(
        Vector3 position,
        Vector3 center,
        Vector3 direction,
        Vector3 across,
        Vector3 normal,
        float halfLength,
        float halfWidth,
        float halfDepth)
    {
        Vector3 delta = position - center;
        float along = delta.Dot(direction);
        float alongAbs = Mathf.Abs(along);
        if (alongAbs > halfLength)
        {
            return 0.0f;
        }

        float acrossDistance = delta.Dot(across);
        float normalDistance = delta.Dot(normal);
        float radial =
            ((acrossDistance * acrossDistance) / Mathf.Max(halfWidth * halfWidth, 0.0001f)) +
            ((normalDistance * normalDistance) / Mathf.Max(halfDepth * halfDepth, 0.0001f));
        if (radial >= 1.0f)
        {
            return 0.0f;
        }

        float alongWeight = 1.0f - Mathf.Clamp(alongAbs / halfLength, 0.0f, 1.0f);
        alongWeight *= alongWeight * (3.0f - (2.0f * alongWeight));
        float radialWeight = 1.0f - radial;
        radialWeight *= radialWeight;
        return alongWeight * radialWeight;
    }

    private static VoxelMaterialId ResolveEditedSurfaceMaterial(
        VoxelMaterialId terrainMaterial,
        Vector3 exposureNormal,
        float densityDelta)
    {
        float upness = Mathf.Abs(SafeNormalized(exposureNormal, Vector3.Up).Dot(Vector3.Up));

        if (densityDelta > 0.0f)
        {
            if (upness >= FlatExposedUpDot)
            {
                return terrainMaterial is VoxelMaterialId.Grass or VoxelMaterialId.Soil
                    ? VoxelMaterialId.Grass
                    : terrainMaterial;
            }

            return upness >= SlopedExposedUpDot
                ? VoxelMaterialId.Soil
                : VoxelMaterialId.Rock;
        }

        if (terrainMaterial == VoxelMaterialId.Snow && upness < 0.86f)
        {
            terrainMaterial = VoxelMaterialId.Rock;
        }

        if (upness >= FlatExposedUpDot)
        {
            return terrainMaterial is VoxelMaterialId.Rock or VoxelMaterialId.Cliff or VoxelMaterialId.Snow
                ? VoxelMaterialId.Rock
                : VoxelMaterialId.Soil;
        }

        if (upness >= SlopedExposedUpDot)
        {
            return terrainMaterial is VoxelMaterialId.Grass or VoxelMaterialId.Soil
                ? VoxelMaterialId.Soil
                : VoxelMaterialId.Rock;
        }

        return VoxelMaterialId.Cliff;
    }

    private static Vector3 SafeNormalized(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() > 0.0001f
            ? value.Normalized()
            : fallback;
    }

    private static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        Vector3 normal = SafeNormalized(planeNormal, Vector3.Up);
        return value - (normal * value.Dot(normal));
    }

    private readonly record struct IndexBounds(Vector3I Min, Vector3I Max, bool IsValid);
}
