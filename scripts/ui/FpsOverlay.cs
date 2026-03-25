using Godot;
using System;
using System.Diagnostics;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.UI;

public partial class FpsOverlay : CanvasLayer
{
    [Export] public Vector2 Margin = new(12.0f, 12.0f);
    [Export] public NodePath TerrainWorldPath = new();
    [Export] public int SampleWindowSize = 120;

    private Label _label = null!;
    private TerrainWorld _terrainWorld = null!;
    private float[] _frameTimesMs = Array.Empty<float>();
    private int _sampleIndex;
    private int _sampleCount;
    private double _uptimeSeconds;
    private double _churnAccumulatorSeconds;
    private long _lastChurnHits;
    private long _lastChurnMisses;
    private long _lastChurnEvictions;
    private long _churnHitsDelta;
    private long _churnMissesDelta;
    private long _churnEvictionsDelta;

    public override void _Ready()
    {
        _frameTimesMs = new float[Mathf.Max(16, SampleWindowSize)];
        _label = GetNodeOrNull<Label>("Label");
        if (_label == null)
        {
            _label = new Label { Name = "Label" };
            AddChild(_label);
        }

        _label.Position = Margin;
        _label.Modulate = Colors.White;
        _label.Text = "FPS: --";
        _label.AutowrapMode = TextServer.AutowrapMode.Off;
        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
    }

    public override void _Process(double delta)
    {
        _uptimeSeconds += delta;

        float frameMs = (float)(delta * 1000.0);
        _frameTimesMs[_sampleIndex] = frameMs;
        _sampleIndex = (_sampleIndex + 1) % _frameTimesMs.Length;
        _sampleCount = Mathf.Min(_sampleCount + 1, _frameTimesMs.Length);

        float avgFrameMs = 0.0f;
        float worstFrameMs = 0.0f;
        for (int i = 0; i < _sampleCount; i++)
        {
            avgFrameMs += _frameTimesMs[i];
            worstFrameMs = Mathf.Max(worstFrameMs, _frameTimesMs[i]);
        }

        if (_sampleCount > 0)
        {
            avgFrameMs /= _sampleCount;
        }

        int fps = (int)Engine.GetFramesPerSecond();
        float gcMiB = BytesToMiB(GC.GetTotalMemory(false));
        float workingSetMiB = GetWorkingSetMiB();
        TerrainWorldProfileSnapshot snapshot = _terrainWorld?.GetProfileSnapshot();
        string terrainStats = _terrainWorld?.GetDebugStats() ?? "Voxel stats unavailable";

        _churnAccumulatorSeconds += delta;
        if (snapshot != null && _churnAccumulatorSeconds >= 1.0)
        {
            _churnAccumulatorSeconds = 0.0;
            _churnHitsDelta = snapshot.CacheHits - _lastChurnHits;
            _churnMissesDelta = snapshot.CacheMisses - _lastChurnMisses;
            _churnEvictionsDelta = snapshot.EvictedChunks - _lastChurnEvictions;
            _lastChurnHits = snapshot.CacheHits;
            _lastChurnMisses = snapshot.CacheMisses;
            _lastChurnEvictions = snapshot.EvictedChunks;
        }

        string summary = snapshot == null
            ? "Init progress: --"
            : $"Init: {(snapshot.InitialLoadProgress * 100.0f):0}% | hit rate: {ComputeHitRate(snapshot.CacheHits, snapshot.CacheMisses):0}% | churn h/m/e {_churnHitsDelta}/{_churnMissesDelta}/{_churnEvictionsDelta}";

        _label.Text =
            $"FPS: {fps} | avg {avgFrameMs:0.00} ms | worst {worstFrameMs:0.00} ms | uptime {_uptimeSeconds:0.0}s | RSS {workingSetMiB:0} MiB | GC {gcMiB:0} MiB\n" +
            $"{summary}\n" +
            $"{terrainStats}";
    }

    private static float GetWorkingSetMiB()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return BytesToMiB(process.WorkingSet64);
    }

    private static float BytesToMiB(long bytes)
    {
        return bytes / (1024.0f * 1024.0f);
    }

    private static float ComputeHitRate(long hits, long misses)
    {
        long total = hits + misses;
        if (total <= 0)
        {
            return 0.0f;
        }

        return (float)hits / total * 100.0f;
    }
}
