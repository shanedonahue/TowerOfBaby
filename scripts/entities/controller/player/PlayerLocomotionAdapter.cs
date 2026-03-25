using Godot;
using TowerOfBaby.Entities.Body.Biped;
using TowerOfBaby.Entities.Motion;

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
    [Export] public NodePath CameraYawPath = new("CameraYaw");
    [Export] public NodePath CameraPitchPath = new("CameraYaw/CameraPitch");
    [Export] public NodePath CameraBoomPath = new("CameraYaw/CameraPitch/SpringArm3D");
    [Export] public NodePath DebugPath = new("LocomotionDebug");

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
    [Export] public float SupportForwardLimit = 0.32f;
    [Export] public float SupportBackwardLimit = 0.24f;
    [Export] public float SupportLateralLimit = 0.18f;
    [Export] public float SupportVerticalLimit = 0.24f;
    [Export] public float StepPredictionTime = 0.2f;
    [Export] public float MinimumStepDistance = 0.16f;
    [Export] public float GroundProbeStartHeight = 1.5f;
    [Export] public float GroundProbeDepth = 3.2f;

    [ExportGroup("Foot Swing")]
    [Export] public float StepDurationSeconds = 0.22f;
    [Export] public float StepLiftHeight = 0.16f;
    [Export] public float StepLiftDistanceScale = 0.08f;

    [ExportGroup("Body")]
    [Export] public float PelvisHeight = 0.98f;
    [Export] public float TorsoHeight = 0.72f;
    [Export] public float HipHalfWidth = 0.18f;
    [Export] public float FootForwardOffset = 0.16f;
    [Export] public float UpperLegLength = 0.62f;
    [Export] public float LowerLegLength = 0.62f;
    [Export] public float FootLength = 0.28f;
    [Export] public float FootWidth = 0.12f;
    [Export] public float FootHeight = 0.08f;

    [ExportGroup("Camera")]
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public float CameraPivotHeight = 1.55f;
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
    private Node3D _cameraYaw = null!;
    private Node3D _cameraPitch = null!;
    private SpringArm3D _cameraBoom = null!;
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
        _cameraYaw = GetNode<Node3D>(CameraYawPath);
        _cameraPitch = GetNode<Node3D>(CameraPitchPath);
        _cameraBoom = GetNode<SpringArm3D>(CameraBoomPath);
        _locomotionDebug = GetNodeOrNull<LocomotionDebug>(DebugPath);

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
                RightFoot = _rightFoot
            });

        float initialYaw = Mathf.Atan2((-GlobalTransform.Basis.Z).X, (-GlobalTransform.Basis.Z).Z);
        _inputDriver = new PlayerInputDriver(
            MouseSensitivity,
            MinimumPitchRadians,
            MaximumPitchRadians,
            initialYaw,
            InitialPitchRadians);

        _cameraBoom.SpringLength = Mathf.Max(_cameraBoom.SpringLength, 4.8f);
        UpdateCameraRig();
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        _inputDriver.HandleInput(@event);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_locomotionController == null)
        {
            return;
        }

        MovementIntent intent = _inputDriver.BuildIntent(_cameraYaw);
        LocomotionFrame frame = _locomotionController.Step(this, intent, delta);
        _poseSolver.Apply(frame, (float)delta);
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

        return new BipedBodyDefinition
        {
            LeftLeg = CreateLeg(FootSide.Left),
            RightLeg = CreateLeg(FootSide.Right),
            PelvisHeight = PelvisHeight,
            TorsoHeight = TorsoHeight
        };
    }
}
