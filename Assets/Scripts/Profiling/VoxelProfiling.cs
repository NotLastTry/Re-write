using Unity.Profiling;

namespace Voxels
{
    /// <summary>
    /// Именованные маркеры Unity Profiler для этапов генерации мира.
    /// Видны в модуле CPU Profiler и доступны через ProfilerRecorder,
    /// поэтому по ним же снимаются метрики в автономном замере.
    /// </summary>
    public static class VoxelProfiling
    {
        /// <summary>Вся генерация одной колонки (4 чанка по высоте).</summary>
        public static readonly ProfilerMarker Column = new ProfilerMarker("Voxels.Column");

        /// <summary>Расчёт высот и биомов (Burst-джобы).</summary>
        public static readonly ProfilerMarker Terrain = new ProfilerMarker("Voxels.Terrain");

        /// <summary>Заполнение вокселей по высотам.</summary>
        public static readonly ProfilerMarker Fill = new ProfilerMarker("Voxels.Fill");

        /// <summary>Вырезание пещер.</summary>
        public static readonly ProfilerMarker Caves = new ProfilerMarker("Voxels.Caves");

        /// <summary>Расстановка деревьев, камней и кактусов.</summary>
        public static readonly ProfilerMarker Structures = new ProfilerMarker("Voxels.Structures");

        /// <summary>Построение меша чанка (грани + AO + submesh'и).</summary>
        public static readonly ProfilerMarker ChunkMesh = new ProfilerMarker("Voxels.ChunkMesh");

        /// <summary>Пересборка мешей соседних колонок после появления новой.</summary>
        public static readonly ProfilerMarker NeighbourRemesh = new ProfilerMarker("Voxels.NeighbourRemesh");

        /// <summary>Запекание MeshCollider чанка.</summary>
        public static readonly ProfilerMarker ColliderBake = new ProfilerMarker("Voxels.ColliderBake");
    }
}
