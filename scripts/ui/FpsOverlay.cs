using Godot;
using System;
using System.Text;
using TowerOfBaby.Entities.Motion;
using TowerOfBaby.Terrain;

namespace TowerOfBaby.UI;

public partial class FpsOverlay : CanvasLayer
{
    private const Key ExpandToggleKey = Key.F1;
    private const float PanelWidth = 352.0f;
    private const int DetailTrimLength = 96;

    [Export] public Vector2 Margin = new(12.0f, 12.0f);
    [Export] public NodePath TerrainWorldPath = new();
    [Export(PropertyHint.Range, "32,360,1")] public int SampleWindowSize = 180;
    [Export(PropertyHint.Range, "0.05,1.0,0.05")] public double MemoryRefreshIntervalSeconds = 0.25;
    [Export(PropertyHint.Range, "0.05,1.0,0.05")] public double TelemetryRefreshIntervalSeconds = 0.25;

    private readonly StringBuilder _detailBuilder = new();

    private PanelContainer _panel = null!;
    private OverlayGraphControl _graphControl = null!;
    private Label _detailLabel = null!;
    private TerrainWorld _terrainWorld = null!;
    private ILocomotionTelemetrySource _locomotionTelemetrySource = null!;
    private float[] _frameTimesMs = Array.Empty<float>();
    private float[] _fpsSamples = Array.Empty<float>();
    private float[] _ramSamplesMiB = Array.Empty<float>();
    private int _sampleIndex;
    private int _sampleCount;
    private double _uptimeSeconds;
    private double _memoryRefreshAccumulator;
    private double _telemetryRefreshAccumulator;
    private double _churnAccumulatorSeconds;
    private float _currentFps;
    private float _averageFrameMs;
    private float _worstFrameMs;
    private float _workingSetMiB;
    private float _gcMiB;
    private bool _isExpanded;
    private long _lastChurnHits;
    private long _lastChurnMisses;
    private long _lastChurnEvictions;
    private long _churnHitsDelta;
    private long _churnMissesDelta;
    private long _churnEvictionsDelta;
    private bool _hasChurnBaseline;
    private TerrainWorldProfileSnapshot _latestTerrainSnapshot = null!;
    private LocomotionTelemetrySnapshot _latestLocomotionSnapshot = null!;

    public override void _Ready()
    {
        int capacity = Mathf.Max(32, SampleWindowSize);
        _frameTimesMs = new float[capacity];
        _fpsSamples = new float[capacity];
        _ramSamplesMiB = new float[capacity];

        BuildUi();

        _terrainWorld = GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _locomotionTelemetrySource = ResolveLocomotionTelemetrySource();

        RefreshMemoryStats();
        RefreshTelemetry(0.0, forceTextRefresh: true);
        UpdateOverlayState(avgFrameMs: 0.0f, worstFrameMs: 0.0f);
    }

    public override void _Process(double delta)
    {
        _uptimeSeconds += delta;
        _memoryRefreshAccumulator += delta;
        _telemetryRefreshAccumulator += delta;

        if (_panel.Position != Margin)
        {
            _panel.Position = Margin;
        }

        if (_memoryRefreshAccumulator >= MemoryRefreshIntervalSeconds || _sampleCount == 0)
        {
            _memoryRefreshAccumulator = 0.0;
            RefreshMemoryStats();
        }

        float frameMs = (float)(delta * 1000.0);
        AddGraphSample(frameMs, _workingSetMiB);
        ComputeFrameStats(out _averageFrameMs, out _worstFrameMs);
        _currentFps = FrameMsToFps(_averageFrameMs);
        UpdateLatestFpsSample(_currentFps);

        if (_telemetryRefreshAccumulator >= TelemetryRefreshIntervalSeconds)
        {
            double telemetryElapsed = _telemetryRefreshAccumulator;
            _telemetryRefreshAccumulator = 0.0;
            RefreshTelemetry(telemetryElapsed, forceTextRefresh: _isExpanded);
        }

        UpdateOverlayState(_averageFrameMs, _worstFrameMs);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent ||
            !keyEvent.Pressed ||
            keyEvent.Echo ||
            keyEvent.Keycode != ExpandToggleKey)
        {
            return;
        }

        _isExpanded = !_isExpanded;
        _detailLabel.Visible = _isExpanded;
        RefreshTelemetry(0.0, forceTextRefresh: true);
        UpdateOverlayState(_averageFrameMs, _worstFrameMs);
        GetViewport().SetInputAsHandled();
    }

    private void BuildUi()
    {
        Label existingLabel = GetNodeOrNull<Label>("Label");

        _panel = new PanelContainer
        {
            Name = "Panel",
            Position = Margin,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        StyleBoxFlat style = new()
        {
            BgColor = new Color(0.03f, 0.05f, 0.07f, 0.82f),
            BorderColor = new Color(0.31f, 0.42f, 0.50f, 0.9f),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusBottomLeft = 8,
            ContentMarginLeft = 10.0f,
            ContentMarginTop = 10.0f,
            ContentMarginRight = 10.0f,
            ContentMarginBottom = 10.0f,
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1
        };
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        VBoxContainer content = new()
        {
            Name = "Content",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        content.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(content);

        _graphControl = new OverlayGraphControl
        {
            Name = "Graphs",
            CustomMinimumSize = new Vector2(PanelWidth - 20.0f, 132.0f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        content.AddChild(_graphControl);

        _detailLabel = existingLabel ?? new Label();
        if (_detailLabel.GetParent() != null)
        {
            _detailLabel.GetParent().RemoveChild(_detailLabel);
        }

        _detailLabel.Name = "Details";
        _detailLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _detailLabel.CustomMinimumSize = new Vector2(PanelWidth - 20.0f, 0.0f);
        _detailLabel.Modulate = new Color(0.92f, 0.96f, 1.0f, 0.96f);
        _detailLabel.Visible = false;
        _detailLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        _detailLabel.AddThemeFontSizeOverride("font_size", 13);
        content.AddChild(_detailLabel);
    }

    private void AddGraphSample(float frameMs, float ramMiB)
    {
        _frameTimesMs[_sampleIndex] = frameMs;
        _fpsSamples[_sampleIndex] = 0.0f;
        _ramSamplesMiB[_sampleIndex] = ramMiB;
        _sampleIndex = (_sampleIndex + 1) % _frameTimesMs.Length;
        _sampleCount = Mathf.Min(_sampleCount + 1, _frameTimesMs.Length);
    }

    private void UpdateLatestFpsSample(float fps)
    {
        if (_sampleCount == 0)
        {
            return;
        }

        int latestSampleIndex = (_sampleIndex - 1 + _fpsSamples.Length) % _fpsSamples.Length;
        _fpsSamples[latestSampleIndex] = fps;
    }

    private void ComputeFrameStats(out float avgFrameMs, out float worstFrameMs)
    {
        avgFrameMs = 0.0f;
        worstFrameMs = 0.0f;

        for (int i = 0; i < _sampleCount; i++)
        {
            avgFrameMs += _frameTimesMs[i];
            worstFrameMs = Mathf.Max(worstFrameMs, _frameTimesMs[i]);
        }

        if (_sampleCount > 0)
        {
            avgFrameMs /= _sampleCount;
        }
    }

    private static float FrameMsToFps(float frameMs)
    {
        return frameMs > Mathf.Epsilon
            ? 1000.0f / frameMs
            : 0.0f;
    }

    private void RefreshMemoryStats()
    {
        _workingSetMiB = BytesToMiB(System.Environment.WorkingSet);
        _gcMiB = BytesToMiB(GC.GetTotalMemory(false));
    }

    private void RefreshTelemetry(double elapsedSeconds, bool forceTextRefresh)
    {
        _terrainWorld ??= GetNodeOrNull<TerrainWorld>(TerrainWorldPath) ?? GetTree().GetFirstNodeInGroup("terrain_world") as TerrainWorld;
        _locomotionTelemetrySource ??= ResolveLocomotionTelemetrySource();

        _latestTerrainSnapshot = _terrainWorld?.GetProfileSnapshot();
        _latestLocomotionSnapshot = _locomotionTelemetrySource?.GetLocomotionTelemetrySnapshot();
        UpdateChurn(_latestTerrainSnapshot, elapsedSeconds);

        if (_isExpanded || forceTextRefresh)
        {
            _detailLabel.Text = BuildExpandedText(_latestTerrainSnapshot, _latestLocomotionSnapshot);
        }
    }

    private void UpdateChurn(TerrainWorldProfileSnapshot snapshot, double elapsedSeconds)
    {
        if (snapshot == null)
        {
            _hasChurnBaseline = false;
            _churnAccumulatorSeconds = 0.0;
            _churnHitsDelta = 0;
            _churnMissesDelta = 0;
            _churnEvictionsDelta = 0;
            return;
        }

        if (!_hasChurnBaseline)
        {
            _lastChurnHits = snapshot.CacheHits;
            _lastChurnMisses = snapshot.CacheMisses;
            _lastChurnEvictions = snapshot.EvictedChunks;
            _churnHitsDelta = 0;
            _churnMissesDelta = 0;
            _churnEvictionsDelta = 0;
            _churnAccumulatorSeconds = 0.0;
            _hasChurnBaseline = true;
            return;
        }

        _churnAccumulatorSeconds += elapsedSeconds;
        if (_churnAccumulatorSeconds < 1.0)
        {
            return;
        }

        _churnAccumulatorSeconds = 0.0;
        _churnHitsDelta = snapshot.CacheHits - _lastChurnHits;
        _churnMissesDelta = snapshot.CacheMisses - _lastChurnMisses;
        _churnEvictionsDelta = snapshot.EvictedChunks - _lastChurnEvictions;
        _lastChurnHits = snapshot.CacheHits;
        _lastChurnMisses = snapshot.CacheMisses;
        _lastChurnEvictions = snapshot.EvictedChunks;
    }

    private void UpdateOverlayState(float avgFrameMs, float worstFrameMs)
    {
        _graphControl.FpsSamples = _fpsSamples;
        _graphControl.RamSamplesMiB = _ramSamplesMiB;
        _graphControl.SampleIndex = _sampleIndex;
        _graphControl.SampleCount = _sampleCount;
        _graphControl.CurrentFps = _currentFps;
        _graphControl.AverageFrameMs = avgFrameMs;
        _graphControl.WorstFrameMs = worstFrameMs;
        _graphControl.WorkingSetMiB = _workingSetMiB;
        _graphControl.GcMiB = _gcMiB;
        _graphControl.Expanded = _isExpanded;
        _graphControl.QueueRedraw();
    }

    private string BuildExpandedText(TerrainWorldProfileSnapshot snapshot, LocomotionTelemetrySnapshot locomotionSnapshot)
    {
        _detailBuilder.Clear();
        TerrainGrassSystem grassSystem = _terrainWorld?.GetNodeOrNull<TerrainGrassSystem>("TerrainGrassSystem");
        _detailBuilder.AppendLine(
            $"Perf {_currentFps:0} fps  avg {_averageFrameMs:0.00} ms  worst {_worstFrameMs:0.00} ms  up {_uptimeSeconds:0.0}s");
        _detailBuilder.AppendLine($"Memory RSS {_workingSetMiB:0} MiB  GC {_gcMiB:0} MiB");

        if (snapshot == null)
        {
            _detailBuilder.AppendLine("Terrain unavailable");
        }
        else if (snapshot.SearchThrottleState == "retired")
        {
            _detailBuilder.AppendLine("Terrain runtime retired from active gameplay");
            _detailBuilder.AppendLine($"Runtime {TrimForOverlay(snapshot.MeshBackendName, 84)}");
            _detailBuilder.AppendLine($"Preserved {TrimForOverlay(snapshot.TrackedBiomeSummary, 84)}");
            _detailBuilder.AppendLine($"Retired {TrimForOverlay(snapshot.TrackedDetailSummary, 84)}");
            _detailBuilder.AppendLine($"Hook {TrimForOverlay(snapshot.TrackedCoverageStateSummary, 84)}");
            _detailBuilder.AppendLine($"Scene {TrimForOverlay(snapshot.LastChunkSourceSummary, 84)}");
        }
        else if (snapshot.SearchThrottleState == "lod_blocks")
        {
            _detailBuilder.AppendLine(
                $"Terrain init {snapshot.InitialLoadProgress * 100.0f:0}%  desired {snapshot.DesiredChunkCount}  visible {snapshot.ActiveChunkCount}  release {snapshot.ToReleaseCount}");
            _detailBuilder.AppendLine(
                $"Bubble parents {snapshot.NearPlayerBubbleParentCount}  refine blocks {snapshot.RefinedSameLodBlockCount}  handoffs {snapshot.RefinementHandoffCount}");
            _detailBuilder.AppendLine(
                $"Retain held {snapshot.HysteresisRetainedBlockCount}  defer h/c {snapshot.LastReleaseDeferralsHysteresisCount}/{snapshot.LastReleaseDeferralsCoverageCount}");
            _detailBuilder.AppendLine(
                $"Supersede active {snapshot.ActiveSupersededBlockTransitionCount}  wait r/v/h/p/f {snapshot.WaitingForMarkReleasableSupersededBlockCount}/{snapshot.WaitingForVisualCoverageSupersededBlockCount}/{snapshot.WaitingForHideSupersededBlockCount}/{snapshot.WaitingForPhysicsCoverageSupersededBlockCount}/{snapshot.WaitingForReleaseSupersededBlockCount}");
            _detailBuilder.AppendLine(
                $"Churn set/create/release {snapshot.BlockSetChangeRatePerSecond:0.0}/{snapshot.BlockCreateRatePerSecond:0.0}/{snapshot.BlockReleaseRatePerSecond:0.0} per s");
            _detailBuilder.AppendLine($"Runtime {TrimForOverlay(snapshot.MeshBackendName, 84)}");
            _detailBuilder.AppendLine($"Viewer {TrimForOverlay(snapshot.TrackedBiomeSummary, 84)}");
            _detailBuilder.AppendLine($"Lifecycle {TrimForOverlay(snapshot.TrackedDetailSummary, 84)}");
            _detailBuilder.AppendLine($"LOD {TrimForOverlay(snapshot.TrackedCoverageStateSummary, 84)}");
            _detailBuilder.AppendLine($"Supersede last {TrimForOverlay(snapshot.LastSupersededBlockTransitionSummary, 84)}");
            _detailBuilder.AppendLine($"Select {TrimForOverlay(snapshot.LastSelectedChunkSummary, 84)}");
            _detailBuilder.AppendLine($"Handoff {TrimForOverlay(snapshot.LastRefinementHandoffSummary, 84)}");
            _detailBuilder.AppendLine($"Latest {TrimForOverlay(snapshot.LastChunkSourceSummary, 84)}");
            _detailBuilder.AppendLine($"Release {TrimForOverlay(snapshot.LastReleasedChunkSummary, 84)}");
            if (grassSystem != null)
            {
                _detailBuilder.AppendLine($"Grass {TrimForOverlay(grassSystem.GetDebugSummary(), 84)}");
            }
        }
        else
        {
            _detailBuilder.AppendLine(
                $"Terrain init {snapshot.InitialLoadProgress * 100.0f:0}%  hit {ComputeHitRate(snapshot.CacheHits, snapshot.CacheMisses):0}%  churn h/m/e {_churnHitsDelta}/{_churnMissesDelta}/{_churnEvictionsDelta}");
            _detailBuilder.AppendLine(
                $"Biome current {snapshot.TrackedBiomeId}  {TrimForOverlay(snapshot.TrackedBiomeSummary, 84)}");
            _detailBuilder.AppendLine(
                $"Struct current {snapshot.TrackedStructureCount} {snapshot.TrackedStructureType} {(snapshot.TrackedStructureRequestsHigherDetail ? "hi" : "std")}  {TrimForOverlay(snapshot.TrackedStructureSummary, 84)}");
            _detailBuilder.AppendLine(
                $"Detail current {snapshot.TrackedDetailRegionCount} max {snapshot.TrackedMaxDetailLevel} dirty {snapshot.TrackedDirtyDetailRegionCount}  {TrimForOverlay(snapshot.TrackedDetailSummary, 84)}");
            _detailBuilder.AppendLine(
                $"Detail hi current {(snapshot.TrackedDetailBrickActive ? "on" : "off")} tri {snapshot.TrackedDetailBrickTriangleCount} replace {snapshot.TrackedDetailBrickReplaceCoarseCellCount}  {TrimForOverlay(snapshot.TrackedDetailBrickSummary, 84)}");
            _detailBuilder.AppendLine(
                $"Edit hi current {(snapshot.TrackedEditedDetailActive ? "on" : "off")} tri {snapshot.TrackedEditedDetailTriangleCount} replace {snapshot.TrackedEditedReplaceCoarseCellCount}  {TrimForOverlay(snapshot.TrackedEditedDetailSummary, 84)}");
            _detailBuilder.AppendLine(
                $"Dirty current r {TrimForOverlay(snapshot.TrackedRenderDirtyBoundsSummary, 42)}  c {TrimForOverlay(snapshot.TrackedCollisionDirtyBoundsSummary, 42)}");
            _detailBuilder.AppendLine(
                $"Chunks active {snapshot.ActiveChunkCount}  resident {snapshot.ResidentChunkCount}  desired {snapshot.DesiredChunkCount}  ram {snapshot.RamCacheChunkCount}  in-flight {snapshot.InFlightChunkCount}");
            _detailBuilder.AppendLine(
                $"Loads run {snapshot.RunningLoadCount}  queued {snapshot.PendingLoadCount}  prepared {snapshot.PreparedChunkCount}  activate {snapshot.PendingActivationCount}  add {snapshot.ToAddCount}  release {snapshot.ToReleaseCount}");
            _detailBuilder.AppendLine(
                $"Search {snapshot.SearchThrottleState}  frontier {snapshot.FrontierSize}  visited {snapshot.VisitedCandidateCount}  invalid {snapshot.SearchInvalidationCount}");
            _detailBuilder.AppendLine(
                $"Timing search {snapshot.LastDesiredSearchMs:0.00} ms  priority {snapshot.LastPriorityEvaluationMs:0.00} ms  visibility {snapshot.LastVisibilityHeuristicMs:0.00} ms");
            _detailBuilder.AppendLine(
                $"Ops load {snapshot.LastChunkLoadCount}/{snapshot.LastChunkLoadMs:0.00} ms  release {snapshot.LastChunkReleaseCount}/{snapshot.LastChunkReleaseMs:0.00} ms  worker {snapshot.LastMeshWorkerBuildCount}/{snapshot.LastMeshWorkerBuildMs:0.00} ms  commit {snapshot.LastVisualRebuildCount}/{snapshot.LastVisualRebuildMs:0.00} ms");
            if (snapshot.TerrainStatsEnabled)
            {
                _detailBuilder.AppendLine(
                    $"Deform {snapshot.DeformOperationCount} ops  last {snapshot.LastDeformKind} {snapshot.LastDeformMs:0.00} ms  chunks {snapshot.LastDeformEditedChunkCount}/{ComputeAverage(snapshot.TotalEditedChunkCount, snapshot.DeformOperationCount):0.0} avg  samples {snapshot.LastDeformEditedSampleCount}/{ComputeAverage(snapshot.TotalEditedSampleCount, snapshot.DeformOperationCount):0.0} avg");
                _detailBuilder.AppendLine(
                    $"Edit detail dirty {snapshot.LastDeformDirtyBoundsVolume:0.0}/{ComputeAverage(snapshot.TotalEditedDirtyBoundsVolume, snapshot.DeformOperationCount):0.0} avg  promotions {snapshot.LastDeformEditDetailPromotionCount}/{ComputeAverage(snapshot.EditDetailPromotionCount, snapshot.DeformOperationCount):0.0} avg");
                _detailBuilder.AppendLine(
                    $"Terrain prof {snapshot.MeshBackendName}  worker {snapshot.MeshBuildWorkerCount}/{snapshot.MeshBuildWorkerMs:0.00} ms  commit {snapshot.MeshRebuildCount}/{snapshot.MeshRebuildMs:0.00} ms  collision {snapshot.CollisionRebuildCount}/{snapshot.CollisionRebuildMs:0.00} ms");
                _detailBuilder.AppendLine(
                    $"Mesh queue build {snapshot.PendingMeshBuildCount}/{snapshot.DeferredMeshBuildCount}/{snapshot.RunningMeshBuildCount}  hi/lo {snapshot.HighPriorityMeshQueueDepth}/{snapshot.LowPriorityMeshQueueDepth}  wait {snapshot.LastMeshWorkerQueueWaitMs:0.00}/{snapshot.AverageMeshWorkerQueueWaitMs:0.00}/{snapshot.PeakMeshWorkerQueueWaitMs:0.00} ms  low-pri defer {snapshot.LowPriorityDeferredMeshBuildCount} skip {snapshot.LastSkippedLowPriorityMeshBuildCount}/{snapshot.SkippedLowPriorityMeshBuildCount} suppress {snapshot.LastSuppressedDuplicateMeshBuildCount}/{snapshot.SuppressedDuplicateMeshBuildCount}");
                _detailBuilder.AppendLine(
                    $"Pressure {(snapshot.PressureModeActive ? "on" : "off")}  frames {snapshot.PressureModeActiveFrameCount}  activations {snapshot.PressureModeActivationCount}  commit {snapshot.PendingMeshCommitCount}  detail defer {snapshot.LastDeferredDetailPromotionCount}/{snapshot.DeferredDetailPromotionCount}  coalesced {snapshot.LastCoalescedRebuildRequestCount}/{snapshot.CoalescedRebuildRequestCount}");
                _detailBuilder.AppendLine(
                    $"Worker heap coarse/detail avg {snapshot.AverageCoarseMeshWorkerHeapDeltaKiB:0.0}/{snapshot.AverageDetailMeshWorkerHeapDeltaKiB:0.0} KiB  max {snapshot.PeakCoarseMeshWorkerHeapDeltaKiB:0.0}/{snapshot.PeakDetailMeshWorkerHeapDeltaKiB:0.0} KiB");
            }
            else
            {
                _detailBuilder.AppendLine("Terrain prof disabled");
            }
            _detailBuilder.AppendLine(
                $"Startup keys {snapshot.StartupSnapshotChunkCount}  coverage {snapshot.StartupDesiredCoverageCount}/{Mathf.Max(1, snapshot.DesiredChunkCount)}  records {snapshot.PersistedChunkRecordCount}");
            _detailBuilder.AppendLine(
                $"Source resident {snapshot.ResidentReuseHits}  ram {snapshot.RamCacheHits}  startup {snapshot.StartupSnapshotHits}  db {snapshot.DatabaseHits}  gen {snapshot.GenerationFallbacks}");
            _detailBuilder.AppendLine(
                $"Writes dirty {snapshot.DirtyPersistWrites}  startup->db {snapshot.StartupPromotionWrites}  evicted {snapshot.EvictedChunks}");
            _detailBuilder.AppendLine($"Selected {TrimForOverlay(snapshot.LastSelectedChunkSummary)}");
            _detailBuilder.AppendLine($"Released {TrimForOverlay(snapshot.LastReleasedChunkSummary)}");
            _detailBuilder.AppendLine($"Source {TrimForOverlay(snapshot.LastChunkSourceSummary)}");
            if (grassSystem != null)
            {
                _detailBuilder.AppendLine($"Grass {TrimForOverlay(grassSystem.GetDebugSummary())}");
            }
        }

        if (locomotionSnapshot == null)
        {
            _detailBuilder.Append("Locomotion unavailable");
            return _detailBuilder.ToString();
        }

        _detailBuilder.AppendLine(
            $"Locomotion desired {locomotionSnapshot.DesiredMovement.Length():0.00}  actual {locomotionSnapshot.ActualMovement.Length():0.00}  stance {locomotionSnapshot.StanceWidth:0.00}");
        _detailBuilder.AppendLine(
            $"Facing {FormatVector(locomotionSnapshot.FacingDirection)}  ground {FormatVector(locomotionSnapshot.GroundNormal)}");
        _detailBuilder.AppendLine(BuildFootLine(locomotionSnapshot.LeftFoot));
        _detailBuilder.Append(BuildFootLine(locomotionSnapshot.RightFoot));
        return _detailBuilder.ToString();
    }

    private static string BuildFootLine(LocomotionFootTelemetry telemetry)
    {
        return
            $"{telemetry.Side} {telemetry.State}  v {telemetry.ThresholdViolation:0.00}  skate {telemetry.FootSkateDistance:0.000}  " +
            $"step {telemetry.CurrentStepDuration:0.00}/{telemetry.LastStepDuration:0.00}s  {TrimForOverlay(telemetry.DecisionReason, 56)}";
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.X:0.00},{value.Y:0.00},{value.Z:0.00})";
    }

    private static string TrimForOverlay(string value, int maxLength = DetailTrimLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "--";
        }

        string singleLine = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine[..Math.Max(0, maxLength - 3)] + "...";
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

    private static double ComputeAverage(long total, long count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        return (double)total / count;
    }

    private static double ComputeAverage(double total, long count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        return total / count;
    }

    private ILocomotionTelemetrySource ResolveLocomotionTelemetrySource()
    {
        return GetTree().GetFirstNodeInGroup("locomotion_telemetry_source") as ILocomotionTelemetrySource;
    }

    private sealed partial class OverlayGraphControl : Control
    {
        private static readonly float[] FpsCeilings = { 30.0f, 60.0f, 90.0f, 120.0f, 144.0f, 165.0f, 240.0f, 360.0f };

        private readonly Color _fpsLineColor = new(0.45f, 0.95f, 0.50f, 0.96f);
        private readonly Color _fpsGridColor = new(0.23f, 0.34f, 0.29f, 0.70f);
        private readonly Color _ramLineColor = new(0.48f, 0.72f, 1.0f, 0.96f);
        private readonly Color _ramGridColor = new(0.24f, 0.30f, 0.40f, 0.70f);
        private readonly Color _graphBackgroundColor = new(0.08f, 0.12f, 0.16f, 0.62f);
        private readonly Color _frameColor = new(0.24f, 0.35f, 0.41f, 0.95f);
        private readonly Color _titleColor = new(0.95f, 0.98f, 1.0f, 0.98f);

        public float[] FpsSamples { get; set; } = Array.Empty<float>();
        public float[] RamSamplesMiB { get; set; } = Array.Empty<float>();
        public int SampleIndex { get; set; }
        public int SampleCount { get; set; }
        public float CurrentFps { get; set; }
        public float AverageFrameMs { get; set; }
        public float WorstFrameMs { get; set; }
        public float WorkingSetMiB { get; set; }
        public float GcMiB { get; set; }
        public bool Expanded { get; set; }

        public override void _Draw()
        {
            Font font = GetThemeDefaultFont();
            if (font == null)
            {
                return;
            }

            const float padding = 4.0f;
            const float titleHeight = 14.0f;
            const float graphHeight = 41.0f;
            const float sectionGap = 10.0f;
            const int fontSize = 12;
            float graphWidth = Mathf.Max(40.0f, Size.X - (padding * 2.0f));
            Rect2 fpsTitleRect = new(padding, padding, graphWidth, titleHeight);
            Rect2 fpsGraphRect = new(padding, fpsTitleRect.End.Y + 2.0f, graphWidth, graphHeight);
            Rect2 ramTitleRect = new(padding, fpsGraphRect.End.Y + sectionGap, graphWidth, titleHeight);
            Rect2 ramGraphRect = new(padding, ramTitleRect.End.Y + 2.0f, graphWidth, graphHeight);

            float fpsCeiling = ComputeFpsCeiling();
            string fpsText = $"FPS {CurrentFps:0} | avg {AverageFrameMs:0.0} ms | worst {WorstFrameMs:0.0} ms";
            DrawString(font, new Vector2(fpsTitleRect.Position.X, fpsTitleRect.Position.Y + 11.0f), fpsText, HorizontalAlignment.Left, -1.0f, fontSize, _titleColor);
            DrawGraph(fpsGraphRect, FpsSamples, minValue: 0.0f, maxValue: fpsCeiling, _fpsLineColor, _fpsGridColor, showSixtyLine: true);

            ComputeRamRange(out float ramMin, out float ramMax);
            string ramText = $"RAM {WorkingSetMiB:0} MiB | GC {GcMiB:0} MiB";
            DrawString(font, new Vector2(ramTitleRect.Position.X, ramTitleRect.Position.Y + 11.0f), ramText, HorizontalAlignment.Left, -1.0f, fontSize, _titleColor);
            DrawGraph(ramGraphRect, RamSamplesMiB, ramMin, ramMax, _ramLineColor, _ramGridColor, showSixtyLine: false);
        }

        private void DrawGraph(Rect2 rect, float[] samples, float minValue, float maxValue, Color lineColor, Color gridColor, bool showSixtyLine)
        {
            DrawRect(rect, _graphBackgroundColor, filled: true);
            DrawRect(rect, _frameColor, filled: false, width: 1.0f);

            DrawHorizontalGuide(rect, 0.25f, gridColor);
            DrawHorizontalGuide(rect, 0.5f, gridColor);
            DrawHorizontalGuide(rect, 0.75f, gridColor);

            if (showSixtyLine && maxValue >= 60.0f)
            {
                float normalized = Mathf.Clamp((60.0f - minValue) / Mathf.Max(0.001f, maxValue - minValue), 0.0f, 1.0f);
                DrawHorizontalGuide(rect, normalized, new Color(lineColor.R, lineColor.G, lineColor.B, 0.28f));
            }

            int count = Mathf.Min(SampleCount, samples.Length);
            if (count <= 0)
            {
                return;
            }

            float span = Mathf.Max(0.001f, maxValue - minValue);
            int startIndex = SampleCount >= samples.Length ? SampleIndex : 0;
            Vector2 previousPoint = Vector2.Zero;

            for (int i = 0; i < count; i++)
            {
                int sampleArrayIndex = (startIndex + i) % samples.Length;
                float rawValue = samples[sampleArrayIndex];
                float normalized = Mathf.Clamp((rawValue - minValue) / span, 0.0f, 1.0f);
                float x = count == 1
                    ? rect.Position.X + (rect.Size.X * 0.5f)
                    : rect.Position.X + (rect.Size.X * i / (count - 1.0f));
                float y = rect.End.Y - (normalized * rect.Size.Y);
                Vector2 point = new(x, y);

                if (i > 0)
                {
                    DrawLine(previousPoint, point, lineColor, 1.8f, antialiased: true);
                }

                previousPoint = point;
            }

            DrawCircle(previousPoint, 2.3f, lineColor);
        }

        private void DrawHorizontalGuide(Rect2 rect, float normalizedHeight, Color color)
        {
            float clamped = Mathf.Clamp(normalizedHeight, 0.0f, 1.0f);
            float y = rect.End.Y - (rect.Size.Y * clamped);
            DrawLine(new Vector2(rect.Position.X, y), new Vector2(rect.End.X, y), color, 1.0f, antialiased: true);
        }

        private float ComputeFpsCeiling()
        {
            float peak = Mathf.Max(60.0f, CurrentFps);
            int count = Mathf.Min(SampleCount, FpsSamples.Length);
            int startIndex = SampleCount >= FpsSamples.Length ? SampleIndex : 0;
            for (int i = 0; i < count; i++)
            {
                int sampleArrayIndex = (startIndex + i) % FpsSamples.Length;
                peak = Mathf.Max(peak, FpsSamples[sampleArrayIndex]);
            }

            foreach (float candidate in FpsCeilings)
            {
                if (peak <= candidate)
                {
                    return candidate;
                }
            }

            return Mathf.Ceil(peak / 120.0f) * 120.0f;
        }

        private void ComputeRamRange(out float minValue, out float maxValue)
        {
            minValue = WorkingSetMiB;
            maxValue = WorkingSetMiB;

            int count = Mathf.Min(SampleCount, RamSamplesMiB.Length);
            int startIndex = SampleCount >= RamSamplesMiB.Length ? SampleIndex : 0;
            for (int i = 0; i < count; i++)
            {
                int sampleArrayIndex = (startIndex + i) % RamSamplesMiB.Length;
                float sample = RamSamplesMiB[sampleArrayIndex];
                minValue = Mathf.Min(minValue, sample);
                maxValue = Mathf.Max(maxValue, sample);
            }

            float span = Mathf.Max(32.0f, maxValue - minValue);
            float padding = Mathf.Max(8.0f, span * 0.18f);
            minValue = Mathf.Max(0.0f, minValue - padding);
            maxValue += padding;

            if (Mathf.IsEqualApprox(minValue, maxValue))
            {
                maxValue = minValue + 32.0f;
            }
        }
    }
}
