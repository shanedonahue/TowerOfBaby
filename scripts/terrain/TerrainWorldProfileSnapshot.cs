namespace TowerOfBaby.Terrain;

public sealed class TerrainWorldProfileSnapshot
{
    public bool TerrainStatsEnabled { get; init; }
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
    public int PendingMeshBuildCount { get; init; }
    public int DeferredMeshBuildCount { get; init; }
    public int RunningMeshBuildCount { get; init; }
    public int PendingMeshCommitCount { get; init; }
    public double LastChunkLoadMs { get; init; }
    public double LastChunkActivationMs { get; init; }
    public double LastChunkReleaseMs { get; init; }
    public double LastVisualRebuildMs { get; init; }
    public double LastCollisionRebuildMs { get; init; }
    public int LastMeshWorkerBuildCount { get; init; }
    public double LastMeshWorkerBuildMs { get; init; }
    public double MeshWorkerQueueWaitMs { get; init; }
    public double LastMeshWorkerQueueWaitMs { get; init; }
    public double AverageMeshWorkerQueueWaitMs { get; init; }
    public double PeakMeshWorkerQueueWaitMs { get; init; }
    public int HighPriorityMeshQueueDepth { get; init; }
    public int LowPriorityMeshQueueDepth { get; init; }
    public long LowPriorityDeferredMeshBuildCount { get; init; }
    public long SkippedLowPriorityMeshBuildCount { get; init; }
    public int LastSkippedLowPriorityMeshBuildCount { get; init; }
    public long SuppressedDuplicateMeshBuildCount { get; init; }
    public int LastSuppressedDuplicateMeshBuildCount { get; init; }
    public long HighPriorityEnqueueBudgetHitCount { get; init; }
    public int LastHighPriorityEnqueueBudgetHitCount { get; init; }
    public long DeferredHighPriorityEnqueueCount { get; init; }
    public int LastDeferredHighPriorityEnqueueCount { get; init; }
    public long SmoothedHighPriorityEnqueueCount { get; init; }
    public int LastSmoothedHighPriorityEnqueueCount { get; init; }
    public bool PressureModeActive { get; init; }
    public long PressureModeActiveFrameCount { get; init; }
    public int PressureModeActivationCount { get; init; }
    public int LastDeferredDetailPromotionCount { get; init; }
    public int LastDeferredPromotionReevaluationCount { get; init; }
    public int LastAvoidedDeferredReevaluationCount { get; init; }
    public int LastSuppressedDeferredLogRepeatCount { get; init; }
    public int LastRequestsReactivatedByMeshCompletionCount { get; init; }
    public int LastRequestsReactivatedByCooldownExpiryCount { get; init; }
    public int LastRequestsReactivatedByPressureExitCount { get; init; }
    public int LastCoalescedRebuildRequestCount { get; init; }
    public string MeshBackendName { get; init; } = string.Empty;
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
    public long DeformOperationCount { get; init; }
    public long TotalEditedChunkCount { get; init; }
    public long TotalEditedSampleCount { get; init; }
    public double TotalEditedDirtyBoundsVolume { get; init; }
    public long EditDetailPromotionCount { get; init; }
    public int LastDeformEditedChunkCount { get; init; }
    public int LastDeformEditedSampleCount { get; init; }
    public double LastDeformDirtyBoundsVolume { get; init; }
    public int LastDeformEditDetailPromotionCount { get; init; }
    public double LastDeformMs { get; init; }
    public string LastDeformKind { get; init; } = string.Empty;
    public long MeshBuildWorkerCount { get; init; }
    public double MeshBuildWorkerMs { get; init; }
    public double LastMeshBuildWorkerMs { get; init; }
    public double AverageCoarseMeshWorkerHeapDeltaKiB { get; init; }
    public double PeakCoarseMeshWorkerHeapDeltaKiB { get; init; }
    public double AverageDetailMeshWorkerHeapDeltaKiB { get; init; }
    public double PeakDetailMeshWorkerHeapDeltaKiB { get; init; }
    public long MeshRebuildCount { get; init; }
    public double MeshRebuildMs { get; init; }
    public double LastMeshRebuildMs { get; init; }
    public long CollisionRebuildCount { get; init; }
    public double CollisionRebuildMs { get; init; }
    public double LastCollisionChunkRebuildMs { get; init; }
    public long DeferredDetailPromotionCount { get; init; }
    public long DeferredPromotionReevaluationCount { get; init; }
    public long AvoidedDeferredReevaluationCount { get; init; }
    public long SuppressedDeferredLogRepeatCount { get; init; }
    public long RequestsReactivatedByMeshCompletionCount { get; init; }
    public long RequestsReactivatedByCooldownExpiryCount { get; init; }
    public long RequestsReactivatedByPressureExitCount { get; init; }
    public long CoalescedRebuildRequestCount { get; init; }
    public long PreventedCoverageGapReleaseCount { get; init; }
    public int LastPreventedCoverageGapReleaseCount { get; init; }
    public long ReplacementCoverageWaitCount { get; init; }
    public int LastReplacementCoverageWaitCount { get; init; }
    public long ChunksHeldForCoverageSafetyCount { get; init; }
    public int LastChunksHeldForCoverageSafetyCount { get; init; }
    public long NormalDebugMismatchCount { get; init; }
    public int LastNormalDebugMismatchCount { get; init; }
    public long TangentGenerationCount { get; init; }
    public int LastTangentGenerationCount { get; init; }
    public long VertexTintEnabledFrameCount { get; init; }
    public int LastVertexTintEnabledFrameCount { get; init; }
    public long PersistenceLoadCount { get; init; }
    public double PersistenceLoadMs { get; init; }
    public double LastPersistenceLoadMs { get; init; }
    public string LastPersistenceLoadScope { get; init; } = string.Empty;
    public long PersistenceSaveCount { get; init; }
    public double PersistenceSaveMs { get; init; }
    public double LastPersistenceSaveMs { get; init; }
    public string LastPersistenceSaveScope { get; init; } = string.Empty;
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
    public BiomeId TrackedBiomeId { get; init; }
    public string TrackedBiomeSummary { get; init; } = string.Empty;
    public int TrackedStructureCount { get; init; }
    public TerrainStructureType TrackedStructureType { get; init; }
    public bool TrackedStructureRequestsHigherDetail { get; init; }
    public string TrackedStructureSummary { get; init; } = string.Empty;
    public int TrackedDetailRegionCount { get; init; }
    public int TrackedDirtyDetailRegionCount { get; init; }
    public int TrackedMaxDetailLevel { get; init; }
    public string TrackedDetailSourceSummary { get; init; } = string.Empty;
    public string TrackedDetailSummary { get; init; } = string.Empty;
    public string TrackedDetailPromotionStateSummary { get; init; } = string.Empty;
    public bool TrackedDetailBrickActive { get; init; }
    public string TrackedDetailBrickSummary { get; init; } = string.Empty;
    public int TrackedDetailBrickTriangleCount { get; init; }
    public int TrackedDetailBrickReplaceCoarseCellCount { get; init; }
    public bool TrackedEditedDetailActive { get; init; }
    public string TrackedEditedDetailSummary { get; init; } = string.Empty;
    public int TrackedEditedDetailTriangleCount { get; init; }
    public int TrackedEditedReplaceCoarseCellCount { get; init; }
    public string TrackedRenderDirtyBoundsSummary { get; init; } = string.Empty;
    public string TrackedCollisionDirtyBoundsSummary { get; init; } = string.Empty;
    public string TrackedCoverageStateSummary { get; init; } = string.Empty;
    public string LastSelectedChunkSummary { get; init; } = string.Empty;
    public string LastReleasedChunkSummary { get; init; } = string.Empty;
    public string LastChunkSourceSummary { get; init; } = string.Empty;
    public float InitialLoadProgress { get; init; }
    public bool InitialLoadComplete { get; init; }
}
