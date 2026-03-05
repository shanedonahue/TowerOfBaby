using Godot;

public partial class DebugCamera : Camera3D
{
    [Export] public float Speed = 20f;
    [Export] public float MouseSensitivity = 0.002f;

    private float _yaw;
    private float _pitch;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            _yaw -= motion.Relative.X * MouseSensitivity;
            _pitch -= motion.Relative.Y * MouseSensitivity;

            _pitch = Mathf.Clamp(_pitch, -1.5f, 1.5f);

            Rotation = new Vector3(_pitch, _yaw, 0);
        }

        if (e.IsActionPressed("ui_cancel"))
            Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _Process(double delta)
    {
        Vector3 dir = Vector3.Zero;
		
		float shiftBoost = Input.IsKeyPressed(Key.Shift) ? 5 : 1;
        if (Input.IsKeyPressed(Key.W)) dir -= Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) dir += Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) dir -= Transform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) dir += Transform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) dir += Transform.Basis.Y;
        if (Input.IsKeyPressed(Key.Q)) dir -= Transform.Basis.Y;

        Position += dir.Normalized() * shiftBoost * Speed * (float)delta;
    }
	// float shiftBoost = 0;
}