using Godot;
using System.Collections.Generic;

public static class HumanoidSkeletonBuilder
{
    public static HumanoidSkeleton Build(HumanoidBodySpec spec)
    {
        Dictionary<string, int> indices = new();
        List<BodyJoint> joints = new();
        List<BodyBone> bones = new();

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
        AddJoint(joints, indices, "foot_l", indices["knee_l"], new Vector3(-spec.HipWidth * 0.5f, spec.HipHeight - spec.LegLength, spec.FootLength * -0.2f));
        AddJoint(joints, indices, "hip_r", indices["pelvis"], new Vector3(spec.HipWidth * 0.5f, spec.HipHeight, 0.0f));
        AddJoint(joints, indices, "knee_r", indices["hip_r"], new Vector3(spec.HipWidth * 0.5f, spec.HipHeight - spec.UpperLegLength, 0.0f));
        AddJoint(joints, indices, "foot_r", indices["knee_r"], new Vector3(spec.HipWidth * 0.5f, spec.HipHeight - spec.LegLength, spec.FootLength * -0.2f));

        AddBone(bones, indices, "spine_lower", "pelvis", "spine_lower", spec.ChestDepth * 0.4f);
        AddBone(bones, indices, "spine_upper", "spine_lower", "spine_upper", spec.ChestDepth * 0.42f);
        AddBone(bones, indices, "neck", "spine_upper", "neck", spec.HeadRadius * 0.42f);
        AddBone(bones, indices, "arm_upper_l", "shoulder_l", "elbow_l", spec.ArmRadius);
        AddBone(bones, indices, "arm_lower_l", "elbow_l", "hand_l", spec.ArmRadius * 0.88f);
        AddBone(bones, indices, "arm_upper_r", "shoulder_r", "elbow_r", spec.ArmRadius);
        AddBone(bones, indices, "arm_lower_r", "elbow_r", "hand_r", spec.ArmRadius * 0.88f);
        AddBone(bones, indices, "leg_upper_l", "hip_l", "knee_l", spec.LegRadius);
        AddBone(bones, indices, "leg_lower_l", "knee_l", "foot_l", spec.LegRadius * 0.9f);
        AddBone(bones, indices, "leg_upper_r", "hip_r", "knee_r", spec.LegRadius);
        AddBone(bones, indices, "leg_lower_r", "knee_r", "foot_r", spec.LegRadius * 0.9f);

        return new HumanoidSkeleton(joints.ToArray(), bones.ToArray(), indices);
    }

    private static void AddJoint(List<BodyJoint> joints, Dictionary<string, int> indices, string id, int parentIndex, Vector3 position)
    {
        indices[id] = joints.Count;
        joints.Add(new BodyJoint(id, parentIndex, position));
    }

    private static void AddBone(List<BodyBone> bones, Dictionary<string, int> indices, string id, string startId, string endId, float radius)
    {
        bones.Add(new BodyBone(id, indices[startId], indices[endId], radius));
    }
}
