using Godot;

public partial class FpsOverlay : CanvasLayer
{
    [Export] public Vector2 Margin = new(12.0f, 12.0f);
    [Export] public NodePath TerrainWorldPath = new();

    private Label _label = null!;
    private TerrainWorld _terrainWorld = null!;

    public override void _Ready()
    {
        _label = GetNodeOrNull<Label>("Label");
        if (_label == null)
        {
            _label = new Label { Name = "Label" };
            AddChild(_label);
        }

        _label.Position = Margin;
        _label.Modulate = Colors.White;
        _label.Text = "FPS: --";
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
    }

    public override void _Process(double delta)
    {
        string stats = _terrainWorld?.GetDebugStats() ?? "Voxel stats unavailable";
        _label.Text = $"FPS: {Engine.GetFramesPerSecond()}\n{stats}";
    }
}
