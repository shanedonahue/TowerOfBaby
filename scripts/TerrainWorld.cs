using Godot;
using System.Collections.Generic;

public partial class TerrainWorld : Node3D
{
    [Export] public PackedScene ChunkScene;
    [Export] public int Radius = 1; // 1 => 3x3 chunks

    private readonly Dictionary<Vector2I, TerrainChunk> _chunks = new();

    public override void _Ready()
    {
        // Spawn a small grid around origin as a first test
        for (int z = -Radius; z <= Radius; z++)
        {
            for (int x = -Radius; x <= Radius; x++)
            {
                EnsureChunk(new Vector2I(x, z));
            }
        }
    }

    private void EnsureChunk(Vector2I key)
    {
        if (_chunks.ContainsKey(key)) return;

        var chunk = ChunkScene.Instantiate<TerrainChunk>();
        AddChild(chunk);
        chunk.Generate(key.X, key.Y);

        _chunks[key] = chunk;
    }
}