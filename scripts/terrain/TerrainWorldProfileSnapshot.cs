namespace TowerOfBaby.Terrain;

public sealed class TerrainWorldProfileSnapshot
{
    public int ActiveChunkCount { get; init; }
    public int LoadedChunkCount { get; init; }
    public int DesiredChunkCount { get; init; }
    public int PendingLoadCount { get; init; }
    public int RunningLoadCount { get; init; }
    public int PendingActivationCount { get; init; }
    public int DirtyRenderCount { get; init; }
    public int DirtyCollisionCount { get; init; }
    public int LastChunkLoadCount { get; init; }
    public int LastChunkActivationCount { get; init; }
    public int LastVisualRebuildCount { get; init; }
    public int LastCollisionRebuildCount { get; init; }
    public double LastChunkLoadMs { get; init; }
    public double LastChunkActivationMs { get; init; }
    public double LastVisualRebuildMs { get; init; }
    public double LastCollisionRebuildMs { get; init; }
    public int LastStartupChunkLoadCount { get; init; }
    public int LastPersistedChunkLoadCount { get; init; }
    public int LastGeneratedChunkLoadCount { get; init; }
    public double LastStartupChunkLoadMs { get; init; }
    public double LastPersistedChunkLoadMs { get; init; }
    public double LastGeneratedChunkLoadMs { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public long EvictedChunks { get; init; }
    public long CacheHitsDelta { get; init; }
    public long CacheMissesDelta { get; init; }
    public long EvictedChunksDelta { get; init; }
    public float InitialLoadProgress { get; init; }
    public bool InitialLoadComplete { get; init; }
}
