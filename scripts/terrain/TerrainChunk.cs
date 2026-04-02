using Godot;
using GodotArray = Godot.Collections.Array;
using System;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public enum TerrainDetailPromotionState
{
    Eligible = 0,
    DeferredPendingMesh = 1,
    DeferredCooldown = 2,
    DeferredWarmup = 3,
    DeferredPressure = 4,
    DeferredPromotionBudget = 5,
    DeferredStartupPriority = 6,
    DeferredCollisionBootstrap = 7,
    Queued = 8,
    Running = 9,
    Applied = 10
}

public partial class TerrainChunk : Node3D
{
    public const string EditedDetailRegionRequestId = "__edited_detail_payload";
    public const string EditedDetailRegionReason = "edited_detail";

    private static readonly StandardMaterial3D SharedTerrainMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Roughness = 1.0f,
        Metallic = 0.0f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
    };

    private static readonly StandardMaterial3D SharedTerrainTintMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Roughness = 1.0f,
        Metallic = 0.0f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
    };

    private static readonly StandardMaterial3D SharedTerrainDebugVertexColorMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Roughness = 1.0f,
        Metallic = 0.0f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
    };

    [Export] public int PointsPerAxis = 18;
    [Export] public float VoxelSize = 1.2f;

    public static void ConfigureSharedSurfaceMaterial(float roughness)
    {
        float clampedRoughness = Mathf.Clamp(roughness, 0.0f, 1.0f);
        SharedTerrainMaterial.Roughness = clampedRoughness;
        SharedTerrainTintMaterial.Roughness = clampedRoughness;
    }

    public Vector3I ChunkKey { get; private set; }
    public TerrainBiomeSample BiomeSample { get; private set; } = TerrainBiomeSample.Default;
    public TerrainChunkStructureMetadata StructureMetadata { get; private set; } = TerrainChunkStructureMetadata.Empty;
    public TerrainChunkDetailRegionManager DetailRegionManager { get; private set; } = new(1.0f);
    public TerrainChunkDirtyBoundsTracker RenderDirtyBoundsTracker { get; private set; } = new(1.0f, 1.0f, 2);
    public TerrainChunkDirtyBoundsTracker CollisionDirtyBoundsTracker { get; private set; } = new(1.0f, 1.0f, 2);
    public BiomeId BiomeId => BiomeSample.DominantBiome;
    public bool HasStructureInfluence => StructureMetadata.IsInInfluenceZone;
    public bool ShouldRequestHigherTerrainDetail => StructureMetadata.RequestHigherTerrainDetail;
    public bool HasDetailRegions => DetailRegionManager.HasRegions;
    public int DetailRegionCount => DetailRegionManager.RegionCount;
    public int DirtyDetailRegionCount => DetailRegionManager.DirtyRegionCount;
    public int MaxRequestedDetailLevel => DetailRegionManager.MaxDetailLevel;
    public string DetailRegionSourceSummary => DetailRegionManager.SourceSummary;
    public string DetailRegionSummary => DetailRegionManager.Summary;
    public bool HasDetailBrick => _data?.HasDetailBrick == true;
    public string DetailBrickSummary => _data?.DetailBrick?.Summary ?? "none";
    public bool HasEditedDetailBrick => _data?.HasEditedDetailBrick == true;
    public string EditedDetailBrickSummary => _data?.EditedDetailBrick?.Summary ?? "none";
    public bool HasRenderDirtyBounds => RenderDirtyBoundsTracker.HasBounds;
    public bool HasCollisionDirtyBounds => CollisionDirtyBoundsTracker.HasBounds;
    public TerrainChunkDirtyBoundsSnapshot RenderDirtyBounds => RenderDirtyBoundsTracker.Snapshot;
    public TerrainChunkDirtyBoundsSnapshot CollisionDirtyBounds => CollisionDirtyBoundsTracker.Snapshot;
    public string RenderDirtyBoundsSummary => RenderDirtyBounds.Summary;
    public string CollisionDirtyBoundsSummary => CollisionDirtyBounds.Summary;
    public float ChunkSize => _data?.ChunkSize ?? ((PointsPerAxis - 1) * VoxelSize);
    public bool HasData => _data != null;
    public VoxelChunkData Data => _data;
    public bool HasCollision => _collision?.Shape != null;
    public bool HasSurface => _mesh != null && _mesh.GetSurfaceCount() > 0;
    public bool HasCompletedInitialVisualBuild { get; private set; }
    public bool IsInitialVisualReady => HasData && HasCompletedInitialVisualBuild && !RenderDirty;
    public bool RenderDirty { get; private set; }
    public bool CollisionDirty { get; private set; }
    public bool HeldForCoverageSafety { get; private set; }
    public bool ReplacementCoveragePending { get; private set; }
    public bool SafeToRelease { get; private set; }
    public string CoverageHoldReason { get; private set; } = string.Empty;
    public double CollisionReadyAtSeconds { get; private set; }
    public double LastRenderBuildMs { get; private set; }
    public double LastCollisionBuildMs { get; private set; }
    public double LastVisualCommitAtSeconds { get; private set; }
    public double ActivatedAtSeconds { get; private set; }
    public ulong ActivatedFrame { get; private set; }
    public int LastTotalTriangleCount { get; private set; }
    public int LastDetailTriangleCount { get; private set; }
    public int LastReplacedCoarseCellCount { get; private set; }
    public int LastDetailCellCount { get; private set; }
    public bool LastUsedDetailBrick { get; private set; }
    public bool LastUsedPersistentDetailEdits { get; private set; }
    public bool PersistenceDirty { get; private set; }
    public TerrainChunkLoadSource LoadSource { get; private set; } = TerrainChunkLoadSource.ProceduralGeneration;
    public int RenderRevision => _renderRevision;
    public TerrainDetailPromotionState DetailPromotionState { get; private set; } = TerrainDetailPromotionState.Eligible;
    public string DetailPromotionDeferReason { get; private set; } = string.Empty;
    public ulong DetailPromotionNextEligibleFrame { get; private set; }
    public double DetailPromotionNextEligibleAtSeconds { get; private set; } = double.NegativeInfinity;
    public bool DetailPromotionReevaluationPending { get; private set; }
    public bool DetailPromotionFollowupRequested { get; private set; }
    public string DetailPromotionReactivationReason { get; private set; } = string.Empty;
    public bool IsDetailPromotionDeferred =>
        DetailPromotionState is TerrainDetailPromotionState.DeferredPendingMesh or
            TerrainDetailPromotionState.DeferredCooldown or
            TerrainDetailPromotionState.DeferredWarmup or
            TerrainDetailPromotionState.DeferredPressure or
            TerrainDetailPromotionState.DeferredPromotionBudget or
            TerrainDetailPromotionState.DeferredStartupPriority or
            TerrainDetailPromotionState.DeferredCollisionBootstrap;
    public bool IsDetailPromotionMeshBlocked =>
        DetailPromotionState is TerrainDetailPromotionState.DeferredPendingMesh or
            TerrainDetailPromotionState.Queued or
            TerrainDetailPromotionState.Running;
    public string DetailPromotionStateSummary
    {
        get
        {
            string reason = string.IsNullOrWhiteSpace(DetailPromotionDeferReason)
                ? "-"
                : DetailPromotionDeferReason;
            string nextFrame = DetailPromotionNextEligibleFrame > 0
                ? DetailPromotionNextEligibleFrame.ToString()
                : "-";
            string nextTime = DetailPromotionNextEligibleAtSeconds > double.NegativeInfinity
                ? DetailPromotionNextEligibleAtSeconds.ToString("0.000")
                : "-";
            string reactivation = DetailPromotionReevaluationPending &&
                                  !string.IsNullOrWhiteSpace(DetailPromotionReactivationReason)
                ? DetailPromotionReactivationReason
                : "-";
            return
                $"{DetailPromotionState} reason {reason} next_f {nextFrame} next_t {nextTime} reactivate {reactivation} followup={DetailPromotionFollowupRequested}";
        }
    }
    public TerrainChunkCoverageState CoverageState
    {
        get
        {
            if (ReplacementCoveragePending)
            {
                return TerrainChunkCoverageState.ReplacementPending;
            }

            if (!HasCompletedInitialVisualBuild)
            {
                return TerrainChunkCoverageState.CoarsePending;
            }

            if (!HasSurface)
            {
                return SafeToRelease
                    ? TerrainChunkCoverageState.SafeToRelease
                    : TerrainChunkCoverageState.CoarsePending;
            }

            if (RenderDirty && (HasDetailBrick || DetailRegionCount > 0))
            {
                return TerrainChunkCoverageState.DetailPending;
            }

            if (HasDetailBrick && !RenderDirty)
            {
                return TerrainChunkCoverageState.DetailReady;
            }

            return SafeToRelease
                ? TerrainChunkCoverageState.SafeToRelease
                : TerrainChunkCoverageState.CoarseReady;
        }
    }
    public string CoverageStateSummary =>
        $"{CoverageState} hold={HeldForCoverageSafety} replacement={ReplacementCoveragePending} safe={SafeToRelease} reason=\"{CoverageHoldReason}\"";

    private MeshInstance3D _meshInstance = null!;
    private CollisionShape3D _collision = null!;
    private VoxelChunkData _data = null!;
    private ArrayMesh _mesh = null!;
    private StandardMaterial3D _biomeDebugMaterial = null!;
    private bool _terrainVertexTintEnabled;
    private bool _biomeDebugTintEnabled;
    private bool _biomeDebugTintRequested;
    private TerrainVisualDebugMode _visualDebugMode = TerrainVisualDebugMode.Lit;
    private int _renderRevision;

    public override void _Ready()
    {
        _meshInstance = GetNode<MeshInstance3D>("Mesh");
        _collision = GetNode<CollisionShape3D>("Body/Collision");
        ApplySurfaceMaterialOverride();
    }

    public void Initialize(Vector3I key, TerrainWorldSettings settings)
    {
        ChunkKey = key;
        PointsPerAxis = settings.PointsPerAxis;
        VoxelSize = settings.VoxelSize;
        DetailRegionManager = new TerrainChunkDetailRegionManager(ChunkSize);
        RenderDirtyBoundsTracker = new TerrainChunkDirtyBoundsTracker(ChunkSize, VoxelSize, PointsPerAxis);
        CollisionDirtyBoundsTracker = new TerrainChunkDirtyBoundsTracker(ChunkSize, VoxelSize, PointsPerAxis);

        Vector3 origin = new(
            key.X * settings.ChunkSize,
            settings.BaseY + (key.Y * settings.ChunkSize),
            key.Z * settings.ChunkSize);

        Position = origin;
        UpdateDebugName();
    }

    public void SetBiomeSample(TerrainBiomeSample biomeSample, bool enableDebugTint)
    {
        BiomeSample = biomeSample;
        _biomeDebugTintRequested = enableDebugTint;
        RefreshMaterialFlags();
        ApplySurfaceMaterialOverride();
    }

    public void SetStructureMetadata(TerrainChunkStructureMetadata structureMetadata)
    {
        StructureMetadata = structureMetadata ?? TerrainChunkStructureMetadata.Empty;
    }

    public void SetVisualConfiguration(bool enableVertexTint, TerrainVisualDebugMode visualDebugMode)
    {
        _terrainVertexTintEnabled = enableVertexTint;
        _visualDebugMode = OS.IsDebugBuild()
            ? visualDebugMode
            : TerrainVisualDebugMode.Lit;
        RefreshMaterialFlags();
        ApplySurfaceMaterialOverride();
    }

    public bool RequestDetail(
        Aabb localBounds,
        int detailLevel,
        TerrainDetailRegionSource source,
        string reason,
        float priority = 0.0f,
        bool sticky = false,
        string requestId = "")
    {
        return DetailRegionManager.RequestDetail(localBounds, detailLevel, source, reason, priority, sticky, requestId);
    }

    public bool RemoveDetailRequest(string requestId)
    {
        return DetailRegionManager.RemoveRequest(requestId);
    }

    public int RemoveDetailRequestsBySource(TerrainDetailRegionSource source)
    {
        return DetailRegionManager.RemoveRequestsBySource(source);
    }

    public TerrainDetailRegion[] QueryDetailRegions(Aabb localBounds)
    {
        return DetailRegionManager.QueryIntersecting(localBounds);
    }

    public void ClearDetailRegionDirtyFlags()
    {
        DetailRegionManager.ClearDirtyFlags();
    }

    public void SetData(VoxelChunkData data, TerrainChunkLoadSource source)
    {
        _data = data;
        LoadSource = source;
        PersistenceDirty = false;
        RenderDirty = false;
        CollisionDirty = false;
        CollisionReadyAtSeconds = 0.0;
        HasCompletedInitialVisualBuild = false;
        LastRenderBuildMs = 0.0;
        LastCollisionBuildMs = 0.0;
        LastVisualCommitAtSeconds = double.NegativeInfinity;
        ActivatedAtSeconds = double.NegativeInfinity;
        ActivatedFrame = ulong.MaxValue;
        LastTotalTriangleCount = 0;
        LastDetailTriangleCount = 0;
        LastReplacedCoarseCellCount = 0;
        LastDetailCellCount = 0;
        LastUsedDetailBrick = false;
        LastUsedPersistentDetailEdits = false;
        _renderRevision = 0;
        HeldForCoverageSafety = false;
        ReplacementCoveragePending = false;
        SafeToRelease = false;
        CoverageHoldReason = string.Empty;
        ResetDetailPromotionTracking();
        RenderDirtyBoundsTracker.Clear();
        CollisionDirtyBoundsTracker.Clear();
        _mesh = null;
        if (_meshInstance != null)
        {
            _meshInstance.Mesh = null;
        }

        if (_collision != null)
        {
            _collision.Shape = null;
        }

        SyncEditedDetailRegionRequest();
        UpdateDebugName();
    }

    public void SetCoverageRetention(
        bool heldForCoverageSafety,
        bool replacementCoveragePending,
        bool safeToRelease,
        string holdReason)
    {
        if (HeldForCoverageSafety == heldForCoverageSafety &&
            ReplacementCoveragePending == replacementCoveragePending &&
            SafeToRelease == safeToRelease &&
            string.Equals(CoverageHoldReason, holdReason ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        HeldForCoverageSafety = heldForCoverageSafety;
        ReplacementCoveragePending = replacementCoveragePending;
        SafeToRelease = safeToRelease;
        CoverageHoldReason = holdReason?.Trim() ?? string.Empty;
        UpdateDebugName();
    }

    public void NotifyActivated(ulong frame, double nowSeconds)
    {
        ActivatedFrame = frame;
        ActivatedAtSeconds = nowSeconds;
    }

    public void DeferDetailPromotion(
        TerrainDetailPromotionState state,
        string reason,
        ulong nextEligibleFrame,
        double nextEligibleAtSeconds)
    {
        DetailPromotionState = state;
        DetailPromotionDeferReason = reason?.Trim() ?? string.Empty;
        DetailPromotionNextEligibleFrame = nextEligibleFrame;
        DetailPromotionNextEligibleAtSeconds = nextEligibleAtSeconds;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
        DetailPromotionFollowupRequested = false;
    }

    public void ReactivateDetailPromotion(string reason)
    {
        DetailPromotionState = TerrainDetailPromotionState.Eligible;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = true;
        DetailPromotionReactivationReason = reason?.Trim() ?? string.Empty;
        DetailPromotionFollowupRequested = false;
    }

    public void SetDetailPromotionEligible()
    {
        DetailPromotionState = TerrainDetailPromotionState.Eligible;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
        DetailPromotionFollowupRequested = false;
    }

    public void MarkDetailPromotionQueued()
    {
        DetailPromotionState = TerrainDetailPromotionState.Queued;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
    }

    public void MarkDetailPromotionRunning()
    {
        DetailPromotionState = TerrainDetailPromotionState.Running;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
    }

    public void MarkDetailPromotionApplied()
    {
        DetailPromotionState = TerrainDetailPromotionState.Applied;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
        DetailPromotionFollowupRequested = false;
    }

    public void RequestDetailPromotionFollowup()
    {
        DetailPromotionFollowupRequested = true;
    }

    public bool ConsumeDetailPromotionReevaluationPending(out string reason)
    {
        if (!DetailPromotionReevaluationPending)
        {
            reason = string.Empty;
            return false;
        }

        reason = DetailPromotionReactivationReason;
        DetailPromotionReevaluationPending = false;
        DetailPromotionReactivationReason = string.Empty;
        return true;
    }

    public bool EnsureDetailBrick(
        Aabb localBounds,
        int detailLevel,
        System.Func<Vector3, float> densitySampler,
        System.Func<Vector3, float, VoxelMaterialId> materialResolver,
        bool persistentEdits = false,
        bool preserveExistingCoverage = false,
        int paddingCoarseCells = 1)
    {
        if (_data == null)
        {
            return false;
        }

        int detailScale = GetDetailScaleForLevel(detailLevel);
        bool changed = _data.EnsureDetailBrick(
            localBounds,
            detailScale,
            paddingCoarseCells,
            densitySampler,
            materialResolver,
            persistentEdits,
            preserveExistingCoverage);
        if (changed)
        {
            PersistenceDirty |= persistentEdits;
            if (persistentEdits)
            {
                SyncEditedDetailRegionRequest();
            }
            UpdateDebugName();
        }

        return changed;
    }

    public bool RemoveTransientDetailBrick()
    {
        if (_data == null)
        {
            return false;
        }

        bool removed = _data.RemoveTransientDetailBrick();
        if (removed)
        {
            SyncEditedDetailRegionRequest();
            UpdateDebugName();
        }

        return removed;
    }

    public bool TryGetDetailBrickLocalBounds(out Aabb localBounds)
    {
        if (_data?.DetailBrick == null)
        {
            localBounds = default;
            return false;
        }

        localBounds = _data.DetailBrick.LocalBounds;
        return true;
    }

    public bool TryGetEditedDetailLocalBounds(out Aabb localBounds)
    {
        if (_data?.EditedDetailBrick == null)
        {
            localBounds = default;
            return false;
        }

        localBounds = _data.EditedDetailBrick.LocalBounds;
        return true;
    }

    public void MarkDirty(bool includeCollision, double collisionDelaySeconds)
    {
        MarkDirtyBounds(new Aabb(Vector3.Zero, Vector3.One * ChunkSize), includeCollision, collisionDelaySeconds);
    }

    public void MarkDirtyBounds(Aabb localBounds, bool includeCollision, double collisionDelaySeconds)
    {
        RenderDirty = true;
        RenderDirtyBoundsTracker.Include(localBounds);
        _renderRevision++;
        if (includeCollision)
        {
            MarkCollisionDirtyBounds(localBounds, collisionDelaySeconds);
        }
    }

    public void MarkCollisionDirty(double collisionDelaySeconds)
    {
        MarkCollisionDirtyBounds(new Aabb(Vector3.Zero, Vector3.One * ChunkSize), collisionDelaySeconds);
    }

    public void MarkCollisionDirtyBounds(Aabb localBounds, double collisionDelaySeconds)
    {
        CollisionDirty = true;
        CollisionDirtyBoundsTracker.Include(localBounds);
        CollisionReadyAtSeconds = Time.GetTicksMsec() / 1000.0 + collisionDelaySeconds;
    }

    internal TerrainVisualBuildJob? TryCreateVisualBuildJob(TerrainVisualBuildRequest request)
    {
        if (_data == null || !RenderDirty)
        {
            return null;
        }

        bool includeTransientDetail = request.DetailMode == TerrainMeshDetailMode.IncludeTransientDetail;
        return new TerrainVisualBuildJob(
            this,
            ChunkKey,
            _renderRevision,
            request.Kind,
            request.QueueClass,
            request.PriorityScore,
            request.DetailMode,
            request.Reason,
            RenderDirtyBounds,
            _data.CreateMeshSnapshot(includeTransientDetail));
    }

    public bool TryCommitRenderMesh(VoxelMeshBuildResult meshBuild, int revision)
    {
        if (_data == null || !RenderDirty || revision != _renderRevision)
        {
            return false;
        }

        ulong start = Time.GetTicksUsec();
        ArrayMesh mesh = new();
        if (meshBuild.HasGeometry)
        {
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
        }

        _mesh = mesh;
        _meshInstance.Mesh = _mesh;
        _meshInstance.CastShadow = _mesh.GetSurfaceCount() > 0
            ? GeometryInstance3D.ShadowCastingSetting.On
            : GeometryInstance3D.ShadowCastingSetting.Off;
        LastTotalTriangleCount = meshBuild.TotalTriangleCount;
        LastDetailTriangleCount = meshBuild.DetailTriangleCount;
        LastReplacedCoarseCellCount = meshBuild.ReplacedCoarseCellCount;
        LastDetailCellCount = meshBuild.DetailCellCount;
        LastUsedDetailBrick = meshBuild.UsedDetailBrick;
        LastUsedPersistentDetailEdits = meshBuild.UsedPersistentDetailEdits;

        if (_mesh.GetSurfaceCount() > 0)
        {
            ApplySurfaceMaterialOverride();
        }

        LastRenderBuildMs = (Time.GetTicksUsec() - start) / 1000.0;
        LastVisualCommitAtSeconds = Time.GetTicksMsec() / 1000.0;
        HasCompletedInitialVisualBuild = true;
        RenderDirty = false;
        RenderDirtyBoundsTracker.Clear();
        ClearDetailRegionDirtyFlags();
        UpdateDebugName();
        return true;
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
        CollisionDirtyBoundsTracker.Clear();
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

    public bool TryGetLocalBoundsForWorldBounds(Aabb worldBounds, out Aabb localBounds)
    {
        Aabb chunkBounds = new(Position, Vector3.One * ChunkSize);
        if (!TryComputeIntersection(chunkBounds, worldBounds, out Aabb worldIntersection))
        {
            localBounds = default;
            return false;
        }

        localBounds = new Aabb(worldIntersection.Position - Position, worldIntersection.Size);
        return true;
    }

    public bool TryGetLocalBoundsForSphere(Vector3 worldCenter, float radius, out Aabb localBounds)
    {
        Vector3 extents = Vector3.One * radius;
        return TryGetLocalBoundsForWorldBounds(new Aabb(worldCenter - extents, extents * 2.0f), out localBounds);
    }

    public VoxelEditStats ApplySphereBrush(
        VoxelSphereEdit edit,
        System.Func<Vector3, float, VoxelMaterialId> materialResolver)
    {
        if (_data == null)
        {
            return VoxelEditStats.None;
        }

        VoxelEditStats editStats = VoxelTerrainEditing.ApplySphere(_data, edit, materialResolver);
        if (_data.HasDetailBrick)
        {
            VoxelEditStats detailStats = VoxelTerrainEditing.ApplySphere(_data.DetailBrick.Data, edit, materialResolver);
            editStats = CombineEditStats(editStats, detailStats);
        }

        if (editStats.Modified)
        {
            PersistenceDirty = true;
        }
        return editStats;
    }

    public VoxelEditStats ApplySlashBrush(
        VoxelSlashEdit edit,
        System.Func<Vector3, float, VoxelMaterialId> materialResolver)
    {
        if (_data == null)
        {
            return VoxelEditStats.None;
        }

        VoxelEditStats editStats = VoxelTerrainEditing.ApplySlash(_data, edit, materialResolver);
        if (_data.HasDetailBrick)
        {
            VoxelEditStats detailStats = VoxelTerrainEditing.ApplySlash(_data.DetailBrick.Data, edit, materialResolver);
            editStats = CombineEditStats(editStats, detailStats);
        }
        if (editStats.Modified)
        {
            PersistenceDirty = true;
        }

        return editStats;
    }

    public void MarkPersisted()
    {
        PersistenceDirty = false;
    }

    private void SyncEditedDetailRegionRequest()
    {
        RemoveDetailRequest(EditedDetailRegionRequestId);
        if (_data == null)
        {
            return;
        }

        if (!HasEditedDetailBrick)
        {
            _data.RemovePersistedDetailRegion(EditedDetailRegionRequestId);
            return;
        }

        if (TryGetEditedDetailLocalBounds(out Aabb localBounds))
        {
            _data.UpsertPersistedDetailRegion(BuildEditedPersistedDetailRegion(localBounds));
        }

        foreach (TerrainPersistedDetailRegionData persistedRegion in _data.PersistedDetailRegions)
        {
            RequestDetail(
                persistedRegion.LocalBounds,
                persistedRegion.RequestedDetailLevel,
                persistedRegion.Source,
                persistedRegion.Reason,
                persistedRegion.Priority,
                persistedRegion.Sticky,
                persistedRegion.Id);
        }
    }

    private void UpdateDebugName()
    {
        string suffix = HasEditedDetailBrick
            ? "_edit_hi"
            : (HasDetailBrick ? "_detail_hi" : string.Empty);
        string coverageSuffix = CoverageState switch
        {
            TerrainChunkCoverageState.ReplacementPending => "_hold",
            TerrainChunkCoverageState.SafeToRelease => "_release",
            _ => string.Empty
        };
        Name = $"Chunk_{ChunkKey.X}_{ChunkKey.Y}_{ChunkKey.Z}{suffix}{coverageSuffix}";
    }

    private void ResetDetailPromotionTracking()
    {
        DetailPromotionState = TerrainDetailPromotionState.Eligible;
        DetailPromotionDeferReason = string.Empty;
        DetailPromotionNextEligibleFrame = 0;
        DetailPromotionNextEligibleAtSeconds = double.NegativeInfinity;
        DetailPromotionReevaluationPending = false;
        DetailPromotionFollowupRequested = false;
        DetailPromotionReactivationReason = string.Empty;
    }

    private static int GetDetailScaleForLevel(int detailLevel)
    {
        return detailLevel >= 2 ? 3 : 2;
    }

    private static TerrainPersistedDetailRegionData BuildEditedPersistedDetailRegion(Aabb localBounds)
    {
        return new TerrainPersistedDetailRegionData(
            EditedDetailRegionRequestId,
            localBounds,
            2,
            TerrainDetailRegionSource.Edit,
            EditedDetailRegionReason,
            priority: 100.0f,
            sticky: true);
    }

    private void ApplySurfaceMaterialOverride()
    {
        if (_meshInstance == null || _mesh == null || _mesh.GetSurfaceCount() <= 0)
        {
            return;
        }

        StandardMaterial3D baseMaterial = ResolveBaseSurfaceMaterial();
        if (!_biomeDebugTintEnabled || _visualDebugMode != TerrainVisualDebugMode.Lit)
        {
            _meshInstance.SetSurfaceOverrideMaterial(0, baseMaterial);
            return;
        }

        _biomeDebugMaterial ??= (StandardMaterial3D)baseMaterial.Duplicate();
        _biomeDebugMaterial.VertexColorUseAsAlbedo = baseMaterial.VertexColorUseAsAlbedo;
        _biomeDebugMaterial.Roughness = baseMaterial.Roughness;
        _biomeDebugMaterial.Metallic = baseMaterial.Metallic;
        _biomeDebugMaterial.ShadingMode = baseMaterial.ShadingMode;
        _biomeDebugMaterial.AlbedoColor = baseMaterial.AlbedoColor.Lerp(BiomeSample.DebugColor, 0.2f);
        _meshInstance.SetSurfaceOverrideMaterial(0, _biomeDebugMaterial);
    }

    private StandardMaterial3D ResolveBaseSurfaceMaterial()
    {
        if (_visualDebugMode.UsesDiagnosticVertexColors())
        {
            return SharedTerrainDebugVertexColorMaterial;
        }

        return _terrainVertexTintEnabled
            ? SharedTerrainTintMaterial
            : SharedTerrainMaterial;
    }

    private void RefreshMaterialFlags()
    {
        _biomeDebugTintEnabled =
            _biomeDebugTintRequested &&
            OS.IsDebugBuild() &&
            _visualDebugMode == TerrainVisualDebugMode.Lit;
    }

    private static VoxelEditStats CombineEditStats(VoxelEditStats a, VoxelEditStats b)
    {
        return new VoxelEditStats(
            a.Modified || b.Modified,
            a.DensitySamplesEdited + b.DensitySamplesEdited,
            a.MaterialSamplesTouched + b.MaterialSamplesTouched);
    }

    private static bool TryComputeIntersection(Aabb a, Aabb b, out Aabb intersection)
    {
        Vector3 aEnd = a.Position + a.Size;
        Vector3 bEnd = b.Position + b.Size;
        Vector3 min = new(
            Mathf.Max(a.Position.X, b.Position.X),
            Mathf.Max(a.Position.Y, b.Position.Y),
            Mathf.Max(a.Position.Z, b.Position.Z));
        Vector3 max = new(
            Mathf.Min(aEnd.X, bEnd.X),
            Mathf.Min(aEnd.Y, bEnd.Y),
            Mathf.Min(aEnd.Z, bEnd.Z));
        Vector3 size = max - min;
        if (size.X <= 0.001f || size.Y <= 0.001f || size.Z <= 0.001f)
        {
            intersection = default;
            return false;
        }

        intersection = new Aabb(min, size);
        return true;
    }
}
