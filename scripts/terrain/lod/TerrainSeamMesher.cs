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
        float faceInset = Mathf.Max(0.0010f, voxelSize * 0.0045f);
        float surfaceInset = Mathf.Max(0.0015f, voxelSize * 0.016f);
        float skirtDepth = Mathf.Max(0.0035f, voxelSize * 0.020f);

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Vector2> uvs = new();
        List<Color> colors = new();

        TerrainSeamFace generatedFaces = TerrainSeamFace.None;
        int quadCount = 0;

        // Near-field seams should be handled by matching LOD coverage. These skirts are just a
        // far-field fallback for tiny mixed-LOD T-junctions, so keep them tucked under the surface.
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
                quadCount += AddSkirtQuad(
                    vertices,
                    normals,
                    uvs,
                    colors,
                    edge,
                    faceDirection,
                    faceInset,
                    surfaceInset,
                    skirtDepth);
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
            colors.ToArray(),
            Array.Empty<float>(),
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

        Color[] seamColors = baseMesh.HasMaterialColors
            ? baseMesh.MaterialColors
            : baseMesh.Colors;
        Color colorA = TryGetColor(seamColors, indexA);
        Color colorB = TryGetColor(seamColors, indexB);
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

    private static int AddSkirtQuad(
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
        Vector3 topNormalA = ResolveSeamNormal(edge.StartNormal, faceDirection, 0.28f);
        Vector3 topNormalB = ResolveSeamNormal(edge.EndNormal, faceDirection, 0.28f);
        Vector3 midNormalA = ResolveSeamNormal(edge.StartNormal, faceDirection, 0.62f);
        Vector3 midNormalB = ResolveSeamNormal(edge.EndNormal, faceDirection, 0.62f);
        Vector3 bottomNormalA = ResolveSeamNormal(edge.StartNormal, faceDirection, 0.82f);
        Vector3 bottomNormalB = ResolveSeamNormal(edge.EndNormal, faceDirection, 0.82f);

        Vector3 topA = edge.Start
            - (faceDirection * faceInset)
            - (topNormalA * (surfaceInset * 0.10f));
        Vector3 topB = edge.End
            - (faceDirection * faceInset)
            - (topNormalB * (surfaceInset * 0.10f));

        Vector3 midA = edge.Start
            + (faceDirection * (faceInset * 0.04f))
            - (midNormalA * (surfaceInset * 0.34f));
        Vector3 midB = edge.End
            + (faceDirection * (faceInset * 0.04f))
            - (midNormalB * (surfaceInset * 0.34f));

        Vector3 bottomA = edge.Start
            - (faceDirection * (faceInset * 0.12f))
            - (bottomNormalA * ((surfaceInset * 0.72f) + skirtDepth));
        Vector3 bottomB = edge.End
            - (faceDirection * (faceInset * 0.12f))
            - (bottomNormalB * ((surfaceInset * 0.72f) + skirtDepth));

        AddDoubleSidedQuad(
            vertices,
            normals,
            uvs,
            colors,
            topA,
            topB,
            midB,
            midA,
            topNormalA,
            topNormalB,
            midNormalB,
            midNormalA,
            edge.StartColor,
            edge.EndColor,
            edge.EndColor,
            edge.StartColor);
        AddDoubleSidedQuad(
            vertices,
            normals,
            uvs,
            colors,
            midA,
            midB,
            bottomB,
            bottomA,
            midNormalA,
            midNormalB,
            bottomNormalB,
            bottomNormalA,
            edge.StartColor,
            edge.EndColor,
            edge.EndColor,
            edge.StartColor);

        return 2;
    }

    private static void AddDoubleSidedQuad(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector3 normalA,
        Vector3 normalB,
        Vector3 normalC,
        Vector3 normalD,
        Color colorA,
        Color colorB,
        Color colorC,
        Color colorD)
    {
        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            a,
            b,
            c,
            normalA,
            normalB,
            normalC,
            colorA,
            colorB,
            colorC);
        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            a,
            c,
            d,
            normalA,
            normalC,
            normalD,
            colorA,
            colorC,
            colorD);

        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            c,
            b,
            a,
            normalC,
            normalB,
            normalA,
            colorC,
            colorB,
            colorA);
        AddTriangle(
            vertices,
            normals,
            uvs,
            colors,
            d,
            c,
            a,
            normalD,
            normalC,
            normalA,
            colorD,
            colorC,
            colorA);
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

    private static Vector3 ResolveSeamNormal(Vector3 boundaryNormal, Vector3 faceDirection, float faceSuppression)
    {
        if (boundaryNormal.LengthSquared() <= 0.000001f)
        {
            return -faceDirection;
        }

        Vector3 normalized = boundaryNormal.Normalized();
        float faceComponent = normalized.Dot(faceDirection);
        Vector3 surfaceBiased = normalized - (faceDirection * faceComponent * Mathf.Clamp(faceSuppression, 0.0f, 1.0f));
        if (surfaceBiased.LengthSquared() <= 0.000001f)
        {
            surfaceBiased = normalized - (faceDirection * faceComponent);
        }

        if (surfaceBiased.LengthSquared() <= 0.000001f)
        {
            Vector3 fallback = faceDirection.Cross(Vector3.Up);
            if (fallback.LengthSquared() <= 0.000001f)
            {
                fallback = faceDirection.Cross(Vector3.Right);
            }

            surfaceBiased = fallback.LengthSquared() > 0.000001f
                ? fallback.Normalized()
                : -faceDirection;
        }

        float alignment = Mathf.SmoothStep(0.15f, 0.95f, Mathf.Abs(faceComponent));
        Vector3 blended = normalized.Lerp(surfaceBiased.Normalized(), Mathf.Lerp(faceSuppression * 0.35f, faceSuppression, alignment));
        if (blended.LengthSquared() <= 0.000001f)
        {
            return surfaceBiased.Normalized();
        }

        Vector3 resolved = blended.Normalized();
        return resolved.Dot(normalized) < 0.0f
            ? -resolved
            : resolved;
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
