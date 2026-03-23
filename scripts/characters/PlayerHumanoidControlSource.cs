using Godot;

public sealed class PlayerHumanoidControlSource : IHumanoidControlSource
{
    private Vector2 _accumulatedLookDelta;
    private bool _toggleMouseCaptureRequested;

    public void Initialize()
    {
        EnsureInputMap();
    }

    public void HandleInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            _toggleMouseCaptureRequested = true;
        }
    }

    public void HandleUnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mouseMotion)
        {
            _accumulatedLookDelta += mouseMotion.Relative;
        }
    }

    public bool ConsumeMouseCaptureToggle()
    {
        bool requested = _toggleMouseCaptureRequested;
        _toggleMouseCaptureRequested = false;
        return requested;
    }

    public MovementIntent BuildIntent()
    {
        MovementIntent intent = new()
        {
            Move = Input.GetVector("move_left", "move_right", "move_forward", "move_back"),
            LookDelta = _accumulatedLookDelta,
            Jump = Input.IsActionPressed("move_jump"),
            Sprint = Input.IsActionPressed("move_sprint"),
            PrimaryAction = Input.IsMouseButtonPressed(MouseButton.Left),
            SecondaryAction = Input.IsMouseButtonPressed(MouseButton.Right)
        };

        _accumulatedLookDelta = Vector2.Zero;
        return intent;
    }

    private static void EnsureInputMap()
    {
        AddActionIfMissing("move_forward", Key.W, Key.Up);
        AddActionIfMissing("move_back", Key.S, Key.Down);
        AddActionIfMissing("move_left", Key.A, Key.Left);
        AddActionIfMissing("move_right", Key.D, Key.Right);
        AddActionIfMissing("move_jump", Key.Space);
        AddActionIfMissing("move_sprint", Key.Shift);
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
}
