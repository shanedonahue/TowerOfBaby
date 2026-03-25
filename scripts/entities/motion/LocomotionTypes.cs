using Godot;

namespace TowerOfBaby.Entities.Motion;

public enum FootSide
{
    Left = -1,
    Right = 1
}

public enum LocomotionFootState
{
    Planted,
    Stepping
}

public readonly record struct MovementIntent(Vector3 MoveDirection, float MoveAmount, Vector3 FacingDirection)
{
    public bool HasMovement => MoveAmount > 0.01f && MoveDirection.LengthSquared() > 0.0001f;
}

public sealed class RootMotionSettings
{
    public float MaxGroundSpeed { get; init; } = 4.4f;
    public float Acceleration { get; init; } = 17.0f;
    public float Deceleration { get; init; } = 22.0f;
    public float AirAcceleration { get; init; } = 6.0f;
    public float Gravity { get; init; } = 32.0f;
    public float TurnSpeedRadians { get; init; } = 12.0f;
    public float FloorSnapVelocity { get; init; } = 0.05f;
}

public sealed class FootPlannerSettings
{
    public float ForwardLimit { get; init; } = 0.32f;
    public float BackwardLimit { get; init; } = 0.24f;
    public float LateralLimit { get; init; } = 0.18f;
    public float VerticalLimit { get; init; } = 0.24f;
    public float StepPredictionTime { get; init; } = 0.2f;
    public float DesiredVelocityWeight { get; init; } = 0.45f;
    public float ActualVelocityWeight { get; init; } = 0.55f;
    public float MinimumStepDistance { get; init; } = 0.16f;
    public float SupportLossDistance { get; init; } = 0.16f;
    public float EmergencyViolationThreshold { get; init; } = 1.6f;
    public float MinimumPlantTime { get; init; } = 0.05f;
}

public sealed class FootSwingSettings
{
    public float DurationSeconds { get; init; } = 0.22f;
    public float LiftHeight { get; init; } = 0.16f;
    public float DistanceLiftScale { get; init; } = 0.08f;
}

public readonly record struct GroundSample(
    bool HasGround,
    Vector3 Position,
    Vector3 Normal,
    Vector3 ProbePoint,
    float Distance)
{
    public static GroundSample NoHit(Vector3 probePoint)
    {
        return new GroundSample(false, probePoint, Vector3.Up, probePoint, float.PositiveInfinity);
    }
}

public readonly record struct RootMotionFrame(
    Vector3 Position,
    Vector3 DesiredVelocity,
    Vector3 ActualVelocity,
    Vector3 FacingDirection,
    Vector3 GroundNormal,
    bool IsGrounded);

public readonly record struct LocomotionFootPose(
    FootSide Side,
    LocomotionFootState State,
    Vector3 Position,
    Vector3 Normal,
    Vector3 HomePosition,
    Vector3 TargetPosition,
    float SwingProgress);

public readonly record struct LocomotionFrame(
    RootMotionFrame Root,
    LocomotionFootPose LeftFoot,
    LocomotionFootPose RightFoot,
    LocomotionTelemetrySnapshot Telemetry);

public sealed class LocomotionFootTelemetry
{
    public FootSide Side { get; init; }
    public LocomotionFootState State { get; init; }
    public Vector3 SupportPosition { get; init; }
    public Vector3 HomePosition { get; init; }
    public Vector3 NextTargetPosition { get; init; }
    public Vector3 TerrainNormal { get; init; } = Vector3.Up;
    public float ThresholdViolation { get; init; }
    public float ForwardOffset { get; init; }
    public float LateralOffset { get; init; }
    public float VerticalOffset { get; init; }
    public float FootSkateDistance { get; init; }
    public float CurrentStepDuration { get; init; }
    public float LastStepDuration { get; init; }
    public float SwingProgress { get; init; }
    public int StepCount { get; init; }
    public string DecisionReason { get; init; } = "idle";
}

public sealed class LocomotionTelemetrySnapshot
{
    public Vector3 RootPosition { get; init; }
    public Vector3 DesiredMovement { get; init; }
    public Vector3 ActualMovement { get; init; }
    public Vector3 FacingDirection { get; init; } = Vector3.Forward;
    public Vector3 GroundNormal { get; init; } = Vector3.Up;
    public float StanceWidth { get; init; }
    public LocomotionFootTelemetry LeftFoot { get; init; } = new();
    public LocomotionFootTelemetry RightFoot { get; init; } = new();
}

public interface ILocomotionTelemetrySource
{
    LocomotionTelemetrySnapshot GetLocomotionTelemetrySnapshot();
}

public static class LocomotionMath
{
    public static int Sign(this FootSide side)
    {
        return side == FootSide.Left ? -1 : 1;
    }

    public static Vector3 Flatten(Vector3 value)
    {
        return new Vector3(value.X, 0.0f, value.Z);
    }

    public static Vector3 SafeNormalized(Vector3 value, Vector3 fallback)
    {
        return value.LengthSquared() > 0.0001f
            ? value.Normalized()
            : fallback;
    }

    public static Vector3 ProjectOntoPlane(Vector3 value, Vector3 planeNormal)
    {
        Vector3 normal = SafeNormalized(planeNormal, Vector3.Up);
        return value - (normal * value.Dot(normal));
    }

    public static Vector3 RotatePlanarTowards(Vector3 currentForward, Vector3 targetForward, float maxRadiansDelta)
    {
        Vector3 current = SafeNormalized(Flatten(currentForward), Vector3.Forward);
        Vector3 target = SafeNormalized(Flatten(targetForward), current);
        float angle = Mathf.Acos(Mathf.Clamp(current.Dot(target), -1.0f, 1.0f));
        if (angle <= 0.0001f)
        {
            return target;
        }

        if (angle <= maxRadiansDelta)
        {
            return target;
        }

        float weight = maxRadiansDelta / angle;
        return current.Slerp(target, weight).Normalized();
    }

    public static Vector3 GetRight(Vector3 forward, Vector3 up)
    {
        Vector3 upAxis = SafeNormalized(up, Vector3.Up);
        Vector3 forwardAxis = SafeNormalized(ProjectOntoPlane(forward, upAxis), Vector3.Forward);
        return upAxis.Cross(-forwardAxis).Normalized();
    }

    public static Basis CreateBasisFromForward(Vector3 forward, Vector3 up)
    {
        Vector3 upAxis = SafeNormalized(up, Vector3.Up);
        Vector3 forwardAxis = SafeNormalized(ProjectOntoPlane(forward, upAxis), Vector3.Forward);
        Vector3 rightAxis = GetRight(forwardAxis, upAxis);
        Vector3 backAxis = rightAxis.Cross(upAxis).Normalized();
        return new Basis(rightAxis, upAxis, backAxis);
    }

    public static Vector3 TransformBodyOffset(Vector3 forward, Vector3 up, Vector3 localOffset)
    {
        Vector3 upAxis = SafeNormalized(up, Vector3.Up);
        Vector3 forwardAxis = SafeNormalized(ProjectOntoPlane(forward, upAxis), Vector3.Forward);
        Vector3 rightAxis = GetRight(forwardAxis, upAxis);
        return (rightAxis * localOffset.X) + (upAxis * localOffset.Y) + (forwardAxis * localOffset.Z);
    }
}
