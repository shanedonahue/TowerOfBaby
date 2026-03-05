using Godot;
using System;

public partial class TerrainGenerator : Node3D
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
		// 	NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex
		// };

		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		float size = 5f;

		// vertices (XZ plane)
		Vector3 v0 = new Vector3(-size, 0, -size);
		Vector3 v1 = new Vector3(size, 0, -size);
		Vector3 v2 = new Vector3(size, 0, size);
		Vector3 v3 = new Vector3(-size, 0, size);

		// triangle 1
		st.AddVertex(v0);
		st.AddVertex(v1);
		st.AddVertex(v2);

		// triangle 2
		st.AddVertex(v0);
		st.AddVertex(v2);
		st.AddVertex(v3);

		st.GenerateNormals();

		_meshInstance.Mesh = st.Commit();

		var mat = new StandardMaterial3D
		{
			AlbedoColor = Colors.WebGray
		};
		_meshInstance.SetSurfaceOverrideMaterial(0, mat);

		// var mesh = BuildFlatMesh();
		// GD.Print(mesh.GetFaces().Length);

		// _meshInstance.Mesh = mesh;

		// Collision from mesh
		_collisionShape.Shape = new ConcavePolygonShape3D
		{
			Data = _meshInstance.Mesh.GetFaces() // triangles
		};
	}

	private ArrayMesh BuildFlatMesh()
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);
		
		for (int z = -1; z < 1; z++)
		{
			for (int x = -1; x < 1; x++)
			{
				// four corners of a quad
				// var p00 = Vertex(x,     z,     noise);
				// var p10 = Vertex(x + 1, z,     noise);
				// var p01 = Vertex(x,     z + 1, noise);
				// var p11 = Vertex(x + 1, z + 1, noise);

				var p00 = Vertex(x,     z,     0);
				var p10 = Vertex(x + 1, z,     0);
				var p01 = Vertex(x,     z + 1, 0);
				var p11 = Vertex(x + 1, z + 1, 0);

				// two triangles: (p00, p01, p11) and (p00, p11, p10)
				AddTri(st, p00, p01, p11);
				AddTri(st, p00, p11, p10);
			}
			
		}
		st.GenerateNormals();

		return st.Commit();
	}

	// private ArrayMesh BuildMesh(FastNoiseLite noise)
	// {


	// 	st.GenerateNormals();
	// 	// st.GenerateTangents();

	// 	return st.Commit();
	// }

	// private Vector3 Vertex(int gx, int gz, FastNoiseLite noise)
	// {
	// 	float x = gx * CellSize;
	// 	float z = gz * CellSize;

	// 	// Noise is [-1,1] → scale to height
	// 	float y = noise.GetNoise2D(x, z) * Height;

	// 	return new Vector3(x, y, z);
	// }

	private Vector3 Vertex(int gx, int gz, int gy) => new(gz * CellSize, gy * Height, gx * CellSize);
	
	private void AddTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
	{
		st.AddVertex(a);
		st.AddVertex(b);
		st.AddVertex(c);
	}
}
