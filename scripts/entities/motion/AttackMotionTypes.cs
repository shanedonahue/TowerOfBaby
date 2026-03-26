using Godot;

namespace TowerOfBaby.Entities.Motion;

public enum AttackPhase
{
    Idle,
    Windup,
    Release,
    FollowThrough,
    Recovery
}

public readonly record struct AttackPresentationState(
    bool Active,
    AttackPhase Phase,
    float PhaseProgress,
    float TotalProgress,
    float CooldownRemaining)
{
    public static AttackPresentationState Idle(float cooldownRemaining = 0.0f)
    {
        return new AttackPresentationState(
            Active: false,
            AttackPhase.Idle,
            PhaseProgress: 0.0f,
            TotalProgress: 0.0f,
            CooldownRemaining: cooldownRemaining);
    }

    public float UpperBodyBlend
    {
        get
        {
            if (!Active)
            {
                return 0.0f;
            }

            return Phase switch
            {
                AttackPhase.Windup => Mathf.SmoothStep(0.28f, 1.0f, PhaseProgress),
                AttackPhase.Recovery => Mathf.Lerp(1.0f, 0.0f, Mathf.SmoothStep(0.0f, 1.0f, PhaseProgress)),
                _ => 1.0f
            };
        }
    }
}
