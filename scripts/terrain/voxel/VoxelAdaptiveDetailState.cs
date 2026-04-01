using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct VoxelAdaptiveDetailPersistenceMetrics(
    int DetailBrickCount,
    int DetailRegionCount,
    int SerializedByteCount,
    int SchemaVersion)
{
    public static readonly VoxelAdaptiveDetailPersistenceMetrics None = new(0, 0, 0, 0);

    public bool HasPayload => DetailBrickCount > 0 || SerializedByteCount > 0;

    public VoxelAdaptiveDetailPersistenceMetrics Add(VoxelAdaptiveDetailPersistenceMetrics other)
    {
        return new VoxelAdaptiveDetailPersistenceMetrics(
            DetailBrickCount + other.DetailBrickCount,
            DetailRegionCount + other.DetailRegionCount,
            SerializedByteCount + other.SerializedByteCount,
            Math.Max(SchemaVersion, other.SchemaVersion));
    }
}

public readonly record struct VoxelAdaptiveDetailPersistencePayload(
    byte[] Blob,
    VoxelAdaptiveDetailPersistenceMetrics Metrics)
{
    public static readonly VoxelAdaptiveDetailPersistencePayload None =
        new(Array.Empty<byte>(), VoxelAdaptiveDetailPersistenceMetrics.None);

    public bool HasPayload => Metrics.HasPayload && Blob != null && Blob.Length > 0;
}

public sealed class VoxelAdaptiveDetailState
{
    private const int Magic = 0x54424441;
    public const int CurrentSchemaVersion = 1;

    public VoxelAdaptiveDetailState(
        VoxelDetailBrickData detailBrick,
        TerrainPersistedDetailRegionData[] persistedRegions,
        int schemaVersion = CurrentSchemaVersion,
        bool legacyPayload = false)
    {
        DetailBrick = detailBrick;
        PersistedRegions = persistedRegions ?? Array.Empty<TerrainPersistedDetailRegionData>();
        SchemaVersion = schemaVersion;
        LegacyPayload = legacyPayload;
    }

    public VoxelDetailBrickData DetailBrick { get; }
    public TerrainPersistedDetailRegionData[] PersistedRegions { get; }
    public int SchemaVersion { get; }
    public bool LegacyPayload { get; }

    public VoxelAdaptiveDetailPersistenceMetrics BuildMetrics(int serializedByteCount)
    {
        return new VoxelAdaptiveDetailPersistenceMetrics(
            DetailBrick?.HasPersistentEdits == true ? 1 : 0,
            PersistedRegions.Length,
            serializedByteCount,
            SchemaVersion);
    }

    public byte[] Serialize()
    {
        byte[] detailBrickBlob = DetailBrick?.Serialize() ?? Array.Empty<byte>();

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(Magic);
        writer.Write(CurrentSchemaVersion);
        writer.Write(PersistedRegions.Length);
        foreach (TerrainPersistedDetailRegionData region in PersistedRegions)
        {
            writer.Write(region.Id ?? string.Empty);
            WriteAabb(writer, region.LocalBounds);
            writer.Write(region.RequestedDetailLevel);
            writer.Write((int)region.Source);
            writer.Write(region.Reason ?? string.Empty);
            writer.Write(region.Priority);
            writer.Write(region.Sticky);
        }

        writer.Write(detailBrickBlob.Length);
        if (detailBrickBlob.Length > 0)
        {
            writer.Write(detailBrickBlob);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static VoxelAdaptiveDetailState Deserialize(byte[] blob)
    {
        if (blob == null || blob.Length == 0)
        {
            return new VoxelAdaptiveDetailState(null, Array.Empty<TerrainPersistedDetailRegionData>());
        }

        using MemoryStream stream = new(blob, writable: false);
        using BinaryReader reader = new(stream);
        int header = reader.ReadInt32();
        if (header != Magic)
        {
            VoxelDetailBrickData legacyBrick = VoxelDetailBrickData.Deserialize(blob);
            TerrainPersistedDetailRegionData[] legacyRegions = legacyBrick?.HasPersistentEdits == true
                ? [TerrainPersistedDetailRegionData.CreateLegacyEditFallback(legacyBrick.LocalBounds)]
                : Array.Empty<TerrainPersistedDetailRegionData>();
            return new VoxelAdaptiveDetailState(legacyBrick, legacyRegions, schemaVersion: 0, legacyPayload: true);
        }

        int schemaVersion = reader.ReadInt32();
        if (schemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported adaptive detail payload schema {schemaVersion}.");
        }

        int regionCount = reader.ReadInt32();
        List<TerrainPersistedDetailRegionData> persistedRegions = new(regionCount);
        for (int i = 0; i < regionCount; i++)
        {
            string id = reader.ReadString();
            Aabb localBounds = ReadAabb(reader);
            int requestedDetailLevel = reader.ReadInt32();
            TerrainDetailRegionSource source = (TerrainDetailRegionSource)reader.ReadInt32();
            string reason = reader.ReadString();
            float priority = reader.ReadSingle();
            bool sticky = reader.ReadBoolean();
            persistedRegions.Add(new TerrainPersistedDetailRegionData(
                id,
                localBounds,
                requestedDetailLevel,
                source,
                reason,
                priority,
                sticky));
        }

        int detailBrickByteLength = reader.ReadInt32();
        VoxelDetailBrickData detailBrick = null;
        if (detailBrickByteLength > 0)
        {
            byte[] detailBrickBlob = reader.ReadBytes(detailBrickByteLength);
            detailBrick = VoxelDetailBrickData.Deserialize(detailBrickBlob);
        }

        if (detailBrick?.HasPersistentEdits == true && persistedRegions.Count == 0)
        {
            persistedRegions.Add(TerrainPersistedDetailRegionData.CreateLegacyEditFallback(detailBrick.LocalBounds));
        }

        return new VoxelAdaptiveDetailState(detailBrick, persistedRegions.ToArray(), schemaVersion, legacyPayload: false);
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
}
