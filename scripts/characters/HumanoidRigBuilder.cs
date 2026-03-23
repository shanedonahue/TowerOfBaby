using Godot;

public static class HumanoidRigBuilder
{
    public static HumanoidRig Build(Node3D owner, CollisionShape3D collision, HumanoidBodySpec spec, HumanoidSkeleton skeleton)
    {
        ClearExistingVisualRoot(owner);
        ConfigureCollision(collision, spec);

        Node3D visualRoot = new()
        {
            Name = "VisualRoot",
            Position = new Vector3(0.0f, spec.VisualRootHeight, 0.0f)
        };
        owner.AddChild(visualRoot);

        StandardMaterial3D bodyMaterial = new()
        {
            AlbedoColor = new Color(0.72f, 0.62f, 0.45f),
            Roughness = 0.75f
        };

        StandardMaterial3D accentMaterial = new()
        {
            AlbedoColor = new Color(0.18f, 0.24f, 0.31f),
            Roughness = 0.68f
        };

        Node3D hips = new()
        {
            Name = "Hips",
            Position = skeleton.GetJointRestPosition("pelvis")
        };
        visualRoot.AddChild(hips);

        Node3D upperBody = new() { Name = "UpperBody" };
        hips.AddChild(upperBody);

        float torsoOffset = spec.TorsoHeight * 0.5f;
        Node3D torso = CreateBoxPart(
            "Torso",
            new Vector3(spec.ShoulderWidth * 0.92f, spec.TorsoHeight, spec.ChestDepth),
            bodyMaterial,
            new Vector3(0.0f, torsoOffset, 0.0f));
        upperBody.AddChild(torso);

        Node3D chestBand = CreateBoxPart(
            "ChestBand",
            new Vector3(spec.ShoulderWidth * 0.98f, spec.TorsoHeight * 0.26f, spec.ChestDepth * 1.08f),
            accentMaterial,
            new Vector3(0.0f, spec.TorsoHeight * 0.62f, 0.0f));
        upperBody.AddChild(chestBand);

        Node3D head = CreateSpherePart(
            "Head",
            spec.HeadRadius,
            bodyMaterial,
            new Vector3(0.0f, spec.TorsoHeight + spec.NeckLength + spec.HeadRadius * 0.9f, 0.0f));
        upperBody.AddChild(head);

        Node3D leftArm = CreateArm(
            "LeftArm",
            skeleton.GetJointRestPosition("shoulder_l") - skeleton.GetJointRestPosition("pelvis"),
            spec,
            accentMaterial);
        upperBody.AddChild(leftArm);

        Node3D rightArm = CreateArm(
            "RightArm",
            skeleton.GetJointRestPosition("shoulder_r") - skeleton.GetJointRestPosition("pelvis"),
            spec,
            accentMaterial);
        upperBody.AddChild(rightArm);

        HumanoidLegRig leftLeg = CreateLegRig("LeftLeg", skeleton, spec, -spec.HipWidth * 0.5f, accentMaterial, hips);
        HumanoidLegRig rightLeg = CreateLegRig("RightLeg", skeleton, spec, spec.HipWidth * 0.5f, accentMaterial, hips);

        return new HumanoidRig
        {
            VisualRoot = visualRoot,
            Hips = hips,
            UpperBody = upperBody,
            Torso = torso,
            ChestBand = chestBand,
            Head = head,
            LeftArm = leftArm,
            RightArm = rightArm,
            LeftLeg = leftLeg,
            RightLeg = rightLeg,
            Skeleton = skeleton
        };
    }

    private static void ClearExistingVisualRoot(Node3D owner)
    {
        Node existing = owner.GetNodeOrNull("VisualRoot");
        existing?.QueueFree();
    }

    private static void ConfigureCollision(CollisionShape3D collision, HumanoidBodySpec spec)
    {
        collision.Shape = new CapsuleShape3D
        {
            Radius = spec.CollisionRadius,
            Height = spec.CollisionHeight
        };
        collision.Position = new Vector3(0.0f, (spec.CollisionHeight * 0.5f) + spec.VisualRootHeight, 0.0f);
    }

    private static Node3D CreateArm(string name, Vector3 shoulderPosition, HumanoidBodySpec spec, Material material)
    {
        Node3D shoulder = new() { Name = name, Position = shoulderPosition };

        MeshInstance3D upper = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = spec.ArmRadius,
                Height = spec.UpperArmLength
            },
            Position = new Vector3(0.0f, -spec.UpperArmLength * 0.5f, 0.0f),
            MaterialOverride = material
        };
        shoulder.AddChild(upper);

        MeshInstance3D lower = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = spec.ArmRadius * 0.82f,
                Height = spec.LowerArmLength
            },
            Position = new Vector3(0.0f, -(spec.UpperArmLength + (spec.LowerArmLength * 0.5f)), 0.0f),
            MaterialOverride = material
        };
        shoulder.AddChild(lower);

        return shoulder;
    }

    private static HumanoidLegRig CreateLegRig(string name, HumanoidSkeleton skeleton, HumanoidBodySpec spec, float sideOffset, Material material, Node3D hips)
    {
        HumanoidLegRig leg = new()
        {
            SideOffset = sideOffset,
            UpperLength = spec.UpperLegLength,
            LowerLength = spec.LowerLegLength
        };

        Node3D upper = new() { Name = $"{name}Upper", Position = new Vector3(sideOffset, 0.0f, 0.0f) };
        hips.AddChild(upper);

        MeshInstance3D upperMesh = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = spec.LegRadius,
                Height = spec.UpperLegLength
            },
            Position = new Vector3(0.0f, -spec.UpperLegLength * 0.5f, 0.0f),
            MaterialOverride = material
        };
        upper.AddChild(upperMesh);

        Node3D lower = new() { Name = $"{name}Lower" };
        upper.AddChild(lower);

        MeshInstance3D lowerMesh = new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = spec.LegRadius * 0.88f,
                Height = spec.LowerLegLength
            },
            Position = new Vector3(0.0f, -spec.LowerLegLength * 0.5f, 0.0f),
            MaterialOverride = material
        };
        lower.AddChild(lowerMesh);

        Node3D foot = new()
        {
            Name = $"{name}Foot",
            Position = new Vector3(0.0f, -spec.LowerLegLength, 0.0f)
        };
        lower.AddChild(foot);

        MeshInstance3D footMesh = new()
        {
            Mesh = new BoxMesh { Size = new Vector3(spec.FootWidth, spec.FootHeight, spec.FootLength) },
            Position = new Vector3(0.0f, -spec.FootHeight * 0.5f, -spec.FootLength * 0.22f),
            MaterialOverride = material
        };
        foot.AddChild(footMesh);

        leg = new HumanoidLegRig
        {
            Upper = upper,
            Lower = lower,
            Foot = foot,
            SideOffset = sideOffset,
            UpperLength = spec.UpperLegLength,
            LowerLength = spec.LowerLegLength
        };

        return leg;
    }

    private static Node3D CreateBoxPart(string name, Vector3 size, Material material, Vector3 position)
    {
        Node3D pivot = new() { Name = name, Position = position };
        MeshInstance3D mesh = new()
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material
        };
        pivot.AddChild(mesh);
        return pivot;
    }

    private static Node3D CreateSpherePart(string name, float radius, Material material, Vector3 position)
    {
        Node3D pivot = new() { Name = name, Position = position };
        MeshInstance3D mesh = new()
        {
            Mesh = new SphereMesh
            {
                Radius = radius,
                Height = radius * 2.0f
            },
            MaterialOverride = material
        };
        pivot.AddChild(mesh);
        return pivot;
    }
}
