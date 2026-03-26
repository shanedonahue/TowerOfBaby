using Godot;
using System;
using System.Collections.Generic;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainResidencyManager
{
    private readonly HashSet<Vector3I> _desiredSet = new();
    private readonly HashSet<Vector3I> _residentSet = new();
    private readonly HashSet<Vector3I> _inFlightSet = new();
    private readonly List<ChunkPriorityInfo> _toAdd = new();
    private readonly List<ChunkReleaseInfo> _toRelease = new();

    public IReadOnlySet<Vector3I> DesiredSet => _desiredSet;
    public IReadOnlySet<Vector3I> ResidentSet => _residentSet;
    public IReadOnlySet<Vector3I> InFlightSet => _inFlightSet;
    public IReadOnlyList<ChunkPriorityInfo> ToAdd => _toAdd;
    public IReadOnlyList<ChunkReleaseInfo> ToRelease => _toRelease;

    public void Recompute(
        IEnumerable<Vector3I> desiredSet,
        IEnumerable<Vector3I> residentSet,
        IEnumerable<Vector3I> inFlightSet,
        Func<Vector3I, ChunkPriorityInfo> buildAddInfo,
        Func<Vector3I, ChunkReleaseInfo> buildReleaseInfo)
    {
        _desiredSet.Clear();
        _residentSet.Clear();
        _inFlightSet.Clear();
        _toAdd.Clear();
        _toRelease.Clear();

        foreach (Vector3I key in desiredSet)
        {
            _desiredSet.Add(key);
        }

        foreach (Vector3I key in residentSet)
        {
            _residentSet.Add(key);
        }

        foreach (Vector3I key in inFlightSet)
        {
            _inFlightSet.Add(key);
        }

        foreach (Vector3I key in _desiredSet)
        {
            if (_residentSet.Contains(key) || _inFlightSet.Contains(key))
            {
                continue;
            }

            _toAdd.Add(buildAddInfo(key));
        }

        foreach (Vector3I key in _residentSet)
        {
            if (_desiredSet.Contains(key))
            {
                continue;
            }

            _toRelease.Add(buildReleaseInfo(key));
        }

        _toAdd.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));
        _toRelease.Sort((a, b) => a.RetainScore.CompareTo(b.RetainScore));
    }
}
