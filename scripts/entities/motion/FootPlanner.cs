using Godot;
using TowerOfBaby.Entities.Body.Biped;

namespace TowerOfBaby.Entities.Motion;

public sealed class FootPlanner
{
    private readonly FootPlannerSettings _settings;
    private readonly FootSwingSolver _swingSolver;

    private readonly FootRuntime _leftFoot = new(FootSide.Left);
    private readonly FootRuntime _rightFoot = new(FootSide.Right);

    public FootPlanner(FootPlannerSettings settings, FootSwingSolver swingSolver)
    {
        _settings = settings;
        _swingSolver = swingSolver;
    }

    public void Initialize(
        BipedBodyDefinition bodyDefinition,
        BipedGrounding grounding,
        World3D world,
        Rid excludedRid,
        Vector3 rootPosition,
        Vector3 facingDirection,
        Vector3 upDirection)
    {
        InitializeFoot(_leftFoot, bodyDefinition.LeftLeg, grounding, world, excludedRid, rootPosition, facingDirection, upDirection);
        InitializeFoot(_rightFoot, bodyDefinition.RightLeg, grounding, world, excludedRid, rootPosition, facingDirection, upDirection);
    }

    public void Update(
        float delta,
        BipedBodyDefinition bodyDefinition,
        RootMotionFrame root,
        BipedGrounding grounding,
        World3D world,
        Rid excludedRid)
    {
        UpdateCurrentFoot(_leftFoot, delta);
        UpdateCurrentFoot(_rightFoot, delta);

        FootCandidate leftCandidate = BuildCandidate(_leftFoot, bodyDefinition.LeftLeg, root, grounding, world, excludedRid);
        FootCandidate rightCandidate = BuildCandidate(_rightFoot, bodyDefinition.RightLeg, root, grounding, world, excludedRid);

        ApplyCandidateTelemetry(_leftFoot, leftCandidate);
        ApplyCandidateTelemetry(_rightFoot, rightCandidate);

        if (_leftFoot.State == LocomotionFootState.Stepping)
        {
            AdvanceStep(_leftFoot, delta);
        }

        if (_rightFoot.State == LocomotionFootState.Stepping)
        {
            AdvanceStep(_rightFoot, delta);
        }

        bool anyStepping = _leftFoot.State == LocomotionFootState.Stepping || _rightFoot.State == LocomotionFootState.Stepping;
        if (!anyStepping)
        {
            TryStartBestStep(leftCandidate, rightCandidate);
        }
    }

    public LocomotionFootPose GetPose(FootSide side)
    {
        FootRuntime foot = side == FootSide.Left ? _leftFoot : _rightFoot;
        return new LocomotionFootPose(
            foot.Side,
            foot.State,
            foot.CurrentPosition,
            foot.CurrentNormal,
            foot.HomePosition,
            foot.PlannedTarget,
            foot.State == LocomotionFootState.Stepping && foot.SwingDuration > 0.0f
                ? Mathf.Clamp(foot.SwingElapsed / foot.SwingDuration, 0.0f, 1.0f)
                : 0.0f);
    }

    public LocomotionTelemetrySnapshot BuildTelemetry(RootMotionFrame root)
    {
        return new LocomotionTelemetrySnapshot
        {
            RootPosition = root.Position,
            DesiredMovement = root.DesiredVelocity,
            ActualMovement = root.ActualVelocity,
            FacingDirection = root.FacingDirection,
            GroundNormal = root.GroundNormal,
            StanceWidth = _leftFoot.CurrentPosition.DistanceTo(_rightFoot.CurrentPosition),
            LeftFoot = BuildFootTelemetry(_leftFoot),
            RightFoot = BuildFootTelemetry(_rightFoot)
        };
    }

    private void InitializeFoot(
        FootRuntime foot,
        BipedLegDefinition definition,
        BipedGrounding grounding,
        World3D world,
        Rid excludedRid,
        Vector3 rootPosition,
        Vector3 facingDirection,
        Vector3 upDirection)
    {
        Vector3 homePosition = rootPosition + LocomotionMath.TransformBodyOffset(facingDirection, upDirection, definition.HomeOffset);
        GroundSample support = grounding.SampleGround(world, homePosition, excludedRid);
        foot.State = LocomotionFootState.Planted;
        foot.HomePosition = homePosition;
        foot.PlantPosition = support.HasGround ? support.Position : homePosition;
        foot.PlantNormal = support.HasGround ? support.Normal : upDirection;
        foot.PlannedTarget = foot.PlantPosition;
        foot.PlannedTargetNormal = foot.PlantNormal;
        foot.CurrentPosition = foot.PlantPosition;
        foot.CurrentNormal = foot.PlantNormal;
        foot.DecisionReason = "planted at home";
        foot.TimeSincePlant = 1.0f;
    }

    private FootCandidate BuildCandidate(
        FootRuntime foot,
        BipedLegDefinition definition,
        RootMotionFrame root,
        BipedGrounding grounding,
        World3D world,
        Rid excludedRid)
    {
        Vector3 supportUp = LocomotionMath.SafeNormalized(root.GroundNormal, Vector3.Up);
        Vector3 facing = LocomotionMath.SafeNormalized(root.FacingDirection, Vector3.Forward);
        Vector3 right = LocomotionMath.GetRight(facing, Vector3.Up);

        Vector3 homePosition = root.Position + LocomotionMath.TransformBodyOffset(facing, Vector3.Up, definition.HomeOffset);
        Vector3 blendedVelocity =
            (LocomotionMath.Flatten(root.ActualVelocity) * _settings.ActualVelocityWeight) +
            (LocomotionMath.Flatten(root.DesiredVelocity) * _settings.DesiredVelocityWeight);
        Vector3 projectedTarget = homePosition + (blendedVelocity * _settings.StepPredictionTime);
        GroundSample targetSample = grounding.SampleGround(world, projectedTarget, excludedRid);
        Vector3 targetPosition = targetSample.HasGround ? targetSample.Position : projectedTarget;
        Vector3 targetNormal = targetSample.HasGround ? targetSample.Normal : supportUp;

        GroundSample currentSupportSample = grounding.SampleGround(world, foot.PlantPosition, excludedRid);
        bool supportLost = !currentSupportSample.HasGround ||
            currentSupportSample.Position.DistanceTo(foot.PlantPosition) > _settings.SupportLossDistance;

        Vector3 supportDelta = foot.PlantPosition - homePosition;
        float forwardOffset = supportDelta.Dot(facing);
        float lateralOffset = supportDelta.Dot(right);
        float verticalOffset = foot.PlantPosition.Y - targetPosition.Y;

        float forwardViolation = forwardOffset > _settings.ForwardLimit
            ? (forwardOffset - _settings.ForwardLimit) / _settings.ForwardLimit
            : 0.0f;
        float backwardViolation = -forwardOffset > _settings.BackwardLimit
            ? ((-forwardOffset) - _settings.BackwardLimit) / _settings.BackwardLimit
            : 0.0f;
        float lateralViolation = Mathf.Abs(lateralOffset) > _settings.LateralLimit
            ? (Mathf.Abs(lateralOffset) - _settings.LateralLimit) / _settings.LateralLimit
            : 0.0f;
        float verticalViolation = Mathf.Abs(verticalOffset) > _settings.VerticalLimit
            ? (Mathf.Abs(verticalOffset) - _settings.VerticalLimit) / _settings.VerticalLimit
            : 0.0f;

        float violation = Mathf.Max(Mathf.Max(forwardViolation, backwardViolation), Mathf.Max(lateralViolation, verticalViolation));
        string reason = "support within thresholds";
        if (supportLost)
        {
            violation = Mathf.Max(violation, 2.0f);
            reason = "support lost";
        }
        else if (backwardViolation >= forwardViolation && backwardViolation >= lateralViolation && backwardViolation >= verticalViolation && backwardViolation > 0.0f)
        {
            reason = $"trailing support {-forwardOffset:0.00}m";
        }
        else if (forwardViolation >= lateralViolation && forwardViolation >= verticalViolation && forwardViolation > 0.0f)
        {
            reason = $"overreaching support {forwardOffset:0.00}m";
        }
        else if (lateralViolation >= verticalViolation && lateralViolation > 0.0f)
        {
            reason = $"lateral drift {Mathf.Abs(lateralOffset):0.00}m";
        }
        else if (verticalViolation > 0.0f)
        {
            reason = $"height mismatch {Mathf.Abs(verticalOffset):0.00}m";
        }

        float stepDistance = foot.PlantPosition.DistanceTo(targetPosition);
        bool canStep = foot.State == LocomotionFootState.Planted &&
            foot.TimeSincePlant >= _settings.MinimumPlantTime &&
            (supportLost || (violation >= 1.0f && stepDistance >= _settings.MinimumStepDistance));
        bool emergency = canStep && (supportLost || violation >= _settings.EmergencyViolationThreshold);

        return new FootCandidate(
            foot.Side,
            homePosition,
            targetPosition,
            targetNormal,
            forwardOffset,
            lateralOffset,
            verticalOffset,
            violation,
            stepDistance,
            canStep,
            emergency,
            reason);
    }

    private void ApplyCandidateTelemetry(FootRuntime foot, FootCandidate candidate)
    {
        foot.HomePosition = candidate.HomePosition;
        foot.PlannedTarget = candidate.TargetPosition;
        foot.PlannedTargetNormal = candidate.TargetNormal;
        foot.ForwardOffset = candidate.ForwardOffset;
        foot.LateralOffset = candidate.LateralOffset;
        foot.VerticalOffset = candidate.VerticalOffset;
        foot.ThresholdViolation = candidate.Violation;
        if (foot.State == LocomotionFootState.Planted)
        {
            foot.DecisionReason = candidate.Reason;
        }
    }

    private void UpdateCurrentFoot(FootRuntime foot, float delta)
    {
        if (foot.State == LocomotionFootState.Planted)
        {
            foot.TimeSincePlant += delta;
            foot.CurrentPosition = foot.PlantPosition;
            foot.CurrentNormal = foot.PlantNormal;
            foot.CurrentStepDuration = 0.0f;
            foot.FootSkateDistance = foot.CurrentPosition.DistanceTo(foot.PlantPosition);
            return;
        }

        foot.TimeSincePlant = 0.0f;
        foot.FootSkateDistance = 0.0f;
    }

    private void TryStartBestStep(FootCandidate leftCandidate, FootCandidate rightCandidate)
    {
        FootCandidate selected = default;
        if (leftCandidate.CanStep && rightCandidate.CanStep)
        {
            selected = leftCandidate.Violation >= rightCandidate.Violation
                ? leftCandidate
                : rightCandidate;
        }
        else if (leftCandidate.CanStep)
        {
            selected = leftCandidate;
        }
        else if (rightCandidate.CanStep)
        {
            selected = rightCandidate;
        }
        else
        {
            return;
        }

        FootRuntime foot = selected.Side == FootSide.Left ? _leftFoot : _rightFoot;
        StartStep(foot, selected);
    }

    private void StartStep(FootRuntime foot, FootCandidate candidate)
    {
        foot.State = LocomotionFootState.Stepping;
        foot.SwingStart = foot.PlantPosition;
        foot.SwingStartNormal = foot.PlantNormal;
        foot.SwingTarget = candidate.TargetPosition;
        foot.SwingTargetNormal = candidate.TargetNormal;
        foot.SwingElapsed = 0.0f;
        foot.SwingDuration = Mathf.Max(0.05f, _swingSolver.DurationSeconds);
        foot.CurrentStepDuration = 0.0f;
        foot.DecisionReason = $"step: {candidate.Reason}";
    }

    private void AdvanceStep(FootRuntime foot, float delta)
    {
        foot.SwingElapsed += delta;
        foot.CurrentStepDuration = foot.SwingElapsed;
        float progress = Mathf.Clamp(foot.SwingElapsed / foot.SwingDuration, 0.0f, 1.0f);
        (foot.CurrentPosition, foot.CurrentNormal) = _swingSolver.Evaluate(
            foot.SwingStart,
            foot.SwingTarget,
            foot.SwingStartNormal,
            foot.SwingTargetNormal,
            progress);

        if (progress < 1.0f)
        {
            return;
        }

        foot.State = LocomotionFootState.Planted;
        foot.PlantPosition = foot.SwingTarget;
        foot.PlantNormal = foot.SwingTargetNormal;
        foot.CurrentPosition = foot.PlantPosition;
        foot.CurrentNormal = foot.PlantNormal;
        foot.LastStepDuration = foot.SwingElapsed;
        foot.CurrentStepDuration = 0.0f;
        foot.SwingElapsed = 0.0f;
        foot.TimeSincePlant = 0.0f;
        foot.StepCount++;
        foot.DecisionReason = $"landed after {foot.LastStepDuration:0.00}s";
    }

    private static LocomotionFootTelemetry BuildFootTelemetry(FootRuntime foot)
    {
        return new LocomotionFootTelemetry
        {
            Side = foot.Side,
            State = foot.State,
            SupportPosition = foot.State == LocomotionFootState.Planted ? foot.PlantPosition : foot.CurrentPosition,
            HomePosition = foot.HomePosition,
            NextTargetPosition = foot.PlannedTarget,
            TerrainNormal = foot.PlannedTargetNormal,
            ThresholdViolation = foot.ThresholdViolation,
            ForwardOffset = foot.ForwardOffset,
            LateralOffset = foot.LateralOffset,
            VerticalOffset = foot.VerticalOffset,
            FootSkateDistance = foot.FootSkateDistance,
            CurrentStepDuration = foot.CurrentStepDuration,
            LastStepDuration = foot.LastStepDuration,
            SwingProgress = foot.State == LocomotionFootState.Stepping && foot.SwingDuration > 0.0f
                ? Mathf.Clamp(foot.SwingElapsed / foot.SwingDuration, 0.0f, 1.0f)
                : 0.0f,
            StepCount = foot.StepCount,
            DecisionReason = foot.DecisionReason
        };
    }

    private sealed class FootRuntime
    {
        public FootRuntime(FootSide side)
        {
            Side = side;
        }

        public FootSide Side { get; }
        public LocomotionFootState State { get; set; }
        public Vector3 PlantPosition { get; set; }
        public Vector3 PlantNormal { get; set; } = Vector3.Up;
        public Vector3 CurrentPosition { get; set; }
        public Vector3 CurrentNormal { get; set; } = Vector3.Up;
        public Vector3 HomePosition { get; set; }
        public Vector3 PlannedTarget { get; set; }
        public Vector3 PlannedTargetNormal { get; set; } = Vector3.Up;
        public Vector3 SwingStart { get; set; }
        public Vector3 SwingTarget { get; set; }
        public Vector3 SwingStartNormal { get; set; } = Vector3.Up;
        public Vector3 SwingTargetNormal { get; set; } = Vector3.Up;
        public float SwingElapsed { get; set; }
        public float SwingDuration { get; set; }
        public float CurrentStepDuration { get; set; }
        public float LastStepDuration { get; set; }
        public float TimeSincePlant { get; set; }
        public float ThresholdViolation { get; set; }
        public float ForwardOffset { get; set; }
        public float LateralOffset { get; set; }
        public float VerticalOffset { get; set; }
        public float FootSkateDistance { get; set; }
        public int StepCount { get; set; }
        public string DecisionReason { get; set; } = "idle";
    }

    private readonly record struct FootCandidate(
        FootSide Side,
        Vector3 HomePosition,
        Vector3 TargetPosition,
        Vector3 TargetNormal,
        float ForwardOffset,
        float LateralOffset,
        float VerticalOffset,
        float Violation,
        float StepDistance,
        bool CanStep,
        bool Emergency,
        string Reason);
}
