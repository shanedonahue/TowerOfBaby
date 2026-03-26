namespace TowerOfBaby.Terrain;

public enum TerrainChunkLoadSource
{
    Resident,
    RamCache,
    StartupSnapshot,
    PersistedChunk,
    ProceduralGeneration
}
