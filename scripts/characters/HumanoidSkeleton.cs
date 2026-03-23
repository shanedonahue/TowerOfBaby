using Godot;
using System.Collections.Generic;

public sealed class HumanoidSkeleton
{
    public BodyJoint[] Joints { get; }
    public BodyBone[] Bones { get; }
    public IReadOnlyDictionary<string, int> JointIndexById { get; }

    public HumanoidSkeleton(BodyJoint[] joints, BodyBone[] bones, Dictionary<string, int> jointIndexById)
    {
        Joints = joints;
        Bones = bones;
        JointIndexById = jointIndexById;
    }

    public Vector3 GetJointRestPosition(string id)
    {
        return Joints[JointIndexById[id]].LocalRestPosition;
    }
}
