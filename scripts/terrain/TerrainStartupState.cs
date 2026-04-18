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

public sealed class TerrainLodStartupState
{
    public Vector3 PlayerPosition { get; init; }
    public List<TerrainLodStartupBlockDescriptor> Blocks { get; } = new();
}

public sealed record TerrainLodStartupBlockDescriptor(TerrainBlockId BlockId, bool WasVisible);
public sealed record TerrainLodStartupBlockSnapshot(TerrainBlockId BlockId, bool WasVisible, VoxelChunkData Data);
