using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public sealed class TerrainChunkStore
{
    private const int TerrainGenerationCacheVersion = 2;

    public readonly record struct SerializedLodBlockSaveData(
        int PointsPerAxis,
        float VoxelSize,
        float IsoLevel,
        Vector3 Origin,
        byte[] DensityBytes,
        byte[] MaterialBytes,
        byte[] DetailBrickBlob,
        VoxelAdaptiveDetailPersistenceMetrics AdaptiveDetailMetrics,
        double SerializationMs);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly object _databaseLock = new();

    public TerrainChunkStore(int seed)
    {
        string rootPath = $"user://terrain_cache/world_{seed}_gen_{TerrainGenerationCacheVersion}";
        DirAccess.MakeDirRecursiveAbsolute(rootPath);
        _databasePath = ProjectSettings.GlobalizePath($"{rootPath}/terrain_chunks.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeSchema();
    }

    public bool TryLoad(Vector3I key, out VoxelChunkData data)
    {
        return TryLoadChunkFromTable("chunks", key, out data);
    }

    public bool TryLoadStartupChunk(Vector3I key, out VoxelChunkData data)
    {
        return TryLoadChunkFromTable("startup_chunks", key, out data);
    }

    public bool TryLoadLodBlock(TerrainBlockId blockId, out VoxelChunkData data)
    {
        return TryLoadLodChunkFromTable("lod_blocks", blockId, out data);
    }

    public bool TryLoadStartupLodBlock(TerrainBlockId blockId, out VoxelChunkData data)
    {
        return TryLoadLodChunkFromTable("lod_startup_blocks", blockId, out data);
    }

    public bool TryLoadStartupState(out TerrainStartupState state)
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT player_pos_x, player_pos_y, player_pos_z
                FROM startup_state
                WHERE id = 1
                LIMIT 1
                """;

            using SqliteDataReader stateReader = command.ExecuteReader();
            if (!stateReader.Read())
            {
                state = null!;
                return false;
            }

            state = new TerrainStartupState
            {
                PlayerPosition = new Vector3(
                    stateReader.GetFloat(0),
                    stateReader.GetFloat(1),
                    stateReader.GetFloat(2))
            };

            using SqliteCommand chunkCommand = connection.CreateCommand();
            chunkCommand.CommandText =
                """
                SELECT chunk_x, chunk_y, chunk_z, was_active
                FROM startup_chunks
                ORDER BY was_active DESC, updated_at_unix DESC, chunk_y ASC, chunk_x ASC, chunk_z ASC
                """;

            using SqliteDataReader chunkReader = chunkCommand.ExecuteReader();
            while (chunkReader.Read())
            {
                state.Chunks.Add(new TerrainStartupChunkDescriptor(
                    new Vector3I(
                        chunkReader.GetInt32(0),
                        chunkReader.GetInt32(1),
                        chunkReader.GetInt32(2)),
                    chunkReader.GetInt64(3) != 0));
            }

            return true;
        }
    }

    public bool TryLoadLodStartupState(out TerrainLodStartupState state)
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT player_pos_x, player_pos_y, player_pos_z
                FROM lod_startup_state
                WHERE id = 1
                LIMIT 1
                """;

            using SqliteDataReader stateReader = command.ExecuteReader();
            if (!stateReader.Read())
            {
                state = null!;
                return false;
            }

            state = new TerrainLodStartupState
            {
                PlayerPosition = new Vector3(
                    stateReader.GetFloat(0),
                    stateReader.GetFloat(1),
                    stateReader.GetFloat(2))
            };

            using SqliteCommand blockCommand = connection.CreateCommand();
            blockCommand.CommandText =
                """
                SELECT lod, block_x, block_y, block_z, was_visible
                FROM lod_startup_blocks
                ORDER BY was_visible DESC, lod ASC, updated_at_unix DESC, block_y ASC, block_x ASC, block_z ASC
                """;

            using SqliteDataReader blockReader = blockCommand.ExecuteReader();
            while (blockReader.Read())
            {
                state.Blocks.Add(new TerrainLodStartupBlockDescriptor(
                    new TerrainBlockId(
                        blockReader.GetInt32(0),
                        new Vector3I(
                            blockReader.GetInt32(1),
                            blockReader.GetInt32(2),
                            blockReader.GetInt32(3))),
                    blockReader.GetInt64(4) != 0));
            }

            return true;
        }
    }

    public HashSet<Vector3I> LoadPersistedChunkKeys()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT chunk_x, chunk_y, chunk_z
                FROM chunks
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            HashSet<Vector3I> keys = new();
            while (reader.Read())
            {
                keys.Add(new Vector3I(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }

            return keys;
        }
    }

    public HashSet<TerrainBlockId> LoadPersistedLodBlockKeys()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT lod, block_x, block_y, block_z
                FROM lod_blocks
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            HashSet<TerrainBlockId> keys = new();
            while (reader.Read())
            {
                keys.Add(new TerrainBlockId(
                    reader.GetInt32(0),
                    new Vector3I(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3))));
            }

            return keys;
        }
    }

    public TerrainEditRegion[] LoadPersistedEditRegions()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload_blob
                FROM edit_regions
                ORDER BY updated_at_unix ASC, region_id ASC
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            List<TerrainEditRegion> regions = new();
            while (reader.Read())
            {
                byte[] payload = (byte[])reader["payload_blob"];
                if (payload == null || payload.Length == 0)
                {
                    continue;
                }

                try
                {
                    regions.Add(TerrainEditRegion.Deserialize(payload));
                }
                catch (Exception ex)
                {
                    GD.PushWarning($"Terrain edit region load failed | bytes {payload.Length} | {ex.Message}");
                }
            }

            return regions.ToArray();
        }
    }

    public void Save(Vector3I key, VoxelChunkData data)
    {
        lock (_databaseLock)
        {
            VoxelAdaptiveDetailPersistencePayload adaptiveDetail = data.ExportPersistedAdaptiveDetailPayload();
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO chunks (
                    chunk_x, chunk_y, chunk_z,
                    points_per_axis, voxel_size, iso_level,
                    origin_x, origin_y, origin_z,
                    densities_blob, materials_blob, detail_brick_blob, updated_at_unix
                )
                VALUES (
                    $x, $y, $z,
                    $pointsPerAxis, $voxelSize, $isoLevel,
                    $originX, $originY, $originZ,
                    $densities, $materials, $detailBrick, $updatedAtUnix
                )
                ON CONFLICT(chunk_x, chunk_y, chunk_z) DO UPDATE SET
                    points_per_axis = excluded.points_per_axis,
                    voxel_size = excluded.voxel_size,
                    iso_level = excluded.iso_level,
                    origin_x = excluded.origin_x,
                    origin_y = excluded.origin_y,
                    origin_z = excluded.origin_z,
                    densities_blob = excluded.densities_blob,
                    materials_blob = excluded.materials_blob,
                    detail_brick_blob = excluded.detail_brick_blob,
                    updated_at_unix = excluded.updated_at_unix
                """;

            command.Parameters.AddWithValue("$x", key.X);
            command.Parameters.AddWithValue("$y", key.Y);
            command.Parameters.AddWithValue("$z", key.Z);
            command.Parameters.AddWithValue("$pointsPerAxis", data.PointsPerAxis);
            command.Parameters.AddWithValue("$voxelSize", data.VoxelSize);
            command.Parameters.AddWithValue("$isoLevel", data.IsoLevel);
            command.Parameters.AddWithValue("$originX", data.Origin.X);
            command.Parameters.AddWithValue("$originY", data.Origin.Y);
            command.Parameters.AddWithValue("$originZ", data.Origin.Z);
            command.Parameters.AddWithValue("$densities", FloatArrayToBytes(data.CopyDensities()));
            command.Parameters.AddWithValue("$materials", data.CopyMaterials());
            command.Parameters.AddWithValue(
                "$detailBrick",
                adaptiveDetail.HasPayload ? adaptiveDetail.Blob : (object)DBNull.Value);
            command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();

            LogAdaptiveDetailSave("chunks", key.ToString(), adaptiveDetail.Metrics);
        }
    }

    public void SaveLodBlock(TerrainBlockId blockId, VoxelChunkData data)
    {
        SerializedLodBlockSaveData serialized = SerializeLodBlock(data);
        SaveSerializedLodBlock(blockId, serialized);
    }

    public SerializedLodBlockSaveData SerializeLodBlock(VoxelChunkData data)
    {
        return SerializeLodBlockData(data);
    }

    public double SaveSerializedLodBlock(TerrainBlockId blockId, SerializedLodBlockSaveData data)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();
            SaveLodChunkToTable(connection, transaction: null, "lod_blocks", blockId, wasVisible: false, data);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    public void DeleteLodBlocks(IReadOnlyCollection<TerrainBlockId> blockIds)
    {
        if (blockIds == null || blockIds.Count == 0)
        {
            return;
        }

        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM lod_blocks
                WHERE lod = $lod AND block_x = $x AND block_y = $y AND block_z = $z
                """;
            command.Parameters.Add("$lod", SqliteType.Integer);
            command.Parameters.Add("$x", SqliteType.Integer);
            command.Parameters.Add("$y", SqliteType.Integer);
            command.Parameters.Add("$z", SqliteType.Integer);

            foreach (TerrainBlockId blockId in blockIds)
            {
                command.Parameters["$lod"].Value = blockId.Lod;
                command.Parameters["$x"].Value = blockId.Index.X;
                command.Parameters["$y"].Value = blockId.Index.Y;
                command.Parameters["$z"].Value = blockId.Index.Z;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void SaveEditRegion(TerrainEditRegion region)
    {
        if (region == null)
        {
            throw new ArgumentNullException(nameof(region));
        }

        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO edit_regions (
                    region_id,
                    bounds_min_x, bounds_min_y, bounds_min_z,
                    bounds_size_x, bounds_size_y, bounds_size_z,
                    requested_detail_level,
                    payload_blob,
                    updated_at_unix
                )
                VALUES (
                    $regionId,
                    $minX, $minY, $minZ,
                    $sizeX, $sizeY, $sizeZ,
                    $detailLevel,
                    $payload,
                    $updatedAtUnix
                )
                ON CONFLICT(region_id) DO UPDATE SET
                    bounds_min_x = excluded.bounds_min_x,
                    bounds_min_y = excluded.bounds_min_y,
                    bounds_min_z = excluded.bounds_min_z,
                    bounds_size_x = excluded.bounds_size_x,
                    bounds_size_y = excluded.bounds_size_y,
                    bounds_size_z = excluded.bounds_size_z,
                    requested_detail_level = excluded.requested_detail_level,
                    payload_blob = excluded.payload_blob,
                    updated_at_unix = excluded.updated_at_unix
                """;

            command.Parameters.AddWithValue("$regionId", region.Id);
            command.Parameters.AddWithValue("$minX", region.WorldBounds.Position.X);
            command.Parameters.AddWithValue("$minY", region.WorldBounds.Position.Y);
            command.Parameters.AddWithValue("$minZ", region.WorldBounds.Position.Z);
            command.Parameters.AddWithValue("$sizeX", region.WorldBounds.Size.X);
            command.Parameters.AddWithValue("$sizeY", region.WorldBounds.Size.Y);
            command.Parameters.AddWithValue("$sizeZ", region.WorldBounds.Size.Z);
            command.Parameters.AddWithValue("$detailLevel", region.RequestedDetailLevel);
            command.Parameters.AddWithValue("$payload", region.Serialize());
            command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public void DeleteEditRegion(string regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId))
        {
            return;
        }

        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM edit_regions
                WHERE region_id = $regionId
                """;
            command.Parameters.AddWithValue("$regionId", regionId.Trim());
            command.ExecuteNonQuery();
        }
    }

    public void ClearPersistedEditRegions()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM edit_regions;";
            command.ExecuteNonQuery();
        }
    }

    public void SaveStartupState(Vector3 playerPosition, IReadOnlyList<TerrainStartupChunkSnapshot> chunks)
    {
        lock (_databaseLock)
        {
            VoxelAdaptiveDetailPersistenceMetrics totalAdaptiveDetailMetrics = VoxelAdaptiveDetailPersistenceMetrics.None;
            using SqliteConnection connection = new(_connectionString);
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand clearCommand = connection.CreateCommand())
            {
                clearCommand.Transaction = transaction;
                clearCommand.CommandText = "DELETE FROM startup_chunks;";
                clearCommand.ExecuteNonQuery();
            }

            using (SqliteCommand stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = transaction;
                stateCommand.CommandText =
                    """
                    INSERT INTO startup_state (
                        id, player_pos_x, player_pos_y, player_pos_z, updated_at_unix
                    )
                    VALUES (
                        1, $playerX, $playerY, $playerZ, $updatedAtUnix
                    )
                    ON CONFLICT(id) DO UPDATE SET
                        player_pos_x = excluded.player_pos_x,
                        player_pos_y = excluded.player_pos_y,
                        player_pos_z = excluded.player_pos_z,
                        updated_at_unix = excluded.updated_at_unix
                    """;
                stateCommand.Parameters.AddWithValue("$playerX", playerPosition.X);
                stateCommand.Parameters.AddWithValue("$playerY", playerPosition.Y);
                stateCommand.Parameters.AddWithValue("$playerZ", playerPosition.Z);
                stateCommand.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                stateCommand.ExecuteNonQuery();
            }

            foreach (TerrainStartupChunkSnapshot chunk in chunks)
            {
                VoxelAdaptiveDetailPersistencePayload adaptiveDetail = chunk.Data.ExportPersistedAdaptiveDetailPayload();
                totalAdaptiveDetailMetrics = totalAdaptiveDetailMetrics.Add(adaptiveDetail.Metrics);
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO startup_chunks (
                        chunk_x, chunk_y, chunk_z,
                        was_active,
                        points_per_axis, voxel_size, iso_level,
                        origin_x, origin_y, origin_z,
                        densities_blob, materials_blob, detail_brick_blob, updated_at_unix
                    )
                    VALUES (
                        $x, $y, $z,
                        $wasActive,
                        $pointsPerAxis, $voxelSize, $isoLevel,
                        $originX, $originY, $originZ,
                        $densities, $materials, $detailBrick, $updatedAtUnix
                    )
                    """;

                command.Parameters.AddWithValue("$x", chunk.Key.X);
                command.Parameters.AddWithValue("$y", chunk.Key.Y);
                command.Parameters.AddWithValue("$z", chunk.Key.Z);
                command.Parameters.AddWithValue("$wasActive", chunk.WasActive ? 1 : 0);
                command.Parameters.AddWithValue("$pointsPerAxis", chunk.Data.PointsPerAxis);
                command.Parameters.AddWithValue("$voxelSize", chunk.Data.VoxelSize);
                command.Parameters.AddWithValue("$isoLevel", chunk.Data.IsoLevel);
                command.Parameters.AddWithValue("$originX", chunk.Data.Origin.X);
                command.Parameters.AddWithValue("$originY", chunk.Data.Origin.Y);
                command.Parameters.AddWithValue("$originZ", chunk.Data.Origin.Z);
                command.Parameters.AddWithValue("$densities", FloatArrayToBytes(chunk.Data.CopyDensities()));
                command.Parameters.AddWithValue("$materials", chunk.Data.CopyMaterials());
                command.Parameters.AddWithValue(
                    "$detailBrick",
                    adaptiveDetail.HasPayload ? adaptiveDetail.Blob : (object)DBNull.Value);
                command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            LogAdaptiveDetailBatchSave("startup_state", chunks.Count, totalAdaptiveDetailMetrics);
        }
    }

    public void SaveLodStartupState(Vector3 playerPosition, IReadOnlyList<TerrainLodStartupBlockSnapshot> blocks)
    {
        lock (_databaseLock)
        {
            VoxelAdaptiveDetailPersistenceMetrics totalAdaptiveDetailMetrics = VoxelAdaptiveDetailPersistenceMetrics.None;
            using SqliteConnection connection = new(_connectionString);
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand clearCommand = connection.CreateCommand())
            {
                clearCommand.Transaction = transaction;
                clearCommand.CommandText = "DELETE FROM lod_startup_blocks;";
                clearCommand.ExecuteNonQuery();
            }

            using (SqliteCommand stateCommand = connection.CreateCommand())
            {
                stateCommand.Transaction = transaction;
                stateCommand.CommandText =
                    """
                    INSERT INTO lod_startup_state (
                        id, player_pos_x, player_pos_y, player_pos_z, updated_at_unix
                    )
                    VALUES (
                        1, $playerX, $playerY, $playerZ, $updatedAtUnix
                    )
                    ON CONFLICT(id) DO UPDATE SET
                        player_pos_x = excluded.player_pos_x,
                        player_pos_y = excluded.player_pos_y,
                        player_pos_z = excluded.player_pos_z,
                        updated_at_unix = excluded.updated_at_unix
                    """;
                stateCommand.Parameters.AddWithValue("$playerX", playerPosition.X);
                stateCommand.Parameters.AddWithValue("$playerY", playerPosition.Y);
                stateCommand.Parameters.AddWithValue("$playerZ", playerPosition.Z);
                stateCommand.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                stateCommand.ExecuteNonQuery();
            }

            foreach (TerrainLodStartupBlockSnapshot block in blocks)
            {
                SerializedLodBlockSaveData serializedBlock = SerializeLodBlockData(block.Data);
                totalAdaptiveDetailMetrics = totalAdaptiveDetailMetrics.Add(serializedBlock.AdaptiveDetailMetrics);
                SaveLodChunkToTable(
                    connection,
                    transaction,
                    "lod_startup_blocks",
                    block.BlockId,
                    block.WasVisible,
                    serializedBlock);
            }

            transaction.Commit();
            LogAdaptiveDetailBatchSave("lod_startup_state", blocks.Count, totalAdaptiveDetailMetrics);
        }
    }

    public void ClearStartupState()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM startup_chunks;
                DELETE FROM startup_state;
                DELETE FROM lod_startup_blocks;
                DELETE FROM lod_startup_state;
                """;
            command.ExecuteNonQuery();
        }
    }

    public void ClearAllChunkData()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM startup_chunks;
                DELETE FROM startup_state;
                DELETE FROM chunks;
                DELETE FROM lod_startup_blocks;
                DELETE FROM lod_startup_state;
                DELETE FROM lod_blocks;
                DELETE FROM edit_regions;
                """;
            command.ExecuteNonQuery();
        }
    }

    private void InitializeSchema()
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS edit_regions (
                    region_id TEXT NOT NULL PRIMARY KEY,
                    bounds_min_x REAL NOT NULL,
                    bounds_min_y REAL NOT NULL,
                    bounds_min_z REAL NOT NULL,
                    bounds_size_x REAL NOT NULL,
                    bounds_size_y REAL NOT NULL,
                    bounds_size_z REAL NOT NULL,
                    requested_detail_level INTEGER NOT NULL,
                    payload_blob BLOB NOT NULL,
                    updated_at_unix INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS chunks (
                    chunk_x INTEGER NOT NULL,
                    chunk_y INTEGER NOT NULL,
                    chunk_z INTEGER NOT NULL,
                    points_per_axis INTEGER NOT NULL,
                    voxel_size REAL NOT NULL,
                    iso_level REAL NOT NULL,
                    origin_x REAL NOT NULL,
                    origin_y REAL NOT NULL,
                    origin_z REAL NOT NULL,
                    densities_blob BLOB NOT NULL,
                    materials_blob BLOB NOT NULL,
                    detail_brick_blob BLOB NULL,
                    updated_at_unix INTEGER NOT NULL,
                    PRIMARY KEY (chunk_x, chunk_y, chunk_z)
                );

                CREATE TABLE IF NOT EXISTS startup_state (
                    id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                    player_pos_x REAL NOT NULL,
                    player_pos_y REAL NOT NULL,
                    player_pos_z REAL NOT NULL,
                    updated_at_unix INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS startup_chunks (
                    chunk_x INTEGER NOT NULL,
                    chunk_y INTEGER NOT NULL,
                    chunk_z INTEGER NOT NULL,
                    was_active INTEGER NOT NULL,
                    points_per_axis INTEGER NOT NULL,
                    voxel_size REAL NOT NULL,
                    iso_level REAL NOT NULL,
                    origin_x REAL NOT NULL,
                    origin_y REAL NOT NULL,
                    origin_z REAL NOT NULL,
                    densities_blob BLOB NOT NULL,
                    materials_blob BLOB NOT NULL,
                    detail_brick_blob BLOB NULL,
                    updated_at_unix INTEGER NOT NULL,
                    PRIMARY KEY (chunk_x, chunk_y, chunk_z)
                );

                CREATE TABLE IF NOT EXISTS lod_blocks (
                    lod INTEGER NOT NULL,
                    block_x INTEGER NOT NULL,
                    block_y INTEGER NOT NULL,
                    block_z INTEGER NOT NULL,
                    points_per_axis INTEGER NOT NULL,
                    voxel_size REAL NOT NULL,
                    iso_level REAL NOT NULL,
                    origin_x REAL NOT NULL,
                    origin_y REAL NOT NULL,
                    origin_z REAL NOT NULL,
                    densities_blob BLOB NOT NULL,
                    materials_blob BLOB NOT NULL,
                    detail_brick_blob BLOB NULL,
                    updated_at_unix INTEGER NOT NULL,
                    PRIMARY KEY (lod, block_x, block_y, block_z)
                );

                CREATE TABLE IF NOT EXISTS lod_startup_state (
                    id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                    player_pos_x REAL NOT NULL,
                    player_pos_y REAL NOT NULL,
                    player_pos_z REAL NOT NULL,
                    updated_at_unix INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS lod_startup_blocks (
                    lod INTEGER NOT NULL,
                    block_x INTEGER NOT NULL,
                    block_y INTEGER NOT NULL,
                    block_z INTEGER NOT NULL,
                    was_visible INTEGER NOT NULL,
                    points_per_axis INTEGER NOT NULL,
                    voxel_size REAL NOT NULL,
                    iso_level REAL NOT NULL,
                    origin_x REAL NOT NULL,
                    origin_y REAL NOT NULL,
                    origin_z REAL NOT NULL,
                    densities_blob BLOB NOT NULL,
                    materials_blob BLOB NOT NULL,
                    detail_brick_blob BLOB NULL,
                    updated_at_unix INTEGER NOT NULL,
                    PRIMARY KEY (lod, block_x, block_y, block_z)
                );
                """;
            command.ExecuteNonQuery();

            EnsureColumnExists(connection, "chunks", "detail_brick_blob", "BLOB NULL");
            EnsureColumnExists(connection, "startup_chunks", "detail_brick_blob", "BLOB NULL");
            EnsureColumnExists(connection, "lod_blocks", "detail_brick_blob", "BLOB NULL");
            EnsureColumnExists(connection, "lod_startup_blocks", "detail_brick_blob", "BLOB NULL");
        }
    }

    private bool TryLoadChunkFromTable(string tableName, Vector3I key, out VoxelChunkData data)
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT points_per_axis, voxel_size, iso_level, origin_x, origin_y, origin_z, densities_blob, materials_blob, detail_brick_blob
                FROM {tableName}
                WHERE chunk_x = $x AND chunk_y = $y AND chunk_z = $z
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$x", key.X);
            command.Parameters.AddWithValue("$y", key.Y);
            command.Parameters.AddWithValue("$z", key.Z);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                data = null!;
                return false;
            }

            int pointsPerAxis = reader.GetInt32(0);
            float voxelSize = reader.GetFloat(1);
            float isoLevel = reader.GetFloat(2);
            Vector3 origin = new(reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5));
            byte[] densityBytes = (byte[])reader["densities_blob"];
            byte[] materialBytes = (byte[])reader["materials_blob"];
            byte[] detailBrickBytes = reader.IsDBNull(8)
                ? null
                : (byte[])reader["detail_brick_blob"];

            VoxelChunkData loaded = new(pointsPerAxis, voxelSize, origin, isoLevel);
            loaded.LoadFromBuffers(BytesToFloatArray(densityBytes), materialBytes);
            VoxelAdaptiveDetailPersistenceMetrics adaptiveDetailMetrics = VoxelAdaptiveDetailPersistenceMetrics.None;
            if (detailBrickBytes != null && detailBrickBytes.Length > 0)
            {
                try
                {
                    adaptiveDetailMetrics = loaded.LoadPersistedAdaptiveDetailPayload(detailBrickBytes);
                    LogAdaptiveDetailLoad(tableName, key.ToString(), adaptiveDetailMetrics);
                }
                catch (Exception ex)
                {
                    GD.PushWarning(
                        $"Terrain adaptive detail load failed | table {tableName} | chunk {key} | bytes {detailBrickBytes.Length} | {ex.Message} | falling back to coarse terrain only.");
                }
            }

            data = loaded;
            return true;
        }
    }

    private bool TryLoadLodChunkFromTable(string tableName, TerrainBlockId blockId, out VoxelChunkData data)
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT points_per_axis, voxel_size, iso_level, origin_x, origin_y, origin_z, densities_blob, materials_blob, detail_brick_blob
                FROM {tableName}
                WHERE lod = $lod AND block_x = $x AND block_y = $y AND block_z = $z
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$lod", blockId.Lod);
            command.Parameters.AddWithValue("$x", blockId.Index.X);
            command.Parameters.AddWithValue("$y", blockId.Index.Y);
            command.Parameters.AddWithValue("$z", blockId.Index.Z);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                data = null!;
                return false;
            }

            int pointsPerAxis = reader.GetInt32(0);
            float voxelSize = reader.GetFloat(1);
            float isoLevel = reader.GetFloat(2);
            Vector3 origin = new(reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5));
            byte[] densityBytes = (byte[])reader["densities_blob"];
            byte[] materialBytes = (byte[])reader["materials_blob"];
            byte[] detailBrickBytes = reader.IsDBNull(8)
                ? null
                : (byte[])reader["detail_brick_blob"];

            VoxelChunkData loaded = new(pointsPerAxis, voxelSize, origin, isoLevel);
            loaded.LoadFromBuffers(BytesToFloatArray(densityBytes), materialBytes);
            if (detailBrickBytes != null && detailBrickBytes.Length > 0)
            {
                try
                {
                    VoxelAdaptiveDetailPersistenceMetrics adaptiveDetailMetrics =
                        loaded.LoadPersistedAdaptiveDetailPayload(detailBrickBytes);
                    LogAdaptiveDetailLoad(tableName, blockId.ToString(), adaptiveDetailMetrics);
                }
                catch (Exception ex)
                {
                    GD.PushWarning(
                        $"Terrain adaptive detail load failed | table {tableName} | block {blockId} | bytes {detailBrickBytes.Length} | {ex.Message} | falling back to coarse terrain only.");
                }
            }

            data = loaded;
            return true;
        }
    }

    private static void SaveLodChunkToTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        TerrainBlockId blockId,
        bool wasVisible,
        SerializedLodBlockSaveData data)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {tableName} (
                lod, block_x, block_y, block_z,
                {(tableName == "lod_startup_blocks" ? "was_visible," : string.Empty)}
                points_per_axis, voxel_size, iso_level,
                origin_x, origin_y, origin_z,
                densities_blob, materials_blob, detail_brick_blob, updated_at_unix
            )
            VALUES (
                $lod, $x, $y, $z,
                {(tableName == "lod_startup_blocks" ? "$wasVisible," : string.Empty)}
                $pointsPerAxis, $voxelSize, $isoLevel,
                $originX, $originY, $originZ,
                $densities, $materials, $detailBrick, $updatedAtUnix
            )
            ON CONFLICT(lod, block_x, block_y, block_z) DO UPDATE SET
                {(tableName == "lod_startup_blocks" ? "was_visible = excluded.was_visible," : string.Empty)}
                points_per_axis = excluded.points_per_axis,
                voxel_size = excluded.voxel_size,
                iso_level = excluded.iso_level,
                origin_x = excluded.origin_x,
                origin_y = excluded.origin_y,
                origin_z = excluded.origin_z,
                densities_blob = excluded.densities_blob,
                materials_blob = excluded.materials_blob,
                detail_brick_blob = excluded.detail_brick_blob,
                updated_at_unix = excluded.updated_at_unix
            """;

        command.Parameters.AddWithValue("$lod", blockId.Lod);
        command.Parameters.AddWithValue("$x", blockId.Index.X);
        command.Parameters.AddWithValue("$y", blockId.Index.Y);
        command.Parameters.AddWithValue("$z", blockId.Index.Z);
        if (tableName == "lod_startup_blocks")
        {
            command.Parameters.AddWithValue("$wasVisible", wasVisible ? 1 : 0);
        }

        command.Parameters.AddWithValue("$pointsPerAxis", data.PointsPerAxis);
        command.Parameters.AddWithValue("$voxelSize", data.VoxelSize);
        command.Parameters.AddWithValue("$isoLevel", data.IsoLevel);
        command.Parameters.AddWithValue("$originX", data.Origin.X);
        command.Parameters.AddWithValue("$originY", data.Origin.Y);
        command.Parameters.AddWithValue("$originZ", data.Origin.Z);
        command.Parameters.AddWithValue("$densities", data.DensityBytes);
        command.Parameters.AddWithValue("$materials", data.MaterialBytes);
        command.Parameters.AddWithValue(
            "$detailBrick",
            data.DetailBrickBlob ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();

        LogAdaptiveDetailSave(tableName, blockId.ToString(), data.AdaptiveDetailMetrics);
    }

    private static SerializedLodBlockSaveData SerializeLodBlockData(VoxelChunkData data)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        VoxelAdaptiveDetailPersistencePayload adaptiveDetail = data.ExportPersistedAdaptiveDetailPayload();
        return new SerializedLodBlockSaveData(
            data.PointsPerAxis,
            data.VoxelSize,
            data.IsoLevel,
            data.Origin,
            data.CopyDensityBytes(),
            data.GetMaterialBufferUnsafe(),
            adaptiveDetail.HasPayload ? adaptiveDetail.Blob : null,
            adaptiveDetail.Metrics,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        using SqliteCommand pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = $"PRAGMA table_info({tableName});";

        using SqliteDataReader reader = pragmaCommand.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using SqliteCommand alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alterCommand.ExecuteNonQuery();
    }

    private static byte[] FloatArrayToBytes(float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloatArray(byte[] bytes)
    {
        float[] values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static void LogAdaptiveDetailSave(string scope, string keySummary, VoxelAdaptiveDetailPersistenceMetrics metrics)
    {
        if (!metrics.HasPayload)
        {
            return;
        }

        GD.Print(
            $"Terrain adaptive detail save | scope {scope} | chunk {keySummary} | detail_bricks {metrics.DetailBrickCount} | detail_regions {metrics.DetailRegionCount} | bytes {metrics.SerializedByteCount} | schema {FormatSchema(metrics.SchemaVersion)}");
    }

    private static void LogAdaptiveDetailLoad(string scope, string keySummary, VoxelAdaptiveDetailPersistenceMetrics metrics)
    {
        if (!metrics.HasPayload)
        {
            return;
        }

        // GD.Print(
        //     $"Terrain adaptive detail load | scope {scope} | chunk {keySummary} | detail_bricks {metrics.DetailBrickCount} | detail_regions {metrics.DetailRegionCount} | bytes {metrics.SerializedByteCount} | schema {FormatSchema(metrics.SchemaVersion)}");
    }

    private static void LogAdaptiveDetailBatchSave(string scope, int chunkCount, VoxelAdaptiveDetailPersistenceMetrics metrics)
    {
        if (!metrics.HasPayload)
        {
            return;
        }

        GD.Print(
            $"Terrain adaptive detail save | scope {scope} | chunk_count {chunkCount} | detail_bricks {metrics.DetailBrickCount} | detail_regions {metrics.DetailRegionCount} | bytes {metrics.SerializedByteCount} | schema {FormatSchema(metrics.SchemaVersion)}");
    }

    private static string FormatSchema(int schemaVersion)
    {
        return schemaVersion <= 0 ? "legacy" : schemaVersion.ToString();
    }
}
