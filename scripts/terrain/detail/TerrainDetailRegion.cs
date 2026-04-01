using Godot;
using System;

namespace TowerOfBaby.Terrain;

public sealed class TerrainDetailRegion
{
    public TerrainDetailRegion(
        string id,
        Aabb localBounds,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority = 0.0f,
        bool sticky = false,
        bool dirty = true)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        Dirty = dirty;
        ApplyRequest(localBounds, requestedDetailLevel, source, reason, priority, sticky);
    }

    public string Id { get; }
    public Aabb LocalBounds { get; private set; }
    public int RequestedDetailLevel { get; private set; }
    public TerrainDetailRegionSource Source { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public float Priority { get; private set; }
    public bool Sticky { get; private set; }
    public bool Dirty { get; private set; }

    public void ApplyRequest(
        Aabb localBounds,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky)
    {
        bool changed =
            !AreBoundsEqual(LocalBounds, localBounds) ||
            RequestedDetailLevel != requestedDetailLevel ||
            Source != source ||
            !string.Equals(Reason, reason ?? string.Empty, StringComparison.Ordinal) ||
            !Mathf.IsEqualApprox(Priority, priority) ||
            Sticky != sticky;

        LocalBounds = localBounds;
        RequestedDetailLevel = Math.Max(1, requestedDetailLevel);
        Source = source;
        Reason = reason?.Trim() ?? string.Empty;
        Priority = priority;
        Sticky = sticky;
        Dirty |= changed;
    }

    public void Merge(Aabb localBounds, int requestedDetailLevel, float priority, bool sticky)
    {
        LocalBounds = Union(LocalBounds, localBounds);
        RequestedDetailLevel = Math.Max(RequestedDetailLevel, Math.Max(1, requestedDetailLevel));
        Priority = Mathf.Max(Priority, priority);
        Sticky |= sticky;
        Dirty = true;
    }

    public bool Overlaps(Aabb localBounds)
    {
        return LocalBounds.Intersects(localBounds);
    }

    public void ClearDirty()
    {
        Dirty = false;
    }

    public string Summary
    {
        get
        {
            string reasonText = string.IsNullOrWhiteSpace(Reason) ? "-" : Reason;
            return
                $"{Source}:{reasonText} lvl {RequestedDetailLevel} p {Priority:0.0} {(Sticky ? "sticky" : "temp")} {FormatBounds(LocalBounds)}{(Dirty ? "*" : string.Empty)}";
        }
    }

    private static bool AreBoundsEqual(Aabb a, Aabb b)
    {
        return a.Position == b.Position && a.Size == b.Size;
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

    private static string FormatBounds(Aabb bounds)
    {
        return
            $"min({bounds.Position.X:0.0},{bounds.Position.Y:0.0},{bounds.Position.Z:0.0}) size({bounds.Size.X:0.0},{bounds.Size.Y:0.0},{bounds.Size.Z:0.0})";
    }
}
