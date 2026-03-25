using Godot;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    // Grounded stages share a single frame description so every downstream solve stays in the same coordinate system.
    private HumanoidGroundMotionFrame BuildGroundMotionFrame(float delta, Vector3 desiredDirection, Vector3 targetVelocity, float sprintBlend, float maxSpeed)
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

        float gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity") * _settings.GravityScale;
        float dimensionlessSpeed = speed / Mathf.Sqrt(Mathf.Max(gravity * _spec.LegLength, 0.01f));
        float runBlend = Mathf.Clamp(
            Mathf.Max(
                sprintBlend * 0.8f,
                Mathf.InverseLerp(HumanoidLocomotionModel.WalkRunTransitionDimensionlessSpeed, HumanoidLocomotionModel.FullRunDimensionlessSpeed, dimensionlessSpeed)),
            0.0f,
            1.0f);

        _locomotionBlend = Mathf.Lerp(
            _locomotionBlend,
            Mathf.Clamp(speed / Mathf.Max(_settings.MoveSpeed * HumanoidLocomotionModel.LocomotionBlendSpeedRatio, 0.01f), 0.0f, 1.0f),
            DampFactor(HumanoidLocomotionModel.LocomotionBlendSharpness, delta));

        float naturalFrequencyHz = Mathf.Sqrt(Mathf.Max(gravity, 0.01f) / Mathf.Max(_spec.LegLength, 0.01f)) / Mathf.Tau;
        float cycleFrequencyHz = _locomotionBlend * Mathf.Lerp(
            naturalFrequencyHz * HumanoidLocomotionModel.WalkFrequencyGain,
            naturalFrequencyHz * HumanoidLocomotionModel.RunFrequencyGain,
            runBlend);
        if (_locomotionBlend < 0.02f)
        {
            cycleFrequencyHz = 0.0f;
        }

        _gaitPhase = Mathf.PosMod(_gaitPhase + (cycleFrequencyHz * delta), 1.0f);

        Vector3 planarAcceleration = delta > 0.0f
            ? (velocityPlanar - _lastPlanarVelocity) / delta
            : Vector3.Zero;
        _lastPlanarVelocity = velocityPlanar;

        float forwardAcceleration = Mathf.Clamp(
            planarAcceleration.Dot(bodyForward) / Mathf.Max(maxSpeed * 6.0f, 0.01f),
            -1.0f,
            1.0f);
        float lateralAcceleration = Mathf.Clamp(
            planarAcceleration.Dot(bodyRight) / Mathf.Max(maxSpeed * 6.0f, 0.01f),
            -1.0f,
            1.0f);

        float strideLength = speed > 0.01f && cycleFrequencyHz > 0.01f
            ? speed / cycleFrequencyHz
            : _spec.LegLength * HumanoidLocomotionModel.MinStrideLengthRatio;
        float maxStrideRatio = Mathf.Lerp(HumanoidLocomotionModel.MaxWalkStrideLengthRatio, HumanoidLocomotionModel.MaxRunStrideLengthRatio, runBlend);
        strideLength = Mathf.Clamp(
            strideLength,
            _spec.LegLength * HumanoidLocomotionModel.MinStrideLengthRatio,
            _spec.LegLength * maxStrideRatio);

        float stepClearance =
            Mathf.Max(_leftLeg.Contact.GroundClearance, _rightLeg.Contact.GroundClearance) +
            (_spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.WalkStepClearanceRatio, HumanoidLocomotionModel.RunStepClearanceRatio, runBlend) * _locomotionBlend);

        float desiredForwardInfluence = desiredDirection.LengthSquared() > 0.0001f
            ? desiredDirection.Dot(bodyForward)
            : 0.0f;

        HumanoidGroundMotionFrame frame = new()
        {
            VisualOrigin = visualOrigin,
            VisualBasis = visualBasis,
            TargetVelocityPlanar = targetVelocity,
            VelocityPlanar = velocityPlanar,
            Forward = bodyForward,
            Right = bodyRight,
            Speed = speed,
            SpeedRatio = speedRatio,
            RunBlend = runBlend,
            CycleFrequencyHz = cycleFrequencyHz,
            StrideLength = strideLength,
            StepClearance = stepClearance,
            StanceFraction = Mathf.Lerp(HumanoidLocomotionModel.WalkStanceFraction, HumanoidLocomotionModel.RunStanceFraction, runBlend),
            ForwardAcceleration = forwardAcceleration,
            LateralAcceleration = lateralAcceleration,
            DesiredForwardInfluence = desiredForwardInfluence
        };

        _profiler.SetMetric("gait_phase", _gaitPhase);
        _profiler.SetMetric("cycle_hz", cycleFrequencyHz);
        _profiler.SetMetric("stride_len", strideLength);
        _profiler.SetMetric("run_blend", runBlend);
        _profiler.SetMetric("dimensionless_speed", dimensionlessSpeed);
        return frame;
    }

    // Contact state owns foot placement. Pelvis and IK only react to this state rather than inventing their own targets.
    private void UpdateLegContactState(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        if (!leg.Initialized)
        {
            InitializeLegContact(leg, frame);
        }

        float gaitSpeedThreshold = _settings.MoveSpeed * HumanoidLocomotionModel.IdleToWalkSpeedRatio;
        if (frame.Speed < gaitSpeedThreshold || frame.CycleFrequencyHz <= 0.01f)
        {
            Vector3 idleSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
            Vector3 groundedIdleSupport = SampleSupportPoint(idleSupport, leg.Contact.GroundClearance, out Vector3 idleNormal);
            leg.PlantedSupportWorld = leg.PlantedSupportWorld.Lerp(groundedIdleSupport, DampFactor(HumanoidLocomotionModel.IdleFootSharpness, delta));
            leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(leg.PlantedSupportWorld, DampFactor(HumanoidLocomotionModel.IdleFootSharpness, delta));
            leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(idleNormal, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
            leg.TargetGroundNormalWorld = leg.GroundNormalWorld;
            leg.IsInStance = true;
            leg.WasInStance = true;
            leg.StanceProgress = 0.0f;
            leg.SwingProgress = 1.0f;
            PublishLegMetrics(leg);
            return;
        }

        float phase = Mathf.PosMod(_gaitPhase + leg.PhaseOffset, 1.0f);
        bool isInStance = phase < frame.StanceFraction;
        float swingFraction = Mathf.Max(1.0f - frame.StanceFraction, 0.01f);

        if (isInStance)
        {
            leg.StanceProgress = frame.StanceFraction > 0.0f
                ? Mathf.Clamp(phase / frame.StanceFraction, 0.0f, 1.0f)
                : 1.0f;

            if (!leg.WasInStance)
            {
                leg.PlantedSupportWorld = leg.SwingTargetWorld;
                leg.CurrentSupportWorld = leg.SwingTargetWorld;
                leg.GroundNormalWorld = leg.TargetGroundNormalWorld;
            }

            Vector3 plantedProbe = new(leg.PlantedSupportWorld.X, frame.VisualOrigin.Y, leg.PlantedSupportWorld.Z);
            Vector3 stanceSupport = SampleSupportPoint(plantedProbe, leg.Contact.GroundClearance, out Vector3 stanceNormal);
            stanceSupport = ClampSupportTargetToPlanarEnvelope(leg, frame, _rig.Hips.GlobalPosition, stanceSupport, allowForwardPlacement: false);
            leg.PlantedSupportWorld = stanceSupport;
            leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(leg.PlantedSupportWorld, DampFactor(HumanoidLocomotionModel.StanceFootSharpness, delta));
            leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(stanceNormal, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
            leg.TargetGroundNormalWorld = leg.GroundNormalWorld;
            leg.SwingProgress = 1.0f;
        }
        else
        {
            leg.StanceProgress = 1.0f;
            leg.SwingProgress = Mathf.Clamp((phase - frame.StanceFraction) / swingFraction, 0.0f, 1.0f);
            if (leg.WasInStance)
            {
                leg.SwingStartWorld = leg.CurrentSupportWorld;
                leg.SwingTargetWorld = PlanTouchdownWorld(leg, frame, out Vector3 targetNormal);
                leg.TargetGroundNormalWorld = targetNormal;
            }
            else if (leg.SwingProgress < HumanoidLocomotionModel.SwingRetargetWindow)
            {
                Vector3 retargetedTouchdown = PlanTouchdownWorld(leg, frame, out Vector3 retargetedNormal);
                float retargetBlend = DampFactor(HumanoidLocomotionModel.PelvisPositionSharpness * 0.45f, delta);
                leg.SwingTargetWorld = leg.SwingTargetWorld.Lerp(retargetedTouchdown, retargetBlend);
                leg.TargetGroundNormalWorld = leg.TargetGroundNormalWorld.Slerp(retargetedNormal, retargetBlend);
            }

            float swingT = Mathf.SmoothStep(0.0f, 1.0f, leg.SwingProgress);
            float swingAdvanceT = 1.0f - Mathf.Pow(1.0f - swingT, HumanoidLocomotionModel.SwingAdvanceExponent);
            Vector3 support = leg.SwingStartWorld.Lerp(leg.SwingTargetWorld, swingAdvanceT);
            support.Y += Mathf.Sin(swingT * Mathf.Pi) * frame.StepClearance;
            leg.CurrentSupportWorld = support;
            leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(leg.TargetGroundNormalWorld, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
        }

        leg.IsInStance = isInStance;
        leg.WasInStance = isInStance;
        PublishLegMetrics(leg);
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
    }

    private Vector3 PlanTouchdownWorld(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, out Vector3 groundNormal)
    {
        float cycleDuration = frame.CycleFrequencyHz > 0.01f
            ? 1.0f / frame.CycleFrequencyHz
            : 0.0f;
        float lookAheadTime = Mathf.Max(0.08f, cycleDuration * frame.StanceFraction * 0.5f);
        Vector3 forecastVelocity = frame.TargetVelocityPlanar.LengthSquared() > 0.0001f
            ? frame.TargetVelocityPlanar
            : frame.VelocityPlanar;
        float forecastDistance = forecastVelocity.Length() * lookAheadTime;
        float forwardPlacement = Mathf.Clamp(
            forecastDistance + (frame.StrideLength * 0.34f),
            _spec.LegLength * HumanoidLocomotionModel.MinRearReachRatio,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend));

        Vector3 hipWorld = _rig.Hips.GlobalPosition + (frame.VisualBasis * leg.HipOffsetFromPelvisLocal);
        Vector3 supportProbe = hipWorld + (frame.Forward * forwardPlacement);
        supportProbe.Y = frame.VisualOrigin.Y;

        Vector3 touchdown = SampleSupportPoint(supportProbe, leg.Contact.GroundClearance, out groundNormal);
        return ClampSupportTargetToPlanarEnvelope(leg, frame, _rig.Hips.GlobalPosition, touchdown, allowForwardPlacement: true);
    }

    private static Vector3 ResolveRootRelativeSupportWorld(Vector3 supportPointLocal, HumanoidGroundMotionFrame frame)
    {
        return frame.VisualOrigin + (frame.VisualBasis * supportPointLocal);
    }

    private Vector3 ClampSupportTargetToPlanarEnvelope(
        HumanoidLegMotionRuntime leg,
        HumanoidGroundMotionFrame frame,
        Vector3 pelvisWorld,
        Vector3 supportTarget,
        bool allowForwardPlacement)
    {
        Vector3 hipWorld = pelvisWorld + (frame.VisualBasis * leg.HipOffsetFromPelvisLocal);
        Vector3 offset = supportTarget - hipWorld;
        float forward = offset.Dot(frame.Forward);
        float lateral = offset.Dot(frame.Right);
        float vertical = offset.Y;

        float forwardMin = allowForwardPlacement
            ? -_spec.LegLength * HumanoidLocomotionModel.MinRearReachRatio
            : -_spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkRearReachRatio, HumanoidLocomotionModel.MaxRunRearReachRatio, frame.RunBlend);
        float forwardMax = _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend);
        float lateralMax = _spec.LegLength * HumanoidLocomotionModel.LateralReachRatio;

        forward = Mathf.Clamp(forward, forwardMin, forwardMax);
        lateral = Mathf.Clamp(lateral, -lateralMax, lateralMax);

        return hipWorld + (frame.Forward * forward) + (frame.Right * lateral) + (Vector3.Up * vertical);
    }

}
