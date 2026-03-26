namespace TowerOfBaby.Terrain;

public sealed class TerrainWorldProfileSnapshot
{
    public int ActiveChunkCount { get; init; }
    public int ResidentChunkCount { get; init; }
    public int LoadedChunkCount { get; init; }
    public int RamCacheChunkCount { get; init; }
    public int DesiredChunkCount { get; init; }
    public int DesiredColumnCount { get; init; }
    public int PendingLoadCount { get; init; }
    public int RunningLoadCount { get; init; }
    public int PendingActivationCount { get; init; }
    public int PreparedChunkCount { get; init; }
    public int InFlightChunkCount { get; init; }
    public int ToAddCount { get; init; }
    public int ToReleaseCount { get; init; }
    public int FrontierSize { get; init; }
    public int VisitedCandidateCount { get; init; }
    public int DirtyRenderCount { get; init; }
    public int DirtyCollisionCount { get; init; }
    public int LastChunkLoadCount { get; init; }
    public int LastChunkActivationCount { get; init; }
    public int LastChunkReleaseCount { get; init; }
    public int LastVisualRebuildCount { get; init; }
    public int LastCollisionRebuildCount { get; init; }
    public double LastChunkLoadMs { get; init; }
    public double LastChunkActivationMs { get; init; }
    public double LastChunkReleaseMs { get; init; }
    public double LastVisualRebuildMs { get; init; }
    public double LastCollisionRebuildMs { get; init; }
    public double LastDesiredSearchMs { get; init; }
    public double LastPriorityEvaluationMs { get; init; }
    public double LastVisibilityHeuristicMs { get; init; }
    public int LastStartupChunkLoadCount { get; init; }
    public int LastPersistedChunkLoadCount { get; init; }
    public int LastRamCacheLoadCount { get; init; }
    public int LastGeneratedChunkLoadCount { get; init; }
    public double LastStartupChunkLoadMs { get; init; }
    public double LastPersistedChunkLoadMs { get; init; }
    public double LastRamCacheLoadMs { get; init; }
    public double LastGeneratedChunkLoadMs { get; init; }
    public long ResidentReuseHits { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public long EvictedChunks { get; init; }
    public long RamCacheHits { get; init; }
    public long StartupSnapshotHits { get; init; }
    public long DatabaseHits { get; init; }
    public long GenerationFallbacks { get; init; }
    public int PersistedChunkRecordCount { get; init; }
    public int StartupSnapshotChunkCount { get; init; }
    public int StartupDesiredCoverageCount { get; init; }
    public long SearchInvalidationCount { get; init; }
    public long StalePriorityRefreshCount { get; init; }
    public long FrontierCompactionCount { get; init; }
    public long DirtyPersistWrites { get; init; }
    public long StartupPromotionWrites { get; init; }
    public long CacheHitsDelta { get; init; }
    public long CacheMissesDelta { get; init; }
    public long EvictedChunksDelta { get; init; }
    public string SearchThrottleState { get; init; } = string.Empty;
    public string SearchInvalidationReason { get; init; } = string.Empty;
    public string LastSelectedChunkSummary { get; init; } = string.Empty;
    public string LastReleasedChunkSummary { get; init; } = string.Empty;
    public string LastChunkSourceSummary { get; init; } = string.Empty;
    public float InitialLoadProgress { get; init; }
    public bool InitialLoadComplete { get; init; }
}
