using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Motion;

public sealed class MotionProfilerSnapshot
{
    public double TotalFrameMs { get; init; }
    public IReadOnlyDictionary<string, double> StageMs { get; init; } = null!;
    public IReadOnlyDictionary<string, float> Metrics { get; init; } = null!;
}

public sealed class MotionProfiler
{
    private readonly Dictionary<string, double> _stageMs = new();
    private readonly Dictionary<string, float> _metrics = new();
    private string _activeStage = string.Empty;
    private ulong _frameStartUsec;
    private ulong _stageStartUsec;

    public void BeginFrame()
    {
        _stageMs.Clear();
        _metrics.Clear();
        _activeStage = string.Empty;
        _frameStartUsec = Time.GetTicksUsec();
        _stageStartUsec = _frameStartUsec;
    }

    public void BeginStage(string stageName)
    {
        FlushActiveStage();
        _activeStage = stageName;
        _stageStartUsec = Time.GetTicksUsec();
    }

    public void EndStage()
    {
        FlushActiveStage();
    }

    public void SetMetric(string name, float value)
    {
        _metrics[name] = value;
    }

    public MotionProfilerSnapshot CaptureSnapshot()
    {
        FlushActiveStage();
        double totalFrameMs = (Time.GetTicksUsec() - _frameStartUsec) / 1000.0;
        return new MotionProfilerSnapshot
        {
            TotalFrameMs = totalFrameMs,
            StageMs = new Dictionary<string, double>(_stageMs),
            Metrics = new Dictionary<string, float>(_metrics)
        };
    }

    private void FlushActiveStage()
    {
        if (string.IsNullOrEmpty(_activeStage))
        {
            return;
        }

        double elapsedMs = (Time.GetTicksUsec() - _stageStartUsec) / 1000.0;
        if (_stageMs.TryGetValue(_activeStage, out double previous))
        {
            _stageMs[_activeStage] = previous + elapsedMs;
        }
        else
        {
            _stageMs[_activeStage] = elapsedMs;
        }

        _activeStage = string.Empty;
    }
}
