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

public readonly record struct TerrainSeamNeighborData(
    TerrainBlockId BlockId,
    Vector3 Origin,
    VoxelMeshBuildResult Mesh);

public enum TerrainSeamFaceFinalMode
{
    None = 0,
    TransitionGenerated = 1,
    SkirtGenerated = 2,
    ExplicitSkipNoBoundary = 3
}

public readonly record struct TerrainSeamFaceDiagnostic(
    TerrainBlockId BlockId,
    TerrainSeamFace Face,
    bool Requested,
    bool Suppressed,
    TerrainBlockId? TransitionNeighborId,
    bool TransitionAttempted,
    bool TransitionSucceeded,
    bool SkirtFallbackEnabled,
    TerrainSeamFaceFinalMode FinalMode);

public readonly record struct TerrainSeamBuildResult(
    VoxelMeshBuildResult Mesh,
    TerrainSeamFace RequestedFaces,
    TerrainSeamFace GeneratedFaces,
    TerrainSeamFace TransitionFaces,
    TerrainSeamFace SkirtFaces,
    TerrainSeamFaceDiagnostic[] FaceDiagnostics,
    int GeneratedBorderCount,
    int QuadCount,
    string Strategy)
{
    public static TerrainSeamBuildResult None =>
        new(
            VoxelMeshBuildResult.Empty,
            TerrainSeamFace.None,
            TerrainSeamFace.None,
            TerrainSeamFace.None,
            TerrainSeamFace.None,
            Array.Empty<TerrainSeamFaceDiagnostic>(),
            GeneratedBorderCount: 0,
            QuadCount: 0,
            Strategy: "none");

    public int TransitionFaceCount => TerrainSeamMesher.CountFaceDiagnostics(FaceDiagnostics, TerrainSeamFaceFinalMode.TransitionGenerated);
    public int SkirtFaceCount => TerrainSeamMesher.CountFaceDiagnostics(FaceDiagnostics, TerrainSeamFaceFinalMode.SkirtGenerated);
    public int ExplicitSkipFaceCount => TerrainSeamMesher.CountFaceDiagnostics(FaceDiagnostics, TerrainSeamFaceFinalMode.ExplicitSkipNoBoundary);
    public int SuppressedFaceCount => TerrainSeamMesher.CountSuppressedFaceDiagnostics(FaceDiagnostics);
}

public static class TerrainSeamMesher
{
    public const string MixedLodStrategyName = "mixed_lod_skirts";
    public const string TransitionStrategyName = "mixed_lod_transition_xz_v1";
    private const float MinNormalLengthSquared = 0.000001f;
    private const float MinTriangleAreaSquared = 0.0000001f;

    public static TerrainSeamBuildResult BuildMixedLodSeams(
        TerrainConfig config,
        TerrainBlockId blockId,
        Vector3 blockOrigin,
        VoxelMeshBuildResult baseMesh,
        TerrainSeamFace requestedFaces,
        TerrainSeamFace skirtFaces,
        IReadOnlyDictionary<TerrainSeamFace, TerrainSeamNeighborData> transitionNeighbors)
    {
        if (!baseMesh.HasGeometry || requestedFaces == TerrainSeamFace.None)
        {
            return TerrainSeamBuildResult.None with
            {
                RequestedFaces = requestedFaces,
                Strategy = requestedFaces == TerrainSeamFace.None
                    ? "none"
                    : BuildStrategyLabel(TerrainSeamFace.None, skirtFaces)
            };
        }

        transitionNeighbors ??= EmptyTransitionNeighborMap.Instance;

        float span = TerrainMetrics.GetBlockSpan(config, blockId.Lod);
        float voxelSize = TerrainMetrics.GetVoxelSize(config, blockId.Lod);
        float planeEpsilon = Mathf.Max(0.0005f, voxelSize * 0.001f);
        float quantizeStep = Mathf.Max(0.0005f, voxelSize * 0.01f);
        // Keep skirt seams tucked farther under the surface so the mixed-LOD boundary reads more like
        // hidden overlap coverage than a visible hanging curtain.
        float faceInset = Mathf.Max(0.0012f, voxelSize * 0.0065f);
        float surfaceInset = Mathf.Max(0.0020f, voxelSize * 0.022f);
        float skirtDepth = Mathf.Max(0.0030f, voxelSize * 0.016f);

        List<Vector3> vertices = new();
        List<Vector3> normals = new();
        List<Vector2> uvs = new();
        List<Color> colors = new();
        List<TerrainSeamFaceDiagnostic> faceDiagnostics = new();

        TerrainSeamFace generatedFaces = TerrainSeamFace.None;
        TerrainSeamFace generatedTransitionFaces = TerrainSeamFace.None;
        TerrainSeamFace generatedSkirtFaces = TerrainSeamFace.None;
        int quadCount = 0;

        foreach (TerrainSeamFace face in EnumerateFaces(requestedFaces))
        {
            bool suppressed = false;
            TerrainBlockId? transitionNeighborId = null;
            bool generatedTransition = false;
            bool transitionAttempted = false;
            bool skirtFallbackEnabled = (skirtFaces & face) != 0;
            TerrainSeamFaceFinalMode finalMode = TerrainSeamFaceFinalMode.None;

            if (transitionNeighbors.TryGetValue(face, out TerrainSeamNeighborData neighbor))
            {
                transitionNeighborId = neighbor.BlockId;
                if (SupportsTransitionFace(face))
                {
                    transitionAttempted = true;
                    generatedTransition = TryAddTransitionFace(
                        config,
                        blockId,
                        blockOrigin,
                        baseMesh,
                        face,
                        neighbor,
                        span,
                        voxelSize,
                        planeEpsilon,
                        quantizeStep,
                        vertices,
                        normals,
                        uvs,
                        colors);
                }
            }

            if (generatedTransition)
            {
                generatedFaces |= face;
                generatedTransitionFaces |= face;
                finalMode = TerrainSeamFaceFinalMode.TransitionGenerated;
                faceDiagnostics.Add(
                    new TerrainSeamFaceDiagnostic(
                        blockId,
                        face,
                        Requested: true,
                        Suppressed: false,
                        TransitionNeighborId: transitionNeighborId,
                        TransitionAttempted: transitionAttempted,
                        TransitionSucceeded: true,
                        SkirtFallbackEnabled: skirtFallbackEnabled,
                        FinalMode: finalMode));
                continue;
            }

            if (!skirtFallbackEnabled)
            {
                suppressed = true;
                finalMode = TerrainSeamFaceFinalMode.ExplicitSkipNoBoundary;
                faceDiagnostics.Add(
                    new TerrainSeamFaceDiagnostic(
                        blockId,
                        face,
                        Requested: true,
                        Suppressed: suppressed,
                        TransitionNeighborId: transitionNeighborId,
                        TransitionAttempted: transitionAttempted,
                        TransitionSucceeded: false,
                        SkirtFallbackEnabled: false,
                        FinalMode: finalMode));
                continue;
            }

            List<BoundaryEdge> boundaryEdges = CollectBoundaryEdgesForFace(
                baseMesh,
                face,
                span,
                planeEpsilon,
                quantizeStep);
            if (boundaryEdges.Count == 0)
            {
                finalMode = TerrainSeamFaceFinalMode.ExplicitSkipNoBoundary;
                faceDiagnostics.Add(
                    new TerrainSeamFaceDiagnostic(
                        blockId,
                        face,
                        Requested: true,
                        Suppressed: false,
                        TransitionNeighborId: transitionNeighborId,
                        TransitionAttempted: transitionAttempted,
                        TransitionSucceeded: false,
                        SkirtFallbackEnabled: true,
                        FinalMode: finalMode));
                continue;
            }

            generatedFaces |= face;
            generatedSkirtFaces |= face;
            finalMode = TerrainSeamFaceFinalMode.SkirtGenerated;
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

            faceDiagnostics.Add(
                new TerrainSeamFaceDiagnostic(
                    blockId,
                    face,
                    Requested: true,
                    Suppressed: false,
                    TransitionNeighborId: transitionNeighborId,
                    TransitionAttempted: transitionAttempted,
                    TransitionSucceeded: false,
                    SkirtFallbackEnabled: true,
                    FinalMode: finalMode));
        }

        if (vertices.Count == 0)
        {
            return new TerrainSeamBuildResult(
                VoxelMeshBuildResult.Empty,
                requestedFaces,
                TerrainSeamFace.None,
                TerrainSeamFace.None,
                TerrainSeamFace.None,
                faceDiagnostics.ToArray(),
                GeneratedBorderCount: 0,
                QuadCount: 0,
                Strategy: BuildStrategyLabel(TerrainSeamFace.None, TerrainSeamFace.None));
        }

        Color[] seamColors = colors.ToArray();
        VoxelMeshBuildResult seamMesh = new(
            vertices.ToArray(),
            normals.ToArray(),
            uvs.ToArray(),
            seamColors,
            seamColors,
            Array.Empty<VoxelMaterialId>(),
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
            generatedTransitionFaces,
            generatedSkirtFaces,
            faceDiagnostics.ToArray(),
            GeneratedBorderCount: CountFaces(generatedFaces),
            QuadCount: quadCount,
            Strategy: BuildStrategyLabel(generatedTransitionFaces, generatedSkirtFaces));
    }

    public static bool SupportsTransitionFace(TerrainSeamFace face)
    {
        return face is TerrainSeamFace.NegativeX or TerrainSeamFace.PositiveX or TerrainSeamFace.NegativeZ or TerrainSeamFace.PositiveZ;
    }

    public static int CountFaces(TerrainSeamFace faces)
    {
        return BitOperations.PopCount((uint)faces);
    }

    public static int CountFaceDiagnostics(
        IReadOnlyList<TerrainSeamFaceDiagnostic> faceDiagnostics,
        TerrainSeamFaceFinalMode mode)
    {
        if (faceDiagnostics == null || faceDiagnostics.Count == 0)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < faceDiagnostics.Count; i++)
        {
            if (faceDiagnostics[i].FinalMode == mode)
            {
                count++;
            }
        }

        return count;
    }

    public static int CountSuppressedFaceDiagnostics(IReadOnlyList<TerrainSeamFaceDiagnostic> faceDiagnostics)
    {
        if (faceDiagnostics == null || faceDiagnostics.Count == 0)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < faceDiagnostics.Count; i++)
        {
            if (faceDiagnostics[i].Suppressed)
            {
                count++;
            }
        }

        return count;
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

    public static string DescribeFaceDiagnostics(IReadOnlyList<TerrainSeamFaceDiagnostic> diagnostics)
    {
        if (diagnostics == null || diagnostics.Count == 0)
        {
            return "none";
        }

        List<string> labels = new(diagnostics.Count);
        for (int i = 0; i < diagnostics.Count; i++)
        {
            TerrainSeamFaceDiagnostic diagnostic = diagnostics[i];
            string neighborId = diagnostic.TransitionNeighborId?.ToString() ?? "none";
            labels.Add(
                $"{DescribeFaces(diagnostic.Face)} req {(diagnostic.Requested ? "y" : "n")} sup {(diagnostic.Suppressed ? "y" : "n")} " +
                $"neigh {neighborId} try {(diagnostic.TransitionAttempted ? "y" : "n")} ok {(diagnostic.TransitionSucceeded ? "y" : "n")} " +
                $"skirt {(diagnostic.SkirtFallbackEnabled ? "y" : "n")} final {GetDisplayName(diagnostic.FinalMode)}");
        }

        return string.Join(" | ", labels);
    }

    public static string GetDisplayName(TerrainSeamFaceFinalMode mode)
    {
        return mode switch
        {
            TerrainSeamFaceFinalMode.TransitionGenerated => "transition",
            TerrainSeamFaceFinalMode.SkirtGenerated => "skirt",
            TerrainSeamFaceFinalMode.ExplicitSkipNoBoundary => "skip_no_boundary",
            _ => "none"
        };
    }

    public static IEnumerable<TerrainSeamFace> EnumerateFaces(TerrainSeamFace faces)
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

    private static bool TryAddTransitionFace(
        TerrainConfig config,
        TerrainBlockId fineBlockId,
        Vector3 fineBlockOrigin,
        VoxelMeshBuildResult fineMesh,
        TerrainSeamFace face,
        TerrainSeamNeighborData coarseNeighbor,
        float fineSpan,
        float fineVoxelSize,
        float planeEpsilon,
        float quantizeStep,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors)
    {
        if (!fineMesh.HasGeometry ||
            !coarseNeighbor.Mesh.HasGeometry ||
            fineBlockId.Lod + 1 != coarseNeighbor.BlockId.Lod)
        {
            return false;
        }

        float coarseSpan = TerrainMetrics.GetBlockSpan(config, coarseNeighbor.BlockId.Lod);
        if (Mathf.Abs(coarseSpan - (fineSpan * 2.0f)) > Mathf.Max(0.01f, fineVoxelSize * 0.5f))
        {
            return false;
        }

        float coarsePlaneEpsilon = Mathf.Max(
            planeEpsilon,
            TerrainMetrics.GetVoxelSize(config, coarseNeighbor.BlockId.Lod) * 0.001f);

        Rect2 clipRect = BuildFaceRect(fineBlockOrigin, fineSpan, face);
        List<BoundarySegment> fineSegments = CollectTransitionSegmentsForFace(
            fineMesh,
            face,
            fineSpan,
            planeEpsilon,
            quantizeStep,
            fineBlockOrigin,
            clipRect: null);
        if (fineSegments.Count == 0)
        {
            return false;
        }

        List<BoundarySegment> coarseSegments = CollectTransitionSegmentsForFace(
            coarseNeighbor.Mesh,
            GetOppositeFace(face),
            coarseSpan,
            coarsePlaneEpsilon,
            quantizeStep,
            coarseNeighbor.Origin,
            clipRect);
        if (coarseSegments.Count == 0)
        {
            return false;
        }

        float minContourLength = Mathf.Max(fineVoxelSize * 0.35f, 0.02f);
        List<BoundaryContour> fineContours = BuildContours(fineSegments, minContourLength, quantizeStep);
        List<BoundaryContour> coarseContours = BuildContours(coarseSegments, minContourLength, quantizeStep);
        if (fineContours.Count == 0 || fineContours.Count != coarseContours.Count)
        {
            return false;
        }

        if (!TryMatchContours(fineContours, coarseContours, fineVoxelSize, out List<ContourMatch> matches))
        {
            return false;
        }

        List<Vector3> faceVertices = new();
        List<Vector3> faceNormals = new();
        List<Vector2> faceUvs = new();
        List<Color> faceColors = new();

        foreach (ContourMatch match in matches)
        {
            if (!TryAddContourBridge(
                    fineBlockOrigin,
                    match.Fine,
                    match.Coarse,
                    faceVertices,
                    faceNormals,
                    faceUvs,
                    faceColors))
            {
                return false;
            }
        }

        if (faceVertices.Count == 0)
        {
            return false;
        }

        vertices.AddRange(faceVertices);
        normals.AddRange(faceNormals);
        uvs.AddRange(faceUvs);
        colors.AddRange(faceColors);
        return true;
    }

    private static List<BoundarySegment> CollectTransitionSegmentsForFace(
        VoxelMeshBuildResult mesh,
        TerrainSeamFace face,
        float span,
        float planeEpsilon,
        float quantizeStep,
        Vector3 meshOrigin,
        Rect2? clipRect)
    {
        List<BoundaryEdge> edges = CollectBoundaryEdgesForFace(mesh, face, span, planeEpsilon, quantizeStep);
        List<BoundarySegment> segments = new(edges.Count);
        foreach (BoundaryEdge edge in edges)
        {
            ContourVertex start = CreateContourVertex(edge.Start + meshOrigin, edge.StartNormal, edge.StartColor, face);
            ContourVertex end = CreateContourVertex(edge.End + meshOrigin, edge.EndNormal, edge.EndColor, face);
            if (clipRect.HasValue &&
                !TryClipSegmentToRect(start, end, clipRect.Value, out start, out end))
            {
                continue;
            }

            if (start.PlanePosition.DistanceSquaredTo(end.PlanePosition) <= quantizeStep * quantizeStep)
            {
                continue;
            }

            segments.Add(CreateBoundarySegment(start, end, quantizeStep));
        }

        return segments;
    }

    private static List<BoundaryContour> BuildContours(
        List<BoundarySegment> segments,
        float minContourLength,
        float quantizeStep)
    {
        List<BoundaryContour> contours = new();
        if (segments.Count == 0)
        {
            return contours;
        }

        Dictionary<QuantizedPoint2, List<int>> adjacency = new();
        for (int i = 0; i < segments.Count; i++)
        {
            AddAdjacency(adjacency, segments[i].StartKey, i);
            AddAdjacency(adjacency, segments[i].EndKey, i);
        }

        bool[] visited = new bool[segments.Count];
        foreach ((QuantizedPoint2 point, List<int> segmentIndices) in adjacency)
        {
            if (segmentIndices.Count != 1)
            {
                continue;
            }

            int segmentIndex = segmentIndices[0];
            if (visited[segmentIndex])
            {
                continue;
            }

            BoundaryContour contour = WalkContour(point, segmentIndex, segments, adjacency, visited, quantizeStep);
            if (IsUsableContour(contour, minContourLength))
            {
                contours.Add(contour);
            }
        }

        for (int i = 0; i < segments.Count; i++)
        {
            if (visited[i])
            {
                continue;
            }

            BoundaryContour contour = WalkContour(segments[i].StartKey, i, segments, adjacency, visited, quantizeStep);
            if (IsUsableContour(contour, minContourLength))
            {
                contours.Add(contour);
            }
        }

        return contours;
    }

    private static bool TryMatchContours(
        List<BoundaryContour> fineContours,
        List<BoundaryContour> coarseContours,
        float fineVoxelSize,
        out List<ContourMatch> matches)
    {
        matches = new List<ContourMatch>(fineContours.Count);
        bool[] coarseUsed = new bool[coarseContours.Count];

        List<BoundaryContour> sortedFine = new(fineContours);
        sortedFine.Sort((left, right) => right.Length.CompareTo(left.Length));

        float maxCenterDistance = Mathf.Max(fineVoxelSize * 6.0f, 0.25f);
        foreach (BoundaryContour fine in sortedFine)
        {
            int bestIndex = -1;
            float bestScore = float.MaxValue;
            for (int coarseIndex = 0; coarseIndex < coarseContours.Count; coarseIndex++)
            {
                if (coarseUsed[coarseIndex])
                {
                    continue;
                }

                BoundaryContour coarse = coarseContours[coarseIndex];
                if (fine.Closed != coarse.Closed)
                {
                    continue;
                }

                float centerDistance = fine.Center.DistanceTo(coarse.Center);
                if (centerDistance > maxCenterDistance)
                {
                    continue;
                }

                float score = fine.Closed
                    ? centerDistance + (Mathf.Abs(fine.Length - coarse.Length) * 0.25f)
                    : ComputeOpenContourEndpointScore(fine, coarse);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = coarseIndex;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            coarseUsed[bestIndex] = true;
            matches.Add(new ContourMatch(fine, coarseContours[bestIndex]));
        }

        return matches.Count == fineContours.Count;
    }

    private static float ComputeOpenContourEndpointScore(BoundaryContour fine, BoundaryContour coarse)
    {
        ContourVertex fineStart = fine.Vertices[0];
        ContourVertex fineEnd = fine.Vertices[fine.Vertices.Length - 1];
        ContourVertex coarseStart = coarse.Vertices[0];
        ContourVertex coarseEnd = coarse.Vertices[coarse.Vertices.Length - 1];

        float direct =
            fineStart.PlanePosition.DistanceTo(coarseStart.PlanePosition) +
            fineEnd.PlanePosition.DistanceTo(coarseEnd.PlanePosition);
        float reversed =
            fineStart.PlanePosition.DistanceTo(coarseEnd.PlanePosition) +
            fineEnd.PlanePosition.DistanceTo(coarseStart.PlanePosition);
        return Mathf.Min(direct, reversed);
    }

    private static bool TryAddContourBridge(
        Vector3 blockOrigin,
        BoundaryContour fineContour,
        BoundaryContour coarseContour,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors)
    {
        ContourVertex[] fineVertices;
        ContourVertex[] coarseVertices;
        if (fineContour.Closed)
        {
            if (!TryAlignClosedContours(fineContour, coarseContour, out fineVertices, out coarseVertices))
            {
                return false;
            }
        }
        else
        {
            fineVertices = (ContourVertex[])fineContour.Vertices.Clone();
            coarseVertices = (ContourVertex[])coarseContour.Vertices.Clone();
            float direct =
                fineVertices[0].PlanePosition.DistanceSquaredTo(coarseVertices[0].PlanePosition) +
                fineVertices[fineVertices.Length - 1].PlanePosition.DistanceSquaredTo(coarseVertices[coarseVertices.Length - 1].PlanePosition);
            float reversed =
                fineVertices[0].PlanePosition.DistanceSquaredTo(coarseVertices[coarseVertices.Length - 1].PlanePosition) +
                fineVertices[fineVertices.Length - 1].PlanePosition.DistanceSquaredTo(coarseVertices[0].PlanePosition);
            if (reversed < direct)
            {
                Array.Reverse(coarseVertices);
            }
        }

        int triangleCountBefore = vertices.Count;
        int fineIndex = 0;
        int coarseIndex = 0;
        while (fineIndex < fineVertices.Length - 1 || coarseIndex < coarseVertices.Length - 1)
        {
            bool canAdvanceFine = fineIndex < fineVertices.Length - 1;
            bool canAdvanceCoarse = coarseIndex < coarseVertices.Length - 1;
            if (!canAdvanceFine && !canAdvanceCoarse)
            {
                break;
            }

            bool advanceFine;
            if (!canAdvanceCoarse)
            {
                advanceFine = true;
            }
            else if (!canAdvanceFine)
            {
                advanceFine = false;
            }
            else
            {
                float nextFineDistance = fineVertices[fineIndex + 1].PlanePosition.DistanceSquaredTo(coarseVertices[coarseIndex].PlanePosition);
                float nextCoarseDistance = fineVertices[fineIndex].PlanePosition.DistanceSquaredTo(coarseVertices[coarseIndex + 1].PlanePosition);
                advanceFine = nextFineDistance <= nextCoarseDistance;
            }

            if (advanceFine)
            {
                AddTransitionTriangle(
                    vertices,
                    normals,
                    uvs,
                    colors,
                    blockOrigin,
                    fineVertices[fineIndex],
                    fineVertices[fineIndex + 1],
                    coarseVertices[coarseIndex]);
                fineIndex++;
            }
            else
            {
                AddTransitionTriangle(
                    vertices,
                    normals,
                    uvs,
                    colors,
                    blockOrigin,
                    fineVertices[fineIndex],
                    coarseVertices[coarseIndex + 1],
                    coarseVertices[coarseIndex]);
                coarseIndex++;
            }
        }

        return vertices.Count > triangleCountBefore;
    }

    private static bool TryAlignClosedContours(
        BoundaryContour fineContour,
        BoundaryContour coarseContour,
        out ContourVertex[] fineVertices,
        out ContourVertex[] coarseVertices)
    {
        fineVertices = Array.Empty<ContourVertex>();
        coarseVertices = Array.Empty<ContourVertex>();

        if (fineContour.Vertices.Length < 3 || coarseContour.Vertices.Length < 3)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        int fineStartIndex = 0;
        int coarseStartIndex = 0;
        for (int fineIndex = 0; fineIndex < fineContour.Vertices.Length; fineIndex++)
        {
            Vector2 finePoint = fineContour.Vertices[fineIndex].PlanePosition;
            for (int coarseIndex = 0; coarseIndex < coarseContour.Vertices.Length; coarseIndex++)
            {
                float distance = finePoint.DistanceSquaredTo(coarseContour.Vertices[coarseIndex].PlanePosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    fineStartIndex = fineIndex;
                    coarseStartIndex = coarseIndex;
                }
            }
        }

        ContourVertex[] rotatedFine = RotateClosedVertices(fineContour.Vertices, fineStartIndex);
        ContourVertex[] rotatedCoarse = RotateClosedVertices(coarseContour.Vertices, coarseStartIndex);
        ContourVertex[] reversedCoarseSource = ReverseClosedVertices(coarseContour.Vertices);
        int reversedCoarseStart = FindClosestVertexIndex(reversedCoarseSource, coarseContour.Vertices[coarseStartIndex].PlanePosition);
        ContourVertex[] rotatedReversedCoarse = RotateClosedVertices(reversedCoarseSource, reversedCoarseStart);

        float forwardScore = ComputeClosedAlignmentScore(rotatedFine, rotatedCoarse);
        float reversedScore = ComputeClosedAlignmentScore(rotatedFine, rotatedReversedCoarse);
        ContourVertex[] chosenCoarse = reversedScore < forwardScore
            ? rotatedReversedCoarse
            : rotatedCoarse;

        fineVertices = new ContourVertex[rotatedFine.Length + 1];
        coarseVertices = new ContourVertex[chosenCoarse.Length + 1];
        Array.Copy(rotatedFine, fineVertices, rotatedFine.Length);
        Array.Copy(chosenCoarse, coarseVertices, chosenCoarse.Length);
        fineVertices[fineVertices.Length - 1] = rotatedFine[0];
        coarseVertices[coarseVertices.Length - 1] = chosenCoarse[0];
        return true;
    }

    private static float ComputeClosedAlignmentScore(ContourVertex[] fineVertices, ContourVertex[] coarseVertices)
    {
        int sampleCount = Mathf.Max(fineVertices.Length, coarseVertices.Length);
        float score = 0.0f;
        for (int sample = 0; sample < sampleCount; sample++)
        {
            int fineIndex = RemapSampleIndex(sample, sampleCount, fineVertices.Length);
            int coarseIndex = RemapSampleIndex(sample, sampleCount, coarseVertices.Length);
            score += fineVertices[fineIndex].PlanePosition.DistanceTo(coarseVertices[coarseIndex].PlanePosition);
        }

        return score;
    }

    private static int RemapSampleIndex(int sample, int sampleCount, int destinationCount)
    {
        if (destinationCount <= 1 || sampleCount <= 1)
        {
            return 0;
        }

        float t = sample / (float)(sampleCount - 1);
        return Mathf.Clamp(Mathf.RoundToInt(t * (destinationCount - 1)), 0, destinationCount - 1);
    }

    private static int FindClosestVertexIndex(ContourVertex[] vertices, Vector2 target)
    {
        int bestIndex = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < vertices.Length; i++)
        {
            float distance = vertices[i].PlanePosition.DistanceSquaredTo(target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static ContourVertex[] RotateClosedVertices(ContourVertex[] source, int startIndex)
    {
        ContourVertex[] rotated = new ContourVertex[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            rotated[i] = source[(startIndex + i) % source.Length];
        }

        return rotated;
    }

    private static ContourVertex[] ReverseClosedVertices(ContourVertex[] source)
    {
        ContourVertex[] reversed = (ContourVertex[])source.Clone();
        Array.Reverse(reversed);
        return reversed;
    }

    private static void AddTransitionTriangle(
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<Color> colors,
        Vector3 blockOrigin,
        ContourVertex a,
        ContourVertex b,
        ContourVertex c)
    {
        Vector3 localA = a.Vertex.Position - blockOrigin;
        Vector3 localB = b.Vertex.Position - blockOrigin;
        Vector3 localC = c.Vertex.Position - blockOrigin;
        if (ComputeTriangleAreaSquared(localA, localB, localC) <= MinTriangleAreaSquared)
        {
            return;
        }

        AddTriangle(vertices, normals, uvs, colors, localA, localB, localC, a.Vertex.Normal, b.Vertex.Normal, c.Vertex.Normal, a.Vertex.Color, b.Vertex.Color, c.Vertex.Color);
        AddTriangle(vertices, normals, uvs, colors, localC, localB, localA, c.Vertex.Normal, b.Vertex.Normal, a.Vertex.Normal, c.Vertex.Color, b.Vertex.Color, a.Vertex.Color);
    }

    private static float ComputeTriangleAreaSquared(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 cross = (b - a).Cross(c - a);
        return cross.LengthSquared() * 0.25f;
    }

    private static bool TryClipSegmentToRect(
        ContourVertex start,
        ContourVertex end,
        Rect2 clipRect,
        out ContourVertex clippedStart,
        out ContourVertex clippedEnd)
    {
        Vector2 delta = end.PlanePosition - start.PlanePosition;
        float t0 = 0.0f;
        float t1 = 1.0f;
        Vector2 min = clipRect.Position;
        Vector2 max = clipRect.Position + clipRect.Size;

        if (!ClipTest(-delta.X, start.PlanePosition.X - min.X, ref t0, ref t1) ||
            !ClipTest(delta.X, max.X - start.PlanePosition.X, ref t0, ref t1) ||
            !ClipTest(-delta.Y, start.PlanePosition.Y - min.Y, ref t0, ref t1) ||
            !ClipTest(delta.Y, max.Y - start.PlanePosition.Y, ref t0, ref t1))
        {
            clippedStart = default;
            clippedEnd = default;
            return false;
        }

        if (t1 <= t0)
        {
            clippedStart = default;
            clippedEnd = default;
            return false;
        }

        clippedStart = LerpContourVertex(start, end, t0);
        clippedEnd = LerpContourVertex(start, end, t1);
        return clippedStart.PlanePosition.DistanceSquaredTo(clippedEnd.PlanePosition) > 0.0000001f;
    }

    private static bool ClipTest(float p, float q, ref float t0, ref float t1)
    {
        if (Mathf.IsZeroApprox(p))
        {
            return q >= 0.0f;
        }

        float r = q / p;
        if (p < 0.0f)
        {
            if (r > t1)
            {
                return false;
            }

            if (r > t0)
            {
                t0 = r;
            }
        }
        else
        {
            if (r < t0)
            {
                return false;
            }

            if (r < t1)
            {
                t1 = r;
            }
        }

        return true;
    }

    private static ContourVertex LerpContourVertex(ContourVertex start, ContourVertex end, float t)
    {
        Vector3 blendedNormal = start.Vertex.Normal.Lerp(end.Vertex.Normal, t);
        if (blendedNormal.LengthSquared() <= MinNormalLengthSquared)
        {
            blendedNormal = start.Vertex.Normal.LengthSquared() > MinNormalLengthSquared
                ? start.Vertex.Normal
                : Vector3.Up;
        }

        return new ContourVertex(
            new SeamVertex(
                start.Vertex.Position.Lerp(end.Vertex.Position, t),
                blendedNormal.Normalized(),
                start.Vertex.Color.Lerp(end.Vertex.Color, t)),
            start.PlanePosition.Lerp(end.PlanePosition, t));
    }

    private static bool IsUsableContour(BoundaryContour contour, float minContourLength)
    {
        int minimumVertexCount = contour.Closed ? 3 : 2;
        return contour.Vertices.Length >= minimumVertexCount &&
               contour.Length >= minContourLength;
    }

    private static BoundaryContour WalkContour(
        QuantizedPoint2 startKey,
        int startSegmentIndex,
        List<BoundarySegment> segments,
        Dictionary<QuantizedPoint2, List<int>> adjacency,
        bool[] visited,
        float quantizeStep)
    {
        List<ContourVertex> vertices = new();
        QuantizedPoint2 currentKey = startKey;
        int currentSegmentIndex = startSegmentIndex;
        bool closed = false;

        while (currentSegmentIndex >= 0)
        {
            BoundarySegment oriented = OrientSegment(segments[currentSegmentIndex], currentKey);
            visited[currentSegmentIndex] = true;

            if (vertices.Count == 0)
            {
                vertices.Add(oriented.Start);
            }
            else if (!AreEquivalent(vertices[vertices.Count - 1].PlanePosition, oriented.Start.PlanePosition, quantizeStep))
            {
                vertices.Add(oriented.Start);
            }

            vertices.Add(oriented.End);
            QuantizedPoint2 nextKey = oriented.EndKey;
            if (nextKey.Equals(startKey))
            {
                closed = vertices.Count > 2;
                break;
            }

            Vector2 nextDirection = oriented.End.PlanePosition - oriented.Start.PlanePosition;
            currentSegmentIndex = ChooseNextSegment(nextKey, adjacency, segments, visited, nextDirection);
            currentKey = nextKey;
        }

        if (closed &&
            vertices.Count > 1 &&
            AreEquivalent(vertices[0].PlanePosition, vertices[vertices.Count - 1].PlanePosition, quantizeStep))
        {
            vertices.RemoveAt(vertices.Count - 1);
        }

        return CreateContour(vertices, closed);
    }

    private static int ChooseNextSegment(
        QuantizedPoint2 currentKey,
        Dictionary<QuantizedPoint2, List<int>> adjacency,
        List<BoundarySegment> segments,
        bool[] visited,
        Vector2 previousDirection)
    {
        if (!adjacency.TryGetValue(currentKey, out List<int> candidates))
        {
            return -1;
        }

        int bestIndex = -1;
        float bestScore = float.NegativeInfinity;
        bool hasPreviousDirection = previousDirection.LengthSquared() > 0.0000001f;
        Vector2 normalizedPrevious = hasPreviousDirection
            ? previousDirection.Normalized()
            : Vector2.Zero;

        foreach (int candidateIndex in candidates)
        {
            if (visited[candidateIndex])
            {
                continue;
            }

            BoundarySegment segment = segments[candidateIndex];
            Vector2 direction = segment.StartKey.Equals(currentKey)
                ? segment.End.PlanePosition - segment.Start.PlanePosition
                : segment.Start.PlanePosition - segment.End.PlanePosition;
            float score = hasPreviousDirection && direction.LengthSquared() > 0.0000001f
                ? normalizedPrevious.Dot(direction.Normalized())
                : 0.0f;
            if (bestIndex < 0 || score > bestScore)
            {
                bestIndex = candidateIndex;
                bestScore = score;
            }
        }

        return bestIndex;
    }

    private static bool AreEquivalent(Vector2 a, Vector2 b, float quantizeStep)
    {
        return a.DistanceSquaredTo(b) <= quantizeStep * quantizeStep;
    }

    private static BoundaryContour CreateContour(List<ContourVertex> vertices, bool closed)
    {
        if (vertices.Count == 0)
        {
            return new BoundaryContour(Array.Empty<ContourVertex>(), closed, new Rect2(), Vector2.Zero, 0.0f);
        }

        Vector2 min = vertices[0].PlanePosition;
        Vector2 max = vertices[0].PlanePosition;
        Vector2 sum = Vector2.Zero;
        float length = 0.0f;
        for (int i = 0; i < vertices.Count; i++)
        {
            Vector2 point = vertices[i].PlanePosition;
            min = new Vector2(Mathf.Min(min.X, point.X), Mathf.Min(min.Y, point.Y));
            max = new Vector2(Mathf.Max(max.X, point.X), Mathf.Max(max.Y, point.Y));
            sum += point;
            if (i > 0)
            {
                length += vertices[i - 1].PlanePosition.DistanceTo(point);
            }
        }

        if (closed && vertices.Count > 1)
        {
            length += vertices[vertices.Count - 1].PlanePosition.DistanceTo(vertices[0].PlanePosition);
        }

        return new BoundaryContour(
            vertices.ToArray(),
            closed,
            new Rect2(min, max - min),
            sum / vertices.Count,
            length);
    }

    private static BoundarySegment OrientSegment(BoundarySegment segment, QuantizedPoint2 startKey)
    {
        return segment.StartKey.Equals(startKey)
            ? segment
            : segment.Reversed();
    }

    private static void AddAdjacency(
        Dictionary<QuantizedPoint2, List<int>> adjacency,
        QuantizedPoint2 point,
        int segmentIndex)
    {
        if (!adjacency.TryGetValue(point, out List<int> segmentIndices))
        {
            segmentIndices = new List<int>();
            adjacency[point] = segmentIndices;
        }

        segmentIndices.Add(segmentIndex);
    }

    private static Rect2 BuildFaceRect(Vector3 blockOrigin, float span, TerrainSeamFace face)
    {
        return face switch
        {
            TerrainSeamFace.NegativeX or TerrainSeamFace.PositiveX => new Rect2(new Vector2(blockOrigin.Y, blockOrigin.Z), new Vector2(span, span)),
            TerrainSeamFace.NegativeY or TerrainSeamFace.PositiveY => new Rect2(new Vector2(blockOrigin.X, blockOrigin.Z), new Vector2(span, span)),
            TerrainSeamFace.NegativeZ or TerrainSeamFace.PositiveZ => new Rect2(new Vector2(blockOrigin.X, blockOrigin.Y), new Vector2(span, span)),
            _ => new Rect2()
        };
    }

    private static ContourVertex CreateContourVertex(Vector3 worldPosition, Vector3 normal, Color color, TerrainSeamFace face)
    {
        return new ContourVertex(
            new SeamVertex(
                worldPosition,
                normal.LengthSquared() > MinNormalLengthSquared ? normal.Normalized() : Vector3.Up,
                color),
            ProjectToFacePlane(worldPosition, face));
    }

    private static BoundarySegment CreateBoundarySegment(ContourVertex start, ContourVertex end, float quantizeStep)
    {
        return new BoundarySegment(
            start,
            end,
            Quantize(start.PlanePosition, quantizeStep),
            Quantize(end.PlanePosition, quantizeStep));
    }

    private static Vector2 ProjectToFacePlane(Vector3 worldPosition, TerrainSeamFace face)
    {
        return face switch
        {
            TerrainSeamFace.NegativeX or TerrainSeamFace.PositiveX => new Vector2(worldPosition.Y, worldPosition.Z),
            TerrainSeamFace.NegativeY or TerrainSeamFace.PositiveY => new Vector2(worldPosition.X, worldPosition.Z),
            TerrainSeamFace.NegativeZ or TerrainSeamFace.PositiveZ => new Vector2(worldPosition.X, worldPosition.Y),
            _ => Vector2.Zero
        };
    }

    private static TerrainSeamFace GetOppositeFace(TerrainSeamFace face)
    {
        return face switch
        {
            TerrainSeamFace.NegativeX => TerrainSeamFace.PositiveX,
            TerrainSeamFace.PositiveX => TerrainSeamFace.NegativeX,
            TerrainSeamFace.NegativeY => TerrainSeamFace.PositiveY,
            TerrainSeamFace.PositiveY => TerrainSeamFace.NegativeY,
            TerrainSeamFace.NegativeZ => TerrainSeamFace.PositiveZ,
            TerrainSeamFace.PositiveZ => TerrainSeamFace.NegativeZ,
            _ => TerrainSeamFace.None
        };
    }

    private static string BuildStrategyLabel(TerrainSeamFace transitionFaces, TerrainSeamFace skirtFaces)
    {
        if (transitionFaces != TerrainSeamFace.None && skirtFaces != TerrainSeamFace.None)
        {
            return $"{TransitionStrategyName}+fallback";
        }

        if (transitionFaces != TerrainSeamFace.None)
        {
            return TransitionStrategyName;
        }

        if (skirtFaces != TerrainSeamFace.None)
        {
            return MixedLodStrategyName;
        }

        return "none";
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

    private static QuantizedPoint2 Quantize(Vector2 value, float step)
    {
        return new QuantizedPoint2(
            Mathf.RoundToInt(value.X / step),
            Mathf.RoundToInt(value.Y / step));
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
        return normal.LengthSquared() > MinNormalLengthSquared
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
            - (topNormalA * (surfaceInset * 0.16f));
        Vector3 topB = edge.End
            - (faceDirection * faceInset)
            - (topNormalB * (surfaceInset * 0.16f));

        Vector3 midA = edge.Start
            - (faceDirection * (faceInset * 0.04f))
            - (midNormalA * (surfaceInset * 0.48f));
        Vector3 midB = edge.End
            - (faceDirection * (faceInset * 0.04f))
            - (midNormalB * (surfaceInset * 0.48f));

        Vector3 bottomA = edge.Start
            - (faceDirection * (faceInset * 0.20f))
            - (bottomNormalA * ((surfaceInset * 0.92f) + skirtDepth));
        Vector3 bottomB = edge.End
            - (faceDirection * (faceInset * 0.20f))
            - (bottomNormalB * ((surfaceInset * 0.92f) + skirtDepth));

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
        AddTriangle(vertices, normals, uvs, colors, a, b, c, normalA, normalB, normalC, colorA, colorB, colorC);
        AddTriangle(vertices, normals, uvs, colors, a, c, d, normalA, normalC, normalD, colorA, colorC, colorD);
        AddTriangle(vertices, normals, uvs, colors, c, b, a, normalC, normalB, normalA, colorC, colorB, colorA);
        AddTriangle(vertices, normals, uvs, colors, d, c, a, normalD, normalC, normalA, colorD, colorC, colorA);
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
        Vector3 safeNormal = normal.LengthSquared() > MinNormalLengthSquared
            ? normal.Normalized()
            : Vector3.Up;
        vertices.Add(position);
        normals.Add(safeNormal);
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
        if (boundaryNormal.LengthSquared() <= MinNormalLengthSquared)
        {
            return -faceDirection;
        }

        Vector3 normalized = boundaryNormal.Normalized();
        float faceComponent = normalized.Dot(faceDirection);
        Vector3 surfaceBiased = normalized - (faceDirection * faceComponent * Mathf.Clamp(faceSuppression, 0.0f, 1.0f));
        if (surfaceBiased.LengthSquared() <= MinNormalLengthSquared)
        {
            surfaceBiased = normalized - (faceDirection * faceComponent);
        }

        if (surfaceBiased.LengthSquared() <= MinNormalLengthSquared)
        {
            Vector3 fallback = faceDirection.Cross(Vector3.Up);
            if (fallback.LengthSquared() <= MinNormalLengthSquared)
            {
                fallback = faceDirection.Cross(Vector3.Right);
            }

            surfaceBiased = fallback.LengthSquared() > MinNormalLengthSquared
                ? fallback.Normalized()
                : -faceDirection;
        }

        float alignment = Mathf.SmoothStep(0.15f, 0.95f, Mathf.Abs(faceComponent));
        Vector3 blended = normalized.Lerp(surfaceBiased.Normalized(), Mathf.Lerp(faceSuppression * 0.35f, faceSuppression, alignment));
        if (blended.LengthSquared() <= MinNormalLengthSquared)
        {
            return surfaceBiased.Normalized();
        }

        Vector3 resolved = blended.Normalized();
        return resolved.Dot(normalized) < 0.0f
            ? -resolved
            : resolved;
    }

    private sealed class EmptyTransitionNeighborMap : Dictionary<TerrainSeamFace, TerrainSeamNeighborData>
    {
        public static EmptyTransitionNeighborMap Instance { get; } = new();
    }

    private readonly record struct ContourMatch(BoundaryContour Fine, BoundaryContour Coarse);

    private readonly record struct SeamVertex(Vector3 Position, Vector3 Normal, Color Color);

    private readonly record struct ContourVertex(SeamVertex Vertex, Vector2 PlanePosition);

    private readonly record struct BoundaryContour(
        ContourVertex[] Vertices,
        bool Closed,
        Rect2 Bounds,
        Vector2 Center,
        float Length);

    private readonly record struct BoundarySegment(
        ContourVertex Start,
        ContourVertex End,
        QuantizedPoint2 StartKey,
        QuantizedPoint2 EndKey)
    {
        public BoundarySegment Reversed()
        {
            return new BoundarySegment(End, Start, EndKey, StartKey);
        }
    }

    private readonly record struct QuantizedPoint2(int X, int Y);

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
