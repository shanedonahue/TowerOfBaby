using Godot;
using System.Collections.Generic;
using TowerOfBaby.Motion;

namespace TowerOfBaby.Characters.Humanoid.Definition;

public static class HumanoidMotionDefinitionFactory
{
    public static MotionSkeletonDefinition Build(HumanoidBodySpec spec, HumanoidSkeleton skeleton)
    {
        MotionJointDefinition[] joints = new MotionJointDefinition[skeleton.Joints.Length];
        Dictionary<string, int> jointIndices = new(skeleton.Joints.Length);

        for (int i = 0; i < skeleton.Joints.Length; i++)
        {
            SkeletonJoint joint = skeleton.Joints[i];
            joints[i] = new MotionJointDefinition(joint.Id, joint.ParentIndex, joint.LocalRestPosition);
            jointIndices[joint.Id] = i;
        }

        MotionChainDefinition[] chains =
        {
            CreateChain(jointIndices, "spine", MotionChainType.Spine, "pelvis", "spine_upper", "head", Vector3.Right),
            CreateChain(jointIndices, "left_arm", MotionChainType.Arm, "shoulder_l", "elbow_l", "hand_l", Vector3.Forward),
            CreateChain(jointIndices, "right_arm", MotionChainType.Arm, "shoulder_r", "elbow_r", "hand_r", Vector3.Back),
            CreateChain(jointIndices, "left_leg", MotionChainType.Leg, "hip_l", "knee_l", "foot_l", Vector3.Right),
            CreateChain(jointIndices, "right_leg", MotionChainType.Leg, "hip_r", "knee_r", "foot_r", Vector3.Right)
        };

        Vector3 footSupportOffset = new(0.0f, -spec.FootHeight, -spec.FootLength * 0.22f);
        Vector3 handSupportOffset = new(0.0f, -spec.ArmRadius, 0.0f);

        MotionContactDefinition[] contacts =
        {
            CreateContact(jointIndices, "left_foot", MotionContactType.Foot, "left_leg", "foot_l", footSupportOffset, 0.02f),
            CreateContact(jointIndices, "right_foot", MotionContactType.Foot, "right_leg", "foot_r", footSupportOffset, 0.02f),
            CreateContact(jointIndices, "left_hand", MotionContactType.Hand, "left_arm", "hand_l", handSupportOffset, spec.ArmRadius * 0.2f),
            CreateContact(jointIndices, "right_hand", MotionContactType.Hand, "right_arm", "hand_r", handSupportOffset, spec.ArmRadius * 0.2f)
        };

        return new MotionSkeletonDefinition(joints, chains, contacts, jointIndices);
    }

    private static MotionChainDefinition CreateChain(
        IReadOnlyDictionary<string, int> jointIndices,
        string id,
        MotionChainType type,
        string rootJointId,
        string midJointId,
        string endJointId,
        Vector3 preferredBendNormalLocal)
    {
        return new MotionChainDefinition(
            id,
            type,
            jointIndices[rootJointId],
            jointIndices[midJointId],
            jointIndices[endJointId],
            preferredBendNormalLocal);
    }

    private static MotionContactDefinition CreateContact(
        IReadOnlyDictionary<string, int> jointIndices,
        string id,
        MotionContactType type,
        string chainId,
        string jointId,
        Vector3 supportOffsetLocal,
        float groundClearance)
    {
        return new MotionContactDefinition(
            id,
            type,
            chainId,
            jointIndices[jointId],
            supportOffsetLocal,
            groundClearance);
    }
}
