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

    public bool OverlapsPrecisely(Aabb worldBounds)
    {
        return Kind switch
        {
            TerrainEditStampKind.Sphere => IntersectsSphere(worldBounds, Center, Mathf.Max(0.001f, BoundsRadius)),
            TerrainEditStampKind.Slash => IntersectsSlash(worldBounds, this),
            _ => Intersects(WorldBounds, worldBounds)
        };
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

    private static bool IntersectsSphere(Aabb bounds, Vector3 center, float radius)
    {
        Vector3 min = bounds.Position;
        Vector3 max = bounds.End;
        Vector3 clamped = new(
            Mathf.Clamp(center.X, min.X, max.X),
            Mathf.Clamp(center.Y, min.Y, max.Y),
            Mathf.Clamp(center.Z, min.Z, max.Z));
        return clamped.DistanceSquaredTo(center) <= (radius * radius);
    }

    private static bool IntersectsSlash(Aabb bounds, TerrainEditStampData stamp)
    {
        Vector3 normal = SafeNormalized(stamp.SurfaceNormal, Vector3.Up);
        Vector3 direction = SafeNormalized(ProjectOntoPlane(stamp.Direction, normal), Vector3.Forward);
        Vector3 across = normal.Cross(direction);
        across = across.LengthSquared() > 0.0001f
            ? across.Normalized()
            : SafeNormalized(normal.Cross(Vector3.Forward), Vector3.Right);

        Vector3 center = bounds.Position + (bounds.Size * 0.5f);
        Vector3 extents = bounds.Size * 0.5f;
        Vector3 delta = center - stamp.Center;
        float retexturePadding = Mathf.Max(stamp.RetextureMargin, 0.0f);
        float halfLength = Mathf.Max((stamp.Length * 0.5f) + (retexturePadding * 0.5f), stamp.BoundsRadius * 0.08f);
        float halfWidth = Mathf.Max((stamp.Width * 0.5f) + retexturePadding, stamp.BoundsRadius * 0.08f);
        float halfDepth = Mathf.Max((stamp.Depth * 0.5f) + (retexturePadding * 0.75f), stamp.BoundsRadius * 0.08f);

        return
            Mathf.Abs(delta.Dot(direction)) <= halfLength + ComputeProjectedExtent(extents, direction) &&
            Mathf.Abs(delta.Dot(across)) <= halfWidth + ComputeProjectedExtent(extents, across) &&
            Mathf.Abs(delta.Dot(normal)) <= halfDepth + ComputeProjectedExtent(extents, normal);
    }

    private static float ComputeProjectedExtent(Vector3 extents, Vector3 axis)
    {
        return
            (Mathf.Abs(axis.X) * extents.X) +
            (Mathf.Abs(axis.Y) * extents.Y) +
            (Mathf.Abs(axis.Z) * extents.Z);
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
