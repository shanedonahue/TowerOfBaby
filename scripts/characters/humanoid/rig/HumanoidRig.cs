using Godot;

namespace TowerOfBaby.Characters.Humanoid.Rig;

public sealed class HumanoidRig
{
    public Node3D VisualRoot { get; init; } = null!;
    public Node3D Hips { get; init; } = null!;
    public Node3D UpperBody { get; init; } = null!;
    public Node3D Torso { get; init; } = null!;
    public Node3D ChestBand { get; init; } = null!;
    public Node3D Head { get; init; } = null!;
    public Node3D LeftArm { get; init; } = null!;
    public Node3D RightArm { get; init; } = null!;
    public HumanoidLegRig LeftLeg { get; init; } = null!;
    public HumanoidLegRig RightLeg { get; init; } = null!;
}

public sealed class HumanoidLegRig
{
    public Node3D Upper { get; init; } = null!;
    public Node3D Lower { get; init; } = null!;
    public Node3D Foot { get; init; } = null!;
    public float UpperLength { get; init; }
    public float LowerLength { get; init; }
    public bool IsStepping { get; set; }
    public float StepProgress { get; set; } = 1.0f;
    public Vector3 TargetFootPosition { get; set; } = Vector3.Zero;
    public Vector3 PlantedFootPosition { get; set; } = Vector3.Zero;
    public Vector3 CurrentFootPosition { get; set; } = Vector3.Zero;
    public Vector3 GroundNormal { get; set; } = Vector3.Up;
    public Vector3 TargetNormal { get; set; } = Vector3.Up;
}
