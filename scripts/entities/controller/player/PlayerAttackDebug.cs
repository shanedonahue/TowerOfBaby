using Godot;

namespace TowerOfBaby.Entities.Controller.Player;

public partial class PlayerAttackDebug : Node3D
{
    [Export] public bool Enabled = true;
    [Export] public float PersistSeconds = 0.45f;
    [Export] public float MarkerSize = 0.18f;

    private ImmediateMesh _mesh = null!;
    private MeshInstance3D _meshInstance = null!;

    private float _remaining;
    private Vector3 _queryOrigin;
    private Vector3 _queryEnd;
    private bool _terrainHit;
    private Vector3 _hitPosition;
    private Vector3 _hitNormal;
    private Vector3 _slashDirection;

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

        _mesh = new ImmediateMesh();
        _meshInstance = new MeshInstance3D
        {
            Name = "AttackDebugMesh",
            Mesh = _mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_meshInstance);
    }

    public override void _Process(double delta)
    {
        if (_remaining > 0.0f)
        {
            _remaining = Mathf.Max(0.0f, _remaining - (float)delta);
        }

        Redraw();
    }

    public void ShowSlashQuery(
        Vector3 queryOrigin,
        Vector3 queryEnd,
        bool terrainHit,
        Vector3 hitPosition,
        Vector3 hitNormal,
        Vector3 slashDirection)
    {
        _queryOrigin = queryOrigin;
        _queryEnd = queryEnd;
        _terrainHit = terrainHit;
        _hitPosition = hitPosition;
        _hitNormal = hitNormal;
        _slashDirection = slashDirection;
        _remaining = PersistSeconds;
        Redraw();
    }

    private void Redraw()
    {
        Visible = Enabled && _remaining > 0.0f;
        if (!Visible)
        {
            return;
        }

        GlobalPosition = Vector3.Zero;
        Basis = Basis.Identity;

        Color queryColor = _terrainHit
            ? new Color(0.24f, 0.95f, 0.46f, 1.0f)
            : new Color(1.0f, 0.76f, 0.18f, 1.0f);
        Color hitColor = new Color(1.0f, 0.25f, 0.2f, 1.0f);
        Color slashColor = new Color(0.2f, 0.7f, 1.0f, 1.0f);

        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        DrawLine(_queryOrigin, _queryEnd, queryColor);

        if (_terrainHit)
        {
            DrawCross(_hitPosition, MarkerSize, hitColor);
            DrawLine(_hitPosition, _hitPosition + (_hitNormal * (MarkerSize * 2.0f)), hitColor);
            DrawLine(_hitPosition, _hitPosition + (_slashDirection * (MarkerSize * 2.7f)), slashColor);
            DrawLine(_hitPosition, _hitPosition - (_slashDirection * (MarkerSize * 2.7f)), slashColor);
        }

        _mesh.SurfaceEnd();
    }

    private void DrawCross(Vector3 center, float size, Color color)
    {
        DrawLine(center - (Vector3.Right * size), center + (Vector3.Right * size), color);
        DrawLine(center - (Vector3.Forward * size), center + (Vector3.Forward * size), color);
        DrawLine(center - (Vector3.Up * size), center + (Vector3.Up * size), color);
    }

    private void DrawLine(Vector3 start, Vector3 end, Color color)
    {
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(start);
        _mesh.SurfaceAddVertex(end);
    }
}
