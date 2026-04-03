using Godot;
using GodotArray = Godot.Collections.Array;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainRenderer : Node3D
{
    private const string TerrainSurfaceGroup = "terrain_surface";

    private MeshInstance3D _meshInstance = null!;
    private StaticBody3D _body = null!;
    private CollisionShape3D _collision = null!;
    private Vector3[] _vertices = System.Array.Empty<Vector3>();
    private Vector3[] _normals = System.Array.Empty<Vector3>();
    private Vector2[] _uvs = System.Array.Empty<Vector2>();
    private Color[] _baseColors = System.Array.Empty<Color>();
    private float[] _biomeWeights = System.Array.Empty<float>();
    private float[] _tangents = System.Array.Empty<float>();
    private TerrainVisualDebugMode _debugView = TerrainVisualDebugMode.Lit;

    public TerrainBlockId BlockId { get; private set; }

    public static void ConfigureSharedSurfaceMaterial(float roughness)
    {
        TerrainSurfaceMaterialLibrary.ConfigureSharedSurfaceRoughness(roughness);
    }

    public override void _Ready()
    {
        EnsureNodes();
        EnsureSurfaceGroup();
    }

    public void Initialize(TerrainBlockId blockId, Vector3 origin)
    {
        BlockId = blockId;
        Name = $"TerrainBlock_L{blockId.Lod}_{blockId.Index.X}_{blockId.Index.Y}_{blockId.Index.Z}";
        Position = origin;
        EnsureNodes();
    }

    public void ApplyMesh(
        VoxelMeshBuildResult meshBuild,
        bool includeCollision,
        TerrainVisualDebugMode debugView,
        TerrainSurfaceColorizer surfaceColorizer)
    {
        ApplyVisualMesh(meshBuild, debugView, surfaceColorizer);
        ApplyCollision(includeCollision);
    }

    public void ApplyVisualMesh(
        VoxelMeshBuildResult meshBuild,
        TerrainVisualDebugMode debugView,
        TerrainSurfaceColorizer surfaceColorizer)
    {
        EnsureNodes();
        EnsureSurfaceGroup();
        _debugView = debugView;

        if (!meshBuild.HasGeometry)
        {
            ClearVisuals();
            return;
        }

        _vertices = meshBuild.Vertices;
        _normals = meshBuild.Normals;
        _uvs = meshBuild.Uvs;
        _baseColors = meshBuild.Colors;
        _biomeWeights = meshBuild.BiomeWeights;
        _tangents = meshBuild.Tangents;
        ApplyCachedVisuals(surfaceColorizer, resetCollision: true);
    }

    public void SetDebugView(TerrainVisualDebugMode debugView, TerrainSurfaceColorizer surfaceColorizer)
    {
        _debugView = debugView;
        if (_vertices.Length == 0)
        {
            return;
        }

        ApplyCachedVisuals(surfaceColorizer, resetCollision: false);
    }

    public bool HasVisuals => _vertices.Length > 0;

    public void ApplyCollision(bool includeCollision)
    {
        EnsureNodes();
        if (!includeCollision ||
            _meshInstance.Mesh is not ArrayMesh mesh ||
            mesh.GetSurfaceCount() == 0)
        {
            _collision.Shape = null;
            return;
        }

        _collision.Shape = mesh.CreateTrimeshShape();
    }

    public void ClearVisuals()
    {
        EnsureNodes();
        _vertices = System.Array.Empty<Vector3>();
        _normals = System.Array.Empty<Vector3>();
        _uvs = System.Array.Empty<Vector2>();
        _baseColors = System.Array.Empty<Color>();
        _biomeWeights = System.Array.Empty<float>();
        _tangents = System.Array.Empty<float>();
        _meshInstance.Mesh = null;
        _meshInstance.MaterialOverride = null;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _collision.Shape = null;
    }

    public bool HasCollision => _collision?.Shape != null;

    private void ApplyCachedVisuals(TerrainSurfaceColorizer surfaceColorizer, bool resetCollision)
    {
        ArrayMesh mesh = new();
        GodotArray arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        arrays[(int)Mesh.ArrayType.Normal] = _normals;
        arrays[(int)Mesh.ArrayType.TexUV] = _uvs;
        arrays[(int)Mesh.ArrayType.Color] = BuildRenderColors(surfaceColorizer);
        Mesh.ArrayFormat surfaceFormat = 0;
        if (_biomeWeights.Length > 0)
        {
            arrays[(int)Mesh.ArrayType.Custom0] = _biomeWeights;
            surfaceFormat = (Mesh.ArrayFormat)((int)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        }
        if (_tangents.Length > 0)
        {
            arrays[(int)Mesh.ArrayType.Tangent] = _tangents;
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, surfaceFormat);
        _meshInstance.Mesh = mesh;
        _meshInstance.MaterialOverride = _debugView.UsesDiagnosticVertexColors()
            ? TerrainSurfaceMaterialLibrary.UnshadedVertexColorMaterial
            : TerrainSurfaceMaterialLibrary.TintedLitSurfaceMaterial;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        if (resetCollision)
        {
            _collision.Shape = null;
        }
    }

    private Color[] BuildRenderColors(TerrainSurfaceColorizer surfaceColorizer)
    {
        if (_debugView == TerrainVisualDebugMode.Lit ||
            _debugView == TerrainVisualDebugMode.VertexTint ||
            surfaceColorizer == null)
        {
            return _baseColors;
        }

        Color[] colors = new Color[_vertices.Length];
        Vector3 origin = GlobalTransform.Origin;
        for (int i = 0; i < colors.Length; i++)
        {
            Vector3 worldPosition = origin + _vertices[i];
            colors[i] = surfaceColorizer.ResolveDebugColor(_debugView, worldPosition, _normals[i], _baseColors[i]);
        }

        return colors;
    }

    private void EnsureNodes()
    {
        _meshInstance ??= GetNodeOrNull<MeshInstance3D>("Mesh");
        if (_meshInstance == null)
        {
            _meshInstance = new MeshInstance3D { Name = "Mesh" };
            AddChild(_meshInstance);
        }

        _body ??= GetNodeOrNull<StaticBody3D>("Body");
        if (_body == null)
        {
            _body = new StaticBody3D { Name = "Body" };
            AddChild(_body);
        }

        _collision ??= _body.GetNodeOrNull<CollisionShape3D>("Collision");
        if (_collision == null)
        {
            _collision = new CollisionShape3D { Name = "Collision" };
            _body.AddChild(_collision);
        }
    }

    private void EnsureSurfaceGroup()
    {
        if (_body != null && !_body.IsInGroup(TerrainSurfaceGroup))
        {
            _body.AddToGroup(TerrainSurfaceGroup);
        }
    }
}
