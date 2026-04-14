using Godot;
using System;
using System.Collections.Generic;
using System.Text;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainEditRegionMutationResult(
    bool Changed,
    Aabb DirtyWorldBounds,
    int ActiveRegionCount,
    int ActiveStampCount,
    int MaxDetailLevel,
    string Summary)
{
    public static TerrainEditRegionMutationResult None =>
        new(false, default, 0, 0, 0, "none");
}

public sealed class TerrainEditRegionManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TerrainEditRegion> _regions = new();
    private readonly TerrainChunkStore _store;
    private readonly float _baseVoxelSize;

    public TerrainEditRegionManager(TerrainChunkStore store, float baseVoxelSize)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _baseVoxelSize = Mathf.Max(0.0001f, baseVoxelSize);

        foreach (TerrainEditRegion region in _store.LoadPersistedEditRegions())
        {
            _regions[region.Id] = region;
        }
    }

    public int RegionCount
    {
        get
        {
            lock (_gate)
            {
                return _regions.Count;
            }
        }
    }

    public int StampCount
    {
        get
        {
            lock (_gate)
            {
                return ComputeStampCount(_regions.Values);
            }
        }
    }

    public int MaxDetailLevel
    {
        get
        {
            lock (_gate)
            {
                return ComputeMaxDetailLevel(_regions.Values);
            }
        }
    }

    public TerrainEditRegion[] QueryOverlapping(Aabb worldBounds)
    {
        lock (_gate)
        {
            if (_regions.Count == 0)
            {
                return Array.Empty<TerrainEditRegion>();
            }

            List<TerrainEditRegion> results = new();
            foreach (TerrainEditRegion region in _regions.Values)
            {
                if (region.Overlaps(worldBounds))
                {
                    results.Add(region);
                }
            }

            return results.ToArray();
        }
    }

    public TerrainEditRegionMutationResult RegisterStamp(
        TerrainEditStampData stamp,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority,
        bool sticky,
        string requestId = "")
    {
        TerrainEditRegion regionToSave;
        List<string> removedRegionIds = new();
        Aabb dirtyWorldBounds = stamp.WorldBounds;

        lock (_gate)
        {
            TerrainEditRegion seedRegion = string.IsNullOrWhiteSpace(requestId)
                ? null
                : _regions.GetValueOrDefault(requestId.Trim());
            List<TerrainEditRegion> overlaps = new();
            foreach (TerrainEditRegion existing in _regions.Values)
            {
                if (seedRegion != null && string.Equals(existing.Id, seedRegion.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!CanMerge(existing, stamp.WorldBounds, source, reason, sticky))
                {
                    continue;
                }

                overlaps.Add(existing);
            }

            if (seedRegion == null)
            {
                if (overlaps.Count > 0)
                {
                    overlaps.Sort(static (a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
                    seedRegion = overlaps[0];
                    overlaps.RemoveAt(0);
                }
                else
                {
                    seedRegion = new TerrainEditRegion(
                        requestId,
                        stamp.WorldBounds,
                        requestedDetailLevel,
                        source,
                        reason,
                        priority,
                        sticky,
                        Array.Empty<TerrainEditStampData>());
                }
            }

            TerrainEditRegion merged = seedRegion.AppendStamp(stamp, requestedDetailLevel, priority);
            dirtyWorldBounds = Union(dirtyWorldBounds, seedRegion.WorldBounds);
            foreach (TerrainEditRegion existing in overlaps)
            {
                merged = merged.Merge(existing);
                removedRegionIds.Add(existing.Id);
                dirtyWorldBounds = Union(dirtyWorldBounds, existing.WorldBounds);
            }

            _regions[merged.Id] = merged;
            foreach (string removedId in removedRegionIds)
            {
                _regions.Remove(removedId);
            }

            regionToSave = merged;
        }

        _store.SaveEditRegion(regionToSave);
        foreach (string removedRegionId in removedRegionIds)
        {
            _store.DeleteEditRegion(removedRegionId);
        }

        return BuildMutationResult(
            dirtyWorldBounds,
            $"{regionToSave.BuildSummary(_baseVoxelSize)} merged {removedRegionIds.Count}");
    }

    public TerrainEditRegionMutationResult ClearAll()
    {
        Aabb dirtyWorldBounds = default;
        bool hasBounds = false;
        int clearedCount;

        lock (_gate)
        {
            clearedCount = _regions.Count;
            foreach (TerrainEditRegion region in _regions.Values)
            {
                dirtyWorldBounds = hasBounds
                    ? Union(dirtyWorldBounds, region.WorldBounds)
                    : region.WorldBounds;
                hasBounds = true;
            }

            _regions.Clear();
        }

        _store.ClearPersistedEditRegions();
        return new TerrainEditRegionMutationResult(
            hasBounds,
            dirtyWorldBounds,
            0,
            0,
            0,
            hasBounds ? $"cleared {clearedCount} edit regions" : "none");
    }

    public string BuildDebugSummary()
    {
        lock (_gate)
        {
            if (_regions.Count == 0)
            {
                return "none";
            }

            List<TerrainEditRegion> ordered = new(_regions.Values);
            ordered.Sort(static (a, b) =>
            {
                int detailCompare = b.RequestedDetailLevel.CompareTo(a.RequestedDetailLevel);
                if (detailCompare != 0)
                {
                    return detailCompare;
                }

                int stampCompare = b.StampCount.CompareTo(a.StampCount);
                if (stampCompare != 0)
                {
                    return stampCompare;
                }

                int priorityCompare = b.Priority.CompareTo(a.Priority);
                if (priorityCompare != 0)
                {
                    return priorityCompare;
                }

                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });

            StringBuilder builder = new();
            builder.Append(_regions.Count);
            builder.Append(" regions stamps ");
            builder.Append(ComputeStampCount(ordered));
            builder.Append(" max ");
            builder.Append(ComputeMaxDetailLevel(ordered));
            builder.Append(" top ");

            int previewCount = Math.Min(3, ordered.Count);
            for (int i = 0; i < previewCount; i++)
            {
                if (i > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append(ordered[i].BuildSummary(_baseVoxelSize));
            }

            return builder.ToString();
        }
    }

    private TerrainEditRegionMutationResult BuildMutationResult(Aabb dirtyWorldBounds, string summary)
    {
        lock (_gate)
        {
            return new TerrainEditRegionMutationResult(
                Changed: true,
                dirtyWorldBounds,
                _regions.Count,
                ComputeStampCount(_regions.Values),
                ComputeMaxDetailLevel(_regions.Values),
                summary);
        }
    }

    private static bool CanMerge(
        TerrainEditRegion region,
        Aabb worldBounds,
        TerrainDetailRegionSource source,
        string reason,
        bool sticky)
    {
        return region.Source == source &&
               region.Sticky == sticky &&
               string.Equals(region.Reason, reason ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
               region.Overlaps(worldBounds);
    }

    private static int ComputeStampCount(IEnumerable<TerrainEditRegion> regions)
    {
        int count = 0;
        foreach (TerrainEditRegion region in regions)
        {
            count += region.StampCount;
        }

        return count;
    }

    private static int ComputeMaxDetailLevel(IEnumerable<TerrainEditRegion> regions)
    {
        int max = 0;
        foreach (TerrainEditRegion region in regions)
        {
            max = Math.Max(max, region.RequestedDetailLevel);
        }

        return max;
    }

    private static Aabb Union(Aabb a, Aabb b)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Min(a.Position.X, b.Position.X),
            Mathf.Min(a.Position.Y, b.Position.Y),
            Mathf.Min(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Max(aEnd.X, bEnd.X),
            Mathf.Max(aEnd.Y, bEnd.Y),
            Mathf.Max(aEnd.Z, bEnd.Z));
        return new Aabb(min, max - min);
    }
}
