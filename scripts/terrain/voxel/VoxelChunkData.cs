using Godot;
using System;

namespace TowerOfBaby.Terrain.Voxel;

public sealed class VoxelChunkData
{
    public int PointsPerAxis { get; }
    public float VoxelSize { get; }
    public float IsoLevel { get; }
    public Vector3 Origin { get; }

    private readonly float[] _densities;
    private readonly byte[] _materials;
    private VoxelDetailBrickData _detailBrick;

    public int CellsPerAxis => PointsPerAxis - 1;
    public float ChunkSize => CellsPerAxis * VoxelSize;
    public int PointCount => _densities.Length;
    public bool HasDetailBrick => _detailBrick != null;
    public VoxelDetailBrickData DetailBrick => _detailBrick;
    public bool HasEditedDetailBrick => _detailBrick?.HasPersistentEdits == true;
    public VoxelDetailBrickData EditedDetailBrick => HasEditedDetailBrick ? _detailBrick : null;

    public VoxelChunkData(int pointsPerAxis, float voxelSize, Vector3 origin, float isoLevel = 0.0f)
    {
        PointsPerAxis = pointsPerAxis;
        VoxelSize = voxelSize;
        Origin = origin;
        IsoLevel = isoLevel;
        _densities = new float[pointsPerAxis * pointsPerAxis * pointsPerAxis];
        _materials = new byte[pointsPerAxis * pointsPerAxis * pointsPerAxis];
    }

    public void SetDensity(int x, int y, int z, float density)
    {
        _densities[GetIndex(x, y, z)] = density;
    }

    public float GetDensity(int x, int y, int z)
    {
        return _densities[GetIndex(x, y, z)];
    }

    public void SetMaterial(int x, int y, int z, VoxelMaterialId material)
    {
        _materials[GetIndex(x, y, z)] = (byte)material;
    }

    public VoxelMaterialId GetMaterial(int x, int y, int z)
    {
        return (VoxelMaterialId)_materials[GetIndex(x, y, z)];
    }

    public Vector3 GetPointPosition(int x, int y, int z)
    {
        return Origin + new Vector3(x * VoxelSize, y * VoxelSize, z * VoxelSize);
    }

    public float GetMinY()
    {
        return Origin.Y;
    }

    public float GetMaxY()
    {
        return Origin.Y + ChunkSize;
    }

    public bool EnsureDetailBrick(
        Aabb localBounds,
        int detailScale,
        int paddingCoarseCells,
        Func<Vector3, float> densitySampler,
        Func<Vector3, float, VoxelMaterialId> materialResolver,
        bool persistentEdits,
        bool preserveExistingCoverage)
    {
        if (densitySampler == null)
        {
            throw new ArgumentNullException(nameof(densitySampler));
        }

        if (materialResolver == null)
        {
            throw new ArgumentNullException(nameof(materialResolver));
        }

        VoxelDetailBrickData coverageReference = preserveExistingCoverage ? _detailBrick : null;
        if (!TryComputeDetailCoverage(localBounds, detailScale, paddingCoarseCells, coverageReference, out DetailBrickCoverage coverage))
        {
            return false;
        }

        if (_detailBrick != null &&
            _detailBrick.DetailScale == coverage.DetailScale &&
            _detailBrick.CoarseCellMin == coverage.CoarseCellMin &&
            _detailBrick.CoarseCellCount == coverage.CoarseCellCount)
        {
            if (persistentEdits && !_detailBrick.HasPersistentEdits)
            {
                _detailBrick.MarkPersistentEdits();
                return true;
            }

            return false;
        }

        _detailBrick = BuildDetailBrick(coverage, _detailBrick, densitySampler, materialResolver, persistentEdits);
        return true;
    }

    public bool RemoveTransientDetailBrick()
    {
        if (_detailBrick == null || _detailBrick.HasPersistentEdits)
        {
            return false;
        }

        _detailBrick = null;
        return true;
    }

    public bool ApplySphereBrush(Vector3 center, float radius, float deltaDensity)
    {
        bool modified = false;
        float radiusSquared = radius * radius;

        for (int z = 0; z < PointsPerAxis; z++)
        {
            for (int y = 0; y < PointsPerAxis; y++)
            {
                for (int x = 0; x < PointsPerAxis; x++)
                {
                    Vector3 position = GetPointPosition(x, y, z);
                    float distanceSquared = position.DistanceSquaredTo(center);
                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    float distance = Mathf.Sqrt(distanceSquared);
                    float falloff = 1.0f - Mathf.Clamp(distance / radius, 0.0f, 1.0f);
                    int index = GetIndex(x, y, z);
                    _densities[index] += deltaDensity * falloff;
                    if (deltaDensity > 0.0f)
                    {
                        _materials[index] = (byte)VoxelMaterialId.Soil;
                    }
                    modified = true;
                }
            }
        }

        return modified;
    }

    public float[] CopyDensities()
    {
        return (float[])_densities.Clone();
    }

    public byte[] CopyMaterials()
    {
        return (byte[])_materials.Clone();
    }

    public void LoadFromBuffers(float[] densities, byte[] materials)
    {
        if (densities.Length != _densities.Length || materials.Length != _materials.Length)
        {
            throw new System.ArgumentException("Chunk buffer sizes do not match VoxelChunkData dimensions.");
        }

        densities.CopyTo(_densities, 0);
        materials.CopyTo(_materials, 0);
        _detailBrick = null;
    }

    public byte[] CopyEditedDetailBrickBlob()
    {
        return _detailBrick?.HasPersistentEdits == true
            ? _detailBrick.Serialize()
            : null;
    }

    public void LoadEditedDetailBrickFromBlob(byte[] blob)
    {
        _detailBrick = blob == null || blob.Length == 0
            ? null
            : VoxelDetailBrickData.Deserialize(blob);
    }

    public float SampleDensityTrilinear(Vector3 worldPosition)
    {
        Vector3 normalized = (worldPosition - Origin) / VoxelSize;
        float sampleX = Mathf.Clamp(normalized.X, 0.0f, CellsPerAxis);
        float sampleY = Mathf.Clamp(normalized.Y, 0.0f, CellsPerAxis);
        float sampleZ = Mathf.Clamp(normalized.Z, 0.0f, CellsPerAxis);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, PointsPerAxis - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(sampleY), 0, PointsPerAxis - 1);
        int z0 = Mathf.Clamp(Mathf.FloorToInt(sampleZ), 0, PointsPerAxis - 1);
        int x1 = Mathf.Min(x0 + 1, PointsPerAxis - 1);
        int y1 = Mathf.Min(y0 + 1, PointsPerAxis - 1);
        int z1 = Mathf.Min(z0 + 1, PointsPerAxis - 1);

        float tx = sampleX - x0;
        float ty = sampleY - y0;
        float tz = sampleZ - z0;

        float c000 = GetDensity(x0, y0, z0);
        float c100 = GetDensity(x1, y0, z0);
        float c010 = GetDensity(x0, y1, z0);
        float c110 = GetDensity(x1, y1, z0);
        float c001 = GetDensity(x0, y0, z1);
        float c101 = GetDensity(x1, y0, z1);
        float c011 = GetDensity(x0, y1, z1);
        float c111 = GetDensity(x1, y1, z1);

        float c00 = Mathf.Lerp(c000, c100, tx);
        float c10 = Mathf.Lerp(c010, c110, tx);
        float c01 = Mathf.Lerp(c001, c101, tx);
        float c11 = Mathf.Lerp(c011, c111, tx);
        float c0 = Mathf.Lerp(c00, c10, ty);
        float c1 = Mathf.Lerp(c01, c11, ty);
        return Mathf.Lerp(c0, c1, tz);
    }

    public VoxelMaterialId SampleMaterialNearest(Vector3 worldPosition)
    {
        Vector3 normalized = (worldPosition - Origin) / VoxelSize;
        int x = Mathf.Clamp(Mathf.RoundToInt(normalized.X), 0, PointsPerAxis - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(normalized.Y), 0, PointsPerAxis - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt(normalized.Z), 0, PointsPerAxis - 1);
        return GetMaterial(x, y, z);
    }

    private int GetIndex(int x, int y, int z)
    {
        return x + (PointsPerAxis * (y + (PointsPerAxis * z)));
    }

    private bool TryComputeDetailCoverage(
        Aabb requestedLocalBounds,
        int detailScale,
        int paddingCoarseCells,
        VoxelDetailBrickData existing,
        out DetailBrickCoverage coverage)
    {
        int effectiveScale = existing?.DetailScale ?? Math.Max(2, detailScale);
        int effectivePadding = Math.Max(1, paddingCoarseCells);

        Vector3 start = requestedLocalBounds.Position;
        Vector3 end = requestedLocalBounds.Position + requestedLocalBounds.Size;
        if (existing != null)
        {
            Aabb existingBounds = existing.LocalBounds;
            Vector3 existingEnd = existingBounds.Position + existingBounds.Size;
            start = new Vector3(
                Mathf.Min(start.X, existingBounds.Position.X),
                Mathf.Min(start.Y, existingBounds.Position.Y),
                Mathf.Min(start.Z, existingBounds.Position.Z));
            end = new Vector3(
                Mathf.Max(end.X, existingEnd.X),
                Mathf.Max(end.Y, existingEnd.Y),
                Mathf.Max(end.Z, existingEnd.Z));
        }

        start = new Vector3(
            Mathf.Clamp(start.X, 0.0f, ChunkSize),
            Mathf.Clamp(start.Y, 0.0f, ChunkSize),
            Mathf.Clamp(start.Z, 0.0f, ChunkSize));
        end = new Vector3(
            Mathf.Clamp(end.X, 0.0f, ChunkSize),
            Mathf.Clamp(end.Y, 0.0f, ChunkSize),
            Mathf.Clamp(end.Z, 0.0f, ChunkSize));
        if (end.X <= start.X || end.Y <= start.Y || end.Z <= start.Z)
        {
            coverage = default;
            return false;
        }

        Vector3I minCell = new(
            Mathf.Clamp(Mathf.FloorToInt(start.X / VoxelSize) - effectivePadding, 0, CellsPerAxis),
            Mathf.Clamp(Mathf.FloorToInt(start.Y / VoxelSize) - effectivePadding, 0, CellsPerAxis),
            Mathf.Clamp(Mathf.FloorToInt(start.Z / VoxelSize) - effectivePadding, 0, CellsPerAxis));
        Vector3I maxCellExclusive = new(
            Mathf.Clamp(Mathf.CeilToInt(end.X / VoxelSize) + effectivePadding, 0, CellsPerAxis),
            Mathf.Clamp(Mathf.CeilToInt(end.Y / VoxelSize) + effectivePadding, 0, CellsPerAxis),
            Mathf.Clamp(Mathf.CeilToInt(end.Z / VoxelSize) + effectivePadding, 0, CellsPerAxis));

        int spanX = Math.Max(1, maxCellExclusive.X - minCell.X);
        int spanY = Math.Max(1, maxCellExclusive.Y - minCell.Y);
        int spanZ = Math.Max(1, maxCellExclusive.Z - minCell.Z);
        int side = Math.Max(spanX, Math.Max(spanY, spanZ));
        side = Mathf.Clamp(side, 1, CellsPerAxis);

        Vector3I fittedMin = new(
            FitCoverageAxis(minCell.X, maxCellExclusive.X, side, CellsPerAxis),
            FitCoverageAxis(minCell.Y, maxCellExclusive.Y, side, CellsPerAxis),
            FitCoverageAxis(minCell.Z, maxCellExclusive.Z, side, CellsPerAxis));
        coverage = new DetailBrickCoverage(fittedMin, side, effectiveScale);
        return true;
    }

    private VoxelDetailBrickData BuildDetailBrick(
        DetailBrickCoverage coverage,
        VoxelDetailBrickData previous,
        Func<Vector3, float> densitySampler,
        Func<Vector3, float, VoxelMaterialId> materialResolver,
        bool persistentEdits)
    {
        int pointsPerAxis = (coverage.CoarseCellCount * coverage.DetailScale) + 1;
        float detailVoxelSize = VoxelSize / coverage.DetailScale;
        Vector3 brickOrigin = Origin + new Vector3(
            coverage.CoarseCellMin.X * VoxelSize,
            coverage.CoarseCellMin.Y * VoxelSize,
            coverage.CoarseCellMin.Z * VoxelSize);
        VoxelChunkData detailData = new(pointsPerAxis, detailVoxelSize, brickOrigin, IsoLevel);

        for (int z = 0; z < detailData.PointsPerAxis; z++)
        {
            for (int y = 0; y < detailData.PointsPerAxis; y++)
            {
                for (int x = 0; x < detailData.PointsPerAxis; x++)
                {
                    Vector3 position = detailData.GetPointPosition(x, y, z);
                    bool reusePrevious = previous != null && previous.ContainsWorldPosition(position);
                    float density = reusePrevious
                        ? previous.Data.SampleDensityTrilinear(position)
                        : densitySampler(position);
                    VoxelMaterialId material = reusePrevious
                        ? previous.Data.SampleMaterialNearest(position)
                        : materialResolver(position, density);

                    detailData.SetDensity(x, y, z, density);
                    detailData.SetMaterial(x, y, z, material);
                }
            }
        }

        return new VoxelDetailBrickData(
            coverage.CoarseCellMin,
            coverage.CoarseCellCount,
            coverage.DetailScale,
            detailData,
            hasPersistentEdits: persistentEdits || previous?.HasPersistentEdits == true);
    }

    private static int FitCoverageAxis(int desiredMin, int desiredMaxExclusive, int side, int maxCells)
    {
        int maxStart = Math.Max(0, maxCells - side);
        int span = desiredMaxExclusive - desiredMin;
        if (span >= side)
        {
            return Mathf.Clamp(desiredMin, 0, maxStart);
        }

        float center = (desiredMin + desiredMaxExclusive) * 0.5f;
        int start = Mathf.RoundToInt(center - (side * 0.5f));
        start = Mathf.Clamp(start, 0, maxStart);
        if (start > desiredMin)
        {
            start = desiredMin;
        }

        if ((start + side) < desiredMaxExclusive)
        {
            start = desiredMaxExclusive - side;
        }

        return Mathf.Clamp(start, 0, maxStart);
    }

    private readonly record struct DetailBrickCoverage(
        Vector3I CoarseCellMin,
        int CoarseCellCount,
        int DetailScale);
}
