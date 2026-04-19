using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TowerOfBaby.Terrain;

internal readonly record struct TerrainInstrumentationSnapshot(
    bool Enabled,
    long DeformOperationCount,
    long TotalEditedChunkCount,
    long TotalEditedSampleCount,
    double TotalEditedDirtyBoundsVolume,
    long EditDetailPromotionCount,
    int LastDeformEditedChunkCount,
    int LastDeformEditedSampleCount,
    double LastDeformDirtyBoundsVolume,
    int LastDeformEditDetailPromotionCount,
    double LastDeformMs,
    string LastDeformKind,
    long MeshBuildWorkerCount,
    double MeshBuildWorkerMs,
    double LastMeshBuildWorkerMs,
    double AverageCoarseMeshWorkerHeapDeltaKiB,
    double PeakCoarseMeshWorkerHeapDeltaKiB,
    double AverageDetailMeshWorkerHeapDeltaKiB,
    double PeakDetailMeshWorkerHeapDeltaKiB,
    long MeshRebuildCount,
    double MeshRebuildMs,
    double LastMeshRebuildMs,
    long CollisionRebuildCount,
    double CollisionRebuildMs,
    double LastCollisionRebuildMs,
    long DeferredDetailPromotionCount,
    long CoalescedRebuildRequestCount,
    long PersistenceLoadCount,
    double PersistenceLoadMs,
    double LastPersistenceLoadMs,
    string LastPersistenceLoadScope,
    long PersistenceSaveCount,
    double PersistenceSaveMs,
    double LastPersistenceSaveMs,
    string LastPersistenceSaveScope)
{
    public static TerrainInstrumentationSnapshot Empty =>
        new(
            Enabled: false,
            DeformOperationCount: 0,
            TotalEditedChunkCount: 0,
            TotalEditedSampleCount: 0,
            TotalEditedDirtyBoundsVolume: 0.0,
            EditDetailPromotionCount: 0,
            LastDeformEditedChunkCount: 0,
            LastDeformEditedSampleCount: 0,
            LastDeformDirtyBoundsVolume: 0.0,
            LastDeformEditDetailPromotionCount: 0,
            LastDeformMs: 0.0,
            LastDeformKind: "n/a",
            MeshBuildWorkerCount: 0,
            MeshBuildWorkerMs: 0.0,
            LastMeshBuildWorkerMs: 0.0,
            AverageCoarseMeshWorkerHeapDeltaKiB: 0.0,
            PeakCoarseMeshWorkerHeapDeltaKiB: 0.0,
            AverageDetailMeshWorkerHeapDeltaKiB: 0.0,
            PeakDetailMeshWorkerHeapDeltaKiB: 0.0,
            MeshRebuildCount: 0,
            MeshRebuildMs: 0.0,
            LastMeshRebuildMs: 0.0,
            CollisionRebuildCount: 0,
            CollisionRebuildMs: 0.0,
            LastCollisionRebuildMs: 0.0,
            DeferredDetailPromotionCount: 0,
            CoalescedRebuildRequestCount: 0,
            PersistenceLoadCount: 0,
            PersistenceLoadMs: 0.0,
            LastPersistenceLoadMs: 0.0,
            LastPersistenceLoadScope: "n/a",
            PersistenceSaveCount: 0,
            PersistenceSaveMs: 0.0,
            LastPersistenceSaveMs: 0.0,
            LastPersistenceSaveScope: "n/a");
}

// Legacy-only trace writer for TerrainLegacyWorldRuntime. Current gameplay uses
// TerrainLodManager + TerrainTelemetry, so keep this isolated from the live path.
internal sealed class TerrainStatsTracker
{
    private const string Prefix = "[TerrainStats]";
    private const string LogRelativePath = "user://profiling/legacy_terrain_stats_latest.log";
    private const int DeferredDetailSuppressionFlushThreshold = 32;
    private readonly object _logLock = new();
    private readonly StreamWriter _logWriter;
    private readonly Dictionary<Vector3I, DeferredDetailLogState> _deferredDetailLogStates = new();

    public TerrainStatsTracker(bool enabled)
    {
        Enabled = enabled;
        if (!Enabled)
        {
            return;
        }

        string rootPath = ProjectSettings.GlobalizePath("user://profiling");
        Directory.CreateDirectory(rootPath);
        string logPath = ProjectSettings.GlobalizePath(LogRelativePath);
        _logWriter = new StreamWriter(
            new FileStream(logPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        WriteLine(
            $"{Prefix} event=session_begin utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} path=\"{logPath}\"");
    }

    public bool Enabled { get; }

    private long _deformOperationCount;
    private long _totalEditedChunkCount;
    private long _totalEditedSampleCount;
    private double _totalEditedDirtyBoundsVolume;
    private long _editDetailPromotionCount;
    private int _lastDeformEditedChunkCount;
    private int _lastDeformEditedSampleCount;
    private double _lastDeformDirtyBoundsVolume;
    private int _lastDeformEditDetailPromotionCount;
    private double _lastDeformMs;
    private string _lastDeformKind = "n/a";

    private long _meshRebuildCount;
    private double _meshRebuildMs;
    private double _lastMeshRebuildMs;

    private long _meshBuildWorkerCount;
    private double _meshBuildWorkerMs;
    private double _lastMeshBuildWorkerMs;
    private long _coarseMeshBuildCount;
    private double _coarseMeshBuildHeapDeltaKiB;
    private double _peakCoarseMeshBuildHeapDeltaKiB;
    private long _detailMeshBuildCount;
    private double _detailMeshBuildHeapDeltaKiB;
    private double _peakDetailMeshBuildHeapDeltaKiB;

    private long _collisionRebuildCount;
    private double _collisionRebuildMs;
    private double _lastCollisionRebuildMs;

    private long _deferredDetailPromotionCount;
    private long _coalescedRebuildRequestCount;

    private long _persistenceLoadCount;
    private double _persistenceLoadMs;
    private double _lastPersistenceLoadMs;
    private string _lastPersistenceLoadScope = "n/a";

    private long _persistenceSaveCount;
    private double _persistenceSaveMs;
    private double _lastPersistenceSaveMs;
    private string _lastPersistenceSaveScope = "n/a";

    public TerrainInstrumentationSnapshot GetSnapshot()
    {
        if (!Enabled)
        {
            return TerrainInstrumentationSnapshot.Empty;
        }

        return new TerrainInstrumentationSnapshot(
            Enabled,
            _deformOperationCount,
            _totalEditedChunkCount,
            _totalEditedSampleCount,
            _totalEditedDirtyBoundsVolume,
            _editDetailPromotionCount,
            _lastDeformEditedChunkCount,
            _lastDeformEditedSampleCount,
            _lastDeformDirtyBoundsVolume,
            _lastDeformEditDetailPromotionCount,
            _lastDeformMs,
            _lastDeformKind,
            _meshBuildWorkerCount,
            _meshBuildWorkerMs,
            _lastMeshBuildWorkerMs,
            _coarseMeshBuildCount > 0 ? _coarseMeshBuildHeapDeltaKiB / _coarseMeshBuildCount : 0.0,
            _peakCoarseMeshBuildHeapDeltaKiB,
            _detailMeshBuildCount > 0 ? _detailMeshBuildHeapDeltaKiB / _detailMeshBuildCount : 0.0,
            _peakDetailMeshBuildHeapDeltaKiB,
            _meshRebuildCount,
            _meshRebuildMs,
            _lastMeshRebuildMs,
            _collisionRebuildCount,
            _collisionRebuildMs,
            _lastCollisionRebuildMs,
            _deferredDetailPromotionCount,
            _coalescedRebuildRequestCount,
            _persistenceLoadCount,
            _persistenceLoadMs,
            _lastPersistenceLoadMs,
            _lastPersistenceLoadScope,
            _persistenceSaveCount,
            _persistenceSaveMs,
            _lastPersistenceSaveMs,
            _lastPersistenceSaveScope);
    }

    public void LogDeformBegin(string operation, Vector3 center, float radius, float strength)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=deform_begin op={operation} center={FormatVector(center)} radius={radius:0.00} strength={strength:0.00}");
    }

    public void RecordDeform(
        string operation,
        double ms,
        int editedChunks,
        int editedSamples,
        double dirtyBoundsVolume,
        int detailPromotions)
    {
        if (!Enabled)
        {
            return;
        }

        _deformOperationCount++;
        _totalEditedChunkCount += editedChunks;
        _totalEditedSampleCount += editedSamples;
        _totalEditedDirtyBoundsVolume += dirtyBoundsVolume;
        _editDetailPromotionCount += detailPromotions;
        _lastDeformEditedChunkCount = editedChunks;
        _lastDeformEditedSampleCount = editedSamples;
        _lastDeformDirtyBoundsVolume = dirtyBoundsVolume;
        _lastDeformEditDetailPromotionCount = detailPromotions;
        _lastDeformMs = ms;
        _lastDeformKind = operation;

        WriteLine(
            $"{Prefix} event=deform_end op={operation} ms={ms:0.000} edited_chunks={editedChunks} edited_samples={editedSamples} dirty_volume={dirtyBoundsVolume:0.000} detail_promotions={detailPromotions}");
    }

    public void LogChunkRemeshBegin(Vector3I key, string phase, TerrainChunkDirtyBoundsSnapshot dirtyBounds)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=chunk_remesh_begin chunk={FormatVector(key)} phase={phase} dirty_volume={dirtyBounds.Volume:0.000} dirty_coverage={dirtyBounds.Coverage:0.000} dirty_bounds={FormatDirtyBounds(dirtyBounds)}");
    }

    public void RecordMeshBuildWorker(
        Vector3I key,
        double ms,
        double queueWaitMs,
        int queueDepth,
        TerrainVisualBuildRequestKind buildKind,
        TerrainVisualBuildQueueClass queueClass,
        TerrainChunkDirtyBoundsSnapshot dirtyBounds,
        long managedHeapDeltaBytes,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections,
        bool usedDetailBrick,
        bool usedPersistentEdits,
        int detailTriangleCount,
        int replacedCoarseCellCount,
        int totalTriangleCount)
    {
        if (!Enabled)
        {
            return;
        }

        _meshBuildWorkerCount++;
        _meshBuildWorkerMs += ms;
        _lastMeshBuildWorkerMs = ms;
        double heapDeltaKiB = Math.Abs(managedHeapDeltaBytes) / 1024.0;
        if (usedDetailBrick)
        {
            _detailMeshBuildCount++;
            _detailMeshBuildHeapDeltaKiB += heapDeltaKiB;
            _peakDetailMeshBuildHeapDeltaKiB = Math.Max(_peakDetailMeshBuildHeapDeltaKiB, heapDeltaKiB);
        }
        else
        {
            _coarseMeshBuildCount++;
            _coarseMeshBuildHeapDeltaKiB += heapDeltaKiB;
            _peakCoarseMeshBuildHeapDeltaKiB = Math.Max(_peakCoarseMeshBuildHeapDeltaKiB, heapDeltaKiB);
        }

        WriteLine(
            $"{Prefix} event=chunk_remesh_end chunk={FormatVector(key)} phase=mesh_worker kind={buildKind} queue_class={queueClass} ms={ms:0.000} queue_wait_ms={queueWaitMs:0.000} queue_depth={queueDepth} heap_delta_kib={managedHeapDeltaBytes / 1024.0:0.0} gc0={gen0Collections} gc1={gen1Collections} gc2={gen2Collections} gc_triggered={(gen0Collections + gen1Collections + gen2Collections) > 0} dirty_volume={dirtyBounds.Volume:0.000} dirty_coverage={dirtyBounds.Coverage:0.000} dirty_bounds={FormatDirtyBounds(dirtyBounds)} detail_hi={usedDetailBrick} edit_hi={usedPersistentEdits} detail_tris={detailTriangleCount} replace_cells={replacedCoarseCellCount} total_tris={totalTriangleCount}");
        WriteLine(
            $"{Prefix} event=mesh_queue_wait chunk={FormatVector(key)} kind={buildKind} queue_class={queueClass} queue_wait_ms={queueWaitMs:0.000} queue_depth={queueDepth}");
    }

    public void RecordMeshCommit(
        Vector3I key,
        double ms,
        TerrainChunkDirtyBoundsSnapshot dirtyBounds,
        bool usedDetailBrick,
        bool usedPersistentEdits,
        int detailTriangleCount,
        int replacedCoarseCellCount,
        int totalTriangleCount)
    {
        if (!Enabled)
        {
            return;
        }

        _meshRebuildCount++;
        _meshRebuildMs += ms;
        _lastMeshRebuildMs = ms;
        WriteLine(
            $"{Prefix} event=chunk_remesh_end chunk={FormatVector(key)} phase=mesh_commit ms={ms:0.000} dirty_volume={dirtyBounds.Volume:0.000} dirty_coverage={dirtyBounds.Coverage:0.000} dirty_bounds={FormatDirtyBounds(dirtyBounds)} detail_hi={usedDetailBrick} edit_hi={usedPersistentEdits} detail_tris={detailTriangleCount} replace_cells={replacedCoarseCellCount} total_tris={totalTriangleCount}");
    }

    public void RecordMeshResultDecision(
        Vector3I key,
        string decision,
        TerrainVisualBuildRequestKind buildKind,
        TerrainVisualBuildQueueClass queueClass,
        int totalTriangleCount,
        int detailTriangleCount,
        int replacedCoarseCellCount,
        bool usedDetailBrick,
        bool usedPersistentEdits)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=mesh_result_resolution chunk={FormatVector(key)} decision={Sanitize(decision)} kind={buildKind} queue_class={queueClass} detail_hi={usedDetailBrick} edit_hi={usedPersistentEdits} detail_tris={detailTriangleCount} replace_cells={replacedCoarseCellCount} total_tris={totalTriangleCount}");
    }

    public void RecordMeshSchedulingDecision(
        Vector3I key,
        string category,
        TerrainVisualBuildRequestKind buildKind,
        TerrainVisualBuildQueueClass queueClass,
        TerrainChunkLoadSource loadSource,
        int totalTriangleCount,
        string reason)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=mesh_schedule_decision chunk={FormatVector(key)} category={Sanitize(category)} kind={buildKind} queue_class={queueClass} source={loadSource} total_tris={totalTriangleCount} reason=\"{Sanitize(reason)}\"");
    }

    public void RecordCollisionRebuild(
        Vector3I key,
        double ms,
        TerrainChunkDirtyBoundsSnapshot dirtyBounds,
        bool usedDetailBrick,
        bool usedPersistentEdits,
        int detailTriangleCount,
        int replacedCoarseCellCount,
        int totalTriangleCount)
    {
        if (!Enabled)
        {
            return;
        }

        _collisionRebuildCount++;
        _collisionRebuildMs += ms;
        _lastCollisionRebuildMs = ms;
        WriteLine(
            $"{Prefix} event=chunk_remesh_end chunk={FormatVector(key)} phase=collision ms={ms:0.000} dirty_volume={dirtyBounds.Volume:0.000} dirty_coverage={dirtyBounds.Coverage:0.000} dirty_bounds={FormatDirtyBounds(dirtyBounds)} detail_hi={usedDetailBrick} edit_hi={usedPersistentEdits} detail_tris={detailTriangleCount} replace_cells={replacedCoarseCellCount} total_tris={totalTriangleCount}");
    }

    public void RecordChunkLoadSource(Vector3I key, TerrainChunkLoadSource source, double ms, string context)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=chunk_load_source chunk={FormatVector(key)} source={source} context={context} ms={ms:0.000}");

        if (source is not TerrainChunkLoadSource.StartupSnapshot and not TerrainChunkLoadSource.PersistedChunk)
        {
            return;
        }

        string scope = source == TerrainChunkLoadSource.StartupSnapshot
            ? "startup_snapshot"
            : "persisted_chunk";

        _persistenceLoadCount++;
        _persistenceLoadMs += ms;
        _lastPersistenceLoadMs = ms;
        _lastPersistenceLoadScope = scope;

        WriteLine(
            $"{Prefix} event=chunk_load scope={scope} chunk={FormatVector(key)} source={source} ms={ms:0.000} hit=true");
    }

    public void RecordPersistenceLoad(string scope, double ms, bool hit, int itemCount = 0)
    {
        if (!Enabled)
        {
            return;
        }

        _persistenceLoadCount++;
        _persistenceLoadMs += ms;
        _lastPersistenceLoadMs = ms;
        _lastPersistenceLoadScope = scope;

        WriteLine($"{Prefix} event=chunk_load scope={scope} ms={ms:0.000} hit={hit} item_count={itemCount}");
    }

    public void RecordChunkSave(Vector3I key, string scope, double ms, bool dirty)
    {
        if (!Enabled)
        {
            return;
        }

        _persistenceSaveCount++;
        _persistenceSaveMs += ms;
        _lastPersistenceSaveMs = ms;
        _lastPersistenceSaveScope = scope;

        WriteLine(
            $"{Prefix} event=chunk_save scope={scope} chunk={FormatVector(key)} ms={ms:0.000} dirty={dirty} item_count=1");
    }

    public void RecordPersistenceSave(string scope, double ms, int itemCount)
    {
        if (!Enabled)
        {
            return;
        }

        _persistenceSaveCount++;
        _persistenceSaveMs += ms;
        _lastPersistenceSaveMs = ms;
        _lastPersistenceSaveScope = scope;

        WriteLine($"{Prefix} event=chunk_save scope={scope} ms={ms:0.000} item_count={itemCount}");
    }

    public void LogDetailRegionRequest(Vector3I key, TerrainDetailRegion region)
    {
        if (!Enabled || region == null)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=detail_region_request chunk={FormatVector(key)} source={region.Source} level={region.RequestedDetailLevel} priority={region.Priority:0.00} sticky={region.Sticky} reason=\"{Sanitize(region.Reason)}\" bounds={FormatAabb(region.LocalBounds)} dirty={region.Dirty}");
    }

    public void LogDetailRegionRemoval(Vector3I key, TerrainDetailRegionSource source, int removedCount)
    {
        if (!Enabled || removedCount <= 0)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=detail_region_remove chunk={FormatVector(key)} source={source} removed={removedCount}");
    }

    public void LogChunkDirtyBounds(
        Vector3I key,
        string source,
        Aabb requestedBounds,
        TerrainChunkDirtyBoundsSnapshot mergedBounds,
        bool detailPromoted)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=chunk_dirty_bounds chunk={FormatVector(key)} source={source} requested={FormatAabb(requestedBounds)} merged={FormatDirtyBounds(mergedBounds)} merged_volume={mergedBounds.Volume:0.000} merged_coverage={mergedBounds.Coverage:0.000} detail_promoted={detailPromoted}");
    }

    public void RecordCoverageHold(Vector3I key, string reason, bool replacementCoveragePending)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=coverage_hold chunk={FormatVector(key)} replacement_pending={replacementCoveragePending} reason=\"{Sanitize(reason)}\"");
    }

    public void RecordCoverageReleaseBlocked(Vector3I key, string reason)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=coverage_release_blocked chunk={FormatVector(key)} reason=\"{Sanitize(reason)}\"");
    }

    public void RecordStaleMeshCleared(Vector3I key, string reason, int previousTriangleCount)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=stale_mesh_cleared chunk={FormatVector(key)} previous_total_tris={previousTriangleCount} reason=\"{Sanitize(reason)}\"");
    }

    public void RecordEmptyVerticalChunkSkipped(Vector3I key, string reason, float surfaceMaxY, float chunkMinY)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=desired_vertical_skip chunk={FormatVector(key)} reason=\"{Sanitize(reason)}\" surface_max_y={surfaceMaxY:0.00} chunk_min_y={chunkMinY:0.00}");
    }

    public bool RecordDeferredDetailPromotion(Vector3I key, string reason)
    {
        if (!Enabled)
        {
            return false;
        }

        _deferredDetailPromotionCount++;
        string sanitizedReason = Sanitize(reason);
        if (_deferredDetailLogStates.TryGetValue(key, out DeferredDetailLogState state))
        {
            if (string.Equals(state.Reason, sanitizedReason, StringComparison.Ordinal))
            {
                state.SuppressedRepeats++;
                if (state.SuppressedRepeats >= DeferredDetailSuppressionFlushThreshold)
                {
                    FlushSuppressedDeferredDetailPromotion(key, state);
                }

                return true;
            }

            FlushSuppressedDeferredDetailPromotion(key, state);
            state.Reason = sanitizedReason;
            WriteLine($"{Prefix} event=detail_promotion_deferred chunk={FormatVector(key)} reason={sanitizedReason}");
            return false;
        }

        _deferredDetailLogStates[key] = new DeferredDetailLogState(sanitizedReason);
        WriteLine($"{Prefix} event=detail_promotion_deferred chunk={FormatVector(key)} reason={sanitizedReason}");
        return false;
    }

    public void RecordDetailPromotionEligible(Vector3I key, string trigger)
    {
        if (!Enabled || !_deferredDetailLogStates.TryGetValue(key, out DeferredDetailLogState state))
        {
            return;
        }

        FlushSuppressedDeferredDetailPromotion(key, state);
        WriteLine(
            $"{Prefix} event=detail_promotion_eligible chunk={FormatVector(key)} previous_reason={state.Reason} trigger={Sanitize(trigger)}");
        _deferredDetailLogStates.Remove(key);
    }

    public void RecordCoalescedRebuildRequest(Vector3I key, string queue, string reason)
    {
        if (!Enabled)
        {
            return;
        }

        _coalescedRebuildRequestCount++;
        WriteLine($"{Prefix} event=rebuild_request_coalesced chunk={FormatVector(key)} queue={queue} reason={Sanitize(reason)}");
    }

    public void RecordSkippedLowPriorityBuild(Vector3I key, string queue, string reason)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine($"{Prefix} event=rebuild_request_skipped chunk={FormatVector(key)} queue={queue} reason={Sanitize(reason)}");
    }

    public void RecordSuppressedRebuildRequest(Vector3I key, string queue, string reason)
    {
        if (!Enabled)
        {
            return;
        }

        WriteLine($"{Prefix} event=rebuild_request_suppressed chunk={FormatVector(key)} queue={queue} reason={Sanitize(reason)}");
    }

    public void Close()
    {
        if (!Enabled || _logWriter == null)
        {
            return;
        }

        foreach ((Vector3I key, DeferredDetailLogState state) in _deferredDetailLogStates)
        {
            FlushSuppressedDeferredDetailPromotion(key, state);
        }

        lock (_logLock)
        {
            _logWriter.WriteLine(
                $"{Prefix} event=session_end utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
            _logWriter.Dispose();
        }
    }

    private void WriteLine(string line)
    {
        if (!Enabled || _logWriter == null)
        {
            return;
        }

        lock (_logLock)
        {
            _logWriter.WriteLine(line);
        }
    }

    private void FlushSuppressedDeferredDetailPromotion(Vector3I key, DeferredDetailLogState state)
    {
        if (!Enabled || state == null || state.SuppressedRepeats <= 0)
        {
            return;
        }

        WriteLine(
            $"{Prefix} event=detail_promotion_deferred_suppressed chunk={FormatVector(key)} reason={state.Reason} repeats={state.SuppressedRepeats}");
        state.SuppressedRepeats = 0;
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.X:0.00},{value.Y:0.00},{value.Z:0.00})";
    }

    private static string FormatVector(Vector3I value)
    {
        return $"({value.X},{value.Y},{value.Z})";
    }

    private static string FormatAabb(Aabb value)
    {
        return
            $"({value.Position.X:0.00},{value.Position.Y:0.00},{value.Position.Z:0.00}|{value.Size.X:0.00},{value.Size.Y:0.00},{value.Size.Z:0.00})";
    }

    private static string FormatDirtyBounds(TerrainChunkDirtyBoundsSnapshot dirtyBounds)
    {
        if (!dirtyBounds.HasBounds)
        {
            return "none";
        }

        return
            $"{FormatAabb(dirtyBounds.LocalBounds)} vox={FormatVector(dirtyBounds.VoxelMin)}->{FormatVector(dirtyBounds.VoxelMax)}";
    }

    private static string Sanitize(string value)
    {
        return (value ?? string.Empty).Replace('"', '\'');
    }

    private sealed class DeferredDetailLogState
    {
        public DeferredDetailLogState(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; set; }
        public int SuppressedRepeats { get; set; }
    }
}
