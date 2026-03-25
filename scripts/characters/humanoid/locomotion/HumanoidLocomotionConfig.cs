namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed class HumanoidLocomotionConfig
{
    public float MoveSpeed { get; init; }
    public float SprintSpeedMultiplier { get; init; }
    public float Acceleration { get; init; }
    public float Deceleration { get; init; }
    public float AirAcceleration { get; init; }
    public float TurnResponsiveness { get; init; }
    public float RotationSpeed { get; init; }
    public float GravityScale { get; init; }
    public float FallGravityMultiplier { get; init; }
    public float GroundStickVelocity { get; init; }
    public float FootProbeDistance { get; init; }
    public bool EnableMotionDiagnostics { get; init; }
    public float MotionDiagnosticLogIntervalSeconds { get; init; }
}
