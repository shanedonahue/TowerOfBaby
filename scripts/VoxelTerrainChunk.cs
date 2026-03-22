using Godot;

public partial class VoxelTerrainChunk : Node3D
{
    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;

    public Vector3I ChunkKey { get; private set; }
    public float ChunkSize => _data?.ChunkSize ?? ((PointsPerAxis - 1) * VoxelSize);
    public bool RenderDirty { get; private set; }
    public bool CollisionDirty { get; private set; }
    public double CollisionReadyAtSeconds { get; private set; }
    public double LastRenderBuildMs { get; private set; }
    public double LastCollisionBuildMs { get; private set; }

    private MeshInstance3D _meshInstance = null!;
    private StaticBody3D _body = null!;
    private CollisionShape3D _collision = null!;
    private VoxelChunkData _data = null!;
    private ArrayMesh _mesh = null!;

    public override void _Ready()
    {
        _meshInstance = GetNode<MeshInstance3D>("Mesh");
        _body = GetNode<StaticBody3D>("Body");
        _collision = GetNode<CollisionShape3D>("Body/Collision");
    }

    public void Generate(Vector3I key, VoxelTerrainWorldSettings settings, VoxelFieldGenerator generator)
    {
        ChunkKey = key;
        PointsPerAxis = settings.PointsPerAxis;
        VoxelSize = settings.VoxelSize;

        Vector3 origin = new(
            key.X * settings.ChunkSize,
            settings.BaseY + (key.Y * settings.ChunkSize),
            key.Z * settings.ChunkSize);

        Position = origin;
        _data = new VoxelChunkData(PointsPerAxis, VoxelSize, origin);
        generator.FillChunk(_data);
        MarkDirty(includeCollision: true, 0.0);
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
        ulong start = Time.GetTicksUsec();
        _mesh = VoxelMesher.BuildMesh(_data);
        _meshInstance.Mesh = _mesh;
        _meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;

        StandardMaterial3D material = new()
        {
            VertexColorUseAsAlbedo = true,
            Roughness = 0.97f,
            Metallic = 0.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
        };

        if (_mesh.GetSurfaceCount() > 0)
        {
            _meshInstance.SetSurfaceOverrideMaterial(0, material);
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

    public bool ApplySphereBrush(Vector3 center, float radius, float deltaDensity)
    {
        if (_data == null)
        {
            return false;
        }

        bool modified = _data.ApplySphereBrush(center, radius, deltaDensity);
        return modified;
    }
}
