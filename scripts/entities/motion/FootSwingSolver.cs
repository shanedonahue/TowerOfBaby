using Godot;

namespace TowerOfBaby.Entities.Motion;

public sealed class FootSwingSolver
{
    private readonly FootSwingSettings _settings;

    public FootSwingSolver(FootSwingSettings settings)
    {
        _settings = settings;
    }

    public float DurationSeconds => _settings.DurationSeconds;

    public (Vector3 Position, Vector3 Normal) Evaluate(
        Vector3 startPosition,
        Vector3 targetPosition,
        Vector3 startNormal,
        Vector3 targetNormal,
        float progress)
    {
        float t = Mathf.Clamp(progress, 0.0f, 1.0f);
        Vector3 blendedNormal = LocomotionMath.SafeNormalized(startNormal.Lerp(targetNormal, t), Vector3.Up);
        Vector3 planarPosition = startPosition.Lerp(targetPosition, t);
        float lift = _settings.LiftHeight + (startPosition.DistanceTo(targetPosition) * _settings.DistanceLiftScale);
        float arc = Mathf.Sin(t * Mathf.Pi) * lift;
        return (planarPosition + (blendedNormal * arc), blendedNormal);
    }
}
