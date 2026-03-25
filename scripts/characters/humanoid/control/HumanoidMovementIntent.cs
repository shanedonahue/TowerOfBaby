using Godot;

namespace TowerOfBaby.Characters.Humanoid.Control;

public struct HumanoidMovementIntent
{
    public Vector2 Move;
    public Vector2 LookDelta;
    public bool Jump;
    public bool Sprint;
    public bool PrimaryAction;
    public bool SecondaryAction;
    public bool PrimaryActionPressed;
    public bool SecondaryActionPressed;
}
