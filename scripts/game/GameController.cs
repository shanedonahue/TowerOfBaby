using Godot;
using Godot.Collections;
using TowerOfBaby.Debugging;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.Scene;

public partial class GameController : Node3D
{
    public enum PlayerStartMode
    {
        ResumeSerializedLocation = 0,
        RestartAtSceneSpawn = 1
    }

    [Export] public NodePath TerrainWorldPath = new();
    [Export] public NodePath PlayerPath = new("Player");
    [Export] public float BrushRange = 48.0f;
    [Export] public float BrushScrollStep = 0.2f;
    [Export] public float BrushPreviewConeHeight = 1.35f;
    [Export] public float BrushPreviewGroundLift = 0.06f;
    [Export] public bool EnableDebugTerrainBrush;
    [Export] public PlayerStartMode StartMode = PlayerStartMode.ResumeSerializedLocation;
    [ExportGroup("Debug Cache Hygiene")]
    [Export] public bool ClearProfilingLogsOnReady;
    [Export] public bool ClearStartupCacheOnReady;
    [Export] public bool ClearAllTerrainCacheOnReady;

    private TerrainWorld _terrainWorld = null!;
    private Node3D _player = null!;
    private MeshInstance3D _brushPreview = null!;
    private StandardMaterial3D _brushMaterial = null!;
    private CanvasLayer _loadingOverlay = null!;
    private Label _loadingLabel = null!;
    private PerformanceRunLogger _performanceLogger = null!;
    private Transform3D _playerSpawnTransform;

    public override void _Ready()
    {
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetNodeOrNull<TerrainWorld>("TerrainWorld");
        _player = GetNodeOrNull<Node3D>(PlayerPath) ?? GetNodeOrNull<Node3D>("Player");
        _playerSpawnTransform = _player?.GlobalTransform ?? Transform3D.Identity;
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
            Mesh = new CylinderMesh
            {
                TopRadius = 0.08f,
                BottomRadius = 1.0f,
                Height = 1.0f,
                RadialSegments = 24,
                Rings = 1
            },
            MaterialOverride = _brushMaterial,
            Visible = false
        };
        AddChild(_brushPreview);

        _performanceLogger = new PerformanceRunLogger
        {
            Name = "PerformanceRunLogger",
            TerrainWorldPath = TerrainWorldPath
        };
        AddChild(_performanceLogger);

        ApplyDebugCacheClears();

        BuildLoadingOverlay();
        SetPlayerLoadingState(active: _terrainWorld != null && !_terrainWorld.InitialLoadComplete);
        if (_terrainWorld != null)
        {
            _terrainWorld.InitialLoadCompleted += HandleInitialLoadCompleted;
        }

        if (StartMode == PlayerStartMode.RestartAtSceneSpawn)
        {
            ApplySceneSpawnOverride();
        }
    }

    public override void _Process(double delta)
    {
        UpdateLoadingOverlay();
        UpdateBrushPreview();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
            return;
        }

        if (!EnableDebugTerrainBrush)
        {
            return;
        }

        if (@event is not InputEventMouseButton mouseButton || !mouseButton.Pressed)
        {
            return;
        }

        if (_terrainWorld != null)
        {
            if (mouseButton.ButtonIndex == MouseButton.WheelUp)
            {
                _terrainWorld.AdjustBrushRadius(BrushScrollStep);
                return;
            }

            if (mouseButton.ButtonIndex == MouseButton.WheelDown)
            {
                _terrainWorld.AdjustBrushRadius(-BrushScrollStep);
                return;
            }
        }

        bool carve = mouseButton.ButtonIndex == MouseButton.Left;
        bool build = mouseButton.ButtonIndex == MouseButton.Right;
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
        Vector3 hitNormal = ((Vector3)result["normal"]).Normalized();
        Vector3 brushCenter = _terrainWorld.ResolveBrushCenter(hitPoint, hitNormal, additive: build);
        _terrainWorld.ApplyBrush(brushCenter, additive: build);
    }

    private void UpdateBrushPreview()
    {
        if (!EnableDebugTerrainBrush || _terrainWorld == null || !_terrainWorld.InitialLoadComplete)
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
        Vector3 brushCenter = _terrainWorld.ResolveBrushCenter(hitPoint, hitNormal, additive: Input.IsMouseButtonPressed(MouseButton.Right));
        _brushPreview.Visible = true;
        _brushPreview.GlobalTransform = BuildBrushPreviewTransform(hitPoint, hitNormal, brushCenter, _terrainWorld.BrushRadius);
        _brushMaterial.AlbedoColor = Input.IsMouseButtonPressed(MouseButton.Right)
            ? new Color(0.2f, 0.7f, 1.0f, 0.22f)
            : new Color(0.9f, 0.25f, 0.2f, 0.22f);
    }

    private Transform3D BuildBrushPreviewTransform(Vector3 hitPoint, Vector3 hitNormal, Vector3 brushCenter, float brushRadius)
    {
        Vector3 up = hitNormal.LengthSquared() > 0.0001f
            ? hitNormal.Normalized()
            : Vector3.Up;
        Basis basis = CreateBasisFromUp(up).Scaled(new Vector3(brushRadius, BrushPreviewConeHeight, brushRadius));
        Vector3 origin = brushCenter + (up * ((BrushPreviewConeHeight * 0.5f) + BrushPreviewGroundLift));
        return new Transform3D(basis, origin);
    }

    private static Basis CreateBasisFromUp(Vector3 up)
    {
        Vector3 tangent = Mathf.Abs(up.Dot(Vector3.Forward)) > 0.98f
            ? Vector3.Right
            : Vector3.Forward;
        Vector3 right = tangent.Cross(up).Normalized();
        Vector3 forward = up.Cross(right).Normalized();
        return new Basis(right, up, forward);
    }

    private void BuildLoadingOverlay()
    {
        _loadingOverlay = new CanvasLayer { Name = "LoadingOverlay", Layer = 10 };
        AddChild(_loadingOverlay);

        Control root = new Control
        {
            Name = "LoadingRoot",
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f
        };
        _loadingOverlay.AddChild(root);

        ColorRect loadingShade = new()
        {
            Name = "Shade",
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            Color = new Color(0.04f, 0.05f, 0.07f, 0.76f)
        };
        root.AddChild(loadingShade);

        _loadingLabel = new Label
        {
            Name = "Label",
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -130.0f,
            OffsetTop = -18.0f,
            OffsetRight = 130.0f,
            OffsetBottom = 18.0f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Generating terrain..."
        };
        root.AddChild(_loadingLabel);
    }

    private void UpdateLoadingOverlay()
    {
        if (_terrainWorld == null || _loadingOverlay == null)
        {
            return;
        }

        if (_terrainWorld.InitialLoadComplete)
        {
            _loadingOverlay.Visible = false;
            return;
        }

        _loadingOverlay.Visible = true;
        float progress = _terrainWorld.GetInitialLoadProgress();
        _loadingLabel.Text = $"Generating terrain... {(int)(progress * 100.0f)}%";
    }

    private void HandleInitialLoadCompleted()
    {
        SetPlayerLoadingState(active: false);
        if (_loadingOverlay != null)
        {
            _loadingOverlay.Visible = false;
        }
    }

    private void SetPlayerLoadingState(bool active)
    {
        if (_player == null)
        {
            return;
        }

        _player.ProcessMode = active ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        _player.Visible = !active;
    }

    private void ApplySceneSpawnOverride()
    {
        if (_player == null)
        {
            return;
        }

        _player.GlobalTransform = _playerSpawnTransform;
    }

    private void ApplyDebugCacheClears()
    {
        if (_terrainWorld == null)
        {
            return;
        }

        if (ClearAllTerrainCacheOnReady)
        {
            _terrainWorld.ClearAllPersistentCache();
            ClearAllTerrainCacheOnReady = false;
            ClearStartupCacheOnReady = false;
        }
        else if (ClearStartupCacheOnReady)
        {
            _terrainWorld.ClearStartupCache();
            ClearStartupCacheOnReady = false;
        }

        if (ClearProfilingLogsOnReady)
        {
            ClearProfilingLogs();
            ClearProfilingLogsOnReady = false;
        }
    }

    private static void ClearProfilingLogs()
    {
        string profilingPath = ProjectSettings.GlobalizePath("user://profiling");
        if (!DirAccess.DirExistsAbsolute(profilingPath))
        {
            return;
        }

        using DirAccess directory = DirAccess.Open(profilingPath);
        if (directory == null)
        {
            return;
        }

        directory.ListDirBegin();
        while (true)
        {
            string fileName = directory.GetNext();
            if (string.IsNullOrEmpty(fileName))
            {
                break;
            }

            if (directory.CurrentIsDir() || !fileName.EndsWith(".log"))
            {
                continue;
            }

            directory.Remove(fileName);
        }
        directory.ListDirEnd();
    }
}
