using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainGrassSystem : Node3D
{
    private const string GrassPatchNodeName = "GrassPatch";
    private const string GrassShaderPath = "res://shaders/terrain/TerrainGrass.gdshader";
    // The instanced grass path expects an alpha-cutout blade atlas, so keep it bound to the simple clump sheet.
    private const string GrassTexturePath = "res://assets/terrain/textures/grass/grass_clump_atlas.png";
    private const float Tau = Mathf.Pi * 2.0f;
    private const float SelectiveGrassSlopeMax = 0.18f;
    private const float SelectiveGrassMinHeightAboveWater = 1.5f;
    private const float SelectiveGrassDensityScale = 0.65f;
    private const float GrassMaterialSampleInset = 0.18f;
    private const float TerrainSurfaceQueryCellSizeMin = 0.5f;
    private const string GrassDebugLogPrefix = "[TerrainGrass]";
    private const string GrassDebugLogRelativePath = "user://profiling/terrain_grass_latest.log";

    [ExportGroup("Nodes")]
    [Export] public NodePath TerrainLodManagerPath = new("../TerrainLodManager");

    [ExportGroup("Distribution")]
    [Export(PropertyHint.Range, "0.25,16,0.25")] public float DensityPerSquareMeter = 2.25f;
    [Export(PropertyHint.Range, "0,75,1")] public float MaxSlopeDegrees = 38.0f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float MinHeightAboveWater = 0.35f;
    [Export(PropertyHint.Range, "0,48,0.25")] public float HighlandFadeStart = 16.0f;
    [Export(PropertyHint.Range, "1,96,0.25")] public float HighlandFadeEnd = 34.0f;
    [Export(PropertyHint.Range, "0,256,0.5")] public float DensityFalloffStart = 30.0f;
    [Export(PropertyHint.Range, "0,2,1")] public int MaxGrassLod = 1;
    [Export(PropertyHint.Range, "128,12000,64")] public int MaxInstancesPerPatch = 1600;
    [Export(PropertyHint.Range, "0.00,0.25,0.005")] public float PlacementLift = 0.02f;

    [ExportGroup("Clumping")]
    [Export(PropertyHint.Range, "0.001,0.2,0.001")] public float ClumpNoiseFrequency = 0.028f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpThresholdMin = 0.46f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpThresholdMax = 0.72f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpDensityFloor = 0.14f;
    [Export(PropertyHint.Range, "0.001,0.35,0.001")] public float ClumpDetailFrequency = 0.110f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpDetailStrength = 0.22f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpDetailThresholdMin = 0.40f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClumpDetailThresholdMax = 0.68f;
    [Export(PropertyHint.Range, "1,2,0.05")] public float ShoreDensityBoost = 1.05f;
    [Export(PropertyHint.Range, "0.5,16,0.25")] public float ShoreDensityBoostRange = 3.0f;

    [ExportGroup("Blade Shape")]
    [Export(PropertyHint.Range, "0.2,3,0.05")] public float BladeHeight = 0.80f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float BladeWidth = 0.28f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float ScaleMin = 0.52f;
    [Export(PropertyHint.Range, "0.5,3,0.05")] public float ScaleMax = 0.96f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float HeightVariationMin = 0.60f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float HeightVariationMax = 1.40f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float WidthVariationMin = 0.70f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float WidthVariationMax = 1.30f;
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float LeanVariationRadians = 0.20f;
    [Export(PropertyHint.Range, "1,8,1")] public int BladeAtlasColumns = 4;
    [Export(PropertyHint.Range, "1,8,1")] public int BladeAtlasRows = 1;
    [Export(PropertyHint.Range, "1,64,1")] public int BladeAtlasFrameCount = 4;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float WidthScaleJitter = 0.08f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float HeightScaleJitter = 0.08f;
    [Export(PropertyHint.Range, "0,20,0.25")] public float MaxTiltDegrees = 4.0f;
    [Export] public Color BladeColorMin = new(0.35f, 0.45f, 0.25f, 1.0f);
    [Export] public Color BladeColorMax = new(0.25f, 0.50f, 0.30f, 1.0f);

    [ExportGroup("Wind")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float WindStrength = 0.16f;
    [Export(PropertyHint.Range, "0,8,0.05")] public float WindSpeed = 1.7f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float WindFrequency = 0.11f;
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float WindTopBias = 1.65f;

    [ExportGroup("Rendering")]
    [Export(PropertyHint.Range, "8,256,1")] public float RenderDistance = 72.0f;
    [Export(PropertyHint.Range, "0,64,1")] public float FadeDistance = 10.0f;
    [Export(PropertyHint.Range, "1,8,1")] public int PatchesBuiltPerFrame = 1;
    [Export(PropertyHint.Range, "0.05,1,0.05")] public float SyncIntervalSeconds = 0.2f;
    [Export(PropertyHint.Range, "0.5,1,0.01")] public float GrassRoughness = 0.96f;
    [Export(PropertyHint.Range, "0.05,0.5,0.01")] public float GrassAlphaScissorThreshold = 0.18f;
    [Export] public bool CastGrassShadows;

    [ExportGroup("Debug")]
    [Export] public bool EnableDebugLogging = false;
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float DebugLogIntervalSeconds = 1.5f;
    [Export] public bool DebugForceFallbackMaterial;
    [Export] public bool DebugBypassPlacementFilters;
    [Export] public Color DebugFallbackColor = new(0.24f, 0.95f, 0.32f, 1.0f);

    private readonly Dictionary<ulong, GrassPatchEntry> _patches = new();
    private readonly Dictionary<ulong, GrassPatchSkipEntry> _skippedPatchBuilds = new();
    private readonly Queue<TerrainRenderer> _pendingBuilds = new();
    private readonly HashSet<ulong> _pendingBuildIds = new();
    private readonly object _debugLogLock = new();

    private TerrainWorld _terrainWorld = null!;
    private TerrainLodManager _lodManager = null!;
    private StreamWriter _debugLogWriter = null!;
    private ArrayMesh _grassMesh = null!;
    private ShaderMaterial _grassShaderMaterial = null!;
    private StandardMaterial3D _fallbackMaterial = null!;
    private Material _activeGrassMaterial = null!;
    private Shader _grassShader = null!;
    private Texture2D _grassTexture = null!;
    private FastNoiseLite _clumpNoise = null!;
    private FastNoiseLite _clumpDetailNoise = null!;
    private VoxelFieldGenerator _terrainPlacementSampler = null!;
    private long _settingsSignature;
    private long _terrainPlacementSamplerSignature;
    private float _syncCountdown;
    private float _debugLogCountdown;
    private bool _warnedMissingShader;
    private bool _warnedMissingTexture;
    private bool _warnedDebugLogFailure;
    private bool _usingFallbackMaterial;
    private int _lastScannedRendererCount;
    private int _lastEligibleRendererCount;
    private int _lastRejectedRendererNoVisualsCount;
    private int _lastRejectedRendererLodCount;
    private int _lastRejectedRendererDistanceCount;
    private int _lastActivePatchCount;
    private int _lastActiveInstanceCount;
    private int _lastBuiltInstanceCount;
    private int _lastTriangleCount;
    private int _lastRejectedSlopeCount;
    private int _lastRejectedWaterCount;
    private int _lastRejectedHighlandCount;
    private int _lastRejectedMaterialCount;
    private int _lastRejectedBiomeCount;
    private int _lastRejectedDensityCount;
    private string _lastBuildOutcome = "waiting_for_renderers";

    private const float ThinTallTypeHeight = 1.18f;
    private const float ThinTallTypeWidth = 0.84f;
    private const float ThinTallTypeLean = 1.00f;
    private const float ThinTallMoistureBias = 0.04f;
    private const float ShortDenseTypeHeight = 0.82f;
    private const float ShortDenseTypeWidth = 1.18f;
    private const float ShortDenseTypeLean = 0.72f;
    private const float ShortDenseMoistureBias = -0.02f;
    private const float CurvedTypeHeight = 0.98f;
    private const float CurvedTypeWidth = 0.96f;
    private const float CurvedTypeLean = 1.55f;
    private const float CurvedMoistureBias = 0.01f;

    public override void _Ready()
    {
        _terrainWorld = GetParent() as TerrainWorld;
        _lodManager = ResolveLodManager();
        EnsureTerrainPlacementSampler();
        EnsureResources();
        UpdateClumpNoiseGenerators();
        UpdateMaterialParameters();
        _settingsSignature = BuildSettingsSignature();
        _syncCountdown = 0.0f;
        _debugLogCountdown = Mathf.Max(0.2f, DebugLogIntervalSeconds);
    }

    public override void _Process(double delta)
    {
        _terrainWorld ??= GetParent() as TerrainWorld;
        _lodManager ??= ResolveLodManager();
        EnsureTerrainPlacementSampler();
        EnsureResources();
        UpdateClumpNoiseGenerators();
        UpdateMaterialParameters();

        long currentSignature = BuildSettingsSignature();
        if (currentSignature != _settingsSignature)
        {
            _settingsSignature = currentSignature;
            RebuildAllPatches();
        }

        Vector3 viewerPosition = ResolveViewerPosition();
        _syncCountdown -= (float)delta;
        if (_syncCountdown <= 0.0f)
        {
            SynchronizeRendererPatches(viewerPosition);
            _syncCountdown = Mathf.Max(0.05f, SyncIntervalSeconds);
        }

        BuildPendingPatches(viewerPosition);
        MaybeEmitDebugLog();
    }

    public override void _ExitTree()
    {
        CloseDebugLogWriter();
        ClearAllPatches();
        _skippedPatchBuilds.Clear();
        _pendingBuilds.Clear();
        _pendingBuildIds.Clear();
    }

    private void SynchronizeRendererPatches(Vector3 viewerPosition)
    {
        if (_lodManager == null || !IsInstanceValid(_lodManager))
        {
            return;
        }

        _lastScannedRendererCount = 0;
        _lastEligibleRendererCount = 0;
        _lastRejectedRendererNoVisualsCount = 0;
        _lastRejectedRendererLodCount = 0;
        _lastRejectedRendererDistanceCount = 0;
        HashSet<ulong> seenRendererIds = new();
        foreach (Node child in _lodManager.GetChildren())
        {
            if (child is not TerrainRenderer renderer)
            {
                continue;
            }

            _lastScannedRendererCount++;
            ulong rendererId = renderer.GetInstanceId();
            seenRendererIds.Add(rendererId);

            GrassTrackingState trackingState = EvaluateRendererTracking(renderer, viewerPosition);
            if (trackingState == GrassTrackingState.Eligible)
            {
                _lastEligibleRendererCount++;
                if (!_patches.ContainsKey(rendererId) &&
                    !_pendingBuildIds.Contains(rendererId) &&
                    !ShouldSkipPatchBuild(renderer))
                {
                    _pendingBuilds.Enqueue(renderer);
                    _pendingBuildIds.Add(rendererId);
                }

                continue;
            }

            AccumulateRendererRejection(trackingState);
            RemovePatch(rendererId);
        }

        List<ulong> stalePatchIds = new();
        foreach (KeyValuePair<ulong, GrassPatchEntry> pair in _patches)
        {
            if (!seenRendererIds.Contains(pair.Key) ||
                EvaluateRendererTracking(pair.Value.Renderer, viewerPosition) != GrassTrackingState.Eligible)
            {
                stalePatchIds.Add(pair.Key);
            }
        }

        foreach (ulong patchId in stalePatchIds)
        {
            RemovePatch(patchId);
        }

        List<ulong> staleSkippedBuildIds = new();
        foreach (ulong rendererId in _skippedPatchBuilds.Keys)
        {
            if (!seenRendererIds.Contains(rendererId))
            {
                staleSkippedBuildIds.Add(rendererId);
            }
        }

        foreach (ulong rendererId in staleSkippedBuildIds)
        {
            _skippedPatchBuilds.Remove(rendererId);
        }

        RefreshActivePatchStats();
        if (_lastEligibleRendererCount == 0 && _lastScannedRendererCount > 0)
        {
            _lastBuildOutcome =
                $"no eligible renderers nv/lod/dist {_lastRejectedRendererNoVisualsCount}/{_lastRejectedRendererLodCount}/{_lastRejectedRendererDistanceCount}";
        }
    }

    private void BuildPendingPatches(Vector3 viewerPosition)
    {
        int buildBudget = Mathf.Max(1, PatchesBuiltPerFrame);
        int builtThisFrame = 0;
        while (builtThisFrame < buildBudget && _pendingBuilds.Count > 0)
        {
            TerrainRenderer renderer = _pendingBuilds.Dequeue();
            if (renderer == null || !IsInstanceValid(renderer))
            {
                continue;
            }

            ulong rendererId = renderer.GetInstanceId();
            _pendingBuildIds.Remove(rendererId);
            if (_patches.ContainsKey(rendererId) || EvaluateRendererTracking(renderer, viewerPosition) != GrassTrackingState.Eligible)
            {
                continue;
            }

            MultiMeshInstance3D patch = BuildGrassPatch(renderer, viewerPosition, out bool cacheEmptyBuild);
            if (patch == null)
            {
                if (cacheEmptyBuild)
                {
                    RememberSkippedPatchBuild(renderer);
                }

                continue;
            }

            _skippedPatchBuilds.Remove(rendererId);
            renderer.AddChild(patch);
            _patches[rendererId] = new GrassPatchEntry(renderer, patch);
            _lastBuildOutcome = $"{renderer.BlockId} built {patch.Multimesh?.InstanceCount ?? 0} inst  material {(_usingFallbackMaterial ? "fallback" : "shader")}";
            builtThisFrame++;
        }

        RefreshActivePatchStats();
    }

    private MultiMeshInstance3D BuildGrassPatch(
        TerrainRenderer renderer,
        Vector3 viewerPosition,
        out bool cacheEmptyBuild)
    {
        cacheEmptyBuild = false;
        Vector3[] vertices = renderer.Vertices;
        if (vertices == null || vertices.Length < 3)
        {
            _lastBuildOutcome = $"{renderer.BlockId} skipped no geometry";
            return null;
        }

        float distanceDensityFactor = ComputeDistanceDensityFactor(renderer.GlobalTransform.Origin.DistanceTo(viewerPosition));
        if (distanceDensityFactor <= 0.0f)
        {
            _lastBuildOutcome = $"{renderer.BlockId} skipped distance density 0";
            return null;
        }

        Vector3[] normals = renderer.Normals;
        Color[] terrainColors = renderer.BaseColors;
        float[] biomeWeights = renderer.BiomeWeights;
        float slopeLimitDot = Mathf.Cos(Mathf.DegToRad(Mathf.Clamp(MaxSlopeDegrees, 0.0f, 89.0f)));
        float maxPlacementSlope = Mathf.Min(SelectiveGrassSlopeMax, 1.0f - slopeLimitDot);
        float highlandStart = Mathf.Max(0.0f, HighlandFadeStart);
        float highlandEnd = Mathf.Max(highlandStart + 0.01f, HighlandFadeEnd);
        float waterLevel = _terrainWorld?.WaterLevel ?? -2.6f;
        float minHeightAboveWater = Mathf.Max(MinHeightAboveWater, SelectiveGrassMinHeightAboveWater);
        VoxelFieldGenerator placementSampler = _terrainPlacementSampler;
        float terrainQueryCellSize = ResolveTerrainSurfaceQueryCellSize(renderer.BlockId.Lod);

        RandomNumberGenerator random = new();
        random.Seed = ComputeRendererSeed(renderer.BlockId);

        List<Transform3D> transforms = new();
        List<Color> colors = new();
        List<Color> customData = new();
        Dictionary<TerrainSurfaceQueryKey, TerrainSurfaceColumnSample> terrainColumnCache = new();
        Transform3D rendererTransform = renderer.GlobalTransform;
        int triangleCount = vertices.Length / 3;
        int rejectedSlopeCount = 0;
        int rejectedWaterCount = 0;
        int rejectedHighlandCount = 0;
        int rejectedMaterialCount = 0;
        int rejectedBiomeCount = 0;
        int rejectedDensityCount = 0;
        int eligibleTriangleCount = 0;

        for (int triangle = 0; triangle <= vertices.Length - 3; triangle += 3)
        {
            Vector3 a = vertices[triangle];
            Vector3 b = vertices[triangle + 1];
            Vector3 c = vertices[triangle + 2];

            Vector3 faceNormal = (b - a).Cross(c - a);
            float twiceArea = faceNormal.Length();
            if (twiceArea <= 0.0001f)
            {
                continue;
            }

            faceNormal /= twiceArea;
            Vector3 surfaceNormal = faceNormal;
            if (normals != null && normals.Length >= triangle + 3)
            {
                Vector3 blendedNormal = normals[triangle] + normals[triangle + 1] + normals[triangle + 2];
                if (blendedNormal.LengthSquared() > 0.0001f)
                {
                    surfaceNormal = blendedNormal.Normalized();
                }
            }

            Vector3 centroidLocal = (a + b + c) / 3.0f;
            Vector3 centroidWorld = rendererTransform * centroidLocal;
            float heightAboveWater = centroidWorld.Y - waterLevel;
            if (!DebugBypassPlacementFilters && heightAboveWater < minHeightAboveWater)
            {
                rejectedWaterCount++;
                continue;
            }

            float upDot = Mathf.Clamp(surfaceNormal.Dot(Vector3.Up), -1.0f, 1.0f);
            float sampledSlope = 1.0f - Mathf.Clamp(upDot, 0.0f, 1.0f);
            if (!DebugBypassPlacementFilters && sampledSlope >= maxPlacementSlope)
            {
                rejectedSlopeCount++;
                continue;
            }

            if (!DebugBypassPlacementFilters)
            {
                bool hasTriangleBiome = TryResolveTriangleBiomeSample(biomeWeights, triangle, out TerrainBiomeSample triangleBiome);
                if (hasTriangleBiome && !IsGrassPlacementBiomeAllowed(triangleBiome.DominantBiome))
                {
                    rejectedBiomeCount++;
                    continue;
                }

                TerrainSurfaceColumnSample terrainColumn = ResolveTerrainSurfaceColumnSample(
                    centroidWorld,
                    placementSampler,
                    terrainQueryCellSize,
                    terrainColumnCache);
                TerrainBiomeSample surfaceBiome = hasTriangleBiome ? triangleBiome : terrainColumn.Biome;
                if (!hasTriangleBiome && !IsGrassPlacementBiomeAllowed(surfaceBiome.DominantBiome))
                {
                    rejectedBiomeCount++;
                    continue;
                }

                Vector3 materialSamplePosition = centroidWorld - (surfaceNormal * GrassMaterialSampleInset);
                float surfaceDensity = placementSampler != null
                    ? placementSampler.SampleDensity(materialSamplePosition, terrainColumn.TerrainHeight)
                    : terrainColumn.TerrainHeight - materialSamplePosition.Y;
                VoxelMaterialId surfaceMaterial = placementSampler != null
                    ? placementSampler.SampleMaterial(
                        materialSamplePosition,
                        surfaceDensity,
                        terrainColumn.TerrainHeight,
                        sampledSlope,
                        surfaceBiome)
                    : VoxelMaterialId.Grass;
                if (surfaceMaterial != VoxelMaterialId.Grass)
                {
                    rejectedMaterialCount++;
                    continue;
                }
            }

            float lowlandFactor = DebugBypassPlacementFilters
                ? 1.0f
                : 1.0f - Mathf.SmoothStep(highlandStart, highlandEnd, heightAboveWater);
            if (!DebugBypassPlacementFilters && lowlandFactor <= 0.0f)
            {
                rejectedHighlandCount++;
                continue;
            }

            eligibleTriangleCount++;
            float slopeFactor = DebugBypassPlacementFilters
                ? 1.0f
                : Mathf.SmoothStep(slopeLimitDot, 1.0f, upDot);
            float clumpDensityFactor = DebugBypassPlacementFilters
                ? 1.0f
                : ComputeClumpDensityFactor(centroidWorld, heightAboveWater, lowlandFactor, slopeFactor);
            float shoreProximity = ComputeShoreProximity(heightAboveWater);
            float moisture = ComputeMoistureFactor(lowlandFactor, slopeFactor, shoreProximity);
            Color triangleTerrainColor = ResolveTriangleTerrainColor(terrainColors, triangle);
            float triangleArea = twiceArea * 0.5f;
            float expectedInstances =
                triangleArea *
                DensityPerSquareMeter *
                SelectiveGrassDensityScale *
                lowlandFactor *
                slopeFactor *
                clumpDensityFactor *
                distanceDensityFactor;
            int instanceCount = Mathf.FloorToInt(expectedInstances);
            if (random.Randf() < (expectedInstances - instanceCount))
            {
                instanceCount++;
            }

            if (instanceCount <= 0)
            {
                rejectedDensityCount++;
                continue;
            }

            for (int instance = 0; instance < instanceCount && transforms.Count < MaxInstancesPerPatch; instance++)
            {
                Vector3 position = SampleTriangle(a, b, c, random) + (surfaceNormal * PlacementLift);
                float scale = random.RandfRange(Mathf.Min(ScaleMin, ScaleMax), Mathf.Max(ScaleMin, ScaleMax));
                ResolveGrassType(random, out float typeHeightScale, out float typeWidthScale, out float typeLeanScale, out float moistureBias);
                float widthVariation = random.RandfRange(Mathf.Min(WidthVariationMin, WidthVariationMax), Mathf.Max(WidthVariationMin, WidthVariationMax));
                float heightVariation = random.RandfRange(Mathf.Min(HeightVariationMin, HeightVariationMax), Mathf.Max(HeightVariationMin, HeightVariationMax));
                float widthScale = scale * widthVariation * typeWidthScale * (1.0f + random.RandfRange(-WidthScaleJitter, WidthScaleJitter));
                float heightScale = scale * heightVariation * typeHeightScale * (1.0f + random.RandfRange(-HeightScaleJitter, HeightScaleJitter));
                Vector3 scaleVector = new(
                    BladeWidth * Mathf.Max(0.1f, widthScale),
                    BladeHeight * Mathf.Max(0.1f, heightScale),
                    BladeWidth * Mathf.Max(0.1f, widthScale));

                Basis basis = Basis.Identity;
                basis = basis.Rotated(Vector3.Up, random.RandfRange(0.0f, Tau));
                float maxTiltRadians = Mathf.DegToRad(Mathf.Max(0.0f, MaxTiltDegrees));
                float leanVariation = Mathf.Max(0.0f, LeanVariationRadians) * typeLeanScale;
                if (leanVariation > 0.0001f)
                {
                    basis = basis.Rotated(basis.X.Normalized(), random.RandfRange(-leanVariation, leanVariation));
                    basis = basis.Rotated(basis.Z.Normalized(), random.RandfRange(-leanVariation, leanVariation));
                }
                if (maxTiltRadians > 0.0001f)
                {
                    basis = basis.Rotated(basis.X.Normalized(), random.RandfRange(-maxTiltRadians, maxTiltRadians));
                    basis = basis.Rotated(basis.Z.Normalized(), random.RandfRange(-maxTiltRadians, maxTiltRadians));
                }
                basis = basis.Scaled(scaleVector);

                transforms.Add(new Transform3D(basis, position));
                float instanceMoisture = Mathf.Clamp(moisture + moistureBias + random.RandfRange(-0.10f, 0.10f), 0.0f, 1.0f);
                colors.Add(ResolveGrassColor(triangleTerrainColor, instanceMoisture));
                int atlasFrameCount = ResolveAtlasFrameCount();
                int atlasFrame = atlasFrameCount > 1
                    ? random.RandiRange(0, atlasFrameCount - 1)
                    : 0;
                float atlasFrameOffset = atlasFrameCount > 1
                    ? (atlasFrame + 0.5f) / atlasFrameCount
                    : 0.0f;
                customData.Add(new Color(random.Randf(), random.Randf(), random.Randf(), atlasFrameOffset));
            }

            if (transforms.Count >= MaxInstancesPerPatch)
            {
                break;
            }
        }

        if (transforms.Count == 0)
        {
            _lastTriangleCount = triangleCount;
            _lastRejectedSlopeCount = rejectedSlopeCount;
            _lastRejectedWaterCount = rejectedWaterCount;
            _lastRejectedHighlandCount = rejectedHighlandCount;
            _lastRejectedMaterialCount = rejectedMaterialCount;
            _lastRejectedBiomeCount = rejectedBiomeCount;
            _lastRejectedDensityCount = rejectedDensityCount;
            _lastBuiltInstanceCount = 0;
            _lastBuildOutcome =
                $"{renderer.BlockId} 0 inst  tri {triangleCount}  slope/water/high/material/biome/density {rejectedSlopeCount}/{rejectedWaterCount}/{rejectedHighlandCount}/{rejectedMaterialCount}/{rejectedBiomeCount}/{rejectedDensityCount}";
            cacheEmptyBuild = !DebugBypassPlacementFilters && eligibleTriangleCount == 0;
            return null;
        }

        MultiMesh multiMesh = new()
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = _grassMesh
        };
        multiMesh.InstanceCount = transforms.Count;
        for (int i = 0; i < transforms.Count; i++)
        {
            multiMesh.SetInstanceTransform(i, transforms[i]);
            multiMesh.SetInstanceColor(i, colors[i]);
            multiMesh.SetInstanceCustomData(i, customData[i]);
        }

        MultiMeshInstance3D patch = new()
        {
            Name = GrassPatchNodeName,
            Multimesh = multiMesh,
            MaterialOverride = _activeGrassMaterial,
            CastShadow = CastGrassShadows
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off,
            VisibilityRangeEnd = RenderDistance,
            VisibilityRangeEndMargin = FadeDistance,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            ExtraCullMargin = (BladeHeight * ResolveMaxHeightScale()) +
                (BladeWidth * ResolveMaxWidthScale()) +
                (WindStrength * 4.0f)
        };

        _lastTriangleCount = triangleCount;
        _lastRejectedSlopeCount = rejectedSlopeCount;
        _lastRejectedWaterCount = rejectedWaterCount;
        _lastRejectedHighlandCount = rejectedHighlandCount;
        _lastRejectedMaterialCount = rejectedMaterialCount;
        _lastRejectedBiomeCount = rejectedBiomeCount;
        _lastRejectedDensityCount = rejectedDensityCount;
        _lastBuiltInstanceCount = transforms.Count;
        return patch;
    }

    private TerrainLodManager ResolveLodManager()
    {
        if (!TerrainLodManagerPath.IsEmpty)
        {
            return GetNodeOrNull<TerrainLodManager>(TerrainLodManagerPath);
        }

        return GetParent()?.GetNodeOrNull<TerrainLodManager>("TerrainLodManager");
    }

    private Vector3 ResolveViewerPosition()
    {
        Camera3D camera = GetViewport().GetCamera3D();
        if (camera != null)
        {
            return camera.GlobalTransform.Origin;
        }

        if (_terrainWorld != null && !_terrainWorld.TrackedCharacterPath.IsEmpty)
        {
            Node3D trackedCharacter = _terrainWorld.GetNodeOrNull<Node3D>(_terrainWorld.TrackedCharacterPath);
            if (trackedCharacter != null)
            {
                return trackedCharacter.GlobalTransform.Origin;
            }
        }

        return GlobalTransform.Origin;
    }

    private void EnsureTerrainPlacementSampler()
    {
        if (_terrainWorld == null)
        {
            _terrainPlacementSampler = null;
            _terrainPlacementSamplerSignature = 0;
            return;
        }

        long samplerSignature = BuildTerrainPlacementSamplerSignature();
        if (_terrainPlacementSampler != null && samplerSignature == _terrainPlacementSamplerSignature)
        {
            return;
        }

        _terrainPlacementSampler = new VoxelFieldGenerator(
            _terrainWorld.Seed,
            _terrainWorld.TerrainHeight,
            _terrainWorld.DetailHeight,
            _terrainWorld.CaveScale,
            _terrainWorld.CaveThreshold,
            _terrainWorld.WaterLevel,
            _terrainWorld.ShorelineFalloff,
            _terrainWorld.WaterBasinInfluence);
        _terrainPlacementSamplerSignature = samplerSignature;
    }

    private long BuildTerrainPlacementSamplerSignature()
    {
        ulong hash = 1469598103934665603UL;
        HashCombine(ref hash, _terrainWorld?.Seed ?? 0);
        HashCombine(ref hash, _terrainWorld?.TerrainHeight ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.DetailHeight ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.CaveScale ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.CaveThreshold ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.WaterLevel ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.ShorelineFalloff ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.WaterBasinInfluence ?? 0.0f);
        return unchecked((long)hash);
    }

    private static TerrainSurfaceColumnSample ResolveTerrainSurfaceColumnSample(
        Vector3 worldPosition,
        VoxelFieldGenerator placementSampler,
        float cellSize,
        Dictionary<TerrainSurfaceQueryKey, TerrainSurfaceColumnSample> cache)
    {
        if (placementSampler == null)
        {
            return new TerrainSurfaceColumnSample(worldPosition.Y, TerrainBiomeSample.Default);
        }

        float safeCellSize = Mathf.Max(TerrainSurfaceQueryCellSizeMin, cellSize);
        TerrainSurfaceQueryKey key = new(
            Mathf.FloorToInt(worldPosition.X / safeCellSize),
            Mathf.FloorToInt(worldPosition.Z / safeCellSize));
        if (cache.TryGetValue(key, out TerrainSurfaceColumnSample sample))
        {
            return sample;
        }

        sample = placementSampler.SampleSurfaceColumn(worldPosition.X, worldPosition.Z);
        cache[key] = sample;
        return sample;
    }

    private float ResolveTerrainSurfaceQueryCellSize(int lod)
    {
        float baseVoxelSize = _terrainWorld?.VoxelSize ?? 1.2f;
        return Mathf.Max(TerrainSurfaceQueryCellSizeMin, baseVoxelSize * Mathf.Pow(2.0f, lod));
    }

    private GrassTrackingState EvaluateRendererTracking(TerrainRenderer renderer, Vector3 viewerPosition)
    {
        if (renderer == null ||
            !IsInstanceValid(renderer) ||
            !renderer.IsInsideTree() ||
            !renderer.HasVisuals ||
            renderer.Vertices == null ||
            renderer.Vertices.Length < 3)
        {
            return GrassTrackingState.MissingVisuals;
        }

        if (renderer.BlockId.Lod > Mathf.Max(0, MaxGrassLod))
        {
            return GrassTrackingState.LodFiltered;
        }

        float maxBuildDistance = Mathf.Max(4.0f, RenderDistance + FadeDistance);
        return renderer.GlobalTransform.Origin.DistanceTo(viewerPosition) <= maxBuildDistance
            ? GrassTrackingState.Eligible
            : GrassTrackingState.DistanceFiltered;
    }

    private float ComputeDistanceDensityFactor(float distanceToViewer)
    {
        float densityFalloffStart = Mathf.Clamp(DensityFalloffStart, 0.0f, RenderDistance);
        float densityFalloffEnd = Mathf.Max(densityFalloffStart + 0.01f, RenderDistance + FadeDistance);
        if (distanceToViewer <= densityFalloffStart || densityFalloffEnd <= densityFalloffStart)
        {
            return 1.0f;
        }

        float t = Mathf.InverseLerp(densityFalloffStart, densityFalloffEnd, distanceToViewer);
        return 1.0f - Mathf.SmoothStep(0.0f, 1.0f, t);
    }

    private void RemovePatch(ulong rendererId)
    {
        _pendingBuildIds.Remove(rendererId);
        if (_patches.Remove(rendererId, out GrassPatchEntry entry) &&
            entry.Patch != null &&
            IsInstanceValid(entry.Patch))
        {
            entry.Patch.QueueFree();
        }
    }

    private void RebuildAllPatches()
    {
        ClearAllPatches();
        _skippedPatchBuilds.Clear();
        _pendingBuilds.Clear();
        _pendingBuildIds.Clear();
        _syncCountdown = 0.0f;
    }

    private void ClearAllPatches()
    {
        List<ulong> patchIds = new(_patches.Keys);
        foreach (ulong patchId in patchIds)
        {
            RemovePatch(patchId);
        }
    }

    private bool ShouldSkipPatchBuild(TerrainRenderer renderer)
    {
        ulong rendererId = renderer.GetInstanceId();
        return _skippedPatchBuilds.TryGetValue(rendererId, out GrassPatchSkipEntry entry) &&
            entry.RendererFingerprint == ComputeRendererGrassBuildFingerprint(renderer);
    }

    private void RememberSkippedPatchBuild(TerrainRenderer renderer)
    {
        _skippedPatchBuilds[renderer.GetInstanceId()] =
            new GrassPatchSkipEntry(ComputeRendererGrassBuildFingerprint(renderer));
    }

    private void EnsureResources()
    {
        _grassMesh ??= BuildGrassBladeMesh();
        _grassShaderMaterial ??= BuildGrassShaderMaterial();
        _fallbackMaterial ??= BuildFallbackMaterial();
        RefreshActiveMaterial();
    }

    private ShaderMaterial BuildGrassShaderMaterial()
    {
        _grassShader = ResourceLoader.Load<Shader>(GrassShaderPath);
        if (_grassShader == null && !_warnedMissingShader)
        {
            GD.PushWarning($"TerrainGrassSystem could not load grass shader at {GrassShaderPath}.");
            _warnedMissingShader = true;
        }

        _grassTexture = ResourceLoader.Load<Texture2D>(GrassTexturePath);
        if (_grassTexture == null && !_warnedMissingTexture)
        {
            GD.PushWarning($"TerrainGrassSystem could not load grass texture at {GrassTexturePath}.");
            _warnedMissingTexture = true;
        }

        ShaderMaterial material = new();
        if (_grassShader != null)
        {
            material.Shader = _grassShader;
        }

        if (_grassTexture != null)
        {
            material.SetShaderParameter("blade_texture", _grassTexture);
        }

        return material;
    }

    private StandardMaterial3D BuildFallbackMaterial()
    {
        bool canUseTextureFallback = _grassTexture != null && ResolveAtlasFrameCount() <= 1;
        StandardMaterial3D material = new()
        {
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            Roughness = GrassRoughness,
            AlphaScissorThreshold = GrassAlphaScissorThreshold
        };
        if (canUseTextureFallback)
        {
            material.AlbedoTexture = _grassTexture;
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
        }
        else
        {
            material.AlbedoColor = DebugFallbackColor;
        }

        return material;
    }

    private void UpdateMaterialParameters()
    {
        if (_grassShaderMaterial == null)
        {
            return;
        }

        if (_grassTexture != null)
        {
            _grassShaderMaterial.SetShaderParameter("blade_texture", _grassTexture);
            if (_fallbackMaterial != null)
            {
                // StandardMaterial cannot crop atlas frames, so keep the fallback as a plain debug proxy.
                if (ResolveAtlasFrameCount() <= 1)
                {
                    _fallbackMaterial.AlbedoTexture = _grassTexture;
                    _fallbackMaterial.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                }
                else
                {
                    _fallbackMaterial.AlbedoTexture = null;
                    _fallbackMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                }
            }
        }
        else if (_fallbackMaterial != null)
        {
            _fallbackMaterial.AlbedoTexture = null;
            _fallbackMaterial.AlbedoColor = DebugFallbackColor;
            _fallbackMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
        }

        _grassShaderMaterial.SetShaderParameter("wind_strength", WindStrength);
        _grassShaderMaterial.SetShaderParameter("wind_speed", WindSpeed);
        _grassShaderMaterial.SetShaderParameter("wind_frequency", WindFrequency);
        _grassShaderMaterial.SetShaderParameter("top_bend_power", WindTopBias);
        _grassShaderMaterial.SetShaderParameter("atlas_columns", (float)Mathf.Max(1, BladeAtlasColumns));
        _grassShaderMaterial.SetShaderParameter("atlas_rows", (float)Mathf.Max(1, BladeAtlasRows));
        _grassShaderMaterial.SetShaderParameter("atlas_frame_count", (float)ResolveAtlasFrameCount());
        _grassShaderMaterial.SetShaderParameter("roughness", GrassRoughness);
        _grassShaderMaterial.SetShaderParameter("alpha_scissor_threshold", GrassAlphaScissorThreshold);
        if (_fallbackMaterial != null)
        {
            _fallbackMaterial.Roughness = GrassRoughness;
            _fallbackMaterial.AlphaScissorThreshold = GrassAlphaScissorThreshold;
            _fallbackMaterial.AlbedoColor = DebugFallbackColor;
        }

        RefreshActiveMaterial();
    }

    private static ArrayMesh BuildGrassBladeMesh()
    {
        List<Vector3> vertices = new();
        List<Vector2> uvs = new();
        List<Vector3> normals = new();

        AddGrassPlane(
            vertices,
            uvs,
            normals,
            new Vector3(-0.5f, 0.0f, 0.0f),
            new Vector3(0.5f, 0.0f, 0.0f),
            new Vector3(-0.26f, 0.58f, 0.0f),
            new Vector3(0.26f, 0.58f, 0.0f),
            new Vector3(-0.08f, 1.0f, 0.0f),
            new Vector3(0.08f, 1.0f, 0.0f));

        AddGrassPlane(
            vertices,
            uvs,
            normals,
            new Vector3(0.0f, 0.0f, -0.5f),
            new Vector3(0.0f, 0.0f, 0.5f),
            new Vector3(0.0f, 0.58f, -0.26f),
            new Vector3(0.0f, 0.58f, 0.26f),
            new Vector3(0.0f, 1.0f, -0.08f),
            new Vector3(0.0f, 1.0f, 0.08f));

        Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static void AddGrassPlane(
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<Vector3> normals,
        Vector3 baseLeft,
        Vector3 baseRight,
        Vector3 midLeft,
        Vector3 midRight,
        Vector3 tipLeft,
        Vector3 tipRight)
    {
        AddGrassTriangle(vertices, uvs, normals, baseLeft, baseRight, midRight, new(0.0f, 1.0f), new(1.0f, 1.0f), new(1.0f, 0.42f));
        AddGrassTriangle(vertices, uvs, normals, baseLeft, midRight, midLeft, new(0.0f, 1.0f), new(1.0f, 0.42f), new(0.0f, 0.42f));
        AddGrassTriangle(vertices, uvs, normals, midLeft, midRight, tipRight, new(0.0f, 0.42f), new(1.0f, 0.42f), new(1.0f, 0.0f));
        AddGrassTriangle(vertices, uvs, normals, midLeft, tipRight, tipLeft, new(0.0f, 0.42f), new(1.0f, 0.0f), new(0.0f, 0.0f));
    }

    private static void AddGrassTriangle(
        List<Vector3> vertices,
        List<Vector2> uvs,
        List<Vector3> normals,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector2 uvA,
        Vector2 uvB,
        Vector2 uvC)
    {
        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        uvs.Add(uvA);
        uvs.Add(uvB);
        uvs.Add(uvC);
        normals.Add(ComputeGrassVertexNormal(a));
        normals.Add(ComputeGrassVertexNormal(b));
        normals.Add(ComputeGrassVertexNormal(c));
    }

    private static Vector3 ComputeGrassVertexNormal(Vector3 vertex)
    {
        Vector3 lateral = new(vertex.X * 0.18f, 2.8f, vertex.Z * 0.18f);
        if (lateral.LengthSquared() <= 0.0001f)
        {
            return Vector3.Up;
        }

        return lateral.Normalized();
    }

    private void UpdateClumpNoiseGenerators()
    {
        int seed = _terrainWorld?.Seed ?? 12345;

        _clumpNoise ??= new FastNoiseLite();
        _clumpNoise.Seed = seed + 881;
        _clumpNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _clumpNoise.Frequency = Mathf.Max(0.0001f, ClumpNoiseFrequency);

        _clumpDetailNoise ??= new FastNoiseLite();
        _clumpDetailNoise.Seed = seed + 919;
        _clumpDetailNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        _clumpDetailNoise.Frequency = Mathf.Max(0.0001f, ClumpDetailFrequency);
    }

    private int ResolveAtlasFrameCount()
    {
        int atlasColumns = Mathf.Max(1, BladeAtlasColumns);
        int atlasRows = Mathf.Max(1, BladeAtlasRows);
        int atlasCapacity = atlasColumns * atlasRows;
        return Mathf.Clamp(BladeAtlasFrameCount, 1, atlasCapacity);
    }

    private float ResolveMaxHeightScale()
    {
        float scale = Mathf.Max(ScaleMin, ScaleMax);
        float variation = Mathf.Max(HeightVariationMin, HeightVariationMax);
        return scale * variation * (1.0f + HeightScaleJitter) * Mathf.Max(ThinTallTypeHeight, Mathf.Max(ShortDenseTypeHeight, CurvedTypeHeight));
    }

    private float ResolveMaxWidthScale()
    {
        float scale = Mathf.Max(ScaleMin, ScaleMax);
        float variation = Mathf.Max(WidthVariationMin, WidthVariationMax);
        return scale * variation * (1.0f + WidthScaleJitter) * Mathf.Max(ThinTallTypeWidth, Mathf.Max(ShortDenseTypeWidth, CurvedTypeWidth));
    }

    private float ComputeClumpDensityFactor(
        Vector3 worldPosition,
        float heightAboveWater,
        float lowlandFactor,
        float slopeFactor)
    {
        if (_clumpNoise == null)
        {
            return 1.0f;
        }

        float patchThresholdMin = Mathf.Clamp(ClumpThresholdMin, 0.0f, 0.99f);
        float patchThresholdMax = Mathf.Clamp(ClumpThresholdMax, patchThresholdMin + 0.01f, 1.0f);
        float patchNoise = NoiseToUnit(_clumpNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
        float patchMask = Mathf.SmoothStep(patchThresholdMin, patchThresholdMax, patchNoise);
        // Shrub-style coverage reads better when dense centers fall off quickly into open ground.
        patchMask *= patchMask;
        float clumpMask = Mathf.Lerp(Mathf.Clamp(ClumpDensityFloor, 0.0f, 1.0f), 1.0f, patchMask);

        if (_clumpDetailNoise != null && ClumpDetailStrength > 0.001f)
        {
            float detailThresholdMin = Mathf.Clamp(ClumpDetailThresholdMin, 0.0f, 0.99f);
            float detailThresholdMax = Mathf.Clamp(ClumpDetailThresholdMax, detailThresholdMin + 0.01f, 1.0f);
            float detailNoise = NoiseToUnit(_clumpDetailNoise.GetNoise2D(worldPosition.X, worldPosition.Z));
            float detailMask = Mathf.SmoothStep(detailThresholdMin, detailThresholdMax, detailNoise);
            float detailFloor = Mathf.Clamp(1.0f - ClumpDetailStrength, 0.0f, 1.0f);
            clumpMask *= Mathf.Lerp(detailFloor, 1.0f, detailMask);
        }

        float shoreStart = Mathf.Max(0.0f, MinHeightAboveWater);
        float shoreEnd = shoreStart + Mathf.Max(0.05f, ShoreDensityBoostRange);
        float shoreProximity = 1.0f - Mathf.SmoothStep(shoreStart, shoreEnd, heightAboveWater);
        float shoreBoost = Mathf.Lerp(1.0f, Mathf.Max(1.0f, ShoreDensityBoost), shoreProximity);

        float plainsBias = Mathf.Lerp(0.86f, 1.00f, Mathf.Clamp(slopeFactor, 0.0f, 1.0f));
        float lowlandBias = Mathf.Lerp(0.90f, 1.02f, Mathf.Clamp(lowlandFactor, 0.0f, 1.0f));
        float terrainBias = Mathf.Min(1.12f, plainsBias * lowlandBias * shoreBoost);
        return clumpMask * terrainBias;
    }

    private float ComputeShoreProximity(float heightAboveWater)
    {
        float shoreStart = Mathf.Max(0.0f, MinHeightAboveWater);
        float shoreEnd = shoreStart + Mathf.Max(0.05f, ShoreDensityBoostRange);
        return 1.0f - Mathf.SmoothStep(shoreStart, shoreEnd, heightAboveWater);
    }

    private static float ComputeMoistureFactor(float lowlandFactor, float slopeFactor, float shoreProximity)
    {
        return Mathf.Clamp((lowlandFactor * 0.5f) + (shoreProximity * 0.35f) + (slopeFactor * 0.15f), 0.0f, 1.0f);
    }

    private static Color ResolveTriangleTerrainColor(Color[] terrainColors, int triangleStart)
    {
        if (terrainColors == null || terrainColors.Length < triangleStart + 3)
        {
            return Colors.White;
        }

        Color averaged = new(
            (terrainColors[triangleStart].R + terrainColors[triangleStart + 1].R + terrainColors[triangleStart + 2].R) / 3.0f,
            (terrainColors[triangleStart].G + terrainColors[triangleStart + 1].G + terrainColors[triangleStart + 2].G) / 3.0f,
            (terrainColors[triangleStart].B + terrainColors[triangleStart + 1].B + terrainColors[triangleStart + 2].B) / 3.0f,
            1.0f);
        return new Color(
            Mathf.Clamp(averaged.R, 0.0f, 1.0f),
            Mathf.Clamp(averaged.G, 0.0f, 1.0f),
            Mathf.Clamp(averaged.B, 0.0f, 1.0f),
            1.0f);
    }

    private Color ResolveGrassColor(Color terrainColor, float moisture)
    {
        Color grassColor = BladeColorMin.Lerp(BladeColorMax, Mathf.Clamp(moisture, 0.0f, 1.0f));
        Color modulated = MultiplyColors(grassColor, terrainColor);
        Color blended = terrainColor.Lerp(modulated, 0.5f);
        return new Color(
            Mathf.Clamp(blended.R, 0.0f, 1.0f),
            Mathf.Clamp(blended.G, 0.0f, 1.0f),
            Mathf.Clamp(blended.B, 0.0f, 1.0f),
            1.0f);
    }

    private static Color MultiplyColors(Color a, Color b)
    {
        return new Color(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);
    }

    private static void ResolveGrassType(
        RandomNumberGenerator random,
        out float heightScale,
        out float widthScale,
        out float leanScale,
        out float moistureBias)
    {
        float selector = random.Randf();
        if (selector < 0.34f)
        {
            heightScale = ThinTallTypeHeight;
            widthScale = ThinTallTypeWidth;
            leanScale = ThinTallTypeLean;
            moistureBias = ThinTallMoistureBias;
            return;
        }

        if (selector < 0.68f)
        {
            heightScale = ShortDenseTypeHeight;
            widthScale = ShortDenseTypeWidth;
            leanScale = ShortDenseTypeLean;
            moistureBias = ShortDenseMoistureBias;
            return;
        }

        heightScale = CurvedTypeHeight;
        widthScale = CurvedTypeWidth;
        leanScale = CurvedTypeLean;
        moistureBias = CurvedMoistureBias;
    }

    private long BuildSettingsSignature()
    {
        ulong hash = 1469598103934665603UL;
        HashCombine(ref hash, _terrainWorld?.Seed ?? 0);
        HashCombine(ref hash, _terrainWorld?.TerrainHeight ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.DetailHeight ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.CaveScale ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.CaveThreshold ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.WaterLevel ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.ShorelineFalloff ?? 0.0f);
        HashCombine(ref hash, _terrainWorld?.WaterBasinInfluence ?? 0.0f);
        HashCombine(ref hash, DensityPerSquareMeter);
        HashCombine(ref hash, MaxSlopeDegrees);
        HashCombine(ref hash, MinHeightAboveWater);
        HashCombine(ref hash, HighlandFadeStart);
        HashCombine(ref hash, HighlandFadeEnd);
        HashCombine(ref hash, DensityFalloffStart);
        HashCombine(ref hash, MaxGrassLod);
        HashCombine(ref hash, MaxInstancesPerPatch);
        HashCombine(ref hash, PlacementLift);
        HashCombine(ref hash, ClumpNoiseFrequency);
        HashCombine(ref hash, ClumpThresholdMin);
        HashCombine(ref hash, ClumpThresholdMax);
        HashCombine(ref hash, ClumpDensityFloor);
        HashCombine(ref hash, ClumpDetailFrequency);
        HashCombine(ref hash, ClumpDetailStrength);
        HashCombine(ref hash, ClumpDetailThresholdMin);
        HashCombine(ref hash, ClumpDetailThresholdMax);
        HashCombine(ref hash, ShoreDensityBoost);
        HashCombine(ref hash, ShoreDensityBoostRange);
        HashCombine(ref hash, BladeHeight);
        HashCombine(ref hash, BladeWidth);
        HashCombine(ref hash, ScaleMin);
        HashCombine(ref hash, ScaleMax);
        HashCombine(ref hash, HeightVariationMin);
        HashCombine(ref hash, HeightVariationMax);
        HashCombine(ref hash, WidthVariationMin);
        HashCombine(ref hash, WidthVariationMax);
        HashCombine(ref hash, LeanVariationRadians);
        HashCombine(ref hash, BladeAtlasColumns);
        HashCombine(ref hash, BladeAtlasRows);
        HashCombine(ref hash, BladeAtlasFrameCount);
        HashCombine(ref hash, WidthScaleJitter);
        HashCombine(ref hash, HeightScaleJitter);
        HashCombine(ref hash, MaxTiltDegrees);
        HashCombine(ref hash, RenderDistance);
        HashCombine(ref hash, FadeDistance);
        HashCombine(ref hash, GrassRoughness);
        HashCombine(ref hash, GrassAlphaScissorThreshold);
        HashCombine(ref hash, CastGrassShadows);
        HashCombine(ref hash, BladeColorMin);
        HashCombine(ref hash, BladeColorMax);
        HashCombine(ref hash, DebugForceFallbackMaterial);
        HashCombine(ref hash, DebugBypassPlacementFilters);
        HashCombine(ref hash, DebugFallbackColor);
        return unchecked((long)hash);
    }

    private static bool IsGrassPlacementBiomeAllowed(BiomeId dominantBiome)
    {
        return dominantBiome != BiomeId.Rocky &&
            dominantBiome != BiomeId.Canyon &&
            dominantBiome != BiomeId.Volcanic;
    }

    private static bool TryResolveTriangleBiomeSample(
        float[] biomeWeights,
        int triangleVertexStart,
        out TerrainBiomeSample biome)
    {
        biome = TerrainBiomeSample.Default;
        if (biomeWeights == null || biomeWeights.Length < ((triangleVertexStart + 3) * 4))
        {
            return false;
        }

        int aOffset = triangleVertexStart * 4;
        int bOffset = (triangleVertexStart + 1) * 4;
        int cOffset = (triangleVertexStart + 2) * 4;

        float plainsWeight = (biomeWeights[aOffset] + biomeWeights[bOffset] + biomeWeights[cOffset]) / 3.0f;
        float rockyWeight = (biomeWeights[aOffset + 1] + biomeWeights[bOffset + 1] + biomeWeights[cOffset + 1]) / 3.0f;
        float canyonWeight = (biomeWeights[aOffset + 2] + biomeWeights[bOffset + 2] + biomeWeights[cOffset + 2]) / 3.0f;
        float swampWeight = (biomeWeights[aOffset + 3] + biomeWeights[bOffset + 3] + biomeWeights[cOffset + 3]) / 3.0f;
        float volcanicWeight = Mathf.Max(0.0f, 1.0f - (plainsWeight + rockyWeight + canyonWeight + swampWeight));
        float totalWeight = plainsWeight + rockyWeight + canyonWeight + swampWeight + volcanicWeight;
        if (totalWeight <= 0.0001f)
        {
            return false;
        }

        plainsWeight /= totalWeight;
        rockyWeight /= totalWeight;
        canyonWeight /= totalWeight;
        swampWeight /= totalWeight;
        volcanicWeight /= totalWeight;

        biome = new TerrainBiomeSample(
            ResolveDominantBiome(plainsWeight, rockyWeight, canyonWeight, swampWeight, volcanicWeight),
            plainsWeight,
            rockyWeight,
            canyonWeight,
            swampWeight,
            volcanicWeight,
            0.5f,
            0.5f,
            Mathf.Clamp(rockyWeight + canyonWeight + volcanicWeight, 0.0f, 1.0f),
            volcanicWeight);
        return true;
    }

    private static BiomeId ResolveDominantBiome(
        float plainsWeight,
        float rockyWeight,
        float canyonWeight,
        float swampWeight,
        float volcanicWeight)
    {
        BiomeId dominantBiome = BiomeId.Plains;
        float strongestWeight = plainsWeight;

        if (rockyWeight > strongestWeight)
        {
            dominantBiome = BiomeId.Rocky;
            strongestWeight = rockyWeight;
        }

        if (canyonWeight > strongestWeight)
        {
            dominantBiome = BiomeId.Canyon;
            strongestWeight = canyonWeight;
        }

        if (swampWeight > strongestWeight)
        {
            dominantBiome = BiomeId.Swamp;
            strongestWeight = swampWeight;
        }

        if (volcanicWeight > strongestWeight)
        {
            dominantBiome = BiomeId.Volcanic;
        }

        return dominantBiome;
    }

    private static void HashCombine(ref ulong hash, int value)
    {
        hash ^= unchecked((uint)value);
        hash *= 1099511628211UL;
    }

    private static void HashCombine(ref ulong hash, bool value)
    {
        HashCombine(ref hash, value ? 1 : 0);
    }

    private static void HashCombine(ref ulong hash, float value)
    {
        HashCombine(ref hash, Mathf.RoundToInt(value * 1000.0f));
    }

    private static void HashCombine(ref ulong hash, Color value)
    {
        HashCombine(ref hash, value.R);
        HashCombine(ref hash, value.G);
        HashCombine(ref hash, value.B);
        HashCombine(ref hash, value.A);
    }

    private static ulong ComputeRendererSeed(TerrainBlockId blockId)
    {
        ulong hash = 1469598103934665603UL;
        HashCombine(ref hash, blockId.Lod);
        HashCombine(ref hash, blockId.Index.X);
        HashCombine(ref hash, blockId.Index.Y);
        HashCombine(ref hash, blockId.Index.Z);
        return hash;
    }

    private static ulong ComputeRendererGrassBuildFingerprint(TerrainRenderer renderer)
    {
        ulong hash = ComputeRendererSeed(renderer.BlockId);
        Vector3[] vertices = renderer.Vertices;
        Vector3[] normals = renderer.Normals;
        float[] biomeWeights = renderer.BiomeWeights;
        HashCombine(ref hash, vertices?.Length ?? 0);
        HashCombine(ref hash, normals?.Length ?? 0);
        HashCombine(ref hash, biomeWeights?.Length ?? 0);
        HashCombine(ref hash, vertices == null ? 0 : RuntimeHelpers.GetHashCode(vertices));
        HashCombine(ref hash, normals == null ? 0 : RuntimeHelpers.GetHashCode(normals));
        HashCombine(ref hash, biomeWeights == null ? 0 : RuntimeHelpers.GetHashCode(biomeWeights));
        return hash;
    }

    private static Vector3 SampleTriangle(Vector3 a, Vector3 b, Vector3 c, RandomNumberGenerator random)
    {
        float sqrtR1 = Mathf.Sqrt(random.Randf());
        float r2 = random.Randf();
        float weightA = 1.0f - sqrtR1;
        float weightB = sqrtR1 * (1.0f - r2);
        float weightC = sqrtR1 * r2;
        return (a * weightA) + (b * weightB) + (c * weightC);
    }

    private static float NoiseToUnit(float value)
    {
        return Mathf.Clamp((value + 1.0f) * 0.5f, 0.0f, 1.0f);
    }

    public string GetDebugSummary()
    {
        string materialMode = _usingFallbackMaterial ? "fallback" : "shader";
        string filterMode = DebugBypassPlacementFilters ? "bypass" : "normal";
        return
            $"grass scan {_lastEligibleRendererCount}/{_lastScannedRendererCount} q {_pendingBuilds.Count} patch {_lastActivePatchCount} inst {_lastActiveInstanceCount} " +
            $"rej nv/lod/dist {_lastRejectedRendererNoVisualsCount}/{_lastRejectedRendererLodCount}/{_lastRejectedRendererDistanceCount} " +
            $"tri {_lastTriangleCount} rej s/w/h/m/b/d {_lastRejectedSlopeCount}/{_lastRejectedWaterCount}/{_lastRejectedHighlandCount}/{_lastRejectedMaterialCount}/{_lastRejectedBiomeCount}/{_lastRejectedDensityCount} " +
            $"last {_lastBuiltInstanceCount} {materialMode}/{filterMode}  {TrimDebug(_lastBuildOutcome, 84)}";
    }

    private bool EnsureDebugLogWriter()
    {
        if (_debugLogWriter != null)
        {
            return true;
        }

        try
        {
            string rootPath = ProjectSettings.GlobalizePath("user://profiling");
            Directory.CreateDirectory(rootPath);
            string logPath = ProjectSettings.GlobalizePath(GrassDebugLogRelativePath);
            _debugLogWriter = new StreamWriter(
                new FileStream(logPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            lock (_debugLogLock)
            {
                _debugLogWriter.WriteLine(
                    $"{GrassDebugLogPrefix} event=session_begin utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} path=\"{logPath}\"");
            }

            _warnedDebugLogFailure = false;
            return true;
        }
        catch (Exception exception)
        {
            _debugLogWriter?.Dispose();
            _debugLogWriter = null;
            if (!_warnedDebugLogFailure)
            {
                GD.PushWarning(
                    $"TerrainGrassSystem could not open grass debug log at {GrassDebugLogRelativePath}: {exception.Message}");
                _warnedDebugLogFailure = true;
            }

            return false;
        }
    }

    private void CloseDebugLogWriter()
    {
        if (_debugLogWriter == null)
        {
            return;
        }

        try
        {
            lock (_debugLogLock)
            {
                _debugLogWriter.WriteLine(
                    $"{GrassDebugLogPrefix} event=session_end utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
                _debugLogWriter.Dispose();
            }
        }
        finally
        {
            _debugLogWriter = null;
        }
    }

    private void WriteDebugLogLine(string line)
    {
        if (!EnsureDebugLogWriter())
        {
            return;
        }

        try
        {
            lock (_debugLogLock)
            {
                _debugLogWriter!.WriteLine(line);
            }
        }
        catch (Exception exception)
        {
            if (!_warnedDebugLogFailure)
            {
                GD.PushWarning(
                    $"TerrainGrassSystem could not write grass debug log at {GrassDebugLogRelativePath}: {exception.Message}");
                _warnedDebugLogFailure = true;
            }

            CloseDebugLogWriter();
        }
    }

    private void RefreshActiveMaterial()
    {
        _usingFallbackMaterial = DebugForceFallbackMaterial || _grassShader == null || _grassTexture == null;
        _activeGrassMaterial = _usingFallbackMaterial
            ? _fallbackMaterial
            : _grassShaderMaterial;

        foreach (GrassPatchEntry entry in _patches.Values)
        {
            if (entry?.Patch != null && IsInstanceValid(entry.Patch))
            {
                entry.Patch.MaterialOverride = _activeGrassMaterial;
            }
        }
    }

    private void RefreshActivePatchStats()
    {
        _lastActivePatchCount = 0;
        _lastActiveInstanceCount = 0;
        foreach (GrassPatchEntry entry in _patches.Values)
        {
            if (entry?.Patch == null || !IsInstanceValid(entry.Patch))
            {
                continue;
            }

            _lastActivePatchCount++;
            _lastActiveInstanceCount += entry.Patch.Multimesh?.InstanceCount ?? 0;
        }
    }

    private void MaybeEmitDebugLog()
    {
        if (!EnableDebugLogging)
        {
            CloseDebugLogWriter();
            return;
        }

        _debugLogCountdown -= (float)GetProcessDeltaTime();
        if (_debugLogCountdown > 0.0f)
        {
            return;
        }

        _debugLogCountdown = Mathf.Max(0.2f, DebugLogIntervalSeconds);
        WriteDebugLogLine(
            $"{GrassDebugLogPrefix} event=summary utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {GetDebugSummary()}");
    }

    private void AccumulateRendererRejection(GrassTrackingState trackingState)
    {
        switch (trackingState)
        {
            case GrassTrackingState.MissingVisuals:
                _lastRejectedRendererNoVisualsCount++;
                break;
            case GrassTrackingState.LodFiltered:
                _lastRejectedRendererLodCount++;
                break;
            case GrassTrackingState.DistanceFiltered:
                _lastRejectedRendererDistanceCount++;
                break;
        }
    }

    private static string TrimDebug(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Mathf.Max(0, maxLength - 3)] + "...";
    }

    private enum GrassTrackingState
    {
        Eligible = 0,
        MissingVisuals = 1,
        LodFiltered = 2,
        DistanceFiltered = 3
    }

    private sealed class GrassPatchEntry
    {
        public GrassPatchEntry(TerrainRenderer renderer, MultiMeshInstance3D patch)
        {
            Renderer = renderer;
            Patch = patch;
        }

        public TerrainRenderer Renderer { get; }
        public MultiMeshInstance3D Patch { get; }
    }

    private readonly record struct GrassPatchSkipEntry(ulong RendererFingerprint);

    private readonly record struct TerrainSurfaceQueryKey(int XCell, int ZCell);
}
