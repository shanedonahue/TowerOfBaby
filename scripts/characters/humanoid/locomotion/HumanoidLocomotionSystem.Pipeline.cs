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

        Vector3 leftSupport = _leftLeg.Initialized
            ? _leftLeg.CurrentSupportWorld
            : visualOrigin + (visualBasis * _leftLeg.RestSupportPointLocal);
        Vector3 rightSupport = _rightLeg.Initialized
            ? _rightLeg.CurrentSupportWorld
            : visualOrigin + (visualBasis * _rightLeg.RestSupportPointLocal);
        float leftSupportWeight = ResolveGroundSupportWeight(_leftLeg);
        float rightSupportWeight = ResolveGroundSupportWeight(_rightLeg);
        float totalSupportWeight = Mathf.Max(leftSupportWeight + rightSupportWeight, 0.001f);
        Vector3 supportCenter = ((leftSupport * leftSupportWeight) + (rightSupport * rightSupportWeight)) / totalSupportWeight;
        float supportHeight = ((leftSupport.Y * leftSupportWeight) + (rightSupport.Y * rightSupportWeight)) / totalSupportWeight;
        Vector3 pelvisWorld = _rig.Hips.GlobalPosition;
        Vector3 planarCom = new(
            Mathf.Lerp(visualOrigin.X, pelvisWorld.X, HumanoidLocomotionModel.ComEstimatePelvisBlend),
            supportHeight,
            Mathf.Lerp(visualOrigin.Z, pelvisWorld.Z, HumanoidLocomotionModel.ComEstimatePelvisBlend));

        float gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity") * _settings.GravityScale;
        float comHeight = Mathf.Max(_spec.LegLength * 0.45f, pelvisWorld.Y - supportHeight + (_spec.TorsoHeight * 0.22f));
        float omega = Mathf.Sqrt(Mathf.Max(gravity, 0.01f) / Mathf.Max(comHeight, 0.1f));
        float captureForward = velocityPlanar.Dot(bodyForward) / Mathf.Max(omega, 0.1f);
        float captureLateral = velocityPlanar.Dot(bodyRight) / Mathf.Max(omega, 0.1f) * HumanoidLocomotionModel.CapturePointLateralScale;
        Vector3 balanceTarget = planarCom + (bodyForward * captureForward) + (bodyRight * captureLateral);
        Vector3 balanceError = balanceTarget - new Vector3(supportCenter.X, supportHeight, supportCenter.Z);
        balanceError.Y = 0.0f;

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
            DesiredForwardInfluence = desiredForwardInfluence,
            SupportCenter = supportCenter,
            PlanarCom = planarCom,
            BalanceTarget = balanceTarget,
            BalanceError = balanceError,
            SupportHeight = supportHeight,
            ComHeight = comHeight,
            BalanceErrorForward = balanceError.Dot(bodyForward),
            BalanceErrorLateral = balanceError.Dot(bodyRight)
        };

        _profiler.SetMetric("speed_ratio", speedRatio);
        _profiler.SetMetric("run_blend", runBlend);
        _profiler.SetMetric("step_duration", stepDurationSeconds);
        _profiler.SetMetric("step_length", stepLength);
        _profiler.SetMetric("com_error", balanceError.Length());
        _profiler.SetMetric("com_to_support_center_error", planarCom.DistanceTo(new Vector3(supportCenter.X, supportHeight, supportCenter.Z)));
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
            PublishGroundMetrics();
            return;
        }

        if (allowNewSteps && !leftStepping && !rightStepping)
        {
            TryBeginStep(frame);
        }

        if (_leftLeg.IsInStance && !_rightLeg.IsInStance)
        {
            UpdateStanceLeg(_leftLeg, frame, delta);
            UpdateSwingLeg(_rightLeg, frame, delta);
        }
        else if (_rightLeg.IsInStance && !_leftLeg.IsInStance)
        {
            UpdateStanceLeg(_rightLeg, frame, delta);
            UpdateSwingLeg(_leftLeg, frame, delta);
        }
        else
        {
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
        }

        _profiler.SetMetric("step_left_active", _leftLeg.IsInStance ? 0.0f : 1.0f);
        _profiler.SetMetric("step_right_active", _rightLeg.IsInStance ? 0.0f : 1.0f);

        if (allowNewSteps && (leftStepping || rightStepping) && _leftLeg.IsInStance && _rightLeg.IsInStance)
        {
            TryBeginStep(frame);
            _profiler.SetMetric("step_left_active", _leftLeg.IsInStance ? 0.0f : 1.0f);
            _profiler.SetMetric("step_right_active", _rightLeg.IsInStance ? 0.0f : 1.0f);
        }

        PublishGroundMetrics();
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
        leg.StanceTimeSeconds = frame.StepDurationSeconds;
        leg.HeelStrikeWeight = 0.0f;
        leg.ToeOffWeight = 0.0f;
        leg.RearReachSaturation = 0.0f;
        leg.RearReachDistance = 0.0f;
        leg.RearReleaseArmed = false;
        leg.ComTrailDistance = 0.0f;
        leg.PlannedTouchdownBias = 0.0f;
        leg.BalanceTouchdownBias = 0.0f;
        leg.StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
        leg.FootSkateDistance = 0.0f;
        leg.LastStancePivotWorld = Vector3.Zero;
        leg.DebugSupportTargetWorld = leg.CurrentSupportWorld;
        UpdateFootContactModel(leg, frame);
        PublishLegMetrics(leg);
    }

    private void TryBeginStep(HumanoidGroundMotionFrame frame)
    {
        HumanoidLegMotionRuntime preferredLeg = _stepLeftNext ? _leftLeg : _rightLeg;
        HumanoidLegMotionRuntime alternateLeg = _stepLeftNext ? _rightLeg : _leftLeg;
        HumanoidStepReleaseDecision preferredDecision = EvaluateStepRelease(preferredLeg, frame);
        HumanoidStepReleaseDecision alternateDecision = EvaluateStepRelease(alternateLeg, frame);

        if (!preferredDecision.ShouldStart && !alternateDecision.ShouldStart)
        {
            return;
        }

        HumanoidLegMotionRuntime selectedLeg = preferredLeg;
        HumanoidStepReleaseDecision selectedDecision = preferredDecision;

        if (!preferredDecision.ShouldStart)
        {
            selectedLeg = alternateLeg;
            selectedDecision = alternateDecision;
        }
        else if (alternateDecision.ShouldStart && alternateDecision.Urgency > preferredDecision.Urgency + 0.12f)
        {
            selectedLeg = alternateLeg;
            selectedDecision = alternateDecision;
        }

        BeginStep(selectedLeg, frame, selectedDecision.IsEarlyRelease);
    }

    private void BeginStep(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, bool isEarlyRelease)
    {
        leg.IsInStance = false;
        leg.WasInStance = false;
        leg.StanceProgress = 1.0f;
        leg.SwingProgress = 0.0f;
        leg.StanceTimeSeconds = 0.0f;
        leg.HeelStrikeWeight = 0.0f;
        leg.ToeOffWeight = 0.0f;
        leg.StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
        leg.ActiveSupportOffsetLocal = leg.Contact.SupportOffsetLocal;
        leg.FootSkateDistance = 0.0f;
        leg.LastStancePivotWorld = Vector3.Zero;
        leg.RearReleaseArmed = false;
        leg.SwingStartWorld = leg.CurrentSupportWorld;
        leg.SwingTargetWorld = PlanStepTargetWorld(leg, frame, out Vector3 groundNormal);
        leg.TargetGroundNormalWorld = groundNormal;

        if (isEarlyRelease)
        {
            _earlyReleaseEventsThisFrame++;
            leg.EarlyReleaseDebugTimer = 0.45f;
            leg.EarlyReleaseEventWorld = leg.FootPivotWorld.LengthSquared() > 0.0001f
                ? leg.FootPivotWorld
                : leg.CurrentSupportWorld;
        }
    }

    private void UpdateStanceLeg(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        leg.StanceTimeSeconds += delta;
        EvaluateLegSupportState(
            leg,
            frame,
            leg.PlantedSupportWorld,
            out Vector3 nominalSupport,
            out float trailingDistance,
            out _,
            out float triggerDistance,
            out _,
            out float rearReachDistance,
            out float rearReachSaturation,
            out float comTrailDistance);

        float stanceProgress = Mathf.Clamp(trailingDistance / Mathf.Max(triggerDistance, 0.001f), 0.0f, 1.0f);
        leg.RearReachDistance = rearReachDistance;
        leg.RearReachSaturation = rearReachSaturation;
        leg.ComTrailDistance = comTrailDistance;
        float toeOffWeight = ComputeToeOffWeight(frame, stanceProgress, rearReachSaturation, comTrailDistance);
        float adhesionRelease = leg.RearReleaseArmed ? Mathf.Max(toeOffWeight, 0.62f) : toeOffWeight;
        Vector3 releaseTarget = leg.PlantedSupportWorld.Lerp(nominalSupport, adhesionRelease * (HumanoidLocomotionModel.ToeOffNominalBlend + 0.18f));
        releaseTarget += frame.Forward * (_spec.FootLength * HumanoidLocomotionModel.ToeOffSupportForwardRatio * adhesionRelease);

        Vector3 plantedProbe = new(releaseTarget.X, frame.VisualOrigin.Y, releaseTarget.Z);
        Vector3 plantedSupport = SampleSupportPoint(plantedProbe, leg.Contact.GroundClearance, out Vector3 groundNormal);
        leg.PlantedSupportWorld = ClampSupportTargetToPlanarEnvelope(leg, frame, plantedSupport);
        float stanceSharpness = Mathf.Lerp(
            HumanoidLocomotionModel.StanceFootSharpness,
            HumanoidLocomotionModel.StanceFootSharpness * HumanoidLocomotionModel.ToeOffStickinessFactor,
            adhesionRelease);
        leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(leg.PlantedSupportWorld, DampFactor(stanceSharpness, delta));
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(groundNormal, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
        leg.TargetGroundNormalWorld = leg.GroundNormalWorld;

        EvaluateLegSupportState(
            leg,
            frame,
            leg.PlantedSupportWorld,
            out _,
            out trailingDistance,
            out _,
            out triggerDistance,
            out _,
            out rearReachDistance,
            out rearReachSaturation,
            out comTrailDistance);

        leg.StanceProgress = Mathf.Clamp(trailingDistance / Mathf.Max(triggerDistance, 0.001f), 0.0f, 1.0f);
        leg.HeelStrikeWeight = ComputeHeelStrikeWeight(leg.StanceTimeSeconds, frame.StepDurationSeconds);
        leg.ToeOffWeight = ComputeToeOffWeight(frame, leg.StanceProgress, rearReachSaturation, comTrailDistance);
        leg.RearReachDistance = rearReachDistance;
        leg.RearReachSaturation = rearReachSaturation;
        leg.ComTrailDistance = comTrailDistance;
        leg.SwingProgress = 1.0f;
        leg.IsInStance = true;
        leg.WasInStance = true;
        UpdateFootContactModel(leg, frame);
        PublishLegMetrics(leg);
    }

    private void UpdateSwingLeg(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, float delta)
    {
        float swingUrgency = ComputeOppositeSupportUrgency(leg, frame);
        float swingDurationSeconds = Mathf.Max(
            frame.StepDurationSeconds * Mathf.Lerp(1.0f, 1.0f - HumanoidLocomotionModel.SwingCatchupScale, swingUrgency),
            frame.StepDurationSeconds * 0.45f);
        leg.SwingProgress = Mathf.Clamp(leg.SwingProgress + (delta / swingDurationSeconds), 0.0f, 1.0f);
        leg.HeelStrikeWeight = 0.0f;
        leg.ToeOffWeight = 0.0f;
        leg.RearReachDistance = 0.0f;
        leg.RearReachSaturation = 0.0f;
        leg.ComTrailDistance = 0.0f;
        leg.StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
        leg.ActiveSupportOffsetLocal = leg.Contact.SupportOffsetLocal;
        leg.RearReleaseArmed = false;

        if (leg.SwingProgress < HumanoidLocomotionModel.SwingRetargetWindow)
        {
            Vector3 retargetedSupport = PlanStepTargetWorld(leg, frame, out Vector3 retargetedNormal);
            float blend = leg.SwingProgress / Mathf.Max(HumanoidLocomotionModel.SwingRetargetWindow, 0.001f);
            leg.SwingTargetWorld = leg.SwingTargetWorld.Lerp(retargetedSupport, blend * 0.35f);
            leg.TargetGroundNormalWorld = leg.TargetGroundNormalWorld.Slerp(retargetedNormal, blend * 0.35f);
        }

        float swingT = Mathf.SmoothStep(0.0f, 1.0f, leg.SwingProgress);
        Vector3 support = leg.SwingStartWorld.Lerp(leg.SwingTargetWorld, swingT);
        float swingArcScale = Mathf.Lerp(1.0f, 0.55f, swingUrgency);
        support.Y += Mathf.Sin(swingT * Mathf.Pi) * frame.StepHeight * swingArcScale;
        leg.CurrentSupportWorld = support;
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(leg.TargetGroundNormalWorld, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));

        if (leg.SwingProgress >= 1.0f)
        {
            CompleteStep(leg, frame);
        }

        PublishLegMetrics(leg);
    }

    private void CompleteStep(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        leg.PlantedSupportWorld = leg.SwingTargetWorld;
        leg.CurrentSupportWorld = leg.SwingTargetWorld;
        leg.GroundNormalWorld = leg.TargetGroundNormalWorld;
        leg.IsInStance = true;
        leg.WasInStance = true;
        leg.StanceProgress = 0.0f;
        leg.SwingProgress = 1.0f;
        leg.StanceTimeSeconds = 0.0f;
        leg.HeelStrikeWeight = 1.0f;
        leg.ToeOffWeight = 0.0f;
        leg.RearReachDistance = 0.0f;
        leg.RearReachSaturation = 0.0f;
        leg.ComTrailDistance = 0.0f;
        leg.RearReleaseArmed = false;
        leg.StanceFootPhase = HumanoidStanceFootPhase.HeelStrike;
        leg.FootSkateDistance = 0.0f;
        leg.LastStancePivotWorld = Vector3.Zero;
        UpdateFootContactModel(leg, frame);
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
        leg.StanceTimeSeconds = HumanoidLocomotionModel.WalkStepDurationSeconds;
        leg.HeelStrikeWeight = 0.0f;
        leg.ToeOffWeight = 0.0f;
        leg.RearReachDistance = 0.0f;
        leg.RearReachSaturation = 0.0f;
        leg.ComTrailDistance = 0.0f;
        leg.PlannedTouchdownBias = 0.0f;
        leg.BalanceTouchdownBias = 0.0f;
        leg.StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
        leg.FootSkateDistance = 0.0f;
        leg.LastStancePivotWorld = Vector3.Zero;
        leg.RearReleaseArmed = false;
        leg.DebugSupportTargetWorld = groundedSupport;
        UpdateFootContactModel(leg, frame);
    }

    private Vector3 PlanStepTargetWorld(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, out Vector3 groundNormal)
    {
        Vector3 nominalSupport = ResolveSupportSeekingNominalSupportWorld(leg, frame);
        float baseForwardPlacement = Mathf.Clamp(
            frame.StepLength * 0.55f,
            _spec.LegLength * 0.12f,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend));
        float captureBias = Mathf.Clamp(
            frame.BalanceErrorForward * HumanoidLocomotionModel.CapturePointPlacementGain,
            -_spec.LegLength * 0.08f,
            _spec.LegLength * HumanoidLocomotionModel.CapturePointBiasClampRatio);
        float rearBias = leg.RearReachSaturation * _spec.LegLength * 0.06f;
        float forwardPlacement = Mathf.Clamp(
            baseForwardPlacement + captureBias + rearBias,
            _spec.LegLength * 0.08f,
            _spec.LegLength * Mathf.Lerp(HumanoidLocomotionModel.MaxWalkTouchdownForwardRatio, HumanoidLocomotionModel.MaxRunTouchdownForwardRatio, frame.RunBlend));
        float lateralPlacement = Mathf.Clamp(
            frame.BalanceErrorLateral * HumanoidLocomotionModel.CapturePointLateralGain,
            -_spec.LegLength * 0.08f,
            _spec.LegLength * 0.08f);

        Vector3 targetProbe = nominalSupport + (frame.Forward * forwardPlacement) + (frame.Right * lateralPlacement);
        Vector3 touchdown = SampleSupportPoint(targetProbe, leg.Contact.GroundClearance, out groundNormal);
        Vector3 clampedTouchdown = ClampSupportTargetToPlanarEnvelope(leg, frame, touchdown);
        leg.PlannedTouchdownBias = captureBias + rearBias;
        leg.BalanceTouchdownBias = captureBias;
        leg.DebugSupportTargetWorld = clampedTouchdown;
        return clampedTouchdown;
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

    private void UpdateFootContactModel(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        Vector3 previousPivotWorld = leg.LastStancePivotWorld;
        Basis flatBasis = CreateFootBasis(leg.GroundNormalWorld, frame.Forward);
        SampleFootContactPoints(leg, flatBasis, out Vector3 heelSample, out Vector3 toeSample, out Vector3 supportNormal);
        leg.StanceFootPhase = ResolveStanceFootPhase(leg);

        Basis footBasis;
        Vector3 footPivotWorld;
        Vector3 activeSupportOffsetLocal;
        Vector3 heelContactWorld;
        Vector3 toeContactWorld;

        switch (leg.StanceFootPhase)
        {
            case HumanoidStanceFootPhase.HeelStrike:
                footBasis = CreateFootBasis(supportNormal, frame.Forward);
                footBasis = footBasis.Rotated(
                    footBasis.X.Normalized(),
                    leg.HeelStrikeWeight * HumanoidLocomotionModel.HeelStrikePitchRadians).Orthonormalized();
                heelContactWorld = heelSample;
                toeContactWorld = heelContactWorld + (footBasis * (leg.ToeContactLocal - leg.HeelContactLocal));
                footPivotWorld = heelContactWorld;
                activeSupportOffsetLocal = leg.HeelContactLocal;
                break;

            case HumanoidStanceFootPhase.ToeOff:
                footBasis = CreateFootBasis(supportNormal, frame.Forward);
                footBasis = footBasis.Rotated(
                    footBasis.X.Normalized(),
                    -leg.ToeOffWeight * HumanoidLocomotionModel.ToeOffPitchRadians).Orthonormalized();
                toeContactWorld = toeSample;
                Vector3 releasedHeelWorld = toeContactWorld + (footBasis * (leg.HeelContactLocal - leg.ToeContactLocal));
                heelContactWorld = heelSample.Lerp(releasedHeelWorld, leg.ToeOffWeight);
                footPivotWorld = toeContactWorld;
                activeSupportOffsetLocal = leg.ToeContactLocal;
                break;

            default:
                footBasis = CreateFootBasis(supportNormal, frame.Forward);
                heelContactWorld = heelSample;
                toeContactWorld = toeSample;
                footPivotWorld = ResolveFlatFootPivotWorld(leg, heelContactWorld, toeContactWorld);
                activeSupportOffsetLocal = leg.Contact.SupportOffsetLocal;
                break;
        }

        leg.FootBasisWorld = footBasis;
        leg.FootPivotWorld = footPivotWorld;
        leg.ActiveSupportOffsetLocal = activeSupportOffsetLocal;
        leg.HeelContactWorld = heelContactWorld;
        leg.ToeContactWorld = toeContactWorld;

        if (leg.IsInStance)
        {
            if (previousPivotWorld.LengthSquared() > 0.0001f)
            {
                leg.FootSkateDistance += previousPivotWorld.DistanceTo(footPivotWorld);
            }

            leg.LastStancePivotWorld = footPivotWorld;
        }
        else
        {
            leg.LastStancePivotWorld = Vector3.Zero;
        }
    }

    private void SampleFootContactPoints(
        HumanoidLegMotionRuntime leg,
        Basis footBasis,
        out Vector3 heelContactWorld,
        out Vector3 toeContactWorld,
        out Vector3 supportNormal)
    {
        Vector3 heelProbe = leg.PlantedSupportWorld + (footBasis * (leg.HeelContactLocal - leg.Contact.SupportOffsetLocal));
        Vector3 toeProbe = leg.PlantedSupportWorld + (footBasis * (leg.ToeContactLocal - leg.Contact.SupportOffsetLocal));
        heelContactWorld = SampleSupportPoint(heelProbe, leg.Contact.GroundClearance, out Vector3 heelNormal);
        toeContactWorld = SampleSupportPoint(toeProbe, leg.Contact.GroundClearance, out Vector3 toeNormal);
        supportNormal = (heelNormal + toeNormal + leg.GroundNormalWorld).Normalized();
        if (supportNormal.LengthSquared() < 0.0001f)
        {
            supportNormal = Vector3.Up;
        }
    }

    private static HumanoidStanceFootPhase ResolveStanceFootPhase(HumanoidLegMotionRuntime leg)
    {
        if (leg.StanceFootPhase == HumanoidStanceFootPhase.ToeOff)
        {
            return HumanoidStanceFootPhase.ToeOff;
        }

        if (leg.StanceFootPhase == HumanoidStanceFootPhase.HeelStrike &&
            leg.HeelStrikeWeight > HumanoidLocomotionModel.HeelStrikeExitWeight &&
            leg.ToeOffWeight < HumanoidLocomotionModel.ToeOffEnterWeight)
        {
            return HumanoidStanceFootPhase.HeelStrike;
        }

        if (leg.ToeOffWeight >= HumanoidLocomotionModel.ToeOffEnterWeight)
        {
            return HumanoidStanceFootPhase.ToeOff;
        }

        if (leg.HeelStrikeWeight > HumanoidLocomotionModel.HeelStrikeExitWeight)
        {
            return HumanoidStanceFootPhase.HeelStrike;
        }

        return HumanoidStanceFootPhase.FootFlat;
    }

    private static Vector3 ResolveFlatFootPivotWorld(
        HumanoidLegMotionRuntime leg,
        Vector3 heelContactWorld,
        Vector3 toeContactWorld)
    {
        float heelToToeSpan = leg.HeelContactLocal.Z - leg.ToeContactLocal.Z;
        if (Mathf.Abs(heelToToeSpan) < 0.001f)
        {
            return (heelContactWorld + toeContactWorld) * 0.5f;
        }

        float pivotBlend = Mathf.Clamp(
            (leg.HeelContactLocal.Z - leg.Contact.SupportOffsetLocal.Z) / heelToToeSpan,
            0.0f,
            1.0f);
        return heelContactWorld.Lerp(toeContactWorld, pivotBlend);
    }

    private HumanoidStepReleaseDecision EvaluateStepRelease(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        if (!leg.IsInStance)
        {
            return default;
        }

        EvaluateLegSupportState(
            leg,
            frame,
            leg.PlantedSupportWorld,
            out _,
            out float trailingDistance,
            out float lateralError,
            out float triggerDistance,
            out float triggerLateral,
            out float rearReachDistance,
            out float rearReachSaturation,
            out float comTrailDistance);

        float nominalScore = trailingDistance / Mathf.Max(triggerDistance, 0.001f);
        float lateralScore = lateralError / Mathf.Max(triggerLateral, 0.001f);
        float comReleaseDistance = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.ComReleaseDistanceRatio,
            HumanoidLocomotionModel.ComReleaseDistanceRatio * 1.2f,
            frame.RunBlend);
        float comScore = comTrailDistance / Mathf.Max(comReleaseDistance, 0.001f);
        float rearReleaseThreshold = ComputeRearReleaseThreshold(frame);
        float rearReleaseExitThreshold = Mathf.Max(0.05f, rearReleaseThreshold - HumanoidLocomotionModel.RearReleaseHysteresis);
        bool rearReleaseArmed = rearReachSaturation >= rearReleaseThreshold ||
            (leg.RearReleaseArmed && rearReachSaturation >= rearReleaseExitThreshold);
        if (leg.StanceFootPhase == HumanoidStanceFootPhase.HeelStrike)
        {
            rearReleaseArmed = false;
        }

        float rearScore = rearReleaseArmed
            ? rearReachSaturation / Mathf.Max(rearReleaseThreshold, 0.001f)
            : rearReachSaturation / Mathf.Max(rearReleaseThreshold + HumanoidLocomotionModel.RearReleaseHysteresis, 0.001f);
        float urgency = Mathf.Max(Mathf.Max(nominalScore, lateralScore), Mathf.Max(comScore, rearScore));
        float earlyScore = Mathf.Max(comScore, rearScore);

        leg.RearReachDistance = rearReachDistance;
        leg.RearReachSaturation = rearReachSaturation;
        leg.RearReleaseArmed = rearReleaseArmed;
        leg.ComTrailDistance = comTrailDistance;

        return new HumanoidStepReleaseDecision
        {
            ShouldStart = urgency >= 1.0f,
            IsEarlyRelease = earlyScore >= 1.0f && earlyScore >= Mathf.Max(nominalScore, lateralScore),
            Urgency = urgency
        };
    }

    private void EvaluateLegSupportState(
        HumanoidLegMotionRuntime leg,
        HumanoidGroundMotionFrame frame,
        Vector3 supportWorld,
        out Vector3 nominalSupport,
        out float trailingDistance,
        out float lateralError,
        out float triggerDistance,
        out float triggerLateral,
        out float rearReachDistance,
        out float rearReachSaturation,
        out float comTrailDistance)
    {
        nominalSupport = ResolveRootRelativeSupportWorld(leg.RestSupportPointLocal, frame);
        Vector3 supportError = supportWorld - nominalSupport;
        trailingDistance = Mathf.Max(0.0f, -supportError.Dot(frame.Forward));
        lateralError = Mathf.Abs(supportError.Dot(frame.Right));
        triggerDistance = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.StepTriggerDistanceRatio,
            HumanoidLocomotionModel.StepTriggerDistanceRatio * 1.3f,
            frame.RunBlend);
        triggerLateral = _spec.LegLength * HumanoidLocomotionModel.StepTriggerLateralRatio;

        Vector3 hipWorld = _rig.Hips.GlobalPosition + (frame.VisualBasis * leg.HipOffsetFromPelvisLocal);
        rearReachDistance = Mathf.Max(0.0f, -(supportWorld - hipWorld).Dot(frame.Forward));
        float rearReachLimit = _spec.LegLength * HumanoidLocomotionModel.RearReachRatio;
        rearReachSaturation = Mathf.Clamp(rearReachDistance / Mathf.Max(rearReachLimit, 0.001f), 0.0f, 1.0f);
        comTrailDistance = Mathf.Max(0.0f, (frame.PlanarCom - supportWorld).Dot(frame.Forward));
    }

    private static float ComputeHeelStrikeWeight(float stanceTimeSeconds, float stepDurationSeconds)
    {
        float heelWindow = Mathf.Max(stepDurationSeconds * HumanoidLocomotionModel.HeelStrikeWindowRatio, 0.01f);
        float heelT = Mathf.Clamp(stanceTimeSeconds / heelWindow, 0.0f, 1.0f);
        return 1.0f - Mathf.SmoothStep(0.0f, 1.0f, heelT);
    }

    private float ComputeToeOffWeight(
        HumanoidGroundMotionFrame frame,
        float stanceProgress,
        float rearReachSaturation,
        float comTrailDistance)
    {
        float comReleaseDistance = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.ComReleaseDistanceRatio,
            HumanoidLocomotionModel.ComReleaseDistanceRatio * 1.2f,
            frame.RunBlend);
        float lateStance = Mathf.Max(
            stanceProgress,
            Mathf.Max(rearReachSaturation, comTrailDistance / Mathf.Max(comReleaseDistance, 0.001f)));
        float toeOffT = Mathf.Clamp(
            (lateStance - HumanoidLocomotionModel.ToeOffStart) / Mathf.Max(1.0f - HumanoidLocomotionModel.ToeOffStart, 0.001f),
            0.0f,
            1.0f);
        float speedWeight = Mathf.Lerp(0.68f, 1.0f, frame.SpeedRatio);
        return Mathf.SmoothStep(0.0f, 1.0f, toeOffT) * speedWeight;
    }

    private void PublishGroundMetrics()
    {
        _profiler.SetMetric("rear_reach_left", _leftLeg.RearReachDistance);
        _profiler.SetMetric("rear_reach_right", _rightLeg.RearReachDistance);
        _profiler.SetMetric("rear_reach_saturation", Mathf.Max(_leftLeg.RearReachSaturation, _rightLeg.RearReachSaturation));
        _profiler.SetMetric("rear_reach_saturation_left", _leftLeg.RearReachSaturation);
        _profiler.SetMetric("rear_reach_saturation_right", _rightLeg.RearReachSaturation);
        _profiler.SetMetric("early_release_events", _earlyReleaseEventsThisFrame);
        _profiler.SetMetric("toe_off_weight", Mathf.Max(_leftLeg.ToeOffWeight, _rightLeg.ToeOffWeight));
        _profiler.SetMetric("toe_off_blend", Mathf.Max(_leftLeg.ToeOffWeight, _rightLeg.ToeOffWeight));
        _profiler.SetMetric("foot_skate_distance", Mathf.Max(_leftLeg.FootSkateDistance, _rightLeg.FootSkateDistance));
        _profiler.SetMetric("foot_skate_distance_left", _leftLeg.FootSkateDistance);
        _profiler.SetMetric("foot_skate_distance_right", _rightLeg.FootSkateDistance);
    }

    private float ComputeRearReleaseThreshold(HumanoidGroundMotionFrame frame)
    {
        return Mathf.Lerp(
            HumanoidLocomotionModel.RearReleaseSaturationThreshold,
            HumanoidLocomotionModel.RearReleaseSaturationThreshold * 0.82f,
            frame.RunBlend);
    }

    private float ComputeOppositeSupportUrgency(HumanoidLegMotionRuntime steppingLeg, HumanoidGroundMotionFrame frame)
    {
        HumanoidLegMotionRuntime supportLeg = steppingLeg == _leftLeg ? _rightLeg : _leftLeg;
        if (!supportLeg.IsInStance)
        {
            return 0.0f;
        }

        float comReleaseDistance = _spec.LegLength * Mathf.Lerp(
            HumanoidLocomotionModel.ComReleaseDistanceRatio,
            HumanoidLocomotionModel.ComReleaseDistanceRatio * 1.2f,
            frame.RunBlend);
        float comUrgency = supportLeg.ComTrailDistance / Mathf.Max(comReleaseDistance, 0.001f);
        return Mathf.Clamp(Mathf.Max(supportLeg.RearReachSaturation, comUrgency), 0.0f, 1.0f);
    }

    private float ResolveGroundSupportWeight(HumanoidLegMotionRuntime leg)
    {
        if (!leg.IsInStance)
        {
            return 0.08f;
        }

        return Mathf.Lerp(1.0f, 0.42f, leg.ToeOffWeight);
    }

    private Vector3 ResolveSupportSeekingNominalSupportWorld(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame)
    {
        float lookaheadSeconds = Mathf.Lerp(
            HumanoidLocomotionModel.SupportSeekLookaheadWalkSeconds,
            HumanoidLocomotionModel.SupportSeekLookaheadRunSeconds,
            frame.RunBlend);
        Vector3 predictedOrigin = new(frame.VisualOrigin.X, frame.VisualOrigin.Y, frame.VisualOrigin.Z);
        predictedOrigin += frame.VelocityPlanar * lookaheadSeconds;
        predictedOrigin.X = Mathf.Lerp(predictedOrigin.X, frame.BalanceTarget.X, HumanoidLocomotionModel.SupportSeekBalanceBlend);
        predictedOrigin.Z = Mathf.Lerp(predictedOrigin.Z, frame.BalanceTarget.Z, HumanoidLocomotionModel.SupportSeekBalanceBlend);
        return predictedOrigin + (frame.VisualBasis * leg.RestSupportPointLocal);
    }
}
