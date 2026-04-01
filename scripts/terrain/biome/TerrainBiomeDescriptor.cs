using Godot;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainBiomeDescriptor(
    BiomeId Id,
    string DisplayName,
    Color DebugColor);

public static class TerrainBiomeCatalog
{
    public static TerrainBiomeDescriptor Get(BiomeId biome) => biome switch
    {
        BiomeId.Plains => new TerrainBiomeDescriptor(biome, "Plains", new Color(0.71f, 0.86f, 0.48f, 1.0f)),
        BiomeId.Rocky => new TerrainBiomeDescriptor(biome, "Rocky", new Color(0.67f, 0.66f, 0.62f, 1.0f)),
        BiomeId.Canyon => new TerrainBiomeDescriptor(biome, "Canyon", new Color(0.84f, 0.54f, 0.33f, 1.0f)),
        BiomeId.Swamp => new TerrainBiomeDescriptor(biome, "Swamp", new Color(0.38f, 0.61f, 0.43f, 1.0f)),
        BiomeId.Volcanic => new TerrainBiomeDescriptor(biome, "Volcanic", new Color(0.77f, 0.31f, 0.27f, 1.0f)),
        _ => new TerrainBiomeDescriptor(biome, biome.ToString(), Colors.White)
    };
}
