using Godot;

public sealed class VoxelChunkData
{
    public int PointsPerAxis { get; }
    public float VoxelSize { get; }
    public float IsoLevel { get; }
    public Vector3 Origin { get; }

    private readonly float[] _densities;
    private readonly byte[] _materials;

    public int CellsPerAxis => PointsPerAxis - 1;
    public float ChunkSize => CellsPerAxis * VoxelSize;
    public int PointCount => _densities.Length;

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
    }

    private int GetIndex(int x, int y, int z)
    {
        return x + (PointsPerAxis * (y + (PointsPerAxis * z)));
    }
}
