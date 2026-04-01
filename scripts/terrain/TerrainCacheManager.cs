using Godot;
using System.Collections.Generic;
using System.Diagnostics;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainCacheManager
{
    private readonly TerrainChunkStore _chunkStore;
    private readonly TerrainStatsTracker _terrainStats;
    private readonly object _cacheLock = new();
    private readonly Dictionary<Vector3I, RamCacheEntry> _ramCache = new();
    private readonly HashSet<Vector3I> _knownPersistedKeys = new();
    private readonly HashSet<Vector3I> _startupOnlyKeys = new();
    private HashSet<Vector3I> _startupSnapshotKeys = new();
    private ulong _touchSequence;

    public TerrainCacheManager(TerrainChunkStore chunkStore, TerrainStatsTracker terrainStats = null)
    {
        _chunkStore = chunkStore;
        _terrainStats = terrainStats;
        foreach (Vector3I key in _chunkStore.LoadPersistedChunkKeys())
        {
            _knownPersistedKeys.Add(key);
        }
    }

    public int RamCacheCount
    {
        get
        {
            lock (_cacheLock)
            {
                return _ramCache.Count;
            }
        }
    }

    public long RamCacheHits { get; private set; }
    public long StartupSnapshotHits { get; private set; }
    public long DatabaseHits { get; private set; }
    public long GeneratedFallbacks { get; private set; }
    public long RamCacheEvictions { get; private set; }
    public long DirtyPersistWrites { get; private set; }
    public long StartupPromotionWrites { get; private set; }
    public long CleanEvictions { get; private set; }
    public int PersistedChunkRecordCount => _knownPersistedKeys.Count;

    public void SetStartupSnapshotKeys(IEnumerable<Vector3I> keys)
    {
        lock (_cacheLock)
        {
            _startupSnapshotKeys = new HashSet<Vector3I>(keys);
            foreach (Vector3I key in _startupSnapshotKeys)
            {
                if (!_knownPersistedKeys.Contains(key))
                {
                    _startupOnlyKeys.Add(key);
                }
            }
        }
    }

    public TerrainChunkLoadSource EstimateSource(Vector3I key, bool useStartupSnapshot)
    {
        lock (_cacheLock)
        {
            if (_ramCache.ContainsKey(key))
            {
                return TerrainChunkLoadSource.RamCache;
            }

            if (useStartupSnapshot && _startupSnapshotKeys.Contains(key))
            {
                return TerrainChunkLoadSource.StartupSnapshot;
            }

            if (_knownPersistedKeys.Contains(key))
            {
                return TerrainChunkLoadSource.PersistedChunk;
            }
        }

        return TerrainChunkLoadSource.ProceduralGeneration;
    }

    public ChunkAcquisitionResult AcquireChunk(
        Vector3I key,
        bool useStartupSnapshot,
        System.Func<Vector3I, VoxelChunkData> generateChunk)
    {
        lock (_cacheLock)
        {
            if (_ramCache.Remove(key, out RamCacheEntry cached))
            {
                RamCacheHits++;
                return new ChunkAcquisitionResult(cached.Data, TerrainChunkLoadSource.RamCache);
            }
        }

        bool tryStartupSnapshot;
        lock (_cacheLock)
        {
            tryStartupSnapshot = useStartupSnapshot && _startupSnapshotKeys.Contains(key);
        }

        if (tryStartupSnapshot && _chunkStore.TryLoadStartupChunk(key, out VoxelChunkData startupData))
        {
            lock (_cacheLock)
            {
                StartupSnapshotHits++;
                if (!_knownPersistedKeys.Contains(key))
                {
                    _startupOnlyKeys.Add(key);
                }
            }

            return new ChunkAcquisitionResult(startupData, TerrainChunkLoadSource.StartupSnapshot);
        }

        if (_chunkStore.TryLoad(key, out VoxelChunkData persistedData))
        {
            lock (_cacheLock)
            {
                DatabaseHits++;
                _knownPersistedKeys.Add(key);
                _startupOnlyKeys.Remove(key);
            }

            return new ChunkAcquisitionResult(persistedData, TerrainChunkLoadSource.PersistedChunk);
        }

        VoxelChunkData generated = generateChunk(key);
        lock (_cacheLock)
        {
            GeneratedFallbacks++;
        }

        return new ChunkAcquisitionResult(generated, TerrainChunkLoadSource.ProceduralGeneration);
    }

    public void ReleaseResidentChunk(Vector3I key, TerrainChunk chunk)
    {
        if (!chunk.HasData)
        {
            return;
        }

        StoreRamCacheEntry(key, chunk.Data, chunk.PersistenceDirty, chunk.LoadSource);
    }

    public void StorePreparedChunk(Vector3I key, VoxelChunkData data, TerrainChunkLoadSource source)
    {
        StoreRamCacheEntry(key, data, dirty: false, source);
    }

    public void MaintainCapacity(int maxLoadedChunks, int residentCount, int preparedCount)
    {
        if (maxLoadedChunks <= 0)
        {
            return;
        }

        List<(Vector3I Key, RamCacheEntry Entry)> evictions = new();
        lock (_cacheLock)
        {
            while ((residentCount + preparedCount + _ramCache.Count) > maxLoadedChunks)
            {
                Vector3I? oldestKey = null;
                ulong oldestTick = ulong.MaxValue;
                foreach (KeyValuePair<Vector3I, RamCacheEntry> cachedEntry in _ramCache)
                {
                    if (cachedEntry.Value.TouchTick >= oldestTick)
                    {
                        continue;
                    }

                    oldestTick = cachedEntry.Value.TouchTick;
                    oldestKey = cachedEntry.Key;
                }

                if (oldestKey == null)
                {
                    break;
                }

                RamCacheEntry entry = _ramCache[oldestKey.Value];
                _ramCache.Remove(oldestKey.Value);
                evictions.Add((oldestKey.Value, entry));
            }
        }

        foreach ((Vector3I key, RamCacheEntry entry) in evictions)
        {
            bool promoteStartupSnapshot;
            lock (_cacheLock)
            {
                promoteStartupSnapshot = !entry.Dirty && _startupOnlyKeys.Contains(key);
            }

            if (entry.Dirty || promoteStartupSnapshot)
            {
                if (_terrainStats?.Enabled == true)
                {
                    Stopwatch saveStopwatch = Stopwatch.StartNew();
                    _chunkStore.Save(key, entry.Data);
                    _terrainStats.RecordChunkSave(
                        key,
                        promoteStartupSnapshot ? "startup_promotion" : "persisted_chunk",
                        saveStopwatch.Elapsed.TotalMilliseconds,
                        entry.Dirty);
                }
                else
                {
                    _chunkStore.Save(key, entry.Data);
                }

                lock (_cacheLock)
                {
                    if (entry.Dirty)
                    {
                        DirtyPersistWrites++;
                    }
                    else
                    {
                        StartupPromotionWrites++;
                    }

                    _knownPersistedKeys.Add(key);
                    _startupOnlyKeys.Remove(key);
                    RamCacheEvictions++;
                }
            }
            else
            {
                lock (_cacheLock)
                {
                    CleanEvictions++;
                    RamCacheEvictions++;
                }
            }
        }
    }

    public List<TerrainStartupChunkSnapshot> BuildStartupCacheSnapshots()
    {
        List<TerrainStartupChunkSnapshot> snapshots = new();
        lock (_cacheLock)
        {
            foreach (KeyValuePair<Vector3I, RamCacheEntry> entry in _ramCache)
            {
                snapshots.Add(new TerrainStartupChunkSnapshot(entry.Key, WasActive: false, entry.Value.Data));
            }
        }

        return snapshots;
    }

    public void ClearPersistedKnowledge()
    {
        lock (_cacheLock)
        {
            _knownPersistedKeys.Clear();
        }
    }

    public void ClearStartupSnapshotKnowledge()
    {
        lock (_cacheLock)
        {
            _startupSnapshotKeys.Clear();
            _startupOnlyKeys.Clear();
        }
    }

    private void StoreRamCacheEntry(Vector3I key, VoxelChunkData data, bool dirty, TerrainChunkLoadSource source)
    {
        lock (_cacheLock)
        {
            _ramCache[key] = new RamCacheEntry(data, dirty, source, NextTouchUnsafe());
        }
    }

    private ulong NextTouchUnsafe()
    {
        _touchSequence++;
        return _touchSequence;
    }

    private readonly record struct RamCacheEntry(
        VoxelChunkData Data,
        bool Dirty,
        TerrainChunkLoadSource Source,
        ulong TouchTick);
}
