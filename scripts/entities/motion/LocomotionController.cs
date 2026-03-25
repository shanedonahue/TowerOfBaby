using Godot;
using TowerOfBaby.Entities.Body.Biped;

namespace TowerOfBaby.Entities.Motion;

public sealed class LocomotionController
{
    private readonly BipedBodyDefinition _bodyDefinition;
    private readonly BipedGrounding _grounding;
    private readonly RootMotionDriver _rootMotionDriver;
    private readonly FootPlanner _footPlanner;

    private bool _initialized;
    private LocomotionTelemetrySnapshot _lastTelemetry = new();

    public LocomotionController(
        BipedBodyDefinition bodyDefinition,
        BipedGrounding grounding,
        RootMotionSettings rootMotionSettings,
        FootPlannerSettings footPlannerSettings,
        FootSwingSettings footSwingSettings)
    {
        _bodyDefinition = bodyDefinition;
        _grounding = grounding;
        _rootMotionDriver = new RootMotionDriver(rootMotionSettings);
        _footPlanner = new FootPlanner(footPlannerSettings, new FootSwingSolver(footSwingSettings));
    }

    public LocomotionFrame Step(CharacterBody3D body, MovementIntent intent, double delta)
    {
        Vector3 initialFacing = -body.GlobalTransform.Basis.Z;
        if (!_initialized)
        {
            _rootMotionDriver.Reset(body.GlobalPosition, initialFacing);
            _footPlanner.Initialize(
                _bodyDefinition,
                _grounding,
                body.GetWorld3D(),
                body.GetRid(),
                body.GlobalPosition,
                initialFacing,
                Vector3.Up);
            _initialized = true;
        }

        RootMotionFrame root = _rootMotionDriver.Step(body, intent, delta);
        _footPlanner.Update(
            (float)delta,
            _bodyDefinition,
            root,
            _grounding,
            body.GetWorld3D(),
            body.GetRid());

        _lastTelemetry = _footPlanner.BuildTelemetry(root);
        return new LocomotionFrame(
            root,
            _footPlanner.GetPose(FootSide.Left),
            _footPlanner.GetPose(FootSide.Right),
            _lastTelemetry);
    }

    public LocomotionTelemetrySnapshot GetTelemetrySnapshot()
    {
        return _lastTelemetry;
    }
}
