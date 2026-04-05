using Godot;

namespace TowerOfBaby.Terrain;

internal static class TerrainSurfaceMaterialLibrary
{
    private const string TerrainTriplanarShaderPath = "res://shaders/terrain/TerrainTriplanar.gdshader";

    // Texture slots are semantic terrain roles; keep them bound to curated 2k assets only so the
    // runtime keeps the existing material/shader footprint while we retune biome appearance.
    private const string GrassLushAlbedoPath = "res://assets/terrain/textures/grass/forrest_ground_01_diff_2k.png";
    private const string GrassLushNormalPath = "res://assets/terrain/textures/grass/forrest_ground_01_nor_gl_2k.png";
    private const string GrassPathAlbedoPath = "res://assets/terrain/textures/grass/grass_path_3_diff_2k.png";
    private const string GrassPathNormalPath = "res://assets/terrain/textures/grass/grass_path_3_nor_gl_2k.png";
    private const string GrassDryAlbedoPath = "res://assets/terrain/textures/grass/grass_path_2_diff_2k.png";
    private const string GrassDryNormalPath = "res://assets/terrain/textures/grass/grass_path_2_nor_gl_2k.png";

    private const string DirtRegularAlbedoPath = "res://assets/terrain/textures/dirt/park_dirt_diff_2k.png";
    private const string DirtRegularNormalPath = "res://assets/terrain/textures/dirt/park_dirt_nor_gl_2k.png";
    private const string DirtMudAlbedoPath = "res://assets/terrain/textures/dirt/brown_mud_dry_diff_2k.png";
    private const string DirtMudNormalPath = "res://assets/terrain/textures/dirt/brown_mud_dry_nor_gl_2k.png";
    private const string DirtCrackedAlbedoPath = "res://assets/terrain/textures/dirt/cracked_red_ground_diff_2k.png";
    private const string DirtCrackedNormalPath = "res://assets/terrain/textures/dirt/cracked_red_ground_nor_gl_2k.png";

    private const string RockCliffAlbedoPath = "res://assets/terrain/textures/rock/rocky_terrain_03_diff_2k.png";
    private const string RockCliffNormalPath = "res://assets/terrain/textures/rock/rocky_terrain_03_nor_gl_2k.png";
    private const string RockGravelAlbedoPath = "res://assets/terrain/textures/rock/rocky_gravel_diff_2k.png";
    private const string RockGravelNormalPath = "res://assets/terrain/textures/rock/rocky_gravel_nor_gl_2k.png";
    private const string RockMossyAlbedoPath = "res://assets/terrain/textures/rock/mossy_rock_diff_2k.png";
    private const string RockMossyNormalPath = "res://assets/terrain/textures/rock/mossy_rock_nor_gl_2k.png";

    private const string SandShoreAlbedoPath = "res://assets/terrain/textures/sand/coast_sand_01_diff_2k.png";
    private const string SandShoreNormalPath = "res://assets/terrain/textures/sand/coast_sand_01_nor_gl_2k.png";
    private const string SandCoastAlbedoPath = "res://assets/terrain/textures/sand/coast_sand_02_diff_2k.png";
    private const string SandCoastNormalPath = "res://assets/terrain/textures/sand/coast_sand_02_nor_gl_2k.png";

    private const float DefaultTextureTilingScale = 0.14f;
    private const float DefaultBlendSharpness = 2.6f;
    private const float DefaultGrassSlopeMax = 0.30f;
    private const float DefaultRockSlopeStart = 0.44f;
    private const float DefaultSandHeightBlend = 3.2f;
    private const float DefaultVertexTintStrength = 0.16f;
    private const float DefaultNormalDetailMaxDistance = 72.0f;
    private const float DefaultFlatNormalSkipSlope = 0.12f;
    private const float DefaultDistantModeStart = 140.0f;
    private const float DefaultBiomeVariationScale = 0.022f;

    private static readonly StandardMaterial3D SharedLitVertexColorFallbackMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedTintedLitVertexColorFallbackMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedUnshadedVertexColorMaterial = CreateUnshadedVertexColorMaterial();
    private static ShaderMaterial _sharedLitSurfaceMaterial = null!;
    private static ShaderMaterial _sharedTintedLitSurfaceMaterial = null!;
    private static bool _surfaceMaterialsInitialized;
    private static bool _warnedMissingTerrainMaterialResources;
    private static float _sharedRoughness = 1.0f;
    private static float _sharedWaterLevel = -2.6f;
    private static float _sharedTextureTilingScale = DefaultTextureTilingScale;
    private static float _sharedBlendSharpness = DefaultBlendSharpness;
    private static float _sharedGrassSlopeMax = DefaultGrassSlopeMax;
    private static float _sharedRockSlopeStart = DefaultRockSlopeStart;
    private static float _sharedNormalDetailMaxDistance = DefaultNormalDetailMaxDistance;
    private static float _sharedFlatNormalSkipSlope = DefaultFlatNormalSkipSlope;
    private static float _sharedDistantModeStart = DefaultDistantModeStart;

    public static Material LitSurfaceMaterial => ResolveSurfaceMaterial(tinted: false);
    public static Material TintedLitSurfaceMaterial => ResolveSurfaceMaterial(tinted: true);
    public static StandardMaterial3D UnshadedVertexColorMaterial => SharedUnshadedVertexColorMaterial;

    public static void ConfigureSharedSurfaceRoughness(float roughness)
    {
        _sharedRoughness = Mathf.Clamp(roughness, 0.0f, 1.0f);
        SharedLitVertexColorFallbackMaterial.Roughness = _sharedRoughness;
        SharedTintedLitVertexColorFallbackMaterial.Roughness = _sharedRoughness;
        ApplySharedSurfaceSettings();
    }

    public static void ConfigureSharedWaterLevel(float waterLevel)
    {
        _sharedWaterLevel = waterLevel;
        ApplySharedSurfaceSettings();
    }

    public static void ConfigureSharedTerrainBlend(
        float textureTilingScale,
        float blendSharpness,
        float grassSlopeMax,
        float rockSlopeStart)
    {
        _sharedTextureTilingScale = Mathf.Max(0.01f, textureTilingScale);
        _sharedBlendSharpness = Mathf.Max(0.5f, blendSharpness);
        _sharedGrassSlopeMax = Mathf.Clamp(grassSlopeMax, 0.0f, 0.94f);
        _sharedRockSlopeStart = Mathf.Clamp(rockSlopeStart, _sharedGrassSlopeMax + 0.02f, 1.0f);
        ApplySharedSurfaceSettings();
    }

    public static void ConfigureSharedTerrainOptimization(
        float normalDetailMaxDistance,
        float flatNormalSkipSlope,
        float distantModeStart)
    {
        _sharedNormalDetailMaxDistance = Mathf.Max(1.0f, normalDetailMaxDistance);
        _sharedFlatNormalSkipSlope = Mathf.Clamp(flatNormalSkipSlope, 0.0f, 1.0f);
        _sharedDistantModeStart = Mathf.Max(1.0f, distantModeStart);
        ApplySharedSurfaceSettings();
    }

    private static StandardMaterial3D CreateLitVertexColorMaterial()
    {
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            Roughness = 1.0f,
            Metallic = 0.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
        };
    }

    private static StandardMaterial3D CreateUnshadedVertexColorMaterial()
    {
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            Roughness = 1.0f,
            Metallic = 0.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
    }

    private static Material ResolveSurfaceMaterial(bool tinted)
    {
        EnsureSurfaceMaterials();
        if (tinted)
        {
            return _sharedTintedLitSurfaceMaterial != null
                ? _sharedTintedLitSurfaceMaterial
                : SharedTintedLitVertexColorFallbackMaterial;
        }

        return _sharedLitSurfaceMaterial != null
            ? _sharedLitSurfaceMaterial
            : SharedLitVertexColorFallbackMaterial;
    }

    private static void EnsureSurfaceMaterials()
    {
        if (_surfaceMaterialsInitialized)
        {
            return;
        }

        _surfaceMaterialsInitialized = true;

        Shader terrainShader = ResourceLoader.Load<Shader>(TerrainTriplanarShaderPath);
        if (terrainShader == null)
        {
            WarnMissingTerrainMaterialResources($"Terrain surface shader missing at {TerrainTriplanarShaderPath}; using vertex-color fallback.");
            return;
        }

        if (!TryLoadTexture(GrassLushAlbedoPath, out Texture2D grassLushAlbedo) ||
            !TryLoadTexture(GrassLushNormalPath, out Texture2D grassLushNormal) ||
            !TryLoadTexture(GrassPathAlbedoPath, out Texture2D grassPathAlbedo) ||
            !TryLoadTexture(GrassPathNormalPath, out Texture2D grassPathNormal) ||
            !TryLoadTexture(GrassDryAlbedoPath, out Texture2D grassDryAlbedo) ||
            !TryLoadTexture(GrassDryNormalPath, out Texture2D grassDryNormal) ||
            !TryLoadTexture(DirtRegularAlbedoPath, out Texture2D dirtRegularAlbedo) ||
            !TryLoadTexture(DirtRegularNormalPath, out Texture2D dirtRegularNormal) ||
            !TryLoadTexture(DirtMudAlbedoPath, out Texture2D dirtMudAlbedo) ||
            !TryLoadTexture(DirtMudNormalPath, out Texture2D dirtMudNormal) ||
            !TryLoadTexture(DirtCrackedAlbedoPath, out Texture2D dirtCrackedAlbedo) ||
            !TryLoadTexture(DirtCrackedNormalPath, out Texture2D dirtCrackedNormal) ||
            !TryLoadTexture(RockCliffAlbedoPath, out Texture2D rockCliffAlbedo) ||
            !TryLoadTexture(RockCliffNormalPath, out Texture2D rockCliffNormal) ||
            !TryLoadTexture(RockGravelAlbedoPath, out Texture2D rockGravelAlbedo) ||
            !TryLoadTexture(RockGravelNormalPath, out Texture2D rockGravelNormal) ||
            !TryLoadTexture(RockMossyAlbedoPath, out Texture2D rockMossyAlbedo) ||
            !TryLoadTexture(RockMossyNormalPath, out Texture2D rockMossyNormal) ||
            !TryLoadTexture(SandShoreAlbedoPath, out Texture2D sandShoreAlbedo) ||
            !TryLoadTexture(SandShoreNormalPath, out Texture2D sandShoreNormal) ||
            !TryLoadTexture(SandCoastAlbedoPath, out Texture2D sandCoastAlbedo) ||
            !TryLoadTexture(SandCoastNormalPath, out Texture2D sandCoastNormal))
        {
            WarnMissingTerrainMaterialResources("Terrain biome texture variants missing; using vertex-color fallback.");
            return;
        }

        TerrainTextureSet textures = new(
            grassLushAlbedo,
            grassLushNormal,
            grassPathAlbedo,
            grassPathNormal,
            grassDryAlbedo,
            grassDryNormal,
            dirtRegularAlbedo,
            dirtRegularNormal,
            dirtMudAlbedo,
            dirtMudNormal,
            dirtCrackedAlbedo,
            dirtCrackedNormal,
            rockCliffAlbedo,
            rockCliffNormal,
            rockGravelAlbedo,
            rockGravelNormal,
            rockMossyAlbedo,
            rockMossyNormal,
            sandShoreAlbedo,
            sandShoreNormal,
            sandCoastAlbedo,
            sandCoastNormal);

        _sharedLitSurfaceMaterial = CreateTriplanarMaterial(terrainShader, textures, vertexTintStrength: 0.0f);
        _sharedTintedLitSurfaceMaterial = CreateTriplanarMaterial(terrainShader, textures, vertexTintStrength: DefaultVertexTintStrength);
        ApplySharedSurfaceSettings();
    }

    private static ShaderMaterial CreateTriplanarMaterial(
        Shader terrainShader,
        TerrainTextureSet textures,
        float vertexTintStrength)
    {
        ShaderMaterial material = new()
        {
            Shader = terrainShader
        };

        material.SetShaderParameter("grass_lush_albedo", textures.GrassLushAlbedo);
        material.SetShaderParameter("grass_lush_normal", textures.GrassLushNormal);
        material.SetShaderParameter("grass_path_albedo", textures.GrassPathAlbedo);
        material.SetShaderParameter("grass_path_normal", textures.GrassPathNormal);
        material.SetShaderParameter("grass_dry_albedo", textures.GrassDryAlbedo);
        material.SetShaderParameter("grass_dry_normal", textures.GrassDryNormal);

        material.SetShaderParameter("dirt_regular_albedo", textures.DirtRegularAlbedo);
        material.SetShaderParameter("dirt_regular_normal", textures.DirtRegularNormal);
        material.SetShaderParameter("dirt_mud_albedo", textures.DirtMudAlbedo);
        material.SetShaderParameter("dirt_mud_normal", textures.DirtMudNormal);
        material.SetShaderParameter("dirt_cracked_albedo", textures.DirtCrackedAlbedo);
        material.SetShaderParameter("dirt_cracked_normal", textures.DirtCrackedNormal);

        material.SetShaderParameter("rock_cliff_albedo", textures.RockCliffAlbedo);
        material.SetShaderParameter("rock_cliff_normal", textures.RockCliffNormal);
        material.SetShaderParameter("rock_gravel_albedo", textures.RockGravelAlbedo);
        material.SetShaderParameter("rock_gravel_normal", textures.RockGravelNormal);
        material.SetShaderParameter("rock_mossy_albedo", textures.RockMossyAlbedo);
        material.SetShaderParameter("rock_mossy_normal", textures.RockMossyNormal);

        material.SetShaderParameter("sand_shore_albedo", textures.SandShoreAlbedo);
        material.SetShaderParameter("sand_shore_normal", textures.SandShoreNormal);
        material.SetShaderParameter("sand_coast_albedo", textures.SandCoastAlbedo);
        material.SetShaderParameter("sand_coast_normal", textures.SandCoastNormal);

        material.SetShaderParameter("sand_height_blend", DefaultSandHeightBlend);
        material.SetShaderParameter("biome_variation_scale", DefaultBiomeVariationScale);
        material.SetShaderParameter("vertex_tint_strength", vertexTintStrength);
        material.SetShaderParameter("material_tint", Colors.White);
        return material;
    }

    private static void ApplySharedSurfaceSettings()
    {
        ApplySharedSurfaceSettings(_sharedLitSurfaceMaterial);
        ApplySharedSurfaceSettings(_sharedTintedLitSurfaceMaterial);
    }

    private static void ApplySharedSurfaceSettings(ShaderMaterial material)
    {
        if (material == null)
        {
            return;
        }

        material.SetShaderParameter("texture_tiling_scale", _sharedTextureTilingScale);
        material.SetShaderParameter("blend_sharpness", _sharedBlendSharpness);
        material.SetShaderParameter("grass_slope_max", _sharedGrassSlopeMax);
        material.SetShaderParameter("rock_slope_start", _sharedRockSlopeStart);
        material.SetShaderParameter("water_level", _sharedWaterLevel);
        material.SetShaderParameter("terrain_roughness", _sharedRoughness);
        material.SetShaderParameter("normal_detail_max_distance", _sharedNormalDetailMaxDistance);
        material.SetShaderParameter("flat_normal_skip_slope", _sharedFlatNormalSkipSlope);
        material.SetShaderParameter("distant_mode_start", _sharedDistantModeStart);
    }

    private static bool TryLoadTexture(string resourcePath, out Texture2D texture)
    {
        texture = ResourceLoader.Load<Texture2D>(resourcePath);
        if (texture != null)
        {
            return true;
        }

        WarnMissingTerrainMaterialResources($"Terrain surface texture missing at {resourcePath}; using vertex-color fallback.");
        return false;
    }

    private static void WarnMissingTerrainMaterialResources(string message)
    {
        if (_warnedMissingTerrainMaterialResources)
        {
            return;
        }

        GD.PushWarning(message);
        _warnedMissingTerrainMaterialResources = true;
    }

    private readonly record struct TerrainTextureSet(
        Texture2D GrassLushAlbedo,
        Texture2D GrassLushNormal,
        Texture2D GrassPathAlbedo,
        Texture2D GrassPathNormal,
        Texture2D GrassDryAlbedo,
        Texture2D GrassDryNormal,
        Texture2D DirtRegularAlbedo,
        Texture2D DirtRegularNormal,
        Texture2D DirtMudAlbedo,
        Texture2D DirtMudNormal,
        Texture2D DirtCrackedAlbedo,
        Texture2D DirtCrackedNormal,
        Texture2D RockCliffAlbedo,
        Texture2D RockCliffNormal,
        Texture2D RockGravelAlbedo,
        Texture2D RockGravelNormal,
        Texture2D RockMossyAlbedo,
        Texture2D RockMossyNormal,
        Texture2D SandShoreAlbedo,
        Texture2D SandShoreNormal,
        Texture2D SandCoastAlbedo,
        Texture2D SandCoastNormal);
}
