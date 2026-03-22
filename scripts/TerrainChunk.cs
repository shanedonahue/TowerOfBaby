using Godot;

public partial class TerrainChunk : Node3D
{
    [Export] public float Size = 24.0f;
    [Export] public int Resolution = 24;

    public Vector2I ChunkKey { get; private set; }

    private MeshInstance3D _meshInstance = null!;
    private StaticBody3D _body = null!;
    private CollisionShape3D _collision = null!;

    public override void _Ready()
    {
        EnsureNodes();
    }

    private void EnsureNodes()
    {
        if (_meshInstance != null)
        {
            return;
        }

        _meshInstance = GetNode<MeshInstance3D>("Mesh");
        _body = GetNode<StaticBody3D>("Body");
        _collision = GetNode<CollisionShape3D>("Body/Collision");
    }

    public void Generate(Vector2I key, TerrainChunkConfig config)
    {
        EnsureNodes();
        ChunkKey = key;
        Size = config.ChunkSize;
        Resolution = config.Resolution;

        Position = new Vector3(key.X * Size, 0.0f, key.Y * Size);
        FastNoiseLite baseNoise = config.CreateBaseNoise();
        FastNoiseLite detailNoise = config.CreateDetailNoise();

        var surfaceTool = new SurfaceTool();
        surfaceTool.Begin(Mesh.PrimitiveType.Triangles);

        float halfSize = Size * 0.5f;
        int quads = Mathf.Max(2, Resolution);
        float step = Size / quads;

        for (int z = 0; z < quads; z++)
        {
            float z0 = -halfSize + z * step;
            float z1 = z0 + step;

            for (int x = 0; x < quads; x++)
            {
                float x0 = -halfSize + x * step;
                float x1 = x0 + step;

                Vector3 p00 = SamplePoint(Position.X + x0, Position.Z + z0, x0, z0, config, baseNoise, detailNoise);
                Vector3 p10 = SamplePoint(Position.X + x1, Position.Z + z0, x1, z0, config, baseNoise, detailNoise);
                Vector3 p01 = SamplePoint(Position.X + x0, Position.Z + z1, x0, z1, config, baseNoise, detailNoise);
                Vector3 p11 = SamplePoint(Position.X + x1, Position.Z + z1, x1, z1, config, baseNoise, detailNoise);

                AddTriangle(surfaceTool, p00, p11, p01, ToColor(p00.Y, config), ToColor(p11.Y, config), ToColor(p01.Y, config));
                AddTriangle(surfaceTool, p00, p10, p11, ToColor(p00.Y, config), ToColor(p10.Y, config), ToColor(p11.Y, config));
            }
        }

        surfaceTool.GenerateNormals();
        surfaceTool.GenerateTangents();

        ArrayMesh mesh = surfaceTool.Commit();
        _meshInstance.Mesh = mesh;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;

        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.98f,
            Metallic = 0.0f,
            RimEnabled = true,
            Rim = 0.05f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
        };

        _meshInstance.SetSurfaceOverrideMaterial(0, material);

        _collision.Shape = new ConcavePolygonShape3D
        {
            Data = mesh.GetFaces(),
            BackfaceCollision = false
        };
        _collision.Disabled = false;

        SetActive(true);
    }

    public void SetActive(bool active)
    {
        EnsureNodes();
        Visible = active;
        _body.ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        _collision.Disabled = !active;
    }

    private static void AddTriangle(
        SurfaceTool surfaceTool,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Color ca,
        Color cb,
        Color cc)
    {
        surfaceTool.SetUV(new Vector2(a.X, a.Z) * 0.07f);
        surfaceTool.SetColor(ca);
        surfaceTool.AddVertex(a);

        surfaceTool.SetUV(new Vector2(b.X, b.Z) * 0.07f);
        surfaceTool.SetColor(cb);
        surfaceTool.AddVertex(b);

        surfaceTool.SetUV(new Vector2(c.X, c.Z) * 0.07f);
        surfaceTool.SetColor(cc);
        surfaceTool.AddVertex(c);
    }

    private static Vector3 SamplePoint(
        float worldX,
        float worldZ,
        float localX,
        float localZ,
        TerrainChunkConfig config,
        FastNoiseLite baseNoise,
        FastNoiseLite detailNoise)
    {
        float height = TerrainNoise.SampleHeight(worldX, worldZ, config, baseNoise, detailNoise);
        return new Vector3(localX, height, localZ);
    }

    private static Color ToColor(float height, TerrainChunkConfig config)
    {
        float normalized = Mathf.Clamp((height + config.HeightScale) / (config.HeightScale * 2.2f), 0.0f, 1.0f);

        if (normalized < 0.32f)
        {
            return new Color(0.17f, 0.22f, 0.13f).Lerp(new Color(0.26f, 0.32f, 0.17f), normalized / 0.32f);
        }

        if (normalized < 0.7f)
        {
            return new Color(0.31f, 0.36f, 0.2f).Lerp(new Color(0.5f, 0.44f, 0.28f), (normalized - 0.32f) / 0.38f);
        }

        return new Color(0.56f, 0.53f, 0.48f).Lerp(new Color(0.79f, 0.8f, 0.78f), (normalized - 0.7f) / 0.3f);
    }
}
