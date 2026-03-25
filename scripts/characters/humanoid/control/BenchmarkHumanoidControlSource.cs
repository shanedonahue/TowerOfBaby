using Godot;

namespace TowerOfBaby.Characters.Humanoid.Control;

public sealed class BenchmarkHumanoidControlSource : IHumanoidControlSource
{
    private readonly double _forwardDurationSeconds;
    private readonly double _circleDurationSeconds;
    private readonly float _circleYawRadiansPerSecond;
    private double _elapsedSeconds;

    public BenchmarkHumanoidControlSource(
        double forwardDurationSeconds = 10.0,
        double circleDurationSeconds = 24.0,
        float circleYawRadiansPerSecond = 0.32f)
    {
        _forwardDurationSeconds = forwardDurationSeconds;
        _circleDurationSeconds = circleDurationSeconds;
        _circleYawRadiansPerSecond = circleYawRadiansPerSecond;
    }

    public void Initialize()
    {
    }

    public void HandleInput(InputEvent @event)
    {
    }

    public void HandleUnhandledInput(InputEvent @event)
    {
    }

    public bool ConsumeMouseCaptureToggle()
    {
        return false;
    }

    public HumanoidMovementIntent BuildIntent()
    {
        const float fixedStep = 1.0f / 60.0f;
        _elapsedSeconds += fixedStep;

        HumanoidMovementIntent intent = new()
        {
            Move = new Vector2(0.0f, -1.0f),
            Sprint = true
        };

        if (_elapsedSeconds > _forwardDurationSeconds && _elapsedSeconds <= (_forwardDurationSeconds + _circleDurationSeconds))
        {
            intent.LookDelta = new Vector2(-_circleYawRadiansPerSecond / 0.0025f * fixedStep, 0.0f);
        }

        return intent;
    }
}
