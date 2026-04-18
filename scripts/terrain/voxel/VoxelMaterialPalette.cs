using Godot;

namespace TowerOfBaby.Terrain.Voxel;

public static class VoxelMaterialPalette
{
    private const float MaterialEncodingSlotCount = 8.0f;
    private const float EncodedMaterialMaxAlpha = 0.90f;
    private static readonly int MaxEncodedMaterialIndex = System.Enum.GetValues<VoxelMaterialId>().Length - 1;

    public static Color GetTintColor(VoxelMaterialId materialId)
    {
        return materialId switch
        {
            VoxelMaterialId.Grass => new Color(0.39f, 0.55f, 0.28f),
            VoxelMaterialId.Rock => new Color(0.44f, 0.48f, 0.52f),
            VoxelMaterialId.Cliff => new Color(0.47f, 0.43f, 0.40f),
            VoxelMaterialId.Snow => new Color(0.90f, 0.93f, 0.97f),
            VoxelMaterialId.Scorched => new Color(0.18f, 0.16f, 0.15f),
            _ => new Color(0.58f, 0.39f, 0.22f)
        };
    }

    public static Color GetNeutralColor(VoxelMaterialId materialId)
    {
        return materialId switch
        {
            VoxelMaterialId.Grass => new Color(0.47f, 0.56f, 0.33f),
            VoxelMaterialId.Rock => new Color(0.53f, 0.51f, 0.49f),
            VoxelMaterialId.Cliff => new Color(0.61f, 0.55f, 0.41f),
            VoxelMaterialId.Snow => new Color(0.86f, 0.87f, 0.89f),
            VoxelMaterialId.Scorched => new Color(0.21f, 0.19f, 0.18f),
            _ => new Color(0.50f, 0.40f, 0.27f)
        };
    }

    public static Color EncodeMaterialColor(Color color, VoxelMaterialId materialId)
    {
        return new Color(color.R, color.G, color.B, EncodeMaterialAlpha(materialId));
    }

    public static VoxelMaterialId ResolveMaterial(Color color)
    {
        return TryDecodeEncodedMaterial(color, out VoxelMaterialId materialId)
            ? materialId
            : ResolveNearestTintMaterial(color);
    }

    public static VoxelMaterialId ResolveNearestTintMaterial(Color tint)
    {
        VoxelMaterialId nearest = VoxelMaterialId.Soil;
        float bestDistanceSquared = float.MaxValue;
        foreach (VoxelMaterialId materialId in System.Enum.GetValues<VoxelMaterialId>())
        {
            Color candidate = GetTintColor(materialId);
            float distanceSquared =
                Mathf.Pow(tint.R - candidate.R, 2.0f) +
                Mathf.Pow(tint.G - candidate.G, 2.0f) +
                Mathf.Pow(tint.B - candidate.B, 2.0f);
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            nearest = materialId;
        }

        return nearest;
    }

    private static bool TryDecodeEncodedMaterial(Color color, out VoxelMaterialId materialId)
    {
        if (color.A <= 0.0f || color.A >= EncodedMaterialMaxAlpha)
        {
            materialId = default;
            return false;
        }

        int encodedIndex = Mathf.Clamp(
            Mathf.FloorToInt(color.A * MaterialEncodingSlotCount),
            0,
            MaxEncodedMaterialIndex);
        materialId = (VoxelMaterialId)encodedIndex;
        return true;
    }

    private static float EncodeMaterialAlpha(VoxelMaterialId materialId)
    {
        int clampedMaterialIndex = Mathf.Clamp((int)materialId, 0, MaxEncodedMaterialIndex);
        return (clampedMaterialIndex + 0.5f) / MaterialEncodingSlotCount;
    }
}
