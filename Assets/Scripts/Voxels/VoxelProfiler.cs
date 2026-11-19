using Unity.Profiling;

namespace Voxels
{
    /// <summary>
    /// Маркеры Profiler. Ищи их в Unity Profiler в категории "Voxels".
    /// Ничего не меняют в поведении — только измеряют.
    /// </summary>
    public static class VoxelProfiler
    {
        public static readonly ProfilerMarker Column = new ProfilerMarker("Voxels.CreateColumn");
        public static readonly ProfilerMarker ColumnFill = new ProfilerMarker("Voxels.ColumnFill");
        public static readonly ProfilerMarker Heights = new ProfilerMarker("Voxels.Heights");
        public static readonly ProfilerMarker Density = new ProfilerMarker("Voxels.Density");
        public static readonly ProfilerMarker Caves = new ProfilerMarker("Voxels.Caves");
        public static readonly ProfilerMarker Structures = new ProfilerMarker("Voxels.Structures");
        public static readonly ProfilerMarker Mesh = new ProfilerMarker("Voxels.Mesh");
        public static readonly ProfilerMarker MeshFaces = new ProfilerMarker("Voxels.MeshFaces");
        public static readonly ProfilerMarker MeshGreedy = new ProfilerMarker("Voxels.MeshGreedy");
        public static readonly ProfilerMarker MeshAo = new ProfilerMarker("Voxels.MeshAo");
        public static readonly ProfilerMarker MeshUpload = new ProfilerMarker("Voxels.MeshUpload");
        public static readonly ProfilerMarker Collider = new ProfilerMarker("Voxels.Collider");
        public static readonly ProfilerMarker SetBlock = new ProfilerMarker("Voxels.SetBlock");
        public static readonly ProfilerMarker Streaming = new ProfilerMarker("Voxels.Streaming");

        /// <summary>Каждый кадр — проверка, что маркеры вообще видны в Profiler.</summary>
        public static readonly ProfilerMarker Frame = new ProfilerMarker("Voxels.Frame");
    }
}
