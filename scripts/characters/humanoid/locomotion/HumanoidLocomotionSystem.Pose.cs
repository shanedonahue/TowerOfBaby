using Godot;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    private void UpdatePelvisPose(HumanoidGroundMotionFrame frame, float delta)
    {
        float leftWeight = ResolveSupportWeight(_leftLeg);
        float rightWeight = ResolveSupportWeight(_rightLeg);
        float totalWeight = leftWeight + rightWeight;

        float supportHeight = ((_leftLeg.CurrentSupportWorld.Y * leftWeight) + (_rightLeg.CurrentSupportWorld.Y * rightWeight)) / totalWeight;
        float supportDelta = rightWeight - leftWeight;
        float footHeightDelta = _leftLeg.CurrentSupportWorld.Y - _rightLeg.CurrentSupportWorld.Y;
        Vector3 nominalPelvisWorld = frame.VisualOrigin + (frame.VisualBasis * _motionDefinition.GetJointRestPosition("pelvis"));
        float pushOffWeight = Mathf.Max(_leftLeg.ToeOffWeight, _rightLeg.ToeOffWeight);

        Vector3 desiredPelvisWorld = nominalPelvisWorld;
        desiredPelvisWorld.Y = supportHeight + _spec.HipHeight - (_spec.LegLength * HumanoidLocomotionModel.PelvisSpeedCompressionRatio * frame.SpeedRatio);
        desiredPelvisWorld += frame.Right * (supportDelta * _spec.HipWidth * HumanoidLocomotionModel.PelvisSupportShiftRatio);
        desiredPelvisWorld += frame.Forward * (_spec.LegLength * HumanoidLocomotionModel.PelvisForwardBiasRatio * frame.SpeedRatio * frame.DesiredForwardInfluence);
        desiredPelvisWorld += frame.Forward * (_spec.LegLength * HumanoidLocomotionModel.PelvisPushOffRatio * pushOffWeight);

        _rig.Hips.Position = _rig.Hips.Position.Lerp(
            _rig.VisualRoot.ToLocal(desiredPelvisWorld),
            DampFactor(HumanoidLocomotionModel.PelvisPositionSharpness, delta));

        Vector3 desiredRotation = new(
            -frame.SpeedRatio * frame.DesiredForwardInfluence * HumanoidLocomotionModel.PelvisPitchFromSpeed,
            0.0f,
            Mathf.Clamp(
                (footHeightDelta * HumanoidLocomotionModel.PelvisRollFromHeight) - (supportDelta * HumanoidLocomotionModel.PelvisRollFromSupport),
                -0.14f,
                0.14f));
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(
            desiredRotation,
            DampFactor(HumanoidLocomotionModel.PelvisRotationSharpness, delta));

        _profiler.SetMetric("pelvis_y", _rig.Hips.GlobalPosition.Y);
        _profiler.SetMetric("support_width", _leftLeg.CurrentSupportWorld.DistanceTo(_rightLeg.CurrentSupportWorld));
    }

    private void ApplyLegPose(HumanoidLegMotionRuntime leg, Vector3 preferredForward)
    {
        Basis footBasis = leg.IsInStance
            ? leg.FootBasisWorld
            : CreateFootBasis(leg.GroundNormalWorld, preferredForward);
        Vector3 supportOffsetLocal = leg.IsInStance
            ? leg.ActiveSupportOffsetLocal
            : leg.Contact.SupportOffsetLocal;
        Vector3 supportPivotWorld = leg.IsInStance
            ? leg.FootPivotWorld
            : leg.CurrentSupportWorld;
        Vector3 footJointTarget = supportPivotWorld - (footBasis * supportOffsetLocal);
        Vector3 hipWorld = _rig.Hips.ToGlobal(leg.HipOffsetFromPelvisLocal);
        Vector3 bendPlaneNormalWorld = (_rig.VisualRoot.GlobalBasis * leg.Chain.PreferredBendNormalLocal).Normalized();

        SolveLeg(leg.Rig, hipWorld, footJointTarget, footBasis, bendPlaneNormalWorld);

        leg.Rig.IsStepping = !leg.IsInStance;
        leg.Rig.StepProgress = leg.SwingProgress;
        leg.Rig.CurrentFootPosition = footJointTarget;
        leg.Rig.PlantedFootPosition = leg.IsInStance
            ? footJointTarget
            : leg.PlantedSupportWorld - (footBasis * leg.Contact.SupportOffsetLocal);
        leg.Rig.TargetFootPosition = leg.IsInStance
            ? footJointTarget
            : leg.SwingTargetWorld - (footBasis * leg.Contact.SupportOffsetLocal);
        leg.Rig.GroundNormal = leg.GroundNormalWorld;
        leg.Rig.TargetNormal = leg.TargetGroundNormalWorld;
    }

    private void UpdateUpperBodyPose(HumanoidGroundMotionFrame frame, float delta, float cameraPitch)
    {
        float leftSwing = !_leftLeg.IsInStance ? Mathf.Sin(_leftLeg.SwingProgress * Mathf.Pi) : 0.0f;
        float rightSwing = !_rightLeg.IsInStance ? Mathf.Sin(_rightLeg.SwingProgress * Mathf.Pi) : 0.0f;
        float gaitSwing = leftSwing - rightSwing;
        float pushOffWeight = Mathf.Max(_leftLeg.ToeOffWeight, _rightLeg.ToeOffWeight);
        float torsoBob = (
            (Mathf.Abs(gaitSwing) * _spec.TorsoHeight * 0.007f) +
            (pushOffWeight * _spec.TorsoHeight * 0.01f)) * frame.SpeedRatio;
        float forwardLean = frame.DesiredForwardInfluence * Mathf.Lerp(
            HumanoidLocomotionModel.TorsoLeanWalk,
            HumanoidLocomotionModel.TorsoLeanRun,
            frame.RunBlend) * frame.SpeedRatio;

        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = _rig.UpperBody.Rotation.Lerp(
            new Vector3(-forwardLean * 0.2f, 0.0f, -_rig.Hips.Rotation.Z * 0.35f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        _rig.Torso.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.5f) + torsoBob, 0.0f);
        _rig.Torso.Rotation = _rig.Torso.Rotation.Lerp(
            new Vector3(-forwardLean, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        _rig.ChestBand.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.62f) + (torsoBob * 0.8f), 0.0f);
        _rig.Head.Position = new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f) + (torsoBob * 0.35f), 0.0f);
        _rig.Head.Rotation = _rig.Head.Rotation.Lerp(
            new Vector3((-cameraPitch * 0.24f) + (forwardLean * 0.08f), 0.0f, -_rig.Hips.Rotation.Z * 0.18f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        float armSwing = gaitSwing * Mathf.Lerp(
            HumanoidLocomotionModel.ArmSwingWalk,
            HumanoidLocomotionModel.ArmSwingRun,
            frame.RunBlend) * frame.SpeedRatio;
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(
            new Vector3(-armSwing, -0.04f, -0.18f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(
            new Vector3(armSwing, 0.04f, 0.18f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
    }

    private void UpdateAirborneRig(float delta, Vector3 desiredDirection, float cameraPitch)
    {
        Vector3 bodyForward = desiredDirection.LengthSquared() > 0.0001f
            ? desiredDirection
            : _lastFacingForward;
        _rig.Hips.Position = _rig.Hips.Position.Lerp(
            new Vector3(0.0f, _spec.HipHeight, -_spec.LegLength * 0.02f),
            DampFactor(HumanoidLocomotionModel.PelvisPositionSharpness, delta));
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(
            new Vector3(-0.05f, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.PelvisRotationSharpness, delta));
        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = _rig.UpperBody.Rotation.Lerp(
            new Vector3(-0.04f, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Torso.Position = _rig.Torso.Position.Lerp(
            new Vector3(0.0f, _spec.TorsoHeight * 0.5f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Torso.Rotation = _rig.Torso.Rotation.Lerp(
            new Vector3(0.1f, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.ChestBand.Position = _rig.ChestBand.Position.Lerp(
            new Vector3(0.0f, _spec.TorsoHeight * 0.62f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Head.Position = _rig.Head.Position.Lerp(
            new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f), 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Head.Rotation = _rig.Head.Rotation.Lerp(
            new Vector3(-cameraPitch * 0.2f, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(
            new Vector3(0.3f, 0.0f, -0.12f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(
            new Vector3(0.3f, 0.0f, 0.12f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        UpdateAirborneLeg(_leftLeg, delta, bodyForward);
        UpdateAirborneLeg(_rightLeg, delta, bodyForward);
    }

    private void UpdateAirborneLeg(HumanoidLegMotionRuntime leg, float delta, Vector3 bodyForward)
    {
        Vector3 hipWorld = _rig.Hips.ToGlobal(leg.HipOffsetFromPelvisLocal);
        Vector3 hangingSupport = hipWorld + (bodyForward * 0.08f) + (Vector3.Down * (_spec.LegLength * HumanoidLocomotionModel.AirLegHangRatio));

        leg.CurrentSupportWorld = leg.CurrentSupportWorld.Lerp(hangingSupport, DampFactor(HumanoidLocomotionModel.StanceFootSharpness, delta));
        leg.PlantedSupportWorld = leg.CurrentSupportWorld;
        leg.SwingStartWorld = leg.CurrentSupportWorld;
        leg.SwingTargetWorld = leg.CurrentSupportWorld;
        leg.GroundNormalWorld = leg.GroundNormalWorld.Slerp(Vector3.Up, DampFactor(HumanoidLocomotionModel.SupportNormalSharpness, delta));
        leg.TargetGroundNormalWorld = leg.GroundNormalWorld;
        leg.IsInStance = false;
        leg.WasInStance = false;
        leg.StanceProgress = 1.0f;
        leg.SwingProgress = 1.0f;
        leg.StanceTimeSeconds = 0.0f;
        leg.HeelStrikeWeight = 0.0f;
        leg.ToeOffWeight = 0.0f;
        leg.RearReachDistance = 0.0f;
        leg.RearReachSaturation = 0.0f;
        leg.RearReleaseArmed = false;
        leg.ComTrailDistance = 0.0f;
        leg.PlannedTouchdownBias = 0.0f;
        leg.BalanceTouchdownBias = 0.0f;
        leg.StanceFootPhase = HumanoidStanceFootPhase.FootFlat;
        leg.ActiveSupportOffsetLocal = leg.Contact.SupportOffsetLocal;
        leg.FootSkateDistance = 0.0f;
        leg.LastStancePivotWorld = Vector3.Zero;
        leg.FootBasisWorld = CreateFootBasis(leg.GroundNormalWorld, bodyForward);
        leg.FootPivotWorld = leg.CurrentSupportWorld;
        leg.HeelContactWorld = leg.CurrentSupportWorld + (leg.FootBasisWorld * (leg.HeelContactLocal - leg.Contact.SupportOffsetLocal));
        leg.ToeContactWorld = leg.CurrentSupportWorld + (leg.FootBasisWorld * (leg.ToeContactLocal - leg.Contact.SupportOffsetLocal));
        leg.DebugSupportTargetWorld = leg.CurrentSupportWorld;

        ApplyLegPose(leg, bodyForward);
        PublishLegMetrics(leg);
    }

    private void PublishLegMetrics(HumanoidLegMotionRuntime leg)
    {
        string prefix = leg == _leftLeg ? "left" : "right";
        _profiler.SetMetric($"{prefix}_contact_y", leg.CurrentSupportWorld.Y);
        _profiler.SetMetric($"{prefix}_stance", leg.IsInStance ? 1.0f : 0.0f);
        _profiler.SetMetric($"{prefix}_swing_t", leg.SwingProgress);
        _profiler.SetMetric($"{prefix}_forward_offset", (leg.CurrentSupportWorld - _rig.Hips.GlobalPosition).Dot(_lastFacingForward));
        _profiler.SetMetric($"{prefix}_heel_strike", leg.HeelStrikeWeight);
        _profiler.SetMetric($"{prefix}_toe_off", leg.ToeOffWeight);
        _profiler.SetMetric($"{prefix}_rear_reach_distance", leg.RearReachDistance);
        _profiler.SetMetric($"{prefix}_rear_reach", leg.RearReachSaturation);
        _profiler.SetMetric($"{prefix}_com_trail", leg.ComTrailDistance);
        _profiler.SetMetric($"{prefix}_touchdown_bias", leg.PlannedTouchdownBias);
        _profiler.SetMetric($"{prefix}_touchdown_bias_balance", leg.BalanceTouchdownBias);
        _profiler.SetMetric($"{prefix}_toe_off_blend", leg.ToeOffWeight);
        _profiler.SetMetric($"{prefix}_foot_skate_distance", leg.FootSkateDistance);
        _profiler.SetMetric($"{prefix}_foot_phase", (float)leg.StanceFootPhase);
        _profiler.SetMetric($"{prefix}_heel_y", leg.HeelContactWorld.Y);
        _profiler.SetMetric($"{prefix}_toe_y", leg.ToeContactWorld.Y);
        _profiler.SetMetric($"rear_reach_saturation_{prefix}", leg.RearReachSaturation);
        _profiler.SetMetric($"touchdown_bias_balance_{prefix}", leg.BalanceTouchdownBias);
    }

    private static float ResolveSupportWeight(HumanoidLegMotionRuntime leg)
    {
        if (!leg.IsInStance)
        {
            return 0.16f;
        }

        return Mathf.Lerp(1.12f, 0.58f, leg.ToeOffWeight);
    }
}
