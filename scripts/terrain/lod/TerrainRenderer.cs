using Godot;
using GodotArray = Godot.Collections.Array;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainRenderer : Node3D
{
    private const string TerrainSurfaceGroup = "terrain_surface";

    private static readonly StandardMaterial3D SharedTerrainMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Roughness = 0.97f,
        Metallic = 0.0f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
    };

    private MeshInstance3D _meshInstance = null!;
    private StaticBody3D _body = null!;
    private CollisionShape3D _collision = null!;

    public TerrainBlockId BlockId { get; private set; }

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

    public void ApplyMesh(VoxelMeshBuildResult meshBuild, bool includeCollision)
    {
        EnsureNodes();
        EnsureSurfaceGroup();

        if (!meshBuild.HasGeometry)
        {
            ClearVisuals();
            return;
        }

        ArrayMesh mesh = new();
        GodotArray arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = meshBuild.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = meshBuild.Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = meshBuild.Uvs;
        arrays[(int)Mesh.ArrayType.Color] = meshBuild.Colors;
        if (meshBuild.HasTangents)
        {
            arrays[(int)Mesh.ArrayType.Tangent] = meshBuild.Tangents;
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _meshInstance.Mesh = mesh;
        _meshInstance.MaterialOverride = SharedTerrainMaterial;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
        _collision.Shape = includeCollision
            ? mesh.CreateTrimeshShape()
            : null;
    }

    public void ClearVisuals()
    {
        EnsureNodes();
        _meshInstance.Mesh = null;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _collision.Shape = null;
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
