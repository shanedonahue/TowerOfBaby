using Godot;

namespace TowerOfBaby.Terrain;

internal static class TerrainSurfaceMaterialLibrary
{
    private static readonly StandardMaterial3D SharedLitVertexColorMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedTintedLitVertexColorMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedUnshadedVertexColorMaterial = CreateUnshadedVertexColorMaterial();

    public static StandardMaterial3D LitVertexColorMaterial => SharedLitVertexColorMaterial;
    public static StandardMaterial3D TintedLitVertexColorMaterial => SharedTintedLitVertexColorMaterial;
    public static StandardMaterial3D UnshadedVertexColorMaterial => SharedUnshadedVertexColorMaterial;

    public static void ConfigureSharedSurfaceRoughness(float roughness)
    {
        float clampedRoughness = Mathf.Clamp(roughness, 0.0f, 1.0f);
        SharedLitVertexColorMaterial.Roughness = clampedRoughness;
        SharedTintedLitVertexColorMaterial.Roughness = clampedRoughness;
    }

    // TODO: Optional future terrain shader path belongs here.
    // - Add a triplanar ShaderMaterial with 4 material slots: grass, dirt, sand/shore, cliff rock.
    // - Blend by slope + height, and keep current vertex colors as tint masks / macro variation input.
    // - Preserve this StandardMaterial3D vertex-color fallback when shader resources are missing or disabled.
    // Integration notes:
    // - Reference walterpalladino/godot-shaders: simple terrain shader, simple triplanar shader, stylized grass shader.
    // - Reference jotson/godot3-triplanar-terrain-demo for triplanar/cliff mapping behavior.
    // - Place Poly Haven / ambientCG sets under assets/terrain/textures/grass/Grass004,
    //   assets/terrain/textures/dirt/Ground029, assets/terrain/textures/shore/CoastSandRocks02,
    //   and assets/terrain/textures/cliff/RockyTerrain02 or equivalent dirt/rock variants.

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
}
