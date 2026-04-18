using Godot;
using System;
using System.IO;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public enum TerrainEditStampKind
{
    Sphere = 0,
    Slash = 1
}

public readonly record struct TerrainEditStampData(
    TerrainEditStampKind Kind,
    Vector3 Center,
    float BoundsRadius,
    float Radius,
    Vector3 Direction,
    Vector3 SurfaceNormal,
    float Length,
    float Width,
    float Depth,
    float DensityDelta,
    float PaintStrength,
    float RetextureMargin)
{
    public Aabb WorldBounds
    {
        get
        {
            float radius = Mathf.Max(0.001f, BoundsRadius);
            Vector3 min = Center - (Vector3.One * radius);
            return new Aabb(min, Vector3.One * (radius * 2.0f));
        }
    }

    public string Summary => Kind switch
    {
        TerrainEditStampKind.Sphere =>
            $"sphere r {Radius:0.00} dirty {BoundsRadius:0.00} dd {DensityDelta:0.00} paint {PaintStrength:0.00}",
        TerrainEditStampKind.Slash =>
            $"slash l/w/d {Length:0.00}/{Width:0.00}/{Depth:0.00} dirty {BoundsRadius:0.00} dd {DensityDelta:0.00} paint {PaintStrength:0.00}",
        _ => Kind.ToString()
    };

    public static TerrainEditStampData FromSphere(VoxelSphereEdit edit, float minimumDirtyMargin)
    {
        float dirtyMargin = Mathf.Max(edit.RetextureMargin, Mathf.Max(0.0f, minimumDirtyMargin));
        return new TerrainEditStampData(
            TerrainEditStampKind.Sphere,
            edit.Center,
            edit.Radius + dirtyMargin,
            edit.Radius,
            Vector3.Zero,
            Vector3.Up,
            0.0f,
            0.0f,
            0.0f,
            edit.DeltaDensity,
            edit.PaintStrength,
            edit.RetextureMargin);
    }

    public static TerrainEditStampData FromSlash(VoxelSlashEdit edit, float minimumDirtyMargin)
    {
        float dirtyMargin = Mathf.Max(edit.RetextureMargin, Mathf.Max(0.0f, minimumDirtyMargin));
        return new TerrainEditStampData(
            TerrainEditStampKind.Slash,
            edit.Center,
            edit.BoundingRadius + dirtyMargin,
            0.0f,
            edit.Direction,
            edit.SurfaceNormal,
            edit.Length,
            edit.Width,
            edit.Depth,
            edit.DensityDelta,
            edit.PaintStrength,
            edit.RetextureMargin);
    }

    public bool Overlaps(Aabb worldBounds)
    {
        return Intersects(WorldBounds, worldBounds);
    }

    public VoxelEditStats Apply(
        VoxelChunkData data,
        Func<Vector3, float, VoxelMaterialId> materialResolver)
    {
        return Kind switch
        {
            TerrainEditStampKind.Sphere => VoxelTerrainEditing.ApplySphere(
                data,
                new VoxelSphereEdit(Center, Radius, DensityDelta, PaintStrength, RetextureMargin),
                materialResolver),
            TerrainEditStampKind.Slash => VoxelTerrainEditing.ApplySlash(
                data,
                new VoxelSlashEdit(
                    Center,
                    Direction,
                    SurfaceNormal,
                    Length,
                    Width,
                    Depth,
                    DensityDelta,
                    PaintStrength,
                    RetextureMargin),
                materialResolver),
            _ => VoxelEditStats.None
        };
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((int)Kind);
        WriteVector3(writer, Center);
        writer.Write(BoundsRadius);
        writer.Write(Radius);
        WriteVector3(writer, Direction);
        WriteVector3(writer, SurfaceNormal);
        writer.Write(Length);
        writer.Write(Width);
        writer.Write(Depth);
        writer.Write(DensityDelta);
        writer.Write(PaintStrength);
        writer.Write(RetextureMargin);
    }

    public static TerrainEditStampData Deserialize(BinaryReader reader)
    {
        return new TerrainEditStampData(
            (TerrainEditStampKind)reader.ReadInt32(),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadSingle(),
            ReadVector3(reader),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
    }

    private static bool Intersects(Aabb a, Aabb b)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        return
            a.Position.X < bEnd.X &&
            aEnd.X > b.Position.X &&
            a.Position.Y < bEnd.Y &&
            aEnd.Y > b.Position.Y &&
            a.Position.Z < bEnd.Z &&
            aEnd.Z > b.Position.Z;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
