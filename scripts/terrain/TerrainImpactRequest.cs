using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public enum TerrainImpactShape
{
    Sphere = 0,
    Slash = 1
}

public enum TerrainImpactKind
{
    Custom = 0,
    BuildBrush = 1,
    CarveBrush = 2,
    MeleeSlash = 3,
    Fireball = 4,
    LightningStrike = 5,
    Gunshot = 6
}

// TerrainImpactRequest is the gameplay-facing bridge between abilities and the
// persisted terrain edit system. Abilities can describe "what happened" once,
// then the runtime converts that into the matching voxel stamp and detail request.
public readonly record struct TerrainImpactRequest(
    TerrainImpactShape Shape,
    TerrainImpactKind Kind,
    Vector3 Center,
    float Radius,
    Vector3 Direction,
    Vector3 SurfaceNormal,
    float Length,
    float Width,
    float Depth,
    float DensityDelta,
    float PaintStrength,
    float RetextureMargin,
    int RequestedDetailLevel,
    TerrainDetailRegionSource Source,
    string Reason,
    float Priority,
    bool Sticky)
{
    public string OperationName => Kind switch
    {
        TerrainImpactKind.BuildBrush => "build_brush",
        TerrainImpactKind.CarveBrush => "carve_brush",
        TerrainImpactKind.MeleeSlash => "melee_slash",
        TerrainImpactKind.Fireball => "fireball",
        TerrainImpactKind.LightningStrike => "lightning_strike",
        TerrainImpactKind.Gunshot => "gunshot",
        _ => "impact"
    };

    public string RegionReason => string.IsNullOrWhiteSpace(Reason)
        ? OperationName
        : Reason.Trim();

    public string Summary => Shape switch
    {
        TerrainImpactShape.Sphere =>
            $"{OperationName} sphere r {Radius:0.00} dd {DensityDelta:0.00} paint {PaintStrength:0.00}",
        TerrainImpactShape.Slash =>
            $"{OperationName} slash l/w/d {Length:0.00}/{Width:0.00}/{Depth:0.00} dd {DensityDelta:0.00} paint {PaintStrength:0.00}",
        _ => $"{OperationName} custom"
    };

    public TerrainEditStampData ToStamp(float minimumDirtyMargin)
    {
        return Shape switch
        {
            TerrainImpactShape.Sphere => TerrainEditStampData.FromSphere(
                new VoxelSphereEdit(
                    Center,
                    Radius,
                    DensityDelta,
                    PaintStrength,
                    RetextureMargin),
                minimumDirtyMargin),
            TerrainImpactShape.Slash => TerrainEditStampData.FromSlash(
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
                minimumDirtyMargin),
            _ => TerrainEditStampData.FromSphere(
                new VoxelSphereEdit(
                    Center,
                    Radius,
                    DensityDelta,
                    PaintStrength,
                    RetextureMargin),
                minimumDirtyMargin)
        };
    }

    public static TerrainImpactRequest CreateSphere(
        TerrainImpactKind kind,
        Vector3 center,
        float radius,
        float densityDelta,
        float paintStrength,
        float retextureMargin,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky)
    {
        return new TerrainImpactRequest(
            TerrainImpactShape.Sphere,
            kind,
            center,
            radius,
            Vector3.Zero,
            Vector3.Up,
            0.0f,
            0.0f,
            0.0f,
            densityDelta,
            paintStrength,
            retextureMargin,
            requestedDetailLevel,
            source,
            reason,
            priority,
            sticky);
    }

    public static TerrainImpactRequest CreateSlash(
        TerrainImpactKind kind,
        Vector3 center,
        Vector3 direction,
        Vector3 surfaceNormal,
        float length,
        float width,
        float depth,
        float densityDelta,
        float paintStrength,
        float retextureMargin,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky)
    {
        return new TerrainImpactRequest(
            TerrainImpactShape.Slash,
            kind,
            center,
            0.0f,
            direction,
            surfaceNormal,
            length,
            width,
            depth,
            densityDelta,
            paintStrength,
            retextureMargin,
            requestedDetailLevel,
            source,
            reason,
            priority,
            sticky);
    }
}

public static class TerrainImpactProfiles
{
    private const int DefaultDetailLevel = 2;
    private const float DefaultPriority = 100.0f;

    public static TerrainImpactRequest CreateBrush(
        Vector3 center,
        bool additive,
        float radius,
        float densityDelta,
        float retextureMargin)
    {
        return TerrainImpactRequest.CreateSphere(
            additive ? TerrainImpactKind.BuildBrush : TerrainImpactKind.CarveBrush,
            center,
            Mathf.Max(0.05f, radius),
            densityDelta,
            0.0f,
            Mathf.Max(0.0f, retextureMargin),
            DefaultDetailLevel,
            TerrainDetailRegionSource.Edit,
            additive ? "build_brush" : "carve_brush",
            DefaultPriority,
            true);
    }

    public static TerrainImpactRequest CreateMeleeSlash(
        Vector3 center,
        Vector3 direction,
        Vector3 surfaceNormal,
        float length,
        float width,
        float depth,
        float densityDelta,
        float scorchStrength,
        float retextureMargin)
    {
        return TerrainImpactRequest.CreateSlash(
            TerrainImpactKind.MeleeSlash,
            center,
            direction,
            surfaceNormal,
            Mathf.Max(0.05f, length),
            Mathf.Max(0.05f, width),
            Mathf.Max(0.05f, depth),
            densityDelta,
            Mathf.Max(0.0f, scorchStrength),
            Mathf.Max(0.0f, retextureMargin),
            DefaultDetailLevel,
            TerrainDetailRegionSource.Edit,
            "melee_slash",
            DefaultPriority,
            true);
    }

    public static TerrainImpactRequest CreateFireball(
        Vector3 center,
        float radius,
        float power,
        float voxelSize)
    {
        float resolvedRadius = Mathf.Max(radius, voxelSize * 1.5f);
        float resolvedPower = Mathf.Max(0.2f, power);
        return TerrainImpactRequest.CreateSphere(
            TerrainImpactKind.Fireball,
            center,
            resolvedRadius,
            -Mathf.Max(voxelSize * 2.0f, resolvedRadius * 1.25f) * resolvedPower,
            0.65f * resolvedPower,
            Mathf.Max(voxelSize, resolvedRadius * 0.45f),
            DefaultDetailLevel,
            TerrainDetailRegionSource.Edit,
            "fireball",
            DefaultPriority,
            true);
    }

    public static TerrainImpactRequest CreateLightningStrike(
        Vector3 center,
        Vector3 direction,
        Vector3 surfaceNormal,
        float length,
        float power,
        float voxelSize)
    {
        float resolvedPower = Mathf.Max(0.25f, power);
        float resolvedLength = Mathf.Max(length, voxelSize * 2.0f);
        float resolvedWidth = Mathf.Max(voxelSize * 0.65f, resolvedLength * 0.12f);
        float resolvedDepth = Mathf.Max(voxelSize * 0.45f, resolvedLength * 0.08f);
        return TerrainImpactRequest.CreateSlash(
            TerrainImpactKind.LightningStrike,
            center,
            direction,
            surfaceNormal,
            resolvedLength,
            resolvedWidth,
            resolvedDepth,
            -Mathf.Max(voxelSize * 1.2f, resolvedDepth * 3.4f) * resolvedPower,
            1.05f * resolvedPower,
            Mathf.Max(voxelSize, resolvedWidth * 0.9f),
            DefaultDetailLevel,
            TerrainDetailRegionSource.Edit,
            "lightning_strike",
            DefaultPriority,
            true);
    }

    public static TerrainImpactRequest CreateGunshot(
        Vector3 center,
        float power,
        float voxelSize)
    {
        float resolvedPower = Mathf.Max(0.15f, power);
        float radius = Mathf.Max(voxelSize * 0.55f, voxelSize * resolvedPower);
        return TerrainImpactRequest.CreateSphere(
            TerrainImpactKind.Gunshot,
            center,
            radius,
            -Mathf.Max(voxelSize * 0.75f, radius * 2.4f) * resolvedPower,
            0.12f * resolvedPower,
            Mathf.Max(voxelSize * 0.5f, radius * 0.6f),
            1,
            TerrainDetailRegionSource.Edit,
            "gunshot",
            DefaultPriority,
            true);
    }
}
