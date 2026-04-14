using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace TowerOfBaby.Terrain;

public sealed class TerrainEditRegion
{
    private const int SerializationMagic = 0x54455247;
    private const int SerializationVersion = 1;
    private readonly TerrainEditStampData[] _stamps;

    public TerrainEditRegion(
        string id,
        Aabb worldBounds,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky,
        IReadOnlyList<TerrainEditStampData> stamps)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        WorldBounds = NormalizeBounds(worldBounds);
        RequestedDetailLevel = Math.Max(1, requestedDetailLevel);
        Source = source;
        Reason = reason?.Trim() ?? string.Empty;
        Priority = priority;
        Sticky = sticky;
        _stamps = CopyStamps(stamps);
    }

    public string Id { get; }
    public Aabb WorldBounds { get; }
    public int RequestedDetailLevel { get; }
    public TerrainDetailRegionSource Source { get; }
    public string Reason { get; }
    public float Priority { get; }
    public bool Sticky { get; }
    public IReadOnlyList<TerrainEditStampData> Stamps => _stamps;
    public int StampCount => _stamps.Length;

    public bool Overlaps(Aabb worldBounds)
    {
        return Intersects(WorldBounds, worldBounds);
    }

    public TerrainEditRegion AppendStamp(TerrainEditStampData stamp, int requestedDetailLevel, float priority)
    {
        TerrainEditStampData[] nextStamps = new TerrainEditStampData[_stamps.Length + 1];
        Array.Copy(_stamps, nextStamps, _stamps.Length);
        nextStamps[^1] = stamp;
        return new TerrainEditRegion(
            Id,
            Union(WorldBounds, stamp.WorldBounds),
            Math.Max(RequestedDetailLevel, requestedDetailLevel),
            Source,
            Reason,
            Mathf.Max(Priority, priority),
            Sticky,
            nextStamps);
    }

    public TerrainEditRegion Merge(TerrainEditRegion other)
    {
        if (other == null)
        {
            return this;
        }

        TerrainEditStampData[] mergedStamps = new TerrainEditStampData[_stamps.Length + other._stamps.Length];
        Array.Copy(_stamps, 0, mergedStamps, 0, _stamps.Length);
        Array.Copy(other._stamps, 0, mergedStamps, _stamps.Length, other._stamps.Length);
        return new TerrainEditRegion(
            Id,
            Union(WorldBounds, other.WorldBounds),
            Math.Max(RequestedDetailLevel, other.RequestedDetailLevel),
            Source,
            Reason,
            Mathf.Max(Priority, other.Priority),
            Sticky || other.Sticky,
            mergedStamps);
    }

    public float GetTargetVoxelSize(float baseVoxelSize)
    {
        int baseDetailScale = GetBaseDetailScale(RequestedDetailLevel);
        return Mathf.Max(0.0001f, baseVoxelSize / baseDetailScale);
    }

    public int ResolveDetailScale(float baseVoxelSize, float blockVoxelSize, int maxDetailScale)
    {
        float targetVoxelSize = GetTargetVoxelSize(baseVoxelSize);
        int baseDetailScale = GetBaseDetailScale(RequestedDetailLevel);
        int resolved = Mathf.CeilToInt(blockVoxelSize / targetVoxelSize);
        resolved = Math.Max(baseDetailScale, resolved);
        return Mathf.Clamp(resolved, 2, Math.Max(2, maxDetailScale));
    }

    public bool TryBuildLocalRegion(
        Vector3 blockOrigin,
        float blockSize,
        out TerrainPersistedDetailRegionData localRegion)
    {
        Aabb blockBounds = new(blockOrigin, Vector3.One * blockSize);
        if (!TryIntersect(WorldBounds, blockBounds, out Aabb worldIntersection))
        {
            localRegion = null!;
            return false;
        }

        localRegion = new TerrainPersistedDetailRegionData(
            Id,
            new Aabb(worldIntersection.Position - blockOrigin, worldIntersection.Size),
            RequestedDetailLevel,
            Source,
            Reason,
            Priority,
            Sticky);
        return true;
    }

    public string BuildSummary(float baseVoxelSize)
    {
        return
            $"{Source}:{(string.IsNullOrWhiteSpace(Reason) ? "-" : Reason)} lvl {RequestedDetailLevel} target {GetTargetVoxelSize(baseVoxelSize):0.00}m " +
            $"stamps {StampCount} {(Sticky ? "sticky" : "temp")} {FormatBounds(WorldBounds)}";
    }

    public byte[] Serialize()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(SerializationMagic);
        writer.Write(SerializationVersion);
        writer.Write(Id ?? string.Empty);
        WriteAabb(writer, WorldBounds);
        writer.Write(RequestedDetailLevel);
        writer.Write((int)Source);
        writer.Write(Reason ?? string.Empty);
        writer.Write(Priority);
        writer.Write(Sticky);
        writer.Write(_stamps.Length);
        foreach (TerrainEditStampData stamp in _stamps)
        {
            stamp.Serialize(writer);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static TerrainEditRegion Deserialize(byte[] blob)
    {
        if (blob == null || blob.Length == 0)
        {
            throw new InvalidDataException("Terrain edit region payload is empty.");
        }

        using MemoryStream stream = new(blob, writable: false);
        using BinaryReader reader = new(stream);
        int magic = reader.ReadInt32();
        if (magic != SerializationMagic)
        {
            throw new InvalidDataException($"Unexpected terrain edit region payload header {magic}.");
        }

        int version = reader.ReadInt32();
        if (version is < 1 or > SerializationVersion)
        {
            throw new InvalidDataException($"Unsupported terrain edit region payload version {version}.");
        }

        string id = reader.ReadString();
        Aabb worldBounds = ReadAabb(reader);
        int requestedDetailLevel = reader.ReadInt32();
        TerrainDetailRegionSource source = (TerrainDetailRegionSource)reader.ReadInt32();
        string reason = reader.ReadString();
        float priority = reader.ReadSingle();
        bool sticky = reader.ReadBoolean();
        int stampCount = reader.ReadInt32();
        TerrainEditStampData[] stamps = new TerrainEditStampData[stampCount];
        for (int i = 0; i < stampCount; i++)
        {
            stamps[i] = TerrainEditStampData.Deserialize(reader);
        }

        return new TerrainEditRegion(
            id,
            worldBounds,
            requestedDetailLevel,
            source,
            reason,
            priority,
            sticky,
            stamps);
    }

    private static TerrainEditStampData[] CopyStamps(IReadOnlyList<TerrainEditStampData> stamps)
    {
        if (stamps == null || stamps.Count == 0)
        {
            return Array.Empty<TerrainEditStampData>();
        }

        TerrainEditStampData[] copy = new TerrainEditStampData[stamps.Count];
        for (int i = 0; i < stamps.Count; i++)
        {
            copy[i] = stamps[i];
        }

        return copy;
    }

    private static int GetBaseDetailScale(int requestedDetailLevel)
    {
        return requestedDetailLevel >= 2 ? 3 : 2;
    }

    private static Aabb NormalizeBounds(Aabb bounds)
    {
        Vector3 start = bounds.Position;
        Vector3 end = bounds.Position + bounds.Size;
        Vector3 min = new(
            Mathf.Min(start.X, end.X),
            Mathf.Min(start.Y, end.Y),
            Mathf.Min(start.Z, end.Z));
        Vector3 max = new(
            Mathf.Max(start.X, end.X),
            Mathf.Max(start.Y, end.Y),
            Mathf.Max(start.Z, end.Z));
        return new Aabb(min, max - min);
    }

    private static bool TryIntersect(Aabb a, Aabb b, out Aabb intersection)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Max(a.Position.X, b.Position.X),
            Mathf.Max(a.Position.Y, b.Position.Y),
            Mathf.Max(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Min(aEnd.X, bEnd.X),
            Mathf.Min(aEnd.Y, bEnd.Y),
            Mathf.Min(aEnd.Z, bEnd.Z));
        Vector3 size = max - min;
        if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
        {
            intersection = default;
            return false;
        }

        intersection = new Aabb(min, size);
        return true;
    }

    private static bool Intersects(Aabb a, Aabb b)
    {
        return TryIntersect(a, b, out _);
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

    private static void WriteAabb(BinaryWriter writer, Aabb bounds)
    {
        writer.Write(bounds.Position.X);
        writer.Write(bounds.Position.Y);
        writer.Write(bounds.Position.Z);
        writer.Write(bounds.Size.X);
        writer.Write(bounds.Size.Y);
        writer.Write(bounds.Size.Z);
    }

    private static Aabb ReadAabb(BinaryReader reader)
    {
        return new Aabb(
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }

    private static string FormatBounds(Aabb bounds)
    {
        return
            $"min({bounds.Position.X:0.0},{bounds.Position.Y:0.0},{bounds.Position.Z:0.0}) size({bounds.Size.X:0.0},{bounds.Size.Y:0.0},{bounds.Size.Z:0.0})";
    }
}
