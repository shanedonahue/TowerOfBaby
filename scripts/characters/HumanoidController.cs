using Godot;
using Godot.Collections;

public partial class HumanoidController : CharacterBody3D
{
    [Export] public float MoveSpeed = 7.0f;
    [Export] public float Acceleration = 10.0f;
    [Export] public float AirAcceleration = 3.0f;
    [Export] public float RotationSpeed = 9.0f;
    [Export] public float GravityScale = 1.0f;
    [Export] public float FloorSnapDistance = 0.9f;
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public float CameraPitchMin = -0.9f;
    [Export] public float CameraPitchMax = 0.35f;
    [Export] public float CameraDistance = 6.5f;
    [Export] public float CameraFarDistance = 220.0f;
    [Export] public float FootProbeDistance = 4.0f;
    [Export] public float HipHeight = 0.72f;
    [Export] public float StrideLength = 0.56f;
    [Export] public float StepHeight = 0.24f;
    [Export] public float WalkCycleSpeed = 2.35f;
    [Export] public float RunCycleSpeed = 4.1f;

    private Node3D _yawPivot = null!;
    private Node3D _cameraPitchPivot = null!;
    private SpringArm3D _springArm = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collision = null!;
    private Node3D _visualRoot = null!;
    private Node3D _hips = null!;
    private Node3D _upperBody = null!;
    private Node3D _torso = null!;
    private Node3D _chestBand = null!;
    private Node3D _head = null!;
    private Node3D _leftArm = null!;
    private Node3D _rightArm = null!;

    private LegRig _leftLeg = null!;
    private LegRig _rightLeg = null!;

    private float _cameraYaw;
    private float _cameraPitch = -0.35f;
    private float _gaitTime;
    private Vector3 _lastFacingForward = Vector3.Forward;

    public override void _Ready()
    {
        AddToGroup("terrain_tracker");
        EnsureInputMap();
        BuildRig();

        Input.MouseMode = Input.MouseModeEnum.Captured;
        FloorSnapLength = FloorSnapDistance;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }

        if (@event is InputEventMouseMotion mouseMotion)
        {
            _cameraYaw -= mouseMotion.Relative.X * MouseSensitivity;
            _yawPivot.Rotation = new Vector3(0.0f, _cameraYaw, 0.0f);
            _cameraPitch = Mathf.Clamp(_cameraPitch - mouseMotion.Relative.Y * MouseSensitivity, CameraPitchMin, CameraPitchMax);
            _cameraPitchPivot.Rotation = new Vector3(_cameraPitch, 0.0f, 0.0f);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector2 moveInput = Input.GetVector("move_left", "move_right", "move_forward", "move_back");

        Basis cameraBasis = _yawPivot.GlobalTransform.Basis;
        Vector3 desiredDirection = (
            cameraBasis.Z * moveInput.Y +
            cameraBasis.X * moveInput.X).Normalized();

        Vector3 horizontalVelocity = new Vector3(Velocity.X, 0.0f, Velocity.Z);
        float targetSpeed = MoveSpeed * Mathf.Clamp(moveInput.Length(), 0.0f, 1.0f);
        Vector3 targetVelocity = desiredDirection * targetSpeed;
        float appliedAcceleration = IsOnFloor() ? Acceleration : AirAcceleration;
        horizontalVelocity = horizontalVelocity.Lerp(targetVelocity, 1.0f - Mathf.Exp(-appliedAcceleration * dt));

        float gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity") * GravityScale;
        float verticalVelocity = Velocity.Y;
        if (!IsOnFloor())
        {
            verticalVelocity -= gravity * dt;
        }
        else if (verticalVelocity < 0.0f)
        {
            verticalVelocity = -0.1f;
        }

        Velocity = new Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z);
        MoveAndSlide();

        Vector3 facingVelocity = new Vector3(Velocity.X, 0.0f, Velocity.Z);
        if (facingVelocity.LengthSquared() > 0.04f)
        {
            _lastFacingForward = facingVelocity.Normalized();
            float targetYaw = Mathf.Atan2(-facingVelocity.X, -facingVelocity.Z);
            _visualRoot.Rotation = new Vector3(
                0.0f,
                Mathf.LerpAngle(_visualRoot.Rotation.Y, targetYaw, 1.0f - Mathf.Exp(-RotationSpeed * dt)),
                0.0f);
        }

        UpdateVisualRig(dt, desiredDirection);
    }

    private void BuildRig()
    {
        _yawPivot = new Node3D { Name = "YawPivot" };
        AddChild(_yawPivot);

        _cameraPitchPivot = new Node3D
        {
            Name = "CameraPitchPivot",
            Position = new Vector3(0.0f, 1.65f, 0.0f)
        };
        _yawPivot.AddChild(_cameraPitchPivot);

        _springArm = new SpringArm3D
        {
            Name = "SpringArm3D",
            SpringLength = CameraDistance,
            Margin = 0.2f
        };
        _cameraPitchPivot.AddChild(_springArm);

        _camera = new Camera3D
        {
            Name = "Camera3D",
            Current = true,
            Fov = 68.0f,
            Far = CameraFarDistance
        };
        _springArm.AddChild(_camera);

        _collision = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        if (_collision == null)
        {
            _collision = new CollisionShape3D { Name = "CollisionShape3D" };
            AddChild(_collision);
        }

        _collision.Shape = new CapsuleShape3D
        {
            Radius = 0.38f,
            Height = 1.1f
        };
        _collision.Position = new Vector3(0.0f, 0.95f, 0.0f);

        _visualRoot = new Node3D
        {
            Name = "VisualRoot",
            Position = new Vector3(0.0f, 0.28f, 0.0f)
        };
        AddChild(_visualRoot);

        StandardMaterial3D bodyMaterial = new()
        {
            AlbedoColor = new Color(0.72f, 0.62f, 0.45f),
            Roughness = 0.75f
        };

        StandardMaterial3D accentMaterial = new()
        {
            AlbedoColor = new Color(0.18f, 0.24f, 0.31f),
            Roughness = 0.68f
        };

        _hips = new Node3D
        {
            Name = "Hips",
            Position = new Vector3(0.0f, HipHeight, 0.0f)
        };
        _visualRoot.AddChild(_hips);

        _upperBody = new Node3D { Name = "UpperBody" };
        _hips.AddChild(_upperBody);

        _torso = CreateBoxPart("Torso", new Vector3(0.68f, 0.9f, 0.32f), bodyMaterial, new Vector3(0.0f, 0.54f, 0.0f));
        _upperBody.AddChild(_torso);

        _chestBand = CreateBoxPart("ChestBand", new Vector3(0.72f, 0.28f, 0.35f), accentMaterial, new Vector3(0.0f, 0.63f, 0.0f));
        _upperBody.AddChild(_chestBand);

        _head = CreateSpherePart("Head", 0.24f, bodyMaterial, new Vector3(0.0f, 1.26f, 0.0f));
        _upperBody.AddChild(_head);

        _leftArm = CreateArm("LeftArm", new Vector3(-0.48f, 0.74f, 0.0f), accentMaterial);
        _rightArm = CreateArm("RightArm", new Vector3(0.48f, 0.74f, 0.0f), accentMaterial);

        _leftLeg = CreateLegRig("LeftLeg", -0.24f, 0.0f, accentMaterial);
        _rightLeg = CreateLegRig("RightLeg", 0.24f, Mathf.Pi, accentMaterial);

        _yawPivot.Rotation = new Vector3(0.0f, _cameraYaw, 0.0f);
        _cameraPitchPivot.Rotation = new Vector3(_cameraPitch, 0.0f, 0.0f);
    }

    private void UpdateVisualRig(float delta, Vector3 desiredDirection)
    {
        Vector3 velocityPlanar = new Vector3(Velocity.X, 0.0f, Velocity.Z);
        float speed = velocityPlanar.Length();
        float speedRatio = Mathf.Clamp(speed / MoveSpeed, 0.0f, 1.0f);
        Vector3 bodyForward = speed > 0.08f ? velocityPlanar.Normalized() : _lastFacingForward;
        Vector3 bodyRight = bodyForward.Cross(Vector3.Up).Normalized();
        if (bodyRight.LengthSquared() < 0.0001f)
        {
            bodyRight = Vector3.Right;
        }

        if (!IsOnFloor())
        {
            UpdateAirborneRig(delta, desiredDirection, bodyForward, bodyRight);
            return;
        }

        float legLength = GetLegLength();
        float derivedStrideLength = GetDerivedStrideLength(legLength, speedRatio);
        float derivedStepHeight = GetDerivedStepHeight(legLength, speedRatio);
        float cycleSpeed = GetDerivedCycleSpeed(speed, derivedStrideLength);
        _gaitTime = Mathf.PosMod(_gaitTime + delta * cycleSpeed, Mathf.Tau);

        UpdateLeg(_leftLeg, _rightLeg, delta, speed, speedRatio, legLength, derivedStrideLength, derivedStepHeight, bodyForward, bodyRight);
        UpdateLeg(_rightLeg, _leftLeg, delta, speed, speedRatio, legLength, derivedStrideLength, derivedStepHeight, bodyForward, bodyRight);

        float supportHeight = (_leftLeg.CurrentFootPosition.Y + _rightLeg.CurrentFootPosition.Y) * 0.5f;
        float desiredHipWorldY = supportHeight + GetDerivedHipHeight(legLength) + Mathf.Lerp(0.0f, legLength * 0.03f, speedRatio);
        float localHipY = desiredHipWorldY - GlobalPosition.Y;
        _hips.Position = new Vector3(
            0.0f,
            Mathf.Lerp(_hips.Position.Y, localHipY, 1.0f - Mathf.Exp(-10.0f * delta)),
            0.0f);

        float pelvisRoll = Mathf.Clamp((_leftLeg.CurrentFootPosition.Y - _rightLeg.CurrentFootPosition.Y) * 0.5f, -0.18f, 0.18f);
        _hips.Rotation = new Vector3(0.0f, 0.0f, pelvisRoll);

        float torsoBob = Mathf.Sin(_gaitTime * 2.0f) * 0.03f * speedRatio;
        _upperBody.Position = Vector3.Zero;
        _upperBody.Rotation = new Vector3(
            speedRatio * 0.03f,
            Mathf.Sin(_gaitTime) * -0.06f * speedRatio,
            -pelvisRoll * 0.35f);

        _torso.Position = new Vector3(0.0f, 0.54f + torsoBob, 0.0f);
        _torso.Rotation = new Vector3(
            0.0f,
            0.0f,
            desiredDirection.X * -0.12f);

        _chestBand.Position = new Vector3(0.0f, 0.63f + torsoBob * 0.7f, 0.0f);
        _head.Position = new Vector3(0.0f, 1.26f + torsoBob * 0.5f, 0.0f);
        _head.Rotation = new Vector3(-_cameraPitch * 0.3f, Mathf.Sin(_gaitTime) * 0.04f * speedRatio, -pelvisRoll * 0.3f);

        float armSwing = Mathf.Sin(_gaitTime) * Mathf.Lerp(0.1f, 0.9f, speedRatio);
        _leftArm.Rotation = new Vector3(-armSwing * 0.75f, 0.0f, 0.18f);
        _rightArm.Rotation = new Vector3(armSwing * 0.75f, 0.0f, -0.18f);
    }

    private void UpdateAirborneRig(float delta, Vector3 desiredDirection, Vector3 bodyForward, Vector3 bodyRight)
    {
        _hips.Position = _hips.Position.Lerp(new Vector3(0.0f, HipHeight, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _hips.Rotation = _hips.Rotation.Lerp(Vector3.Zero, 1.0f - Mathf.Exp(-8.0f * delta));
        _upperBody.Position = Vector3.Zero;
        _upperBody.Rotation = new Vector3(-0.08f, 0.0f, 0.0f);
        _torso.Position = _torso.Position.Lerp(new Vector3(0.0f, 0.54f, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _torso.Rotation = new Vector3(0.08f, 0.0f, desiredDirection.X * -0.06f);
        _chestBand.Position = _chestBand.Position.Lerp(new Vector3(0.0f, 0.63f, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _head.Position = _head.Position.Lerp(new Vector3(0.0f, 1.26f, 0.0f), 1.0f - Mathf.Exp(-8.0f * delta));
        _head.Rotation = new Vector3(-_cameraPitch * 0.2f, 0.0f, 0.0f);
        _leftArm.Rotation = _leftArm.Rotation.Lerp(new Vector3(0.3f, 0.0f, 0.12f), 1.0f - Mathf.Exp(-8.0f * delta));
        _rightArm.Rotation = _rightArm.Rotation.Lerp(new Vector3(0.3f, 0.0f, -0.12f), 1.0f - Mathf.Exp(-8.0f * delta));

        UpdateAirborneLeg(_leftLeg, delta, bodyForward, bodyRight);
        UpdateAirborneLeg(_rightLeg, delta, bodyForward, bodyRight);
    }

    private void UpdateAirborneLeg(LegRig leg, float delta, Vector3 bodyForward, Vector3 bodyRight)
    {
        Vector3 hipWorld = _hips.GlobalPosition + bodyRight * leg.SideOffset;
        Vector3 hangingTarget = hipWorld + bodyForward * 0.08f + Vector3.Down * (leg.UpperLength + leg.LowerLength - 0.12f);

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
        LegRig leg,
        LegRig otherLeg,
        float delta,
        float speed,
        float speedRatio,
        float legLength,
        float strideLength,
        float stepHeight,
        Vector3 bodyForward,
        Vector3 bodyRight)
    {
        Vector3 hipWorld = _hips.GlobalPosition + bodyRight * leg.SideOffset;

        Vector3 stepCenter = GlobalPosition + _visualRoot.Position + bodyRight * leg.SideOffset;
        stepCenter.Y = GlobalPosition.Y + 0.08f;
        float trailingBias = Mathf.Lerp(legLength * 0.0f, legLength * 0.04f, speedRatio);
        float forwardBias = Mathf.Lerp(legLength * 0.08f, legLength * 0.22f, speedRatio);
        float strideDistance = strideLength * Mathf.Lerp(0.22f, 1.0f, speedRatio);
        Vector3 landingProbe = stepCenter + bodyForward * (forwardBias + strideDistance);
        Vector3 stanceProbe = stepCenter - bodyForward * trailingBias;

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

    private float GetLegLength()
    {
        return _leftLeg.UpperLength + _leftLeg.LowerLength;
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
        return Mathf.Lerp(0.0f, Mathf.Clamp(baseCycle, WalkCycleSpeed, RunCycleSpeed), Mathf.Clamp(speed / MoveSpeed, 0.0f, 1.0f));
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector2 av = new Vector2(a.X, a.Z);
        Vector2 bv = new Vector2(b.X, b.Z);
        return av.DistanceTo(bv);
    }

    private static Vector3 ClampFootTargetToReach(Vector3 hipWorld, Vector3 target, float maxReach, float minReach)
    {
        Vector2 hip = new Vector2(hipWorld.X, hipWorld.Z);
        Vector2 foot = new Vector2(target.X, target.Z);
        Vector2 offset = foot - hip;
        float length = offset.Length();

        if (length < 0.0001f)
        {
            offset = Vector2.Up * minReach;
            length = minReach;
        }

        float clampedLength = Mathf.Clamp(length, minReach, maxReach);
        Vector2 clamped = hip + (offset / length) * clampedLength;
        return new Vector3(clamped.X, target.Y, clamped.Y);
    }

    private Vector3 SampleGroundPoint(Vector3 center, out Vector3 normal)
    {
        Vector3 origin = center + Vector3.Up * 2.4f;
        Vector3 target = origin + Vector3.Down * FootProbeDistance;

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;
        query.Exclude = new Array<Rid> { GetRid() };

        Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count > 0)
        {
            normal = ((Vector3)result["normal"]).Normalized();
            return ((Vector3)result["position"]) + normal * 0.04f;
        }

        normal = Vector3.Up;
        return center;
    }

    private static void SolveLeg(LegRig leg, Vector3 hip, Vector3 foot, Vector3 groundNormal, Vector3 preferredForward)
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
        Vector3 knee = hip + direction * along + bendDirection * bendHeight;

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

    private static Node3D CreateBoxPart(string name, Vector3 size, Material material, Vector3 position)
    {
        Node3D pivot = new() { Name = name, Position = position };
        MeshInstance3D mesh = new()
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material
        };
        pivot.AddChild(mesh);
        return pivot;
    }

    private static Node3D CreateSpherePart(string name, float radius, Material material, Vector3 position)
    {
        Node3D pivot = new() { Name = name, Position = position };
        MeshInstance3D mesh = new()
        {
            Mesh = new SphereMesh
            {
                Radius = radius,
                Height = radius * 2.0f
            },
            MaterialOverride = material
        };
        pivot.AddChild(mesh);
        return pivot;
    }

    private Node3D CreateArm(string name, Vector3 shoulderPosition, Material material)
    {
        Node3D shoulder = new() { Name = name, Position = shoulderPosition };
        _upperBody.AddChild(shoulder);

        MeshInstance3D upper = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.085f,
                Height = 0.62f
            },
            Position = new Vector3(0.0f, -0.32f, 0.0f),
            MaterialOverride = material
        };
        shoulder.AddChild(upper);

        MeshInstance3D lower = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.07f,
                Height = 0.56f
            },
            Position = new Vector3(0.0f, -0.85f, 0.0f),
            MaterialOverride = material
        };
        shoulder.AddChild(lower);

        return shoulder;
    }

    private LegRig CreateLegRig(string name, float sideOffset, float phaseOffset, Material material)
    {
        LegRig leg = new()
        {
            SideOffset = sideOffset,
            PhaseOffset = phaseOffset,
            UpperLength = 0.66f,
            LowerLength = 0.7f,
            GroundNormal = Vector3.Up,
            TargetNormal = Vector3.Up
        };

        Node3D upper = new() { Name = $"{name}Upper", Position = new Vector3(sideOffset, 0.0f, 0.0f) };
        _hips.AddChild(upper);

        MeshInstance3D upperMesh = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.11f,
                Height = leg.UpperLength
            },
            Position = new Vector3(0.0f, -leg.UpperLength * 0.5f, 0.0f),
            MaterialOverride = material
        };
        upper.AddChild(upperMesh);

        Node3D lower = new() { Name = $"{name}Lower" };
        upper.AddChild(lower);

        MeshInstance3D lowerMesh = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.095f,
                Height = leg.LowerLength
            },
            Position = new Vector3(0.0f, -leg.LowerLength * 0.5f, 0.0f),
            MaterialOverride = material
        };
        lower.AddChild(lowerMesh);

        Node3D foot = new() { Name = $"{name}Foot" };
        lower.AddChild(foot);

        MeshInstance3D footMesh = new()
        {
            Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.08f, 0.4f) },
            Position = new Vector3(0.0f, -0.04f, -0.08f),
            MaterialOverride = material
        };
        foot.AddChild(footMesh);

        leg.Upper = upper;
        leg.Lower = lower;
        leg.Foot = foot;
        return leg;
    }

    private static void EnsureInputMap()
    {
        AddActionIfMissing("move_forward", Key.W, Key.Up);
        AddActionIfMissing("move_back", Key.S, Key.Down);
        AddActionIfMissing("move_left", Key.A, Key.Left);
        AddActionIfMissing("move_right", Key.D, Key.Right);
    }

    private static void AddActionIfMissing(string actionName, params Key[] keys)
    {
        if (!InputMap.HasAction(actionName))
        {
            InputMap.AddAction(actionName);
        }

        foreach (Key key in keys)
        {
            InputEventKey inputEvent = new()
            {
                Keycode = key,
                PhysicalKeycode = key
            };

            if (!InputMap.ActionHasEvent(actionName, inputEvent))
            {
                InputMap.ActionAddEvent(actionName, inputEvent);
            }
        }
    }

    private sealed class LegRig
    {
        public Node3D Upper = null!;
        public Node3D Lower = null!;
        public Node3D Foot = null!;
        public float SideOffset;
        public float PhaseOffset;
        public float UpperLength;
        public float LowerLength;
        public bool Initialized;
        public bool IsStepping;
        public float StepProgress = 1.0f;
        public Vector3 StepStartPosition = Vector3.Zero;
        public Vector3 TargetFootPosition = Vector3.Zero;
        public Vector3 PlantedFootPosition = Vector3.Zero;
        public Vector3 CurrentFootPosition = Vector3.Zero;
        public Vector3 GroundNormal = Vector3.Up;
        public Vector3 TargetNormal = Vector3.Up;
    }
}
