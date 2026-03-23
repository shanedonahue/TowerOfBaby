using Godot;

public static class HumanoidBodyGenerator
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
        float eyeHeight = (legLength * 0.53f) + torsoHeight + neckLength + headRadius * 0.8f;
        float collisionRadius = Mathf.Max(shoulderWidth * 0.34f, hipWidth * 0.42f);
        float collisionHeight = Mathf.Max(height - (headRadius * 1.2f), height * 0.58f);

        return new HumanoidBodySpec
        {
            Seed = seed,
            Height = height,
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
            VisualRootHeight = footHeight + (height * 0.08f),
            CollisionRadius = collisionRadius,
            CollisionHeight = collisionHeight,
            EyeHeight = eyeHeight
        };
    }
}
