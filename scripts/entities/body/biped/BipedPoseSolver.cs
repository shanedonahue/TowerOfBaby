using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedPoseSolver
{
    private readonly BipedBodyDefinition _bodyDefinition;
    private readonly BipedPoseRig _rig;

    private Vector3 _pelvisPosition;
    private Vector3 _torsoForward = Vector3.Forward;
    private Vector3 _leftHandPosition;
    private Vector3 _rightHandPosition;
    private bool _initialized;

    public BipedPoseSolver(BipedBodyDefinition bodyDefinition, BipedPoseRig rig)
    {
        _bodyDefinition = bodyDefinition;
        _rig = rig;
        ConfigureStaticMeshes();
    }

    public void Apply(LocomotionFrame frame, AttackPresentationState attackState, float delta)
    {
        Vector3 supportUp = LocomotionMath.SafeNormalized(
            frame.LeftFoot.Normal + frame.RightFoot.Normal + frame.Root.GroundNormal,
            Vector3.Up);
        Vector3 supportMidpoint = (frame.LeftFoot.Position + frame.RightFoot.Position) * 0.5f;
        Vector3 rootAnchor = frame.Root.Position;

        Vector3 pelvisTarget = rootAnchor.Lerp(
            new Vector3(supportMidpoint.X, rootAnchor.Y, supportMidpoint.Z),
            _bodyDefinition.PelvisSupportBias);
        pelvisTarget.Y = supportMidpoint.Y + _bodyDefinition.PelvisHeight;

        float basePelvisHeight = rootAnchor.Y + _bodyDefinition.PelvisHeight;
        pelvisTarget.Y = Mathf.Clamp(
            pelvisTarget.Y,
            basePelvisHeight - _bodyDefinition.MaxPelvisDrop,
            basePelvisHeight + _bodyDefinition.MaxPelvisLift);

        float pelvisBlend = 1.0f - Mathf.Exp(-_bodyDefinition.PelvisFollowSpeed * delta);
        float torsoBlend = 1.0f - Mathf.Exp(-_bodyDefinition.TorsoFollowSpeed * delta);

        if (!_initialized)
        {
            _pelvisPosition = pelvisTarget;
            _torsoForward = frame.Root.FacingDirection;
            _initialized = true;
        }
        else
        {
            _pelvisPosition = _pelvisPosition.Lerp(pelvisTarget, pelvisBlend);
        }

        Vector3 planarVelocity = LocomotionMath.Flatten(frame.Root.ActualVelocity);
        Vector3 torsoForward = frame.Root.FacingDirection;
        if (planarVelocity.LengthSquared() > 0.0001f)
        {
            torsoForward = LocomotionMath.SafeNormalized(
                frame.Root.FacingDirection + (planarVelocity.Normalized() * _bodyDefinition.TorsoLean),
                frame.Root.FacingDirection);
        }

        float attackBlend = attackState.UpperBodyBlend;
        float attackTwist = EvaluateAttackTorsoTwist(attackState, _bodyDefinition);
        if (attackBlend > 0.0f && Mathf.Abs(attackTwist) > 0.0001f)
        {
            Vector3 attackForward = new Basis(supportUp, attackTwist) * torsoForward;
            torsoForward = LocomotionMath.SafeNormalized(torsoForward.Slerp(attackForward, attackBlend), torsoForward);
        }

        _torsoForward = LocomotionMath.SafeNormalized(_torsoForward.Slerp(torsoForward, torsoBlend), frame.Root.FacingDirection);
        Vector3 torsoPosition = _pelvisPosition + (supportUp * (_bodyDefinition.TorsoHeight * 0.5f));

        _rig.Pelvis.GlobalTransform = new Transform3D(
            LocomotionMath.CreateBasisFromForward(frame.Root.FacingDirection, supportUp),
            _pelvisPosition);
        _rig.Torso.GlobalTransform = new Transform3D(
            LocomotionMath.CreateBasisFromForward(_torsoForward, supportUp),
            torsoPosition);

        SolveLeg(frame.LeftFoot, _bodyDefinition.LeftLeg, frame.Root.FacingDirection, supportUp, _rig.LeftUpperLeg, _rig.LeftLowerLeg, _rig.LeftFoot);
        SolveLeg(frame.RightFoot, _bodyDefinition.RightLeg, frame.Root.FacingDirection, supportUp, _rig.RightUpperLeg, _rig.RightLowerLeg, _rig.RightFoot);
        float locomotionSpeed = Mathf.Max(planarVelocity.Length(), LocomotionMath.Flatten(frame.Root.DesiredVelocity).Length());
        float locomotionHandBlend = Mathf.Clamp(
            locomotionSpeed / Mathf.Max(0.01f, _bodyDefinition.LocomotionHandForwardSpeed),
            0.0f,
            1.0f);

        SolveArms(torsoPosition, supportUp, attackState, delta, locomotionHandBlend);
    }

    private void ConfigureStaticMeshes()
    {
        _rig.PelvisMesh.Scale = new Vector3(_bodyDefinition.PelvisWidth, _bodyDefinition.PelvisThickness, _bodyDefinition.PelvisDepth);
        _rig.TorsoMesh.Scale = new Vector3(_bodyDefinition.TorsoWidth, _bodyDefinition.TorsoHeight, _bodyDefinition.TorsoDepth);
        ConfigureHandMesh(_rig.LeftHand, _bodyDefinition.LeftArm);
        ConfigureHandMesh(_rig.RightHand, _bodyDefinition.RightArm);
    }

    private void SolveLeg(
        LocomotionFootPose footPose,
        BipedLegDefinition legDefinition,
        Vector3 bodyForward,
        Vector3 supportUp,
        MeshInstance3D upperLegMesh,
        MeshInstance3D lowerLegMesh,
        MeshInstance3D footMesh)
    {
        Vector3 hipPosition = _pelvisPosition + LocomotionMath.TransformBodyOffset(bodyForward, supportUp, legDefinition.HipOffset);
        Vector3 footPosition = footPose.Position;
        Vector3 toFoot = footPosition - hipPosition;

        float upperLength = legDefinition.UpperLegLength;
        float lowerLength = legDefinition.LowerLegLength;
        float maxReach = Mathf.Max(0.05f, upperLength + lowerLength - 0.02f);
        float distance = Mathf.Clamp(toFoot.Length(), 0.05f, maxReach);
        Vector3 direction = distance > 0.0001f ? toFoot / distance : -supportUp;

        Vector3 right = LocomotionMath.GetRight(bodyForward, supportUp);
        Vector3 preferredBend = (bodyForward * legDefinition.KneeForwardBias) + (right * legDefinition.KneeOutwardBias * legDefinition.Side.Sign());
        preferredBend = LocomotionMath.ProjectOntoPlane(preferredBend, direction);
        preferredBend = LocomotionMath.SafeNormalized(preferredBend, right * legDefinition.Side.Sign());

        float kneeAlong = ((upperLength * upperLength) - (lowerLength * lowerLength) + (distance * distance)) / (2.0f * distance);
        float kneeOffset = Mathf.Sqrt(Mathf.Max(0.0f, (upperLength * upperLength) - (kneeAlong * kneeAlong)));
        Vector3 kneePosition = hipPosition + (direction * kneeAlong) + (preferredBend * kneeOffset);

        SetSegmentTransform(upperLegMesh, hipPosition, kneePosition, legDefinition.UpperLegRadius);
        SetSegmentTransform(lowerLegMesh, kneePosition, footPosition, legDefinition.LowerLegRadius);
        SetFootTransform(footMesh, footPosition, footPose.Normal, bodyForward, legDefinition);
    }

    private static void SetSegmentTransform(MeshInstance3D mesh, Vector3 start, Vector3 end, float radius)
    {
        Vector3 delta = end - start;
        float length = Mathf.Max(delta.Length(), 0.01f);
        Vector3 axisY = delta / length;
        Vector3 axisX = axisY.Cross(Vector3.Forward);
        if (axisX.LengthSquared() < 0.0001f)
        {
            axisX = axisY.Cross(Vector3.Right);
        }
        axisX = axisX.Normalized();
        Vector3 axisZ = axisX.Cross(axisY).Normalized();
        Basis basis = new Basis(axisX * radius, axisY * length, axisZ * radius);
        mesh.GlobalTransform = new Transform3D(basis, (start + end) * 0.5f);
    }

    private static void SetFootTransform(
        MeshInstance3D mesh,
        Vector3 footPosition,
        Vector3 footNormal,
        Vector3 bodyForward,
        BipedLegDefinition legDefinition)
    {
        Vector3 up = LocomotionMath.SafeNormalized(footNormal, Vector3.Up);
        Vector3 forward = LocomotionMath.ProjectOntoPlane(bodyForward, up);
        forward = LocomotionMath.SafeNormalized(forward, Vector3.Forward);
        Vector3 right = LocomotionMath.GetRight(forward, up);
        Basis basis = new Basis(
            right * legDefinition.FootWidth,
            up * legDefinition.FootHeight,
            (-forward) * legDefinition.FootLength);
        mesh.GlobalTransform = new Transform3D(
            basis,
            footPosition + (up * (legDefinition.FootHeight * 0.5f)));
    }

    private void SolveArms(
        Vector3 torsoPosition,
        Vector3 supportUp,
        AttackPresentationState attackState,
        float delta,
        float locomotionHandBlend)
    {
        Vector3 leftTarget = torsoPosition + GetHandTargetOffset(_bodyDefinition.LeftArm, supportUp, attackState, locomotionHandBlend);
        Vector3 rightTarget = torsoPosition + GetHandTargetOffset(_bodyDefinition.RightArm, supportUp, attackState, locomotionHandBlend);

        float handBlend = 1.0f - Mathf.Exp(-Mathf.Max(0.01f, _bodyDefinition.ArmFollowSharpness) * delta);
        if (!_initialized || _leftHandPosition == Vector3.Zero)
        {
            _leftHandPosition = leftTarget;
            _rightHandPosition = rightTarget;
        }
        else
        {
            _leftHandPosition = _leftHandPosition.Lerp(leftTarget, handBlend);
            _rightHandPosition = _rightHandPosition.Lerp(rightTarget, handBlend);
        }

        SolveArm(
            _bodyDefinition.LeftArm,
            torsoPosition,
            _torsoForward,
            supportUp,
            _leftHandPosition,
            _rig.LeftUpperArm,
            _rig.LeftLowerArm,
            _rig.LeftHand);
        SolveArm(
            _bodyDefinition.RightArm,
            torsoPosition,
            _torsoForward,
            supportUp,
            _rightHandPosition,
            _rig.RightUpperArm,
            _rig.RightLowerArm,
            _rig.RightHand);
    }

    private Vector3 GetHandTargetOffset(
        BipedArmDefinition armDefinition,
        Vector3 supportUp,
        AttackPresentationState attackState,
        float locomotionHandBlend)
    {
        Vector3 relaxed = armDefinition.RelaxedHandOffset +
            new Vector3(0.0f, 0.0f, _bodyDefinition.LocomotionHandForwardOffset * locomotionHandBlend);
        if (!attackState.Active)
        {
            return LocomotionMath.TransformBodyOffset(_torsoForward, supportUp, relaxed);
        }

        float phaseT = Mathf.SmoothStep(0.0f, 1.0f, attackState.PhaseProgress);
        bool rightArm = armDefinition.Side == FootSide.Right;

        Vector3 windup = rightArm
            ? new Vector3(0.4f, 0.12f, -0.02f)
            : new Vector3(-0.12f, 0.12f, 0.1f);
        Vector3 release = rightArm
            ? new Vector3(-0.2f, 0.08f, 0.36f)
            : new Vector3(-0.04f, 0.16f, 0.18f);
        Vector3 followThrough = rightArm
            ? new Vector3(-0.14f, -0.06f, 0.24f)
            : new Vector3(-0.08f, 0.02f, 0.12f);

        Vector3 target = attackState.Phase switch
        {
            AttackPhase.Windup => relaxed.Lerp(windup, phaseT),
            AttackPhase.Release => windup.Lerp(release, phaseT),
            AttackPhase.FollowThrough => release.Lerp(followThrough, phaseT),
            AttackPhase.Recovery => followThrough.Lerp(relaxed, phaseT),
            _ => relaxed
        };

        return LocomotionMath.TransformBodyOffset(_torsoForward, supportUp, target);
    }

    private void SolveArm(
        BipedArmDefinition definition,
        Vector3 torsoPosition,
        Vector3 torsoForward,
        Vector3 supportUp,
        Vector3 handTarget,
        MeshInstance3D upperArmMesh,
        MeshInstance3D lowerArmMesh,
        MeshInstance3D handMesh)
    {
        Vector3 shoulderPosition = torsoPosition + LocomotionMath.TransformBodyOffset(torsoForward, supportUp, definition.ShoulderOffset);
        Vector3 toHand = handTarget - shoulderPosition;

        float upperLength = definition.UpperArmLength;
        float lowerLength = definition.LowerArmLength;
        float maxReach = Mathf.Max(0.05f, upperLength + lowerLength - 0.03f);
        float distance = Mathf.Clamp(toHand.Length(), 0.05f, maxReach);
        Vector3 direction = distance > 0.0001f ? toHand / distance : torsoForward;

        Vector3 right = LocomotionMath.GetRight(torsoForward, supportUp);
        Vector3 preferredBend =
            // Neutral arms should trail behind the torso instead of breaking toward the chest.
            (-torsoForward * definition.ElbowForwardBias) +
            (right * definition.ElbowOutwardBias * definition.Side.Sign()) -
            (supportUp * definition.ElbowDownBias);
        preferredBend = LocomotionMath.ProjectOntoPlane(preferredBend, direction);
        preferredBend = LocomotionMath.SafeNormalized(preferredBend, right * definition.Side.Sign());

        float elbowAlong = ((upperLength * upperLength) - (lowerLength * lowerLength) + (distance * distance)) / (2.0f * distance);
        float elbowOffset = Mathf.Sqrt(Mathf.Max(0.0f, (upperLength * upperLength) - (elbowAlong * elbowAlong)));
        Vector3 elbowPosition = shoulderPosition + (direction * elbowAlong) + (preferredBend * elbowOffset);

        SetSegmentTransform(upperArmMesh, shoulderPosition, elbowPosition, definition.UpperArmRadius);
        SetSegmentTransform(lowerArmMesh, elbowPosition, handTarget, definition.LowerArmRadius);
        SetHandTransform(handMesh, handTarget, supportUp, torsoForward, definition);
    }

    private static void ConfigureHandMesh(MeshInstance3D handMesh, BipedArmDefinition definition)
    {
        handMesh.Scale = new Vector3(definition.HandWidth, definition.HandThickness, definition.HandLength);
    }

    private static void SetHandTransform(
        MeshInstance3D mesh,
        Vector3 handPosition,
        Vector3 supportUp,
        Vector3 torsoForward,
        BipedArmDefinition definition)
    {
        Vector3 up = LocomotionMath.SafeNormalized(supportUp, Vector3.Up);
        Vector3 forward = LocomotionMath.SafeNormalized(LocomotionMath.ProjectOntoPlane(torsoForward, up), Vector3.Forward);
        Vector3 right = LocomotionMath.GetRight(forward, up);
        Basis basis = new Basis(
            right * definition.HandWidth,
            up * definition.HandThickness,
            (-forward) * definition.HandLength);
        mesh.GlobalTransform = new Transform3D(
            basis,
            handPosition + (up * (definition.HandThickness * 0.5f)));
    }

    private static float EvaluateAttackTorsoTwist(AttackPresentationState attackState, BipedBodyDefinition bodyDefinition)
    {
        float eased = Mathf.SmoothStep(0.0f, 1.0f, attackState.PhaseProgress);
        float degrees = attackState.Phase switch
        {
            AttackPhase.Windup => Mathf.Lerp(0.0f, bodyDefinition.AttackWindupTorsoTwistDegrees, eased),
            AttackPhase.Release => Mathf.Lerp(bodyDefinition.AttackWindupTorsoTwistDegrees, bodyDefinition.AttackReleaseTorsoTwistDegrees, eased),
            AttackPhase.FollowThrough => Mathf.Lerp(bodyDefinition.AttackReleaseTorsoTwistDegrees, bodyDefinition.AttackFollowThroughTorsoTwistDegrees, eased),
            AttackPhase.Recovery => Mathf.Lerp(bodyDefinition.AttackFollowThroughTorsoTwistDegrees, 0.0f, eased),
            _ => 0.0f
        };
        return Mathf.DegToRad(degrees);
    }
}

public sealed class BipedPoseRig
{
    public required Node3D Pelvis { get; init; }
    public required MeshInstance3D PelvisMesh { get; init; }
    public required Node3D Torso { get; init; }
    public required MeshInstance3D TorsoMesh { get; init; }
    public required MeshInstance3D LeftUpperLeg { get; init; }
    public required MeshInstance3D LeftLowerLeg { get; init; }
    public required MeshInstance3D LeftFoot { get; init; }
    public required MeshInstance3D RightUpperLeg { get; init; }
    public required MeshInstance3D RightLowerLeg { get; init; }
    public required MeshInstance3D RightFoot { get; init; }
    public required MeshInstance3D LeftUpperArm { get; init; }
    public required MeshInstance3D LeftLowerArm { get; init; }
    public required MeshInstance3D LeftHand { get; init; }
    public required MeshInstance3D RightUpperArm { get; init; }
    public required MeshInstance3D RightLowerArm { get; init; }
    public required MeshInstance3D RightHand { get; init; }
}
