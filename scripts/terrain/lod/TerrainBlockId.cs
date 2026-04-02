using Godot;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainBlockId(int Lod, Vector3I Index)
{
    public override string ToString()
    {
        return $"lod{Lod}:{Index.X},{Index.Y},{Index.Z}";
    }
}
