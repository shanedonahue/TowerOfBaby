using Godot;
using GodotArray = Godot.Collections.Array;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainRenderer : Node3D
{
    private const string TerrainSurfaceGroup = "terrain_surface";
    private const string TerrainWireframeShaderPath = "res://shaders/terrain/TerrainWireframe.gdshader";

    private static readonly StandardMaterial3D SharedLitVertexColorMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedLitWireframeMaterial = CreateLitVertexColorMaterial();
    private static readonly StandardMaterial3D SharedUnshadedVertexColorMaterial = CreateUnshadedVertexColorMaterial();
    private static bool _wireframeMaterialInitialized;
    private static bool _warnedMissingWireframeShader;

    private MeshInstance3D _meshInstance = null!;
    private MeshInstance3D _seamMeshInstance = null!;
    private StaticBody3D _body = null!;
    private CollisionShape3D _collision = null!;
    private Vector3[] _vertices = System.Array.Empty<Vector3>();
    private Vector3[] _normals = System.Array.Empty<Vector3>();
    private Vector2[] _uvs = System.Array.Empty<Vector2>();
    private Color[] _baseColors = System.Array.Empty<Color>();
    private Color[] _materialColors = System.Array.Empty<Color>();
    private float[] _biomeWeights = System.Array.Empty<float>();
    private float[] _tangents = System.Array.Empty<float>();
    private Vector3[] _seamVertices = System.Array.Empty<Vector3>();
    private Vector3[] _seamNormals = System.Array.Empty<Vector3>();
    private Vector2[] _seamUvs = System.Array.Empty<Vector2>();
    private Color[] _seamColors = System.Array.Empty<Color>();
    private TerrainVisualDebugMode _debugView = TerrainVisualDebugMode.Lit;

    public TerrainBlockId BlockId { get; private set; }
    public Vector3[] Vertices => _vertices;
    public Vector3[] Normals => _normals;
    public Color[] BaseColors => _baseColors;
    public Color[] MaterialColors => _materialColors.Length == _vertices.Length ? _materialColors : _baseColors;
    public float[] BiomeWeights => _biomeWeights;

    public static void ConfigureSharedSurfaceMaterial(float roughness)
    {
        float clampedRoughness = Mathf.Clamp(roughness, 0.0f, 1.0f);
        SharedLitVertexColorMaterial.Roughness = clampedRoughness;
        SharedLitWireframeMaterial.Roughness = clampedRoughness;
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
        _materialColors = meshBuild.MaterialColors;
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

    public void UpdateSeamMesh(VoxelMeshBuildResult seamMesh)
    {
        _seamVertices = seamMesh.Vertices;
        _seamNormals = seamMesh.Normals;
        _seamUvs = seamMesh.Uvs;
        _seamColors = seamMesh.Colors;
        ApplyCachedSeamVisuals();
    }

    public VoxelMeshBuildResult BuildVisualMeshSnapshot(TerrainSurfaceColorizer surfaceColorizer)
    {
        if (_vertices.Length == 0)
        {
            return VoxelMeshBuildResult.Empty;
        }

        Color[] colors = BuildRenderColors(surfaceColorizer);
        Color[] materialColors = _debugView == TerrainVisualDebugMode.VertexTint
            ? MaterialColors
            : System.Array.Empty<Color>();
        return new VoxelMeshBuildResult(
            _vertices,
            _normals,
            _uvs,
            colors,
            materialColors,
            System.Array.Empty<float>(),
            System.Array.Empty<float>(),
            NormalDebugMismatchCount: 0,
            TotalTriangleCount: _vertices.Length / 3,
            UsedDetailBrick: false,
            UsedPersistentDetailEdits: false,
            DetailTriangleCount: 0,
            ReplacedCoarseCellCount: 0,
            DetailCellCount: 0);
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
        _materialColors = System.Array.Empty<Color>();
        _biomeWeights = System.Array.Empty<float>();
        _tangents = System.Array.Empty<float>();
        _seamVertices = System.Array.Empty<Vector3>();
        _seamNormals = System.Array.Empty<Vector3>();
        _seamUvs = System.Array.Empty<Vector2>();
        _seamColors = System.Array.Empty<Color>();
        _meshInstance.Mesh = null;
        if (_seamMeshInstance != null)
        {
            _seamMeshInstance.Mesh = null;
            _seamMeshInstance.MaterialOverride = null;
            _seamMeshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }

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
        if (_tangents.Length > 0)
        {
            arrays[(int)Mesh.ArrayType.Tangent] = _tangents;
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _meshInstance.Mesh = mesh;
        _meshInstance.MaterialOverride = ResolveSurfaceMaterial();
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        ApplyCachedSeamVisuals();
        if (resetCollision)
        {
            _collision.Shape = null;
        }
    }

    private void ApplyCachedSeamVisuals()
    {
        EnsureNodes();
        if (_seamVertices.Length == 0)
        {
            if (_seamMeshInstance != null)
            {
                _seamMeshInstance.Mesh = null;
                _seamMeshInstance.MaterialOverride = null;
                _seamMeshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }

            return;
        }

        ArrayMesh seamMesh = new();
        GodotArray arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _seamVertices;
        arrays[(int)Mesh.ArrayType.Normal] = _seamNormals;
        arrays[(int)Mesh.ArrayType.TexUV] = _seamUvs;
        arrays[(int)Mesh.ArrayType.Color] = _seamColors;
        seamMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _seamMeshInstance.Mesh = seamMesh;
        _seamMeshInstance.MaterialOverride = ResolveSurfaceMaterial();
        _seamMeshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
    }

    private Color[] BuildRenderColors(TerrainSurfaceColorizer surfaceColorizer)
    {
        if (_debugView is TerrainVisualDebugMode.Lit or TerrainVisualDebugMode.Wireframe)
        {
            return _baseColors;
        }

        if (_debugView == TerrainVisualDebugMode.VertexTint)
        {
            return MaterialColors;
        }

        if (surfaceColorizer == null)
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

    private Material ResolveSurfaceMaterial()
    {
        if (_debugView == TerrainVisualDebugMode.Wireframe)
        {
            EnsureWireframeMaterial();
            return SharedLitWireframeMaterial;
        }

        return _debugView.UsesDiagnosticVertexColors()
            ? SharedUnshadedVertexColorMaterial
            : SharedLitVertexColorMaterial;
    }

    private static StandardMaterial3D CreateLitVertexColorMaterial()
    {
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            Roughness = 1.0f,
            Metallic = 0.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
        };
    }

    private static StandardMaterial3D CreateUnshadedVertexColorMaterial()
    {
        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            Roughness = 1.0f,
            Metallic = 0.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
    }

    private static void EnsureWireframeMaterial()
    {
        if (_wireframeMaterialInitialized)
        {
            return;
        }

        _wireframeMaterialInitialized = true;
        Shader wireframeShader = ResourceLoader.Load<Shader>(TerrainWireframeShaderPath);
        if (wireframeShader == null)
        {
            if (!_warnedMissingWireframeShader)
            {
                GD.PushWarning($"Terrain wireframe shader missing at {TerrainWireframeShaderPath}; falling back to lit terrain.");
                _warnedMissingWireframeShader = true;
            }

            return;
        }

        SharedLitWireframeMaterial.NextPass = new ShaderMaterial
        {
            Shader = wireframeShader
        };
    }

    private void EnsureNodes()
    {
        _meshInstance ??= GetNodeOrNull<MeshInstance3D>("Mesh");
        if (_meshInstance == null)
        {
            _meshInstance = new MeshInstance3D { Name = "Mesh" };
            AddChild(_meshInstance);
        }

        _seamMeshInstance ??= GetNodeOrNull<MeshInstance3D>("Seams");
        if (_seamMeshInstance == null)
        {
            _seamMeshInstance = new MeshInstance3D { Name = "Seams" };
            AddChild(_seamMeshInstance);
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
