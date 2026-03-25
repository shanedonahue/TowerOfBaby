using Godot;

namespace TowerOfBaby.Characters.Humanoid.Control;

public sealed class PlayerHumanoidControlSource : IHumanoidControlSource
{
    private Vector2 _accumulatedLookDelta;
    private bool _toggleMouseCaptureRequested;
    private bool _primaryActionPressed;
    private bool _secondaryActionPressed;

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

        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                _primaryActionPressed = true;
            }
            else if (mouseButton.ButtonIndex == MouseButton.Right)
            {
                _secondaryActionPressed = true;
            }
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

    public HumanoidMovementIntent BuildIntent()
    {
        HumanoidMovementIntent intent = new()
        {
            Move = Input.GetVector("move_left", "move_right", "move_forward", "move_back"),
            LookDelta = _accumulatedLookDelta,
            Jump = Input.IsActionPressed("move_jump"),
            Sprint = Input.IsActionPressed("move_sprint"),
            PrimaryAction = Input.IsMouseButtonPressed(MouseButton.Left),
            SecondaryAction = Input.IsMouseButtonPressed(MouseButton.Right),
            PrimaryActionPressed = _primaryActionPressed,
            SecondaryActionPressed = _secondaryActionPressed
        };

        _accumulatedLookDelta = Vector2.Zero;
        _primaryActionPressed = false;
        _secondaryActionPressed = false;
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
