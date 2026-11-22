using System.Collections.Generic;
using UnityEngine;

namespace Voxels
{
    public sealed class World
    {
        readonly Dictionary<ChunkId, Chunk> _chunks = new Dictionary<ChunkId, Chunk>();

        /// <summary>Выгруженные чанки не уничтожаем, а держим в пуле: меши, воксели и списки переиспользуются.</summary>
        readonly Stack<Chunk> _chunkPool = new Stack<Chunk>(32);

        const int ColumnCells = VoxelConstants.ChunkSize * VoxelConstants.ChunkSize;

        readonly short[] _heightsScratch = new short[ColumnCells];
        readonly byte[] _biomesScratch = new byte[ColumnCells];
        readonly Chunk[] _columnScratch = new Chunk[VoxelConstants.ChunksInHeight];
        readonly ushort[][] _voxelsScratch = new ushort[VoxelConstants.ChunksInHeight][];

        public World(TerrainGenerator generator)
        {
            Generator = generator ?? new TerrainGenerator();

            // Пещеры должны зависеть от сида мира, иначе при смене сида
            // рельеф новый, а рисунок пещер остаётся прежним.
            Caves.Seed = unchecked((uint)Generator.Seed);
            Density.Configure(Generator.GetParams(), Generator.Seed);
            Structures.Seed = Generator.Seed;
            Structures.Definitions = Resources.LoadAll<StructureDefinition>("Structures");
        }

        public TerrainGenerator Generator { get; }
        public CaveGenerator Caves { get; } = new CaveGenerator();
        public DensityGenerator Density { get; } = new DensityGenerator();
        public StructureGenerator Structures { get; } = new StructureGenerator();

        /// <summary>Режим 3D-плотности вместо карты высот.</summary>
        public bool UseDensity;
        public IReadOnlyDictionary<ChunkId, Chunk> Chunks => _chunks;
        public int ChunkCount => _chunks.Count;
        public int PooledChunkCount => _chunkPool.Count;

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

            using (VoxelProfiling.Column.Auto())
            {
                CreateColumnInternal(chunkX, chunkZ, parent);
            }
        }

        void CreateColumnInternal(int chunkX, int chunkZ, Transform parent)
        {
            short[] heights = _heightsScratch;
            byte[] biomes = _biomesScratch;
            Chunk[] column = _columnScratch;
            ushort[][] voxels = _voxelsScratch;

            for (int chunkY = 0; chunkY < VoxelConstants.ChunksInHeight; chunkY++)
            {
                var id = new ChunkId(chunkX, chunkY, chunkZ);

                Chunk chunk = RentChunk(parent);
                chunk.Initialize(id);
                chunk.HasBlocks = false;

                voxels[chunkY] = chunk.Voxels;
                _chunks[id] = chunk;
                column[chunkY] = chunk;
            }

            if (UseDensity)
            {
                using (VoxelProfiling.Terrain.Auto())
                {
                    Density.FillColumn(chunkX, chunkZ, voxels, heights, biomes);
                }
            }
            else
            {
                using (VoxelProfiling.Terrain.Auto())
                {
                    Generator.FillColumnHeights(chunkX, chunkZ, heights, biomes);
                }

                using (VoxelProfiling.Fill.Auto())
                {
                    for (int chunkY = 0; chunkY < column.Length; chunkY++)
                    {
                        var id = new ChunkId(chunkX, chunkY, chunkZ);
                        column[chunkY].HasBlocks =
                            Generator.FillChunkFromHeights(id, heights, biomes, voxels[chunkY]);
                    }
                }
            }

            // Вырезаем пещеры во всех четырёх чанках колонки сразу.
            using (VoxelProfiling.Caves.Auto())
            {
                Caves.Carve(chunkX, chunkZ, heights, voxels);
            }

            // Деревья и камни ставим после пещер, чтобы они не висели в воздухе.
            using (VoxelProfiling.Structures.Auto())
            {
                Structures.Place(this, chunkX, chunkZ, voxels);
            }

            for (int chunkY = 0; chunkY < column.Length; chunkY++)
            {
                Chunk chunk = column[chunkY];
                chunk.HasBlocks = HasAnySolid(chunk.Voxels);

                if (chunk.HasBlocks)
                    chunk.RebuildMesh(this);
            }

            // Соседние колонки могли быть отмешированы раньше, когда этой ещё не было:
            // на стыке они считали грани по генератору, без пещер.
            using (VoxelProfiling.NeighbourRemesh.Auto())
            {
                RebuildNeighbourColumns(chunkX, chunkZ);
            }
        }

        Chunk RentChunk(Transform parent)
        {
            if (_chunkPool.Count > 0)
            {
                Chunk pooled = _chunkPool.Pop();
                pooled.transform.SetParent(parent, false);
                pooled.gameObject.SetActive(true);
                return pooled;
            }

            var gameObject = new GameObject("Chunk");
            gameObject.transform.SetParent(parent, false);
            return gameObject.AddComponent<Chunk>();
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
                {
                    // Коллайдер соседа не перезапекаем: на стыке меняются единичные грани,
                    // а кукинг PhysX-меша стоит заметно дороже.
                    chunk.RebuildMesh(this, false);
                }
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


        /// <summary>Выгружает колонку: чанки уходят в пул, кэш высот чистится.</summary>
        public void RemoveColumn(int chunkX, int chunkZ, bool rebuildNeighbours = true)
        {
            for (int chunkY = 0; chunkY < VoxelConstants.ChunksInHeight; chunkY++)
            {
                var id = new ChunkId(chunkX, chunkY, chunkZ);
                if (!_chunks.TryGetValue(id, out Chunk chunk))
                    continue;

                _chunks.Remove(id);

                if (chunk != null)
                {
                    chunk.PrepareForPool();
                    _chunkPool.Push(chunk);
                }
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

        public byte GetBiome(int x, int z)
            => UseDensity ? Density.GetBiome(x, z) : Generator.GetBiome(x, z);

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

        /// <summary>Уничтожает все чанки, включая пул. Нужно при смене мира/сида.</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<ChunkId, Chunk> pair in _chunks)
            {
                if (pair.Value != null)
                    Object.Destroy(pair.Value.gameObject);
            }

            _chunks.Clear();

            while (_chunkPool.Count > 0)
            {
                Chunk pooled = _chunkPool.Pop();
                if (pooled != null)
                    Object.Destroy(pooled.gameObject);
            }

            Generator.ClearHeightCache();
            Density.ClearCaches();
        }
    }
}
