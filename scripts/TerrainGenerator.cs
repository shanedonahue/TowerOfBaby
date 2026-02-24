using Godot;
using System;

public partial class TerrainGenerator : StaticBody3D
{
	[Export] public int Size = 128;          // number of quads per side
	[Export] public float CellSize = 1.0f;   // spacing
	[Export] public float Height = 8.0f;     // amplitude
	[Export] public int Seed = 12345;

	[Export] public float Frequency = 0.02f;

	private MeshInstance3D _meshInstance;
	private CollisionShape3D _collisionShape;

	public override void _Ready()
	{
		_meshInstance = GetNode<MeshInstance3D>("MeshInstance3D");
		_collisionShape = GetNode<CollisionShape3D>("CollisionShape3D");

		// var noise = new FastNoiseLite
		// {
		// 	Seed = Seed,
		// 	Frequency = Frequency,
		// 	NoiseType = FastNoiseLite.NoiseTypeEnum.OpenSimplex2
		// };

		// var mesh = BuildMesh(noise);
		// _meshInstance.Mesh = mesh;

		// // Collision from mesh
		// var shape = new ConcavePolygonShape3D();
		// shape.Data = mesh.GetFaces(); // triangles
		// _collisionShape.Shape = shape;
	}

	private ArrayMesh BuildMesh(FastNoiseLite noise)
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		for (int z = 0; z < Size; z++)
		for (int x = 0; x < Size; x++)
		{
			// four corners of a quad
			// var p00 = Vertex(x,     z,     noise);
			// var p10 = Vertex(x + 1, z,     noise);
			// var p01 = Vertex(x,     z + 1, noise);
			// var p11 = Vertex(x + 1, z + 1, noise);

			// // two triangles: (p00, p01, p11) and (p00, p11, p10)
			// AddTri(st, p00, p01, p11);
			// AddTri(st, p00, p11, p10);
		}

		st.GenerateNormals();
		st.GenerateTangents();

		return st.Commit();
	}

	// private Vector3 Vertex(int gx, int gz, FastNoiseLite noise)
	// {
	// 	float x = gx * CellSize;
	// 	float z = gz * CellSize;

	// 	// Noise is [-1,1] → scale to height
	// 	float y = noise.GetNoise2D(x, z) * Height;

	// 	return new Vector3(x, y, z);
	// }

	// private void AddTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
	// {
	// 	st.AddVertex(a);
	// 	st.AddVertex(b);
	// 	st.AddVertex(c);
	// }
}
