using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Terrain;

public partial class TerrainGrassSystem : Node3D
{
    private const string GrassPatchNodeName = "GrassPatch";
    private const string GrassShaderPath = "res://shaders/terrain/TerrainGrass.gdshader";
    private const string GrassTexturePath = "res://textures/terrain/grass_blades.svg";
    private const float Tau = Mathf.Pi * 2.0f;

    [ExportGroup("Nodes")]
    [Export] public NodePath TerrainLodManagerPath = new("../TerrainLodManager");

    [ExportGroup("Distribution")]
    [Export(PropertyHint.Range, "0.25,16,0.25")] public float DensityPerSquareMeter = 4.25f;
    [Export(PropertyHint.Range, "0,75,1")] public float MaxSlopeDegrees = 38.0f;
    [Export(PropertyHint.Range, "0,4,0.05")] public float MinHeightAboveWater = 0.35f;
    [Export(PropertyHint.Range, "0,48,0.25")] public float HighlandFadeStart = 16.0f;
    [Export(PropertyHint.Range, "1,96,0.25")] public float HighlandFadeEnd = 34.0f;
    [Export(PropertyHint.Range, "0,256,0.5")] public float DensityFalloffStart = 30.0f;
    [Export(PropertyHint.Range, "0,2,1")] public int MaxGrassLod = 1;
    [Export(PropertyHint.Range, "128,12000,64")] public int MaxInstancesPerPatch = 2800;
    [Export(PropertyHint.Range, "0.00,0.25,0.005")] public float PlacementLift = 0.02f;

    [ExportGroup("Blade Shape")]
    [Export(PropertyHint.Range, "0.2,3,0.05")] public float BladeHeight = 0.78f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float BladeWidth = 0.16f;
    [Export(PropertyHint.Range, "0.3,2,0.05")] public float ScaleMin = 0.55f;
    [Export(PropertyHint.Range, "0.5,3,0.05")] public float ScaleMax = 0.90f;
    [Export] public Color BladeColorMin = new(35.0f / 255.0f, 97.0f / 255.0f, 53.0f / 255.0f, 1.0f);
    [Export] public Color BladeColorMax = new(137.0f / 255.0f, 148.0f / 255.0f, 80.0f / 255.0f, 1.0f);

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
    [Export] public bool CastGrassShadows;

    [ExportGroup("Debug")]
    [Export] public bool EnableDebugLogging = true;
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float DebugLogIntervalSeconds = 1.5f;
    [Export] public bool DebugForceFallbackMaterial;
    [Export] public bool DebugBypassPlacementFilters;
    [Export] public Color DebugFallbackColor = new(0.24f, 0.95f, 0.32f, 1.0f);

    private readonly Dictionary<ulong, GrassPatchEntry> _patches = new();
    private readonly Queue<TerrainRenderer> _pendingBuilds = new();
    private readonly HashSet<ulong> _pendingBuildIds = new();

    private TerrainWorld _terrainWorld = null!;
    private TerrainLodManager _lodManager = null!;
    private ArrayMesh _grassMesh = null!;
    private ShaderMaterial _grassShaderMaterial = null!;
    private StandardMaterial3D _fallbackMaterial = null!;
    private Material _activeGrassMaterial = null!;
    private Shader _grassShader = null!;
    private Texture2D _grassTexture = null!;
    private long _settingsSignature;
    private float _syncCountdown;
    private float _debugLogCountdown;
    private bool _warnedMissingShader;
    private bool _warnedMissingTexture;
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
    private int _lastRejectedDensityCount;
    private string _lastBuildOutcome = "waiting_for_renderers";

    public override void _Ready()
    {
        _terrainWorld = GetParent() as TerrainWorld;
        _lodManager = ResolveLodManager();
        EnsureResources();
        UpdateMaterialParameters();
        _settingsSignature = BuildSettingsSignature();
        _syncCountdown = 0.0f;
        _debugLogCountdown = Mathf.Max(0.2f, DebugLogIntervalSeconds);
    }

    public override void _Process(double delta)
    {
        _terrainWorld ??= GetParent() as TerrainWorld;
        _lodManager ??= ResolveLodManager();
        EnsureResources();
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
        ClearAllPatches();
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
                if (!_patches.ContainsKey(rendererId) && !_pendingBuildIds.Contains(rendererId))
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

            MultiMeshInstance3D patch = BuildGrassPatch(renderer, viewerPosition);
            if (patch == null)
            {
                continue;
            }

            renderer.AddChild(patch);
            _patches[rendererId] = new GrassPatchEntry(renderer, patch);
            _lastBuildOutcome = $"{renderer.BlockId} built {patch.Multimesh?.InstanceCount ?? 0} inst  material {(_usingFallbackMaterial ? "fallback" : "shader")}";
            builtThisFrame++;
        }

        RefreshActivePatchStats();
    }

    private MultiMeshInstance3D BuildGrassPatch(TerrainRenderer renderer, Vector3 viewerPosition)
    {
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
        float slopeLimitDot = Mathf.Cos(Mathf.DegToRad(Mathf.Clamp(MaxSlopeDegrees, 0.0f, 89.0f)));
        float highlandStart = Mathf.Max(0.0f, HighlandFadeStart);
        float highlandEnd = Mathf.Max(highlandStart + 0.01f, HighlandFadeEnd);
        float waterLevel = _terrainWorld?.WaterLevel ?? -2.6f;

        RandomNumberGenerator random = new();
        random.Seed = ComputeRendererSeed(renderer.BlockId);

        List<Transform3D> transforms = new();
        List<Color> colors = new();
        List<Color> customData = new();
        Transform3D rendererTransform = renderer.GlobalTransform;
        int triangleCount = vertices.Length / 3;
        int rejectedSlopeCount = 0;
        int rejectedWaterCount = 0;
        int rejectedHighlandCount = 0;
        int rejectedDensityCount = 0;

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

            float upDot = Mathf.Clamp(surfaceNormal.Dot(Vector3.Up), -1.0f, 1.0f);
            if (!DebugBypassPlacementFilters && upDot < slopeLimitDot)
            {
                rejectedSlopeCount++;
                continue;
            }

            Vector3 centroidLocal = (a + b + c) / 3.0f;
            Vector3 centroidWorld = rendererTransform * centroidLocal;
            float heightAboveWater = centroidWorld.Y - waterLevel;
            if (!DebugBypassPlacementFilters && heightAboveWater < MinHeightAboveWater)
            {
                rejectedWaterCount++;
                continue;
            }

            float lowlandFactor = DebugBypassPlacementFilters
                ? 1.0f
                : 1.0f - Mathf.SmoothStep(highlandStart, highlandEnd, heightAboveWater);
            if (!DebugBypassPlacementFilters && lowlandFactor <= 0.0f)
            {
                rejectedHighlandCount++;
                continue;
            }

            float slopeFactor = DebugBypassPlacementFilters
                ? 1.0f
                : Mathf.SmoothStep(slopeLimitDot, 1.0f, upDot);
            float triangleArea = twiceArea * 0.5f;
            float expectedInstances = triangleArea * DensityPerSquareMeter * lowlandFactor * slopeFactor * distanceDensityFactor;
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
                Vector3 scaleVector = new(
                    BladeWidth * scale,
                    BladeHeight * scale,
                    BladeWidth * scale);

                Basis basis = new Basis(Vector3.Up, random.RandfRange(0.0f, Tau));
                basis = basis.Scaled(scaleVector);

                transforms.Add(new Transform3D(basis, position));
                colors.Add(BladeColorMin.Lerp(BladeColorMax, random.Randf()));
                customData.Add(new Color(random.Randf(), random.Randf(), random.Randf(), 1.0f));
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
            _lastRejectedDensityCount = rejectedDensityCount;
            _lastBuiltInstanceCount = 0;
            _lastBuildOutcome =
                $"{renderer.BlockId} 0 inst  tri {triangleCount}  slope/water/high/density {rejectedSlopeCount}/{rejectedWaterCount}/{rejectedHighlandCount}/{rejectedDensityCount}";
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
            ExtraCullMargin = (BladeHeight * Mathf.Max(ScaleMin, ScaleMax)) + (WindStrength * 3.0f)
        };

        _lastTriangleCount = triangleCount;
        _lastRejectedSlopeCount = rejectedSlopeCount;
        _lastRejectedWaterCount = rejectedWaterCount;
        _lastRejectedHighlandCount = rejectedHighlandCount;
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
        StandardMaterial3D material = new()
        {
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            Roughness = 1.0f,
            AlphaScissorThreshold = 0.35f
        };
        if (_grassTexture != null)
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
                _fallbackMaterial.AlbedoTexture = _grassTexture;
                _fallbackMaterial.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
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
        if (_fallbackMaterial != null)
        {
            _fallbackMaterial.AlbedoColor = DebugFallbackColor;
        }

        RefreshActiveMaterial();
    }

    private static ArrayMesh BuildGrassBladeMesh()
    {
        Vector3[] vertices =
        {
            new(-0.5f, 0.0f, 0.0f),
            new(0.5f, 0.0f, 0.0f),
            new(0.5f, 1.0f, 0.0f),
            new(-0.5f, 0.0f, 0.0f),
            new(0.5f, 1.0f, 0.0f),
            new(-0.5f, 1.0f, 0.0f),

            new(0.0f, 0.0f, -0.5f),
            new(0.0f, 0.0f, 0.5f),
            new(0.0f, 1.0f, 0.5f),
            new(0.0f, 0.0f, -0.5f),
            new(0.0f, 1.0f, 0.5f),
            new(0.0f, 1.0f, -0.5f)
        };

        Vector2[] uvs =
        {
            new(0.0f, 1.0f),
            new(1.0f, 1.0f),
            new(1.0f, 0.0f),
            new(0.0f, 1.0f),
            new(1.0f, 0.0f),
            new(0.0f, 0.0f),

            new(0.0f, 1.0f),
            new(1.0f, 1.0f),
            new(1.0f, 0.0f),
            new(0.0f, 1.0f),
            new(1.0f, 0.0f),
            new(0.0f, 0.0f)
        };

        Vector3[] normals = new Vector3[vertices.Length];
        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = Vector3.Up;
        }

        Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private long BuildSettingsSignature()
    {
        ulong hash = 1469598103934665603UL;
        HashCombine(ref hash, DensityPerSquareMeter);
        HashCombine(ref hash, MaxSlopeDegrees);
        HashCombine(ref hash, MinHeightAboveWater);
        HashCombine(ref hash, HighlandFadeStart);
        HashCombine(ref hash, HighlandFadeEnd);
        HashCombine(ref hash, DensityFalloffStart);
        HashCombine(ref hash, MaxGrassLod);
        HashCombine(ref hash, MaxInstancesPerPatch);
        HashCombine(ref hash, PlacementLift);
        HashCombine(ref hash, BladeHeight);
        HashCombine(ref hash, BladeWidth);
        HashCombine(ref hash, ScaleMin);
        HashCombine(ref hash, ScaleMax);
        HashCombine(ref hash, RenderDistance);
        HashCombine(ref hash, FadeDistance);
        HashCombine(ref hash, CastGrassShadows);
        HashCombine(ref hash, BladeColorMin);
        HashCombine(ref hash, BladeColorMax);
        HashCombine(ref hash, DebugForceFallbackMaterial);
        HashCombine(ref hash, DebugBypassPlacementFilters);
        HashCombine(ref hash, DebugFallbackColor);
        return unchecked((long)hash);
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

    private static Vector3 SampleTriangle(Vector3 a, Vector3 b, Vector3 c, RandomNumberGenerator random)
    {
        float sqrtR1 = Mathf.Sqrt(random.Randf());
        float r2 = random.Randf();
        float weightA = 1.0f - sqrtR1;
        float weightB = sqrtR1 * (1.0f - r2);
        float weightC = sqrtR1 * r2;
        return (a * weightA) + (b * weightB) + (c * weightC);
    }

    public string GetDebugSummary()
    {
        string materialMode = _usingFallbackMaterial ? "fallback" : "shader";
        string filterMode = DebugBypassPlacementFilters ? "bypass" : "normal";
        return
            $"grass scan {_lastEligibleRendererCount}/{_lastScannedRendererCount} q {_pendingBuilds.Count} patch {_lastActivePatchCount} inst {_lastActiveInstanceCount} " +
            $"rej nv/lod/dist {_lastRejectedRendererNoVisualsCount}/{_lastRejectedRendererLodCount}/{_lastRejectedRendererDistanceCount} " +
            $"tri {_lastTriangleCount} rej s/w/h/d {_lastRejectedSlopeCount}/{_lastRejectedWaterCount}/{_lastRejectedHighlandCount}/{_lastRejectedDensityCount} " +
            $"last {_lastBuiltInstanceCount} {materialMode}/{filterMode}  {TrimDebug(_lastBuildOutcome, 84)}";
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
            return;
        }

        _debugLogCountdown -= (float)GetProcessDeltaTime();
        if (_debugLogCountdown > 0.0f)
        {
            return;
        }

        _debugLogCountdown = Mathf.Max(0.2f, DebugLogIntervalSeconds);
        GD.Print($"[TerrainGrass] {GetDebugSummary()}");
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
}
