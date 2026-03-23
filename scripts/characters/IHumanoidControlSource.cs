using Godot;

public interface IHumanoidControlSource
{
    void Initialize();
    void HandleInput(InputEvent @event);
    void HandleUnhandledInput(InputEvent @event);
    bool ConsumeMouseCaptureToggle();
    MovementIntent BuildIntent();
}
