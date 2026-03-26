# Terrain / Locomotion Log Investigation - 2026-03-25

Artifact investigated first:
- `/home/shanedonahue/.local/share/godot/app_userdata/TowerOfBaby/profiling/run_20260325_230755.log`

## Suspicious findings from the latest log

1. Persistence path looked inactive.
- Summary showed `PersistedChunkLoads: 0` while startup warm loads were high (`StartupChunkLoads: 267`).
- That raised the question of whether DB-backed restore was unreachable or whether startup snapshot data was masking it.

2. Desired-set search remained expensive after startup.
- Summary showed `AccumulatedDesiredSearchMs: 8263.63`, `AccumulatedPriorityEvalMs: 9078.53`, and `AccumulatedVisibilityMs: 8558.08`.
- The sample table also showed `stale_priority_refreshes` climbing to `35987` with `PeakFrontier: 18051`, which is much larger than the live visited-candidate count.

3. Startup/load scheduling still showed churn.
- The run completed startup, but pending work stayed busy while the desired set was already full.
- That made the queued-load and frontier stale-entry paths worth checking for redundant work.

4. Render rebuild cost was still significant.
- Summary showed `AccumulatedRenderRebuildMs: 9904.09`.
- This needed to be traced to determine whether chunks were rebuilding unnecessarily or whether the cost mostly came from legitimate activation churn.

5. Locomotion reasons were dominated by `trailing support`.
- The latest log still showed symmetric stepping and `LocomotionPeakFootSkate: 0.000`.
- That suggested the locomotion debug data might be honest but needed to be verified against the source of the reason strings.

## Metric -> code path -> confirmed cause / no-issue result

### 1. `PersistedChunkLoads: 0`
- Metric path:
  `PerformanceRunLogger` -> `TerrainWorldProfileSnapshot.LastPersistedChunkLoadCount` -> `TerrainWorld.RegisterLoadStats` -> `TerrainCacheManager.AcquireChunk`
- Confirmed cause:
  DB restore was reachable, but startup-snapshot loads were incorrectly adding keys to the cache manager's persisted-key set even when no `chunks` row existed.
- Why that was bad:
  It made cost estimation and future diagnostics treat startup-only data as DB-backed.
  It also allowed startup-restored chunks to be discarded clean later without ever being promoted into the real DB-backed cache.
- Fix applied:
  `TerrainCacheManager` now tracks startup-only keys separately from actual DB-backed keys.
  Clean startup-restored chunks are promoted into the `chunks` table on first clean eviction if they do not already have DB backing.
  Added counters so the next run can show actual persisted-record availability and startup-to-DB promotions.

### 2. `stale_priority_refreshes` and large frontier size
- Metric path:
  `TerrainDesiredSetBuilder.StaleRefreshCount` / `FrontierCount` -> `TerrainWorldProfileSnapshot` -> `PerformanceRunLogger`
- Confirmed cause:
  The desired-set builder could enqueue the same candidate repeatedly within an epoch, keep stale frontier entries after repeated invalidations, and leave selected keys behind in the candidate map.
- Why that was bad:
  The latest log's `35987` stale refreshes and `PeakFrontier: 18051` are consistent with heap bloat rather than purely useful search work.
- Fix applied:
  Candidate enqueueing is now deduplicated within an epoch.
  Selected columns are removed from candidate state when selected.
  The frontier is compacted when heap size grows far beyond the live candidate set, preserving membership but dropping stale heap baggage.

### 3. Startup / queued load churn
- Metric path:
  `to_add`, pending loads, and load-source counts -> `TerrainLoadScheduler.SyncTargets`
- Confirmed cause:
  Queued load targets were being re-enqueued every frame while still waiting to start.
- Why that was bad:
  Behavior stayed mostly correct because tokens invalidated older entries, but the heap still accumulated redundant stale work during startup bursts.
- Fix applied:
  `TerrainLoadScheduler` now deduplicates queued load requests unless their priority materially changes.

### 4. Render rebuild time
- Metric path:
  `TerrainWorld.ProcessPendingChunkActivations` / `ProcessDirtyChunks` -> `TerrainChunk.SetData` / `RebuildRenderMesh`
- Result:
  No clear duplicate-rebuild bug was confirmed from the code path.
  Most rebuild cost in this run still appears to come from legitimate chunk activation churn rather than the same chunk being re-enqueued repeatedly after becoming clean.
- Fix applied:
  No direct render-path rewrite in this pass.
  Added better cache/load instrumentation so the next run can separate `resident`, `ram`, `startup`, `db`, and `generated` behavior more clearly.

### 5. Locomotion reason dominance
- Metric path:
  log reason strings -> `LocomotionTelemetrySnapshot` -> `FootPlanner.BuildCandidate`
- Result:
  The telemetry is honest.
  `trailing support`, `lateral drift`, `height mismatch`, and `overreaching support` all come directly from `FootPlanner` threshold comparisons.
  The latest log did not show an instrumentation bug, so no locomotion code change was made here.

## Fixes applied

- `TerrainCacheManager`
  - stopped treating startup-snapshot loads as already DB-backed
  - tracks startup-only keys separately
  - promotes startup-only chunks into the DB on first clean eviction
  - added `StartupPromotionWrites`
- `TerrainDesiredSetBuilder`
  - deduplicates candidate enqueueing inside an epoch
  - removes selected columns from candidate state
  - compacts bloated frontier heaps
  - added `FrontierCompactionCount`
- `TerrainLoadScheduler`
  - deduplicates already-queued load requests
- `TerrainWorld` / profiling
  - profile snapshot now exposes RAM cache count, startup-promotion writes, and frontier compactions
  - debug summary now separates resident vs RAM cache and surfaces the new counters

## Still uncertain

- The latest log predated these fixes, so the real validation has to come from the next run.
- I could not query the SQLite file with `sqlite3` in this shell because the binary is not on this PATH; the persistence investigation above was done by tracing the local C# code paths directly.
- Render rebuild cost may still be high after these fixes if activation churn remains high for valid gameplay reasons.

