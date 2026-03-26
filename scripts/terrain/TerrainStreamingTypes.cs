using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal enum DesiredSearchThrottleState
{
    FrontierLimited,
    BudgetLimited,
    ThresholdLimited
}

internal readonly record struct TerrainDesiredSetContext(
    Vector2I CenterChunk,
    int SearchRadius,
    int MaxColumns,
    float GuaranteedRadius)
{
    public bool Contains(Vector2I key, float extraMargin = 0.35f)
    {
        Vector2 offset = new(key.X - CenterChunk.X, key.Y - CenterChunk.Y);
        return offset.Length() <= SearchRadius + extraMargin;
    }
}

internal sealed record ColumnPriorityInfo(
    Vector2I Key,
    float TotalScore,
    float Distance,
    float ForwardAlignment,
    float Visibility,
    float ResidentBonus,
    float HysteresisBonus,
    float AdjacencyBonus,
    float ShoulderBonus,
    float LoadCostBonus,
    TerrainChunkLoadSource EstimatedSource,
    bool IsGuaranteed)
{
    public string Summary =>
        $"{Key} score {TotalScore:0.0} dist {Distance:0.0} align {ForwardAlignment:0.00} vis {Visibility:0.00} resident {ResidentBonus:0.0} retain {HysteresisBonus:0.0} adj {AdjacencyBonus:0.0} shoulder {ShoulderBonus:0.0} cost {LoadCostBonus:0.0} src {EstimatedSource}";
}

internal sealed record ChunkPriorityInfo(
    Vector3I Key,
    float TotalScore,
    float Distance,
    float ForwardAlignment,
    float Visibility,
    float HysteresisBonus,
    float AdjacencyBonus,
    float ShoulderBonus,
    float LoadCostBonus,
    float VerticalBias,
    TerrainChunkLoadSource EstimatedSource,
    bool IsGuaranteed)
{
    public string Summary =>
        $"{Key} score {TotalScore:0.0} dist {Distance:0.0} align {ForwardAlignment:0.00} vis {Visibility:0.00} retain {HysteresisBonus:0.0} adj {AdjacencyBonus:0.0} shoulder {ShoulderBonus:0.0} cost {LoadCostBonus:0.0} y {VerticalBias:0.0} src {EstimatedSource}";
}

internal sealed record ChunkReleaseInfo(
    Vector3I Key,
    float RetainScore,
    string Reason,
    TerrainChunkLoadSource LastSource)
{
    public string Summary => $"{Key} retain {RetainScore:0.0} src {LastSource} {Reason}";
}

internal readonly record struct ChunkAcquisitionResult(VoxelChunkData Data, TerrainChunkLoadSource Source);

internal readonly record struct SearchEvaluationContext(
    Vector2I CenterChunk,
    Vector2 StreamForward,
    Vector3 TrackedPosition,
    Vector3 CameraPosition,
    int SearchRadius);
