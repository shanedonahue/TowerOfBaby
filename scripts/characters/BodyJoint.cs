using Godot;

public sealed class BodyJoint
{
    public string Id { get; }
    public int ParentIndex { get; }
    public Vector3 LocalRestPosition { get; }

    public BodyJoint(string id, int parentIndex, Vector3 localRestPosition)
    {
        Id = id;
        ParentIndex = parentIndex;
        LocalRestPosition = localRestPosition;
    }
}
