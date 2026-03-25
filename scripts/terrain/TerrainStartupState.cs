using Godot;
using System.Collections.Generic;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainStartupState
{
    public Vector3 PlayerPosition { get; init; }
    public List<TerrainStartupChunkDescriptor> Chunks { get; } = new();
}

public sealed record TerrainStartupChunkDescriptor(Vector3I Key, bool WasActive);
public sealed record TerrainStartupChunkSnapshot(Vector3I Key, bool WasActive, VoxelChunkData Data);
