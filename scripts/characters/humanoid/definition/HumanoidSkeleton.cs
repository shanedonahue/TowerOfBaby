using Godot;

namespace TowerOfBaby.Characters.Humanoid.Definition;

public sealed class HumanoidSkeleton
{
    public SkeletonJoint[] Joints { get; }
    private readonly System.Collections.Generic.Dictionary<string, int> _jointIndexById;

    public HumanoidSkeleton(SkeletonJoint[] joints, System.Collections.Generic.Dictionary<string, int> jointIndexById)
    {
        Joints = joints;
        _jointIndexById = jointIndexById;
    }

    public Vector3 GetJointRestPosition(string id)
    {
        return Joints[_jointIndexById[id]].LocalRestPosition;
    }
}
