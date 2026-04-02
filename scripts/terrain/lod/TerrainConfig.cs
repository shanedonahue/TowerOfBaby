using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainConfig
{
    public int PointsPerAxis { get; init; }
    public float BaseVoxelSize { get; init; }
    public float BaseY { get; init; }
    public int Seed { get; init; }
    public float TerrainHeight { get; init; }
    public float DetailHeight { get; init; }
    public float CaveScale { get; init; }
    public float CaveThreshold { get; init; }
    public int CoarseRadiusXZ { get; init; } = 1;
    public int VerticalRadius { get; init; }
    public int FieldBuildsPerFrame { get; init; } = 4;
    public int MeshBuildsPerFrame { get; init; } = 4;
    public int CommitsPerFrame { get; init; } = 4;
    public int ReleasesPerFrame { get; init; } = 8;
    public bool GenerateCollisionForCoarseLods { get; init; }
    public bool GenerateTangents { get; init; }
    public VoxelMeshColorMode MeshColorMode { get; init; } = VoxelMeshColorMode.MaterialTint;
}
