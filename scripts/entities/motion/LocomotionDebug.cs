using Godot;

namespace TowerOfBaby.Entities.Motion;

public partial class LocomotionDebug : Node3D
{
    [Export] public bool Enabled = true;
    [Export] public float ForwardLimit = 0.32f;
    [Export] public float BackwardLimit = 0.24f;
    [Export] public float LateralLimit = 0.18f;
    [Export] public float MarkerSize = 0.08f;
    [Export] public float VectorScale = 0.18f;
    [Export] public float GroundLift = 0.02f;

    private ImmediateMesh _lineMesh = null!;
    private MeshInstance3D _lineMeshInstance = null!;

    public override void _Ready()
    {
        TopLevel = true;

        StandardMaterial3D material = new()
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        _lineMesh = new ImmediateMesh();
        _lineMeshInstance = new MeshInstance3D
        {
            Name = "Lines",
            Mesh = _lineMesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_lineMeshInstance);
    }

    public void RenderSnapshot(LocomotionTelemetrySnapshot snapshot)
    {
        Visible = Enabled && snapshot != null;
        if (!Visible)
        {
            return;
        }

        GlobalPosition = Vector3.Zero;
        Basis = Basis.Identity;

        _lineMesh.ClearSurfaces();
        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        DrawRootVectors(snapshot);
        DrawFoot(snapshot.LeftFoot, snapshot.FacingDirection, new Color(0.24f, 0.82f, 0.95f, 1.0f));
        DrawFoot(snapshot.RightFoot, snapshot.FacingDirection, new Color(0.98f, 0.62f, 0.28f, 1.0f));

        _lineMesh.SurfaceEnd();
    }

    private void DrawRootVectors(LocomotionTelemetrySnapshot snapshot)
    {
        Vector3 origin = snapshot.RootPosition + (Vector3.Up * 0.95f);
        DrawLine(origin, origin + (snapshot.FacingDirection * 0.7f), Colors.White);
        DrawLine(origin, origin + (snapshot.DesiredMovement * VectorScale), new Color(0.4f, 1.0f, 0.4f, 1.0f));
        DrawLine(origin, origin + (snapshot.ActualMovement * VectorScale), new Color(0.4f, 0.7f, 1.0f, 1.0f));
    }

    private void DrawFoot(LocomotionFootTelemetry foot, Vector3 facingDirection, Color color)
    {
        Vector3 up = LocomotionMath.SafeNormalized(foot.TerrainNormal, Vector3.Up);
        Vector3 forward = LocomotionMath.SafeNormalized(LocomotionMath.ProjectOntoPlane(facingDirection, up), Vector3.Forward);
        Vector3 right = LocomotionMath.GetRight(forward, up);

        Vector3 home = foot.HomePosition + (up * GroundLift);
        Vector3 support = foot.SupportPosition + (up * GroundLift);
        Vector3 target = foot.NextTargetPosition + (up * GroundLift);

        DrawSupportRegion(home, forward, right, color);
        DrawCross(support, MarkerSize, color);
        DrawCross(target, MarkerSize * 0.85f, color.Lightened(0.2f));
        DrawLine(support, home, foot.State == LocomotionFootState.Planted ? color : color.Darkened(0.25f));
        DrawLine(target, target + (up * 0.32f), color.Lightened(0.35f));

        if (foot.State == LocomotionFootState.Stepping)
        {
            DrawLine(support, target, color.Lightened(0.55f));
        }
    }

    private void DrawSupportRegion(Vector3 center, Vector3 forward, Vector3 right, Color color)
    {
        Vector3 frontLeft = center + (forward * ForwardLimit) - (right * LateralLimit);
        Vector3 frontRight = center + (forward * ForwardLimit) + (right * LateralLimit);
        Vector3 backLeft = center - (forward * BackwardLimit) - (right * LateralLimit);
        Vector3 backRight = center - (forward * BackwardLimit) + (right * LateralLimit);

        DrawLine(frontLeft, frontRight, color.Darkened(0.2f));
        DrawLine(frontRight, backRight, color.Darkened(0.2f));
        DrawLine(backRight, backLeft, color.Darkened(0.2f));
        DrawLine(backLeft, frontLeft, color.Darkened(0.2f));
    }

    private void DrawCross(Vector3 center, float size, Color color)
    {
        DrawLine(center - (Vector3.Right * size), center + (Vector3.Right * size), color);
        DrawLine(center - (Vector3.Forward * size), center + (Vector3.Forward * size), color);
        DrawLine(center - (Vector3.Up * size), center + (Vector3.Up * size), color);
    }

    private void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        _lineMesh.SurfaceSetColor(color);
        _lineMesh.SurfaceAddVertex(start);
        _lineMesh.SurfaceAddVertex(end);
    }
}
