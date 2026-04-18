using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

public enum TerrainBlockState
{
    Requested = 0,
    FieldReady = 1,
    MeshReady = 2,
    Visible = 3,
    Releasable = 4
}

public sealed class TerrainBlockData
{
    public TerrainBlockData(TerrainBlockId id, TerrainRenderer renderer, long instanceVersion)
    {
        Id = id;
        Renderer = renderer;
        InstanceVersion = instanceVersion;
    }

    public TerrainBlockId Id { get; }
    public TerrainRenderer Renderer { get; }
    public long InstanceVersion { get; }
    public TerrainBlockState State { get; set; } = TerrainBlockState.Requested;
    public bool Desired { get; set; } = true;
    public int TriangleCount { get; private set; }
    public VoxelChunkData Field { get; private set; }
    public VoxelChunkData PersistableField { get; private set; }
    public VoxelMeshBuildResult Mesh { get; private set; } = VoxelMeshBuildResult.Empty;
    public TerrainSeamBuildResult SeamBuild { get; private set; } = TerrainSeamBuildResult.None;
    public double ReleaseEligibleAtSeconds { get; private set; }
    public int FieldBuildRevision { get; private set; }
    public bool FieldBuildRunning { get; private set; }
    public int MeshBuildRevision { get; private set; }
    public bool MeshBuildRunning { get; private set; }
    public bool CollisionPending { get; private set; }
    public bool DisplayedRefreshDirty { get; private set; }
    public int DisplayedRefreshRevision { get; private set; }
    public long DisplayedRefreshOperationSequence { get; private set; }
    public long PendingCollisionRefreshOperationSequence { get; private set; }
    public bool HasDisplayedRefreshFieldReady => DisplayedRefreshDirty && Field != null && !HasDisplayedRefreshMeshReady;
    public bool HasDisplayedRefreshMeshReady { get; private set; }

    public int BeginFieldBuild()
    {
        FieldBuildRunning = true;
        return ++FieldBuildRevision;
    }

    public bool MatchesFieldBuild(long instanceVersion, int revision)
    {
        return InstanceVersion == instanceVersion && FieldBuildRevision == revision;
    }

    public void SetField(VoxelChunkData field)
    {
        FieldBuildRunning = false;
        Field = field;
        PersistableField = field;
        HasDisplayedRefreshMeshReady = false;
        State = TerrainBlockState.FieldReady;
    }

    public void ClearFieldBuildRunning(int revision)
    {
        if (FieldBuildRevision == revision)
        {
            FieldBuildRunning = false;
        }
    }

    public int BeginMeshBuild()
    {
        MeshBuildRunning = true;
        return ++MeshBuildRevision;
    }

    public bool MatchesMeshBuild(long instanceVersion, int revision)
    {
        return InstanceVersion == instanceVersion && MeshBuildRevision == revision;
    }

    public void SetMesh(VoxelMeshBuildResult mesh)
    {
        MeshBuildRunning = false;
        Mesh = mesh;
        TriangleCount = mesh.TotalTriangleCount;
        HasDisplayedRefreshMeshReady = false;
        State = TerrainBlockState.MeshReady;
    }

    public void RefreshDisplayedContent(VoxelMeshBuildResult mesh, bool collisionPending)
    {
        ClearTransientBuildArtifacts();
        FieldBuildRunning = false;
        MeshBuildRunning = false;
        TriangleCount = mesh.TotalTriangleCount;
        CollisionPending = collisionPending;
        DisplayedRefreshDirty = false;
        DisplayedRefreshOperationSequence = 0;
        PendingCollisionRefreshOperationSequence = 0;
    }

    public void MarkDisplayedRefreshDirty(long operationSequence)
    {
        DisplayedRefreshDirty = true;
        DisplayedRefreshRevision++;
        DisplayedRefreshOperationSequence = operationSequence;
        PendingCollisionRefreshOperationSequence = 0;
        CollisionPending = false;
        ClearTransientBuildArtifacts();
    }

    public void SetDisplayedRefreshField(VoxelChunkData field)
    {
        FieldBuildRunning = false;
        Field = field;
        PersistableField = field;
        Mesh = VoxelMeshBuildResult.Empty;
        HasDisplayedRefreshMeshReady = false;
        SeamBuild = TerrainSeamBuildResult.None;
    }

    public void SetDisplayedRefreshMesh(VoxelMeshBuildResult mesh)
    {
        MeshBuildRunning = false;
        Field = null;
        Mesh = mesh;
        HasDisplayedRefreshMeshReady = true;
        SeamBuild = TerrainSeamBuildResult.None;
    }

    public void SetPendingCollisionRefreshOperation(long operationSequence)
    {
        PendingCollisionRefreshOperationSequence = operationSequence;
    }

    public long ConsumePendingCollisionRefreshOperation()
    {
        long operationSequence = PendingCollisionRefreshOperationSequence;
        PendingCollisionRefreshOperationSequence = 0;
        return operationSequence;
    }

    public void SetSeamBuild(TerrainSeamBuildResult seamBuild)
    {
        SeamBuild = seamBuild;
    }

    public void ClearMeshBuildRunning(int revision)
    {
        if (MeshBuildRevision == revision)
        {
            MeshBuildRunning = false;
        }
    }

    public void MarkVisible(bool collisionPending = false)
    {
        ClearTransientBuildArtifacts();
        Desired = true;
        ReleaseEligibleAtSeconds = 0.0;
        CollisionPending = collisionPending;
        DisplayedRefreshDirty = false;
        DisplayedRefreshOperationSequence = 0;
        PendingCollisionRefreshOperationSequence = 0;
        State = TerrainBlockState.Visible;
    }

    public void RestoreVisibility()
    {
        Desired = true;
        ReleaseEligibleAtSeconds = 0.0;
        CollisionPending = false;
        PendingCollisionRefreshOperationSequence = 0;
        State = TerrainBlockState.Visible;
    }

    public void MarkReleasable(double releaseEligibleAtSeconds)
    {
        Desired = false;
        ReleaseEligibleAtSeconds = releaseEligibleAtSeconds;
        CollisionPending = false;
        PendingCollisionRefreshOperationSequence = 0;
        State = TerrainBlockState.Releasable;
    }

    public void MarkCollisionPending()
    {
        CollisionPending = true;
    }

    public void MarkCollisionReady()
    {
        CollisionPending = false;
    }

    public bool IsHeldForRelease(double nowSeconds)
    {
        return State == TerrainBlockState.Releasable && nowSeconds < ReleaseEligibleAtSeconds;
    }

    public void InvalidatePendingBuildData()
    {
        ClearTransientBuildArtifacts();
        FieldBuildRunning = false;
        MeshBuildRunning = false;
        CollisionPending = false;
        TriangleCount = 0;
        DisplayedRefreshDirty = false;
        DisplayedRefreshOperationSequence = 0;
        PendingCollisionRefreshOperationSequence = 0;
        FieldBuildRevision++;
        MeshBuildRevision++;
        State = TerrainBlockState.Requested;
    }

    public void CancelPendingData()
    {
        ClearTransientBuildArtifacts();
        FieldBuildRunning = false;
        MeshBuildRunning = false;
        CollisionPending = false;
        DisplayedRefreshDirty = false;
        DisplayedRefreshOperationSequence = 0;
        PendingCollisionRefreshOperationSequence = 0;
    }

    public bool TryGetPersistableField(out VoxelChunkData field)
    {
        field = PersistableField;
        return field != null;
    }

    private void ClearTransientBuildArtifacts()
    {
        Field = null;
        Mesh = VoxelMeshBuildResult.Empty;
        SeamBuild = TerrainSeamBuildResult.None;
        HasDisplayedRefreshMeshReady = false;
    }
}
