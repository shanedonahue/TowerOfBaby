using Godot;
using System.Collections.Generic;
using System.Text;

namespace TowerOfBaby.Motion;

public sealed class MotionDebugTelemetry
{
    private readonly bool _enabled;
    private readonly double _logIntervalSeconds;
    private double _elapsedSinceLog;

    public MotionDebugTelemetry(bool enabled, double logIntervalSeconds)
    {
        _enabled = enabled;
        _logIntervalSeconds = logIntervalSeconds <= 0.0 ? 0.25 : logIntervalSeconds;
    }

    public void Update(double delta, string channel, MotionProfilerSnapshot snapshot)
    {
        if (!_enabled)
        {
            return;
        }

        _elapsedSinceLog += delta;
        if (_elapsedSinceLog < _logIntervalSeconds)
        {
            return;
        }

        _elapsedSinceLog = 0.0;
        GD.Print(BuildMessage(channel, snapshot));
    }

    private static string BuildMessage(string channel, MotionProfilerSnapshot snapshot)
    {
        StringBuilder builder = new();
        builder.Append("motion[");
        builder.Append(channel);
        builder.Append("] frame_ms=");
        builder.Append(snapshot.TotalFrameMs.ToString("0.00"));

        AppendMap(builder, " stages=", snapshot.StageMs);
        AppendMap(builder, " metrics=", snapshot.Metrics);
        return builder.ToString();
    }

    private static void AppendMap<TValue>(StringBuilder builder, string prefix, IReadOnlyDictionary<string, TValue> values)
    {
        builder.Append(prefix);
        builder.Append('{');

        List<string> keys = new(values.Keys);
        keys.Sort();
        for (int i = 0; i < keys.Count; i++)
        {
            string key = keys[i];
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(key);
            builder.Append('=');
            builder.Append(values[key]);
        }

        builder.Append('}');
    }
}
