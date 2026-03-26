using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedArmDefinition
{
    public FootSide Side { get; init; }
    public Vector3 ShoulderOffset { get; init; }
    public Vector3 RelaxedHandOffset { get; init; }
    public float UpperArmLength { get; init; } = 0.44f;
    public float LowerArmLength { get; init; } = 0.42f;
    public float UpperArmRadius { get; init; } = 0.065f;
    public float LowerArmRadius { get; init; } = 0.055f;
    public float HandLength { get; init; } = 0.16f;
    public float HandWidth { get; init; } = 0.07f;
    public float HandThickness { get; init; } = 0.07f;
    public float ElbowForwardBias { get; init; } = 0.28f;
    public float ElbowOutwardBias { get; init; } = 0.72f;
    public float ElbowDownBias { get; init; } = 0.36f;
}
