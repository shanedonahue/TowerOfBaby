# Terrain Persistence Schema

## Overview

Terrain persistence currently uses two storage layers:

- Base chunk payloads in the `chunks` and `startup_chunks` tables.
- Optional adaptive-detail payloads in the existing `detail_brick_blob` column.
- World-space edit-region payloads in the `edit_regions` table.

Untouched terrain should remain deterministic from the world seed and procedural generation rules. Persisted data only overrides that deterministic result when we have real chunk edits or cached startup snapshots.

## World Edit Regions

The active `TerrainLodManager` runtime now keeps edit fidelity in a separate world-space authority:

- `edit_regions` rows are independent of camera residency and block lifetime
- each row stores:
  - world-space bounds metadata
  - requested detail level
  - a serialized `TerrainEditRegion` payload
- the serialized payload contains:
  - sticky edit/detail metadata similar to `TerrainPersistedDetailRegionData`
  - one or more world-edit stamps (sphere/slash today)

On load, the LOD runtime restores these regions first and then any block field build queries overlapping regions before mesh generation. This keeps untouched terrain on normal camera-driven LOD while edited areas can request local higher-resolution sampling even when the player is far away.

## Base Chunk Data

Each stored chunk row contains:

- `points_per_axis`
- `voxel_size`
- `iso_level`
- `origin_*`
- `densities_blob`
- `materials_blob`
- `updated_at_unix`

These coarse voxel buffers are the authoritative persisted override for edited chunks. If a chunk has no stored row, terrain falls back to normal procedural generation.

## Adaptive Detail Data

`detail_brick_blob` now stores a versioned adaptive-detail envelope:

- Magic/schema marker: `VoxelAdaptiveDetailState` schema `1`
- Persisted detail regions:
  - request id
  - local bounds
  - requested detail level
  - source
  - reason
  - priority
  - sticky flag
- One persisted high-detail brick payload:
  - stored with the existing `VoxelDetailBrickData` serializer
  - contains the local higher-resolution voxel field for the edited region

Older saves may still contain a legacy raw `VoxelDetailBrickData` blob with no outer envelope. Load code treats that as a legacy edited-detail brick and restores a default sticky edit region around the brick bounds.

## What Is Deterministic vs Persisted

Deterministic:

- Normal coarse terrain generation for chunks with no saved row
- Temporary local detail requested by player proximity
- Temporary local detail requested by biome policy
- Temporary local detail requested by structure influence

Persisted:

- Coarse voxel edits written into chunk density/material buffers
- Sticky edited high-detail brick data
- Sticky persisted detail-region metadata used to restore edited local detail on load
- World-space edit-region registrations used by the active LOD runtime

Not persisted:

- Temporary adaptive-detail requests that do not contain real edits
- Transient detail-brick expansion caused by nearby player/biome/structure requests

When a chunk has both sticky edited detail and temporary nearby detail, save/export crops the persisted high-detail payload back to the sticky edited region before serialization.

## Load Behavior

- If a chunk row exists, coarse chunk buffers load from storage.
- If `detail_brick_blob` exists and parses successfully, the sticky detail region metadata and high-detail brick are restored.
- If `detail_brick_blob` is absent, the chunk uses only the coarse stored buffers.
- If `detail_brick_blob` is invalid, load falls back to coarse chunk data and logs a warning.

## Logging

Adaptive-detail save/load logs report:

- detail brick count
- persisted detail region count
- serialized byte size
- schema version

This is intended to make edited high-detail persistence easy to confirm during save/reload testing.
