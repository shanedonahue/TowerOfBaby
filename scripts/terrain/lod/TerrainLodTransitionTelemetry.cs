namespace TowerOfBaby.Terrain;

internal sealed class TerrainLodSupersededBlockTransition
{
    public TerrainLodSupersededBlockTransition(TerrainBlockId outgoingBlockId, double removedAtSeconds)
    {
        OutgoingBlockId = outgoingBlockId;
        RemovedAtSeconds = removedAtSeconds;
    }

    public TerrainBlockId OutgoingBlockId { get; }
    public double RemovedAtSeconds { get; }
    public double? MarkReleasableAtSeconds { get; set; }
    public double? ReplacementVisualsReadyAtSeconds { get; set; }
    public double? ReplacementCollisionReadyAtSeconds { get; set; }
    public double? HiddenAtSeconds { get; set; }
    public double? ReleasedAtSeconds { get; set; }
    public TerrainBlockState? LastObservedState { get; set; }
    public bool LastOutgoingHasVisuals { get; set; }
    public bool LastVisualCoverageReady { get; set; }
    public bool LastPhysicsCoverageReady { get; set; }
    public string LastReason { get; set; } = "created";
    public string LastSuccessorIdsSummary { get; set; } = "none";
    public string LastSuccessorLodsSummary { get; set; } = "none";
    public string LastSuccessorStatesSummary { get; set; } = "none";
}

internal readonly record struct TerrainLodSuccessorCoverageStatus(
    string SuccessorIdsSummary,
    string SuccessorLodsSummary,
    string SuccessorStatesSummary,
    bool VisualCoverageReady,
    bool PhysicsCoverageReady,
    string VisualDeferralReason,
    string PhysicsDeferralReason)
{
    public bool FullCoverageReady => VisualCoverageReady && PhysicsCoverageReady;
}
