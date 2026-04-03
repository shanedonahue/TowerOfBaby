using Godot;

namespace TowerOfBaby.Terrain;

[Tool]
public partial class TerrainWaterSurface : Node3D
{
    private const string SurfaceNodeName = "Surface";
    private const string WaterShaderPath = "res://shaders/terrain/StylizedLakeWater.gdshader";

    [ExportGroup("Nodes")]
    [Export] public NodePath TerrainWorldPath = new("..");
    [Export] public NodePath FollowTargetPath = new();

    [ExportGroup("Layout")]
    [Export] public bool FollowViewer = true;
    [Export(PropertyHint.Range, "128,4096,16")] public float SurfaceSize = 960.0f;
    [Export(PropertyHint.Range, "0,256,1")] public float CenterSnapStep = 32.0f;
    [Export(PropertyHint.Range, "-0.250,0.250,0.001")] public float SurfaceLevelOffset = 0.02f;

    [ExportGroup("Water Color")]
    [Export] public Color ShallowColor = new(0.12f, 0.30f, 0.38f, 1.0f);
    [Export] public Color DeepColor = new(0.03f, 0.08f, 0.14f, 1.0f);
    [Export] public Color ShoreColor = new(0.19f, 0.36f, 0.38f, 1.0f);
    [Export] public Color FresnelColor = new(0.20f, 0.34f, 0.46f, 1.0f);

    [ExportGroup("Water Shaping")]
    [Export(PropertyHint.Range, "0.1,8,0.05")] public float ShallowDepth = 1.6f;
    [Export(PropertyHint.Range, "1,32,0.1")] public float DeepDepth = 13.0f;
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float ShoreFadeDepth = 0.8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShallowAlpha = 0.60f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DeepAlpha = 0.88f;

    [ExportGroup("Lighting")]
    [Export(PropertyHint.Range, "0,2,0.01")] public float FresnelStrength = 0.08f;
    [Export(PropertyHint.Range, "0.5,8,0.05")] public float FresnelPower = 4.8f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float SpecularStrength = 0.22f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Metallic = 0.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessNear = 0.32f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float RoughnessFar = 0.22f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ShoreGlow = 0.00f;
    [Export(PropertyHint.Range, "0,0.25,0.005")] public float MacroTintStrength = 0.03f;

    private TerrainWorld _terrainWorld = null!;
    private Node3D _followTarget = null!;
    private MeshInstance3D _surface = null!;
    private PlaneMesh _surfaceMesh = null!;
    private ShaderMaterial _waterMaterial = null!;
    private bool _warnedMissingShader;

    public override void _EnterTree()
    {
        SetProcess(true);
    }

    public override void _Ready()
    {
        ResolveReferences();
        EnsureSurface();
        ApplySurfaceSettings();
        UpdateSurfaceTransform();
    }

    public override void _Process(double delta)
    {
        ResolveReferences();
        EnsureSurface();
        ApplySurfaceSettings();
        UpdateSurfaceTransform();
    }

    private void ResolveReferences()
    {
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetParent() as TerrainWorld;
        _followTarget =
            GetNodeOrNull<Node3D>(FollowTargetPath) ??
            ResolveTerrainTrackedCharacter() ??
            GetViewport()?.GetCamera3D();
    }

    private Node3D ResolveTerrainTrackedCharacter()
    {
        if (_terrainWorld == null || _terrainWorld.TrackedCharacterPath.IsEmpty)
        {
            return null;
        }

        return _terrainWorld.GetNodeOrNull<Node3D>(_terrainWorld.TrackedCharacterPath);
    }

    private void EnsureSurface()
    {
        _surface ??= GetNodeOrNull<MeshInstance3D>(SurfaceNodeName);
        if (_surface == null)
        {
            _surface = new MeshInstance3D
            {
                Name = SurfaceNodeName,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(_surface);
            if (Engine.IsEditorHint())
            {
                _surface.Owner = GetTree().EditedSceneRoot;
            }
        }

        _surfaceMesh = _surface.Mesh as PlaneMesh;
        if (_surfaceMesh == null)
        {
            _surfaceMesh = new PlaneMesh
            {
                Size = new Vector2(SurfaceSize, SurfaceSize),
                SubdivideDepth = 1,
                SubdivideWidth = 1
            };
            _surface.Mesh = _surfaceMesh;
        }

        _waterMaterial = _surface.MaterialOverride as ShaderMaterial;
        if (_waterMaterial == null)
        {
            Shader waterShader = ResourceLoader.Load<Shader>(WaterShaderPath);
            if (waterShader == null)
            {
                if (!_warnedMissingShader)
                {
                    GD.PushWarning($"TerrainWaterSurface could not load water shader at {WaterShaderPath}.");
                    _warnedMissingShader = true;
                }

                return;
            }

            _warnedMissingShader = false;
            _waterMaterial = new ShaderMaterial
            {
                ResourceLocalToScene = true,
                Shader = waterShader
            };
            _surface.MaterialOverride = _waterMaterial;
        }
    }

    private void ApplySurfaceSettings()
    {
        if (_surface == null || _surfaceMesh == null || _waterMaterial == null)
        {
            return;
        }

        float size = Mathf.Max(128.0f, SurfaceSize);
        _surfaceMesh.Size = new Vector2(size, size);
        _surface.ExtraCullMargin = size * 0.65f;

        _waterMaterial.SetShaderParameter("shallow_color", ShallowColor);
        _waterMaterial.SetShaderParameter("deep_color", DeepColor);
        _waterMaterial.SetShaderParameter("shore_color", ShoreColor);
        _waterMaterial.SetShaderParameter("fresnel_color", FresnelColor);
        _waterMaterial.SetShaderParameter("shallow_depth", Mathf.Max(0.05f, ShallowDepth));
        _waterMaterial.SetShaderParameter("deep_depth", Mathf.Max(ShallowDepth + 0.1f, DeepDepth));
        _waterMaterial.SetShaderParameter("shore_fade_depth", Mathf.Max(0.05f, ShoreFadeDepth));
        _waterMaterial.SetShaderParameter("alpha_shallow", Mathf.Clamp(ShallowAlpha, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("alpha_deep", Mathf.Clamp(DeepAlpha, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("fresnel_strength", Mathf.Max(0.0f, FresnelStrength));
        _waterMaterial.SetShaderParameter("fresnel_power", Mathf.Max(0.01f, FresnelPower));
        _waterMaterial.SetShaderParameter("specular_strength", Mathf.Clamp(SpecularStrength, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("metallic_amount", Mathf.Clamp(Metallic, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("roughness_near", Mathf.Clamp(RoughnessNear, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("roughness_far", Mathf.Clamp(RoughnessFar, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("shore_glow", Mathf.Clamp(ShoreGlow, 0.0f, 1.0f));
        _waterMaterial.SetShaderParameter("macro_tint_strength", Mathf.Clamp(MacroTintStrength, 0.0f, 0.25f));
    }

    private void UpdateSurfaceTransform()
    {
        if (_surface == null)
        {
            return;
        }

        float waterLevel = (_terrainWorld?.WaterLevel ?? 0.0f) + SurfaceLevelOffset;
        Vector3 center = FollowViewer && _followTarget != null
            ? _followTarget.GlobalTransform.Origin
            : GlobalTransform.Origin;

        float snapStep = Mathf.Max(0.0f, CenterSnapStep);
        float snappedX = snapStep > 0.001f
            ? Mathf.Snapped(center.X, snapStep)
            : center.X;
        float snappedZ = snapStep > 0.001f
            ? Mathf.Snapped(center.Z, snapStep)
            : center.Z;

        GlobalTransform = new Transform3D(
            Basis.Identity,
            new Vector3(snappedX, waterLevel, snappedZ));
    }
}
