namespace TowerOfBaby.Entities.Controller.Player;

public sealed class PlayerAttackDefinition
{
    public float SlashRange { get; init; } = 4.6f;
    public float SlashLength { get; init; } = 3.4f;
    public float SlashWidth { get; init; } = 0.62f;
    public float SlashDepth { get; init; } = 0.58f;
    public float ScorchStrength { get; init; } = 1.0f;
    public float AttackCooldown { get; init; } = 0.58f;
    public float AttackPower { get; init; } = 1.0f;
    public float WindupDuration { get; init; } = 0.13f;
    public float ReleaseDuration { get; init; } = 0.11f;
    public float FollowThroughDuration { get; init; } = 0.15f;
    public float RecoveryDuration { get; init; } = 0.19f;

    public float TotalSwingDuration => WindupDuration + ReleaseDuration + FollowThroughDuration + RecoveryDuration;
}
