using Godot;
using TowerOfBaby.Entities.Body.Biped;
using TowerOfBaby.Entities.Motion;
using TowerOfBaby.Terrain;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Entities.Controller.Player;

public partial class PlayerLocomotionAdapter : CharacterBody3D, ILocomotionTelemetrySource
{
    [ExportGroup("Scene Paths")]
    [Export] public NodePath PelvisPath = new("VisualRoot/Pelvis");
    [Export] public NodePath PelvisMeshPath = new("VisualRoot/Pelvis/PelvisMesh");
    [Export] public NodePath TorsoPath = new("VisualRoot/Pelvis/Torso");
    [Export] public NodePath TorsoMeshPath = new("VisualRoot/Pelvis/Torso/TorsoMesh");
    [Export] public NodePath LeftUpperLegPath = new("VisualRoot/LeftUpperLeg");
    [Export] public NodePath LeftLowerLegPath = new("VisualRoot/LeftLowerLeg");
    [Export] public NodePath LeftFootPath = new("VisualRoot/LeftFoot");
    [Export] public NodePath RightUpperLegPath = new("VisualRoot/RightUpperLeg");
    [Export] public NodePath RightLowerLegPath = new("VisualRoot/RightLowerLeg");
    [Export] public NodePath RightFootPath = new("VisualRoot/RightFoot");
    [Export] public NodePath LeftUpperArmPath = new("VisualRoot/LeftUpperArm");
    [Export] public NodePath LeftLowerArmPath = new("VisualRoot/LeftLowerArm");
    [Export] public NodePath LeftHandPath = new("VisualRoot/LeftHand");
    [Export] public NodePath RightUpperArmPath = new("VisualRoot/RightUpperArm");
    [Export] public NodePath RightLowerArmPath = new("VisualRoot/RightLowerArm");
    [Export] public NodePath RightHandPath = new("VisualRoot/RightHand");
    [Export] public NodePath WeaponGripPath = new("VisualRoot/RightHand/WeaponGrip");
    [Export] public NodePath WeaponMountPath = new("VisualRoot/RightHand/WeaponGrip/SwordPivot");
    [Export] public NodePath CameraYawPath = new("CameraYaw");
    [Export] public NodePath CameraPitchPath = new("CameraYaw/CameraPitch");
    [Export] public NodePath CameraBoomPath = new("CameraYaw/CameraPitch/SpringArm3D");
    [Export] public NodePath DebugPath = new("LocomotionDebug");
    [Export] public NodePath AttackDebugPath = new("AttackDebug");
    [Export] public NodePath TerrainWorldPath = new("../TerrainWorld");
    [Export(PropertyHint.File, "*.glb,*.tscn")] public string WeaponAssetPath = "res://assets/character/equipment/weapon/Sword.glb";

    [ExportGroup("Movement")]
    [Export] public float MaxGroundSpeed = 4.4f;
    [Export] public float GroundAcceleration = 17.0f;
    [Export] public float GroundDeceleration = 22.0f;
    [Export] public float AirAcceleration = 6.0f;
    [Export] public float GravityStrength = 32.0f;
    [Export] public float TurnSpeedRadians = 12.0f;
    [Export] public float FloorSnapDistance = 0.55f;
    [Export] public float FloorAngleDegrees = 55.0f;

    [ExportGroup("Foot Planner")]
    [Export] public float SupportForwardLimit = 0.34f;
    [Export] public float SupportBackwardLimit = 0.24f;
    [Export] public float SupportLateralLimit = 0.16f;
    [Export] public float SupportVerticalLimit = 0.24f;
    [Export] public float StepPredictionTime = 0.2f;
    [Export] public float MinimumStepDistance = 0.16f;
    [Export] public float GroundProbeStartHeight = 1.5f;
    [Export] public float GroundProbeDepth = 3.2f;

    [ExportGroup("Foot Swing")]
    [Export] public float StepDurationSeconds = 0.22f;
    [Export] public float StepLiftHeight = 0.14f;
    [Export] public float StepLiftDistanceScale = 0.08f;

    [ExportGroup("Body")]
    [Export] public float PelvisHeight = 0.98f;
    [Export] public float TorsoHeight = 0.72f;
    [Export] public float HipHalfWidth = 0.17f;
    [Export] public float FootForwardOffset = 0.18f;
    [Export] public float UpperLegLength = 0.62f;
    [Export] public float LowerLegLength = 0.62f;
    [Export] public float FootLength = 0.28f;
    [Export] public float FootWidth = 0.12f;
    [Export] public float FootHeight = 0.08f;

    [ExportGroup("Arms")]
    [Export] public float ShoulderHalfWidth = 0.22f;
    [Export] public float ShoulderHeightOffset = 0.18f;
    [Export] public float ShoulderForwardOffset = 0.02f;
    [Export] public float UpperArmLength = 0.44f;
    [Export] public float LowerArmLength = 0.42f;
    [Export] public float HandLength = 0.16f;
    [Export] public float HandWidth = 0.07f;
    [Export] public float HandThickness = 0.07f;
    [Export] public float RelaxedHandSideOffset = 0.28f;
    [Export] public float RelaxedHandDownOffset = 0.16f;
    [Export] public float RelaxedHandForwardOffset = 0.12f;
    [Export] public float ElbowForwardBias = 0.22f;
    [Export] public float ElbowOutwardBias = 0.24f;
    [Export] public float ElbowDownBias = 0.58f;
    [Export] public float ArmFollowSharpness = 18.0f;

    [ExportGroup("Attack")]
    [Export] public float AttackCooldown = 0.58f;
    [Export] public float AttackPower = 1.0f;
    [Export] public float SlashRange = 4.6f;
    [Export] public float SlashLength = 3.4f;
    [Export] public float SlashWidth = 0.62f;
    [Export] public float SlashDepth = 0.58f;
    [Export] public float SlashScorchStrength = 1.0f;
    [Export] public float AttackWindupSeconds = 0.13f;
    [Export] public float AttackReleaseSeconds = 0.11f;
    [Export] public float AttackFollowThroughSeconds = 0.15f;
    [Export] public float AttackRecoverySeconds = 0.19f;
    [Export] public float AttackOriginForwardOffset = 0.2f;
    [Export] public float AttackOriginHeightOffset = 0.08f;
    [Export] public float AttackWindupTorsoTwistDegrees = 22.0f;
    [Export] public float AttackReleaseTorsoTwistDegrees = -18.0f;
    [Export] public float AttackFollowThroughTorsoTwistDegrees = -8.0f;
    [Export] public bool EnableAttackDebugLogs = true;

    [ExportGroup("Camera")]
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public float CameraPivotHeight = 1.55f;
    [Export] public float CameraCollisionMargin = 0.18f;
    [Export(PropertyHint.Range, "-1.4,0.0,0.01")] public float MinimumPitchRadians = -1.05f;
    [Export(PropertyHint.Range, "0.0,1.2,0.01")] public float MaximumPitchRadians = 0.28f;
    [Export(PropertyHint.Range, "-1.2,0.4,0.01")] public float InitialPitchRadians = -0.32f;

    private PlayerInputDriver _inputDriver = null!;
    private LocomotionController _locomotionController = null!;
    private BipedPoseSolver _poseSolver = null!;
    private LocomotionDebug _locomotionDebug = null!;
    private Node3D _pelvis = null!;
    private MeshInstance3D _pelvisMesh = null!;
    private Node3D _torso = null!;
    private MeshInstance3D _torsoMesh = null!;
    private MeshInstance3D _leftUpperLeg = null!;
    private MeshInstance3D _leftLowerLeg = null!;
    private MeshInstance3D _leftFoot = null!;
    private MeshInstance3D _rightUpperLeg = null!;
    private MeshInstance3D _rightLowerLeg = null!;
    private MeshInstance3D _rightFoot = null!;
    private MeshInstance3D _leftUpperArm = null!;
    private MeshInstance3D _leftLowerArm = null!;
    private MeshInstance3D _leftHand = null!;
    private MeshInstance3D _rightUpperArm = null!;
    private MeshInstance3D _rightLowerArm = null!;
    private MeshInstance3D _rightHand = null!;
    private Node3D _weaponGrip = null!;
    private WeaponVisualMount _weaponMount = null!;
    private Node3D _cameraYaw = null!;
    private Node3D _cameraPitch = null!;
    private SpringArm3D _cameraBoom = null!;
    private TerrainWorld _terrainWorld = null!;
    private PlayerAttackMotor _attackMotor = null!;
    private PlayerAttackDefinition _attackDefinition = null!;
    private PlayerAttackDebug _attackDebug = null!;
    private LocomotionTelemetrySnapshot _lastTelemetry = new();

    public override void _Ready()
    {
        AddToGroup("locomotion_telemetry_source");

        UpDirection = Vector3.Up;
        FloorSnapLength = FloorSnapDistance;
        FloorMaxAngle = Mathf.DegToRad(FloorAngleDegrees);

        _pelvis = GetNode<Node3D>(PelvisPath);
        _pelvisMesh = GetNode<MeshInstance3D>(PelvisMeshPath);
        _torso = GetNode<Node3D>(TorsoPath);
        _torsoMesh = GetNode<MeshInstance3D>(TorsoMeshPath);
        _leftUpperLeg = GetNode<MeshInstance3D>(LeftUpperLegPath);
        _leftLowerLeg = GetNode<MeshInstance3D>(LeftLowerLegPath);
        _leftFoot = GetNode<MeshInstance3D>(LeftFootPath);
        _rightUpperLeg = GetNode<MeshInstance3D>(RightUpperLegPath);
        _rightLowerLeg = GetNode<MeshInstance3D>(RightLowerLegPath);
        _rightFoot = GetNode<MeshInstance3D>(RightFootPath);
        _leftUpperArm = GetNode<MeshInstance3D>(LeftUpperArmPath);
        _leftLowerArm = GetNode<MeshInstance3D>(LeftLowerArmPath);
        _leftHand = GetNode<MeshInstance3D>(LeftHandPath);
        _rightUpperArm = GetNode<MeshInstance3D>(RightUpperArmPath);
        _rightLowerArm = GetNode<MeshInstance3D>(RightLowerArmPath);
        _rightHand = GetNode<MeshInstance3D>(RightHandPath);
        _weaponGrip = GetNode<Node3D>(WeaponGripPath);
        _weaponMount = GetNodeOrNull<WeaponVisualMount>(WeaponMountPath);
        _cameraYaw = GetNode<Node3D>(CameraYawPath);
        _cameraPitch = GetNode<Node3D>(CameraPitchPath);
        _cameraBoom = GetNode<SpringArm3D>(CameraBoomPath);
        _locomotionDebug = GetNodeOrNull<LocomotionDebug>(DebugPath);
        _attackDebug = GetNodeOrNull<PlayerAttackDebug>(AttackDebugPath);
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;

        _cameraYaw.TopLevel = true;
        _locomotionDebug?.Set("ForwardLimit", SupportForwardLimit);
        _locomotionDebug?.Set("BackwardLimit", SupportBackwardLimit);
        _locomotionDebug?.Set("LateralLimit", SupportLateralLimit);

        BipedBodyDefinition bodyDefinition = BuildBodyDefinition();
        BipedGrounding grounding = new()
        {
            ProbeStartHeight = GroundProbeStartHeight,
            ProbeDepth = GroundProbeDepth,
            FootLift = 0.015f
        };

        _locomotionController = new LocomotionController(
            bodyDefinition,
            grounding,
            new RootMotionSettings
            {
                MaxGroundSpeed = MaxGroundSpeed,
                Acceleration = GroundAcceleration,
                Deceleration = GroundDeceleration,
                AirAcceleration = AirAcceleration,
                Gravity = GravityStrength,
                TurnSpeedRadians = TurnSpeedRadians,
                FloorSnapVelocity = 0.05f
            },
            new FootPlannerSettings
            {
                ForwardLimit = SupportForwardLimit,
                BackwardLimit = SupportBackwardLimit,
                LateralLimit = SupportLateralLimit,
                VerticalLimit = SupportVerticalLimit,
                StepPredictionTime = StepPredictionTime,
                MinimumStepDistance = MinimumStepDistance
            },
            new FootSwingSettings
            {
                DurationSeconds = StepDurationSeconds,
                LiftHeight = StepLiftHeight,
                DistanceLiftScale = StepLiftDistanceScale
            });

        _poseSolver = new BipedPoseSolver(
            bodyDefinition,
            new BipedPoseRig
            {
                Pelvis = _pelvis,
                PelvisMesh = _pelvisMesh,
                Torso = _torso,
                TorsoMesh = _torsoMesh,
                LeftUpperLeg = _leftUpperLeg,
                LeftLowerLeg = _leftLowerLeg,
                LeftFoot = _leftFoot,
                RightUpperLeg = _rightUpperLeg,
                RightLowerLeg = _rightLowerLeg,
                RightFoot = _rightFoot,
                LeftUpperArm = _leftUpperArm,
                LeftLowerArm = _leftLowerArm,
                LeftHand = _leftHand,
                RightUpperArm = _rightUpperArm,
                RightLowerArm = _rightLowerArm,
                RightHand = _rightHand
            });

        _attackDefinition = BuildAttackDefinition();
        _attackMotor = new PlayerAttackMotor(_attackDefinition);
        EnsureWeaponAttachment();

        float initialYaw = Mathf.Atan2((-GlobalTransform.Basis.Z).X, (-GlobalTransform.Basis.Z).Z);
        _inputDriver = new PlayerInputDriver(
            MouseSensitivity,
            MinimumPitchRadians,
            MaximumPitchRadians,
            initialYaw,
            InitialPitchRadians);

        _cameraBoom.SpringLength = Mathf.Max(_cameraBoom.SpringLength, 4.8f);
        _cameraBoom.ClearExcludedObjects();
        _cameraBoom.AddExcludedObject(GetRid());
        _cameraBoom.Margin = Mathf.Max(_cameraBoom.Margin, CameraCollisionMargin);
        UpdateCameraRig();
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        bool requestAttack =
            Input.MouseMode == Input.MouseModeEnum.Captured &&
            @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };

        _inputDriver.HandleInput(@event);
        if (requestAttack)
        {
            TryStartAttack();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_locomotionController == null)
        {
            return;
        }

        MovementIntent intent = _inputDriver.BuildIntent(_cameraYaw);
        LocomotionFrame frame = _locomotionController.Step(this, intent, delta);
        AttackStepResult attackStep = _attackMotor.Step((float)delta);
        _poseSolver.Apply(frame, attackStep.PresentationState, (float)delta);
        if (attackStep.EmitSlash)
        {
            FireProjectedSlash(attackStep.PresentationState);
        }

        _lastTelemetry = frame.Telemetry;

        UpdateCameraRig();
        _locomotionDebug?.RenderSnapshot(_lastTelemetry);
    }

    public LocomotionTelemetrySnapshot GetLocomotionTelemetrySnapshot()
    {
        return _lastTelemetry;
    }

    private void UpdateCameraRig()
    {
        _cameraYaw.GlobalPosition = GlobalPosition + (Vector3.Up * CameraPivotHeight);
        _cameraYaw.GlobalRotation = new Vector3(0.0f, _inputDriver?.Yaw ?? 0.0f, 0.0f);
        _cameraPitch.Rotation = new Vector3(_inputDriver?.Pitch ?? InitialPitchRadians, 0.0f, 0.0f);
    }

    private void TryStartAttack()
    {
        if (_attackMotor.TryStartAttack())
        {
            if (EnableAttackDebugLogs)
            {
                GD.Print(
                    $"Attack start | cooldown {_attackDefinition.AttackCooldown:0.00}s | range {_attackDefinition.SlashRange:0.00} | slash {_attackDefinition.SlashLength:0.00}x{_attackDefinition.SlashWidth:0.00}x{_attackDefinition.SlashDepth:0.00} | scorch {_attackDefinition.ScorchStrength:0.00}");
            }

            return;
        }

        if (EnableAttackDebugLogs)
        {
            GD.Print(
                $"Attack blocked | active {_attackMotor.IsActive} | cooldown {_attackMotor.CooldownRemaining:0.00}s");
        }
    }

    private void EnsureWeaponAttachment()
    {
        if (_weaponGrip == null)
        {
            GD.PushError($"Weapon setup failed | WeaponGrip not found at '{WeaponGripPath}' on {GetPath()}.");
            return;
        }

        GD.Print($"Weapon setup | weapon grip found at {_weaponGrip.GetPath()}");

        PackedScene swordAsset = ResourceLoader.Load<PackedScene>(WeaponAssetPath);
        if (swordAsset == null)
        {
            GD.PushError($"Weapon setup failed | sword asset could not be loaded from '{WeaponAssetPath}'.");
            return;
        }

        GD.Print($"Weapon setup | sword asset loaded from {WeaponAssetPath}");

        if (_weaponMount == null)
        {
            GD.PushError($"Weapon setup failed | WeaponVisualMount not found at '{WeaponMountPath}'.");
            return;
        }

        Node existingSword = _weaponMount.GetNodeOrNull<Node>("Sword");
        if (existingSword != null)
        {
            _weaponMount.RemoveChild(existingSword);
            existingSword.QueueFree();
        }

        Node loadedSwordScene = swordAsset.Instantiate();
        if (loadedSwordScene == null)
        {
            GD.PushError($"Weapon setup failed | instancing '{WeaponAssetPath}' returned null.");
            return;
        }

        Node3D swordRoot = new()
        {
            Name = "Sword"
        };
        _weaponMount.AddChild(swordRoot);
        swordRoot.AddChild(loadedSwordScene);

        if (loadedSwordScene is Node3D loadedSwordRoot3D)
        {
            loadedSwordRoot3D.Position = Vector3.Zero;
            loadedSwordRoot3D.Rotation = Vector3.Zero;
            loadedSwordRoot3D.Scale = Vector3.One;
        }

        GD.Print($"Weapon setup | sword instance created at {swordRoot.GetPath()}");

        if (!_weaponMount.RefreshMount())
        {
            GD.PushError($"Weapon setup failed | WeaponVisualMount could not fit sword child under {_weaponMount.GetPath()}.");
        }
    }

    private void FireProjectedSlash(AttackPresentationState presentationState)
    {
        Vector3 queryOrigin = _weaponGrip.GlobalPosition +
            (-GlobalTransform.Basis.Z * AttackOriginForwardOffset) +
            (Vector3.Up * AttackOriginHeightOffset);
        Vector3 forward = LocomotionMath.SafeNormalized(
            LocomotionMath.Flatten(-_torso.GlobalTransform.Basis.Z),
            LocomotionMath.SafeNormalized(LocomotionMath.Flatten(-GlobalTransform.Basis.Z), Vector3.Forward));
        Vector3 queryEnd = queryOrigin + (forward * _attackDefinition.SlashRange);

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(queryOrigin, queryEnd);
        query.CollideWithAreas = false;
        query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };

        Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        Node collider = result.Count > 0
            ? result["collider"].AsGodotObject() as Node
            : null;
        bool terrainHit = collider?.GetParent() is TerrainChunk;

        Vector3 hitPoint = terrainHit ? (Vector3)result["position"] : queryEnd;
        Vector3 hitNormal = terrainHit
            ? ((Vector3)result["normal"]).Normalized()
            : Vector3.Up;
        Vector3 slashDirection = LocomotionMath.ProjectOntoPlane(-_torso.GlobalTransform.Basis.X, hitNormal);
        slashDirection = LocomotionMath.SafeNormalized(slashDirection, forward);

        if (terrainHit && _terrainWorld != null)
        {
            VoxelSlashEdit slashEdit = new(
                _terrainWorld.ResolveSlashCenter(hitPoint, hitNormal, _attackDefinition.SlashDepth),
                slashDirection,
                hitNormal,
                _attackDefinition.SlashLength,
                _attackDefinition.SlashWidth,
                _attackDefinition.SlashDepth,
                BuildSlashDensityDelta(_attackDefinition),
                Mathf.Clamp(_attackDefinition.ScorchStrength * _attackDefinition.AttackPower, 0.0f, 1.5f),
                Mathf.Max(_attackDefinition.SlashWidth * 0.35f, _terrainWorld.VoxelSize));
            _terrainWorld.ApplySlash(slashEdit);
        }

        _attackDebug?.ShowSlashQuery(queryOrigin, queryEnd, terrainHit, hitPoint, hitNormal, slashDirection);

        if (EnableAttackDebugLogs)
        {
            GD.Print(
                $"Attack release | terrain_hit {terrainHit} | phase {presentationState.Phase} | hit {hitPoint} | normal {hitNormal} | slash_dir {slashDirection} | params range {_attackDefinition.SlashRange:0.00} length {_attackDefinition.SlashLength:0.00} width {_attackDefinition.SlashWidth:0.00} depth {_attackDefinition.SlashDepth:0.00} scorch {_attackDefinition.ScorchStrength:0.00}");
        }
    }

    private BipedBodyDefinition BuildBodyDefinition()
    {
        BipedLegDefinition CreateLeg(FootSide side)
        {
            float sideSign = side.Sign();
            return new BipedLegDefinition
            {
                Side = side,
                HipOffset = new Vector3(HipHalfWidth * sideSign, 0.0f, 0.0f),
                HomeOffset = new Vector3(HipHalfWidth * sideSign, 0.0f, FootForwardOffset),
                UpperLegLength = UpperLegLength,
                LowerLegLength = LowerLegLength,
                FootLength = FootLength,
                FootWidth = FootWidth,
                FootHeight = FootHeight
            };
        }

        BipedArmDefinition CreateArm(FootSide side)
        {
            float sideSign = side.Sign();
            return new BipedArmDefinition
            {
                Side = side,
                ShoulderOffset = new Vector3(ShoulderHalfWidth * sideSign, ShoulderHeightOffset, ShoulderForwardOffset),
                RelaxedHandOffset = new Vector3(
                    RelaxedHandSideOffset * sideSign,
                    -RelaxedHandDownOffset,
                    RelaxedHandForwardOffset),
                UpperArmLength = UpperArmLength,
                LowerArmLength = LowerArmLength,
                HandLength = HandLength,
                HandWidth = HandWidth,
                HandThickness = HandThickness,
                ElbowForwardBias = ElbowForwardBias,
                ElbowOutwardBias = ElbowOutwardBias,
                ElbowDownBias = ElbowDownBias
            };
        }

        return new BipedBodyDefinition
        {
            LeftLeg = CreateLeg(FootSide.Left),
            RightLeg = CreateLeg(FootSide.Right),
            LeftArm = CreateArm(FootSide.Left),
            RightArm = CreateArm(FootSide.Right),
            PelvisHeight = PelvisHeight,
            TorsoHeight = TorsoHeight,
            ArmFollowSharpness = ArmFollowSharpness,
            AttackWindupTorsoTwistDegrees = AttackWindupTorsoTwistDegrees,
            AttackReleaseTorsoTwistDegrees = AttackReleaseTorsoTwistDegrees,
            AttackFollowThroughTorsoTwistDegrees = AttackFollowThroughTorsoTwistDegrees
        };
    }

    private PlayerAttackDefinition BuildAttackDefinition()
    {
        return new PlayerAttackDefinition
        {
            SlashRange = SlashRange,
            SlashLength = SlashLength,
            SlashWidth = SlashWidth,
            SlashDepth = SlashDepth,
            ScorchStrength = SlashScorchStrength,
            AttackCooldown = AttackCooldown,
            AttackPower = AttackPower,
            WindupDuration = AttackWindupSeconds,
            ReleaseDuration = AttackReleaseSeconds,
            FollowThroughDuration = AttackFollowThroughSeconds,
            RecoveryDuration = AttackRecoverySeconds
        };
    }

    private static float BuildSlashDensityDelta(PlayerAttackDefinition definition)
    {
        return -Mathf.Max(0.18f, definition.SlashDepth * 4.25f * definition.AttackPower);
    }
}
