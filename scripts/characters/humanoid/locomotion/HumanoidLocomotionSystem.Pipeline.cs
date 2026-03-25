using Godot;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    private HumanoidGroundMotionFrame BuildGroundMotionFrame(float delta, Vector3 desiredDirection, float sprintBlend, float maxSpeed)
    {
        Vector3 visualOrigin = _rig.VisualRoot.GlobalPosition;
        Basis visualBasis = _rig.VisualRoot.GlobalBasis.Orthonormalized();
        Vector3 velocityPlanar = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        float speed = velocityPlanar.Length();
        float speedRatio = Mathf.Clamp(speed / Mathf.Max(maxSpeed, 0.01f), 0.0f, 1.0f);

        Vector3 bodyForward = ResolveBodyForward(desiredDirection, velocityPlanar);
        Vector3 bodyRight = bodyForward.Cross(Vector3.Up).Normalized();
        if (bodyRight.LengthSquared() < 0.0001f)
        {
            bodyRight = Vector3.Right;
        }

        float runBlend = Mathf.Clamp(
            Mathf.Max(
                sprintBlend * 0.85f,
                Mathf.InverseLerp(_settings.MoveSpeed * HumanoidLocomotionModel.RunStartSpeedRatio, maxSpeed, speed)),
            0.0f,
            1.0f);

        _locomotionBlend = Mathf.Lerp(
            _locomotionBlend,
            speedRatio,
            DampFactor(HumanoidLocomotionModel.LocomotionBlendSharpness, delta));

        float stepDurationSeconds = Mathf.Lerp(
            HumanoidLocomotionModel.WalkStepDurationSeconds,
            HumanoidLocomotionModel.RunStepDurationSeconds,
            runBlend);
        float stepLength = Mathf.Clamp(
            speed * stepDurationSeconds * 0.75f,
            _spec.LegLength * HumanoidLocomotionModel.WalkStepLengthRatio,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.WalkStepLengthRatio, HumanoidLocomotionModel.RunStepLengthRatio, runBlend));
        float stepHeight = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.WalkStepHeightRatio,
            HumanoidLocomotionModel.RunStepHeightRatio,
            runBlend);

        float desiredForwardInfluence = desiredDirection.LengthSquared() > 0.0001f
            ? desiredDirection.Dot(bodyForward)
            : 0.0f;

        HumanoidGroundMotionFrame frame = new()
        {
            VisualOrigin = visualOrigin,
            VisualBasis = visualBasis,
            VelocityPlanar = velocityPlanar,
            Forward = bodyForward,
            Right = bodyRight,
            Speed = speed,
            SpeedRatio = speedRatio,
            RunBlend = runBlend,
            StepDurationSeconds = stepDurationSeconds,
            StepLength = stepLength,
            StepHeight = stepHeight,
            DesiredForwardInfluence = desiredForwardInfluence
        };

        _profiler.SetMetric("speed_ratio", speedRatio);
        _profiler.SetMetric("run_blend", runBlend);
        _profiler.SetMetric("step_duration", stepDurationSeconds);
        _profiler.SetMetric("step_length", stepLength);
        return frame;
    }

    private void UpdateGroundedLegs(HumanoidGroundMotionFrame frame, float delta)
    {
        if (!_leftLeg.Initialized)
        {
            InitializeLegContact(_leftLeg, frame);
        }

        if (!_rightLeg.Initialized)
        {
            InitializeLegContact(_rightLeg, frame);
        }

        bool leftStepping = !_leftLeg.IsInStance;
        bool rightStepping = !_rightLeg.IsInStance;
        float gaitSpeedThreshold = _settings.MoveSpeed * HumanoidLocomotionModel.IdleToWalkSpeedRatio;
        bool allowNewSteps = frame.Speed >= gaitSpeedThreshold;

        if (!allowNewSteps && !leftStepping && !rightStepping)
        {
            UpdateIdleGroundedLeg(_leftLeg, frame, delta);
            UpdateIdleGroundedLeg(_rightLeg, frame, delta);
            _profiler.SetMetric("step_left_active", 0.0f);
            _profiler.SetMetric("step_right_active", 0.0f);
            return;
        }

        if (allowNewSteps && !leftStepping && !rightStepping)
        {
            TryBeginStep(frame);
        }

        if (_leftLeg.IsInStance)
        {
            UpdateStanceLeg(_leftLeg, frame, delta);
        }
        else
        {
            UpdateSwingLeg(_leftLeg, frame, delta);
        }

        if (_rightLeg.IsInStance)
        {
            UpdateStanceLeg(_rightLeg, frame, delta);
        }
        else
        {
            UpdateSwingLeg(_rightLeg, frame, delta);
        }

        _profiler.SetMetric("step_left_active", _leftLeg.IsInStance ? 0.0f : 1.0f);
        _profiler.SetMetric("step_right_active", _rightLeg.IsInStance ? 0.0f : 1.0f);
    }

    private void UpdateIdleGroundedLeg(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        Vector3 nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        Vector3 groundedSupport = SampleSupportPoint(nominalSupport, leg.Contact.GroundClearance, out Vector3 supportNormal);
        leg.PlantedSupportWorld = leg.PlantedSupportWorld.Lerp(groundedSupport, DampFactor(HumanoidLocomotionModel.IdleFootSharpness, delta));
        leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(leg.PlantedSupportWorld, DampFactor(HumanoidLocomotionModel.IdleFootSharpness, delta));
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(supportNormal, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
        leg.TargetGroundNormalWorld = leg.GroundNormalWorld;
        leg.IsInStance = true;
        leg.WasInStance = true;
        leg.StanceProgress = 0.0f;
        leg.SwingProgress = 1.0f;
        PublishLegMetrics(leg);
    }

    private void TryBeginStep(HumanoidGroundMotionFrame frame)
    {
        HumanoidLegMotionRuntime preferredLeg = _stepLeftNext ? _leftLeg : _rightLeg;
        HumanoidLegMotionRuntime supportLeg = _stepLeftNext ? _rightLeg : _leftLeg;

        if (ShouldStartStep(preferredLeg, frame))
        {
            BeginStep(preferredLeg, frame);
            return;
        }

        if (ShouldStartStep(supportLeg, frame))
        {
            BeginStep(supportLeg, frame);
        }
    }

    private bool ShouldStartStep(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        if (!leg.IsInStance)
        {
            return false;
        }

        Vector3 nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        Vector3 supportError = leg.PlantedSupportWorld - nominalSupport;
        float trailingDistance = -supportError.Dot(frame.Forward);
        float lateralError = Mathf.Abs(supportError.Dot(frame.Right));
        float triggerDistance = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.StepTriggerDistanceRatio,
            HumanoidLocomotionModel.StepTriggerDistanceRatio * 1.3f,
            frame.RunBlend);
        float triggerLateral = _spec.LegLength * HumanoidLocomotionModel.StepTriggerLateralRatio;

        return trailingDistance > triggerDistance || lateralError > triggerLateral;
    }

    private void BeginStep(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        leg.IsInStance = false;
        leg.WasInStance = false;
        leg.StanceProgress = 1.0f;
        leg.SwingProgress = 0.0f;
        leg.SwingStartWorld = leg.CurrentSupportWorld;
        leg.SwingTargetWorld = PlanStepTargetWorld(leg, frame, out Vector3 groundNormal);
        leg.TargetGroundNormalWorld = groundNormal;
    }

    private void UpdateStanceLeg(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        Vector3 plantedProbe = new(leg.PlantedSupportWorld.X, frame.VisualOrigin.Y, leg.PlantedSupportWorld.Z);
        Vector3 plantedSupport = SampleSupportPoint(plantedProbe, leg.Contact.GroundClearance, out Vector3 groundNormal);
        leg.PlantedSupportWorld = ClampSupportTargetToPlanarEnvelope(leg, frame, plantedSupport);
        leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(leg.PlantedSupportWorld, DampFactor(HumanoidLocomotionModel.StanceFootSharpness, delta));
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(groundNormal, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
        leg.TargetGroundNormalWorld = leg.GroundNormalWorld;

        Vector3 nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        float trailingDistance = Mathf.Max(0.0f, -(leg.PlantedSupportWorld - nominalSupport).Dot(frame.Forward));
        float triggerDistance = _spec.LegLength * HumanoidLocomotionModel.StepTriggerDistanceRatio;
        leg.StanceProgress = Mathf.Clamp(trailingDistance / Mathf.Max(triggerDistance, 0.001f), 0.0f, 1.0f);
        leg.SwingProgress = 1.0f;
        leg.IsInStance = true;
        leg.WasInStance = true;
        PublishLegMetrics(leg);
    }

    private void UpdateSwingLeg(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        leg.SwingProgress = Mathf.Clamp(leg.SwingProgress + (delta / Mathf.Max(frame.StepDurationSeconds, 0.01f)), 0.0f, 1.0f);

        if (leg.SwingProgress < HumanoidLocomotionModel.SwingRetargetWindow)
        {
            Vector3 retargetedSupport = PlanStepTargetWorld(leg, frame, out Vector3 retargetedNormal);
            float blend = leg.SwingProgress / Mathf.Max(HumanoidLocomotionModel.SwingRetargetWindow, 0.001f);
            leg.SwingTargetWorld = leg.SwingTargetWorld.Lerp(retargetedSupport, blend * 0.35f);
            leg.TargetGroundNormalWorld = leg.TargetGroundNormalWorld.Slerp(retargetedNormal, blend * 0.35f);
        }

        float swingT = Mathf.SmoothStep(0.0f, 1.0f, leg.SwingProgress);
        Vector3 support = leg.SwingStartWorld.Lerp(leg.SwingTargetWorld, swingT);
        support.Y += Mathf.Sin(swingT * Mathf.Pi) * frame.StepHeight;
        leg.CurrentSupportWorld = support;
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(leg.TargetGroundNormalWorld, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));

        if (leg.SwingProgress >= 1.0f)
        {
            CompleteStep(leg);
        }

        PublishLegMetrics(leg);
    }

    private void CompleteStep(HumanoidLegMotionRuntime leg)
    {
        leg.PlantedSupportWorld = leg.SwingTargetWorld;
        leg.CurrentSupportWorld = leg.SwingTargetWorld;
        leg.GroundNormalWorld = leg.TargetGroundNormalWorld;
        leg.IsInStance = true;
        leg.WasInStance = true;
        leg.StanceProgress = 0.0f;
        leg.SwingProgress = 1.0f;
        _stepLeftNext = leg == _leftLeg ? false : true;
    }

    private void InitializeLegContact(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        Vector3 nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        Vector3 groundedSupport = SampleSupportPoint(nominalSupport, leg.Contact.GroundClearance, out Vector3 supportNormal);
        leg.Initialized = true;
        leg.PlantedSupportWorld = groundedSupport;
        leg.CurrentSupportWorld = groundedSupport;
        leg.SwingStartWorld = groundedSupport;
        leg.SwingTargetWorld = groundedSupport;
        leg.GroundNormalWorld = supportNormal;
        leg.TargetGroundNormalWorld = supportNormal;
        leg.IsInStance = true;
        leg.WasInStance = true;
    }

    private Vector3 PlanStepTargetWorld(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, out Vector3 groundNormal)
    {
        Vector3 nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        float forwardPlacement = Mathf.Clamp(
            frame.StepLength * 0.55f,
            _spec.LegLength * 0.12f,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend));

        Vector3 targetProbe = nominalSupport + (frame.Forward * forwardPlacement);
        Vector3 touchdown = SampleSupportPoint(targetProbe, leg.Contact.GroundClearance, out groundNormal);
        return ClampSupportTargetToPlanarEnvelope(leg, frame, touchdown);
    }

    private static Vector3 ResolveRootRelativeSupportWorld(Vector3 supportPointLocal, HumanoidGroundMotionFrame frame)
    {
        return frame.VisualOrigin + (frame.VisualBasis * supportPointLocal);
    }

    private Vector3 ClampSupportTargetToPlanarEnvelope(
        HumanoidLegMotionRuntime leg,
        HumanoidGroundMotionFrame frame,
        Vector3 supportTarget)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (frame.VisualBasis * leg.HipOffsetFromPelvisLocal);
        Vector3 offset = supportTarget - hipWorld;
        float forward = Mathf.Clamp(
            offset.Dot(frame.Forward),
            -_spec.LegLength * HumanoidLocomotionModel.RearReachRatio,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend));
        float lateral = Mathf.Clamp(
            offset.Dot(frame.Right),
            -_spec.LegLength * HumanoidLocomotionModel.LateralReachRatio,
            _spec.LegLength * HumanoidLocomotionModel.LateralReachRatio);

        return hipWorld + (frame.Forward * forward) + (frame.Right * lateral) + (Vector3.Up * offset.Y);
    }
}
