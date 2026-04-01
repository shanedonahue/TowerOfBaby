using Godot;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainStructureInstance(
    string Id,
    TerrainStructureType Type,
    Transform3D Transform,
    Vector3 InfluenceExtents,
    float Priority,
    bool RequestHigherTerrainDetail,
    string[] Tags)
{
    public Vector3 AnchorPosition => Transform.Origin;

    public Aabb InfluenceBounds =>
        new(AnchorPosition - InfluenceExtents, InfluenceExtents * 2.0f);

    public bool Overlaps(Aabb bounds)
    {
        return InfluenceBounds.Intersects(bounds);
    }

    public string Summary
    {
        get
        {
            string tagSummary = Tags == null || Tags.Length == 0
                ? "-"
                : string.Join(",", Tags);
            return
                $"{Id} {Type} p {Priority:0.00} detail {(RequestHigherTerrainDetail ? "high" : "normal")} tags {tagSummary}";
        }
    }
}
