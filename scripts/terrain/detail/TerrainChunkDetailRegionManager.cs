using Godot;
using System;
using System.Collections.Generic;
using System.Text;

namespace TowerOfBaby.Terrain;

public sealed class TerrainChunkDetailRegionManager
{
    private readonly List<TerrainDetailRegion> _regions = new();
    private readonly float _chunkSize;

    public TerrainChunkDetailRegionManager(float chunkSize)
    {
        _chunkSize = Mathf.Max(chunkSize, 0.01f);
    }

    public IReadOnlyList<TerrainDetailRegion> Regions => _regions;
    public int RegionCount => _regions.Count;
    public bool HasRegions => _regions.Count > 0;

    public int DirtyRegionCount
    {
        get
        {
            int count = 0;
            foreach (TerrainDetailRegion region in _regions)
            {
                if (region.Dirty)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int MaxDetailLevel
    {
        get
        {
            int max = 0;
            foreach (TerrainDetailRegion region in _regions)
            {
                max = Math.Max(max, region.RequestedDetailLevel);
            }

            return max;
        }
    }

    public bool RequestDetail(
        Aabb localBounds,
        int requestedDetailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority = 0.0f,
        bool sticky = false,
        string requestId = "")
    {
        if (!TryNormalizeLocalBounds(localBounds, out Aabb normalizedBounds))
        {
            return false;
        }

        int normalizedDetailLevel = Math.Max(1, requestedDetailLevel);
        string normalizedReason = reason?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            TerrainDetailRegion existingById = FindById(requestId);
            if (existingById != null)
            {
                bool changed =
                    existingById.LocalBounds.Position != normalizedBounds.Position ||
                    existingById.LocalBounds.Size != normalizedBounds.Size ||
                    existingById.RequestedDetailLevel != normalizedDetailLevel ||
                    existingById.Source != source ||
                    !string.Equals(existingById.Reason, normalizedReason, StringComparison.Ordinal) ||
                    !Mathf.IsEqualApprox(existingById.Priority, priority) ||
                    existingById.Sticky != sticky;
                if (!changed)
                {
                    return false;
                }

                existingById.ApplyRequest(normalizedBounds, normalizedDetailLevel, source, normalizedReason, priority, sticky);
                return true;
            }
        }

        foreach (TerrainDetailRegion existing in _regions)
        {
            if (!CanMerge(existing, normalizedBounds, normalizedDetailLevel, source, normalizedReason, sticky))
            {
                continue;
            }

            existing.Merge(normalizedBounds, normalizedDetailLevel, priority, sticky);
            return true;
        }

        _regions.Add(new TerrainDetailRegion(requestId, normalizedBounds, normalizedDetailLevel, source, normalizedReason, priority, sticky));
        return true;
    }

    public bool RemoveRequest(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        int removed = _regions.RemoveAll(region => string.Equals(region.Id, requestId.Trim(), StringComparison.Ordinal));
        return removed > 0;
    }

    public int RemoveRequestsBySource(TerrainDetailRegionSource source)
    {
        return _regions.RemoveAll(region => region.Source == source);
    }

    public TerrainDetailRegion[] QueryIntersecting(Aabb localBounds)
    {
        if (!TryNormalizeLocalBounds(localBounds, out Aabb normalizedBounds))
        {
            return Array.Empty<TerrainDetailRegion>();
        }

        List<TerrainDetailRegion> results = new();
        foreach (TerrainDetailRegion region in _regions)
        {
            if (region.Overlaps(normalizedBounds))
            {
                results.Add(region);
            }
        }

        return results.ToArray();
    }

    public void ClearDirtyFlags()
    {
        foreach (TerrainDetailRegion region in _regions)
        {
            region.ClearDirty();
        }
    }

    public string SourceSummary
    {
        get
        {
            if (_regions.Count == 0)
            {
                return "none";
            }

            Dictionary<TerrainDetailRegionSource, int> counts = new();
            foreach (TerrainDetailRegion region in _regions)
            {
                counts[region.Source] = counts.GetValueOrDefault(region.Source, 0) + 1;
            }

            StringBuilder builder = new();
            bool first = true;
            foreach (TerrainDetailRegionSource source in Enum.GetValues<TerrainDetailRegionSource>())
            {
                if (!counts.TryGetValue(source, out int count) || count <= 0)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(source);
                builder.Append(':');
                builder.Append(count);
                first = false;
            }

            return builder.ToString();
        }
    }

    public string Summary
    {
        get
        {
            if (_regions.Count == 0)
            {
                return "none";
            }

            TerrainDetailRegion dominant = _regions[0];
            foreach (TerrainDetailRegion region in _regions)
            {
                if (region.RequestedDetailLevel > dominant.RequestedDetailLevel)
                {
                    dominant = region;
                    continue;
                }

                if (region.RequestedDetailLevel == dominant.RequestedDetailLevel && region.Priority > dominant.Priority)
                {
                    dominant = region;
                    continue;
                }

                if (region.RequestedDetailLevel == dominant.RequestedDetailLevel &&
                    Mathf.IsEqualApprox(region.Priority, dominant.Priority) &&
                    region.Dirty && !dominant.Dirty)
                {
                    dominant = region;
                }
            }

            List<TerrainDetailRegion> ordered = new(_regions);
            ordered.Sort(static (a, b) =>
            {
                int levelCompare = b.RequestedDetailLevel.CompareTo(a.RequestedDetailLevel);
                if (levelCompare != 0)
                {
                    return levelCompare;
                }

                int priorityCompare = b.Priority.CompareTo(a.Priority);
                if (priorityCompare != 0)
                {
                    return priorityCompare;
                }

                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });

            int previewCount = Math.Min(ordered.Count, 4);
            StringBuilder previewBuilder = new();
            for (int i = 0; i < previewCount; i++)
            {
                if (i > 0)
                {
                    previewBuilder.Append(" | ");
                }

                previewBuilder.Append(ordered[i].Summary);
            }

            return
                $"{RegionCount} regions max {MaxDetailLevel} dirty {DirtyRegionCount} src {SourceSummary} top {dominant.Summary} all {previewBuilder}";
        }
    }

    private TerrainDetailRegion FindById(string requestId)
    {
        string normalized = requestId.Trim();
        foreach (TerrainDetailRegion region in _regions)
        {
            if (string.Equals(region.Id, normalized, StringComparison.Ordinal))
            {
                return region;
            }
        }

        return null;
    }

    private bool CanMerge(
        TerrainDetailRegion existing,
        Aabb normalizedBounds,
        int normalizedDetailLevel,
        TerrainDetailRegionSource source,
        string normalizedReason,
        bool sticky)
    {
        return existing.Source == source &&
               string.Equals(existing.Reason, normalizedReason, StringComparison.OrdinalIgnoreCase) &&
               existing.Sticky == sticky &&
               existing.Overlaps(normalizedBounds);
    }

    private bool TryNormalizeLocalBounds(Aabb localBounds, out Aabb normalizedBounds)
    {
        Vector3 start = localBounds.Position;
        Vector3 end = localBounds.Position + localBounds.Size;
        Vector3 min = new(
            Mathf.Clamp(Mathf.Min(start.X, end.X), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Min(start.Y, end.Y), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Min(start.Z, end.Z), 0.0f, _chunkSize));
        Vector3 max = new(
            Mathf.Clamp(Mathf.Max(start.X, end.X), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Max(start.Y, end.Y), 0.0f, _chunkSize),
            Mathf.Clamp(Mathf.Max(start.Z, end.Z), 0.0f, _chunkSize));
        Vector3 size = max - min;
        if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
        {
            normalizedBounds = default;
            return false;
        }

        normalizedBounds = new Aabb(min, size);
        return true;
    }
}
