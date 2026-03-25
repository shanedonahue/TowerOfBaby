using Godot;
using System.Collections.Generic;

namespace TowerOfBaby.Characters.Humanoid.Definition;

public static class HumanoidSkeletonFactory
{
    public static HumanoidSkeleton Build(HumanoidBodySpec spec)
    {
        Dictionary<string, int> indices = new();
        List<SkeletonJoint> joints = new();

        AddJoint(joints, indices, "root", -1, Vector3.Zero);
        AddJoint(joints, indices, "pelvis", indices["root"], new Vector3(0.0f, spec.HipHeight, 0.0f));
        AddJoint(joints, indices, "spine_lower", indices["pelvis"], new Vector3(0.0f, spec.HipHeight + (spec.TorsoHeight * 0.38f), 0.0f));
        AddJoint(joints, indices, "spine_upper", indices["pelvis"], new Vector3(0.0f, spec.ShoulderHeight, 0.0f));
        AddJoint(joints, indices, "neck", indices["spine_upper"], new Vector3(0.0f, spec.ShoulderHeight + spec.NeckLength, 0.0f));
        AddJoint(joints, indices, "head", indices["neck"], new Vector3(0.0f, spec.ShoulderHeight + spec.NeckLength + spec.HeadRadius * 1.25f, 0.0f));

        AddJoint(joints, indices, "shoulder_l", indices["spine_upper"], new Vector3(-spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.HeadRadius * 0.2f, 0.0f));
        AddJoint(joints, indices, "elbow_l", indices["shoulder_l"], new Vector3(-spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.UpperArmLength, 0.0f));
        AddJoint(joints, indices, "hand_l", indices["elbow_l"], new Vector3(-spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.UpperArmLength - spec.LowerArmLength, 0.0f));
        AddJoint(joints, indices, "shoulder_r", indices["spine_upper"], new Vector3(spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.HeadRadius * 0.2f, 0.0f));
        AddJoint(joints, indices, "elbow_r", indices["shoulder_r"], new Vector3(spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.UpperArmLength, 0.0f));
        AddJoint(joints, indices, "hand_r", indices["elbow_r"], new Vector3(spec.ShoulderWidth * 0.5f, spec.ShoulderHeight - spec.UpperArmLength - spec.LowerArmLength, 0.0f));

        AddJoint(joints, indices, "hip_l", indices["pelvis"], new Vector3(-spec.HipWidth * 0.5f, spec.HipHeight, 0.0f));
        AddJoint(joints, indices, "knee_l", indices["hip_l"], new Vector3(-spec.HipWidth * 0.5f, spec.HipHeight - spec.UpperLegLength, 0.0f));
        AddJoint(joints, indices, "foot_l", indices["knee_l"], new Vector3(-spec.HipWidth * 0.5f, spec.FootHeight, spec.FootLength * -0.2f));
        AddJoint(joints, indices, "hip_r", indices["pelvis"], new Vector3(spec.HipWidth * 0.5f, spec.HipHeight, 0.0f));
        AddJoint(joints, indices, "knee_r", indices["hip_r"], new Vector3(spec.HipWidth * 0.5f, spec.HipHeight - spec.UpperLegLength, 0.0f));
        AddJoint(joints, indices, "foot_r", indices["knee_r"], new Vector3(spec.HipWidth * 0.5f, spec.FootHeight, spec.FootLength * -0.2f));

        return new HumanoidSkeleton(joints.ToArray(), indices);
    }

    private static void AddJoint(List<SkeletonJoint> joints, Dictionary<string, int> indices, string id, int parentIndex, Vector3 position)
    {
        indices[id] = joints.Count;
        joints.Add(new SkeletonJoint(id, parentIndex, position));
    }
}
