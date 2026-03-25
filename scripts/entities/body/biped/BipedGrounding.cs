using Godot;
using Godot.Collections;
using TowerOfBaby.Entities.Motion;

namespace TowerOfBaby.Entities.Body.Biped;

public sealed class BipedGrounding
{
    public float ProbeStartHeight { get; init; } = 1.5f;
    public float ProbeDepth { get; init; } = 3.2f;
    public float FootLift { get; init; } = 0.015f;

    public GroundSample SampleGround(World3D world, Vector3 probePoint, Rid excludedRid)
    {
        if (world == null)
        {
            return GroundSample.NoHit(probePoint);
        }

        Vector3 rayStart = probePoint + (Vector3.Up * ProbeStartHeight);
        Vector3 rayEnd = probePoint - (Vector3.Up * ProbeDepth);
        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd);
        query.CollideWithAreas = false;
        query.Exclude = new Array<Rid> { excludedRid };

        Dictionary result = world.DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            return GroundSample.NoHit(probePoint);
        }

        Vector3 position = (Vector3)result["position"];
        Vector3 normal = ((Vector3)result["normal"]).Normalized();
        return new GroundSample(
            true,
            position + (normal * FootLift),
            normal,
            probePoint,
            rayStart.DistanceTo(position));
    }
}
