using Godot;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    // Pose application stays separate from contact planning so new species can reuse the contact model with different pose solvers.
    private void UpdatePelvisPose(HumanoidGroundMotionFrame frame, float delta)
    {
        float leftWeight = _leftLeg.IsInStance ? 1.0f : 0.2f;
        float rightWeight = _rightLeg.IsInStance ? 1.0f : 0.2f;
        float totalWeight = leftWeight + rightWeight;

        Vector3 supportCenter = ((_leftLeg.CurrentSupportWorld * leftWeight) + (_rightLeg.CurrentSupportWorld * rightWeight)) / totalWeight;
        Vector3 nominalLeftSupport = ResolveRootRelativeSupportWorld(_leftLeg.RestSupportPointLocal, frame);
        Vector3 nominalRightSupport = ResolveRootRelativeSupportWorld(_rightLeg.RestSupportPointLocal, frame);
        Vector3 nominalSupportCenter = (nominalLeftSupport + nominalRightSupport) * 0.5f;
        Vector3 nominalPelvisWorld = frame.VisualOrigin + (frame.VisualBasis * _motionDefinition.GetJointRestPosition("pelvis"));
        float supportDelta = rightWeight - leftWeight;
        float footHeightDelta = _leftLeg.CurrentSupportWorld.Y - _rightLeg.CurrentSupportWorld.Y;
        Vector3 supportError = supportCenter - nominalSupportCenter;

        float supportForwardCorrection = Mathf.Clamp(
            supportError.Dot(frame.Forward) * HumanoidLocomotionModel.PelvisSupportFollowRatio,
            -_spec.LegLength * HumanoidLocomotionModel.PelvisMaxForwardCorrectionRatio,
            _spec.LegLength * HumanoidLocomotionModel.PelvisMaxForwardCorrectionRatio);
        float supportLateralCorrection = Mathf.Clamp(
            supportError.Dot(frame.Right) * HumanoidLocomotionModel.PelvisSupportFollowRatio,
            -_spec.LegLength * HumanoidLocomotionModel.PelvisMaxLateralCorrectionRatio,
            _spec.LegLength * HumanoidLocomotionModel.PelvisMaxLateralCorrectionRatio);
        float supportVerticalCorrection = Mathf.Clamp(
            supportError.Y * HumanoidLocomotionModel.PelvisSupportFollowRatio,
            -_spec.LegLength * HumanoidLocomotionModel.PelvisMaxVerticalCorrectionRatio,
            _spec.LegLength * HumanoidLocomotionModel.PelvisMaxVerticalCorrectionRatio);

        Vector3 desiredPelvisWorld =
            nominalPelvisWorld +
            (frame.Forward * supportForwardCorrection) +
            (frame.Right * supportLateralCorrection) +
            (Vector3.Up * (supportVerticalCorrection - (_spec.LegLength * HumanoidLocomotionModel.PelvisSpeedCompressionRatio * frame.SpeedRatio))) +
            (frame.Right * (supportDelta * _spec.HipWidth * HumanoidLocomotionModel.PelvisSupportShiftRatio)) +
            (frame.Forward * (_spec.LegLength * HumanoidLocomotionModel.PelvisForwardBiasRatio * frame.DesiredForwardInfluence));

        Vector3 desiredPelvisLocal = _rig.VisualRoot.ToLocal(desiredPelvisWorld);
        _rig.Hips.Position = _rig.Hips.Position.Lerp(
            desiredPelvisLocal,
            DampFactor(HumanoidLocomotionModel.PelvisPositionSharpness, delta));

        Vector3 desiredRotation = new(
            -frame.ForwardAcceleration * HumanoidLocomotionModel.PelvisPitchFromAccel,
            0.0f,
            Mathf.Clamp(
                (footHeightDelta * HumanoidLocomotionModel.PelvisRollFromHeight) - (supportDelta * HumanoidLocomotionModel.PelvisRollFromSupport),
                -0.22f,
                0.22f));
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(
            desiredRotation,
            DampFactor(HumanoidLocomotionModel.PelvisRotationSharpness, delta));

        _profiler.SetMetric("pelvis_y", _rig.Hips.GlobalPosition.Y);
        _profiler.SetMetric("pelvis_local_z", _rig.Hips.Position.Z);
        _profiler.SetMetric("pelvis_support_error_z", supportError.Dot(frame.Forward));
        _profiler.SetMetric("support_width", _leftLeg.CurrentSupportWorld.DistanceTo(_rightLeg.CurrentSupportWorld));
    }

    private void ApplyLegPose(HumanoidLegMotionRuntime leg, Vector3 preferredForward)
    {
        Basis footBasis = CreateFootBasis(leg.GroundNormalWorld, preferredForward);
        Vector3 footJointTarget = leg.CurrentSupportWorld - (footBasis * leg.Contact.SupportOffsetLocal);
        Vector3 hipWorld = _rig.Hips.ToGlobal(leg.HipOffsetFromPelvisLocal);
        Vector3 bendPlaneNormalWorld = (_rig.VisualRoot.GlobalBasis * leg.Chain.PreferredBendNormalLocal).Normalized();

        Vector3 planarRetraction = hipWorld - footJointTarget;
        planarRetraction.Y = 0.0f;

        if (leg.IsInStance)
        {
            float releaseWeight = Mathf.SmoothStep(
                0.0f,
                1.0f,
                Mathf.InverseLerp(HumanoidLocomotionModel.LateStanceReleaseStart, 1.0f, leg.StanceProgress));
            footJointTarget += planarRetraction * (HumanoidLocomotionModel.LateStanceRetractionRatio * releaseWeight);
            footJointTarget += Vector3.Up * (_spec.LegLength * HumanoidLocomotionModel.LateStanceToeLiftRatio * releaseWeight);
        }
        else
        {
            float swingRetractionWeight = Mathf.Sin(leg.SwingProgress * Mathf.Pi);
            swingRetractionWeight *= swingRetractionWeight;
            footJointTarget += planarRetraction * (HumanoidLocomotionModel.SwingRetractionRatio * swingRetractionWeight);
            footJointTarget += Vector3.Up * (_spec.LegLength * HumanoidLocomotionModel.SwingExtraLiftRatio * swingRetractionWeight);
        }

        SolveLeg(leg.Rig, hipWorld, footJointTarget, footBasis, bendPlaneNormalWorld);

        leg.Rig.IsStepping = !leg.IsInStance;
        leg.Rig.StepProgress = leg.SwingProgress;
        leg.Rig.CurrentFootPosition = footJointTarget;
        leg.Rig.PlantedFootPosition = leg.PlantedSupportWorld - (footBasis * leg.Contact.SupportOffsetLocal);
        leg.Rig.TargetFootPosition = leg.SwingTargetWorld - (footBasis * leg.Contact.SupportOffsetLocal);
        leg.Rig.GroundNormal = leg.GroundNormalWorld;
        leg.Rig.TargetNormal = leg.TargetGroundNormalWorld;
    }

    private void UpdateUpperBodyPose(HumanoidGroundMotionFrame frame, float delta, float cameraPitch)
    {
        float gaitSwing = Mathf.Sin(_gaitPhase * Mathf.Tau);
        float torsoBob = Mathf.Sin((_gaitPhase * Mathf.Tau * 2.0f) + 0.2f) * _spec.TorsoHeight * 0.012f * _locomotionBlend;
        float forwardLean =
            (frame.DesiredForwardInfluence * Mathf.Lerp(HumanoidLocomotionModel.TorsoLeanWalk, HumanoidLocomotionModel.TorsoLeanRun, frame.RunBlend)) +
            (Mathf.Max(0.0f, frame.ForwardAcceleration) * 0.08f);
        float torsoYaw = (gaitSwing * 0.05f * _locomotionBlend) + (frame.LateralAcceleration * 0.05f);
        float torsoRoll = (-_rig.Hips.Rotation.Z * 0.45f) - (frame.LateralAcceleration * 0.03f);

        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = _rig.UpperBody.Rotation.Lerp(
            new Vector3(-forwardLean * 0.2f, torsoYaw, torsoRoll),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        _rig.Torso.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.5f) + torsoBob, 0.0f);
        _rig.Torso.Rotation = _rig.Torso.Rotation.Lerp(
            new Vector3(-forwardLean, 0.0f, 0.0f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        _rig.ChestBand.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.62f) + (torsoBob * 0.8f), 0.0f);
        _rig.Head.Position = new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f) + (torsoBob * 0.4f), 0.0f);
        _rig.Head.Rotation = _rig.Head.Rotation.Lerp(
            new Vector3((-cameraPitch * 0.28f) + (forwardLean * 0.1f), -torsoYaw * 0.2f, -_rig.Hips.Rotation.Z * 0.18f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

        float armSwing = gaitSwing * Mathf.Lerp(HumanoidLocomotionModel.ArmSwingWalk, HumanoidLocomotionModel.ArmSwingRun, frame.RunBlend) * _locomotionBlend;
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(
            new Vector3(-armSwing, -0.05f - (torsoYaw * 0.25f), -0.2f),
            DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(
            new Vector3(armSwing, 0.05f - (torsoYaw * 0.25f), 0.2f),
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
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(new Vector3(-0.08f, 0.0f, 0.0f), DampFactor(HumanoidLocomotionModel.PelvisRotationSharpness, delta));
        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = _rig.UpperBody.Rotation.Lerp(new Vector3(-0.05f, 0.0f, 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Torso.Position = _rig.Torso.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.5f, 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Torso.Rotation = _rig.Torso.Rotation.Lerp(new Vector3(0.12f, 0.0f, 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.ChestBand.Position = _rig.ChestBand.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.62f, 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Head.Position = _rig.Head.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f), 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.Head.Rotation = _rig.Head.Rotation.Lerp(new Vector3(-cameraPitch * 0.2f, 0.0f, 0.0f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(new Vector3(0.34f, 0.0f, -0.12f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(new Vector3(0.34f, 0.0f, 0.12f), DampFactor(HumanoidLocomotionModel.UpperBodySharpness, delta));

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
    }
}
