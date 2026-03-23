using Godot;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

public sealed class TerrainChunkStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly object _databaseLock = new();

    public TerrainChunkStore(int seed)
    {
        string rootPath = $"user://terrain_cache/world_{seed}";
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

    public void Save(Vector3I key, VoxelChunkData data)
    {
        lock (_databaseLock)
        {
            using SqliteConnection connection = new(_connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO chunks (
                    chunk_x, chunk_y, chunk_z,
                    points_per_axis, voxel_size, iso_level,
                    origin_x, origin_y, origin_z,
                    densities_blob, materials_blob, updated_at_unix
                )
                VALUES (
                    $x, $y, $z,
                    $pointsPerAxis, $voxelSize, $isoLevel,
                    $originX, $originY, $originZ,
                    $densities, $materials, $updatedAtUnix
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
            command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
    }

    public void SaveStartupState(Vector3 playerPosition, IReadOnlyList<TerrainStartupChunkSnapshot> chunks)
    {
        lock (_databaseLock)
        {
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
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO startup_chunks (
                        chunk_x, chunk_y, chunk_z,
                        was_active,
                        points_per_axis, voxel_size, iso_level,
                        origin_x, origin_y, origin_z,
                        densities_blob, materials_blob, updated_at_unix
                    )
                    VALUES (
                        $x, $y, $z,
                        $wasActive,
                        $pointsPerAxis, $voxelSize, $isoLevel,
                        $originX, $originY, $originZ,
                        $densities, $materials, $updatedAtUnix
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
                command.Parameters.AddWithValue("$updatedAtUnix", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.ExecuteNonQuery();
            }

            transaction.Commit();
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
                    updated_at_unix INTEGER NOT NULL,
                    PRIMARY KEY (chunk_x, chunk_y, chunk_z)
                );
                """;
            command.ExecuteNonQuery();
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
                SELECT points_per_axis, voxel_size, iso_level, origin_x, origin_y, origin_z, densities_blob, materials_blob
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

            VoxelChunkData loaded = new(pointsPerAxis, voxelSize, origin, isoLevel);
            loaded.LoadFromBuffers(BytesToFloatArray(densityBytes), materialBytes);
            data = loaded;
            return true;
        }
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
}
