using Godot;

namespace TowerOfBaby.Characters.Humanoid.Control;

public interface IHumanoidControlSource
{
    void Initialize();
    void HandleInput(InputEvent @event);
    void HandleUnhandledInput(InputEvent @event);
    bool ConsumeMouseCaptureToggle();
    HumanoidMovementIntent BuildIntent();
}
