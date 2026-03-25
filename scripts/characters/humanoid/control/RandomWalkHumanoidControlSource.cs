using Godot;

namespace TowerOfBaby.Characters.Humanoid.Control;

public sealed class RandomWalkHumanoidControlSource : IHumanoidControlSource
{
    private readonly RandomNumberGenerator _rng = new();
    private readonly float _turnRateRadiansPerSecond;
    private readonly float _pauseChance;
    private readonly float _strafeChance;
    private double _segmentTimeRemaining;
    private float _currentYawRate;
    private Vector2 _currentMove;

    public RandomWalkHumanoidControlSource(
        int seed,
        float turnRateRadiansPerSecond = 0.9f,
        float pauseChance = 0.18f,
        float strafeChance = 0.22f)
    {
        _turnRateRadiansPerSecond = turnRateRadiansPerSecond;
        _pauseChance = pauseChance;
        _strafeChance = strafeChance;
        _rng.Seed = (ulong)Mathf.Abs(seed == 0 ? 1 : seed);
    }

    public void Initialize()
    {
        PickNextSegment();
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
        _segmentTimeRemaining -= fixedStep;
        if (_segmentTimeRemaining <= 0.0)
        {
            PickNextSegment();
        }

        return new HumanoidMovementIntent
        {
            Move = _currentMove,
            LookDelta = new Vector2(-_currentYawRate / 0.0025f * fixedStep, 0.0f),
            Sprint = _currentMove.LengthSquared() > 0.8f
        };
    }

    private void PickNextSegment()
    {
        _segmentTimeRemaining = _rng.RandfRange(1.2f, 3.8f);

        if (_rng.Randf() < _pauseChance)
        {
            _currentMove = Vector2.Zero;
            _currentYawRate = _rng.RandfRange(-_turnRateRadiansPerSecond, _turnRateRadiansPerSecond);
            return;
        }

        float forwardAmount = _rng.RandfRange(0.65f, 1.0f);
        float lateralAmount = _rng.Randf() < _strafeChance
            ? _rng.RandfRange(-0.45f, 0.45f)
            : 0.0f;
        _currentMove = new Vector2(lateralAmount, -forwardAmount).Normalized();
        _currentYawRate = _rng.RandfRange(-_turnRateRadiansPerSecond, _turnRateRadiansPerSecond);
    }
}
