using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public partial class TerrainChunk : Node3D
{
    private static readonly StandardMaterial3D SharedTerrainMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        Roughness = 0.97f,
        Metallic = 0.0f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
    };

    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;

    public Vector3I ChunkKey { get; private set; }
    public float ChunkSize => _data?.ChunkSize ?? ((PointsPerAxis - 1) * VoxelSize);
    public bool HasData => _data != null;
    public VoxelChunkData Data => _data;
    public bool HasCollision => _collision?.Shape != null;
    public bool HasSurface => _mesh != null && _mesh.GetSurfaceCount() > 0;
    public bool IsInitialLoadReady => HasData && !RenderDirty && !CollisionDirty && (!HasSurface || HasCollision);
    public bool RenderDirty { get; private set; }
    public bool CollisionDirty { get; private set; }
    public double CollisionReadyAtSeconds { get; private set; }
    public double LastRenderBuildMs { get; private set; }
    public double LastCollisionBuildMs { get; private set; }
    public bool PersistenceDirty { get; private set; }

    private MeshInstance3D _meshInstance = null!;
    private CollisionShape3D _collision = null!;
    private VoxelChunkData _data = null!;
    private ArrayMesh _mesh = null!;

    public override void _Ready()
    {
        _meshInstance = GetNode<MeshInstance3D>("Mesh");
        _collision = GetNode<CollisionShape3D>("Body/Collision");
    }

    public void Initialize(Vector3I key, TerrainWorldSettings settings)
    {
        ChunkKey = key;
        PointsPerAxis = settings.PointsPerAxis;
        VoxelSize = settings.VoxelSize;

        Vector3 origin = new(
            key.X * settings.ChunkSize,
            settings.BaseY + (key.Y * settings.ChunkSize),
            key.Z * settings.ChunkSize);

        Position = origin;
    }

    public void SetData(VoxelChunkData data, double collisionDelaySeconds)
    {
        _data = data;
        PersistenceDirty = false;
        MarkDirty(includeCollision: true, collisionDelaySeconds);
    }

    public void MarkDirty(bool includeCollision, double collisionDelaySeconds)
    {
        RenderDirty = true;
        if (includeCollision)
        {
            CollisionDirty = true;
            CollisionReadyAtSeconds = Time.GetTicksMsec() / 1000.0 + collisionDelaySeconds;
        }
    }

    public void RebuildRenderMesh()
    {
        if (_data == null)
        {
            return;
        }

        ulong start = Time.GetTicksUsec();
        _mesh = VoxelMesher.BuildMesh(_data);
        _meshInstance.Mesh = _mesh;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;

        if (_mesh.GetSurfaceCount() > 0)
        {
            _meshInstance.SetSurfaceOverrideMaterial(0, SharedTerrainMaterial);
        }
        LastRenderBuildMs = (Time.GetTicksUsec() - start) / 1000.0;
        RenderDirty = false;
    }

    public bool TryRebuildCollision(double nowSeconds)
    {
        if (!CollisionDirty || RenderDirty || _mesh == null || nowSeconds < CollisionReadyAtSeconds)
        {
            return false;
        }

        ulong start = Time.GetTicksUsec();
        _collision.Shape = _mesh.GetSurfaceCount() > 0 ? _mesh.CreateTrimeshShape() : null;
        LastCollisionBuildMs = (Time.GetTicksUsec() - start) / 1000.0;
        CollisionDirty = false;
        return true;
    }

    public bool IntersectsSphere(Vector3 center, float radius)
    {
        Vector3 min = Position;
        Vector3 max = Position + new Vector3(ChunkSize, ChunkSize, ChunkSize);
        Vector3 clamped = new Vector3(
            Mathf.Clamp(center.X, min.X, max.X),
            Mathf.Clamp(center.Y, min.Y, max.Y),
            Mathf.Clamp(center.Z, min.Z, max.Z));

        return clamped.DistanceSquaredTo(center) <= radius * radius;
    }

    public bool ApplySphereBrush(
        VoxelSphereEdit edit,
        System.Func<Vector3, float, VoxelMaterialId> materialResolver)
    {
        if (_data == null)
        {
            return false;
        }

        bool modified = VoxelTerrainEditing.ApplySphere(_data, edit, materialResolver);
        if (modified)
        {
            PersistenceDirty = true;
        }
        return modified;
    }

    public void MarkPersisted()
    {
        PersistenceDirty = false;
    }
}
