public sealed class BodyBone
{
    public string Id { get; }
    public int StartJointIndex { get; }
    public int EndJointIndex { get; }
    public float Radius { get; }

    public BodyBone(string id, int startJointIndex, int endJointIndex, float radius)
    {
        Id = id;
        StartJointIndex = startJointIndex;
        EndJointIndex = endJointIndex;
        Radius = radius;
    }
}
