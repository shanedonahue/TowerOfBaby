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
    private int _sampleRamLoadCount;
    private double _sampleRamLoadMs;
    private int _sampleGeneratedLoadCount;
    private double _sampleGeneratedLoadMs;
    private int _sampleAttachCount;
    private double _sampleAttachMs;
    private int _sampleRenderCount;
    private double _sampleRenderMs;
    private int _sampleMeshWorkerCount;
    private double _sampleMeshWorkerMs;
    private int _sampleCollisionCount;
    private double _sampleCollisionMs;
    private int _sampleReleaseCount;
    private double _sampleReleaseMs;
    private int _sampleDeferredDetailPromotions;
    private int _sampleDeferredPromotionReevaluations;
    private int _sampleAvoidedDeferredReevaluations;
    private int _sampleSuppressedDeferredLogRepeats;
    private int _sampleRequestsReactivatedByMeshCompletion;
    private int _sampleRequestsReactivatedByCooldownExpiry;
    private int _sampleRequestsReactivatedByPressureExit;
    private int _sampleCoalescedRebuildRequests;
    private double _sampleSearchMs;
    private double _samplePriorityEvalMs;
    private double _sampleVisibilityMs;
    private int _minFps = int.MaxValue;
    private int _maxFps;
    private long _previousCacheHits;
    private long _previousCacheMisses;
    private long _previousEvictions;
    private double _previousMeshWorkerQueueWaitMs;
    private long _previousLowPriorityDeferredMeshBuildCount;
    private float _previousManagedHeapMiB = float.NaN;
    private int _previousGen0CollectionCount = -1;
    private int _previousGen1CollectionCount = -1;
    private int _previousGen2CollectionCount = -1;
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
            _sampleRamLoadCount += snapshot.LastRamCacheLoadCount;
            _sampleRamLoadMs += snapshot.LastRamCacheLoadMs;
            _sampleGeneratedLoadCount += snapshot.LastGeneratedChunkLoadCount;
            _sampleGeneratedLoadMs += snapshot.LastGeneratedChunkLoadMs;
            _sampleAttachCount += snapshot.LastChunkActivationCount;
            _sampleAttachMs += snapshot.LastChunkActivationMs;
            _sampleReleaseCount += snapshot.LastChunkReleaseCount;
            _sampleReleaseMs += snapshot.LastChunkReleaseMs;
            _sampleRenderCount += snapshot.LastVisualRebuildCount;
            _sampleRenderMs += snapshot.LastVisualRebuildMs;
            _sampleMeshWorkerCount += snapshot.LastMeshWorkerBuildCount;
            _sampleMeshWorkerMs += snapshot.LastMeshWorkerBuildMs;
            _sampleCollisionCount += snapshot.LastCollisionRebuildCount;
            _sampleCollisionMs += snapshot.LastCollisionRebuildMs;
            _sampleDeferredDetailPromotions += snapshot.LastDeferredDetailPromotionCount;
            _sampleDeferredPromotionReevaluations += snapshot.LastDeferredPromotionReevaluationCount;
            _sampleAvoidedDeferredReevaluations += snapshot.LastAvoidedDeferredReevaluationCount;
            _sampleSuppressedDeferredLogRepeats += snapshot.LastSuppressedDeferredLogRepeatCount;
            _sampleRequestsReactivatedByMeshCompletion += snapshot.LastRequestsReactivatedByMeshCompletionCount;
            _sampleRequestsReactivatedByCooldownExpiry += snapshot.LastRequestsReactivatedByCooldownExpiryCount;
            _sampleRequestsReactivatedByPressureExit += snapshot.LastRequestsReactivatedByPressureExitCount;
            _sampleCoalescedRebuildRequests += snapshot.LastCoalescedRebuildRequestCount;
            _sampleSearchMs += snapshot.LastDesiredSearchMs;
            _samplePriorityEvalMs += snapshot.LastPriorityEvaluationMs;
            _sampleVisibilityMs += snapshot.LastVisibilityHeuristicMs;
        }

        if (_sampleAccumulator < SampleIntervalSeconds)
        {
            return;
        }

        float sampleAverageFrameMs = _sampleFrameCount > 0
            ? (float)(_sampleFrameMsAccum / _sampleFrameCount)
            : 0.0f;
        MemoryUsageSnapshot memory = CaptureMemoryUsage();
        int gen0CollectionCount = GC.CollectionCount(0);
        int gen1CollectionCount = GC.CollectionCount(1);
        int gen2CollectionCount = GC.CollectionCount(2);
        float managedHeapDeltaMiB = float.IsNaN(_previousManagedHeapMiB)
            ? 0.0f
            : memory.ManagedHeapMiB - _previousManagedHeapMiB;
        int gen0CollectionsDelta = _previousGen0CollectionCount < 0
            ? 0
            : gen0CollectionCount - _previousGen0CollectionCount;
        int gen1CollectionsDelta = _previousGen1CollectionCount < 0
            ? 0
            : gen1CollectionCount - _previousGen1CollectionCount;
        int gen2CollectionsDelta = _previousGen2CollectionCount < 0
            ? 0
            : gen2CollectionCount - _previousGen2CollectionCount;
        double meshWorkerQueueWaitMsDelta = snapshot == null
            ? 0.0
            : snapshot.MeshWorkerQueueWaitMs - _previousMeshWorkerQueueWaitMs;
        long lowPriorityDeferredMeshBuildsDelta = snapshot == null
            ? 0
            : snapshot.LowPriorityDeferredMeshBuildCount - _previousLowPriorityDeferredMeshBuildCount;
        SamplePoint sample = new(
            _elapsedSeconds,
            fps,
            sampleAverageFrameMs,
            _sampleMaxFrameMs,
            memory.WorkingSetMiB,
            memory.PrivateMemoryMiB,
            memory.ManagedHeapMiB,
            managedHeapDeltaMiB,
            gen0CollectionsDelta,
            gen1CollectionsDelta,
            gen2CollectionsDelta,
            snapshot,
            _sampleTotalLoadCount,
            _sampleTotalLoadMs,
            _sampleStartupLoadCount,
            _sampleStartupLoadMs,
            _samplePersistedLoadCount,
            _samplePersistedLoadMs,
            _sampleRamLoadCount,
            _sampleRamLoadMs,
            _sampleGeneratedLoadCount,
            _sampleGeneratedLoadMs,
            _sampleAttachCount,
            _sampleAttachMs,
            _sampleReleaseCount,
            _sampleReleaseMs,
            _sampleRenderCount,
            _sampleRenderMs,
            _sampleMeshWorkerCount,
            _sampleMeshWorkerMs,
            meshWorkerQueueWaitMsDelta,
            _sampleCollisionCount,
            _sampleCollisionMs,
            _sampleDeferredDetailPromotions,
            _sampleDeferredPromotionReevaluations,
            _sampleAvoidedDeferredReevaluations,
            _sampleSuppressedDeferredLogRepeats,
            _sampleRequestsReactivatedByMeshCompletion,
            _sampleRequestsReactivatedByCooldownExpiry,
            _sampleRequestsReactivatedByPressureExit,
            _sampleCoalescedRebuildRequests,
            _sampleSearchMs,
            _samplePriorityEvalMs,
            _sampleVisibilityMs,
            lowPriorityDeferredMeshBuildsDelta,
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
        _sampleRamLoadCount = 0;
        _sampleRamLoadMs = 0.0;
        _sampleGeneratedLoadCount = 0;
        _sampleGeneratedLoadMs = 0.0;
        _sampleAttachCount = 0;
        _sampleAttachMs = 0.0;
        _sampleReleaseCount = 0;
        _sampleReleaseMs = 0.0;
        _sampleRenderCount = 0;
        _sampleRenderMs = 0.0;
        _sampleMeshWorkerCount = 0;
        _sampleMeshWorkerMs = 0.0;
        _sampleCollisionCount = 0;
        _sampleCollisionMs = 0.0;
        _sampleDeferredDetailPromotions = 0;
        _sampleDeferredPromotionReevaluations = 0;
        _sampleAvoidedDeferredReevaluations = 0;
        _sampleSuppressedDeferredLogRepeats = 0;
        _sampleRequestsReactivatedByMeshCompletion = 0;
        _sampleRequestsReactivatedByCooldownExpiry = 0;
        _sampleRequestsReactivatedByPressureExit = 0;
        _sampleCoalescedRebuildRequests = 0;
        _sampleSearchMs = 0.0;
        _samplePriorityEvalMs = 0.0;
        _sampleVisibilityMs = 0.0;

        if (snapshot != null)
        {
            _previousCacheHits = snapshot.CacheHits;
            _previousCacheMisses = snapshot.CacheMisses;
            _previousEvictions = snapshot.EvictedChunks;
            _previousMeshWorkerQueueWaitMs = snapshot.MeshWorkerQueueWaitMs;
            _previousLowPriorityDeferredMeshBuildCount = snapshot.LowPriorityDeferredMeshBuildCount;
        }

        _previousManagedHeapMiB = memory.ManagedHeapMiB;
        _previousGen0CollectionCount = gen0CollectionCount;
        _previousGen1CollectionCount = gen1CollectionCount;
        _previousGen2CollectionCount = gen2CollectionCount;

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
            TerrainWorldProfileSnapshot latestSnapshot = null;
            int peakLoadedChunks = 0;
            int peakActiveChunks = 0;
            int peakPendingLoads = 0;
            int peakPendingMeshBuilds = 0;
            int peakDeferredMeshBuilds = 0;
            int peakRunningMeshBuilds = 0;
            int peakPendingMeshCommits = 0;
            int peakHighPriorityMeshQueueDepth = 0;
            int peakLowPriorityMeshQueueDepth = 0;
            int peakRamCacheChunks = 0;
            double totalChunkLoadMs = 0.0;
            double totalReleaseMs = 0.0;
            double totalRenderMs = 0.0;
            double totalMeshWorkerMs = 0.0;
            double totalMeshWorkerQueueWaitMs = 0.0;
            double totalCollisionMs = 0.0;
            double totalSearchMs = 0.0;
            double totalPriorityEvalMs = 0.0;
            double totalVisibilityMs = 0.0;
            int totalStartupLoads = 0;
            int totalPersistedLoads = 0;
            int totalGeneratedLoads = 0;
            int totalRamLoads = 0;
            int totalReleases = 0;
            int totalMeshWorkerBuilds = 0;
            long totalLowPriorityDeferredMeshBuilds = 0;
            int totalDeferredDetailPromotions = 0;
            int totalDeferredPromotionReevaluations = 0;
            int totalAvoidedDeferredReevaluations = 0;
            int totalSuppressedDeferredLogRepeats = 0;
            int totalRequestsReactivatedByMeshCompletion = 0;
            int totalRequestsReactivatedByCooldownExpiry = 0;
            int totalRequestsReactivatedByPressureExit = 0;
            int totalCoalescedRebuildRequests = 0;
            long totalSkippedLowPriorityMeshBuilds = 0;
            long totalSuppressedDuplicateMeshBuilds = 0;
            double totalStartupLoadMs = 0.0;
            double totalPersistedLoadMs = 0.0;
            double totalGeneratedLoadMs = 0.0;
            double totalRamLoadMs = 0.0;
            float peakWorkingSetMiB = 0.0f;
            float peakPrivateMemoryMiB = 0.0f;
            float peakManagedHeapMiB = 0.0f;
            float peakManagedHeapDeltaMiB = 0.0f;
            double peakMeshWorkerQueueWaitMs = 0.0;
            int peakFrontier = 0;
            int peakToAdd = 0;
            int peakToRelease = 0;
            int peakStartupSnapshotChunks = 0;
            int peakStartupDesiredCoverage = 0;
            int peakPersistedChunkRecords = 0;
            long peakSearchInvalidations = 0;
            long peakFrontierCompactions = 0;
            long peakStartupPromotionWrites = 0;
            int totalGen0Collections = 0;
            int totalGen1Collections = 0;
            int totalGen2Collections = 0;
            long peakPressureModeActiveFrames = 0;
            int peakPressureModeActivationCount = 0;
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

                latestSnapshot = sample.Snapshot;
                peakLoadedChunks = Mathf.Max(peakLoadedChunks, sample.Snapshot.LoadedChunkCount);
                peakActiveChunks = Mathf.Max(peakActiveChunks, sample.Snapshot.ActiveChunkCount);
                peakPendingLoads = Mathf.Max(peakPendingLoads, sample.Snapshot.PendingLoadCount + sample.Snapshot.RunningLoadCount);
                peakPendingMeshBuilds = Mathf.Max(peakPendingMeshBuilds, sample.Snapshot.PendingMeshBuildCount);
                peakDeferredMeshBuilds = Mathf.Max(peakDeferredMeshBuilds, sample.Snapshot.DeferredMeshBuildCount);
                peakRunningMeshBuilds = Mathf.Max(peakRunningMeshBuilds, sample.Snapshot.RunningMeshBuildCount);
                peakPendingMeshCommits = Mathf.Max(peakPendingMeshCommits, sample.Snapshot.PendingMeshCommitCount);
                peakHighPriorityMeshQueueDepth = Mathf.Max(peakHighPriorityMeshQueueDepth, sample.Snapshot.HighPriorityMeshQueueDepth);
                peakLowPriorityMeshQueueDepth = Mathf.Max(peakLowPriorityMeshQueueDepth, sample.Snapshot.LowPriorityMeshQueueDepth);
                peakRamCacheChunks = Mathf.Max(peakRamCacheChunks, sample.Snapshot.RamCacheChunkCount);
                peakFrontier = Mathf.Max(peakFrontier, sample.Snapshot.FrontierSize);
                peakToAdd = Mathf.Max(peakToAdd, sample.Snapshot.ToAddCount);
                peakToRelease = Mathf.Max(peakToRelease, sample.Snapshot.ToReleaseCount);
                peakStartupSnapshotChunks = Mathf.Max(peakStartupSnapshotChunks, sample.Snapshot.StartupSnapshotChunkCount);
                peakStartupDesiredCoverage = Mathf.Max(peakStartupDesiredCoverage, sample.Snapshot.StartupDesiredCoverageCount);
                peakPersistedChunkRecords = Mathf.Max(peakPersistedChunkRecords, sample.Snapshot.PersistedChunkRecordCount);
                peakSearchInvalidations = Math.Max(peakSearchInvalidations, sample.Snapshot.SearchInvalidationCount);
                peakFrontierCompactions = Math.Max(peakFrontierCompactions, sample.Snapshot.FrontierCompactionCount);
                peakStartupPromotionWrites = Math.Max(peakStartupPromotionWrites, sample.Snapshot.StartupPromotionWrites);
                peakMeshWorkerQueueWaitMs = Math.Max(peakMeshWorkerQueueWaitMs, sample.Snapshot.PeakMeshWorkerQueueWaitMs);
                peakWorkingSetMiB = Mathf.Max(peakWorkingSetMiB, sample.WorkingSetMiB);
                peakPrivateMemoryMiB = Mathf.Max(peakPrivateMemoryMiB, sample.PrivateMemoryMiB);
                peakManagedHeapMiB = Mathf.Max(peakManagedHeapMiB, sample.ManagedHeapMiB);
                peakManagedHeapDeltaMiB = Mathf.Max(peakManagedHeapDeltaMiB, sample.ManagedHeapDeltaMiB);
                totalStartupLoads += sample.StartupLoadCount;
                totalPersistedLoads += sample.PersistedLoadCount;
                totalRamLoads += sample.RamLoadCount;
                totalGeneratedLoads += sample.GeneratedLoadCount;
                totalStartupLoadMs += sample.StartupLoadMs;
                totalPersistedLoadMs += sample.PersistedLoadMs;
                totalRamLoadMs += sample.RamLoadMs;
                totalGeneratedLoadMs += sample.GeneratedLoadMs;
                totalChunkLoadMs += sample.TotalLoadMs;
                totalReleases += sample.ReleaseCount;
                totalReleaseMs += sample.ReleaseMs;
                totalRenderMs += sample.RenderMs;
                totalMeshWorkerBuilds += sample.MeshWorkerCount;
                totalMeshWorkerMs += sample.MeshWorkerMs;
                totalMeshWorkerQueueWaitMs += sample.MeshWorkerQueueWaitMsDelta;
                totalCollisionMs += sample.CollisionMs;
                totalLowPriorityDeferredMeshBuilds += sample.LowPriorityDeferredMeshBuildsDelta;
                totalDeferredDetailPromotions += sample.DeferredDetailPromotions;
                totalDeferredPromotionReevaluations += sample.DeferredPromotionReevaluations;
                totalAvoidedDeferredReevaluations += sample.AvoidedDeferredReevaluations;
                totalSuppressedDeferredLogRepeats += sample.SuppressedDeferredLogRepeats;
                totalRequestsReactivatedByMeshCompletion += sample.RequestsReactivatedByMeshCompletion;
                totalRequestsReactivatedByCooldownExpiry += sample.RequestsReactivatedByCooldownExpiry;
                totalRequestsReactivatedByPressureExit += sample.RequestsReactivatedByPressureExit;
                totalCoalescedRebuildRequests += sample.CoalescedRebuildRequests;
                totalSkippedLowPriorityMeshBuilds = Math.Max(totalSkippedLowPriorityMeshBuilds, sample.Snapshot.SkippedLowPriorityMeshBuildCount);
                totalSuppressedDuplicateMeshBuilds = Math.Max(totalSuppressedDuplicateMeshBuilds, sample.Snapshot.SuppressedDuplicateMeshBuildCount);
                peakPressureModeActiveFrames = Math.Max(peakPressureModeActiveFrames, sample.Snapshot.PressureModeActiveFrameCount);
                peakPressureModeActivationCount = Math.Max(peakPressureModeActivationCount, sample.Snapshot.PressureModeActivationCount);
                totalSearchMs += sample.SearchMs;
                totalPriorityEvalMs += sample.PriorityEvalMs;
                totalVisibilityMs += sample.VisibilityMs;
                totalGen0Collections += sample.Gen0CollectionsDelta;
                totalGen1Collections += sample.Gen1CollectionsDelta;
                totalGen2Collections += sample.Gen2CollectionsDelta;
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
            builder.AppendLine($"PeakPendingMeshBuilds: {peakPendingMeshBuilds}");
            builder.AppendLine($"PeakDeferredMeshBuilds: {peakDeferredMeshBuilds}");
            builder.AppendLine($"PeakRunningMeshBuilds: {peakRunningMeshBuilds}");
            builder.AppendLine($"PeakPendingMeshCommits: {peakPendingMeshCommits}");
            builder.AppendLine($"PeakHighPriorityMeshQueueDepth: {peakHighPriorityMeshQueueDepth}");
            builder.AppendLine($"PeakLowPriorityMeshQueueDepth: {peakLowPriorityMeshQueueDepth}");
            builder.AppendLine($"PeakRamCacheChunks: {peakRamCacheChunks}");
            builder.AppendLine($"PeakMeshWorkerQueueWaitMs: {peakMeshWorkerQueueWaitMs:0.00}");
            builder.AppendLine($"PeakFrontier: {peakFrontier}");
            builder.AppendLine($"PeakToAdd: {peakToAdd}");
            builder.AppendLine($"PeakToRelease: {peakToRelease}");
            builder.AppendLine($"PeakStartupSnapshotChunks: {peakStartupSnapshotChunks}");
            builder.AppendLine($"PeakStartupDesiredCoverage: {peakStartupDesiredCoverage}");
            builder.AppendLine($"PeakPersistedChunkRecords: {peakPersistedChunkRecords}");
            builder.AppendLine($"PeakManagedHeapDeltaMiB: {peakManagedHeapDeltaMiB:0.00}");
            builder.AppendLine($"SearchInvalidations: {peakSearchInvalidations}");
            builder.AppendLine($"FrontierCompactions: {peakFrontierCompactions}");
            builder.AppendLine($"RamChunkLoads: {totalRamLoads}");
            builder.AppendLine($"StartupChunkLoads: {totalStartupLoads}");
            builder.AppendLine($"PersistedChunkLoads: {totalPersistedLoads}");
            builder.AppendLine($"GeneratedChunkLoads: {totalGeneratedLoads}");
            builder.AppendLine($"ChunkReleases: {totalReleases}");
            builder.AppendLine($"MeshWorkerBuilds: {totalMeshWorkerBuilds}");
            builder.AppendLine($"LowPriorityDeferredMeshBuilds: {totalLowPriorityDeferredMeshBuilds}");
            builder.AppendLine($"SkippedLowPriorityMeshBuilds: {totalSkippedLowPriorityMeshBuilds}");
            builder.AppendLine($"SuppressedDuplicateMeshBuilds: {totalSuppressedDuplicateMeshBuilds}");
            builder.AppendLine($"DeferredDetailPromotions: {totalDeferredDetailPromotions}");
            builder.AppendLine($"DeferredPromotionReevaluations: {totalDeferredPromotionReevaluations}");
            builder.AppendLine($"AvoidedDeferredReevaluations: {totalAvoidedDeferredReevaluations}");
            builder.AppendLine($"SuppressedDeferredLogRepeats: {totalSuppressedDeferredLogRepeats}");
            builder.AppendLine($"RequestsReactivatedByMeshCompletion: {totalRequestsReactivatedByMeshCompletion}");
            builder.AppendLine($"RequestsReactivatedByCooldownExpiry: {totalRequestsReactivatedByCooldownExpiry}");
            builder.AppendLine($"RequestsReactivatedByPressureExit: {totalRequestsReactivatedByPressureExit}");
            builder.AppendLine($"CoalescedRebuildRequests: {totalCoalescedRebuildRequests}");
            builder.AppendLine($"PressureModeActiveFrames: {peakPressureModeActiveFrames}");
            builder.AppendLine($"PressureModeActivations: {peakPressureModeActivationCount}");
            builder.AppendLine($"StartupPromotionWrites: {peakStartupPromotionWrites}");
            builder.AppendLine($"AccumulatedRamChunkLoadMs: {totalRamLoadMs:0.00}");
            builder.AppendLine($"AccumulatedStartupChunkLoadMs: {totalStartupLoadMs:0.00}");
            builder.AppendLine($"AccumulatedPersistedChunkLoadMs: {totalPersistedLoadMs:0.00}");
            builder.AppendLine($"AccumulatedGeneratedChunkLoadMs: {totalGeneratedLoadMs:0.00}");
            builder.AppendLine($"AccumulatedChunkLoadMs: {totalChunkLoadMs:0.00}");
            builder.AppendLine($"AccumulatedChunkReleaseMs: {totalReleaseMs:0.00}");
            builder.AppendLine($"AccumulatedMeshWorkerBuildMs: {totalMeshWorkerMs:0.00}");
            builder.AppendLine($"AccumulatedMeshWorkerQueueWaitMs: {totalMeshWorkerQueueWaitMs:0.00}");
            builder.AppendLine($"AccumulatedRenderRebuildMs: {totalRenderMs:0.00}");
            builder.AppendLine($"AccumulatedCollisionRebuildMs: {totalCollisionMs:0.00}");
            builder.AppendLine($"AccumulatedDesiredSearchMs: {totalSearchMs:0.00}");
            builder.AppendLine($"AccumulatedPriorityEvalMs: {totalPriorityEvalMs:0.00}");
            builder.AppendLine($"AccumulatedVisibilityMs: {totalVisibilityMs:0.00}");
            builder.AppendLine($"Gen0Collections: {totalGen0Collections}");
            builder.AppendLine($"Gen1Collections: {totalGen1Collections}");
            builder.AppendLine($"Gen2Collections: {totalGen2Collections}");
            if (latestSnapshot != null)
            {
                builder.AppendLine($"AverageCoarseMeshWorkerHeapDeltaKiB: {latestSnapshot.AverageCoarseMeshWorkerHeapDeltaKiB:0.00}");
                builder.AppendLine($"PeakCoarseMeshWorkerHeapDeltaKiB: {latestSnapshot.PeakCoarseMeshWorkerHeapDeltaKiB:0.00}");
                builder.AppendLine($"AverageDetailMeshWorkerHeapDeltaKiB: {latestSnapshot.AverageDetailMeshWorkerHeapDeltaKiB:0.00}");
                builder.AppendLine($"PeakDetailMeshWorkerHeapDeltaKiB: {latestSnapshot.PeakDetailMeshWorkerHeapDeltaKiB:0.00}");
            }
            builder.AppendLine($"LocomotionLeftStepCount: {leftStepCount}");
            builder.AppendLine($"LocomotionRightStepCount: {rightStepCount}");
            builder.AppendLine($"LocomotionPeakFootSkate: {peakFootSkate:0.000}");
            builder.AppendLine($"LocomotionLeftLastStepDuration: {lastLeftStepDuration:0.000}");
            builder.AppendLine($"LocomotionRightLastStepDuration: {lastRightStepDuration:0.000}");
        }

        builder.AppendLine();
        builder.AppendLine("Samples");
        builder.AppendLine("time_s,fps,avg_frame_ms,max_frame_ms,working_set_mib,private_memory_mib,managed_heap_mib,managed_heap_delta_mib,gen0_collections,gen1_collections,gen2_collections,active_chunks,resident_chunks,loaded_chunks,ram_cache_chunks,desired_columns,desired_chunks,to_add,to_release,frontier,visited_candidates,pending_loads,running_loads,pending_activation,prepared_chunks,in_flight_chunks,dirty_render,dirty_collision,load_count,load_ms,ram_load_count,ram_load_ms,startup_load_count,startup_load_ms,persisted_load_count,persisted_load_ms,generated_load_count,generated_load_ms,attach_count,attach_ms,release_count,release_ms,render_count,render_ms,mesh_worker_count,mesh_worker_ms,mesh_worker_queue_wait_ms,collision_count,collision_ms,pending_mesh_builds,deferred_mesh_builds,running_mesh_builds,pending_mesh_commits,last_mesh_worker_queue_wait_ms,peak_mesh_worker_queue_wait_ms,low_priority_deferred_mesh_builds,deferred_detail_promotions,deferred_promotion_reevaluations,avoided_deferred_reevaluations,suppressed_deferred_log_repeats,requests_reactivated_by_mesh_completion,requests_reactivated_by_cooldown_expiry,requests_reactivated_by_pressure_exit,coalesced_rebuild_requests,mesh_backend,search_ms,priority_eval_ms,visibility_ms,resident_reuse_hits,ram_cache_hits,startup_hits,db_hits,generation_fallbacks,persisted_chunk_records,startup_snapshot_chunks,startup_desired_coverage,search_invalidations,stale_priority_refreshes,frontier_compactions,dirty_persist_writes,startup_promotion_writes,cache_hits,cache_misses,evicted_chunks,cache_hits_delta,cache_misses_delta,evicted_chunks_delta,search_state,initial_load_progress,initial_load_complete," + LocomotionMetrics.BuildCsvHeader());

        foreach (SamplePoint sample in _samples)
        {
            TerrainWorldProfileSnapshot snapshot = sample.Snapshot;
            if (snapshot == null)
            {
                builder.AppendLine(
                    $"{sample.TimeSeconds:0.00},{sample.Fps},{sample.AverageFrameMs:0.00},{sample.MaxFrameMs:0.00},{sample.WorkingSetMiB:0.00},{sample.PrivateMemoryMiB:0.00},{sample.ManagedHeapMiB:0.00},{sample.ManagedHeapDeltaMiB:0.00},{sample.Gen0CollectionsDelta},{sample.Gen1CollectionsDelta},{sample.Gen2CollectionsDelta},,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,," +
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
                sample.ManagedHeapDeltaMiB.ToString("0.00", CultureInfo.InvariantCulture),
                sample.Gen0CollectionsDelta.ToString(CultureInfo.InvariantCulture),
                sample.Gen1CollectionsDelta.ToString(CultureInfo.InvariantCulture),
                sample.Gen2CollectionsDelta.ToString(CultureInfo.InvariantCulture),
                snapshot.ActiveChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.ResidentChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.LoadedChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.RamCacheChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DesiredColumnCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DesiredChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.ToAddCount.ToString(CultureInfo.InvariantCulture),
                snapshot.ToReleaseCount.ToString(CultureInfo.InvariantCulture),
                snapshot.FrontierSize.ToString(CultureInfo.InvariantCulture),
                snapshot.VisitedCandidateCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PendingLoadCount.ToString(CultureInfo.InvariantCulture),
                snapshot.RunningLoadCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PendingActivationCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PreparedChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.InFlightChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DirtyRenderCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DirtyCollisionCount.ToString(CultureInfo.InvariantCulture),
                sample.TotalLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.TotalLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.RamLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.RamLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.StartupLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.StartupLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.PersistedLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.PersistedLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.GeneratedLoadCount.ToString(CultureInfo.InvariantCulture),
                sample.GeneratedLoadMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.AttachCount.ToString(CultureInfo.InvariantCulture),
                sample.AttachMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.ReleaseCount.ToString(CultureInfo.InvariantCulture),
                sample.ReleaseMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.RenderCount.ToString(CultureInfo.InvariantCulture),
                sample.RenderMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.MeshWorkerCount.ToString(CultureInfo.InvariantCulture),
                sample.MeshWorkerMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.MeshWorkerQueueWaitMsDelta.ToString("0.00", CultureInfo.InvariantCulture),
                sample.CollisionCount.ToString(CultureInfo.InvariantCulture),
                sample.CollisionMs.ToString("0.00", CultureInfo.InvariantCulture),
                snapshot.PendingMeshBuildCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DeferredMeshBuildCount.ToString(CultureInfo.InvariantCulture),
                snapshot.RunningMeshBuildCount.ToString(CultureInfo.InvariantCulture),
                snapshot.PendingMeshCommitCount.ToString(CultureInfo.InvariantCulture),
                snapshot.LastMeshWorkerQueueWaitMs.ToString("0.00", CultureInfo.InvariantCulture),
                snapshot.PeakMeshWorkerQueueWaitMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.LowPriorityDeferredMeshBuildsDelta.ToString(CultureInfo.InvariantCulture),
                sample.DeferredDetailPromotions.ToString(CultureInfo.InvariantCulture),
                sample.DeferredPromotionReevaluations.ToString(CultureInfo.InvariantCulture),
                sample.AvoidedDeferredReevaluations.ToString(CultureInfo.InvariantCulture),
                sample.SuppressedDeferredLogRepeats.ToString(CultureInfo.InvariantCulture),
                sample.RequestsReactivatedByMeshCompletion.ToString(CultureInfo.InvariantCulture),
                sample.RequestsReactivatedByCooldownExpiry.ToString(CultureInfo.InvariantCulture),
                sample.RequestsReactivatedByPressureExit.ToString(CultureInfo.InvariantCulture),
                sample.CoalescedRebuildRequests.ToString(CultureInfo.InvariantCulture),
                snapshot.MeshBackendName,
                sample.SearchMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.PriorityEvalMs.ToString("0.00", CultureInfo.InvariantCulture),
                sample.VisibilityMs.ToString("0.00", CultureInfo.InvariantCulture),
                snapshot.ResidentReuseHits.ToString(CultureInfo.InvariantCulture),
                snapshot.RamCacheHits.ToString(CultureInfo.InvariantCulture),
                snapshot.StartupSnapshotHits.ToString(CultureInfo.InvariantCulture),
                snapshot.DatabaseHits.ToString(CultureInfo.InvariantCulture),
                snapshot.GenerationFallbacks.ToString(CultureInfo.InvariantCulture),
                snapshot.PersistedChunkRecordCount.ToString(CultureInfo.InvariantCulture),
                snapshot.StartupSnapshotChunkCount.ToString(CultureInfo.InvariantCulture),
                snapshot.StartupDesiredCoverageCount.ToString(CultureInfo.InvariantCulture),
                snapshot.SearchInvalidationCount.ToString(CultureInfo.InvariantCulture),
                snapshot.StalePriorityRefreshCount.ToString(CultureInfo.InvariantCulture),
                snapshot.FrontierCompactionCount.ToString(CultureInfo.InvariantCulture),
                snapshot.DirtyPersistWrites.ToString(CultureInfo.InvariantCulture),
                snapshot.StartupPromotionWrites.ToString(CultureInfo.InvariantCulture),
                snapshot.CacheHits.ToString(CultureInfo.InvariantCulture),
                snapshot.CacheMisses.ToString(CultureInfo.InvariantCulture),
                snapshot.EvictedChunks.ToString(CultureInfo.InvariantCulture),
                sample.CacheHitsDelta.ToString(CultureInfo.InvariantCulture),
                sample.CacheMissesDelta.ToString(CultureInfo.InvariantCulture),
                sample.EvictionsDelta.ToString(CultureInfo.InvariantCulture),
                snapshot.SearchThrottleState,
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
        float ManagedHeapDeltaMiB,
        int Gen0CollectionsDelta,
        int Gen1CollectionsDelta,
        int Gen2CollectionsDelta,
        TerrainWorldProfileSnapshot Snapshot,
        int TotalLoadCount,
        double TotalLoadMs,
        int StartupLoadCount,
        double StartupLoadMs,
        int PersistedLoadCount,
        double PersistedLoadMs,
        int RamLoadCount,
        double RamLoadMs,
        int GeneratedLoadCount,
        double GeneratedLoadMs,
        int AttachCount,
        double AttachMs,
        int ReleaseCount,
        double ReleaseMs,
        int RenderCount,
        double RenderMs,
        int MeshWorkerCount,
        double MeshWorkerMs,
        double MeshWorkerQueueWaitMsDelta,
        int CollisionCount,
        double CollisionMs,
        int DeferredDetailPromotions,
        int DeferredPromotionReevaluations,
        int AvoidedDeferredReevaluations,
        int SuppressedDeferredLogRepeats,
        int RequestsReactivatedByMeshCompletion,
        int RequestsReactivatedByCooldownExpiry,
        int RequestsReactivatedByPressureExit,
        int CoalescedRebuildRequests,
        double SearchMs,
        double PriorityEvalMs,
        double VisibilityMs,
        long LowPriorityDeferredMeshBuildsDelta,
        long CacheHitsDelta,
        long CacheMissesDelta,
        long EvictionsDelta,
        LocomotionTelemetrySnapshot LocomotionSnapshot);

    private sealed record MemoryUsageSnapshot(
        float WorkingSetMiB,
        float PrivateMemoryMiB,
        float ManagedHeapMiB);
}
