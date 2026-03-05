using System.Runtime.CompilerServices;
using Godot;

public partial class TerrainChunk : Node3D
{
    // [Export] public int Resolution = 32;     // vertices per side
    [Export] public float Size = 64f;        // world meters per chunk
	[Export] public int Seed = 12345;
	[Export] public float Frequency = 0.02f;
	[Export] public float Height = 8.0f;     // amplitude

    private MeshInstance3D _mesh;
	private FastNoiseLite _noise;
    public override void _Ready()
    {
        _mesh = GetNode<MeshInstance3D>("Mesh");
		_noise = new FastNoiseLite
		{
			Seed = Seed,
			Frequency = Frequency,
			NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex
		};
    }

    public void Generate(int chunkX, int chunkZ)
    {
        // Position this chunk in world space
		Vector2 flatPos = new Vector2(chunkX, chunkZ) * Size;
		Vector3 pos = GetNoisyVec3(flatPos);
        // For now: just a simple plane made from 2 triangles (later: grid)
        float half = Size * 0.5f;

        Vector3 v0 = GetNoisyVec3(flatPos + new Vector2(-half, -half));
        Vector3 v1 = GetNoisyVec3(flatPos + new Vector2( half, -half));
        Vector3 v2 = GetNoisyVec3(flatPos + new Vector2( half,  half));
        Vector3 v3 = GetNoisyVec3(flatPos + new Vector2(-half,  half));

        var vertices = new Vector3[] { v0, v1, v2, v3 };
        var indices  = new int[] { 0, 1, 2, 0, 2, 3 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        _mesh.Mesh = mesh;
    }


	private Vector3 GetNoisyVec3(Vector2 input) => GetNoisyVec3(input.X, input.Y);
	private Vector3 GetNoisyVec3(float x, float z) => new(x, GetNoisyHeight(x, z), z);
	private float GetNoisyHeight(float x, float z) => _noise.GetNoise2D(x, z) * Height;
}