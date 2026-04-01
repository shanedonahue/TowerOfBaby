using System;
using System.Collections.Generic;

namespace TowerOfBaby.Terrain;

public sealed class TerrainChunkStructureMetadata
{
    public static readonly TerrainChunkStructureMetadata Empty = new(
        Array.Empty<TerrainStructureInstance>(),
        dominantStructureId: string.Empty,
        TerrainStructureType.None,
        highestPriority: 0.0f,
        requestHigherTerrainDetail: false);

    private readonly TerrainStructureInstance[] _overlappingStructures;

    public TerrainChunkStructureMetadata(
        TerrainStructureInstance[] overlappingStructures,
        string dominantStructureId,
        TerrainStructureType dominantStructureType,
        float highestPriority,
        bool requestHigherTerrainDetail)
    {
        _overlappingStructures = overlappingStructures ?? Array.Empty<TerrainStructureInstance>();
        DominantStructureId = dominantStructureId ?? string.Empty;
        DominantStructureType = dominantStructureType;
        HighestPriority = highestPriority;
        RequestHigherTerrainDetail = requestHigherTerrainDetail;
    }

    public IReadOnlyList<TerrainStructureInstance> OverlappingStructures => _overlappingStructures;
    public int StructureCount => _overlappingStructures.Length;
    public bool IsInInfluenceZone => StructureCount > 0;
    public bool RequestHigherTerrainDetail { get; }
    public float HighestPriority { get; }
    public string DominantStructureId { get; }
    public TerrainStructureType DominantStructureType { get; }

    public string Summary
    {
        get
        {
            if (!IsInInfluenceZone)
            {
                return "none";
            }

            return
                $"{StructureCount} {DominantStructureType} id {DominantStructureId} detail {(RequestHigherTerrainDetail ? "high" : "normal")} p {HighestPriority:0.00}";
        }
    }
}
