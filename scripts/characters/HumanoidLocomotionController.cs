using Godot;
using Godot.Collections;

public sealed class HumanoidLocomotionController
{
    private readonly CharacterBody3D _body;
    private readonly HumanoidRig _rig;
    private readonly HumanoidBodySpec _spec;
    private readonly HumanoidLocomotionSettings _settings;

    private float _gaitTime;
    private float _locomotionBlend;
    private Vector3 _lastFacingForward = Vector3.Forward;
    private Vector3 _lastPlanarVelocity = Vector3.Zero;

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
        float moveAmount = Mathf.Clamp(intent.Move.Length(), 0.0f, 1.0f);
        float sprintBlend = moveAmount > 0.05f && intent.Sprint ? 1.0f : 0.0f;
        float maxSpeed = _settings.MoveSpeed * Mathf.Lerp(1.0f, _settings.SprintSpeedMultiplier, sprintBlend);
        float targetSpeed = maxSpeed * moveAmount;
        Vector3 targetVelocity = desiredDirection * targetSpeed;

        bool wasOnFloor = _body.IsOnFloor();
        if (wasOnFloor)
        {
            horizontalVelocity = UpdateGroundVelocity(horizontalVelocity, targetVelocity, moveAmount, delta);
        }
        else
        {
            float airFactor = DampFactor(_settings.AirAcceleration, delta);
            horizontalVelocity = horizontalVelocity.Lerp(targetVelocity, airFactor);
        }

        float gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity") * _settings.GravityScale;
        float verticalVelocity = _body.Velocity.Y;
        if (!wasOnFloor)
        {
            float appliedGravity = verticalVelocity <= 0.0f
                ? gravity * _settings.FallGravityMultiplier
                : gravity;
            verticalVelocity -= appliedGravity * delta;
        }
        else
        {
            verticalVelocity = -_settings.GroundStickVelocity;
        }

        _body.Velocity = new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
        _body.MoveAndSlide();
        if (_body.IsOnFloor())
        {
            _body.Velocity = new Vector3(_body.Velocity.X, -_settings.GroundStickVelocity, _body.Velocity.Z);
        }

        Vector3 facingVelocity = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        Vector3 facingTarget = facingVelocity.LengthSquared() > 0.01f
            ? facingVelocity.Normalized()
            : desiredDirection.LengthSquared() > 0.0001f
                ? desiredDirection
                : _lastFacingForward;

        if (facingTarget.LengthSquared() > 0.0001f)
        {
            _lastFacingForward = facingTarget.Normalized();
            float targetYaw = Mathf.Atan2(-_lastFacingForward.X, -_lastFacingForward.Z);
            _rig.VisualRoot.Rotation = new Vector3(
                0.0f,
                Mathf.LerpAngle(_rig.VisualRoot.Rotation.Y, targetYaw, DampFactor(_settings.RotationSpeed, delta)),
                0.0f);
        }

        UpdateVisualRig(delta, desiredDirection, targetVelocity, cameraPitch, sprintBlend, maxSpeed);
    }

    private Vector3 UpdateGroundVelocity(Vector3 currentVelocity, Vector3 targetVelocity, float moveAmount, float delta)
    {
        if (targetVelocity.LengthSquared() < 0.0001f)
        {
            return currentVelocity.MoveToward(Vector3.Zero, _settings.Deceleration * delta);
        }

        Vector3 targetDirection = targetVelocity.Normalized();
        float targetSpeed = targetVelocity.Length();
        float alongSpeed = currentVelocity.Dot(targetDirection);
        Vector3 alongVelocity = targetDirection * alongSpeed;
        Vector3 lateralVelocity = currentVelocity - alongVelocity;

        float acceleration = alongSpeed < targetSpeed
            ? _settings.Acceleration
            : _settings.Deceleration;
        float newAlongSpeed = Mathf.MoveToward(alongSpeed, targetSpeed, acceleration * delta);

        float turnRate = _settings.TurnResponsiveness * Mathf.Lerp(0.7f, 1.25f, moveAmount);
        if (alongSpeed < 0.0f)
        {
            turnRate *= 1.25f;
        }

        Vector3 newLateralVelocity = lateralVelocity.MoveToward(Vector3.Zero, turnRate * delta);
        Vector3 resolvedVelocity = (targetDirection * newAlongSpeed) + newLateralVelocity;
        float maxResolvedSpeed = _settings.MoveSpeed * _settings.SprintSpeedMultiplier;
        return resolvedVelocity.LimitLength(maxResolvedSpeed);
    }

    private void UpdateVisualRig(float delta, Vector3 desiredDirection, Vector3 targetVelocity, float cameraPitch, float sprintBlend, float maxSpeed)
    {
        Vector3 velocityPlanar = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
        float speed = velocityPlanar.Length();
        float gaitSpeedRatio = Mathf.Clamp(speed / Mathf.Max(maxSpeed, 0.01f), 0.0f, 1.0f);
        float absoluteSpeedRatio = Mathf.Clamp(speed / Mathf.Max(_settings.MoveSpeed * _settings.SprintSpeedMultiplier, 0.01f), 0.0f, 1.0f);
        float runBlend = Mathf.Clamp(Mathf.Max(sprintBlend * 0.8f, Mathf.InverseLerp(0.7f, 1.0f, absoluteSpeedRatio)), 0.0f, 1.0f);
        _locomotionBlend = Mathf.Lerp(_locomotionBlend, Mathf.Clamp(speed / Mathf.Max(_settings.MoveSpeed * 0.35f, 0.01f), 0.0f, 1.0f), DampFactor(8.0f, delta));

        Vector3 bodyForward = speed > 0.08f
            ? velocityPlanar.Normalized()
            : desiredDirection.LengthSquared() > 0.0001f
                ? desiredDirection
                : _lastFacingForward;
        Vector3 bodyRight = bodyForward.Cross(Vector3.Up).Normalized();
        if (bodyRight.LengthSquared() < 0.0001f)
        {
            bodyRight = Vector3.Right;
        }

        if (!_body.IsOnFloor())
        {
            UpdateAirborneRig(delta, desiredDirection, bodyForward, bodyRight, cameraPitch);
            _lastPlanarVelocity = velocityPlanar;
            return;
        }

        float legLength = _spec.LegLength;
        float strideLength = GetDerivedStrideLength(legLength, gaitSpeedRatio, runBlend);
        float stepHeight = GetDerivedStepHeight(legLength, gaitSpeedRatio, runBlend);
        float cycleSpeed = GetDerivedCycleSpeed(speed, strideLength, runBlend);
        _gaitTime = Mathf.PosMod(_gaitTime + (delta * cycleSpeed), Mathf.Tau);

        UpdateLeg(_rig.LeftLeg, _rig.RightLeg, delta, speed, gaitSpeedRatio, runBlend, legLength, strideLength, stepHeight, bodyForward, bodyRight, 0.0f);
        UpdateLeg(_rig.RightLeg, _rig.LeftLeg, delta, speed, gaitSpeedRatio, runBlend, legLength, strideLength, stepHeight, bodyForward, bodyRight, Mathf.Pi);

        float leftSupport = GetLegSupportWeight(_rig.LeftLeg, 0.0f);
        float rightSupport = GetLegSupportWeight(_rig.RightLeg, Mathf.Pi);
        float supportDelta = rightSupport - leftSupport;

        Vector3 planarAcceleration = delta > 0.0f
            ? (velocityPlanar - _lastPlanarVelocity) / delta
            : Vector3.Zero;
        _lastPlanarVelocity = velocityPlanar;

        float forwardAccel = Mathf.Clamp(planarAcceleration.Dot(bodyForward) / Mathf.Max(maxSpeed * 7.0f, 0.01f), -1.0f, 1.0f);
        float lateralAccel = Mathf.Clamp(planarAcceleration.Dot(bodyRight) / Mathf.Max(maxSpeed * 7.0f, 0.01f), -1.0f, 1.0f);
        float targetForwardInfluence = desiredDirection.LengthSquared() > 0.0001f
            ? desiredDirection.Dot(bodyForward)
            : 0.0f;

        float strideBounce = Mathf.Sin(_gaitTime * 2.0f) * legLength * Mathf.Lerp(0.008f, 0.018f, runBlend) * gaitSpeedRatio;
        float pelvisCompression = -Mathf.Max(0.0f, forwardAccel) * legLength * 0.018f;
        float desiredHipWorldY =
            _body.GlobalPosition.Y +
            GetDerivedHipHeight(legLength) +
            Mathf.Lerp(legLength * 0.01f, -legLength * 0.004f, gaitSpeedRatio) +
            strideBounce +
            pelvisCompression;
        float localHipY = desiredHipWorldY - _body.GlobalPosition.Y;
        float desiredHipX = supportDelta * _spec.HipWidth * Mathf.Lerp(0.1f, 0.24f, runBlend);
        float desiredHipZ = -legLength * Mathf.Lerp(0.01f, 0.09f, runBlend) - Mathf.Max(0.0f, forwardAccel) * legLength * 0.025f;
        _rig.Hips.Position = new Vector3(
            Mathf.Lerp(_rig.Hips.Position.X, desiredHipX, DampFactor(9.0f, delta)),
            Mathf.Lerp(_rig.Hips.Position.Y, localHipY, DampFactor(11.0f, delta)),
            Mathf.Lerp(_rig.Hips.Position.Z, desiredHipZ, DampFactor(8.0f, delta)));

        float footHeightDelta = _rig.LeftLeg.CurrentFootPosition.Y - _rig.RightLeg.CurrentFootPosition.Y;
        float pelvisRoll = Mathf.Clamp((footHeightDelta * 0.45f) - (supportDelta * Mathf.Lerp(0.05f, 0.14f, runBlend)), -0.22f, 0.22f);
        float pelvisPitch = (-forwardAccel * Mathf.Lerp(0.03f, 0.09f, runBlend)) + (Mathf.Sin(_gaitTime) * Mathf.Lerp(0.02f, 0.06f, runBlend));
        float pelvisYaw = -lateralAccel * Mathf.Lerp(0.03f, 0.08f, runBlend);
        _rig.Hips.Rotation = new Vector3(pelvisPitch, pelvisYaw, pelvisRoll);

        float torsoBob = Mathf.Sin((_gaitTime * 2.0f) + 0.35f) * _spec.TorsoHeight * Mathf.Lerp(0.008f, 0.018f, runBlend) * _locomotionBlend;
        float forwardLean =
            (targetForwardInfluence * Mathf.Lerp(0.08f, 0.18f, runBlend)) +
            (Mathf.Max(0.0f, forwardAccel) * Mathf.Lerp(0.05f, 0.14f, runBlend)) +
            (runBlend * 0.04f);
        float torsoYaw = (Mathf.Sin(_gaitTime) * Mathf.Lerp(0.02f, 0.08f, runBlend) * _locomotionBlend) + (lateralAccel * 0.04f);
        float torsoRoll = (-pelvisRoll * Mathf.Lerp(0.3f, 0.48f, runBlend)) - (supportDelta * Mathf.Lerp(0.02f, 0.05f, runBlend));

        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = new Vector3(
            Mathf.Lerp(_rig.UpperBody.Rotation.X, Mathf.Clamp(-forwardLean * 0.22f, -0.08f, 0.12f), DampFactor(7.0f, delta)),
            Mathf.Lerp(_rig.UpperBody.Rotation.Y, torsoYaw, DampFactor(7.0f, delta)),
            Mathf.Lerp(_rig.UpperBody.Rotation.Z, torsoRoll, DampFactor(7.0f, delta)));

        _rig.Torso.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.5f) + torsoBob, 0.0f);
        _rig.Torso.Rotation = new Vector3(-forwardLean, 0.0f, 0.0f);

        _rig.ChestBand.Position = new Vector3(0.0f, (_spec.TorsoHeight * 0.62f) + (torsoBob * 0.75f), 0.0f);
        _rig.Head.Position = new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f) + (torsoBob * 0.45f), 0.0f);
        _rig.Head.Rotation = new Vector3(
            (-cameraPitch * 0.28f) + (forwardLean * 0.14f),
            -torsoYaw * 0.25f,
            (-pelvisRoll * 0.2f) + (lateralAccel * 0.04f));

        float armSwing = Mathf.Sin(_gaitTime) * Mathf.Lerp(0.16f, 0.92f, absoluteSpeedRatio);
        float armPump = Mathf.Lerp(0.8f, 1.22f, runBlend);
        float shoulderYaw = Mathf.Lerp(0.02f, 0.1f, runBlend);
        float armLift = Mathf.Max(0.0f, forwardAccel) * Mathf.Lerp(0.02f, 0.08f, runBlend);
        _rig.LeftArm.Rotation = new Vector3(
            (-armSwing * armPump) - armLift - (runBlend * 0.05f),
            -shoulderYaw - (torsoYaw * 0.25f),
            -Mathf.Lerp(0.18f, 0.32f, runBlend));
        _rig.RightArm.Rotation = new Vector3(
            (armSwing * armPump) - armLift - (runBlend * 0.05f),
            shoulderYaw - (torsoYaw * 0.25f),
            Mathf.Lerp(0.18f, 0.32f, runBlend));
    }

    private void UpdateAirborneRig(float delta, Vector3 desiredDirection, Vector3 bodyForward, Vector3 bodyRight, float cameraPitch)
    {
        _rig.Hips.Position = _rig.Hips.Position.Lerp(new Vector3(0.0f, _spec.HipHeight, -_spec.LegLength * 0.03f), DampFactor(8.0f, delta));
        _rig.Hips.Rotation = _rig.Hips.Rotation.Lerp(new Vector3(-0.06f, 0.0f, 0.0f), DampFactor(7.0f, delta));
        _rig.UpperBody.Position = Vector3.Zero;
        _rig.UpperBody.Rotation = _rig.UpperBody.Rotation.Lerp(new Vector3(-0.04f, 0.0f, 0.0f), DampFactor(7.0f, delta));
        _rig.Torso.Position = _rig.Torso.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.5f, 0.0f), DampFactor(8.0f, delta));
        _rig.Torso.Rotation = _rig.Torso.Rotation.Lerp(new Vector3(0.1f, 0.0f, 0.0f), DampFactor(7.0f, delta));
        _rig.ChestBand.Position = _rig.ChestBand.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight * 0.62f, 0.0f), DampFactor(8.0f, delta));
        _rig.Head.Position = _rig.Head.Position.Lerp(new Vector3(0.0f, _spec.TorsoHeight + _spec.NeckLength + (_spec.HeadRadius * 0.9f), 0.0f), DampFactor(8.0f, delta));
        _rig.Head.Rotation = _rig.Head.Rotation.Lerp(new Vector3(-cameraPitch * 0.22f, 0.0f, 0.0f), DampFactor(7.0f, delta));
        _rig.LeftArm.Rotation = _rig.LeftArm.Rotation.Lerp(new Vector3(0.34f, 0.0f, -0.12f), DampFactor(8.0f, delta));
        _rig.RightArm.Rotation = _rig.RightArm.Rotation.Lerp(new Vector3(0.34f, 0.0f, 0.12f), DampFactor(8.0f, delta));

        UpdateAirborneLeg(_rig.LeftLeg, delta, bodyForward, bodyRight);
        UpdateAirborneLeg(_rig.RightLeg, delta, bodyForward, bodyRight);
    }

    private void UpdateAirborneLeg(HumanoidLegRig leg, float delta, Vector3 bodyForward, Vector3 bodyRight)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (bodyRight * leg.SideOffset);
        Vector3 hangingTarget = hipWorld + (bodyForward * 0.05f) + (Vector3.Down * (leg.UpperLength + leg.LowerLength - (_spec.FootHeight * 1.45f)));

        leg.CurrentFootPosition = leg.CurrentFootPosition.Lerp(hangingTarget, DampFactor(10.0f, delta));
        leg.PlantedFootPosition = leg.CurrentFootPosition;
        leg.TargetFootPosition = leg.CurrentFootPosition;
        leg.StepStartPosition = leg.CurrentFootPosition;
        leg.StepProgress = 1.0f;
        leg.IsStepping = false;
        leg.GroundNormal = leg.GroundNormal.Slerp(Vector3.Up, DampFactor(8.0f, delta));
        leg.TargetNormal = leg.GroundNormal;

        SolveLeg(leg, hipWorld, leg.CurrentFootPosition, leg.GroundNormal, bodyForward);
    }

    private void UpdateLeg(
        HumanoidLegRig leg,
        HumanoidLegRig otherLeg,
        float delta,
        float speed,
        float speedRatio,
        float runBlend,
        float legLength,
        float strideLength,
        float stepHeight,
        Vector3 bodyForward,
        Vector3 bodyRight,
        float phaseOffset)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (bodyRight * leg.SideOffset);
        float phase = Mathf.PosMod(_gaitTime + phaseOffset, Mathf.Tau);
        float swingSignal = Mathf.Sin(phase);
        if (swingSignal < -0.35f)
        {
            leg.StepCycleConsumed = false;
        }

        Vector3 stepCenter =
            _body.GlobalPosition +
            _rig.VisualRoot.Position +
            (bodyForward * Mathf.Lerp(legLength * 0.08f, legLength * 0.16f, runBlend)) +
            (bodyRight * leg.SideOffset);
        stepCenter.Y = _body.GlobalPosition.Y + _spec.FootHeight;

        float trailingBias = Mathf.Lerp(legLength * 0.01f, legLength * 0.06f, speedRatio) + (runBlend * legLength * 0.015f);
        float forwardBias = Mathf.Lerp(legLength * 0.12f, legLength * 0.28f, speedRatio) + (runBlend * legLength * 0.08f);
        float strideDistance = strideLength * Mathf.Lerp(0.26f, 0.9f, speedRatio);
        Vector3 landingProbe = stepCenter + (bodyForward * (forwardBias + strideDistance * Mathf.Max(0.2f, swingSignal + 0.25f)));
        Vector3 stanceProbe = stepCenter - (bodyForward * trailingBias);

        Vector3 landingPosition = SampleGroundPoint(landingProbe, out Vector3 landingNormal);
        Vector3 stancePosition = SampleGroundPoint(stanceProbe, out Vector3 stanceNormal);
        landingPosition = ClampLegTargetToReach(hipWorld, landingPosition, bodyForward, bodyRight, legLength, runBlend, speedRatio, allowForwardBias: true);
        stancePosition = ClampLegTargetToReach(hipWorld, stancePosition, bodyForward, bodyRight, legLength, runBlend, speedRatio, allowForwardBias: false);

        float supportRadius = Mathf.Lerp(legLength * 0.16f, legLength * 0.36f, speedRatio) + (runBlend * legLength * 0.04f);
        float stepTriggerRadius = Mathf.Lerp(legLength * 0.22f, legLength * 0.32f, speedRatio);
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
            leg.StepDuration = 0.0f;
            leg.IsStepping = false;
            leg.StepCycleConsumed = false;
        }

        float plantedForwardOffset = (leg.PlantedFootPosition - hipWorld).Dot(bodyForward);
        bool swingWindow = swingSignal > 0.05f;
        bool footTrailing = plantedForwardOffset < -trailingBias;
        bool reachExceeded = currentReach > stepTriggerRadius;
        bool landingChanged = HorizontalDistance(leg.PlantedFootPosition, landingPosition) > legLength * 0.08f;

        bool shouldStep =
            !leg.IsStepping &&
            !otherLeg.IsStepping &&
            !leg.StepCycleConsumed &&
            speedRatio > 0.035f &&
            swingWindow &&
            landingChanged &&
            (reachExceeded || footTrailing);

        if (shouldStep)
        {
            leg.StepStartPosition = leg.CurrentFootPosition;
            leg.TargetFootPosition = landingPosition;
            leg.TargetNormal = landingNormal;
            leg.StepProgress = 0.0f;
            leg.StepDuration = GetStepDuration(speedRatio, runBlend);
            leg.IsStepping = true;
            leg.StepCycleConsumed = true;
        }

        if (leg.IsStepping)
        {
            float duration = Mathf.Max(leg.StepDuration, 0.01f);
            leg.StepProgress = Mathf.Min(1.0f, leg.StepProgress + (delta / duration));

            float swingT = Mathf.SmoothStep(0.0f, 1.0f, leg.StepProgress);
            Vector3 foot = leg.StepStartPosition.Lerp(leg.TargetFootPosition, swingT);
            foot.Y += Mathf.Sin(swingT * Mathf.Pi) * stepHeight;
            leg.CurrentFootPosition = foot;
            leg.GroundNormal = leg.GroundNormal.Slerp(leg.TargetNormal, DampFactor(10.0f, delta));

            if (leg.StepProgress >= 1.0f)
            {
                leg.IsStepping = false;
                leg.StepDuration = 0.0f;
                leg.PlantedFootPosition = leg.TargetFootPosition;
                leg.CurrentFootPosition = leg.TargetFootPosition;
                leg.GroundNormal = leg.TargetNormal;
            }
        }
        else
        {
            Vector3 planted = leg.PlantedFootPosition;
            planted.Y = Mathf.Lerp(planted.Y, stancePosition.Y, DampFactor(10.0f, delta));
            if (HorizontalDistance(hipWorld, planted) > supportRadius)
            {
                planted = ClampLegTargetToReach(hipWorld, planted, bodyForward, bodyRight, supportRadius, runBlend, speedRatio, allowForwardBias: false);
            }

            leg.PlantedFootPosition = planted;
            leg.CurrentFootPosition = leg.CurrentFootPosition.Lerp(planted, DampFactor(18.0f, delta));
            leg.GroundNormal = leg.GroundNormal.Slerp(stanceNormal, DampFactor(10.0f, delta));
        }

        SolveLeg(leg, hipWorld, leg.CurrentFootPosition, leg.GroundNormal, bodyForward);
    }

    private float GetDerivedHipHeight(float legLength)
    {
        return legLength * 0.52f;
    }

    private float GetDerivedStrideLength(float legLength, float speedRatio, float runBlend)
    {
        return Mathf.Lerp(legLength * 0.2f, legLength * 0.48f, speedRatio) + (runBlend * legLength * 0.08f);
    }

    private float GetDerivedStepHeight(float legLength, float speedRatio, float runBlend)
    {
        return Mathf.Lerp(legLength * 0.08f, legLength * 0.16f, speedRatio) + (runBlend * legLength * 0.03f);
    }

    private float GetDerivedCycleSpeed(float speed, float strideLength, float runBlend)
    {
        float walkCadence = Mathf.Tau / 1.05f;
        float runCadence = Mathf.Tau / 0.72f;
        float speedRatio = Mathf.Clamp(speed / Mathf.Max(_settings.MoveSpeed * _settings.SprintSpeedMultiplier, 0.01f), 0.0f, 1.0f);
        return Mathf.Lerp(walkCadence, runCadence, Mathf.Lerp(speedRatio * 0.6f, 1.0f, runBlend));
    }

    private static float GetStepDuration(float speedRatio, float runBlend)
    {
        float walkStepSeconds = 0.42f;
        float runStepSeconds = 0.3f;
        return Mathf.Lerp(walkStepSeconds, runStepSeconds, Mathf.Clamp((speedRatio * 0.6f) + (runBlend * 0.4f), 0.0f, 1.0f));
    }

    private float GetLegSupportWeight(HumanoidLegRig leg, float phaseOffset)
    {
        float phase = Mathf.PosMod(_gaitTime + phaseOffset, Mathf.Tau);
        float swingAmount = Mathf.Max(0.0f, Mathf.Sin(phase));
        float support = 1.0f - (swingAmount * 0.72f);
        return leg.IsStepping ? support * 0.35f : support;
    }

    private static float GetSupportFootHeight(HumanoidLegRig leg)
    {
        float plantedHeight = leg.PlantedFootPosition.Y;
        if (!leg.IsStepping)
        {
            return plantedHeight;
        }

        return Mathf.Min(plantedHeight, leg.CurrentFootPosition.Y);
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector2 av = new(a.X, a.Z);
        Vector2 bv = new(b.X, b.Z);
        return av.DistanceTo(bv);
    }

    private static Vector3 ClampLegTargetToReach(
        Vector3 hipWorld,
        Vector3 target,
        Vector3 bodyForward,
        Vector3 bodyRight,
        float maxReach,
        float runBlend,
        float speedRatio,
        bool allowForwardBias)
    {
        Vector3 toTarget = target - hipWorld;
        Vector3 flattenedForward = bodyForward.Normalized();
        Vector3 flattenedRight = bodyRight.Normalized();
        float forward = toTarget.Dot(flattenedForward);
        float lateral = toTarget.Dot(flattenedRight);
        float vertical = toTarget.Y;

        float minPlanarReach = maxReach * (allowForwardBias ? 0.28f : 0.18f);
        float maxPlanarReach = maxReach * (allowForwardBias ? 0.9f : 0.82f);
        float forwardMin = allowForwardBias
            ? Mathf.Lerp(-maxReach * 0.02f, maxReach * 0.12f, speedRatio)
            : -Mathf.Lerp(maxReach * 0.08f, maxReach * 0.18f, speedRatio + (runBlend * 0.15f));
        float forwardMax = Mathf.Lerp(maxReach * 0.28f, maxReach * 0.5f, speedRatio) + (runBlend * maxReach * 0.08f);
        float lateralMax = Mathf.Lerp(maxReach * 0.18f, maxReach * 0.3f, speedRatio);
        float minVertical = -Mathf.Lerp(maxReach * 0.82f, maxReach * 0.92f, runBlend);
        float maxVertical = Mathf.Lerp(maxReach * 0.14f, maxReach * 0.08f, speedRatio);

        forward = Mathf.Clamp(forward, forwardMin, forwardMax);
        lateral = Mathf.Clamp(lateral, -lateralMax, lateralMax);
        vertical = Mathf.Clamp(vertical, minVertical, maxVertical);

        Vector2 planar = new(forward, lateral);
        float planarLength = planar.Length();
        if (planarLength < 0.0001f)
        {
            planar = new Vector2(minPlanarReach, 0.0f);
            planarLength = minPlanarReach;
        }

        float clampedPlanarLength = Mathf.Clamp(planarLength, minPlanarReach, maxPlanarReach);
        planar = (planar / planarLength) * clampedPlanarLength;
        forward = planar.X;
        lateral = planar.Y;

        Vector3 clamped = hipWorld + (flattenedForward * forward) + (flattenedRight * lateral) + (Vector3.Up * vertical);
        Vector3 offset = clamped - hipWorld;
        float distance = offset.Length();
        if (distance > maxReach)
        {
            clamped = hipWorld + (offset / distance) * maxReach;
        }

        return clamped;
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

    private static void SolveLeg(HumanoidLegRig leg, Vector3 hip, Vector3 footTarget, Vector3 groundNormal, Vector3 preferredForward)
    {
        Vector3 foot = ClampFootToBoneReach(leg, hip, footTarget);
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

    private static float DampFactor(float sharpness, float delta)
    {
        return 1.0f - Mathf.Exp(-sharpness * delta);
    }
}
