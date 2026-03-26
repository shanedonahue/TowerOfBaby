using Godot;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Controller.Player;

public sealed class PlayerInputDriver
{
    private readonly float _mouseSensitivity;
    private readonly float _minimumPitch;
    private readonly float _maximumPitch;

    private float _yaw;
    private float _pitch;

    public PlayerInputDriver(
        float mouseSensitivity,
        float minimumPitch,
        float maximumPitch,
        float initialYaw,
        float initialPitch)
    {
        _mouseSensitivity = mouseSensitivity;
        _minimumPitch = minimumPitch;
        _maximumPitch = maximumPitch;
        _yaw = initialYaw;
        _pitch = Mathf.Clamp(initialPitch, _minimumPitch, _maximumPitch);
    }

    public float Yaw => _yaw;
    public float Pitch => _pitch;

    public void HandleInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton &&
            mouseButton.Pressed &&
            mouseButton.ButtonIndex == MouseButton.Left &&
            Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        if (Input.MouseMode != Input.MouseModeEnum.Captured ||
            @event is not InputEventMouseMotion mouseMotion)
        {
            return;
        }

        _yaw -= mouseMotion.Relative.X * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - (mouseMotion.Relative.Y * _mouseSensitivity), _minimumPitch, _maximumPitch);
    }

    public MovementIntent BuildIntent(Node3D cameraYawNode)
    {
        Vector2 axis = GetMoveAxis();
        Vector3 cameraForward = LocomotionMath.SafeNormalized(
            LocomotionMath.Flatten(-cameraYawNode.GlobalTransform.Basis.Z),
            Vector3.Forward);
        Vector3 cameraRight = LocomotionMath.SafeNormalized(
            LocomotionMath.Flatten(cameraYawNode.GlobalTransform.Basis.X),
            Vector3.Right);

        Vector3 moveDirection = axis.LengthSquared() > 0.0001f
            ? LocomotionMath.SafeNormalized((cameraRight * axis.X) + (cameraForward * axis.Y), cameraForward)
            : Vector3.Zero;
        Vector3 facingDirection = axis.LengthSquared() > 0.0001f
            ? moveDirection
            : Vector3.Zero;

        return new MovementIntent(moveDirection, Mathf.Clamp(axis.Length(), 0.0f, 1.0f), facingDirection);
    }

    private static Vector2 GetMoveAxis()
    {
        float left = IsMoveKeyPressed(Key.A, Key.Left) ? 1.0f : 0.0f;
        float right = IsMoveKeyPressed(Key.D, Key.Right) ? 1.0f : 0.0f;
        float forward = IsMoveKeyPressed(Key.W, Key.Up) ? 1.0f : 0.0f;
        float back = IsMoveKeyPressed(Key.S, Key.Down) ? 1.0f : 0.0f;

        Vector2 axis = new(right - left, forward - back);
        return axis.LengthSquared() > 1.0f
            ? axis.Normalized()
            : axis;
    }

    private static bool IsMoveKeyPressed(Key primary, Key alternate)
    {
        return Input.IsPhysicalKeyPressed(primary) || Input.IsPhysicalKeyPressed(alternate);
    }
}
