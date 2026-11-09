using System.Collections.Generic;
using UnityEngine;

namespace Voxels
{
    public sealed class World
    {
        readonly Dictionary<ChunkId, Chunk> _chunks = new Dictionary<ChunkId, Chunk>();

        public World(TerrainGenerator generator)
        {
            Generator = generator ?? new TerrainGenerator();

            // Пещеры должны зависеть от сида мира, иначе при смене сида
            // рельеф новый, а рисунок пещер остаётся прежним.
            Caves.Seed = unchecked((uint)Generator.Seed);
            Density.Configure(Generator.GetParams());
        }

        public TerrainGenerator Generator { get; }
        public CaveGenerator Caves { get; } = new CaveGenerator();
        public DensityGenerator Density { get; } = new DensityGenerator();

        /// <summary>Режим 3D-плотности вместо карты высот.</summary>
        public bool UseDensity;
        public IReadOnlyDictionary<ChunkId, Chunk> Chunks => _chunks;
        public int ChunkCount => _chunks.Count;

        public int ColumnCount
        {
            get
            {
                int count = 0;
                foreach (ChunkId id in _chunks.Keys)
                    if (id.Y == 0)
                        count++;
                return count;
            }
        }

        public bool HasColumn(int chunkX, int chunkZ) => _chunks.ContainsKey(new ChunkId(chunkX, 0, chunkZ));

        /// <summary>Создаёт колонку: 4 чанка по высоте из одного прохода по высотам.</summary>
        public void CreateColumn(int chunkX, int chunkZ, Transform parent)
        {
            if (HasColumn(chunkX, chunkZ))
                return;

            var heights = new short[VoxelConstants.ChunkSize * VoxelConstants.ChunkSize];
            var biomes = new byte[VoxelConstants.ChunkSize * VoxelConstants.ChunkSize];

            var column = new Chunk[VoxelConstants.ChunksInHeight];
            var voxels = new ushort[VoxelConstants.ChunksInHeight][];

            for (int chunkY = 0; chunkY < VoxelConstants.ChunksInHeight; chunkY++)
            {
                var id = new ChunkId(chunkX, chunkY, chunkZ);

                var gameObject = new GameObject("Chunk");
                gameObject.transform.SetParent(parent, false);

                var chunk = gameObject.AddComponent<Chunk>();
                chunk.Initialize(id);
                chunk.HasBlocks = false;

                voxels[chunkY] = chunk.Voxels;
                _chunks[id] = chunk;
                column[chunkY] = chunk;
            }

            if (UseDensity)
            {
                Density.FillColumn(chunkX, chunkZ, voxels, heights, biomes);
            }
            else
            {
                Generator.FillColumnHeights(chunkX, chunkZ, heights, biomes);

                for (int chunkY = 0; chunkY < column.Length; chunkY++)
                {
                    var id = new ChunkId(chunkX, chunkY, chunkZ);
                    column[chunkY].HasBlocks =
                        Generator.FillChunkFromHeights(id, heights, biomes, voxels[chunkY]);
                }
            }

            // Вырезаем пещеры во всех четырёх чанках колонки сразу.
            Caves.Carve(chunkX, chunkZ, heights, voxels);

            for (int chunkY = 0; chunkY < column.Length; chunkY++)
            {
                Chunk chunk = column[chunkY];
                chunk.HasBlocks = HasAnySolid(chunk.Voxels);

                if (chunk.HasBlocks)
                    chunk.RebuildMesh(this);
            }

            // Соседние колонки могли быть отмешированы раньше, когда этой ещё не было:
            // на стыке они считали грани по генератору, без пещер.
            RebuildNeighbourColumns(chunkX, chunkZ);
        }

        void RebuildNeighbourColumns(int chunkX, int chunkZ)
        {
            RebuildColumn(chunkX - 1, chunkZ);
            RebuildColumn(chunkX + 1, chunkZ);
            RebuildColumn(chunkX, chunkZ - 1);
            RebuildColumn(chunkX, chunkZ + 1);
        }

        void RebuildColumn(int chunkX, int chunkZ)
        {
            for (int chunkY = 0; chunkY < VoxelConstants.ChunksInHeight; chunkY++)
            {
                var id = new ChunkId(chunkX, chunkY, chunkZ);
                if (_chunks.TryGetValue(id, out Chunk chunk) && chunk.HasBlocks)
                    chunk.RebuildMesh(this);
            }
        }

        static bool HasAnySolid(ushort[] voxels)
        {
            for (int i = 0; i < voxels.Length; i++)
            {
                if (voxels[i] != BlockType.Air)
                    return true;
            }

            return false;
        }


        /// <summary>Выгружает колонку: уничтожает 4 чанка и чистит кэш высот.</summary>
        public void RemoveColumn(int chunkX, int chunkZ, bool rebuildNeighbours = true)
        {
            for (int chunkY = 0; chunkY < VoxelConstants.ChunksInHeight; chunkY++)
            {
                var id = new ChunkId(chunkX, chunkY, chunkZ);
                if (!_chunks.TryGetValue(id, out Chunk chunk))
                    continue;

                _chunks.Remove(id);

                if (chunk != null)
                    Object.Destroy(chunk.gameObject);
            }

            Generator.ForgetColumn(chunkX, chunkZ);
            Density.ForgetColumn(chunkX, chunkZ);

            // У соседей могли быть нарисованы грани в сторону выгруженной колонки.
            if (rebuildNeighbours)
                RebuildNeighbourColumns(chunkX, chunkZ);
        }

        /// <summary>
        /// Блок в мировой точке. Для незагруженного чанка считает генератором —
        /// грани на стыках остаются корректными.
        /// </summary>
        public ushort GetBlock(int x, int y, int z)
        {
            if (y < 0)
                return BlockType.Stone;
            if (y >= VoxelConstants.WorldHeight)
                return BlockType.Air;

            var id = ChunkId.FromWorldPos(x, y, z);
            if (_chunks.TryGetValue(id, out Chunk chunk))
            {
                return chunk[
                    x & VoxelConstants.ChunkMask,
                    y & VoxelConstants.ChunkMask,
                    z & VoxelConstants.ChunkMask];
            }

            return UseDensity ? Density.GetBlock(x, y, z) : Generator.GetBlock(x, y, z);
        }

        /// <summary>Высота поверхности для спавна и отладки.</summary>
        public int GetSurfaceHeight(int x, int z)
            => UseDensity ? Density.GetSurfaceHeight(x, z) : Generator.GetSurfaceHeight(x, z);

        public void SetBlock(int x, int y, int z, ushort block)
        {
            if (y < 0 || y >= VoxelConstants.WorldHeight)
                return;

            var id = ChunkId.FromWorldPos(x, y, z);
            if (!_chunks.TryGetValue(id, out Chunk chunk))
                return;

            int localX = x & VoxelConstants.ChunkMask;
            int localY = y & VoxelConstants.ChunkMask;
            int localZ = z & VoxelConstants.ChunkMask;

            chunk[localX, localY, localZ] = block;

            if (block != BlockType.Air)
                chunk.HasBlocks = true;

            chunk.RebuildMesh(this);

            if (localX == 0) RebuildNeighbour(id, -1, 0, 0);
            if (localX == VoxelConstants.ChunkMask) RebuildNeighbour(id, 1, 0, 0);
            if (localY == 0) RebuildNeighbour(id, 0, -1, 0);
            if (localY == VoxelConstants.ChunkMask) RebuildNeighbour(id, 0, 1, 0);
            if (localZ == 0) RebuildNeighbour(id, 0, 0, -1);
            if (localZ == VoxelConstants.ChunkMask) RebuildNeighbour(id, 0, 0, 1);
        }

        void RebuildNeighbour(ChunkId id, int dx, int dy, int dz)
        {
            var neighbourId = new ChunkId(id.X + dx, id.Y + dy, id.Z + dz);
            if (_chunks.TryGetValue(neighbourId, out Chunk neighbour))
                neighbour.RebuildMesh(this);
        }
    }
}