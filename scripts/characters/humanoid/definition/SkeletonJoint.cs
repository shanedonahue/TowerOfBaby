using Godot;

namespace TowerOfBaby.Characters.Humanoid.Definition;

public sealed class SkeletonJoint
{
    public string Id { get; }
    public int ParentIndex { get; }
    public Vector3 LocalRestPosition { get; }

    public SkeletonJoint(string id, int parentIndex, Vector3 localRestPosition)
    {
        Id = id;
        ParentIndex = parentIndex;
        LocalRestPosition = localRestPosition;
    }
}
