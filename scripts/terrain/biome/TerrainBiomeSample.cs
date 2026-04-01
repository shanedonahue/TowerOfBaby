using Godot;

namespace TowerOfBaby.Terrain;

public readonly record struct TerrainBiomeSample(
    BiomeId DominantBiome,
    float PlainsWeight,
    float RockyWeight,
    float CanyonWeight,
    float SwampWeight,
    float VolcanicWeight,
    float Heat,
    float Moisture,
    float Ruggedness,
    float Activity)
{
    public static TerrainBiomeSample Default =>
        CreateNormalized(1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.5f, 0.5f, 0.0f, 0.0f);

    public float GetWeight(BiomeId biome) => biome switch
    {
        BiomeId.Plains => PlainsWeight,
        BiomeId.Rocky => RockyWeight,
        BiomeId.Canyon => CanyonWeight,
        BiomeId.Swamp => SwampWeight,
        BiomeId.Volcanic => VolcanicWeight,
        _ => 0.0f
    };

    public Color DebugColor
    {
        get
        {
            Color plains = TerrainBiomeCatalog.Get(BiomeId.Plains).DebugColor;
            Color rocky = TerrainBiomeCatalog.Get(BiomeId.Rocky).DebugColor;
            Color canyon = TerrainBiomeCatalog.Get(BiomeId.Canyon).DebugColor;
            Color swamp = TerrainBiomeCatalog.Get(BiomeId.Swamp).DebugColor;
            Color volcanic = TerrainBiomeCatalog.Get(BiomeId.Volcanic).DebugColor;

            return new Color(
                (plains.R * PlainsWeight) + (rocky.R * RockyWeight) + (canyon.R * CanyonWeight) + (swamp.R * SwampWeight) + (volcanic.R * VolcanicWeight),
                (plains.G * PlainsWeight) + (rocky.G * RockyWeight) + (canyon.G * CanyonWeight) + (swamp.G * SwampWeight) + (volcanic.G * VolcanicWeight),
                (plains.B * PlainsWeight) + (rocky.B * RockyWeight) + (canyon.B * CanyonWeight) + (swamp.B * SwampWeight) + (volcanic.B * VolcanicWeight),
                1.0f);
        }
    }

    public string Summary =>
        $"{DominantBiome} p {PlainsWeight:0.00} r {RockyWeight:0.00} c {CanyonWeight:0.00} s {SwampWeight:0.00} v {VolcanicWeight:0.00}";

    public static TerrainBiomeSample CreateNormalized(
        float plainsWeight,
        float rockyWeight,
        float canyonWeight,
        float swampWeight,
        float volcanicWeight,
        float heat,
        float moisture,
        float ruggedness,
        float activity)
    {
        float plains = Mathf.Max(plainsWeight, 0.0001f);
        float rocky = Mathf.Max(rockyWeight, 0.0001f);
        float canyon = Mathf.Max(canyonWeight, 0.0001f);
        float swamp = Mathf.Max(swampWeight, 0.0001f);
        float volcanic = Mathf.Max(volcanicWeight, 0.0001f);

        float total = plains + rocky + canyon + swamp + volcanic;
        plains /= total;
        rocky /= total;
        canyon /= total;
        swamp /= total;
        volcanic /= total;

        return new TerrainBiomeSample(
            GetDominantBiome(plains, rocky, canyon, swamp, volcanic),
            plains,
            rocky,
            canyon,
            swamp,
            volcanic,
            Mathf.Clamp(heat, 0.0f, 1.0f),
            Mathf.Clamp(moisture, 0.0f, 1.0f),
            Mathf.Clamp(ruggedness, 0.0f, 1.0f),
            Mathf.Clamp(activity, 0.0f, 1.0f));
    }

    private static BiomeId GetDominantBiome(
        float plainsWeight,
        float rockyWeight,
        float canyonWeight,
        float swampWeight,
        float volcanicWeight)
    {
        BiomeId dominant = BiomeId.Plains;
        float strongest = plainsWeight;

        if (rockyWeight > strongest)
        {
            dominant = BiomeId.Rocky;
            strongest = rockyWeight;
        }

        if (canyonWeight > strongest)
        {
            dominant = BiomeId.Canyon;
            strongest = canyonWeight;
        }

        if (swampWeight > strongest)
        {
            dominant = BiomeId.Swamp;
            strongest = swampWeight;
        }

        if (volcanicWeight > strongest)
        {
            dominant = BiomeId.Volcanic;
        }

        return dominant;
    }
}
