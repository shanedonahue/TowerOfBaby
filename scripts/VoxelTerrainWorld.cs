using Godot;
using System.Collections.Generic;

public partial class VoxelTerrainWorld : Node3D
{
    [Export] public PackedScene ChunkScene = null!;
    [Export] public NodePath TrackedCharacterPath = new();
    [Export] public int Radius = 2;
    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;
    [Export] public float BaseY = -12.0f;
    [Export] public int Seed = 12345;
    [Export] public float TerrainHeight = 10.0f;
    [Export] public float DetailHeight = 2.8f;
    [Export] public float CaveScale = 9.0f;
    [Export] public float CaveThreshold = 0.63f;
    [Export] public float BrushRadius = 2.4f;
    [Export] public float CarveStrength = -3.4f;
    [Export] public float BuildStrength = 2.8f;
    [Export] public int VerticalChunkCount = 3;
    [Export] public int MaxVisualChunkRebuildsPerFrame = 2;
    [Export] public int MaxCollisionChunkRebuildsPerFrame = 1;
    [Export] public float CollisionRebuildDelaySeconds = 0.08f;

    private readonly Dictionary<Vector3I, VoxelTerrainChunk> _chunks = new();
    private readonly HashSet<VoxelTerrainChunk> _dirtyRenderChunks = new();
    private readonly HashSet<VoxelTerrainChunk> _dirtyCollisionChunks = new();

    private VoxelFieldGenerator _generator = null!;
    private VoxelTerrainWorldSettings _settings = null!;
    private Node3D _trackedCharacter = null!;
    private Vector2I _lastCenterChunk = new(int.MinValue, int.MinValue);
    private int _lastVisualRebuildCount;
    private int _lastCollisionRebuildCount;
    private double _lastVisualRebuildMs;
    private double _lastCollisionRebuildMs;

    public override void _Ready()
    {
        AddToGroup("voxel_world");
        _settings = new VoxelTerrainWorldSettings
        {
            PointsPerAxis = PointsPerAxis,
            VoxelSize = VoxelSize,
            BaseY = BaseY
        };

        _generator = new VoxelFieldGenerator(Seed, TerrainHeight, DetailHeight, CaveScale, CaveThreshold);
        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;

        RefreshChunks(force: true);
    }

    public override void _Process(double delta)
    {
        RefreshChunks(force: false);
        ProcessDirtyChunks();
    }

    private void RefreshChunks(bool force)
    {
        Vector2I centerChunk = _trackedCharacter == null
            ? Vector2I.Zero
            : new Vector2I(
                Mathf.FloorToInt(_trackedCharacter.GlobalPosition.X / _settings.ChunkSize),
                Mathf.FloorToInt(_trackedCharacter.GlobalPosition.Z / _settings.ChunkSize));

        if (!force && centerChunk == _lastCenterChunk)
        {
            return;
        }

        _lastCenterChunk = centerChunk;
        HashSet<Vector3I> desired = new();

        for (int z = -Radius; z <= Radius; z++)
        {
            for (int x = -Radius; x <= Radius; x++)
            {
                for (int y = 0; y < VerticalChunkCount; y++)
                {
                    Vector3I key = new(centerChunk.X + x, y, centerChunk.Y + z);
                    desired.Add(key);
                    EnsureChunk(key);
                }
            }
        }

        foreach (KeyValuePair<Vector3I, VoxelTerrainChunk> entry in _chunks)
        {
            bool active = desired.Contains(entry.Key);
            entry.Value.Visible = active;
            entry.Value.ProcessMode = active ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        }
    }

    private void EnsureChunk(Vector3I key)
    {
        if (_chunks.ContainsKey(key))
        {
            return;
        }

        VoxelTerrainChunk chunk = ChunkScene.Instantiate<VoxelTerrainChunk>();
        AddChild(chunk);
        chunk.Generate(key, _settings, _generator);
        _chunks[key] = chunk;
        QueueChunkForRebuild(chunk);
    }

    public void ApplyBrush(Vector3 worldCenter, bool additive)
    {
        float strength = additive ? BuildStrength : CarveStrength;

        foreach (VoxelTerrainChunk chunk in _chunks.Values)
        {
            if (!chunk.IntersectsSphere(worldCenter, BrushRadius))
            {
                continue;
            }

            if (chunk.ApplySphereBrush(worldCenter, BrushRadius, strength))
            {
                chunk.MarkDirty(includeCollision: true, CollisionRebuildDelaySeconds);
                QueueChunkForRebuild(chunk);
            }
        }
    }

    public string GetDebugStats()
    {
        int activeCount = 0;
        foreach (VoxelTerrainChunk chunk in _chunks.Values)
        {
            if (chunk.Visible)
            {
                activeCount++;
            }
        }

        return
            $"Chunks: {activeCount} active / {_chunks.Count} loaded\n" +
            $"Dirty render: {_dirtyRenderChunks.Count} | dirty collision: {_dirtyCollisionChunks.Count}\n" +
            $"Last rebuilds: render {_lastVisualRebuildCount} ({_lastVisualRebuildMs:0.00} ms) | " +
            $"collision {_lastCollisionRebuildCount} ({_lastCollisionRebuildMs:0.00} ms)";
    }

    private void QueueChunkForRebuild(VoxelTerrainChunk chunk)
    {
        if (chunk.RenderDirty)
        {
            _dirtyRenderChunks.Add(chunk);
        }

        if (chunk.CollisionDirty)
        {
            _dirtyCollisionChunks.Add(chunk);
        }
    }

    private void ProcessDirtyChunks()
    {
        _lastVisualRebuildCount = 0;
        _lastCollisionRebuildCount = 0;
        _lastVisualRebuildMs = 0.0;
        _lastCollisionRebuildMs = 0.0;

        int visualBudget = MaxVisualChunkRebuildsPerFrame;
        if (visualBudget > 0)
        {
            List<VoxelTerrainChunk> renderQueue = new(_dirtyRenderChunks);
            foreach (VoxelTerrainChunk chunk in renderQueue)
            {
                if (visualBudget <= 0)
                {
                    break;
                }

                if (!IsInstanceValid(chunk))
                {
                    _dirtyRenderChunks.Remove(chunk);
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (!chunk.RenderDirty)
                {
                    _dirtyRenderChunks.Remove(chunk);
                    continue;
                }

                chunk.RebuildRenderMesh();
                _lastVisualRebuildCount++;
                _lastVisualRebuildMs += chunk.LastRenderBuildMs;
                visualBudget--;
                if (!chunk.RenderDirty)
                {
                    _dirtyRenderChunks.Remove(chunk);
                }
            }
        }

        int collisionBudget = MaxCollisionChunkRebuildsPerFrame;
        if (collisionBudget > 0)
        {
            double nowSeconds = Time.GetTicksMsec() / 1000.0;
            List<VoxelTerrainChunk> collisionQueue = new(_dirtyCollisionChunks);
            foreach (VoxelTerrainChunk chunk in collisionQueue)
            {
                if (collisionBudget <= 0)
                {
                    break;
                }

                if (!IsInstanceValid(chunk))
                {
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (!chunk.CollisionDirty)
                {
                    _dirtyCollisionChunks.Remove(chunk);
                    continue;
                }

                if (chunk.TryRebuildCollision(nowSeconds))
                {
                    _lastCollisionRebuildCount++;
                    _lastCollisionRebuildMs += chunk.LastCollisionBuildMs;
                    collisionBudget--;
                    if (!chunk.CollisionDirty)
                    {
                        _dirtyCollisionChunks.Remove(chunk);
                    }
                }
            }
        }
    }
}
