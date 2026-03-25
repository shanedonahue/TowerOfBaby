using Godot;
using Godot.Collections;

public partial class HumanoidController : CharacterBody3D
{
    public enum HumanoidControlMode
    {
        Player = 0,
        Benchmark = 1,
        RandomWalk = 2
    }

    [Export] public int BodySeed = 12345;
    [Export] public bool RandomizeBodySeedOnReady;
    [Export] public HumanoidControlMode ControlMode = HumanoidControlMode.Player;
    [Export] public bool ActsAsTerrainTracker = true;
    [Export] public bool EnableFollowCamera = true;
    [Export] public float MoveSpeed = 7.0f;
    [Export] public float SprintSpeedMultiplier = 1.55f;
    [Export] public float Acceleration = 10.0f;
    [Export] public float Deceleration = 18.0f;
    [Export] public float AirAcceleration = 3.0f;
    [Export] public float TurnResponsiveness = 16.0f;
    [Export] public float RotationSpeed = 9.0f;
    [Export] public float GravityScale = 1.0f;
    [Export] public float FallGravityMultiplier = 1.7f;
    [Export] public float GroundStickVelocity = 1.6f;
    [Export] public float FloorSnapDistance = 0.9f;
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public float CameraPitchMin = -0.9f;
    [Export] public float CameraPitchMax = 0.35f;
    [Export] public float CameraDistance = 6.5f;
    [Export] public float CameraDistanceMin = 3.2f;
    [Export] public float CameraDistanceMax = 11.0f;
    [Export] public float CameraZoomStep = 0.8f;
    [Export] public float CameraZoomLerpSpeed = 10.0f;
    [Export] public float CameraFarDistance = 220.0f;
    [ExportGroup("Terrain Interaction")]
    [Export] public bool EnableTerrainDig = true;
    [Export] public float DigRange = 4.5f;
    [Export] public float DigOriginHeightOffset = 1.1f;
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
    private TerrainWorld _terrainWorld = null!;

    private IHumanoidControlSource _controlSource = null!;
    private HumanoidBodySpec _bodySpec = null!;
    private HumanoidSkeleton _skeleton = null!;
    private HumanoidRig _rig = null!;
    private HumanoidLocomotionController _locomotion = null!;

    private float _cameraYaw;
    private float _cameraPitch = -0.35f;
    private float _targetCameraDistance;

    public override void _Ready()
    {
        if (ActsAsTerrainTracker)
        {
            AddToGroup("terrain_tracker");
        }

        int controlSeed = RandomizeBodySeedOnReady
            ? (int)GD.Randi()
            : BodySeed;
        _controlSource = CreateControlSource(controlSeed);
        _controlSource.Initialize();

        BuildCameraRig();
        BuildGeneratedBody();
        _terrainWorld = GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _targetCameraDistance = CameraDistance;

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

        if (@event is InputEventMouseButton mouseButton &&
            mouseButton.Pressed &&
            EnableFollowCamera &&
            !Input.IsKeyPressed(Key.Ctrl))
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                AdjustCameraDistance(-CameraZoomStep);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                AdjustCameraDistance(CameraZoomStep);
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        _controlSource.HandleUnhandledInput(@event);
    }

    public override void _PhysicsProcess(double delta)
    {
        MovementIntent intent = _controlSource.BuildIntent();
        ApplyLookIntent(intent.LookDelta);
        TryHandleTerrainDig(intent);
        UpdateCameraDistance((float)delta);

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

        if (EnableFollowCamera)
        {
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
        }

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
                SprintSpeedMultiplier = SprintSpeedMultiplier,
                Acceleration = Acceleration,
                Deceleration = Deceleration,
                AirAcceleration = AirAcceleration,
                TurnResponsiveness = TurnResponsiveness,
                RotationSpeed = RotationSpeed,
                GravityScale = GravityScale,
                FallGravityMultiplier = FallGravityMultiplier,
                GroundStickVelocity = GroundStickVelocity,
                FootProbeDistance = FootProbeDistance,
                WalkCycleSpeed = WalkCycleSpeed,
                RunCycleSpeed = RunCycleSpeed
            });
    }

    private IHumanoidControlSource CreateControlSource(int seed)
    {
        HumanoidControlMode mode = UseBenchmarkControl
            ? HumanoidControlMode.Benchmark
            : ControlMode;

        return mode switch
        {
            HumanoidControlMode.Benchmark => new BenchmarkHumanoidControlSource(
                BenchmarkForwardDurationSeconds,
                BenchmarkCircleDurationSeconds,
                BenchmarkCircleYawRadiansPerSecond),
            HumanoidControlMode.RandomWalk => new RandomWalkHumanoidControlSource(seed),
            _ => new PlayerHumanoidControlSource()
        };
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

    private void AdjustCameraDistance(float delta)
    {
        _targetCameraDistance = Mathf.Clamp(_targetCameraDistance + delta, CameraDistanceMin, CameraDistanceMax);
    }

    private void UpdateCameraDistance(float delta)
    {
        if (!EnableFollowCamera || _springArm == null)
        {
            return;
        }

        _springArm.SpringLength = Mathf.Lerp(_springArm.SpringLength, _targetCameraDistance, 1.0f - Mathf.Exp(-CameraZoomLerpSpeed * delta));
    }

    private void TryHandleTerrainDig(MovementIntent intent)
    {
        if (!EnableTerrainDig ||
            !intent.PrimaryActionPressed ||
            _terrainWorld == null ||
            !_terrainWorld.InitialLoadComplete ||
            Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            return;
        }

        Vector3 origin = GlobalPosition + new Vector3(0.0f, DigOriginHeightOffset, 0.0f);
        Vector3 direction = -_yawPivot.GlobalTransform.Basis.Z;
        Vector3 target = origin + (direction * DigRange);

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, target);
        query.CollideWithAreas = false;
        query.Exclude = new Array<Rid> { GetRid() };

        Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            return;
        }

        if (!result.TryGetValue("collider", out Variant colliderVariant) ||
            colliderVariant.Obj is not StaticBody3D hitBody ||
            hitBody.GetParent() is not TerrainChunk)
        {
            return;
        }

        Vector3 hitPoint = (Vector3)result["position"];
        Vector3 hitNormal = ((Vector3)result["normal"]).Normalized();
        Vector3 brushCenter = _terrainWorld.ResolveBrushCenter(hitPoint, hitNormal, additive: false);
        _terrainWorld.ApplyBrush(brushCenter, additive: false);
    }
}
