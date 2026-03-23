using Godot;
using System.Collections.Generic;

public partial class HumanoidNpcSpawner : Node3D
{
    [Export] public PackedScene HumanoidScene = null!;
    [Export] public NodePath TerrainWorldPath = new("../TerrainWorld");
    [Export] public NodePath PlayerPath = new("../Player");
    [Export] public int DesiredNpcCount = 1;
    [Export] public float SpawnRadiusMin = 18.0f;
    [Export] public float SpawnRadiusMax = 48.0f;
    [Export] public float DespawnDistance = 84.0f;
    [Export] public float RespawnIntervalSeconds = 1.5f;
    [Export] public float SpawnProbeHeight = 32.0f;
    [Export] public int SpawnAttemptsPerCycle = 8;

    private readonly RandomNumberGenerator _rng = new();
    private readonly List<HumanoidController> _npcs = new();
    private TerrainWorld _terrainWorld = null!;
    private Node3D _player = null!;
    private double _respawnAccumulator;

    public override void _Ready()
    {
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _player = GetNodeOrNull<Node3D>(PlayerPath) ?? GetParent()?.GetNodeOrNull<Node3D>("Player");
        _rng.Randomize();
    }

    public override void _Process(double delta)
    {
        CullInvalidNpcs();
        DespawnUnsupportedNpcs();

        _respawnAccumulator += delta;
        if (_respawnAccumulator < RespawnIntervalSeconds)
        {
            return;
        }

        _respawnAccumulator = 0.0;
        while (_npcs.Count < DesiredNpcCount)
        {
            if (!TrySpawnNpc())
            {
                break;
            }
        }
    }

    private void CullInvalidNpcs()
    {
        for (int i = _npcs.Count - 1; i >= 0; i--)
        {
            if (!IsInstanceValid(_npcs[i]))
            {
                _npcs.RemoveAt(i);
            }
        }
    }

    private void DespawnUnsupportedNpcs()
    {
        if (_terrainWorld == null || _player == null)
        {
            return;
        }

        for (int i = _npcs.Count - 1; i >= 0; i--)
        {
            HumanoidController npc = _npcs[i];
            if (!IsInstanceValid(npc))
            {
                _npcs.RemoveAt(i);
                continue;
            }

            bool tooFar = npc.GlobalPosition.DistanceTo(_player.GlobalPosition) > DespawnDistance;
            bool unsupported = !_terrainWorld.IsColumnActiveAtPosition(npc.GlobalPosition);
            if (!tooFar && !unsupported)
            {
                continue;
            }

            npc.QueueFree();
            _npcs.RemoveAt(i);
        }
    }

    private bool TrySpawnNpc()
    {
        if (_terrainWorld == null || _player == null || HumanoidScene == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < SpawnAttemptsPerCycle; attempt++)
        {
            Vector2 offset = Vector2.Right.Rotated(_rng.RandfRange(0.0f, Mathf.Tau)) * _rng.RandfRange(SpawnRadiusMin, SpawnRadiusMax);
            Vector3 probePoint = _player.GlobalPosition + new Vector3(offset.X, SpawnProbeHeight, offset.Y);
            if (!_terrainWorld.IsColumnActiveAtPosition(probePoint))
            {
                continue;
            }

            PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(
                probePoint,
                probePoint + (Vector3.Down * SpawnProbeHeight * 2.0f));
            query.CollideWithAreas = false;

            var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (result.Count == 0)
            {
                continue;
            }

            Vector3 spawnPosition = (Vector3)result["position"] + new Vector3(0.0f, 0.2f, 0.0f);
            HumanoidController npc = HumanoidScene.Instantiate<HumanoidController>();
            npc.ControlMode = HumanoidController.HumanoidControlMode.RandomWalk;
            npc.EnableFollowCamera = false;
            npc.ActsAsTerrainTracker = false;
            npc.RandomizeBodySeedOnReady = true;
            npc.Position = ToLocal(spawnPosition);
            AddChild(npc);
            _npcs.Add(npc);
            return true;
        }

        return false;
    }
}
