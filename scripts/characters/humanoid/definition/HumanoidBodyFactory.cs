using Godot;

namespace TowerOfBaby.Characters.Humanoid.Definition;

public static class HumanoidBodyFactory
{
    public static HumanoidBodySpec Generate(int seed)
    {
        RandomNumberGenerator rng = new() { Seed = (ulong)seed };

        float height = rng.RandfRange(2.2f, 3.05f);
        float upperLeg = height * rng.RandfRange(0.22f, 0.25f);
        float lowerLeg = height * rng.RandfRange(0.22f, 0.25f);
        float torsoHeight = height * rng.RandfRange(0.23f, 0.29f);
        float neckLength = height * rng.RandfRange(0.028f, 0.04f);
        float headRadius = height * rng.RandfRange(0.07f, 0.085f);
        float shoulderWidth = height * rng.RandfRange(0.18f, 0.24f);
        float hipWidth = shoulderWidth * rng.RandfRange(0.72f, 0.88f);
        float chestDepth = height * rng.RandfRange(0.1f, 0.13f);
        float upperArm = height * rng.RandfRange(0.16f, 0.19f);
        float lowerArm = height * rng.RandfRange(0.15f, 0.18f);
        float armRadius = height * rng.RandfRange(0.025f, 0.032f);
        float legRadius = height * rng.RandfRange(0.034f, 0.042f);
        float footLength = height * rng.RandfRange(0.11f, 0.14f);
        float footWidth = footLength * rng.RandfRange(0.42f, 0.55f);
        float footHeight = height * rng.RandfRange(0.025f, 0.032f);
        float legLength = upperLeg + lowerLeg;
        float hipHeight = legLength + footHeight;
        float totalHeight = hipHeight + torsoHeight + neckLength + (headRadius * 2.0f);
        float eyeHeight = hipHeight + torsoHeight + neckLength + (headRadius * 0.8f);
        float collisionRadius = Mathf.Max(shoulderWidth * 0.34f, hipWidth * 0.42f);
        float collisionHeight = Mathf.Max(totalHeight - (headRadius * 1.2f), totalHeight * 0.58f);

        return new HumanoidBodySpec
        {
            Seed = seed,
            Height = totalHeight,
            ShoulderWidth = shoulderWidth,
            HipWidth = hipWidth,
            TorsoHeight = torsoHeight,
            ChestDepth = chestDepth,
            NeckLength = neckLength,
            HeadRadius = headRadius,
            UpperArmLength = upperArm,
            LowerArmLength = lowerArm,
            UpperLegLength = upperLeg,
            LowerLegLength = lowerLeg,
            ArmRadius = armRadius,
            LegRadius = legRadius,
            FootLength = footLength,
            FootWidth = footWidth,
            FootHeight = footHeight,
            CollisionRadius = collisionRadius,
            CollisionHeight = collisionHeight,
            EyeHeight = eyeHeight
        };
    }
}
