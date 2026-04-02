using Godot;
using System;
using System.Collections.Generic;
using BitOperations = System.Numerics.BitOperations;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

[Flags]
public enum TerrainSeamFace
{
    None = 0,
    NegativeX = 1 << 0,
    PositiveX = 1 << 1,
    NegativeY = 1 << 2,
    PositiveY = 1 << 3,
    NegativeZ = 1 << 4,
    PositiveZ = 1 << 5
}

public readonly record struct TerrainSeamBuildResult(
    VoxelMeshBuildResult Mesh,
    TerrainSeamFace RequestedFaces,
    TerrainSeamFace GeneratedFaces,
    int GeneratedBorderCount,
    int QuadCount,
    string Strategy)
{
    public static TerrainSeamBuildResult None =>
        new(
            VoxelMeshBuildResult.Empty,
            TerrainSeamFace.None,
            TerrainSeamFace.None,
            GeneratedBorderCount: 0,
            QuadCount: 0,
            Strategy: "none");
}

public static class TerrainSeamMesher
{
    public const string MixedLodStrategyName = "mixed_lod_skirts";

    public static TerrainSeamBuildResult BuildMixedLodSeams(
        TerrainConfig config,
        TerrainBlockId blockId,
        VoxelMeshBuildResult baseMesh,
        TerrainSeamFace requestedFaces)
    {
        if (!baseMesh.HasGeometry || requestedFaces == TerrainSeamFace.None)
        {
            return TerrainSeamBuildResult.None with
            {
                RequestedFaces = requestedFaces,
                Strategy = requestedFaces == TerrainSeamFace.None
                    ? "none"
                    : MixedLodStrategyName
            };
        }

        float span = TerrainMetrics.GetBlockSpan(config, blockId.Lod);
        float voxelSize = TerrainMetrics.GetVoxelSize(config, blockId.Lod);
        float planeEpsilon = Mathf.Max(0.0005f, voxelSize * 0.001f);
        float quantizeStep = Mathf.Max(0.0005f, voxelSize * 0.01f);
        float faceInset = Mathf.Max(0.0025f, voxelSize * 0.015f);
        float surfaceInset = Mathf.Max(0.005f, voxelSize * 0.06f);
        float skirtDepth = Mathf.Max(0.01f, voxelSize * 0.08f);

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Vector2> uvs = new();
        List<Color> colors = new();

        TerrainSeamFace generatedFaces = TerrainSeamFace.None;
        int quadCount = 0;

        // Current seam strategy: use a very small skirt because the mixed-LOD issue here is
        // a T-junction raster crack, not a wide missing chunk. Keep the skirt tucked slightly
        // under the fine surface so it does not show up as a dark border at grazing angles.
        foreach (TerrainSeamFace face in EnumerateFaces(requestedFaces))
        {
            List<BoundaryEdge> boundaryEdges = CollectBoundaryEdgesForFace(
                baseMesh,
                face,
                span,
                planeEpsilon,
                quantizeStep);
            if (boundaryEdges.Count == 0)
            {
                continue;
            }

            generatedFaces |= face;
            Vector3 faceDirection = GetFaceNormal(face);
            foreach (BoundaryEdge edge in boundaryEdges)
            {
                AddSkirtQuad(vertices, normals, uvs, colors, edge, faceDirection, faceInset, surfaceInset, skirtDepth);
                quadCount++;
            }
        }

        if (vertices.Count == 0)
        {
            return new TerrainSeamBuildResult(
                VoxelMeshBuildResult.Empty,
                requestedFaces,
                TerrainSeamFace.None,
                GeneratedBorderCount: 0,
                QuadCount: 0,
                Strategy: MixedLodStrategyName);
        }

        VoxelMeshBuildResult seamMesh = new(
            vertices.ToArray(),
            normals.ToArray(),
            uvs.ToArray(),
            colors.ToArray(),
            Array.Empty<float>(),
            NormalDebugMismatchCount: 0,
            TotalTriangleCount: vertices.Count / 3,
            UsedDetailBrick: false,
            UsedPersistentDetailEdits: false,
            DetailTriangleCount: 0,
            ReplacedCoarseCellCount: 0,
            DetailCellCount: 0);

        return new TerrainSeamBuildResult(
            seamMesh,
            requestedFaces,
            generatedFaces,
            GeneratedBorderCount: CountFaces(generatedFaces),
            QuadCount: quadCount,
            Strategy: MixedLodStrategyName);
    }

    public static int CountFaces(TerrainSeamFace faces)
    {
        return BitOperations.PopCount((uint)faces);
    }

    public static string DescribeFaces(TerrainSeamFace faces)
    {
        if (faces == TerrainSeamFace.None)
        {
            return "none";
        }

        List<string> labels = new();
        foreach (TerrainSeamFace face in EnumerateFaces(faces))
        {
            labels.Add(face switch
            {
                TerrainSeamFace.NegativeX => "-x",
                TerrainSeamFace.PositiveX => "+x",
                TerrainSeamFace.NegativeY => "-y",
                TerrainSeamFace.PositiveY => "+y",
                TerrainSeamFace.NegativeZ => "-z",
                TerrainSeamFace.PositiveZ => "+z",
                _ => "?"
            });
        }

        return string.Join(",", labels);
    }

    private static IEnumerable<TerrainSeamFace> EnumerateFaces(TerrainSeamFace faces)
    {
        if ((faces & TerrainSeamFace.NegativeX) != 0)
        {
            yield return TerrainSeamFace.NegativeX;
        }

        if ((faces & TerrainSeamFace.PositiveX) != 0)
        {
            yield return TerrainSeamFace.PositiveX;
        }

        if ((faces & TerrainSeamFace.NegativeY) != 0)
        {
            yield return TerrainSeamFace.NegativeY;
        }

        if ((faces & TerrainSeamFace.PositiveY) != 0)
        {
            yield return TerrainSeamFace.PositiveY;
        }

        if ((faces & TerrainSeamFace.NegativeZ) != 0)
        {
            yield return TerrainSeamFace.NegativeZ;
        }

        if ((faces & TerrainSeamFace.PositiveZ) != 0)
        {
            yield return TerrainSeamFace.PositiveZ;
        }
    }

    private static List<BoundaryEdge> CollectBoundaryEdgesForFace(
        VoxelMeshBuildResult baseMesh,
        TerrainSeamFace face,
        float span,
        float planeEpsilon,
        float quantizeStep)
    {
        Dictionary<EdgeKey, BoundaryEdgeAccumulator> edges = new();
        for (int i = 0; i < baseMesh.Vertices.Length; i += 3)
        {
            TryAddBoundaryEdge(baseMesh, face, span, planeEpsilon, quantizeStep, edges, i, i + 1);
            TryAddBoundaryEdge(baseMesh, face, span, planeEpsilon, quantizeStep, edges, i + 1, i + 2);
            TryAddBoundaryEdge(baseMesh, face, span, planeEpsilon, quantizeStep, edges, i + 2, i);
        }

        List<BoundaryEdge> result = new();
        foreach (BoundaryEdgeAccumulator edge in edges.Values)
        {
            if (edge.Count == 1)
            {
                result.Add(edge.Edge);
            }
        }

        return result;
    }

    private static void TryAddBoundaryEdge(
        VoxelMeshBuildResult baseMesh,
        TerrainSeamFace face,
        float span,
        float planeEpsilon,
        float quantizeStep,
        Dictionary<EdgeKey, BoundaryEdgeAccumulator> edges,
        int indexA,
        int indexB)
    {
        Vector3 vertexA = baseMesh.Vertices[indexA];
        Vector3 vertexB = baseMesh.Vertices[indexB];
        if (!IsOnFace(vertexA, face, span, planeEpsilon) ||
            !IsOnFace(vertexB, face, span, planeEpsilon))
        {
            return;
        }

        if (vertexA.DistanceSquaredTo(vertexB) <= planeEpsilon * planeEpsilon)
        {
            return;
        }

        EdgeKey key = new(
            Quantize(vertexA, quantizeStep),
            Quantize(vertexB, quantizeStep));
        if (edges.TryGetValue(key, out BoundaryEdgeAccumulator existing))
        {
            edges[key] = existing with { Count = existing.Count + 1 };
            return;
        }

        Color colorA = TryGetColor(baseMesh.Colors, indexA);
        Color colorB = TryGetColor(baseMesh.Colors, indexB);
        Vector3 normalA = TryGetNormal(baseMesh.Normals, indexA);
        Vector3 normalB = TryGetNormal(baseMesh.Normals, indexB);
        edges[key] = new BoundaryEdgeAccumulator(
            Count: 1,
            Edge: new BoundaryEdge(vertexA, vertexB, normalA, normalB, colorA, colorB));
    }

    private static bool IsOnFace(Vector3 vertex, TerrainSeamFace face, float span, float planeEpsilon)
    {
        float plane = face switch
        {
            TerrainSeamFace.NegativeX => 0.0f,
            TerrainSeamFace.PositiveX => span,
            TerrainSeamFace.NegativeY => 0.0f,
            TerrainSeamFace.PositiveY => span,
            TerrainSeamFace.NegativeZ => 0.0f,
            TerrainSeamFace.PositiveZ => span,
            _ => 0.0f
        };
        float coordinate = face switch
        {
            TerrainSeamFace.NegativeX or TerrainSeamFace.PositiveX => vertex.X,
            TerrainSeamFace.NegativeY or TerrainSeamFace.PositiveY => vertex.Y,
            TerrainSeamFace.NegativeZ or TerrainSeamFace.PositiveZ => vertex.Z,
            _ => 0.0f
        };

        return Mathf.Abs(coordinate - plane) <= planeEpsilon;
    }

    private static QuantizedVector Quantize(Vector3 value, float step)
    {
        return new QuantizedVector(
            Mathf.RoundToInt(value.X / step),
            Mathf.RoundToInt(value.Y / step),
            Mathf.RoundToInt(value.Z / step));
    }

    private static Color TryGetColor(Color[] colors, int index)
    {
        return colors != null && index >= 0 && index < colors.Length
            ? colors[index]
            : Colors.White;
    }

    private static Vector3 TryGetNormal(Vector3[] normals, int index)
    {
        if (normals == null || index < 0 || index >= normals.Length)
        {
            return Vector3.Up;
        }

        Vector3 normal = normals[index];
        return normal.LengthSquared() > 0.000001f
            ? normal.Normalized()
            : Vector3.Up;
    }

    private static void AddSkirtQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors,
        BoundaryEdge edge,
        Vector3 faceDirection,
        float faceInset,
        float surfaceInset,
        float skirtDepth)
    {
        Vector3 startNormal = ResolveSeamNormal(edge.StartNormal, faceDirection);
        Vector3 endNormal = ResolveSeamNormal(edge.EndNormal, faceDirection);
        Vector3 topInsetA = (faceDirection * faceInset) + (startNormal * (surfaceInset * 0.35f));
        Vector3 topInsetB = (faceDirection * faceInset) + (endNormal * (surfaceInset * 0.35f));
        Vector3 bottomOffsetA = (faceDirection * skirtDepth) - (startNormal * surfaceInset);
        Vector3 bottomOffsetB = (faceDirection * skirtDepth) - (endNormal * surfaceInset);

        Vector3 a = edge.Start - topInsetA;
        Vector3 b = edge.End - topInsetB;
        Vector3 c = edge.End + bottomOffsetB;
        Vector3 d = edge.Start + bottomOffsetA;

        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            a,
            b,
            c,
            startNormal,
            endNormal,
            endNormal,
            edge.StartColor,
            edge.EndColor,
            edge.EndColor);
        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            a,
            c,
            d,
            startNormal,
            endNormal,
            startNormal,
            edge.StartColor,
            edge.EndColor,
            edge.StartColor);

        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            c,
            b,
            a,
            endNormal,
            endNormal,
            startNormal,
            edge.EndColor,
            edge.EndColor,
            edge.StartColor);
        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            d,
            c,
            a,
            startNormal,
            endNormal,
            startNormal,
            edge.StartColor,
            edge.EndColor,
            edge.StartColor);
    }

    private static void AddTriangle(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 normalA,
        Vector3 normalB,
        Vector3 normalC,
        Color colorA,
        Color colorB,
        Color colorC)
    {
        AddVertex(vertices, normals, uvs, colors, a, normalA, colorA);
        AddVertex(vertices, normals, uvs, colors, b, normalB, colorB);
        AddVertex(vertices, normals, uvs, colors, c, normalC, colorC);
    }

    private static void AddVertex(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors,
        Vector3 position,
        Vector3 normal,
        Color color)
    {
        vertices.Add(position);
        normals.Add(normal);
        uvs.Add(new Vector2(position.X, position.Z) * 0.09f);
        colors.Add(color);
    }

    private static Vector3 GetFaceNormal(TerrainSeamFace face)
    {
        return face switch
        {
            TerrainSeamFace.NegativeX => Vector3.Left,
            TerrainSeamFace.PositiveX => Vector3.Right,
            TerrainSeamFace.NegativeY => Vector3.Down,
            TerrainSeamFace.PositiveY => Vector3.Up,
            TerrainSeamFace.NegativeZ => Vector3.Forward,
            TerrainSeamFace.PositiveZ => Vector3.Back,
            _ => Vector3.Zero
        };
    }

    private static Vector3 ResolveSeamNormal(Vector3 boundaryNormal, Vector3 faceDirection)
    {
        if (boundaryNormal.LengthSquared() <= 0.000001f)
        {
            return -faceDirection;
        }

        Vector3 normalized = boundaryNormal.Normalized();
        if (Mathf.Abs(normalized.Dot(faceDirection)) > 0.92f)
        {
            Vector3 blended = (normalized - (faceDirection * 0.35f)).Normalized();
            if (blended.LengthSquared() > 0.000001f)
            {
                return blended;
            }
        }

        return normalized;
    }

    private readonly record struct QuantizedVector(int X, int Y, int Z) : IComparable<QuantizedVector>
    {
        public int CompareTo(QuantizedVector other)
        {
            int xCompare = X.CompareTo(other.X);
            if (xCompare != 0)
            {
                return xCompare;
            }

            int yCompare = Y.CompareTo(other.Y);
            if (yCompare != 0)
            {
                return yCompare;
            }

            return Z.CompareTo(other.Z);
        }
    }

    private readonly record struct EdgeKey
    {
        public EdgeKey(QuantizedVector a, QuantizedVector b)
        {
            if (a.CompareTo(b) <= 0)
            {
                A = a;
                B = b;
            }
            else
            {
                A = b;
                B = a;
            }
        }

        public QuantizedVector A { get; }
        public QuantizedVector B { get; }
    }

    private readonly record struct BoundaryEdge(
        Vector3 Start,
        Vector3 End,
        Vector3 StartNormal,
        Vector3 EndNormal,
        Color StartColor,
        Color EndColor);

    private readonly record struct BoundaryEdgeAccumulator(int Count, BoundaryEdge Edge);
}
