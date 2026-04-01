using Godot;
using System;

namespace TowerOfBaby.Terrain;

public sealed class TerrainPersistedDetailRegionData
{
    public TerrainPersistedDetailRegionData(
        string id,
        Aabb localBounds,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        LocalBounds = NormalizeBounds(localBounds);
        RequestedDetailLevel = Math.Max(1, requestedDetailLevel);
        Source = source;
        Reason = reason?.Trim() ?? string.Empty;
        Priority = priority;
        Sticky = sticky;
    }

    public string Id { get; }
    public Aabb LocalBounds { get; }
    public int RequestedDetailLevel { get; }
    public TerrainDetailRegionSource Source { get; }
    public string Reason { get; }
    public float Priority { get; }
    public bool Sticky { get; }

    public string Summary
    {
        get
        {
            string reasonText = string.IsNullOrWhiteSpace(Reason) ? "-" : Reason;
            return $"{Source}:{reasonText} lvl {RequestedDetailLevel} p {Priority:0.0} {(Sticky ? "sticky" : "temp")} {FormatBounds(LocalBounds)}";
        }
    }

    public static TerrainPersistedDetailRegionData CreateLegacyEditFallback(Aabb localBounds)
    {
        return new TerrainPersistedDetailRegionData(
            TerrainChunk.EditedDetailRegionRequestId,
            localBounds,
            2,
            TerrainDetailRegionSource.Edit,
            TerrainChunk.EditedDetailRegionReason,
            priority: 100.0f,
            sticky: true);
    }

    private static Aabb NormalizeBounds(Aabb localBounds)
    {
        Vector3 start = localBounds.Position;
        Vector3 end = localBounds.Position + localBounds.Size;
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

    private static string FormatBounds(Aabb bounds)
    {
        return
            $"min({bounds.Position.X:0.0},{bounds.Position.Y:0.0},{bounds.Position.Z:0.0}) size({bounds.Size.X:0.0},{bounds.Size.Y:0.0},{bounds.Size.Z:0.0})";
    }
}
