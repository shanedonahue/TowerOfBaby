using Godot;

namespace TowerOfBaby.Characters.Humanoid.Locomotion;

public sealed partial class HumanoidLocomotionSystem
{
    private MeshInstance3D _diagnosticLinesInstance = null!;
    private ImmediateMesh _diagnosticLinesMesh = null!;

    private void UpdateMotionDiagnostics(float delta, bool hasGroundFrame, HumanoidGroundMotionFrame frame)
    {
        UpdateEarlyReleaseDebugTimers(delta);

        if (!_settings.EnableMotionDiagnostics)
        {
            ClearMotionDiagnostics();
            return;
        }

        EnsureMotionDiagnostics();
        if (_diagnosticLinesMesh is null || _diagnosticLinesInstance is null)
        {
            return;
        }

        _diagnosticLinesInstance.Visible = true;
        _diagnosticLinesMesh.ClearSurfaces();
        _diagnosticLinesMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        if (hasGroundFrame)
        {
            DrawGroundDiagnostics(frame);
        }

        DrawLegDiagnostics(_leftLeg, hasGroundFrame, frame, true);
        DrawLegDiagnostics(_rightLeg, hasGroundFrame, frame, false);

        _diagnosticLinesMesh.SurfaceEnd();
    }

    private void EnsureMotionDiagnostics()
    {
        if (_diagnosticLinesInstance is not null)
        {
            return;
        }

        _diagnosticLinesMesh = new ImmediateMesh();
        _diagnosticLinesInstance = new MeshInstance3D
        {
            Name = "MotionDiagnostics",
            TopLevel = true,
            Mesh = _diagnosticLinesMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };

        StandardMaterial3D material = new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };

        _diagnosticLinesInstance.MaterialOverride = material;
        _body.AddChild(_diagnosticLinesInstance);
    }

    private void ClearMotionDiagnostics()
    {
        if (_diagnosticLinesMesh is not null)
        {
            _diagnosticLinesMesh.ClearSurfaces();
        }

        if (_diagnosticLinesInstance is not null)
        {
            _diagnosticLinesInstance.Visible = false;
        }
    }

    private void UpdateEarlyReleaseDebugTimers(float delta)
    {
        _leftLeg.EarlyReleaseDebugTimer = Mathf.Max(0.0f, _leftLeg.EarlyReleaseDebugTimer - delta);
        _rightLeg.EarlyReleaseDebugTimer = Mathf.Max(0.0f, _rightLeg.EarlyReleaseDebugTimer - delta);
    }

    private void DrawGroundDiagnostics(HumanoidGroundMotionFrame frame)
    {
        Vector3 supportCenter = new(frame.SupportCenter.X, frame.SupportHeight, frame.SupportCenter.Z);
        Color supportColor = new(0.8f, 0.8f, 0.85f, 0.85f);
        Color comColor = new(0.1f, 0.9f, 1.0f, 0.95f);
        Color captureColor = new(1.0f, 0.2f, 0.85f, 0.95f);

        AddPointCross(supportCenter, 0.06f, supportColor);
        AddPointCross(frame.PlanarCom, 0.08f, comColor);
        AddPointCross(frame.BalanceTarget, 0.08f, captureColor);
        AddLine(frame.PlanarCom, frame.BalanceTarget, captureColor);
        AddLine(supportCenter, frame.PlanarCom, comColor);
    }

    private void DrawLegDiagnostics(HumanoidLegMotionRuntime leg, bool hasGroundFrame, HumanoidGroundMotionFrame frame, bool isLeft)
    {
        Vector3 phaseAnchor = leg.IsInStance ? leg.FootPivotWorld : leg.CurrentSupportWorld;
        Color phaseColor = ResolvePhaseColor(leg);
        AddLine(phaseAnchor, phaseAnchor + (Vector3.Up * 0.3f), phaseColor);
        AddLine(
            phaseAnchor + (Vector3.Left * 0.05f),
            phaseAnchor + (Vector3.Right * 0.05f),
            phaseColor);

        if (leg.HeelContactWorld.LengthSquared() > 0.0001f)
        {
            AddPointCross(leg.HeelContactWorld, 0.05f, new Color(1.0f, 0.92f, 0.22f, 0.95f));
        }

        if (leg.ToeContactWorld.LengthSquared() > 0.0001f)
        {
            AddPointCross(leg.ToeContactWorld, 0.05f, new Color(0.24f, 1.0f, 0.42f, 0.95f));
        }

        if (leg.HeelContactWorld.LengthSquared() > 0.0001f && leg.ToeContactWorld.LengthSquared() > 0.0001f)
        {
            AddLine(leg.HeelContactWorld, leg.ToeContactWorld, new Color(0.95f, 0.95f, 0.95f, 0.65f));
        }

        if (leg.DebugSupportTargetWorld.LengthSquared() > 0.0001f)
        {
            Color targetColor = new(0.9f, 0.35f, 1.0f, 0.85f);
            AddLine(phaseAnchor, leg.DebugSupportTargetWorld, targetColor);
            AddPointCross(leg.DebugSupportTargetWorld, 0.045f, targetColor);
        }

        if (hasGroundFrame)
        {
            DrawRearReachLimit(leg, frame, isLeft);
        }

        if (leg.IsInStance && leg.RearReleaseArmed)
        {
            Vector3 armedMarker = phaseAnchor + (Vector3.Up * 0.18f);
            Color armedColor = new(1.0f, 0.55f, 0.08f, 0.95f);
            AddPointCross(armedMarker, 0.045f, armedColor);
            AddLine(phaseAnchor, armedMarker, armedColor);
        }

        if (leg.EarlyReleaseDebugTimer > 0.0f)
        {
            float alpha = Mathf.Clamp(leg.EarlyReleaseDebugTimer / 0.45f, 0.0f, 1.0f);
            Color eventColor = new(1.0f, 0.18f, 0.18f, alpha);
            Vector3 eventTop = leg.EarlyReleaseEventWorld + (Vector3.Up * (0.15f + (0.2f * alpha)));
            AddLine(leg.EarlyReleaseEventWorld, eventTop, eventColor);
            AddPointCross(eventTop, 0.06f, eventColor);
        }
    }

    private void DrawRearReachLimit(HumanoidLegMotionRuntime leg, HumanoidGroundMotionFrame frame, bool isLeft)
    {
        Vector3 hipWorld = _rig.Hips.GlobalPosition + (frame.VisualBasis * leg.HipOffsetFromPelvisLocal);
        Vector3 rearLimitWorld = hipWorld - (frame.Forward * (_spec.LegLength * HumanoidLocomotionModel.RearReachRatio));
        Color limitColor = new(
            Mathf.Lerp(0.4f, 1.0f, leg.RearReachSaturation),
            Mathf.Lerp(0.85f, 0.2f, leg.RearReachSaturation),
            0.2f,
            0.9f);

        AddLine(hipWorld, rearLimitWorld, limitColor);
        Vector3 barOffset = frame.Right * (isLeft ? -0.06f : 0.06f);
        AddLine(rearLimitWorld - barOffset, rearLimitWorld + barOffset, limitColor);
    }

    private static Color ResolvePhaseColor(HumanoidLegMotionRuntime leg)
    {
        if (!leg.IsInStance)
        {
            return new Color(0.22f, 0.6f, 1.0f, 0.95f);
        }

        return leg.StanceFootPhase switch
        {
            HumanoidStanceFootPhase.HeelStrike => new Color(1.0f, 0.86f, 0.2f, 0.95f),
            HumanoidStanceFootPhase.ToeOff => new Color(1.0f, 0.45f, 0.12f, 0.95f),
            _ => new Color(0.25f, 1.0f, 0.4f, 0.95f)
        };
    }

    private void AddPointCross(Vector3 position, float radius, Color color)
    {
        AddLine(position + (Vector3.Right * radius), position - (Vector3.Right * radius), color);
        AddLine(position + (Vector3.Up * radius), position - (Vector3.Up * radius), color);
        AddLine(position + (Vector3.Forward * radius), position - (Vector3.Forward * radius), color);
    }

    private void AddLine(Vector3 from, Vector3 to, Color color)
    {
        if (_diagnosticLinesMesh is null)
        {
            return;
        }

        _diagnosticLinesMesh.SurfaceSetColor(color);
        _diagnosticLinesMesh.SurfaceAddVertex(from);
        _diagnosticLinesMesh.SurfaceSetColor(color);
        _diagnosticLinesMesh.SurfaceAddVertex(to);
    }
}
