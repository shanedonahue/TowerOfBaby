using Godot;
using Godot.Collections;

public partial class VoxelPrototypeController : Node3D
{
    [Export] public NodePath TerrainWorldPath = new();
    [Export] public float BrushRange = 48.0f;

    private VoxelTerrainWorld _terrainWorld = null!;
    private MeshInstance3D _brushPreview = null!;
    private StandardMaterial3D _brushMaterial = null!;

    public override void _Ready()
    {
        _terrainWorld = GetNodeOrNull<VoxelTerrainWorld>(TerrainWorldPath) ?? GetNodeOrNull<VoxelTerrainWorld>("VoxelTerrainWorld");
        _brushMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.9f, 0.25f, 0.2f, 0.22f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true
        };

        _brushPreview = new MeshInstance3D
        {
            Name = "BrushPreview",
            Mesh = new SphereMesh
            {
                Radius = 1.0f,
                Height = 2.0f
            },
            MaterialOverride = _brushMaterial,
            Visible = false
        };
        AddChild(_brushPreview);
    }

    public override void _Process(double delta)
    {
        UpdateBrushPreview();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton || !mouseButton.Pressed)
        {
            return;
        }

        bool carve = mouseButton.ButtonIndex == MouseButton.Left && Input.IsKeyPressed(Key.Ctrl);
        bool build = mouseButton.ButtonIndex == MouseButton.Right && Input.IsKeyPressed(Key.Ctrl);
        if (!carve && !build)
        {
            return;
        }

        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null || _terrainWorld == null)
        {
            return;
        }

        Vector2 screenPoint = mouseButton.Position;
        Vector3 origin = camera.ProjectRayOrigin(screenPoint);
        Vector3 end = origin + camera.ProjectRayNormal(screenPoint) * BrushRange;

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, end);
        query.CollideWithAreas = false;

        Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            return;
        }

        Vector3 hitPoint = (Vector3)result["position"];
        _terrainWorld.ApplyBrush(hitPoint, additive: build);
    }

    private void UpdateBrushPreview()
    {
        if (_terrainWorld == null)
        {
            return;
        }

        bool showPreview = Input.IsKeyPressed(Key.Ctrl);
        if (!showPreview)
        {
            _brushPreview.Visible = false;
            return;
        }

        Camera3D camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            _brushPreview.Visible = false;
            return;
        }

        Vector2 mousePosition = GetViewport().GetMousePosition();
        Vector3 origin = camera.ProjectRayOrigin(mousePosition);
        Vector3 end = origin + camera.ProjectRayNormal(mousePosition) * BrushRange;

        PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, end);
        query.CollideWithAreas = false;

        Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count == 0)
        {
            _brushPreview.Visible = false;
            return;
        }

        Vector3 hitPoint = (Vector3)result["position"];
        Vector3 hitNormal = ((Vector3)result["normal"]).Normalized();
        _brushPreview.Visible = true;
        _brushPreview.GlobalPosition = hitPoint + hitNormal * 0.05f;
        float diameter = _terrainWorld.BrushRadius * 2.0f;
        _brushPreview.Scale = Vector3.One * diameter;
        _brushMaterial.AlbedoColor = Input.IsMouseButtonPressed(MouseButton.Right)
            ? new Color(0.2f, 0.7f, 1.0f, 0.22f)
            : new Color(0.9f, 0.25f, 0.2f, 0.22f);
    }
}
