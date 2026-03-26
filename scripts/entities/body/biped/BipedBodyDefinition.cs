using System.Collections.Generic;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedBodyDefinition
{
    public BipedLegDefinition LeftLeg { get; init; } = new();
    public BipedLegDefinition RightLeg { get; init; } = new();
    public BipedArmDefinition LeftArm { get; init; } = new();
    public BipedArmDefinition RightArm { get; init; } = new();
    public float PelvisHeight { get; init; } = 0.98f;
    public float PelvisWidth { get; init; } = 0.32f;
    public float PelvisDepth { get; init; } = 0.2f;
    public float PelvisThickness { get; init; } = 0.12f;
    public float PelvisFollowSpeed { get; init; } = 10.0f;
    public float PelvisSupportBias { get; init; } = 0.2f;
    public float MaxPelvisDrop { get; init; } = 0.12f;
    public float MaxPelvisLift { get; init; } = 0.08f;
    public float TorsoHeight { get; init; } = 0.72f;
    public float TorsoWidth { get; init; } = 0.22f;
    public float TorsoDepth { get; init; } = 0.16f;
    public float TorsoLean { get; init; } = 0.08f;
    public float TorsoFollowSpeed { get; init; } = 8.0f;

    public IEnumerable<BipedLegDefinition> Legs
    {
        get
        {
            yield return LeftLeg;
            yield return RightLeg;
        }
    }

    public IEnumerable<BipedArmDefinition> Arms
    {
        get
        {
            yield return LeftArm;
            yield return RightArm;
        }
    }
}
