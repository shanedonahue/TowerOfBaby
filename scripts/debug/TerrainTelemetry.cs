using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TowerOfBaby.Debugging;

public enum TerrainTelemetryProbe
{
    LodTransition = 0,
    Grass = 1,
    Deform = 2,
    Persistence = 3
}

public readonly record struct TerrainTelemetryBootstrap(
    bool AutoStartCapture,
    double CaptureIntervalSeconds,
    bool ExpensiveMetricsEnabled,
    bool EnableLodTransitionProbe,
    bool EnableGrassProbe,
    bool EnableDeformProbe,
    bool EnablePersistenceProbe);

public readonly record struct TerrainTelemetryModeSnapshot(
    bool CaptureEnabledOnReady,
    bool CaptureSessionActive,
    double CaptureIntervalSeconds,
    bool ExpensiveMetricsEnabled,
    bool LodTransitionProbeEnabled,
    bool GrassProbeEnabled,
    bool DeformProbeEnabled,
    bool PersistenceProbeEnabled)
{
    public bool AnyProbeEnabled =>
        LodTransitionProbeEnabled ||
        GrassProbeEnabled ||
        DeformProbeEnabled ||
        PersistenceProbeEnabled;

    public string ModeLabel =>
        TerrainTelemetry.BuildModeLabel(CaptureSessionActive, AnyProbeEnabled);

    public string ProbeSummary =>
        TerrainTelemetry.BuildProbeSummary(this);
}

// Shared telemetry policy:
// - live snapshot metrics stay cheap and memory-only
// - capture sessions and probe traces are opt-in
// - file output happens at session/end-of-run boundaries, not from gameplay _Process
public static class TerrainTelemetry
{
    private const string ProfilingRoot = "user://profiling";
    private static readonly object Sync = new();
    private static readonly Dictionary<TerrainTelemetryProbe, List<string>> ProbeBuffers = new()
    {
        [TerrainTelemetryProbe.LodTransition] = new List<string>(),
        [TerrainTelemetryProbe.Grass] = new List<string>(),
        [TerrainTelemetryProbe.Deform] = new List<string>(),
        [TerrainTelemetryProbe.Persistence] = new List<string>()
    };

    private static readonly HashSet<TerrainTelemetryProbe> PendingProbeOverrides = new();
    private static TerrainTelemetryModeSnapshot _modeSnapshot = new(
        CaptureEnabledOnReady: false,
        CaptureSessionActive: false,
        CaptureIntervalSeconds: 1.0,
        ExpensiveMetricsEnabled: false,
        LodTransitionProbeEnabled: false,
        GrassProbeEnabled: false,
        DeformProbeEnabled: false,
        PersistenceProbeEnabled: false);
    private static DateTime _configuredAtUtc = DateTime.UtcNow;
    private static int _configurationVersion = 1;
    private static bool _probeArtifactsFlushed;

    public static bool ShouldAutoStartCapture
    {
        get
        {
            lock (Sync)
            {
                return _modeSnapshot.CaptureEnabledOnReady;
            }
        }
    }

    public static double CaptureIntervalSeconds
    {
        get
        {
            lock (Sync)
            {
                return _modeSnapshot.CaptureIntervalSeconds;
            }
        }
    }

    public static bool ExpensiveMetricsEnabled
    {
        get
        {
            lock (Sync)
            {
                return _modeSnapshot.ExpensiveMetricsEnabled;
            }
        }
    }

    public static int ConfigurationVersion
    {
        get
        {
            lock (Sync)
            {
                return _configurationVersion;
            }
        }
    }

    public static TerrainTelemetryModeSnapshot Configure(TerrainTelemetryBootstrap bootstrap)
    {
        lock (Sync)
        {
            bool autoStartCapture = bootstrap.AutoStartCapture;
            double captureIntervalSeconds = SanitizeCaptureIntervalSeconds(bootstrap.CaptureIntervalSeconds);
            bool expensiveMetricsEnabled = bootstrap.ExpensiveMetricsEnabled;
            bool lodTransitionProbeEnabled = bootstrap.EnableLodTransitionProbe;
            bool grassProbeEnabled = bootstrap.EnableGrassProbe;
            bool deformProbeEnabled = bootstrap.EnableDeformProbe;
            bool persistenceProbeEnabled = bootstrap.EnablePersistenceProbe;

            foreach (TerrainTelemetryProbe probe in PendingProbeOverrides)
            {
                ApplyProbeOverride(
                    probe,
                    ref lodTransitionProbeEnabled,
                    ref grassProbeEnabled,
                    ref deformProbeEnabled,
                    ref persistenceProbeEnabled);
            }

            ApplyCommandLineOverrides(
                ref autoStartCapture,
                ref captureIntervalSeconds,
                ref expensiveMetricsEnabled,
                ref lodTransitionProbeEnabled,
                ref grassProbeEnabled,
                ref deformProbeEnabled,
                ref persistenceProbeEnabled);

            TerrainTelemetryModeSnapshot nextSnapshot = new(
                CaptureEnabledOnReady: autoStartCapture,
                CaptureSessionActive: _modeSnapshot.CaptureSessionActive,
                CaptureIntervalSeconds: captureIntervalSeconds,
                ExpensiveMetricsEnabled: expensiveMetricsEnabled,
                LodTransitionProbeEnabled: lodTransitionProbeEnabled,
                GrassProbeEnabled: grassProbeEnabled,
                DeformProbeEnabled: deformProbeEnabled,
                PersistenceProbeEnabled: persistenceProbeEnabled);

            if (!nextSnapshot.Equals(_modeSnapshot))
            {
                _modeSnapshot = nextSnapshot;
                _configurationVersion++;
            }

            foreach (List<string> lines in ProbeBuffers.Values)
            {
                lines.Clear();
            }

            _configuredAtUtc = DateTime.UtcNow;
            _probeArtifactsFlushed = false;
            return _modeSnapshot;
        }
    }

    public static TerrainTelemetryModeSnapshot GetModeSnapshot()
    {
        lock (Sync)
        {
            return _modeSnapshot;
        }
    }

    public static void EnableProbe(TerrainTelemetryProbe probe)
    {
        lock (Sync)
        {
            PendingProbeOverrides.Add(probe);
            if (IsProbeEnabledNoLock(probe))
            {
                return;
            }

            _modeSnapshot = probe switch
            {
                TerrainTelemetryProbe.LodTransition => _modeSnapshot with { LodTransitionProbeEnabled = true },
                TerrainTelemetryProbe.Grass => _modeSnapshot with { GrassProbeEnabled = true },
                TerrainTelemetryProbe.Deform => _modeSnapshot with { DeformProbeEnabled = true },
                TerrainTelemetryProbe.Persistence => _modeSnapshot with { PersistenceProbeEnabled = true },
                _ => _modeSnapshot
            };
            _configurationVersion++;
        }
    }

    public static void SetCaptureSessionActive(bool active)
    {
        lock (Sync)
        {
            if (_modeSnapshot.CaptureSessionActive == active)
            {
                return;
            }

            _modeSnapshot = _modeSnapshot with { CaptureSessionActive = active };
            _configurationVersion++;
        }
    }

    public static bool IsProbeEnabled(TerrainTelemetryProbe probe)
    {
        lock (Sync)
        {
            return IsProbeEnabledNoLock(probe);
        }
    }

    public static void AppendProbeLine(TerrainTelemetryProbe probe, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (Sync)
        {
            if (!IsProbeEnabledNoLock(probe))
            {
                return;
            }

            ProbeBuffers[probe].Add(line);
            _probeArtifactsFlushed = false;
        }
    }

    public static void FlushProbeArtifacts()
    {
        Dictionary<TerrainTelemetryProbe, List<string>> pending = new();
        TerrainTelemetryModeSnapshot modeSnapshot;
        string timestamp;

        lock (Sync)
        {
            if (_probeArtifactsFlushed)
            {
                return;
            }

            modeSnapshot = _modeSnapshot;
            timestamp = _configuredAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            foreach ((TerrainTelemetryProbe probe, List<string> lines) in ProbeBuffers)
            {
                if (!IsProbeEnabledNoLock(probe) || lines.Count == 0)
                {
                    continue;
                }

                pending[probe] = new List<string>(lines);
            }

            _probeArtifactsFlushed = true;
        }

        if (pending.Count == 0)
        {
            return;
        }

        EnsureProfilingRootExists();
        foreach ((TerrainTelemetryProbe probe, List<string> lines) in pending)
        {
            string relativePath = $"{ProfilingRoot}/probe_{GetProbeFileStem(probe)}_{timestamp}.log";
            using FileAccess file = FileAccess.Open(relativePath, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PushWarning($"TerrainTelemetry could not write probe log {relativePath}.");
                continue;
            }

            StringBuilder builder = new();
            builder.AppendLine("TowerOfBaby Telemetry Probe");
            builder.AppendLine($"UTC: {DateTime.UtcNow:O}");
            builder.AppendLine($"Probe: {GetProbeDisplayName(probe)}");
            builder.AppendLine($"ProfilingMode: {modeSnapshot.ModeLabel}");
            builder.AppendLine($"CaptureIntervalSeconds: {modeSnapshot.CaptureIntervalSeconds:0.00}");
            builder.AppendLine($"ExpensiveMetricsEnabled: {(modeSnapshot.ExpensiveMetricsEnabled ? "true" : "false")}");
            builder.AppendLine($"EnabledProbes: {modeSnapshot.ProbeSummary}");
            builder.AppendLine();
            foreach (string line in lines)
            {
                builder.AppendLine(line);
            }

            file.StoreString(builder.ToString());
            GD.Print($"Telemetry probe written to {ProjectSettings.GlobalizePath(relativePath)}");
        }
    }

    public static string BuildModeLabel(bool captureSessionActive, bool anyProbeEnabled)
    {
        if (captureSessionActive && anyProbeEnabled)
        {
            return "capture session + subsystem traces";
        }

        if (captureSessionActive)
        {
            return "capture session";
        }

        if (anyProbeEnabled)
        {
            return "cheap/live HUD + subsystem traces";
        }

        return "cheap/live HUD only";
    }

    public static string BuildProbeSummary(TerrainTelemetryModeSnapshot modeSnapshot)
    {
        List<string> enabledProbes = new();
        if (modeSnapshot.LodTransitionProbeEnabled)
        {
            enabledProbes.Add("lod_transition");
        }

        if (modeSnapshot.GrassProbeEnabled)
        {
            enabledProbes.Add("grass");
        }

        if (modeSnapshot.DeformProbeEnabled)
        {
            enabledProbes.Add("deform");
        }

        if (modeSnapshot.PersistenceProbeEnabled)
        {
            enabledProbes.Add("persistence");
        }

        return enabledProbes.Count == 0
            ? "none"
            : string.Join(", ", enabledProbes);
    }

    private static void ApplyCommandLineOverrides(
        ref bool autoStartCapture,
        ref double captureIntervalSeconds,
        ref bool expensiveMetricsEnabled,
        ref bool lodTransitionProbeEnabled,
        ref bool grassProbeEnabled,
        ref bool deformProbeEnabled,
        ref bool persistenceProbeEnabled)
    {
        foreach (string rawArg in OS.GetCmdlineArgs())
        {
            string arg = rawArg?.Trim() ?? string.Empty;
            if (arg.Length == 0)
            {
                continue;
            }

            if (string.Equals(arg, "--telemetry-capture", StringComparison.OrdinalIgnoreCase))
            {
                autoStartCapture = true;
                continue;
            }

            if (TryReadArgumentValue(arg, "--telemetry-capture-interval", out string intervalValue) &&
                double.TryParse(intervalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedInterval))
            {
                captureIntervalSeconds = SanitizeCaptureIntervalSeconds(parsedInterval);
                continue;
            }

            if (string.Equals(arg, "--telemetry-expensive", StringComparison.OrdinalIgnoreCase))
            {
                expensiveMetricsEnabled = true;
                continue;
            }

            if (TryReadArgumentValue(arg, "--telemetry-probes", out string probesValue) ||
                TryReadArgumentValue(arg, "--telemetry-probe", out probesValue))
            {
                foreach (string probeName in probesValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    ApplyProbeOverride(
                        probeName,
                        ref lodTransitionProbeEnabled,
                        ref grassProbeEnabled,
                        ref deformProbeEnabled,
                        ref persistenceProbeEnabled);
                }
            }
        }
    }

    private static void ApplyProbeOverride(
        TerrainTelemetryProbe probe,
        ref bool lodTransitionProbeEnabled,
        ref bool grassProbeEnabled,
        ref bool deformProbeEnabled,
        ref bool persistenceProbeEnabled)
    {
        switch (probe)
        {
            case TerrainTelemetryProbe.LodTransition:
                lodTransitionProbeEnabled = true;
                break;
            case TerrainTelemetryProbe.Grass:
                grassProbeEnabled = true;
                break;
            case TerrainTelemetryProbe.Deform:
                deformProbeEnabled = true;
                break;
            case TerrainTelemetryProbe.Persistence:
                persistenceProbeEnabled = true;
                break;
        }
    }

    private static void ApplyProbeOverride(
        string probeName,
        ref bool lodTransitionProbeEnabled,
        ref bool grassProbeEnabled,
        ref bool deformProbeEnabled,
        ref bool persistenceProbeEnabled)
    {
        switch ((probeName ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "all":
                lodTransitionProbeEnabled = true;
                grassProbeEnabled = true;
                deformProbeEnabled = true;
                persistenceProbeEnabled = true;
                break;
            case "lod":
            case "lod_transition":
            case "terrain_lod":
                lodTransitionProbeEnabled = true;
                break;
            case "grass":
                grassProbeEnabled = true;
                break;
            case "deform":
                deformProbeEnabled = true;
                break;
            case "persistence":
                persistenceProbeEnabled = true;
                break;
        }
    }

    private static bool TryReadArgumentValue(string arg, string prefix, out string value)
    {
        string expectedPrefix = prefix + "=";
        if (arg.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[expectedPrefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsProbeEnabledNoLock(TerrainTelemetryProbe probe)
    {
        return probe switch
        {
            TerrainTelemetryProbe.LodTransition => _modeSnapshot.LodTransitionProbeEnabled,
            TerrainTelemetryProbe.Grass => _modeSnapshot.GrassProbeEnabled,
            TerrainTelemetryProbe.Deform => _modeSnapshot.DeformProbeEnabled,
            TerrainTelemetryProbe.Persistence => _modeSnapshot.PersistenceProbeEnabled,
            _ => false
        };
    }

    private static double SanitizeCaptureIntervalSeconds(double captureIntervalSeconds)
    {
        return Math.Clamp(captureIntervalSeconds, 0.1, 30.0);
    }

    private static void EnsureProfilingRootExists()
    {
        string absoluteRoot = ProjectSettings.GlobalizePath(ProfilingRoot);
        if (!DirAccess.DirExistsAbsolute(absoluteRoot))
        {
            DirAccess.MakeDirRecursiveAbsolute(absoluteRoot);
        }
    }

    private static string GetProbeDisplayName(TerrainTelemetryProbe probe)
    {
        return probe switch
        {
            TerrainTelemetryProbe.LodTransition => "terrain LOD transition trace",
            TerrainTelemetryProbe.Grass => "grass trace",
            TerrainTelemetryProbe.Deform => "deform trace",
            TerrainTelemetryProbe.Persistence => "persistence trace",
            _ => probe.ToString()
        };
    }

    private static string GetProbeFileStem(TerrainTelemetryProbe probe)
    {
        return probe switch
        {
            TerrainTelemetryProbe.LodTransition => "lod_transition",
            TerrainTelemetryProbe.Grass => "grass",
            TerrainTelemetryProbe.Deform => "deform",
            TerrainTelemetryProbe.Persistence => "persistence",
            _ => probe.ToString().ToLowerInvariant()
        };
    }
}
