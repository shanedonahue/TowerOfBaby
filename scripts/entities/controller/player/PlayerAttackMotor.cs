using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Controller.Player;

public sealed class PlayerAttackMotor
{
    private readonly PlayerAttackDefinition _definition;

    private AttackPhase _phase = AttackPhase.Idle;
    private float _phaseElapsed;
    private float _totalElapsed;
    private float _cooldownRemaining;

    public PlayerAttackMotor(PlayerAttackDefinition definition)
    {
        _definition = definition;
    }

    public AttackPresentationState PresentationState => BuildPresentationState();
    public bool IsActive => _phase != AttackPhase.Idle;
    public float CooldownRemaining => _cooldownRemaining;

    public bool TryStartAttack()
    {
        if (IsActive || _cooldownRemaining > 0.0f)
        {
            return false;
        }

        _phase = AttackPhase.Windup;
        _phaseElapsed = 0.0f;
        _totalElapsed = 0.0f;
        _cooldownRemaining = _definition.AttackCooldown;
        return true;
    }

    public AttackStepResult Step(float delta)
    {
        _cooldownRemaining = Mathf.Max(0.0f, _cooldownRemaining - delta);
        if (!IsActive)
        {
            return new AttackStepResult(BuildPresentationState(), EmitSlash: false, PhaseChanged: false);
        }

        _phaseElapsed += delta;
        _totalElapsed += delta;

        bool emitSlash = false;
        bool phaseChanged = false;

        while (IsActive)
        {
            float duration = GetPhaseDuration(_phase);
            if (duration <= 0.0f)
            {
                phaseChanged = true;
                _phase = AdvancePhase(_phase, ref emitSlash);
                if (_phase == AttackPhase.Idle)
                {
                    _phaseElapsed = 0.0f;
                    _totalElapsed = 0.0f;
                    break;
                }

                continue;
            }

            if (_phaseElapsed < duration)
            {
                break;
            }

            _phaseElapsed -= duration;
            phaseChanged = true;
            _phase = AdvancePhase(_phase, ref emitSlash);

            if (_phase == AttackPhase.Idle)
            {
                _phaseElapsed = 0.0f;
                _totalElapsed = 0.0f;
                break;
            }
        }

        return new AttackStepResult(BuildPresentationState(), emitSlash, phaseChanged);
    }

    private AttackPresentationState BuildPresentationState()
    {
        if (!IsActive)
        {
            return AttackPresentationState.Idle(_cooldownRemaining);
        }

        float duration = GetPhaseDuration(_phase);
        float phaseProgress = duration > 0.0f
            ? Mathf.Clamp(_phaseElapsed / duration, 0.0f, 1.0f)
            : 1.0f;
        float totalProgress = _definition.TotalSwingDuration > 0.0f
            ? Mathf.Clamp(_totalElapsed / _definition.TotalSwingDuration, 0.0f, 1.0f)
            : 1.0f;
        return new AttackPresentationState(
            Active: true,
            Phase: _phase,
            PhaseProgress: phaseProgress,
            TotalProgress: totalProgress,
            CooldownRemaining: _cooldownRemaining);
    }

    private float GetPhaseDuration(AttackPhase phase)
    {
        return phase switch
        {
            AttackPhase.Windup => _definition.WindupDuration,
            AttackPhase.Release => _definition.ReleaseDuration,
            AttackPhase.FollowThrough => _definition.FollowThroughDuration,
            AttackPhase.Recovery => _definition.RecoveryDuration,
            _ => 0.0f
        };
    }

    private static AttackPhase AdvancePhase(AttackPhase phase, ref bool emitSlash)
    {
        AttackPhase nextPhase = phase switch
        {
            AttackPhase.Windup => AttackPhase.Release,
            AttackPhase.Release => AttackPhase.FollowThrough,
            AttackPhase.FollowThrough => AttackPhase.Recovery,
            AttackPhase.Recovery => AttackPhase.Idle,
            _ => AttackPhase.Idle
        };

        if (nextPhase == AttackPhase.Release)
        {
            emitSlash = true;
        }

        return nextPhase;
    }
}

public readonly record struct AttackStepResult(
    AttackPresentationState PresentationState,
    bool EmitSlash,
    bool PhaseChanged);
