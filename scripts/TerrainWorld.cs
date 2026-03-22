using Godot;
using System.Collections.Generic;

public partial class TerrainWorld : Node3D
{
    [Export] public PackedScene ChunkScene = null!;
    [Export] public NodePath TrackedCharacterPath = new();
    [Export] public float ChunkSize = 24.0f;
    [Export] public int ChunkResolution = 24;
    [Export] public int Seed = 12345;
    [Export] public float BaseFrequency = 0.016f;
    [Export] public float DetailFrequency = 0.065f;
    [Export] public float HeightScale = 8.5f;
    [Export] public float DetailScale = 2.3f;
    [Export] public int ForwardChunkRange = 5;
    [Export] public int BackwardChunkRange = 2;
    [Export] public int SideChunkRange = 3;
    [Export] public float MotionLookaheadDistance = 10.0f;
    [Export] public float MaxLookaheadDistance = 16.0f;

    private readonly Dictionary<Vector2I, TerrainChunk> _chunks = new();
    private readonly HashSet<Vector2I> _activeKeys = new();

    private Node3D _trackedCharacter = null!;
    private Vector3 _lastTrackedPosition = Vector3.Zero;
    private Vector3 _motionDirection = Vector3.Forward;
    private TerrainChunkConfig _config = null!;

    public override void _Ready()
    {
        _trackedCharacter = GetNodeOrNull<Node3D>(TrackedCharacterPath) ?? GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;
        _config = BuildConfig();

        if (_trackedCharacter != null)
        {
            _lastTrackedPosition = _trackedCharacter.GlobalPosition;
            RefreshStreaming(force: true);
        }
    }

    public override void _Process(double delta)
    {
        if (_trackedCharacter == null)
        {
            _trackedCharacter = GetTree().GetFirstNodeInGroup("terrain_tracker") as Node3D;
            if (_trackedCharacter == null)
            {
                return;
            }

            _lastTrackedPosition = _trackedCharacter.GlobalPosition;
            RefreshStreaming(force: true);
            return;
        }

        Vector3 displacement = _trackedCharacter.GlobalPosition - _lastTrackedPosition;
        Vector3 planarDisplacement = new Vector3(displacement.X, 0.0f, displacement.Z);

        if (planarDisplacement.LengthSquared() > 0.0001f)
        {
            _motionDirection = planarDisplacement.Normalized();
        }

        _lastTrackedPosition = _trackedCharacter.GlobalPosition;
        RefreshStreaming(force: false);
    }

    public TerrainChunkConfig GetConfig() => _config;

    private void RefreshStreaming(bool force)
    {
        if (_trackedCharacter == null)
        {
            return;
        }

        Vector3 trackedVelocity = Vector3.Zero;
        if (_trackedCharacter is CharacterBody3D body)
        {
            trackedVelocity = new Vector3(body.Velocity.X, 0.0f, body.Velocity.Z);
        }

        float speedLookahead = Mathf.Min(trackedVelocity.Length() * 0.55f, MaxLookaheadDistance);
        float appliedLookahead = Mathf.Max(MotionLookaheadDistance, speedLookahead);
        Vector3 focusPosition = _trackedCharacter.GlobalPosition + (_motionDirection * appliedLookahead);
        Vector2I focusChunk = ToChunkCoord(focusPosition);

        HashSet<Vector2I> desired = new();
        Vector2 forward = new Vector2(_motionDirection.X, _motionDirection.Z).Normalized();
        if (forward == Vector2.Zero)
        {
            forward = Vector2.Up;
        }

        Vector2 right = new Vector2(forward.Y, -forward.X);

        for (int dz = -(BackwardChunkRange + ForwardChunkRange); dz <= BackwardChunkRange + ForwardChunkRange; dz++)
        {
            for (int dx = -(SideChunkRange + 1); dx <= SideChunkRange + 1; dx++)
            {
                Vector2I candidate = new(focusChunk.X + dx, focusChunk.Y + dz);
                Vector2 centerOffset = new Vector2(
                    (candidate.X - focusChunk.X) * ChunkSize,
                    (candidate.Y - focusChunk.Y) * ChunkSize);

                float forwardDistance = centerOffset.Dot(forward);
                float lateralDistance = Mathf.Abs(centerOffset.Dot(right));

                bool insideLongitudinalRange =
                    forwardDistance <= ForwardChunkRange * ChunkSize &&
                    forwardDistance >= -BackwardChunkRange * ChunkSize;

                bool insideLateralRange = lateralDistance <= SideChunkRange * ChunkSize;

                if (insideLongitudinalRange && insideLateralRange)
                {
                    desired.Add(candidate);
                }
            }
        }

        if (!force && desired.SetEquals(_activeKeys))
        {
            return;
        }

        foreach (Vector2I key in desired)
        {
            TerrainChunk chunk = EnsureChunk(key);
            chunk.SetActive(true);
        }

        foreach (Vector2I key in _activeKeys)
        {
            if (desired.Contains(key))
            {
                continue;
            }

            if (_chunks.TryGetValue(key, out TerrainChunk chunk))
            {
                chunk.SetActive(false);
            }
        }

        _activeKeys.Clear();
        foreach (Vector2I key in desired)
        {
            _activeKeys.Add(key);
        }
    }

    private TerrainChunk EnsureChunk(Vector2I key)
    {
        if (_chunks.TryGetValue(key, out TerrainChunk existing))
        {
            return existing;
        }

        TerrainChunk chunk = ChunkScene.Instantiate<TerrainChunk>();
        AddChild(chunk);
        chunk.Generate(key, _config);
        _chunks[key] = chunk;
        return chunk;
    }

    private Vector2I ToChunkCoord(Vector3 worldPosition)
    {
        return new Vector2I(
            Mathf.FloorToInt((worldPosition.X + (ChunkSize * 0.5f)) / ChunkSize),
            Mathf.FloorToInt((worldPosition.Z + (ChunkSize * 0.5f)) / ChunkSize));
    }

    private TerrainChunkConfig BuildConfig()
    {
        return new TerrainChunkConfig
        {
            ChunkSize = ChunkSize,
            Resolution = ChunkResolution,
            Seed = Seed,
            BaseFrequency = BaseFrequency,
            DetailFrequency = DetailFrequency,
            HeightScale = HeightScale,
            DetailScale = DetailScale
        };
    }
}
