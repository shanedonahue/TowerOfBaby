using Godot;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainChunkDirtyBoundsSnapshot(
    bool HasBounds,
    Aabb LocalBounds,
    Vector3I VoxelMin,
    Vector3I VoxelMax,
    double Volume,
    double Coverage)
{
    public static TerrainChunkDirtyBoundsSnapshot Empty =>
        new(false, default, Vector3I.Zero, Vector3I.Zero, 0.0, 0.0);

    public string Summary
    {
        get
        {
            if (!HasBounds)
            {
                return "none";
            }

            return
                $"min({LocalBounds.Position.X:0.0},{LocalBounds.Position.Y:0.0},{LocalBounds.Position.Z:0.0}) " +
                $"size({LocalBounds.Size.X:0.0},{LocalBounds.Size.Y:0.0},{LocalBounds.Size.Z:0.0}) " +
                $"vox({VoxelMin.X},{VoxelMin.Y},{VoxelMin.Z})->({VoxelMax.X},{VoxelMax.Y},{VoxelMax.Z}) " +
                $"vol {Volume:0.0} cov {Coverage * 100.0:0.#}%";
        }
    }
}

public sealed class TerrainChunkDirtyBoundsTracker
{
    private readonly float _chunkSize;
    private readonly float _voxelSize;
    private readonly int _pointsPerAxis;

    private bool _hasBounds;
    private Aabb _localBounds;
    private Vector3I _voxelMin;
    private Vector3I _voxelMax;

    public TerrainChunkDirtyBoundsTracker(float chunkSize, float voxelSize, int pointsPerAxis)
    {
        _chunkSize = Mathf.Max(chunkSize, 0.01f);
        _voxelSize = Mathf.Max(voxelSize, 0.001f);
        _pointsPerAxis = Mathf.Max(pointsPerAxis, 2);
    }

    public bool HasBounds => _hasBounds;

    public TerrainChunkDirtyBoundsSnapshot Snapshot => GetSnapshot();

    public void Include(Aabb localBounds)
    {
        if (!TryNormalizeLocalBounds(localBounds, out Aabb normalizedBounds))
        {
            return;
        }

        _localBounds = _hasBounds
            ? Union(_localBounds, normalizedBounds)
            : normalizedBounds;
        _hasBounds = true;
        _voxelMin = ToVoxelMin(_localBounds.Position);
        _voxelMax = ToVoxelMax(_localBounds.Position + _localBounds.Size);
    }

    public void IncludeFullChunk()
    {
        Include(new Aabb(Vector3.Zero, Vector3.One * _chunkSize));
    }

    public void Clear()
    {
        _hasBounds = false;
        _localBounds = default;
        _voxelMin = Vector3I.Zero;
        _voxelMax = Vector3I.Zero;
    }

    public TerrainChunkDirtyBoundsSnapshot GetSnapshot()
    {
        if (!_hasBounds)
        {
            return TerrainChunkDirtyBoundsSnapshot.Empty;
        }

        double volume = _localBounds.Size.X * _localBounds.Size.Y * _localBounds.Size.Z;
        double chunkVolume = _chunkSize * _chunkSize * _chunkSize;
        double coverage = chunkVolume > 0.0001
            ? volume / chunkVolume
            : 0.0;
        return new TerrainChunkDirtyBoundsSnapshot(
            true,
            _localBounds,
            _voxelMin,
            _voxelMax,
            volume,
            coverage);
    }

    private bool TryNormalizeLocalBounds(Aabb localBounds, out Aabb normalizedBounds)
    {
        Vector3 start = localBounds.Position;
        Vector3 end = localBounds.Position + localBounds.Size;
        Vector3 min = new(
            Mathf.Clamp(Mathf.Min(start.X, end.X), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Min(start.Y, end.Y), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Min(start.Z, end.Z), 0.0f, _chunkSize));
        Vector3 max = new(
            Mathf.Clamp(Mathf.Max(start.X, end.X), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Max(start.Y, end.Y), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Max(start.Z, end.Z), 0.0f, _chunkSize));
        Vector3 size = max - min;
        if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
        {
            normalizedBounds = default;
            return false;
        }

        normalizedBounds = new Aabb(min, size);
        return true;
    }

    private static Aabb Union(Aabb a, Aabb b)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Min(a.Position.X, b.Position.X),
            Mathf.Min(a.Position.Y, b.Position.Y),
            Mathf.Min(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Max(aEnd.X, bEnd.X),
            Mathf.Max(aEnd.Y, bEnd.Y),
            Mathf.Max(aEnd.Z, bEnd.Z));
        return new Aabb(min, max - min);
    }

    private Vector3I ToVoxelMin(Vector3 localPosition)
    {
        return new Vector3I(
            Mathf.Clamp(Mathf.FloorToInt(localPosition.X / _voxelSize), 0, _pointsPerAxis - 1),
            Mathf.Clamp(Mathf.FloorToInt(localPosition.Y / _voxelSize), 0, _pointsPerAxis - 1),
            Mathf.Clamp(Mathf.FloorToInt(localPosition.Z / _voxelSize), 0, _pointsPerAxis - 1));
    }

    private Vector3I ToVoxelMax(Vector3 localEnd)
    {
        return new Vector3I(
            Mathf.Clamp(Mathf.CeilToInt(localEnd.X / _voxelSize), 0, _pointsPerAxis - 1),
            Mathf.Clamp(Mathf.CeilToInt(localEnd.Y / _voxelSize), 0, _pointsPerAxis - 1),
            Mathf.Clamp(Mathf.CeilToInt(localEnd.Z / _voxelSize), 0, _pointsPerAxis - 1));
    }
}
