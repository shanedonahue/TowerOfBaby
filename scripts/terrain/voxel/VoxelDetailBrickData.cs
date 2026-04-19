using Godot;
using System;
using System.IO;

namespace TowerOfBaby.Terrain.Voxel;

public sealed class VoxelDetailBrickData : IDisposable
{
    private const int SerializationVersion = 2;
    private const int ReplacePaddingCoarseCells = 1;

    public VoxelDetailBrickData(
        Vector3I coarseCellMin,
        int coarseCellCount,
        int detailScale,
        VoxelChunkData data,
        bool hasPersistentEdits)
    {
        CoarseCellMin = coarseCellMin;
        CoarseCellCount = Math.Max(1, coarseCellCount);
        DetailScale = Math.Max(2, detailScale);
        Data = data ?? throw new ArgumentNullException(nameof(data));
        HasPersistentEdits = hasPersistentEdits;
    }

    public Vector3I CoarseCellMin { get; }
    public int CoarseCellCount { get; }
    public int DetailScale { get; }
    public VoxelChunkData Data { get; }
    public bool HasPersistentEdits { get; private set; }

    public Vector3I CoarseCellMaxExclusive => CoarseCellMin + new Vector3I(CoarseCellCount, CoarseCellCount, CoarseCellCount);
    public float CoarseVoxelSize => Data.VoxelSize * DetailScale;
    public float CoverageSize => CoarseCellCount * CoarseVoxelSize;

    public Aabb LocalBounds =>
        new(
            new Vector3(
                CoarseCellMin.X * CoarseVoxelSize,
                CoarseCellMin.Y * CoarseVoxelSize,
                CoarseCellMin.Z * CoarseVoxelSize),
            Vector3.One * CoverageSize);

    public Vector3I ReplaceCellMin
    {
        get
        {
            int padding = CoarseCellCount > 2 ? ReplacePaddingCoarseCells : 0;
            return CoarseCellMin + new Vector3I(padding, padding, padding);
        }
    }

    public Vector3I ReplaceCellMaxExclusive
    {
        get
        {
            int padding = CoarseCellCount > 2 ? ReplacePaddingCoarseCells : 0;
            Vector3I max = CoarseCellMaxExclusive - new Vector3I(padding, padding, padding);
            return new Vector3I(
                Mathf.Max(ReplaceCellMin.X, max.X),
                Mathf.Max(ReplaceCellMin.Y, max.Y),
                Mathf.Max(ReplaceCellMin.Z, max.Z));
        }
    }

    public bool HasReplaceInterior =>
        ReplaceCellMin.X < ReplaceCellMaxExclusive.X &&
        ReplaceCellMin.Y < ReplaceCellMaxExclusive.Y &&
        ReplaceCellMin.Z < ReplaceCellMaxExclusive.Z;

    public Aabb ReplaceLocalBounds
    {
        get
        {
            if (!HasReplaceInterior)
            {
                return default;
            }

            Vector3 min = new(
                ReplaceCellMin.X * CoarseVoxelSize,
                ReplaceCellMin.Y * CoarseVoxelSize,
                ReplaceCellMin.Z * CoarseVoxelSize);
            Vector3 max = new(
                ReplaceCellMaxExclusive.X * CoarseVoxelSize,
                ReplaceCellMaxExclusive.Y * CoarseVoxelSize,
                ReplaceCellMaxExclusive.Z * CoarseVoxelSize);
            return new Aabb(min, max - min);
        }
    }

    public string Summary
    {
        get
        {
            string replaceSummary = HasReplaceInterior
                ? $"{ReplaceCellMin}->{ReplaceCellMaxExclusive - Vector3I.One}"
                : "none";
            return
                $"{(HasPersistentEdits ? "edit_hi" : "detail_hi")} scale {DetailScale} cover {CoarseCellMin}->{CoarseCellMaxExclusive - Vector3I.One} replace {replaceSummary} pts {Data.PointsPerAxis}";
        }
    }

    public void MarkPersistentEdits()
    {
        HasPersistentEdits = true;
    }

    public bool ContainsWorldPosition(Vector3 worldPosition)
    {
        Vector3 min = Data.Origin;
        Vector3 max = min + (Vector3.One * CoverageSize);
        const float epsilon = 0.0001f;
        return
            worldPosition.X >= min.X - epsilon &&
            worldPosition.Y >= min.Y - epsilon &&
            worldPosition.Z >= min.Z - epsilon &&
            worldPosition.X <= max.X + epsilon &&
            worldPosition.Y <= max.Y + epsilon &&
            worldPosition.Z <= max.Z + epsilon;
    }

    public bool ShouldReplaceCoarseCell(int x, int y, int z)
    {
        if (!HasReplaceInterior)
        {
            return false;
        }

        return
            x >= ReplaceCellMin.X &&
            y >= ReplaceCellMin.Y &&
            z >= ReplaceCellMin.Z &&
            x < ReplaceCellMaxExclusive.X &&
            y < ReplaceCellMaxExclusive.Y &&
            z < ReplaceCellMaxExclusive.Z;
    }

    public VoxelDetailBrickData CreateMeshSnapshot()
    {
        VoxelChunkData dataSnapshot = Data.CreateMeshSnapshot(includeTransientDetailBrick: false);
        return new VoxelDetailBrickData(CoarseCellMin, CoarseCellCount, DetailScale, dataSnapshot, HasPersistentEdits);
    }

    public VoxelDetailBrickData CreateCopy()
    {
        return new VoxelDetailBrickData(
            CoarseCellMin,
            CoarseCellCount,
            DetailScale,
            Data.CreateEditableCopy(includeTransientDetailBrick: false),
            HasPersistentEdits);
    }

    public void Dispose()
    {
        Data?.Dispose();
    }

    public byte[] Serialize()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(SerializationVersion);
        writer.Write(CoarseCellMin.X);
        writer.Write(CoarseCellMin.Y);
        writer.Write(CoarseCellMin.Z);
        writer.Write(CoarseCellCount);
        writer.Write(DetailScale);
        writer.Write(HasPersistentEdits);
        writer.Write(Data.PointsPerAxis);
        writer.Write(Data.VoxelSize);
        writer.Write(Data.IsoLevel);
        writer.Write(Data.Origin.X);
        writer.Write(Data.Origin.Y);
        writer.Write(Data.Origin.Z);

        float[] densities = Data.CopyDensities();
        byte[] densityBytes = new byte[densities.Length * sizeof(float)];
        Buffer.BlockCopy(densities, 0, densityBytes, 0, densityBytes.Length);
        writer.Write(densityBytes.Length);
        writer.Write(densityBytes);

        byte[] materials = Data.CopyMaterials();
        writer.Write(materials.Length);
        writer.Write(materials);
        writer.Flush();
        return stream.ToArray();
    }

    public static VoxelDetailBrickData Deserialize(byte[] blob)
    {
        if (blob == null || blob.Length == 0)
        {
            throw new ArgumentException("Serialized detail brick blob is empty.", nameof(blob));
        }

        using MemoryStream stream = new(blob, writable: false);
        using BinaryReader reader = new(stream);
        int version = reader.ReadInt32();
        if (version is < 1 or > SerializationVersion)
        {
            throw new InvalidDataException($"Unsupported detail brick payload version {version}.");
        }

        Vector3I coarseCellMin = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        int coarseCellCount = reader.ReadInt32();
        int detailScale = reader.ReadInt32();
        bool hasPersistentEdits = version >= 2
            ? reader.ReadBoolean()
            : true;
        int pointsPerAxis = reader.ReadInt32();
        float voxelSize = reader.ReadSingle();
        float isoLevel = reader.ReadSingle();
        Vector3 origin = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        int densityByteLength = reader.ReadInt32();
        byte[] densityBytes = reader.ReadBytes(densityByteLength);
        float[] densities = new float[densityByteLength / sizeof(float)];
        Buffer.BlockCopy(densityBytes, 0, densities, 0, densityByteLength);

        int materialByteLength = reader.ReadInt32();
        byte[] materials = reader.ReadBytes(materialByteLength);

        VoxelChunkData data = new(pointsPerAxis, voxelSize, origin, isoLevel);
        data.LoadFromBuffers(densities, materials);
        return new VoxelDetailBrickData(coarseCellMin, coarseCellCount, detailScale, data, hasPersistentEdits);
    }
}
