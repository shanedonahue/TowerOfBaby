using Godot;

namespace TowerOfBaby.Terrain;

internal static class TerrainSurfaceMaterialLibrary
{
    private const string TerrainTriplanarShaderPath = "res://shaders/terrain/TerrainTriplanar.gdshader";
    private const string GrassAlbedoPath = "res://assets/terrain/textures/grass/forrest_ground_01_diff_4k.png";
    private const string GrassNormalPath = "res://assets/terrain/textures/grass/forrest_ground_01_nor_gl_4k.png";
    private const string DirtAlbedoPath = "res://assets/terrain/textures/dirt/dirt_floor_diff_4k.png";
    private const string DirtNormalPath = "res://assets/terrain/textures/dirt/dirt_floor_nor_gl_4k.png";
    private const string RockAlbedoPath = "res://assets/terrain/textures/rock/rocky_terrain_02_diff_4k.png";
    private const string RockNormalPath = "res://assets/terrain/textures/rock/rocky_terrain_02_nor_gl_4k.png";
    private const string SandAlbedoPath = "res://assets/terrain/textures/sand/coast_sand_rocks_02_diff_4k.png";
    private const string SandNormalPath = "res://assets/terrain/textures/sand/coast_sand_rocks_02_nor_gl_4k.png";
    private const float DefaultTextureTilingScale = 0.18f;
    private const float DefaultBlendSharpness = 3.4f;
    private const float DefaultGrassSlopeMax = 0.18f;
    private const float DefaultRockSlopeStart = 0.56f;
    private const float DefaultSandHeightBlend = 2.25f;
    private const float DefaultVertexTintStrength = 0.38f;
    private const float DefaultNormalDetailMaxDistance = 72.0f;
    private const float DefaultFlatNormalSkipSlope = 0.12f;
    private const float DefaultDistantModeStart = 140.0f;

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

        if (!TryLoadTexture(GrassAlbedoPath, out Texture2D grassAlbedo) ||
            !TryLoadTexture(GrassNormalPath, out Texture2D grassNormal) ||
            !TryLoadTexture(DirtAlbedoPath, out Texture2D dirtAlbedo) ||
            !TryLoadTexture(DirtNormalPath, out Texture2D dirtNormal) ||
            !TryLoadTexture(RockAlbedoPath, out Texture2D rockAlbedo) ||
            !TryLoadTexture(RockNormalPath, out Texture2D rockNormal) ||
            !TryLoadTexture(SandAlbedoPath, out Texture2D sandAlbedo) ||
            !TryLoadTexture(SandNormalPath, out Texture2D sandNormal))
        {
            WarnMissingTerrainMaterialResources("Terrain triplanar textures missing; using vertex-color fallback.");
            return;
        }

        _sharedLitSurfaceMaterial = CreateTriplanarMaterial(
            terrainShader,
            grassAlbedo,
            grassNormal,
            dirtAlbedo,
            dirtNormal,
            rockAlbedo,
            rockNormal,
            sandAlbedo,
            sandNormal,
            vertexTintStrength: 0.0f);
        _sharedTintedLitSurfaceMaterial = CreateTriplanarMaterial(
            terrainShader,
            grassAlbedo,
            grassNormal,
            dirtAlbedo,
            dirtNormal,
            rockAlbedo,
            rockNormal,
            sandAlbedo,
            sandNormal,
            vertexTintStrength: DefaultVertexTintStrength);
        ApplySharedSurfaceSettings();
    }

    private static ShaderMaterial CreateTriplanarMaterial(
        Shader terrainShader,
        Texture2D grassAlbedo,
        Texture2D grassNormal,
        Texture2D dirtAlbedo,
        Texture2D dirtNormal,
        Texture2D rockAlbedo,
        Texture2D rockNormal,
        Texture2D sandAlbedo,
        Texture2D sandNormal,
        float vertexTintStrength)
    {
        ShaderMaterial material = new()
        {
            Shader = terrainShader
        };
        material.SetShaderParameter("grass_albedo", grassAlbedo);
        material.SetShaderParameter("grass_normal", grassNormal);
        material.SetShaderParameter("dirt_albedo", dirtAlbedo);
        material.SetShaderParameter("dirt_normal", dirtNormal);
        material.SetShaderParameter("rock_albedo", rockAlbedo);
        material.SetShaderParameter("rock_normal", rockNormal);
        material.SetShaderParameter("sand_albedo", sandAlbedo);
        material.SetShaderParameter("sand_normal", sandNormal);
        material.SetShaderParameter("sand_height_blend", DefaultSandHeightBlend);
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
}
