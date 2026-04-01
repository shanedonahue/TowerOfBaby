using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Terrain;

public sealed class TerrainStructureSource
{
    private readonly TerrainWorldSettings _settings;
    private readonly TerrainStructureInstance[] _structures;

    public TerrainStructureSource(int seed, TerrainWorldSettings settings)
    {
        _settings = settings;
        _structures = BuildDemoStructures(seed, settings);
    }

    public IReadOnlyList<TerrainStructureInstance> Structures => _structures;

    public TerrainChunkStructureMetadata GetChunkStructureMetadata(Vector3I chunkKey)
    {
        Aabb chunkBounds = _settings.GetChunkBounds(chunkKey);
        List<TerrainStructureInstance> overlappingStructures = new();
        string dominantStructureId = string.Empty;
        TerrainStructureType dominantStructureType = TerrainStructureType.None;
        float highestPriority = float.NegativeInfinity;
        bool requestHigherTerrainDetail = false;

        foreach (TerrainStructureInstance structure in _structures)
        {
            if (!structure.Overlaps(chunkBounds))
            {
                continue;
            }

            overlappingStructures.Add(structure);
            requestHigherTerrainDetail |= structure.RequestHigherTerrainDetail;
            if (structure.Priority <= highestPriority)
            {
                continue;
            }

            highestPriority = structure.Priority;
            dominantStructureId = structure.Id;
            dominantStructureType = structure.Type;
        }

        if (overlappingStructures.Count == 0)
        {
            return TerrainChunkStructureMetadata.Empty;
        }

        return new TerrainChunkStructureMetadata(
            overlappingStructures.ToArray(),
            dominantStructureId,
            dominantStructureType,
            highestPriority,
            requestHigherTerrainDetail);
    }

    private static TerrainStructureInstance[] BuildDemoStructures(int seed, TerrainWorldSettings settings)
    {
        RandomNumberGenerator random = new();
        random.Seed = (ulong)((long)seed * 1610612741L + 73L);
        float chunkSize = settings.ChunkSize;

        float Offset(float scale)
        {
            return random.RandfRange(-chunkSize * scale, chunkSize * scale);
        }

        Vector3 ruinAnchor = new(
            (chunkSize * 0.75f) + Offset(0.18f),
            settings.BaseY + (chunkSize * 0.55f),
            (chunkSize * 0.35f) + Offset(0.18f));
        Vector3 roadAnchor = new(
            (chunkSize * 2.4f) + Offset(0.24f),
            settings.BaseY + (chunkSize * 0.40f),
            Offset(0.12f));
        Vector3 dungeonAnchor = new(
            (-chunkSize * 1.8f) + Offset(0.20f),
            settings.BaseY + (chunkSize * 0.20f),
            (chunkSize * 2.2f) + Offset(0.20f));

        return
        [
            new TerrainStructureInstance(
                "origin_ruin",
                TerrainStructureType.Ruin,
                new Transform3D(Basis.Identity, ruinAnchor),
                new Vector3(chunkSize * 1.15f, chunkSize * 0.85f, chunkSize * 1.15f),
                Priority: 1.20f,
                RequestHigherTerrainDetail: true,
                ["demo", "ruin", "origin"]),
            new TerrainStructureInstance(
                "east_road_segment",
                TerrainStructureType.Road,
                new Transform3D(Basis.Identity, roadAnchor),
                new Vector3(chunkSize * 2.30f, chunkSize * 0.35f, chunkSize * 0.65f),
                Priority: 0.80f,
                RequestHigherTerrainDetail: true,
                ["demo", "road", "approach"]),
            new TerrainStructureInstance(
                "south_dungeon_entry",
                TerrainStructureType.DungeonEntrance,
                new Transform3D(Basis.Identity, dungeonAnchor),
                new Vector3(chunkSize * 1.05f, chunkSize * 1.10f, chunkSize * 1.05f),
                Priority: 1.45f,
                RequestHigherTerrainDetail: true,
                ["demo", "dungeon", "entrance"])
        ];
    }
}
