public sealed class HumanoidBodySpec
{
    public string SpeciesId { get; init; } = "humanoid";
    public int Seed { get; init; }
    public float Height { get; init; }
    public float ShoulderWidth { get; init; }
    public float HipWidth { get; init; }
    public float TorsoHeight { get; init; }
    public float ChestDepth { get; init; }
    public float NeckLength { get; init; }
    public float HeadRadius { get; init; }
    public float UpperArmLength { get; init; }
    public float LowerArmLength { get; init; }
    public float UpperLegLength { get; init; }
    public float LowerLegLength { get; init; }
    public float ArmRadius { get; init; }
    public float LegRadius { get; init; }
    public float FootLength { get; init; }
    public float FootWidth { get; init; }
    public float FootHeight { get; init; }
    public float VisualRootHeight { get; init; }
    public float CollisionRadius { get; init; }
    public float CollisionHeight { get; init; }
    public float EyeHeight { get; init; }

    public float LegLength => UpperLegLength + LowerLegLength;
    public float HipHeight => LegLength * 0.53f;
    public float ShoulderHeight => HipHeight + TorsoHeight;
}
