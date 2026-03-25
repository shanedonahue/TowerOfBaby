using Godot;

namespace TowerOfBaby.World;

public partial class TimeOfDayController : Node
{
    [Export] public NodePath WorldEnvironmentPath = new("../WorldEnvironment");
    [Export] public NodePath SunLightPath = new("../DirectionalLight3D");
    [Export] public NodePath FillLightPath = new("../FillLight");
    [Export] public NodePath MoonLightPath = new("../MoonLight");
    [Export] public NodePath MoonVisualPath = new("../MoonVisual");
    [Export] public float DayLengthSeconds = 180.0f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float TimeOfDay = 0.28f;
    [Export] public bool AutoAdvance = true;

    private WorldEnvironment _worldEnvironment = null!;
    private DirectionalLight3D _sunLight = null!;
    private DirectionalLight3D _fillLight = null!;
    private DirectionalLight3D _moonLight = null!;
    private Sprite3D _moonVisual = null!;
    private Environment _environment = null!;
    private ProceduralSkyMaterial _skyMaterial = null!;

    public override void _Ready()
    {
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath) ?? GetParent().GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath) ?? GetParent().GetNodeOrNull<DirectionalLight3D>("DirectionalLight3D");
        _fillLight = GetNodeOrNull<DirectionalLight3D>(FillLightPath) ?? GetParent().GetNodeOrNull<DirectionalLight3D>("FillLight");
        _moonLight = GetNodeOrNull<DirectionalLight3D>(MoonLightPath) ?? GetParent().GetNodeOrNull<DirectionalLight3D>("MoonLight");
        _moonVisual = GetNodeOrNull<Sprite3D>(MoonVisualPath) ?? GetParent().GetNodeOrNull<Sprite3D>("MoonVisual");

        _environment = _worldEnvironment?.Environment;
        _skyMaterial = _environment?.Sky?.SkyMaterial as ProceduralSkyMaterial;
        ApplyLighting();
    }

    public override void _Process(double delta)
    {
        if (AutoAdvance && DayLengthSeconds > 0.0f)
        {
            TimeOfDay = Mathf.PosMod(TimeOfDay + ((float)delta / DayLengthSeconds), 1.0f);
        }

        ApplyLighting();
    }

    private void ApplyLighting()
    {
        if (_environment == null || _skyMaterial == null || _sunLight == null || _fillLight == null)
        {
            return;
        }

        float sunAngle = (TimeOfDay * Mathf.Tau) - (Mathf.Pi * 0.5f);
        float dayAmount = Mathf.Clamp(Mathf.Sin(sunAngle) * 0.5f + 0.5f, 0.0f, 1.0f);
        float daylight = Mathf.SmoothStep(0.08f, 0.9f, dayAmount);
        float nightAmount = 1.0f - daylight;
        float dusk = 1.0f - Mathf.Abs((TimeOfDay - 0.75f) * 4.0f);
        dusk = Mathf.Clamp(dusk, 0.0f, 1.0f);
        float dawn = 1.0f - Mathf.Abs((TimeOfDay - 0.25f) * 4.0f);
        dawn = Mathf.Clamp(dawn, 0.0f, 1.0f);
        float horizonWarmth = Mathf.Max(dawn, dusk);

        _sunLight.Rotation = new Vector3(
            Mathf.Lerp(0.2f, -0.95f, dayAmount),
            TimeOfDay * Mathf.Tau,
            0.0f);
        _fillLight.Rotation = new Vector3(
            Mathf.Lerp(0.45f, -0.25f, dayAmount),
            (TimeOfDay * Mathf.Tau) + Mathf.Pi,
            0.0f);
        if (_moonLight != null)
        {
            _moonLight.Rotation = new Vector3(
                Mathf.Lerp(0.2f, -0.95f, 1.0f - dayAmount),
                (TimeOfDay * Mathf.Tau) + Mathf.Pi,
                0.0f);
        }

        _sunLight.LightEnergy = Mathf.Lerp(0.04f, 1.75f, daylight);
        _sunLight.LightIndirectEnergy = Mathf.Lerp(0.02f, 0.35f, daylight);
        _sunLight.LightColor = new Color(0.92f, 0.95f, 1.0f).Lerp(new Color(1.0f, 0.86f, 0.62f), horizonWarmth * 0.9f);

        _fillLight.LightEnergy = Mathf.Lerp(0.03f, 0.22f, daylight);
        _fillLight.LightColor = new Color(0.18f, 0.25f, 0.4f).Lerp(new Color(0.56f, 0.72f, 0.96f), daylight);
        if (_moonLight != null)
        {
            _moonLight.LightEnergy = Mathf.Lerp(0.0f, 0.22f, nightAmount);
            _moonLight.LightIndirectEnergy = Mathf.Lerp(0.0f, 0.08f, nightAmount);
            _moonLight.LightColor = new Color(0.62f, 0.72f, 0.95f);
        }

        _environment.AmbientLightSkyContribution = Mathf.Lerp(0.18f, 0.72f, daylight);
        _environment.TonemapExposure = Mathf.Lerp(0.38f, 1.18f, daylight);
        _environment.FogDensity = Mathf.Lerp(0.078f, 0.112f, 1.0f - daylight);
        _environment.FogDepthBegin = Mathf.Lerp(8.0f, 14.0f, daylight);
        _environment.FogDepthEnd = Mathf.Lerp(44.0f, 64.0f, daylight);
        _environment.FogDepthCurve = Mathf.Lerp(2.8f, 2.2f, daylight);
        _environment.FogAerialPerspective = Mathf.Lerp(0.84f, 0.72f, daylight);
        _environment.FogSkyAffect = Mathf.Lerp(0.86f, 0.72f, daylight);

        _skyMaterial.SkyTopColor = new Color(0.03f, 0.05f, 0.12f).Lerp(new Color(0.34f, 0.68f, 0.98f), daylight);
        _skyMaterial.SkyHorizonColor = new Color(0.12f, 0.13f, 0.17f).Lerp(new Color(0.64f, 0.76f, 0.84f), daylight);
        _skyMaterial.SkyHorizonColor = _skyMaterial.SkyHorizonColor.Lerp(new Color(0.94f, 0.72f, 0.5f), horizonWarmth * 0.38f);

        // Keep the "below the world" color close to the terrain palette so the horizon
        // doesn't read as a muddy brown seam.
        _skyMaterial.GroundHorizonColor = new Color(0.12f, 0.14f, 0.16f).Lerp(new Color(0.56f, 0.68f, 0.74f), daylight * 0.9f);
        _skyMaterial.GroundBottomColor = new Color(0.05f, 0.06f, 0.08f).Lerp(new Color(0.22f, 0.28f, 0.34f), daylight * 0.72f);

        if (_moonVisual != null)
        {
            Vector3 moonDirection = new Vector3(0.0f, 0.0f, -1.0f)
                .Rotated(Vector3.Right, Mathf.Lerp(0.2f, -0.95f, 1.0f - dayAmount))
                .Rotated(Vector3.Up, (TimeOfDay * Mathf.Tau) + Mathf.Pi);
            _moonVisual.GlobalPosition = GetParent<Node3D>().GlobalPosition + (moonDirection * 140.0f);
            _moonVisual.LookAt(_worldEnvironment.GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero, Vector3.Up);
            _moonVisual.Modulate = new Color(0.86f, 0.9f, 1.0f, Mathf.Lerp(0.0f, 0.92f, nightAmount));
            _moonVisual.Visible = nightAmount > 0.08f;
        }
    }
}
