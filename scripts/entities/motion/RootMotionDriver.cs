using Godot;

namespace TowerOfBaby.Entities.Motion;

public sealed class RootMotionDriver
{
    private readonly RootMotionSettings _settings;

    private Vector3 _facingDirection = Vector3.Forward;
    private Vector3 _previousPosition;
    private bool _hasPreviousPosition;

    public RootMotionDriver(RootMotionSettings settings)
    {
        _settings = settings;
    }

    public void Reset(Vector3 position, Vector3 facingDirection)
    {
        _previousPosition = position;
        _facingDirection = LocomotionMath.SafeNormalized(LocomotionMath.Flatten(facingDirection), Vector3.Forward);
        _hasPreviousPosition = true;
    }

    public RootMotionFrame Step(CharacterBody3D body, MovementIntent intent, double delta)
    {
        float dt = Mathf.Max((float)delta, 0.0001f);
        if (!_hasPreviousPosition)
        {
            Reset(body.GlobalPosition, -body.GlobalTransform.Basis.Z);
        }

        bool wasGrounded = body.IsOnFloor();
        Vector3 floorNormal = wasGrounded ? body.GetFloorNormal() : Vector3.Up;
        Vector3 desiredVelocity = intent.HasMovement
            ? intent.MoveDirection * (_settings.MaxGroundSpeed * Mathf.Clamp(intent.MoveAmount, 0.0f, 1.0f))
            : Vector3.Zero;
        desiredVelocity = LocomotionMath.ProjectOntoPlane(desiredVelocity, floorNormal);

        Vector3 velocity = body.Velocity;
        Vector3 horizontalVelocity = new Vector3(velocity.X, 0.0f, velocity.Z);
        Vector3 desiredHorizontalVelocity = new Vector3(desiredVelocity.X, 0.0f, desiredVelocity.Z);
        float acceleration = desiredHorizontalVelocity.LengthSquared() > horizontalVelocity.LengthSquared()
            ? _settings.Acceleration
            : _settings.Deceleration;
        if (!wasGrounded)
        {
            acceleration = _settings.AirAcceleration;
        }

        horizontalVelocity = horizontalVelocity.MoveToward(desiredHorizontalVelocity, acceleration * dt);
        velocity.X = horizontalVelocity.X;
        velocity.Z = horizontalVelocity.Z;

        if (wasGrounded && velocity.Y <= 0.0f)
        {
            velocity.Y = -_settings.FloorSnapVelocity;
        }
        else
        {
            velocity.Y -= _settings.Gravity * dt;
        }

        body.Velocity = velocity;
        body.MoveAndSlide();

        Vector3 actualVelocity = (body.GlobalPosition - _previousPosition) / dt;
        _previousPosition = body.GlobalPosition;

        Vector3 targetFacing = intent.FacingDirection.LengthSquared() > 0.0001f
            ? intent.FacingDirection
            : (horizontalVelocity.LengthSquared() > 0.0001f ? horizontalVelocity : _facingDirection);
        _facingDirection = LocomotionMath.RotatePlanarTowards(_facingDirection, targetFacing, _settings.TurnSpeedRadians * dt);
        body.Basis = LocomotionMath.CreateBasisFromForward(_facingDirection, Vector3.Up);

        bool isGrounded = body.IsOnFloor();
        Vector3 groundNormal = isGrounded ? body.GetFloorNormal() : Vector3.Up;
        return new RootMotionFrame(
            body.GlobalPosition,
            desiredHorizontalVelocity,
            new Vector3(actualVelocity.X, 0.0f, actualVelocity.Z),
            _facingDirection,
            groundNormal,
            isGrounded);
    }
}
