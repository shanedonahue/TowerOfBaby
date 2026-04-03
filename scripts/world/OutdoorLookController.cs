using Godot;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.World;

[Tool]
public partial class OutdoorLookController : Node
{
    [ExportGroup("Nodes")]
    [Export] public NodePath WorldEnvironmentPath = new("../WorldEnvironment");
    [Export] public NodePath SunLightPath = new("../SunLight");

    [ExportGroup("Sun Direction")]
    [Export(PropertyHint.Range, "0,360,0.1")] public float SunAzimuthDegrees = 34.0f;
    [Export(PropertyHint.Range, "5,85,0.1")] public float SunElevationDegrees = 48.0f;

    [ExportGroup("Sun Lighting")]
    [Export] public Color SunColor = new(1.0f, 0.95f, 0.84f, 1.0f);
    [Export(PropertyHint.Range, "0,8,0.01")] public float SunEnergy = 1.35f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float SunIndirectEnergy = 0.10f;
    [Export] public bool EnableSunShadows = true;

    [ExportGroup("Sky")]
    [Export] public Color SkyTopColor = new(0.23f, 0.55f, 0.90f, 1.0f);
    [Export] public Color SkyHorizonColor = new(0.78f, 0.90f, 1.0f, 1.0f);
    [Export] public Color GroundHorizonColor = new(0.48f, 0.57f, 0.62f, 1.0f);
    [Export] public Color GroundBottomColor = new(0.21f, 0.24f, 0.28f, 1.0f);
    [Export(PropertyHint.Range, "0,90,0.1")] public float SunDiscAngle = 18.0f;

    [ExportGroup("Environment")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float AmbientSkyContribution = 0.36f;
    [Export(PropertyHint.Range, "0,3,0.01")] public float TonemapExposure = 0.92f;
    [Export] public bool ForceLegacyImageEffectsOff = true;

    [ExportGroup("Atmosphere")]
    [Export(PropertyHint.Range, "0,1,0.001")] public float FogDensity = 0.032f;
    [Export(PropertyHint.Range, "0,512,1")] public float FogDepthBegin = 72.0f;
    [Export(PropertyHint.Range, "1,512,1")] public float FogDepthEnd = 340.0f;
    [Export(PropertyHint.Range, "0.1,8,0.01")] public float FogDepthCurve = 1.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogAerialPerspective = 0.68f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogSkyAffect = 0.78f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float FogSunScatter = 0.05f;

    [ExportGroup("Terrain Material")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float TerrainRoughness = 1.0f;
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float TerrainTextureTilingScale = 0.18f;
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float TerrainBlendSharpness = 3.4f;
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float TerrainGrassSlopeMax = 0.18f;
    [Export(PropertyHint.Range, "0.1,1,0.01")] public float TerrainRockSlopeStart = 0.56f;
    [Export(PropertyHint.Range, "8,256,1")] public float TerrainNormalDetailMaxDistance = 72.0f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float TerrainFlatNormalSkipSlope = 0.12f;
    [Export(PropertyHint.Range, "16,512,1")] public float TerrainDistantModeStart = 140.0f;

    private WorldEnvironment _worldEnvironment = null!;
    private DirectionalLight3D _sunLight = null!;
    private Environment _environment = null!;
    private ProceduralSkyMaterial _skyMaterial = null!;
    private bool _warnedMissingRig;

    public override void _EnterTree()
    {
        SetProcess(true);
    }

    public override void _Ready()
    {
        ApplyLook();
    }

    public override void _Process(double delta)
    {
        // Tool mode lets inspector tweaks immediately restyle the scene without rerunning.
        if (Engine.IsEditorHint())
        {
            ApplyLook();
        }
    }

    private void ApplyLook()
    {
        if (!EnsureRig())
        {
            return;
        }

        float clampedElevation = Mathf.Clamp(SunElevationDegrees, 0.0f, 89.9f);
        _sunLight.RotationDegrees = new Vector3(-clampedElevation, SunAzimuthDegrees, 0.0f);
        _sunLight.LightColor = SunColor;
        _sunLight.LightEnergy = Mathf.Max(0.0f, SunEnergy);
        _sunLight.LightIndirectEnergy = Mathf.Max(0.0f, SunIndirectEnergy);
        _sunLight.ShadowEnabled = EnableSunShadows;

        if (ForceLegacyImageEffectsOff)
        {
            _environment.SsaoEnabled = false;
            _environment.SsilEnabled = false;
        }

        float fogBegin = Mathf.Max(0.0f, FogDepthBegin);
        float fogEnd = Mathf.Max(fogBegin + 0.1f, FogDepthEnd);
        _environment.AmbientLightSkyContribution = Mathf.Clamp(AmbientSkyContribution, 0.0f, 1.0f);
        _environment.TonemapExposure = Mathf.Max(0.0f, TonemapExposure);
        _environment.FogDensity = Mathf.Max(0.0f, FogDensity);
        _environment.FogDepthBegin = fogBegin;
        _environment.FogDepthEnd = fogEnd;
        _environment.FogDepthCurve = Mathf.Max(0.01f, FogDepthCurve);
        _environment.FogAerialPerspective = Mathf.Clamp(FogAerialPerspective, 0.0f, 1.0f);
        _environment.FogSkyAffect = Mathf.Clamp(FogSkyAffect, 0.0f, 1.0f);
        _environment.FogSunScatter = Mathf.Clamp(FogSunScatter, 0.0f, 1.0f);

        _skyMaterial.SkyTopColor = SkyTopColor;
        _skyMaterial.SkyHorizonColor = SkyHorizonColor;
        _skyMaterial.GroundHorizonColor = GroundHorizonColor;
        _skyMaterial.GroundBottomColor = GroundBottomColor;
        _skyMaterial.SunAngleMax = Mathf.Clamp(SunDiscAngle, 0.0f, 90.0f);

        TerrainRenderer.ConfigureSharedSurfaceMaterial(TerrainRoughness);
        TerrainChunk.ConfigureSharedSurfaceMaterial(TerrainRoughness);
        TerrainSurfaceMaterialLibrary.ConfigureSharedTerrainBlend(
            TerrainTextureTilingScale,
            TerrainBlendSharpness,
            TerrainGrassSlopeMax,
            TerrainRockSlopeStart);
        TerrainSurfaceMaterialLibrary.ConfigureSharedTerrainOptimization(
            TerrainNormalDetailMaxDistance,
            TerrainFlatNormalSkipSlope,
            TerrainDistantModeStart);
    }

    private bool EnsureRig()
    {
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath)
            ?? GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath)
            ?? GetParent()?.GetNodeOrNull<DirectionalLight3D>("SunLight");
        _environment = _worldEnvironment?.Environment;
        _skyMaterial = _environment?.Sky?.SkyMaterial as ProceduralSkyMaterial;

        bool ready = _worldEnvironment != null &&
                     _sunLight != null &&
                     _environment != null &&
                     _skyMaterial != null;
        if (ready)
        {
            _warnedMissingRig = false;
            return true;
        }

        if (!_warnedMissingRig)
        {
            GD.PushWarning(
                "OutdoorLookController requires a WorldEnvironment with an Environment/Sky/ProceduralSkyMaterial and a SunLight DirectionalLight3D.");
            _warnedMissingRig = true;
        }

        return false;
    }
}
