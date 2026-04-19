using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TowerOfBaby.Entities.Motion;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.Debugging;

public partial class TelemetryCaptureSession : Node
{
    private const string ProfilingRoot = "user://profiling";

    [Export] public NodePath TerrainWorldPath = new();
    [Export(PropertyHint.Range, "0.1,30,0.1")] public double CaptureIntervalSeconds = 1.0;

    private readonly List<float> _frameTimesMs = new();
    private readonly List<CaptureSample> _samples = new();
    private TerrainWorld _terrainWorld = null!;
    private ILocomotionTelemetrySource _locomotionTelemetrySource = null!;
    private DateTime _captureStartedUtc;
    private double _elapsedSeconds;
    private double _sampleAccumulator;
    private double _sampleFrameMsAccum;
    private float _sampleMaxFrameMs;
    private int _sampleFrameCount;
    private float _previousManagedHeapMiB = float.NaN;
    private int _previousGen0CollectionCount = -1;
    private int _previousGen1CollectionCount = -1;
    private int _previousGen2CollectionCount = -1;

    public bool IsCapturing { get; private set; }

    public override void _Process(double delta)
    {
        if (!IsCapturing)
        {
            return;
        }

        _elapsedSeconds += delta;
        _sampleAccumulator += delta;

        float frameMs = (float)(delta * 1000.0);
        _frameTimesMs.Add(frameMs);
        _sampleFrameMsAccum += frameMs;
        _sampleMaxFrameMs = Mathf.Max(_sampleMaxFrameMs, frameMs);
        _sampleFrameCount++;

        if (_sampleAccumulator >= CaptureIntervalSeconds)
        {
            RecordCaptureSample();
        }
    }

    public bool StartCapture(string reason = "manual_toggle")
    {
        if (IsCapturing)
        {
            return false;
        }

        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _locomotionTelemetrySource = ResolveLocomotionTelemetrySource();
        CaptureIntervalSeconds = Math.Clamp(TerrainTelemetry.CaptureIntervalSeconds, 0.1, 30.0);
        _captureStartedUtc = DateTime.UtcNow;
        _elapsedSeconds = 0.0;
        _sampleAccumulator = 0.0;
        _sampleFrameMsAccum = 0.0;
        _sampleMaxFrameMs = 0.0f;
        _sampleFrameCount = 0;
        _previousManagedHeapMiB = float.NaN;
        _previousGen0CollectionCount = -1;
        _previousGen1CollectionCount = -1;
        _previousGen2CollectionCount = -1;
        _frameTimesMs.Clear();
        _samples.Clear();
        IsCapturing = true;
        TerrainTelemetry.SetCaptureSessionActive(true);
        GD.Print($"Telemetry capture started ({reason}) at {_captureStartedUtc:O}");
        return true;
    }

    public string StopCapture(string reason = "manual_toggle")
    {
        if (!IsCapturing)
        {
            return string.Empty;
        }

        RecordCaptureSample();
        TerrainTelemetryModeSnapshot modeSnapshot = TerrainTelemetry.GetModeSnapshot();
        IsCapturing = false;
        TerrainTelemetry.SetCaptureSessionActive(false);

        string relativePath = WriteArtifact(modeSnapshot, reason);
        if (!string.IsNullOrEmpty(relativePath))
        {
            GD.Print($"Telemetry capture written to {ProjectSettings.GlobalizePath(relativePath)}");
        }

        return relativePath;
    }

    public override void _ExitTree()
    {
        if (IsCapturing)
        {
            StopCapture("tree_exit");
        }
    }

    private void RecordCaptureSample()
    {
        if (!IsCapturing)
        {
            return;
        }

        float averageFrameMs = _sampleFrameCount > 0
            ? (float)(_sampleFrameMsAccum / _sampleFrameCount)
            : 0.0f;

        TerrainWorldProfileSnapshot terrainSnapshot = (_terrainWorld ??= GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld)
            ?.GetProfileSnapshot();
        LocomotionTelemetrySnapshot locomotionSnapshot = (_locomotionTelemetrySource ??= ResolveLocomotionTelemetrySource())
            ?.GetLocomotionTelemetrySnapshot();

        float managedHeapMiB = BytesToMiB(GC.GetTotalMemory(false));
        RuntimeGcMetrics expensiveMetrics = null;
        if (TerrainTelemetry.ExpensiveMetricsEnabled)
        {
            int gen0Count = GC.CollectionCount(0);
            int gen1Count = GC.CollectionCount(1);
            int gen2Count = GC.CollectionCount(2);
            expensiveMetrics = new RuntimeGcMetrics
            {
                ManagedHeapDeltaMiB = float.IsNaN(_previousManagedHeapMiB)
                    ? 0.0f
                    : managedHeapMiB - _previousManagedHeapMiB,
                Gen0CollectionsDelta = _previousGen0CollectionCount < 0 ? 0 : gen0Count - _previousGen0CollectionCount,
                Gen1CollectionsDelta = _previousGen1CollectionCount < 0 ? 0 : gen1Count - _previousGen1CollectionCount,
                Gen2CollectionsDelta = _previousGen2CollectionCount < 0 ? 0 : gen2Count - _previousGen2CollectionCount
            };
            _previousGen0CollectionCount = gen0Count;
            _previousGen1CollectionCount = gen1Count;
            _previousGen2CollectionCount = gen2Count;
        }

        _previousManagedHeapMiB = managedHeapMiB;
        _samples.Add(new CaptureSample
        {
            TimeSeconds = _elapsedSeconds,
            AverageFrameMs = averageFrameMs,
            MaxFrameMs = _sampleMaxFrameMs,
            Fps = averageFrameMs > Mathf.Epsilon
                ? 1000.0f / averageFrameMs
                : 0.0f,
            WorkingSetMiB = BytesToMiB(System.Environment.WorkingSet),
            ManagedHeapMiB = managedHeapMiB,
            ExpensiveMetrics = expensiveMetrics,
            Terrain = terrainSnapshot,
            Locomotion = CaptureLocomotionSample.From(locomotionSnapshot)
        });

        _sampleAccumulator = 0.0;
        _sampleFrameMsAccum = 0.0;
        _sampleMaxFrameMs = 0.0f;
        _sampleFrameCount = 0;
    }

    private string WriteArtifact(TerrainTelemetryModeSnapshot modeSnapshot, string stopReason)
    {
        string timestamp = _captureStartedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string relativePath = $"{ProfilingRoot}/capture_{timestamp}.json";
        string absoluteRoot = ProjectSettings.GlobalizePath(ProfilingRoot);
        if (!DirAccess.DirExistsAbsolute(absoluteRoot))
        {
            DirAccess.MakeDirRecursiveAbsolute(absoluteRoot);
        }

        CaptureArtifact artifact = new()
        {
            ArtifactVersion = 1,
            ArtifactType = "tower_of_baby_telemetry_capture",
            StartedUtc = _captureStartedUtc.ToString("O", CultureInfo.InvariantCulture),
            EndedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ProfilingMode = CaptureProfilingMode.From(modeSnapshot),
            Summary = CaptureSummary.From(
                durationSeconds: _elapsedSeconds,
                frameTimesMs: _frameTimesMs,
                samples: _samples,
                expensiveMetricsEnabled: modeSnapshot.ExpensiveMetricsEnabled),
            Samples = new List<CaptureSample>(_samples),
            StopReason = stopReason
        };

        JsonSerializerOptions serializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        serializerOptions.Converters.Add(new JsonStringEnumConverter());
        using FileAccess file = FileAccess.Open(relativePath, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushWarning($"TelemetryCaptureSession could not write capture artifact {relativePath}.");
            return string.Empty;
        }

        file.StoreString(JsonSerializer.Serialize(artifact, serializerOptions));
        return relativePath;
    }

    private static float BytesToMiB(long bytes)
    {
        return bytes / (1024.0f * 1024.0f);
    }

    private ILocomotionTelemetrySource ResolveLocomotionTelemetrySource()
    {
        return GetTree().GetFirstNodeInGroup("locomotion_telemetry_source") as ILocomotionTelemetrySource;
    }

    private sealed class CaptureArtifact
    {
        public int ArtifactVersion { get; init; }
        public string ArtifactType { get; init; } = string.Empty;
        public string StartedUtc { get; init; } = string.Empty;
        public string EndedUtc { get; init; } = string.Empty;
        public CaptureProfilingMode ProfilingMode { get; init; } = new();
        public CaptureSummary Summary { get; init; } = new();
        public List<CaptureSample> Samples { get; init; } = new();
        public string StopReason { get; init; } = string.Empty;
    }

    private sealed class CaptureProfilingMode
    {
        public string Mode { get; init; } = string.Empty;
        public double CaptureIntervalSeconds { get; init; }
        public bool ExpensiveMetricsEnabled { get; init; }
        public string EnabledProbes { get; init; } = string.Empty;
        public bool LodTransitionTraceEnabled { get; init; }
        public bool GrassTraceEnabled { get; init; }
        public bool DeformTraceEnabled { get; init; }
        public bool PersistenceTraceEnabled { get; init; }

        public static CaptureProfilingMode From(TerrainTelemetryModeSnapshot modeSnapshot)
        {
            return new CaptureProfilingMode
            {
                Mode = modeSnapshot.ModeLabel,
                CaptureIntervalSeconds = modeSnapshot.CaptureIntervalSeconds,
                ExpensiveMetricsEnabled = modeSnapshot.ExpensiveMetricsEnabled,
                EnabledProbes = modeSnapshot.ProbeSummary,
                LodTransitionTraceEnabled = modeSnapshot.LodTransitionProbeEnabled,
                GrassTraceEnabled = modeSnapshot.GrassProbeEnabled,
                DeformTraceEnabled = modeSnapshot.DeformProbeEnabled,
                PersistenceTraceEnabled = modeSnapshot.PersistenceProbeEnabled
            };
        }
    }

    private sealed class CaptureSummary
    {
        public double DurationSeconds { get; init; }
        public int SampleCount { get; init; }
        public float AverageFps { get; init; }
        public float AverageFrameMs { get; init; }
        public float P95FrameMs { get; init; }
        public float MaxFrameMs { get; init; }
        public float PeakWorkingSetMiB { get; init; }
        public float PeakManagedHeapMiB { get; init; }
        public int TotalGen0Collections { get; init; }
        public int TotalGen1Collections { get; init; }
        public int TotalGen2Collections { get; init; }
        public int PeakActiveChunks { get; init; }
        public int PeakDesiredChunks { get; init; }
        public int PeakPendingLoads { get; init; }
        public int PeakPendingMeshBuilds { get; init; }
        public int PeakPendingMeshCommits { get; init; }
        public int PeakPersistenceQueueDepth { get; init; }
        public TerrainWorldProfileSnapshot LatestTerrainSnapshot { get; init; }

        public static CaptureSummary From(
            double durationSeconds,
            List<float> frameTimesMs,
            List<CaptureSample> samples,
            bool expensiveMetricsEnabled)
        {
            int totalGen0Collections = 0;
            int totalGen1Collections = 0;
            int totalGen2Collections = 0;
            float peakWorkingSetMiB = 0.0f;
            float peakManagedHeapMiB = 0.0f;
            int peakActiveChunks = 0;
            int peakDesiredChunks = 0;
            int peakPendingLoads = 0;
            int peakPendingMeshBuilds = 0;
            int peakPendingMeshCommits = 0;
            int peakPersistenceQueueDepth = 0;
            TerrainWorldProfileSnapshot latestTerrainSnapshot = null;

            foreach (CaptureSample sample in samples)
            {
                peakWorkingSetMiB = Mathf.Max(peakWorkingSetMiB, sample.WorkingSetMiB);
                peakManagedHeapMiB = Mathf.Max(peakManagedHeapMiB, sample.ManagedHeapMiB);
                if (sample.ExpensiveMetrics != null)
                {
                    totalGen0Collections += sample.ExpensiveMetrics.Gen0CollectionsDelta;
                    totalGen1Collections += sample.ExpensiveMetrics.Gen1CollectionsDelta;
                    totalGen2Collections += sample.ExpensiveMetrics.Gen2CollectionsDelta;
                }

                if (sample.Terrain == null)
                {
                    continue;
                }

                latestTerrainSnapshot = sample.Terrain;
                peakActiveChunks = Mathf.Max(peakActiveChunks, sample.Terrain.ActiveChunkCount);
                peakDesiredChunks = Mathf.Max(peakDesiredChunks, sample.Terrain.DesiredChunkCount);
                peakPendingLoads = Mathf.Max(peakPendingLoads, sample.Terrain.PendingLoadCount + sample.Terrain.RunningLoadCount);
                peakPendingMeshBuilds = Mathf.Max(peakPendingMeshBuilds, sample.Terrain.PendingMeshBuildCount + sample.Terrain.DeferredMeshBuildCount + sample.Terrain.RunningMeshBuildCount);
                peakPendingMeshCommits = Mathf.Max(peakPendingMeshCommits, sample.Terrain.PendingMeshCommitCount);
                peakPersistenceQueueDepth = Mathf.Max(peakPersistenceQueueDepth, sample.Terrain.PersistenceQueueDepth);
            }

            float averageFrameMs = ComputeAverage(frameTimesMs);
            return new CaptureSummary
            {
                DurationSeconds = durationSeconds,
                SampleCount = samples.Count,
                AverageFps = averageFrameMs > Mathf.Epsilon
                    ? 1000.0f / averageFrameMs
                    : 0.0f,
                AverageFrameMs = averageFrameMs,
                P95FrameMs = ComputePercentile(frameTimesMs, 0.95f),
                MaxFrameMs = ComputePercentile(frameTimesMs, 1.0f),
                PeakWorkingSetMiB = peakWorkingSetMiB,
                PeakManagedHeapMiB = peakManagedHeapMiB,
                TotalGen0Collections = expensiveMetricsEnabled ? totalGen0Collections : 0,
                TotalGen1Collections = expensiveMetricsEnabled ? totalGen1Collections : 0,
                TotalGen2Collections = expensiveMetricsEnabled ? totalGen2Collections : 0,
                PeakActiveChunks = peakActiveChunks,
                PeakDesiredChunks = peakDesiredChunks,
                PeakPendingLoads = peakPendingLoads,
                PeakPendingMeshBuilds = peakPendingMeshBuilds,
                PeakPendingMeshCommits = peakPendingMeshCommits,
                PeakPersistenceQueueDepth = peakPersistenceQueueDepth,
                LatestTerrainSnapshot = latestTerrainSnapshot
            };
        }

        private static float ComputeAverage(List<float> values)
        {
            if (values.Count == 0)
            {
                return 0.0f;
            }

            double total = 0.0;
            foreach (float value in values)
            {
                total += value;
            }

            return (float)(total / values.Count);
        }

        private static float ComputePercentile(List<float> values, float percentile)
        {
            if (values.Count == 0)
            {
                return 0.0f;
            }

            List<float> sorted = new(values);
            sorted.Sort();
            int index = Mathf.Clamp(Mathf.CeilToInt((sorted.Count - 1) * percentile), 0, sorted.Count - 1);
            return sorted[index];
        }
    }

    private sealed class CaptureSample
    {
        public double TimeSeconds { get; init; }
        public float AverageFrameMs { get; init; }
        public float MaxFrameMs { get; init; }
        public float Fps { get; init; }
        public float WorkingSetMiB { get; init; }
        public float ManagedHeapMiB { get; init; }
        public RuntimeGcMetrics ExpensiveMetrics { get; init; }
        public TerrainWorldProfileSnapshot Terrain { get; init; }
        public CaptureLocomotionSample Locomotion { get; init; }
    }

    private sealed class RuntimeGcMetrics
    {
        public float ManagedHeapDeltaMiB { get; init; }
        public int Gen0CollectionsDelta { get; init; }
        public int Gen1CollectionsDelta { get; init; }
        public int Gen2CollectionsDelta { get; init; }
    }

    private sealed class CaptureLocomotionSample
    {
        public string FacingDirection { get; init; } = string.Empty;
        public string GroundNormal { get; init; } = string.Empty;
        public float DesiredSpeed { get; init; }
        public float ActualSpeed { get; init; }
        public float StanceWidth { get; init; }
        public CaptureFootSample LeftFoot { get; init; }
        public CaptureFootSample RightFoot { get; init; }

        public static CaptureLocomotionSample From(LocomotionTelemetrySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            return new CaptureLocomotionSample
            {
                FacingDirection = FormatVector(snapshot.FacingDirection),
                GroundNormal = FormatVector(snapshot.GroundNormal),
                DesiredSpeed = snapshot.DesiredMovement.Length(),
                ActualSpeed = snapshot.ActualMovement.Length(),
                StanceWidth = snapshot.StanceWidth,
                LeftFoot = CaptureFootSample.From(snapshot.LeftFoot),
                RightFoot = CaptureFootSample.From(snapshot.RightFoot)
            };
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.X:0.00},{value.Y:0.00},{value.Z:0.00})";
        }
    }

    private sealed class CaptureFootSample
    {
        public FootSide Side { get; init; }
        public LocomotionFootState State { get; init; }
        public float ThresholdViolation { get; init; }
        public float FootSkateDistance { get; init; }
        public float CurrentStepDuration { get; init; }
        public float LastStepDuration { get; init; }
        public float SwingProgress { get; init; }
        public int StepCount { get; init; }
        public string DecisionReason { get; init; } = string.Empty;

        public static CaptureFootSample From(LocomotionFootTelemetry telemetry)
        {
            if (telemetry == null)
            {
                return null;
            }

            return new CaptureFootSample
            {
                Side = telemetry.Side,
                State = telemetry.State,
                ThresholdViolation = telemetry.ThresholdViolation,
                FootSkateDistance = telemetry.FootSkateDistance,
                CurrentStepDuration = telemetry.CurrentStepDuration,
                LastStepDuration = telemetry.LastStepDuration,
                SwingProgress = telemetry.SwingProgress,
                StepCount = telemetry.StepCount,
                DecisionReason = telemetry.DecisionReason
            };
        }
    }
}
