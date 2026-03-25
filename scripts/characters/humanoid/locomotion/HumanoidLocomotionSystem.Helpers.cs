using Godot;
using Godot.Collections;
using TowerOfBaby.Characters.Humanoid.Definition;
using TowerOfBaby.Characters.Humanoid.Rig;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    private Vector3 SampleSupportPoint(Vector3 preferredSupportPoint, float groundClearance, out Vector3 normal)
    {
        float probeHeight = Mathf.Max(_spec.HipHeight + _spec.FootHeight, 2.4f);
        float probeDistance = Mathf.Max(_settings.FootProbeDistance, probeHeight + _spec.LegLength);
        Vector3 origin = preferredSupportPoint + (Vector3.Up * probeHeight);
        Vector3 target = preferredSupportPoint + (Vector3.Down * probeDistance);

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;
        query.Exclude = new Array<Rid> { _body.GetRid() };

        Dictionary result = _body.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count > 0)
        {
            normal = ((Vector3)result["normal"]).Normalized();
            return ((Vector3)result["position"]) + (normal * groundClearance);
        }

        normal = Vector3.Up;
        return preferredSupportPoint;
    }

    private static void SolveLeg(HumanoidLegRig leg, Vector3 hip, Vector3 footTarget, Basis footBasis, Vector3 bendPlaneNormal)
    {
        Vector3 foot = ClampFootToBoneReach(leg, hip, footTarget);
        Vector3 targetVector = foot - hip;
        float distance = targetVector.Length();
        Vector3 direction = distance > 0.001f ? targetVector / distance : Vector3.Down;

        float maxDistance = Mathf.Max(0.05f, leg.UpperLength + leg.LowerLength - 0.02f);
        float clampedDistance = Mathf.Clamp(distance, 0.05f, maxDistance);

        Vector3 planeNormal = bendPlaneNormal.Normalized();
        if (planeNormal.LengthSquared() < 0.0001f || Mathf.Abs(planeNormal.Dot(direction)) > 0.98f)
        {
            planeNormal = direction.Cross(Vector3.Right);
        }

        if (planeNormal.LengthSquared() < 0.0001f)
        {
            planeNormal = direction.Cross(Vector3.Forward);
        }

        planeNormal = planeNormal.Normalized();
        Vector3 bendDirection = planeNormal.Cross(direction).Normalized();

        float upperAngle = Mathf.Acos(Mathf.Clamp(
            ((leg.UpperLength * leg.UpperLength) + (clampedDistance * clampedDistance) - (leg.LowerLength * leg.LowerLength)) /
            (2.0f * leg.UpperLength * clampedDistance),
            -1.0f,
            1.0f));

        float along = Mathf.Cos(upperAngle) * leg.UpperLength;
        float bendHeight = Mathf.Sin(upperAngle) * leg.UpperLength;
        Vector3 knee = hip + (direction * along) + (bendDirection * bendHeight);

        leg.Upper.GlobalPosition = hip;
        leg.Upper.GlobalBasis = CreateBoneBasis(knee - hip, bendDirection);

        leg.Lower.GlobalPosition = knee;
        leg.Lower.GlobalBasis = CreateBoneBasis(foot - knee, bendDirection);

        leg.Foot.GlobalPosition = foot;
        leg.Foot.GlobalBasis = footBasis;
    }

    private static Vector3 ClampFootToBoneReach(HumanoidLegRig leg, Vector3 hip, Vector3 foot)
    {
        float maxReach = Mathf.Max(0.05f, leg.UpperLength + leg.LowerLength - 0.02f);
        Vector3 offset = foot - hip;
        float distance = offset.Length();
        if (distance <= maxReach || distance < 0.001f)
        {
            return foot;
        }

        return hip + (offset / distance) * maxReach;
    }

    private static Basis CreateBoneBasis(Vector3 boneDirection, Vector3 bendDirection)
    {
        Vector3 y = -boneDirection.Normalized();
        Vector3 z = bendDirection.Cross(y).Normalized();
        Vector3 x = y.Cross(z).Normalized();
        return new Basis(x, y, z);
    }

    private static Basis CreateFootBasis(Vector3 upNormal, Vector3 forwardHint)
    {
        Vector3 y = upNormal.Normalized();
        Vector3 z = (-forwardHint).Slide(y).Normalized();
        if (z.LengthSquared() < 0.0001f)
        {
            z = Vector3.Forward;
        }

        Vector3 x = y.Cross(z).Normalized();
        z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
    }

    private HumanoidLegMotionRuntime CreateLegRuntime(HumanoidLegRig rig, string chainId, string contactId, float phaseOffset)
    {
        MotionChainDefinition chain = _motionDefinition.GetChain(chainId);
        MotionContactDefinition contact = _motionDefinition.GetContact(contactId);
        Vector3 pelvisRest = _motionDefinition.GetJointRestPosition("pelvis");
        Vector3 hipOffsetLocal = _motionDefinition.Joints[chain.RootJointIndex].LocalRestPosition - pelvisRest;
        Vector3 restSupportLocal = _motionDefinition.Joints[contact.JointIndex].LocalRestPosition + contact.SupportOffsetLocal;
        float contactInset = _spec.FootLength * HumanoidLocomotionModel.FootContactInsetRatio;
        float heelToeOffset = (_spec.FootLength * 0.5f) - contactInset;
        Vector3 heelContactLocal = contact.SupportOffsetLocal + new Vector3(0.0f, 0.0f, heelToeOffset);
        Vector3 toeContactLocal = contact.SupportOffsetLocal + new Vector3(0.0f, 0.0f, -heelToeOffset);

        return new HumanoidLegMotionRuntime(
            rig,
            chain,
            contact,
            hipOffsetLocal,
            restSupportLocal,
            heelContactLocal,
            toeContactLocal,
            phaseOffset);
    }

    private Vector3 ResolveBodyForward(Vector3 desiredDirection, Vector3 velocityPlanar)
    {
        if (desiredDirection.LengthSquared() > 0.0001f)
        {
            return desiredDirection.Normalized();
        }

        if (velocityPlanar.LengthSquared() > 0.01f)
        {
            return velocityPlanar.Normalized();
        }

        return _lastFacingForward;
    }

    private Vector3 ResolveLocomotionDirection(Vector3 desiredDirection, float moveAmount, float delta)
    {
        if (moveAmount > 0.05f && desiredDirection.LengthSquared() > 0.0001f)
        {
            Vector3 targetForward = desiredDirection.Normalized();
            float headingSharpness = Mathf.Lerp(_settings.RotationSpeed * 1.5f, _settings.TurnResponsiveness, moveAmount);
            _locomotionForward = _locomotionForward.Slerp(targetForward, DampFactor(headingSharpness, delta)).Normalized();
        }
        else
        {
            Vector3 velocityPlanar = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
            _locomotionForward = velocityPlanar.LengthSquared() > 0.01f
                ? velocityPlanar.Normalized()
                : _lastFacingForward;
        }

        return _locomotionForward;
    }

    private static MotionProfilerSnapshot CreateEmptySnapshot()
    {
        return new MotionProfilerSnapshot
        {
            TotalFrameMs = 0.0,
            StageMs = new System.Collections.Generic.Dictionary<string, double>(),
            Metrics = new System.Collections.Generic.Dictionary<string, float>()
        };
    }

    private static float DampFactor(float sharpness, float delta)
    {
        return 1.0f - Mathf.Exp(-sharpness * delta);
    }
}
