using Godot;
using TowerOfBaby.Terrain.Voxel;

namespace TowerOfBaby.Terrain;

internal sealed class TerrainComputeMeshBackend : ITerrainMeshBackend
{
    public string BackendName => "compute_stub";

    public static bool CanUseCurrentRenderer()
    {
        string renderingMethod = ProjectSettings.GetSetting("rendering/renderer/rendering_method", "forward_plus").AsString();
        return renderingMethod is "forward_plus" or "mobile";
    }

    public VoxelMeshBuildResult BuildMesh(VoxelChunkData data, VoxelMeshBuildOptions options)
    {
        // Future hook only. RenderingDevice compute meshing requires the Forward+ or Mobile
        // renderers and is not available under Compatibility, so the async CPU backend remains
        // the default production path until a full compute implementation is ready.
        throw new System.NotSupportedException("Experimental compute terrain meshing is not implemented yet.");
    }
}
