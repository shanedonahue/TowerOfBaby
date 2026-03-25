using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using TowerOfBaby.Entities.Motion;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.Debugging;

public partial class PerformanceRunLogger : Node
{
    [Export] public NodePath TerrainWorldPath = new();
    [Export] public double SampleIntervalSeconds = 1.0;

    private TerrainWorld _terrainWorld = null!;
    private readonly List<SamplePoint> _samples = new();
    private readonly List<float> _frameTimesMs = new();
    private double _elapsedSeconds;
    private double _sampleAccumulator;
    private double _sampleFrameMsAccum;
    private float _sampleMaxFrameMs;
    private int _sampleFrameCount;
    private int _sampleTotalLoadCount;
    private double _sampleTotalLoadMs;
    private int _sampleStartupLoadCount;
    private double _sampleStartupLoadMs;
    private int _samplePersistedLoadCount;
    private double _samplePersistedLoadMs;
    private int _sampleGeneratedLoadCount;
    private double _sampleGeneratedLoadMs;
    private int _sampleAttachCount;
    private double _sampleAttachMs;
    private int _sampleRenderCount;
    private double _sampleRenderMs;
    private int _sampleCollisionCount;
    private double _sampleCollisionMs;
    private int _minFps = int.MaxValue;
    private int _maxFps;
    private long _previousCacheHits;
    private long _previousCacheMisses;
    private long _previousEvictions;
    private ILocomotionTelemetrySource _locomotionTelemetrySource = null!;

    public override void _Ready()
    {
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _locomotionTelemetrySource = ResolveLocomotionTelemetrySource();
        TreeExiting += HandleTreeExiting;
    }

    public override void _Process(double delta)
    {
        _elapsedSeconds += delta;
        _sampleAccumulator += delta;
        float frameMs = (float)(delta * 1000.0);
        _frameTimesMs.Add(frameMs);
        _sampleFrameMsAccum += frameMs;
        _sampleMaxFrameMs = Mathf.Max(_sampleMaxFrameMs, frameMs);
        _sampleFrameCount++;

        int fps = (int)Engine.GetFramesPerSecond();
        _minFps = Mathf.Min(_minFps, fps);
        _maxFps = Mathf.Max(_maxFps, fps);

        TerrainWorldProfileSnapshot snapshot = _terrainWorld?.GetProfileSnapshot();
        _locomotionTelemetrySource ??= ResolveLocomotionTelemetrySource();
        LocomotionTelemetrySnapshot locomotionSnapshot = _locomotionTelemetrySource?.GetLocomotionTelemetrySnapshot();
        if (snapshot != null)
        {
            _sampleTotalLoadCount += snapshot.LastChunkLoadCount;
            _sampleTotalLoadMs += snapshot.LastChunkLoadMs;
            _sampleStartupLoadCount += snapshot.LastStartupChunkLoadCount;
            _sampleStartupLoadMs += snapshot.LastStartupChunkLoadMs;
            _samplePersistedLoadCount += snapshot.LastPersistedChunkLoadCount;
            _samplePersistedLoadMs += snapshot.LastPersistedChunkLoadMs;
            _sampleGeneratedLoadCount += snapshot.LastGeneratedChunkLoadCount;
            _sampleGeneratedLoadMs += snapshot.LastGeneratedChunkLoadMs;
            _sampleAttachCount += snapshot.LastChunkActivationCount;
            _sampleAttachMs += snapshot.LastChunkActivationMs;
            _sampleRenderCount += snapshot.LastVisualRebuildCount;
            _sampleRenderMs += snapshot.LastVisualRebuildMs;
            _sampleCollisionCount += snapshot.LastCollisionRebuildCount;
            _sampleCollisionMs += snapshot.LastCollisionRebuildMs;
        }

        if (_sampleAccumulator < SampleIntervalSeconds)
        {
            return;
        }

        float sampleAverageFrameMs = _sampleFrameCount > 0
            ? (float)(_sampleFrameMsAccum / _sampleFrameCount)
            : 0.0f;
        MemoryUsageSnapshot memory = CaptureMemoryUsage();
        SamplePoint sample = new(
            _elapsedSeconds,
            fps,
            sampleAverageFrameMs,
            _sampleMaxFrameMs,
            memory.WorkingSetMiB,
            memory.PrivateMemoryMiB,
            memory.ManagedHeapMiB,
            snapshot,
            _sampleTotalLoadCount,
            _sampleTotalLoadMs,
            _sampleStartupLoadCount,
            _sampleStartupLoadMs,
            _samplePersistedLoadCount,
            _samplePersistedLoadMs,
            _sampleGeneratedLoadCount,
            _sampleGeneratedLoadMs,
            _sampleAttachCount,
            _sampleAttachMs,
            _sampleRenderCount,
            _sampleRenderMs,
            _sampleCollisionCount,
            _sampleCollisionMs,
            snapshot == null ? 0 : snapshot.CacheHits - _previousCacheHits,
            snapshot == null ? 0 : snapshot.CacheMisses - _previousCacheMisses,
            snapshot == null ? 0 : snapshot.EvictedChunks - _previousEvictions,
            locomotionSnapshot);
        _sampleAccumulator = 0.0;
        _sampleFrameMsAccum = 0.0;
        _sampleFrameCount = 0;
        _sampleMaxFrameMs = 0.0f;
        _sampleTotalLoadCount = 0;
        _sampleTotalLoadMs = 0.0;
        _sampleStartupLoadCount = 0;
        _sampleStartupLoadMs = 0.0;
        _samplePersistedLoadCount = 0;
        _samplePersistedLoadMs = 0.0;
        _sampleGeneratedLoadCount = 0;
        _sampleGeneratedLoadMs = 0.0;
        _sampleAttachCount = 0;
        _sampleAttachMs = 0.0;
        _sampleRenderCount = 0;
        _sampleRenderMs = 0.0;
        _sampleCollisionCount = 0;
        _sampleCollisionMs = 0.0;

        if (snapshot != null)
        {
            _previousCacheHits = snapshot.CacheHits;
            _previousCacheMisses = snapshot.CacheMisses;
            _previousEvictions = snapshot.EvictedChunks;
        }

        _samples.Add(sample);
    }

    private void HandleTreeExiting()
    {
        WriteLog();
    }

    private void WriteLog()
    {
        string root = "user://profiling";
        DirAccess.MakeDirRecursiveAbsolute(root);

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = $"{root}/run_{timestamp}.log";
        using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            return;
        }

        StringBuilder builder = new();
        builder.AppendLine("TowerOfBaby Performance Run");
        builder.AppendLine($"UTC: {DateTime.UtcNow:O}");
        builder.AppendLine($"DurationSeconds: {_elapsedSeconds:0.00}");
        builder.AppendLine($"Samples: {_samples.Count}");
        builder.AppendLine($"MinFPS: {(_minFps == int.MaxValue ? 0 : _minFps)}");
        builder.AppendLine($"MaxFPS: {_maxFps}");

        if (_samples.Count > 0)
        {
            double averageFps = 0.0;
            int peakLoadedChunks = 0;
            int peakActiveChunks = 0;
            int peakPendingLoads = 0;
            double totalChunkLoadMs = 0.0;
            double totalRenderMs = 0.0;
            double totalCollisionMs = 0.0;
            int totalStartupLoads = 0;
            int totalPersistedLoads = 0;
            int totalGeneratedLoads = 0;
            double totalStartupLoadMs = 0.0;
            double totalPersistedLoadMs = 0.0;
            double totalGeneratedLoadMs = 0.0;
            float peakWorkingSetMiB = 0.0f;
            float peakPrivateMemoryMiB = 0.0f;
            float peakManagedHeapMiB = 0.0f;
            float averageFrameMs = ComputeAverageFrameMs();
            float p95FrameMs = ComputePercentileFrameMs(0.95f);
            float maxFrameMs = ComputePercentileFrameMs(1.0f);
            float peakFootSkate = 0.0f;
            int leftStepCount = 0;
            int rightStepCount = 0;
            float lastLeftStepDuration = 0.0f;
            float lastRightStepDuration = 0.0f;

            foreach (SamplePoint sample in _samples)
            {
                averageFps += sample.Fps;
                if (sample.LocomotionSnapshot != null)
                {
                    peakFootSkate = Mathf.Max(
                        peakFootSkate,
                        Mathf.Max(sample.LocomotionSnapshot.LeftFoot.FootSkateDistance, sample.LocomotionSnapshot.RightFoot.FootSkateDistance));
                    leftStepCount = Mathf.Max(leftStepCount, sample.LocomotionSnapshot.LeftFoot.StepCount);
                    rightStepCount = Mathf.Max(rightStepCount, sample.LocomotionSnapshot.RightFoot.StepCount);
                    lastLeftStepDuration = Mathf.Max(lastLeftStepDuration, sample.LocomotionSnapshot.LeftFoot.LastStepDuration);
                    lastRightStepDuration = Mathf.Max(lastRightStepDuration, sample.LocomotionSnapshot.RightFoot.LastStepDuration);
                }

                if (sample.Snapshot == null)
                {
                    continue;
                }

                peakLoadedChunks = Mathf.Max(peakLoadedChunks, sample.Snapshot.LoadedChunkCount);
                peakActiveChunks = Mathf.Max(peakActiveChunks, sample.Snapshot.ActiveChunkCount);
                peakPendingLoads = Mathf.Max(peakPendingLoads, sample.Snapshot.PendingLoadCount + sample.Snapshot.RunningLoadCount);
                peakWorkingSetMiB = Mathf.Max(peakWorkingSetMiB, sample.WorkingSetMiB);
                peakPrivateMemoryMiB = Mathf.Max(peakPrivateMemoryMiB, sample.PrivateMemoryMiB);
                peakManagedHeapMiB = Mathf.Max(peakManagedHeapMiB, sample.ManagedHeapMiB);
                totalStartupLoads += sample.StartupLoadCount;
                totalPersistedLoads += sample.PersistedLoadCount;
                totalGeneratedLoads += sample.GeneratedLoadCount;
                totalStartupLoadMs += sample.StartupLoadMs;
                totalPersistedLoadMs += sample.PersistedLoadMs;
                totalGeneratedLoadMs += sample.GeneratedLoadMs;
                totalChunkLoadMs += sample.TotalLoadMs;
                totalRenderMs += sample.RenderMs;
                totalCollisionMs += sample.CollisionMs;
            }

            averageFps /= _samples.Count;
            builder.AppendLine($"AvgFPS: {averageFps:0.00}");
            builder.AppendLine($"AvgFrameMs: {averageFrameMs:0.00}");
            builder.AppendLine($"P95FrameMs: {p95FrameMs:0.00}");
            builder.AppendLine($"MaxFrameMs: {maxFrameMs:0.00}");
            builder.AppendLine($"PeakWorkingSetMiB: {peakWorkingSetMiB:0.00}");
            builder.AppendLine($"PeakPrivateMemoryMiB: {peakPrivateMemoryMiB:0.00}");
            builder.AppendLine($"PeakManagedHeapMiB: {peakManagedHeapMiB:0.00}");
            builder.AppendLine($"PeakLoadedChunks: {peakLoadedChunks}");
            builder.AppendLine($"PeakActiveChunks: {peakActiveChunks}");
            builder.AppendLine($"PeakPendingLoads: {peakPendingLoads}");
            builder.AppendLine($"StartupChunkLoads: {totalStartupLoads}");
            builder.AppendLine($"PersistedChunkLoads: {totalPersistedLoads}");
            builder.AppendLine($"GeneratedChunkLoads: {totalGeneratedLoads}");
            builder.AppendLine($"AccumulatedStartupChunkLoadMs: {totalStartupLoadMs:0.00}");
            builder.AppendLine($"AccumulatedPersistedChunkLoadMs: {totalPersistedLoadMs:0.00}");
            builder.AppendLine($"AccumulatedGeneratedChunkLoadMs: {totalGeneratedLoadMs:0.00}");
            builder.AppendLine($"AccumulatedChunkLoadMs: {totalChunkLoadMs:0.00}");
            builder.AppendLine($"AccumulatedRenderRebuildMs: {totalRenderMs:0.00}");
            builder.AppendLine($"AccumulatedCollisionRebuildMs: {totalCollisionMs:0.00}");
            builder.AppendLine($"LocomotionLeftStepCount: {leftStepCount}");
            builder.AppendLine($"LocomotionRightStepCount: {rightStepCount}");
            builder.AppendLine($"LocomotionPeakFootSkate: {peakFootSkate:0.000}");
            builder.AppendLine($"LocomotionLeftLastStepDuration: {lastLeftStepDuration:0.000}");
            builder.AppendLine($"LocomotionRightLastStepDuration: {lastRightStepDuration:0.000}");
        }

        builder.AppendLine();
        builder.AppendLine("Samples");
        builder.AppendLine("time_s,fps,avg_frame_ms,max_frame_ms,working_set_mib,private_memory_mib,managed_heap_mib,active_chunks,loaded_chunks,desired_chunks,pending_loads,running_loads,pending_activation,dirty_render,dirty_collision,load_count,load_ms,startup_load_count,startup_load_ms,persisted_load_count,persisted_load_ms,generated_load_count,generated_load_ms,attach_count,attach_ms,render_count,render_ms,collision_count,collision_ms,cache_hits,cache_misses,evicted_chunks,cache_hits_delta,cache_misses_delta,evicted_chunks_delta,initial_load_progress,initial_load_complete," + LocomotionMetrics.BuildCsvHeader());

        foreach (SamplePoint sample in _samples)
        {
            TerrainWorldProfileSnapshot snapshot = sample.Snapshot;
            if (snapshot == null)
            {
                builder.AppendLine(
                    $"{sample.TimeSeconds:0.00},{sample.Fps},{sample.AverageFrameMs:0.00},{sample.MaxFrameMs:0.00},{sample.WorkingSetMiB:0.00},{sample.PrivateMemoryMiB:0.00},{sample.ManagedHeapMiB:0.00},,,,,,,,,,,,,,,,,,,,,,,,," +
                    $",{LocomotionMetrics.BuildCsvValues(sample.LocomotionSnapshot)}");
                continue;
            }

            builder.AppendLine(string.Join(",",
                sample.TimeSeconds.ToString("0.00", CultureInfo.InvariantCulture),
                sample.Fps.ToString(CultureInfo.InvariantCulture),
                sample.AverageFrameMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.MaxFrameMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.WorkingSetMiB.ToString("0.00", CultureInfo.InvariantCulture),
                sample.PrivateMemoryMiB.ToString("0.00", CultureInfo.InvariantCulture),
                sample.ManagedHeapMiB.ToString("0.00", CultureInfo.InvariantCulture),
                snapshot.ActiveChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.LoadedChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DesiredChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PendingLoadCount.ToString(CultureInfo.InvariantCulture),
                snapshot.RunningLoadCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PendingActivationCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DirtyRenderCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DirtyCollisionCount.ToString(CultureInfo.InvariantCulture),
                sample.TotalLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.TotalLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.StartupLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.StartupLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.PersistedLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.PersistedLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.GeneratedLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.GeneratedLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.AttachCount.ToString(CultureInfo.InvariantCulture),
                sample.AttachMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.RenderCount.ToString(CultureInfo.InvariantCulture),
                sample.RenderMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.CollisionCount.ToString(CultureInfo.InvariantCulture),
                sample.CollisionMs.ToString("0.00", CultureInfo.InvariantCulture),
                snapshot.CacheHits.ToString(CultureInfo.InvariantCulture),
                snapshot.CacheMisses.ToString(CultureInfo.InvariantCulture),
                snapshot.EvictedChunks.ToString(CultureInfo.InvariantCulture),
                sample.CacheHitsDelta.ToString(CultureInfo.InvariantCulture),
                sample.CacheMissesDelta.ToString(CultureInfo.InvariantCulture),
                sample.EvictionsDelta.ToString(CultureInfo.InvariantCulture),
                snapshot.InitialLoadProgress.ToString("0.000", CultureInfo.InvariantCulture),
                snapshot.InitialLoadComplete ? "1" : "0",
                LocomotionMetrics.BuildCsvValues(sample.LocomotionSnapshot)));
        }

        file.StoreString(builder.ToString());
        GD.Print($"Performance run log written to {ProjectSettings.GlobalizePath(path)}");
    }

    private float ComputeAverageFrameMs()
    {
        if (_frameTimesMs.Count == 0)
        {
            return 0.0f;
        }

        double total = 0.0;
        foreach (float frameMs in _frameTimesMs)
        {
            total += frameMs;
        }

        return (float)(total / _frameTimesMs.Count);
    }

    private float ComputePercentileFrameMs(float percentile)
    {
        if (_frameTimesMs.Count == 0)
        {
            return 0.0f;
        }

        List<float> sorted = new(_frameTimesMs);
        sorted.Sort();
        int index = Mathf.Clamp(Mathf.CeilToInt((sorted.Count - 1) * percentile), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static MemoryUsageSnapshot CaptureMemoryUsage()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemoryUsageSnapshot(
            BytesToMiB(process.WorkingSet64),
            BytesToMiB(process.PrivateMemorySize64),
            BytesToMiB(GC.GetTotalMemory(false)));
    }

    private static float BytesToMiB(long bytes)
    {
        return bytes / (1024.0f * 1024.0f);
    }

    private ILocomotionTelemetrySource ResolveLocomotionTelemetrySource()
    {
        return GetTree().GetFirstNodeInGroup("locomotion_telemetry_source") as ILocomotionTelemetrySource;
    }

    private sealed record SamplePoint(
        double TimeSeconds,
        int Fps,
        float AverageFrameMs,
        float MaxFrameMs,
        float WorkingSetMiB,
        float PrivateMemoryMiB,
        float ManagedHeapMiB,
        TerrainWorldProfileSnapshot Snapshot,
        int TotalLoadCount,
        double TotalLoadMs,
        int StartupLoadCount,
        double StartupLoadMs,
        int PersistedLoadCount,
        double PersistedLoadMs,
        int GeneratedLoadCount,
        double GeneratedLoadMs,
        int AttachCount,
        double AttachMs,
        int RenderCount,
        double RenderMs,
        int CollisionCount,
        double CollisionMs,
        long CacheHitsDelta,
        long CacheMissesDelta,
        long EvictionsDelta,
        LocomotionTelemetrySnapshot LocomotionSnapshot);

    private sealed record MemoryUsageSnapshot(
        float WorkingSetMiB,
        float PrivateMemoryMiB,
        float ManagedHeapMiB);
}
