using Godot;
using Godot.Collections;

public sealed class HumanoidLocomotionController
{
    private readonly CharacterBody3D _body;
    private readonly HumanoidRig _rig;
    private readonly HumanoidBodySpec _spec;
    private readonly HumanoidLocomotionSettings _settings;

    private float _gaitTime;
    private Vector3 _lastFacingForward = Vector3.Forward;

    public HumanoidLocomotionController(
        CharacterBody3D body,
        HumanoidRig rig,
        HumanoidBodySpec spec,
        HumanoidLocomotionSettings settings)
    {
        _body = body;
        _rig = rig;
        _spec = spec;
        _settings = settings;
    }

    public void Update(float delta, MovementIntent intent, Vector3 desiredDirection, float cameraPitch)
    {
        Vector3 horizontalVelocity = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        float targetSpeed = _settings.MoveSpeed * Mathf.Clamp(intent.Move.Length(), 0.0f, 1.0f);
        Vector3 targetVelocity = desiredDirection * targetSpeed;
        float appliedAcceleration = _body.IsOnFloor() ? _settings.Acceleration : _settings.AirAcceleration;
        horizontalVelocity = horizontalVelocity.Lerp(targetVelocity, 1.0f - Mathf.Exp(-appliedAcceleration * delta));

        float gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity") * _settings.GravityScale;
        float verticalVelocity = _body.Velocity.Y;
        if (!_body.IsOnFloor())
        {
            verticalVelocity -= gravity * delta;
        }
        else if (verticalVelocity < 0.0f)
        {
            verticalVelocity = -0.1f;
        }

        _body.Velocity = new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
        _body.MoveAndSlide();

        Vector3 facingVelocity = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        if (facingVelocity.LengthSquared() > 0.04f)
        {
            _lastFacingForward = facingVelocity.Normalized();
            float targetYaw = Mathf.Atan2(-facingVelocity.X, -facingVelocity.Z);
            _rig.VisualRoot.Rotation = new Vector3(
                0.0f,
                Mathf.LerpAngle(_rig.VisualRoot.Rotation.Y, targetYaw, 1.0f - Mathf.Exp(-_settings.RotationSpeed * delta)),
                0.0f);
        }

        UpdateVisualRig(delta, desiredDirection, cameraPitch);
    }

    private void UpdateVisualRig(float delta, Vector3 desiredDirection, float cameraPitch)
    {
        Vector3 velocityPlanar = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        float speed = velocityPlanar.Length();
        float speedRatio = Mathf.Clamp(speed / _settings.MoveSpeed, 0.0f, 1.0f);
        Vector3 bodyForward = speed > 0.08f ? velocityPlanar.Normalized() : _lastFacingForward;
        Vector3 bodyRight = bodyForward.Cross(Vector3.Up).Normalized();
        if (bodyRight.LengthSquared() < 0.0001f)
        {
            bodyRight = Vector3.Right;
        }

        if (!_body.IsOnFloor())
        {
            UpdateAirborneRig(delta, desiredDirection, bodyForward, bodyRight, cameraPitch);
            return;
        }

        float legLength = _spec.LegLength;
        float derivedStrideLength = GetDerivedStrideLength(legLength, speedRatio);
        float derivedStepHeight = GetDerivedStepHeight(legLength, speedRatio);
        float cycleSpeed = GetDerivedCycleSpeed(speed, derivedStrideLength);
        _gaitTime = Mathf.PosMod(_gaitTime + (delta * cycleSpeed), Mathf.Tau);

        UpdateLeg(_rig.LeftLeg, _rig.RightLeg, delta, speed, speedRatio, legLength, derivedStrideLength, derivedStepHeight, bodyForward, bodyRight);
        UpdateLeg(_rig.RightLeg, _rig.LeftLeg, delta, speed, speedRatio, legLength, derivedStrideLength, derivedStepHeight, bodyForward, bodyRight);

        float supportHeight = (_rig.LeftLeg.CurrentFootPosition.Y + _rig.RightLeg.CurrentFootPosition.Y) * 0.5f;
        float desiredHipWorldY = supportHeight + GetDerivedHipHeight(legLength) + Mathf.Lerp(0.0f, legLength * 0.03f, speedRatio);
        float localHipY = desiredHipWorldY - _body.GlobalPosition.Y;
        _rig.Hips.Position = new Vector3(
            0.0f,
            Mathf.Lerp(_rig.Hips.Position.Y, localHipY, 1.0f - Mathf.Exp(-10.0f * delta)),
            0.0f);

        float pelvisRoll = Mathf.Clamp((_rig.LeftLeg.CurrentFootPosition.Y - _rig.RightLeg.CurrentFootPosition.Y) * 0.5f, -0.18f, 0.18f);
        _rig.Hips.Rotation = new Vector3(0.0f, 0.0f, pelvisRoll);

        float torsoBob = Mathf.Sin(_gaitTime * 2.0f) * (_spec.TorsoHeight * 0.033f) * speedRatio;
        float forwardLean = desiredDirection.Dot(bodyForward) * 0.12f;
        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = new Vector3(
            speedRatio * 0.03f,
            Mathf.Sin(_gaitTime) * -0.06f * speedRatio,
            -pelvisRoll * 0.35f);

        _rig.Torso.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.5f) + torsoBob, 0.0f);
        _rig.Torso.Rotation = new Vector3(-forwardLean, 0.0f, 0.0f);

        _rig.ChestBand.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.62f) + (torsoBob * 0.7f), 0.0f);
        _rig.Head.Position = new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f) + (torsoBob * 0.5f), 0.0f);
        _rig.Head.Rotation = new Vector3(-cameraPitch * 0.3f, Mathf.Sin(_gaitTime) * 0.04f * speedRatio, -pelvisRoll * 0.3f);

        float armSwing = Mathf.Sin(_gaitTime) * Mathf.Lerp(0.1f, 0.9f, speedRatio);
        _rig.LeftArm.Rotation = new Vector3(-armSwing * 0.75f, 0.0f, -0.18f);
        _rig.RightArm.Rotation = new Vector3(armSwing * 0.75f, 0.0f, 0.18f);
    }

    private void UpdateAirborneRig(float delta, Vector3 desiredDirection, Vector3 bodyForward, Vector3 bodyRight, float cameraPitch)
    {
        _rig.Hips.Position = _rig.Hips.Position.Lerp(new Vector3(0.0f, _spec.HipHeight, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(Vector3.Zero, 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = new Vector3(-0.08f, 0.0f, 0.0f);
        _rig.Torso.Position = _rig.Torso.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.5f, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.Torso.Rotation = new Vector3(0.08f, 0.0f, 0.0f);
        _rig.ChestBand.Position = _rig.ChestBand.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.62f, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.Head.Position = _rig.Head.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f), 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.Head.Rotation = new Vector3(-cameraPitch * 0.2f, 0.0f, 0.0f);
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(new Vector3(0.3f, 0.0f, -0.12f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(new Vector3(0.3f, 0.0f, 0.12f), 1.0f - Mathf.Exp(-8.0f * delta));

        UpdateAirborneLeg(_rig.LeftLeg, delta, bodyForward, bodyRight);
        UpdateAirborneLeg(_rig.RightLeg, delta, bodyForward, bodyRight);
    }

    private void UpdateAirborneLeg(HumanoidLegRig leg, float delta, Vector3 bodyForward, Vector3 bodyRight)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (bodyRight * leg.SideOffset);
        Vector3 hangingTarget = hipWorld + (bodyForward * 0.08f) + (Vector3.Down * (leg.UpperLength + leg.LowerLength - (_spec.FootHeight * 1.5f)));

        leg.CurrentFootPosition = leg.CurrentFootPosition.Lerp(hangingTarget, 1.0f - Mathf.Exp(-10.0f * delta));
        leg.PlantedFootPosition = leg.CurrentFootPosition;
        leg.TargetFootPosition = leg.CurrentFootPosition;
        leg.StepStartPosition = leg.CurrentFootPosition;
        leg.StepProgress = 1.0f;
        leg.IsStepping = false;
        leg.GroundNormal = leg.GroundNormal.Slerp(Vector3.Up, 1.0f - Mathf.Exp(-8.0f * delta));
        leg.TargetNormal = leg.GroundNormal;

        SolveLeg(leg, hipWorld, leg.CurrentFootPosition, leg.GroundNormal, bodyForward);
    }

    private void UpdateLeg(
        HumanoidLegRig leg,
        HumanoidLegRig otherLeg,
        float delta,
        float speed,
        float speedRatio,
        float legLength,
        float strideLength,
        float stepHeight,
        Vector3 bodyForward,
        Vector3 bodyRight)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (bodyRight * leg.SideOffset);

        Vector3 stepCenter = _body.GlobalPosition + _rig.VisualRoot.Position + (bodyRight * leg.SideOffset);
        stepCenter.Y = _body.GlobalPosition.Y + _spec.FootHeight;
        float trailingBias = Mathf.Lerp(legLength * 0.0f, legLength * 0.04f, speedRatio);
        float forwardBias = Mathf.Lerp(legLength * 0.08f, legLength * 0.22f, speedRatio);
        float strideDistance = strideLength * Mathf.Lerp(0.22f, 1.0f, speedRatio);
        Vector3 landingProbe = stepCenter + (bodyForward * (forwardBias + strideDistance));
        Vector3 stanceProbe = stepCenter - (bodyForward * trailingBias);

        Vector3 landingPosition = SampleGroundPoint(landingProbe, out Vector3 landingNormal);
        Vector3 stancePosition = SampleGroundPoint(stanceProbe, out Vector3 stanceNormal);
        landingPosition = ClampFootTargetToReach(hipWorld, landingPosition, legLength * 0.88f, legLength * 0.34f);
        stancePosition = ClampFootTargetToReach(hipWorld, stancePosition, legLength * 0.82f, legLength * 0.18f);

        float supportRadius = Mathf.Lerp(legLength * 0.16f, legLength * 0.38f, speedRatio);
        float stepTriggerRadius = Mathf.Lerp(legLength * 0.22f, legLength * 0.34f, speedRatio);
        float currentReach = HorizontalDistance(hipWorld, leg.CurrentFootPosition);

        if (!leg.Initialized)
        {
            leg.Initialized = true;
            leg.GroundNormal = stanceNormal;
            leg.CurrentFootPosition = stancePosition;
            leg.PlantedFootPosition = stancePosition;
            leg.TargetFootPosition = stancePosition;
            leg.StepStartPosition = stancePosition;
            leg.StepProgress = 1.0f;
            leg.IsStepping = false;
        }

        bool shouldStep =
            !leg.IsStepping &&
            !otherLeg.IsStepping &&
            speedRatio > 0.03f &&
            currentReach > stepTriggerRadius;

        if (shouldStep)
        {
            leg.StepStartPosition = leg.CurrentFootPosition;
            leg.TargetFootPosition = landingPosition;
            leg.TargetNormal = landingNormal;
            leg.StepProgress = 0.0f;
            leg.IsStepping = true;
        }

        if (leg.IsStepping)
        {
            float stepDistance = Mathf.Max(leg.StepStartPosition.DistanceTo(leg.TargetFootPosition), legLength * 0.08f);
            float swingSpeed = Mathf.Max(speed * 1.45f, legLength * 1.85f);
            leg.StepProgress = Mathf.Min(1.0f, leg.StepProgress + ((swingSpeed * delta) / stepDistance));

            float swingT = leg.StepProgress;
            Vector3 foot = leg.StepStartPosition.Lerp(leg.TargetFootPosition, swingT);
            foot.Y += Mathf.Sin(swingT * Mathf.Pi) * stepHeight;
            leg.CurrentFootPosition = foot;
            leg.GroundNormal = leg.GroundNormal.Slerp(leg.TargetNormal, 1.0f - Mathf.Exp(-8.0f * delta));

            if (leg.StepProgress >= 1.0f)
            {
                leg.IsStepping = false;
                leg.PlantedFootPosition = leg.TargetFootPosition;
                leg.CurrentFootPosition = leg.TargetFootPosition;
                leg.GroundNormal = leg.TargetNormal;
            }
        }
        else
        {
            Vector3 planted = leg.PlantedFootPosition.Lerp(stancePosition, 1.0f - Mathf.Exp(-14.0f * delta));
            if (HorizontalDistance(hipWorld, planted) > supportRadius)
            {
                planted = ClampFootTargetToReach(hipWorld, planted, supportRadius, legLength * 0.16f);
            }

            leg.PlantedFootPosition = planted;
            leg.CurrentFootPosition = leg.CurrentFootPosition.Lerp(planted, 1.0f - Mathf.Exp(-20.0f * delta));
            leg.GroundNormal = leg.GroundNormal.Slerp(stanceNormal, 1.0f - Mathf.Exp(-10.0f * delta));
        }

        SolveLeg(leg, hipWorld, leg.CurrentFootPosition, leg.GroundNormal, bodyForward);
    }

    private float GetDerivedHipHeight(float legLength)
    {
        return legLength * 0.53f;
    }

    private float GetDerivedStrideLength(float legLength, float speedRatio)
    {
        return Mathf.Lerp(legLength * 0.18f, legLength * 0.42f, speedRatio);
    }

    private float GetDerivedStepHeight(float legLength, float speedRatio)
    {
        return Mathf.Lerp(legLength * 0.08f, legLength * 0.18f, speedRatio);
    }

    private float GetDerivedCycleSpeed(float speed, float strideLength)
    {
        float baseCycle = speed / Mathf.Max(strideLength, 0.01f);
        return Mathf.Lerp(0.0f, Mathf.Clamp(baseCycle, _settings.WalkCycleSpeed, _settings.RunCycleSpeed), Mathf.Clamp(speed / _settings.MoveSpeed, 0.0f, 1.0f));
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector2 av = new(a.X, a.Z);
        Vector2 bv = new(b.X, b.Z);
        return av.DistanceTo(bv);
    }

    private static Vector3 ClampFootTargetToReach(Vector3 hipWorld, Vector3 target, float maxReach, float minReach)
    {
        Vector2 hip = new(hipWorld.X, hipWorld.Z);
        Vector2 foot = new(target.X, target.Z);
        Vector2 offset = foot - hip;
        float length = offset.Length();

        if (length < 0.0001f)
        {
            offset = Vector2.Up * minReach;
            length = minReach;
        }

        float clampedLength = Mathf.Clamp(length, minReach, maxReach);
        Vector2 clamped = hip + ((offset / length) * clampedLength);
        return new Vector3(clamped.X, target.Y, clamped.Y);
    }

    private Vector3 SampleGroundPoint(Vector3 center, out Vector3 normal)
    {
        Vector3 origin = center + (Vector3.Up * 2.4f);
        Vector3 target = origin + (Vector3.Down * _settings.FootProbeDistance);

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;
        query.Exclude = new Array<Rid> { _body.GetRid() };

        Dictionary result = _body.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count > 0)
        {
            normal = ((Vector3)result["normal"]).Normalized();
            return ((Vector3)result["position"]) + (normal * 0.04f);
        }

        normal = Vector3.Up;
        return center;
    }

    private static void SolveLeg(HumanoidLegRig leg, Vector3 hip, Vector3 foot, Vector3 groundNormal, Vector3 preferredForward)
    {
        Vector3 targetVector = foot - hip;
        float distance = targetVector.Length();
        Vector3 direction = distance > 0.001f ? targetVector / distance : Vector3.Down;

        float maxDistance = Mathf.Max(0.05f, leg.UpperLength + leg.LowerLength - 0.02f);
        float clampedDistance = Mathf.Clamp(distance, 0.05f, maxDistance);

        Vector3 planeNormal = direction.Cross(preferredForward);
        if (planeNormal.LengthSquared() < 0.0001f)
        {
            planeNormal = direction.Cross(Vector3.Right);
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
        leg.Foot.GlobalBasis = CreateFootBasis(groundNormal, preferredForward);
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
        Vector3 z = forwardHint.Slide(y).Normalized();
        if (z.LengthSquared() < 0.0001f)
        {
            z = -Vector3.Forward;
        }

        Vector3 x = y.Cross(z).Normalized();
        z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
    }
}
