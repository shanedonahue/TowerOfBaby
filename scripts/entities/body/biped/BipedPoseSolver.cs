using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedPoseSolver
{
    private readonly BipedBodyDefinition _bodyDefinition;
    private readonly BipedPoseRig _rig;

    private Vector3 _pelvisPosition;
    private Vector3 _torsoForward = Vector3.Forward;
    private bool _initialized;

    public BipedPoseSolver(BipedBodyDefinition bodyDefinition, BipedPoseRig rig)
    {
        _bodyDefinition = bodyDefinition;
        _rig = rig;
        ConfigureStaticMeshes();
    }

    public void Apply(LocomotionFrame frame, float delta)
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
    }

    private void ConfigureStaticMeshes()
    {
        _rig.PelvisMesh.Scale = new Vector3(_bodyDefinition.PelvisWidth, _bodyDefinition.PelvisThickness, _bodyDefinition.PelvisDepth);
        _rig.TorsoMesh.Scale = new Vector3(_bodyDefinition.TorsoWidth, _bodyDefinition.TorsoHeight, _bodyDefinition.TorsoDepth);
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
}
