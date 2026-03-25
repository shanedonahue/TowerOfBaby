using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedLegDefinition
{
    public FootSide Side { get; init; }
    public Vector3 HipOffset { get; init; }
    public Vector3 HomeOffset { get; init; }
    public float UpperLegLength { get; init; } = 0.62f;
    public float LowerLegLength { get; init; } = 0.62f;
    public float UpperLegRadius { get; init; } = 0.08f;
    public float LowerLegRadius { get; init; } = 0.065f;
    public float FootLength { get; init; } = 0.28f;
    public float FootWidth { get; init; } = 0.12f;
    public float FootHeight { get; init; } = 0.08f;
    public float KneeForwardBias { get; init; } = 0.85f;
    public float KneeOutwardBias { get; init; } = 0.25f;
}
