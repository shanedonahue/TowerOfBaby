# Telemetry Policy

Current gameplay telemetry is split into four layers:

1. Always-on cheap telemetry
`TerrainWorldProfileSnapshot` is the canonical live source.
Only cheap counters, queue depths, rolling timings, event totals, and mode flags belong here.

2. Live HUD
`FpsOverlay` is the default in-game consumer.
Collapsed mode should stay cheap.
Expanded text should rebuild on a timer, not every frame.

3. Capture sessions
`TelemetryCaptureSession` is opt-in only.
It samples the cheap live snapshot on an interval and writes one primary JSON artifact at the end of the run.
It must never be auto-added to gameplay by default.

4. Subsystem trace probes
High-detail traces are opt-in only.
Current probe families are:
`lod_transition`
`grass`
`deform`
`persistence`

## Rules For Future Changes

1. Cheap metrics go into `TerrainWorldProfileSnapshot`.
2. Expensive or verbose diagnostics go behind an explicit probe flag.
3. Do not do synchronous file IO from gameplay `_Process()`.
4. Do not call `Process.GetCurrentProcess()` or `Refresh()` from gameplay loops.
5. Do not add new ad hoc log files unless they are an explicit probe artifact.
6. Capture sessions should write one main artifact.
7. Each enabled probe should write zero or one artifact for the run.
8. Legacy-only telemetry must be labeled as legacy/retired so it is not confused with the `lod_blocks` runtime.

## Current Entry Points

- HUD toggle: `F1`
- Capture toggle: `F7` in debug builds
- Terrain debug view cycle: `F6`

## Command Line Flags

- `--telemetry-capture`
- `--telemetry-capture-interval=<seconds>`
- `--telemetry-expensive`
- `--telemetry-probes=lod_transition,grass,deform,persistence`

## Output Policy

- Main capture artifact: `user://profiling/capture_<timestamp>.json`
- Probe artifacts: `user://profiling/probe_<probe>_<timestamp>.log`
- Legacy runtime artifact: `user://profiling/legacy_terrain_stats_latest.log`

If a new feature needs telemetry, start by asking:
"Is this cheap live state, or is this an opt-in probe?"
