using Godot;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct VoxelMeshBuildResult(
    ArrayMesh Mesh,
    int TotalTriangleCount,
    bool UsedDetailBrick,
    bool UsedPersistentDetailEdits,
    int DetailTriangleCount,
    int ReplacedCoarseCellCount,
    int DetailCellCount);

public static class VoxelMesher
{
    private static readonly Vector3I[] CornerOffsets =
    {
        new(0, 0, 0),
        new(1, 0, 0),
        new(1, 1, 0),
        new(0, 1, 0),
        new(0, 0, 1),
        new(1, 0, 1),
        new(1, 1, 1),
        new(0, 1, 1)
    };

    public static VoxelMeshBuildResult BuildMesh(VoxelChunkData data)
    {
        SurfaceTool surfaceTool = new();
        surfaceTool.Begin(Mesh.PrimitiveType.Triangles);
        int vertexCount = 0;
        int replacedCoarseCellCount = 0;
        int detailCellCount = 0;
        VoxelDetailBrickData detailBrick = data.DetailBrick;

        int cells = data.CellsPerAxis;
        for (int z = 0; z < cells; z++)
        {
            for (int y = 0; y < cells; y++)
            {
                for (int x = 0; x < cells; x++)
                {
                    if (detailBrick != null && detailBrick.ShouldReplaceCoarseCell(x, y, z))
                    {
                        replacedCoarseCellCount++;
                        continue;
                    }

                    PolygonizeCube(surfaceTool, data, data.Origin, x, y, z, ref vertexCount);
                }
            }
        }

        int detailVertexStart = vertexCount;
        if (detailBrick != null)
        {
            int detailCells = detailBrick.Data.CellsPerAxis;
            for (int z = 0; z < detailCells; z++)
            {
                for (int y = 0; y < detailCells; y++)
                {
                    for (int x = 0; x < detailCells; x++)
                    {
                        PolygonizeCube(surfaceTool, detailBrick.Data, data.Origin, x, y, z, ref vertexCount);
                        detailCellCount++;
                    }
                }
            }
        }

        if (vertexCount == 0)
        {
            return new VoxelMeshBuildResult(
                new ArrayMesh(),
                TotalTriangleCount: 0,
                UsedDetailBrick: detailBrick != null,
                UsedPersistentDetailEdits: detailBrick?.HasPersistentEdits == true,
                DetailTriangleCount: 0,
                ReplacedCoarseCellCount: replacedCoarseCellCount,
                DetailCellCount: detailCellCount);
        }

        surfaceTool.GenerateNormals();
        surfaceTool.GenerateTangents();
        ArrayMesh mesh = surfaceTool.Commit();
        return new VoxelMeshBuildResult(
            mesh,
            TotalTriangleCount: vertexCount / 3,
            UsedDetailBrick: detailBrick != null,
            UsedPersistentDetailEdits: detailBrick?.HasPersistentEdits == true,
            DetailTriangleCount: (vertexCount - detailVertexStart) / 3,
            ReplacedCoarseCellCount: replacedCoarseCellCount,
            DetailCellCount: detailCellCount);
    }

    private static void PolygonizeCube(
        SurfaceTool surfaceTool,
        VoxelChunkData data,
        Vector3 meshOrigin,
        int x,
        int y,
        int z,
        ref int vertexCount)
    {
        Vector3[] positions = new Vector3[8];
        float[] densities = new float[8];
        VoxelMaterialId[] materials = new VoxelMaterialId[8];
        int cubeIndex = 0;

        for (int corner = 0; corner < 8; corner++)
        {
            Vector3I offset = CornerOffsets[corner];
            positions[corner] = data.GetPointPosition(x + offset.X, y + offset.Y, z + offset.Z) - meshOrigin;
            densities[corner] = data.GetDensity(x + offset.X, y + offset.Y, z + offset.Z);
            materials[corner] = data.GetMaterial(x + offset.X, y + offset.Y, z + offset.Z);
            if (densities[corner] >= data.IsoLevel)
            {
                cubeIndex |= 1 << corner;
            }
        }

        int edgeMask = MarchingCubesTables.EdgeMasks[cubeIndex];
        if (edgeMask == 0)
        {
            return;
        }

        Vector3[] edgeVertices = new Vector3[12];
        Color[] edgeColors = new Color[12];
        for (int edge = 0; edge < 12; edge++)
        {
            if ((edgeMask & (1 << edge)) == 0)
            {
                continue;
            }

            int a = MarchingCubesTables.EdgeVertexIndices[edge, 0];
            int b = MarchingCubesTables.EdgeVertexIndices[edge, 1];
            float t;
            edgeVertices[edge] = Interpolate(positions[a], positions[b], densities[a], densities[b], data.IsoLevel, out t);
            edgeColors[edge] = MaterialColor(materials[a]).Lerp(MaterialColor(materials[b]), t);
        }

        for (int index = 0; index < 16; index += 3)
        {
            int edgeA = MarchingCubesTables.TriangleTable[cubeIndex, index];
            if (edgeA == -1)
            {
                break;
            }

            int edgeB = MarchingCubesTables.TriangleTable[cubeIndex, index + 1];
            int edgeC = MarchingCubesTables.TriangleTable[cubeIndex, index + 2];

            AddVertex(surfaceTool, edgeVertices[edgeA], edgeColors[edgeA]);
            AddVertex(surfaceTool, edgeVertices[edgeB], edgeColors[edgeB]);
            AddVertex(surfaceTool, edgeVertices[edgeC], edgeColors[edgeC]);
            vertexCount += 3;
        }
    }

    private static Vector3 Interpolate(Vector3 p0, Vector3 p1, float d0, float d1, float isoLevel, out float t)
    {
        float denominator = d1 - d0;
        if (Mathf.Abs(denominator) < 0.00001f)
        {
            t = 0.5f;
            return (p0 + p1) * 0.5f;
        }

        t = Mathf.Clamp((isoLevel - d0) / denominator, 0.0f, 1.0f);
        return p0.Lerp(p1, t);
    }

    private static void AddVertex(SurfaceTool surfaceTool, Vector3 position, Color color)
    {
        surfaceTool.SetColor(color);
        surfaceTool.SetUV(new Vector2(position.X, position.Z) * 0.09f);
        surfaceTool.AddVertex(position);
    }

    private static Color MaterialColor(VoxelMaterialId materialId)
    {
        return materialId switch
        {
            VoxelMaterialId.Grass => new Color(0.32f, 0.42f, 0.19f),
            VoxelMaterialId.Rock => new Color(0.42f, 0.4f, 0.38f),
            VoxelMaterialId.Cliff => new Color(0.5f, 0.46f, 0.33f),
            VoxelMaterialId.Snow => new Color(0.83f, 0.84f, 0.86f),
            VoxelMaterialId.Scorched => new Color(0.11f, 0.1f, 0.1f),
            _ => new Color(0.39f, 0.3f, 0.18f)
        };
    }
}
