using Godot;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace TowerOfBaby.Terrain.Voxel;

public enum VoxelMeshColorMode
{
    Neutral = 0,
    MaterialTint = 1,
    NormalDebug = 2
}

public readonly record struct VoxelMeshBuildResult(
    Vector3[] Vertices,
    Vector3[] Normals,
    Vector2[] Uvs,
    Color[] Colors,
    Color[] MaterialColors,
    float[] BiomeWeights,
    float[] Tangents,
    int NormalDebugMismatchCount,
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
            Array.Empty<Color>(),
            Array.Empty<float>(),
            Array.Empty<float>(),
            NormalDebugMismatchCount: 0,
            TotalTriangleCount: 0,
            UsedDetailBrick: false,
            UsedPersistentDetailEdits: false,
            DetailTriangleCount: 0,
            ReplacedCoarseCellCount: 0,
            DetailCellCount: 0);

    public bool HasGeometry => Vertices.Length > 0;
    public bool HasMaterialColors => MaterialColors.Length > 0;
    public bool HasBiomeWeights => BiomeWeights.Length > 0;
    public bool HasTangents => Tangents.Length > 0;
}

public readonly record struct VoxelMeshBuildOptions(
    bool GenerateTangents,
    VoxelMeshColorMode ColorMode = VoxelMeshColorMode.Neutral)
{
    public static VoxelMeshBuildOptions Default => new(false, VoxelMeshColorMode.Neutral);
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
        int normalDebugMismatchCount = 0;
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

                    normalDebugMismatchCount += PolygonizeCube(
                        scratch,
                        data,
                        data,
                        data.Origin,
                        x,
                        y,
                        z,
                        options.GenerateTangents,
                        options.ColorMode);
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
                        normalDebugMismatchCount += PolygonizeCube(
                            scratch,
                            detailBrick.Data,
                            data,
                            data.Origin,
                            x,
                            y,
                            z,
                            options.GenerateTangents,
                            options.ColorMode);
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
                Array.Empty<Color>(),
                Array.Empty<float>(),
                Array.Empty<float>(),
                NormalDebugMismatchCount: 0,
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
            scratch.MaterialColors.ToArray(),
            Array.Empty<float>(),
            options.GenerateTangents
                ? scratch.Tangents.ToArray()
                : Array.Empty<float>(),
            normalDebugMismatchCount,
            TotalTriangleCount: vertexCount / 3,
            UsedDetailBrick: detailBrick != null,
            UsedPersistentDetailEdits: detailBrick?.HasPersistentEdits == true,
            DetailTriangleCount: (vertexCount - detailVertexStart) / 3,
            ReplacedCoarseCellCount: replacedCoarseCellCount,
            DetailCellCount: detailCellCount);
    }

    private static int PolygonizeCube(
        MeshBuildScratch scratch,
        VoxelChunkData sourceData,
        VoxelChunkData normalSampleData,
        Vector3 meshOrigin,
        int x,
        int y,
        int z,
        bool generateTangents,
        VoxelMeshColorMode colorMode)
    {
        Span<Vector3> positions = stackalloc Vector3[8];
        Span<float> densities = stackalloc float[8];
        Span<VoxelMaterialId> materials = stackalloc VoxelMaterialId[8];
        int cubeIndex = 0;

        for (int corner = 0; corner < 8; corner++)
        {
            Vector3I offset = CornerOffsets[corner];
            positions[corner] = sourceData.GetPointPosition(x + offset.X, y + offset.Y, z + offset.Z) - meshOrigin;
            densities[corner] = sourceData.GetDensity(x + offset.X, y + offset.Y, z + offset.Z);
            materials[corner] = sourceData.GetMaterial(x + offset.X, y + offset.Y, z + offset.Z);
            if (densities[corner] >= sourceData.IsoLevel)
            {
                cubeIndex |= 1 << corner;
            }
        }

        int edgeMask = MarchingCubesTables.EdgeMasks[cubeIndex];
        if (edgeMask == 0)
        {
            return 0;
        }

        Span<Vector3> edgeVertices = stackalloc Vector3[12];
        Span<Vector3> edgeNormals = stackalloc Vector3[12];
        Span<Color> edgeMaterialColors = stackalloc Color[12];
        for (int edge = 0; edge < 12; edge++)
        {
            if ((edgeMask & (1 << edge)) == 0)
            {
                continue;
            }

            int a = MarchingCubesTables.EdgeVertexIndices[edge, 0];
            int b = MarchingCubesTables.EdgeVertexIndices[edge, 1];
            float t;
            edgeVertices[edge] = Interpolate(positions[a], positions[b], densities[a], densities[b], sourceData.IsoLevel, out t);
            edgeMaterialColors[edge] = MaterialColor(materials[a]).Lerp(MaterialColor(materials[b]), t);
            Vector3 worldPosition = edgeVertices[edge] + meshOrigin;
            edgeNormals[edge] = normalSampleData.SampleSurfaceNormal(worldPosition);
        }

        int normalDebugMismatchCount = 0;
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
            Vector3 referenceNormal = ResolveReferenceNormal(
                faceNormal,
                edgeNormals[edgeA],
                edgeNormals[edgeB],
                edgeNormals[edgeC]);
            if (HasNormalMismatch(edgeNormals[edgeA], edgeNormals[edgeB], edgeNormals[edgeC], referenceNormal))
            {
                normalDebugMismatchCount++;
            }

            Vector3 normalA = AlignSmoothNormal(edgeNormals[edgeA], referenceNormal);
            Vector3 normalB = AlignSmoothNormal(edgeNormals[edgeB], referenceNormal);
            Vector3 normalC = AlignSmoothNormal(edgeNormals[edgeC], referenceNormal);
            Color materialColorA = edgeMaterialColors[edgeA];
            Color materialColorB = edgeMaterialColors[edgeB];
            Color materialColorC = edgeMaterialColors[edgeC];
            Color colorA = ResolveVertexColor(colorMode, materialColorA, normalA);
            Color colorB = ResolveVertexColor(colorMode, materialColorB, normalB);
            Color colorC = ResolveVertexColor(colorMode, materialColorC, normalC);

            if (generateTangents)
            {
                Vector3 tangentNormal = (normalA + normalB + normalC).Normalized();
                if (tangentNormal.LengthSquared() <= 0.000001f)
                {
                    tangentNormal = referenceNormal;
                }

                ComputeTriangleTangent(vertexA, vertexB, vertexC, uvA, uvB, uvC, tangentNormal, out float tx, out float ty, out float tz, out float tw);
                AddVertex(scratch, vertexA, normalA, uvA, colorA, materialColorA, tx, ty, tz, tw);
                AddVertex(scratch, vertexB, normalB, uvB, colorB, materialColorB, tx, ty, tz, tw);
                AddVertex(scratch, vertexC, normalC, uvC, colorC, materialColorC, tx, ty, tz, tw);
            }
            else
            {
                AddVertex(scratch, vertexA, normalA, uvA, colorA, materialColorA);
                AddVertex(scratch, vertexB, normalB, uvB, colorB, materialColorB);
                AddVertex(scratch, vertexC, normalC, uvC, colorC, materialColorC);
            }
        }

        return normalDebugMismatchCount;
    }

    private static Vector3 ResolveReferenceNormal(
        Vector3 faceNormal,
        Vector3 normalA,
        Vector3 normalB,
        Vector3 normalC)
    {
        Vector3 smoothNormalSum = Vector3.Zero;
        if (normalA.LengthSquared() > 0.000001f)
        {
            smoothNormalSum += normalA;
        }

        if (normalB.LengthSquared() > 0.000001f)
        {
            smoothNormalSum += normalB;
        }

        if (normalC.LengthSquared() > 0.000001f)
        {
            smoothNormalSum += normalC;
        }

        if (smoothNormalSum.LengthSquared() <= 0.000001f)
        {
            return faceNormal;
        }

        return smoothNormalSum.Dot(faceNormal) < 0.0f
            ? -faceNormal
            : faceNormal;
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

    private static bool HasNormalMismatch(Vector3 normalA, Vector3 normalB, Vector3 normalC, Vector3 faceNormal)
    {
        return
            normalA.LengthSquared() > 0.000001f && normalA.Dot(faceNormal) < -0.2f ||
            normalB.LengthSquared() > 0.000001f && normalB.Dot(faceNormal) < -0.2f ||
            normalC.LengthSquared() > 0.000001f && normalC.Dot(faceNormal) < -0.2f;
    }

    private static Color ResolveVertexColor(VoxelMeshColorMode colorMode, Color materialColor, Vector3 normal)
    {
        return colorMode switch
        {
            VoxelMeshColorMode.NormalDebug => new Color(
                (normal.X * 0.5f) + 0.5f,
                (normal.Y * 0.5f) + 0.5f,
                (normal.Z * 0.5f) + 0.5f,
                1.0f),
            _ => materialColor
        };
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
        Color materialColor,
        float tangentX = 0.0f,
        float tangentY = 0.0f,
        float tangentZ = 0.0f,
        float tangentW = 1.0f)
    {
        scratch.Vertices.Add(position);
        scratch.Normals.Add(normal);
        scratch.Uvs.Add(uv);
        scratch.Colors.Add(color);
        scratch.MaterialColors.Add(materialColor);

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
            // Stronger palette separation here gives the terrain colorizer a better base to push from.
            VoxelMaterialId.Grass => new Color(0.39f, 0.55f, 0.28f),
            VoxelMaterialId.Rock => new Color(0.44f, 0.48f, 0.52f),
            VoxelMaterialId.Cliff => new Color(0.47f, 0.43f, 0.40f),
            VoxelMaterialId.Snow => new Color(0.90f, 0.93f, 0.97f),
            VoxelMaterialId.Scorched => new Color(0.18f, 0.16f, 0.15f),
            _ => new Color(0.58f, 0.39f, 0.22f)
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
        public readonly PooledBuffer<Color> MaterialColors = new();
        public readonly PooledBuffer<float> Tangents = new();

        public bool IncludeTangents { get; private set; }
        public int VertexCount => Vertices.Count;

        public void Reset()
        {
            Vertices.Reset();
            Normals.Reset();
            Uvs.Reset();
            Colors.Reset();
            MaterialColors.Reset();
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
