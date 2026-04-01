using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TowerOfBaby.Terrain.Voxel;

public readonly record struct VoxelMeshBuildResult(
    Vector3[] Vertices,
    Vector3[] Normals,
    Vector2[] Uvs,
    Color[] Colors,
    float[] Tangents,
    int TotalTriangleCount,
    bool UsedDetailBrick,
    bool UsedPersistentDetailEdits,
    int DetailTriangleCount,
    int ReplacedCoarseCellCount,
    int DetailCellCount)
{
    public static VoxelMeshBuildResult Empty =>
        new(
            Array.Empty<Vector3>(),
            Array.Empty<Vector3>(),
            Array.Empty<Vector2>(),
            Array.Empty<Color>(),
            Array.Empty<float>(),
            TotalTriangleCount: 0,
            UsedDetailBrick: false,
            UsedPersistentDetailEdits: false,
            DetailTriangleCount: 0,
            ReplacedCoarseCellCount: 0,
            DetailCellCount: 0);

    public bool HasGeometry => Vertices.Length > 0;
    public bool HasTangents => Tangents.Length > 0;
}

public readonly record struct VoxelMeshBuildOptions(
    bool GenerateTangents,
    bool EnableVertexTint = false)
{
    public static VoxelMeshBuildOptions Default => new(false, false);
}

public static class VoxelMesher
{
    [ThreadStatic] private static MeshBuildScratch _threadScratch;

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

    public static VoxelMeshBuildResult BuildMesh(VoxelChunkData data, VoxelMeshBuildOptions options)
    {
        MeshBuildScratch scratch = GetScratch();
        scratch.Reset();
        if (options.GenerateTangents)
        {
            scratch.EnableTangents();
        }

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

                    PolygonizeCube(scratch, data, data.Origin, x, y, z, options.GenerateTangents, options.EnableVertexTint);
                }
            }
        }

        int detailVertexStart = scratch.VertexCount;
        if (detailBrick != null)
        {
            int detailCells = detailBrick.Data.CellsPerAxis;
            for (int z = 0; z < detailCells; z++)
            {
                for (int y = 0; y < detailCells; y++)
                {
                    for (int x = 0; x < detailCells; x++)
                    {
                        PolygonizeCube(scratch, detailBrick.Data, data.Origin, x, y, z, options.GenerateTangents, options.EnableVertexTint);
                        detailCellCount++;
                    }
                }
            }
        }

        int vertexCount = scratch.VertexCount;
        if (vertexCount == 0)
        {
            return new VoxelMeshBuildResult(
                Array.Empty<Vector3>(),
                Array.Empty<Vector3>(),
                Array.Empty<Vector2>(),
                Array.Empty<Color>(),
                Array.Empty<float>(),
                TotalTriangleCount: 0,
                UsedDetailBrick: detailBrick != null,
                UsedPersistentDetailEdits: detailBrick?.HasPersistentEdits == true,
                DetailTriangleCount: 0,
                ReplacedCoarseCellCount: replacedCoarseCellCount,
                DetailCellCount: detailCellCount);
        }

        return new VoxelMeshBuildResult(
            scratch.Vertices.ToArray(),
            scratch.Normals.ToArray(),
            scratch.Uvs.ToArray(),
            scratch.Colors.ToArray(),
            options.GenerateTangents
                ? scratch.Tangents.ToArray()
                : Array.Empty<float>(),
            TotalTriangleCount: vertexCount / 3,
            UsedDetailBrick: detailBrick != null,
            UsedPersistentDetailEdits: detailBrick?.HasPersistentEdits == true,
            DetailTriangleCount: (vertexCount - detailVertexStart) / 3,
            ReplacedCoarseCellCount: replacedCoarseCellCount,
            DetailCellCount: detailCellCount);
    }

    private static void PolygonizeCube(
        MeshBuildScratch scratch,
        VoxelChunkData data,
        Vector3 meshOrigin,
        int x,
        int y,
        int z,
        bool generateTangents,
        bool useVertexTint)
    {
        Span<Vector3> positions = stackalloc Vector3[8];
        Span<float> densities = stackalloc float[8];
        Span<VoxelMaterialId> materials = stackalloc VoxelMaterialId[8];
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

        Span<Vector3> edgeVertices = stackalloc Vector3[12];
        Span<Vector3> edgeNormals = stackalloc Vector3[12];
        Span<Color> edgeColors = stackalloc Color[12];
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
            edgeColors[edge] = useVertexTint
                ? MaterialColor(materials[a]).Lerp(MaterialColor(materials[b]), t)
                : Colors.White;
            Vector3 worldPosition = edgeVertices[edge] + meshOrigin;
            edgeNormals[edge] = data.SampleSurfaceNormal(worldPosition);
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

            Vector3 vertexA = edgeVertices[edgeA];
            Vector3 vertexB = edgeVertices[edgeB];
            Vector3 vertexC = edgeVertices[edgeC];
            Vector2 uvA = ComputeUv(vertexA);
            Vector2 uvB = ComputeUv(vertexB);
            Vector2 uvC = ComputeUv(vertexC);
            Vector3 faceNormal = ComputeTriangleNormal(vertexA, vertexB, vertexC);
            Vector3 normalA = AlignSmoothNormal(edgeNormals[edgeA], faceNormal);
            Vector3 normalB = AlignSmoothNormal(edgeNormals[edgeB], faceNormal);
            Vector3 normalC = AlignSmoothNormal(edgeNormals[edgeC], faceNormal);

            if (generateTangents)
            {
                Vector3 tangentNormal = (normalA + normalB + normalC).Normalized();
                if (tangentNormal.LengthSquared() <= 0.000001f)
                {
                    tangentNormal = faceNormal;
                }

                ComputeTriangleTangent(vertexA, vertexB, vertexC, uvA, uvB, uvC, tangentNormal, out float tx, out float ty, out float tz, out float tw);
                AddVertex(scratch, vertexA, normalA, uvA, edgeColors[edgeA], tx, ty, tz, tw);
                AddVertex(scratch, vertexB, normalB, uvB, edgeColors[edgeB], tx, ty, tz, tw);
                AddVertex(scratch, vertexC, normalC, uvC, edgeColors[edgeC], tx, ty, tz, tw);
            }
            else
            {
                AddVertex(scratch, vertexA, normalA, uvA, edgeColors[edgeA]);
                AddVertex(scratch, vertexB, normalB, uvB, edgeColors[edgeB]);
                AddVertex(scratch, vertexC, normalC, uvC, edgeColors[edgeC]);
            }
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

    private static Vector2 ComputeUv(Vector3 position)
    {
        return new Vector2(position.X, position.Z) * 0.09f;
    }

    private static Vector3 AlignSmoothNormal(Vector3 smoothNormal, Vector3 faceNormal)
    {
        if (smoothNormal.LengthSquared() <= 0.000001f)
        {
            return faceNormal;
        }

        return smoothNormal.Dot(faceNormal) < 0.0f
            ? -smoothNormal
            : smoothNormal;
    }

    private static Vector3 ComputeTriangleNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = (b - a).Cross(c - a);
        if (normal.LengthSquared() <= 0.000001f)
        {
            return Vector3.Up;
        }

        return normal.Normalized();
    }

    private static void ComputeTriangleTangent(
        Vector3 vertexA,
        Vector3 vertexB,
        Vector3 vertexC,
        Vector2 uvA,
        Vector2 uvB,
        Vector2 uvC,
        Vector3 normal,
        out float tangentX,
        out float tangentY,
        out float tangentZ,
        out float tangentW)
    {
        Vector3 edge1 = vertexB - vertexA;
        Vector3 edge2 = vertexC - vertexA;
        Vector2 deltaUv1 = uvB - uvA;
        Vector2 deltaUv2 = uvC - uvA;
        float determinant = (deltaUv1.X * deltaUv2.Y) - (deltaUv2.X * deltaUv1.Y);
        if (Mathf.Abs(determinant) <= 0.000001f)
        {
            Vector3 fallbackAxis = Mathf.Abs(normal.Dot(Vector3.Right)) > 0.95f
                ? Vector3.Forward
                : Vector3.Right;
            Vector3 fallbackTangent = (fallbackAxis - (normal * fallbackAxis.Dot(normal))).Normalized();
            tangentX = fallbackTangent.X;
            tangentY = fallbackTangent.Y;
            tangentZ = fallbackTangent.Z;
            tangentW = 1.0f;
            return;
        }

        float inverseDeterminant = 1.0f / determinant;
        Vector3 tangent = ((edge1 * deltaUv2.Y) - (edge2 * deltaUv1.Y)) * inverseDeterminant;
        Vector3 bitangent = ((edge2 * deltaUv1.X) - (edge1 * deltaUv2.X)) * inverseDeterminant;
        tangent = (tangent - (normal * tangent.Dot(normal))).Normalized();
        if (tangent.LengthSquared() <= 0.000001f)
        {
            tangent = Mathf.Abs(normal.Dot(Vector3.Right)) > 0.95f
                ? Vector3.Forward
                : Vector3.Right;
            tangent = (tangent - (normal * tangent.Dot(normal))).Normalized();
        }

        tangentX = tangent.X;
        tangentY = tangent.Y;
        tangentZ = tangent.Z;
        tangentW = normal.Cross(tangent).Dot(bitangent) < 0.0f
            ? -1.0f
            : 1.0f;
    }

    private static void AddVertex(
        MeshBuildScratch scratch,
        Vector3 position,
        Vector3 normal,
        Vector2 uv,
        Color color,
        float tangentX = 0.0f,
        float tangentY = 0.0f,
        float tangentZ = 0.0f,
        float tangentW = 1.0f)
    {
        scratch.Vertices.Add(position);
        scratch.Normals.Add(normal);
        scratch.Uvs.Add(uv);
        scratch.Colors.Add(color);

        if (!scratch.IncludeTangents)
        {
            return;
        }

        scratch.Tangents.Add(tangentX);
        scratch.Tangents.Add(tangentY);
        scratch.Tangents.Add(tangentZ);
        scratch.Tangents.Add(tangentW);
    }

    private static Color MaterialColor(VoxelMaterialId materialId)
    {
        return materialId switch
        {
            VoxelMaterialId.Grass => new Color(0.82f, 0.90f, 0.66f),
            VoxelMaterialId.Rock => new Color(0.78f, 0.77f, 0.75f),
            VoxelMaterialId.Cliff => new Color(0.86f, 0.76f, 0.61f),
            VoxelMaterialId.Snow => new Color(0.94f, 0.95f, 0.97f),
            VoxelMaterialId.Scorched => new Color(0.44f, 0.40f, 0.38f),
            _ => new Color(0.74f, 0.62f, 0.48f)
        };
    }

    private static MeshBuildScratch GetScratch()
    {
        return _threadScratch ??= new MeshBuildScratch();
    }

    private sealed class MeshBuildScratch
    {
        public readonly PooledBuffer<Vector3> Vertices = new();
        public readonly PooledBuffer<Vector3> Normals = new();
        public readonly PooledBuffer<Vector2> Uvs = new();
        public readonly PooledBuffer<Color> Colors = new();
        public readonly PooledBuffer<float> Tangents = new();

        public bool IncludeTangents { get; private set; }
        public int VertexCount => Vertices.Count;

        public void Reset()
        {
            Vertices.Reset();
            Normals.Reset();
            Uvs.Reset();
            Colors.Reset();
            Tangents.Reset();
            IncludeTangents = false;
        }

        public void EnableTangents()
        {
            IncludeTangents = true;
        }
    }

    private sealed class PooledBuffer<T>
    {
        private T[] _buffer = Array.Empty<T>();

        public int Count { get; private set; }

        public void Reset()
        {
            Count = 0;
        }

        public void Add(T value)
        {
            EnsureCapacity(Count + 1);
            _buffer[Count++] = value;
        }

        public T[] ToArray()
        {
            if (Count == 0)
            {
                return Array.Empty<T>();
            }

            T[] result = new T[Count];
            Array.Copy(_buffer, result, Count);
            return result;
        }

        private void EnsureCapacity(int capacity)
        {
            if (_buffer.Length >= capacity)
            {
                return;
            }

            int nextCapacity = _buffer.Length == 0
                ? 256
                : _buffer.Length * 2;
            while (nextCapacity < capacity)
            {
                nextCapacity *= 2;
            }

            T[] nextBuffer = ArrayPool<T>.Shared.Rent(nextCapacity);
            if (Count > 0)
            {
                Array.Copy(_buffer, nextBuffer, Count);
            }

            if (_buffer.Length > 0)
            {
                ArrayPool<T>.Shared.Return(_buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
            }

            _buffer = nextBuffer;
        }
    }
}
