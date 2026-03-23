using Godot;

public partial class HumanoidController : CharacterBody3D
{
    [Export] public int BodySeed = 12345;
    [Export] public bool RandomizeBodySeedOnReady;
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
    [Export] public float WalkCycleSpeed = 2.35f;
    [Export] public float RunCycleSpeed = 4.1f;
    [Export] public bool UseBenchmarkControl;
    [Export] public double BenchmarkForwardDurationSeconds = 10.0;
    [Export] public double BenchmarkCircleDurationSeconds = 24.0;
    [Export] public float BenchmarkCircleYawRadiansPerSecond = 0.32f;

    private Node3D _yawPivot = null!;
    private Node3D _cameraPitchPivot = null!;
    private SpringArm3D _springArm = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collision = null!;

    private IHumanoidControlSource _controlSource = null!;
    private HumanoidBodySpec _bodySpec = null!;
    private HumanoidSkeleton _skeleton = null!;
    private HumanoidRig _rig = null!;
    private HumanoidLocomotionController _locomotion = null!;

    private float _cameraYaw;
    private float _cameraPitch = -0.35f;

    public override void _Ready()
    {
        AddToGroup("terrain_tracker");

        _controlSource = UseBenchmarkControl
            ? new BenchmarkHumanoidControlSource(
                BenchmarkForwardDurationSeconds,
                BenchmarkCircleDurationSeconds,
                BenchmarkCircleYawRadiansPerSecond)
            : new PlayerHumanoidControlSource();
        _controlSource.Initialize();

        BuildCameraRig();
        BuildGeneratedBody();

        FloorSnapLength = FloorSnapDistance;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Input(InputEvent @event)
    {
        _controlSource.HandleInput(@event);
        if (_controlSource.ConsumeMouseCaptureToggle())
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

        _controlSource.HandleUnhandledInput(@event);
    }

    public override void _PhysicsProcess(double delta)
    {
        MovementIntent intent = _controlSource.BuildIntent();
        ApplyLookIntent(intent.LookDelta);

        Basis cameraBasis = _yawPivot.GlobalTransform.Basis;
        Vector3 desiredDirection = (
            cameraBasis.Z * intent.Move.Y +
            cameraBasis.X * intent.Move.X).Normalized();

        _locomotion.Update((float)delta, intent, desiredDirection, _cameraPitch);
    }

    private void BuildCameraRig()
    {
        _yawPivot = new Node3D { Name = "YawPivot" };
        AddChild(_yawPivot);

        _cameraPitchPivot = new Node3D { Name = "CameraPitchPivot" };
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
    }

    private void BuildGeneratedBody()
    {
        int seed = RandomizeBodySeedOnReady
            ? (int)GD.Randi()
            : BodySeed;

        _bodySpec = HumanoidBodyGenerator.Generate(seed);
        _skeleton = HumanoidSkeletonBuilder.Build(_bodySpec);
        _rig = HumanoidRigBuilder.Build(this, _collision, _bodySpec, _skeleton);

        _cameraPitchPivot.Position = new Vector3(0.0f, _bodySpec.EyeHeight, 0.0f);
        _yawPivot.Rotation = new Vector3(0.0f, _cameraYaw, 0.0f);
        _cameraPitchPivot.Rotation = new Vector3(_cameraPitch, 0.0f, 0.0f);

        _locomotion = new HumanoidLocomotionController(
            this,
            _rig,
            _bodySpec,
            new HumanoidLocomotionSettings
            {
                MoveSpeed = MoveSpeed,
                Acceleration = Acceleration,
                AirAcceleration = AirAcceleration,
                RotationSpeed = RotationSpeed,
                GravityScale = GravityScale,
                FootProbeDistance = FootProbeDistance,
                WalkCycleSpeed = WalkCycleSpeed,
                RunCycleSpeed = RunCycleSpeed
            });
    }

    private void ApplyLookIntent(Vector2 lookDelta)
    {
        if (lookDelta == Vector2.Zero)
        {
            return;
        }

        _cameraYaw -= lookDelta.X * MouseSensitivity;
        _cameraPitch = Mathf.Clamp(_cameraPitch - lookDelta.Y * MouseSensitivity, CameraPitchMin, CameraPitchMax);
        _yawPivot.Rotation = new Vector3(0.0f, _cameraYaw, 0.0f);
        _cameraPitchPivot.Rotation = new Vector3(_cameraPitch, 0.0f, 0.0f);
    }
}
