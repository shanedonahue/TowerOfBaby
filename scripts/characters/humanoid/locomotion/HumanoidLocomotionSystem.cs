using Godot;
using TowerOfBaby.Characters.Humanoid.Control;
using TowerOfBaby.Characters.Humanoid.Definition;
using TowerOfBaby.Characters.Humanoid.Rig;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    private readonly CharacterBody3D _body;
    private readonly HumanoidRig _rig;
    private readonly HumanoidBodySpec _spec;
    private readonly MotionSkeletonDefinition _motionDefinition;
    private readonly HumanoidLocomotionConfig _settings;
    private readonly MotionProfiler _profiler = new();
    private readonly MotionDebugTelemetry _telemetry;
    private readonly HumanoidLegMotionRuntime _leftLeg;
    private readonly HumanoidLegMotionRuntime _rightLeg;

    private float _gaitPhase;
    private float _locomotionBlend;
    private Vector3 _locomotionForward = Vector3.Forward;
    private Vector3 _lastFacingForward = Vector3.Forward;
    private Vector3 _lastPlanarVelocity = Vector3.Zero;

    public MotionProfilerSnapshot LastProfilerSnapshot { get; private set; } = CreateEmptySnapshot();

    public HumanoidLocomotionSystem(
        CharacterBody3D body,
        HumanoidRig rig,
        HumanoidBodySpec spec,
        MotionSkeletonDefinition motionDefinition,
        HumanoidLocomotionConfig settings)
    {
        _body = body;
        _rig = rig;
        _spec = spec;
        _motionDefinition = motionDefinition;
        _settings = settings;
        _telemetry = new MotionDebugTelemetry(settings.EnableMotionDiagnostics, settings.MotionDiagnosticLogIntervalSeconds);
        _leftLeg = CreateLegRuntime(rig.LeftLeg, "left_leg", "left_foot", 0.0f);
        _rightLeg = CreateLegRuntime(rig.RightLeg, "right_leg", "right_foot", 0.5f);
    }

    public void Update(float delta, HumanoidMovementIntent intent, Vector3 desiredDirection, float cameraPitch)
    {
        _profiler.BeginFrame();

        float moveAmount = Mathf.Clamp(intent.Move.Length(), 0.0f, 1.0f);
        float sprintBlend = moveAmount > 0.05f && intent.Sprint ? 1.0f : 0.0f;
        float maxSpeed = _settings.MoveSpeed * Mathf.Lerp(1.0f, _settings.SprintSpeedMultiplier, sprintBlend);
        Vector3 locomotionDirection = ResolveLocomotionDirection(desiredDirection, moveAmount, delta);
        float targetSpeed = maxSpeed * moveAmount;
        Vector3 targetVelocity = locomotionDirection * targetSpeed;

        _profiler.BeginStage("movement");
        UpdateBodyMotion(delta, moveAmount, locomotionDirection, targetVelocity, cameraPitch);
        _profiler.EndStage();

        _profiler.SetMetric("move_amount", moveAmount);
        _profiler.SetMetric("target_speed", targetSpeed);
        _profiler.SetMetric(
            "heading_error_deg",
            desiredDirection.LengthSquared() > 0.0001f
                ? Mathf.RadToDeg(locomotionDirection.AngleTo(desiredDirection))
                : 0.0f);

        if (_body.IsOnFloor())
        {
            _profiler.BeginStage("gait_model");
            HumanoidGroundMotionFrame frame = BuildGroundMotionFrame(delta, locomotionDirection, targetVelocity, sprintBlend, maxSpeed);
            _profiler.EndStage();

            _profiler.BeginStage("contacts");
            UpdateLegContactState(_leftLeg, frame, delta);
            UpdateLegContactState(_rightLeg, frame, delta);
            _profiler.EndStage();

            _profiler.BeginStage("pelvis");
            UpdatePelvisPose(frame, delta);
            _profiler.EndStage();

            _profiler.BeginStage("limbs");
            ApplyLegPose(_leftLeg, frame.Forward);
            ApplyLegPose(_rightLeg, frame.Forward);
            _profiler.EndStage();

            _profiler.BeginStage("upper_body");
            UpdateUpperBodyPose(frame, delta, cameraPitch);
            _profiler.EndStage();
        }
        else
        {
            _profiler.BeginStage("airborne");
            UpdateAirborneRig(delta, locomotionDirection, cameraPitch);
            _profiler.EndStage();
        }

        LastProfilerSnapshot = _profiler.CaptureSnapshot();
        _telemetry.Update(delta, "humanoid", LastProfilerSnapshot);
    }

    // CharacterBody motion stays in this file so the controller entrypoint reads as a small orchestration layer.
    private void UpdateBodyMotion(float delta, float moveAmount, Vector3 desiredDirection, Vector3 targetVelocity, float cameraPitch)
    {
        Vector3 horizontalVelocity = new(_body.Velocity.X, 0.0f, _body.Velocity.Z);
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
        Vector3 facingTarget = ResolveBodyForward(desiredDirection, facingVelocity);

        if (facingTarget.LengthSquared() > 0.0001f)
        {
            _lastFacingForward = facingTarget.Normalized();
            float targetYaw = Mathf.Atan2(-_lastFacingForward.X, -_lastFacingForward.Z);
            float rotationSharpness = moveAmount > 0.05f
                ? _settings.RotationSpeed * 2.0f
                : _settings.RotationSpeed;
            _rig.VisualRoot.Rotation = new Vector3(
                0.0f,
                Mathf.LerpAngle(_rig.VisualRoot.Rotation.Y, targetYaw, DampFactor(rotationSharpness, delta)),
                0.0f);
        }

        _profiler.SetMetric("body_speed", facingVelocity.Length());
        _profiler.SetMetric("body_grounded", _body.IsOnFloor() ? 1.0f : 0.0f);
        _profiler.SetMetric("camera_pitch", cameraPitch);
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
}
