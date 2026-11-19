using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Voxels
{
    public sealed class World
    {
        readonly Dictionary<ChunkId, Chunk> _chunks = new Dictionary<ChunkId, Chunk>();

        // Изменения игрока: колонка -> (локальный индекс блока -> блок).
        readonly Dictionary<long, Dictionary<int, ushort>> _columnEdits =
            new Dictionary<long, Dictionary<int, ushort>>();

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
            using var columnMarker = VoxelProfiler.Column.Auto();

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

            // Деревья и камни ставим после пещер, чтобы они не висели в воздухе.
            Structures.Place(this, chunkX, chunkZ, voxels);

            // Правки игрока накладываются поверх сгенерированного мира.
            ApplyEdits(chunkX, chunkZ, voxels);

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

        public byte GetBiome(int x, int z)
            => UseDensity ? Density.GetBiome(x, z) : Generator.GetBiome(x, z);

        /// <summary>
        /// Ставит блок в любой загруженный чанк, только если там воздух.
        /// Нужен структурам, которые пересекают границы чанков; меш перестраивает вызывающий.
        /// </summary>
        public bool PlaceRaw(int x, int y, int z, ushort block)
        {
            if (y < 0 || y >= VoxelConstants.WorldHeight)
                return false;

            var id = ChunkId.FromWorldPos(x, y, z);
            if (!_chunks.TryGetValue(id, out Chunk chunk))
                return false;

            int localX = x & VoxelConstants.ChunkMask;
            int localY = y & VoxelConstants.ChunkMask;
            int localZ = z & VoxelConstants.ChunkMask;

            if (chunk[localX, localY, localZ] != BlockType.Air)
                return false;

            chunk[localX, localY, localZ] = block;
            chunk.HasBlocks = true;
            return true;
        }

        public int EditCount
        {
            get
            {
                int count = 0;
                foreach (Dictionary<int, ushort> column in _columnEdits.Values)
                    count += column.Count;
                return count;
            }
        }

        static long ColumnKey(int chunkX, int chunkZ)
            => ((long)chunkX << 32) ^ (uint)chunkZ;

        static int LocalKey(int x, int y, int z)
            => ((x & VoxelConstants.ChunkMask) << 12)
             | (y << 5)
             | (z & VoxelConstants.ChunkMask);

        void StoreEdit(int x, int y, int z, ushort block)
        {
            int chunkX = x >> VoxelConstants.ChunkShift;
            int chunkZ = z >> VoxelConstants.ChunkShift;
            long key = ColumnKey(chunkX, chunkZ);

            if (!_columnEdits.TryGetValue(key, out Dictionary<int, ushort> column))
            {
                column = new Dictionary<int, ushort>();
                _columnEdits[key] = column;
            }

            column[LocalKey(x, y, z)] = block;
        }

        void ApplyEdits(int chunkX, int chunkZ, ushort[][] voxels)
        {
            if (!_columnEdits.TryGetValue(ColumnKey(chunkX, chunkZ), out Dictionary<int, ushort> column))
                return;

            foreach (KeyValuePair<int, ushort> edit in column)
            {
                int local = edit.Key;
                int localX = (local >> 12) & VoxelConstants.ChunkMask;
                int y = (local >> 5) & 0x7F;
                int localZ = local & VoxelConstants.ChunkMask;

                int chunkY = y >> VoxelConstants.ChunkShift;
                int localY = y & VoxelConstants.ChunkMask;
                int index = (localX * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + localZ;

                voxels[chunkY][index] = edit.Value;
            }
        }

        public void WriteEdits(BinaryWriter writer)
        {
            writer.Write(EditCount);

            foreach (KeyValuePair<long, Dictionary<int, ushort>> pair in _columnEdits)
            {
                int chunkX = (int)(pair.Key >> 32);
                int chunkZ = unchecked((int)(pair.Key & 0xFFFFFFFF));

                foreach (KeyValuePair<int, ushort> edit in pair.Value)
                {
                    int local = edit.Key;
                    int localX = (local >> 12) & VoxelConstants.ChunkMask;
                    int y = (local >> 5) & 0x7F;
                    int localZ = local & VoxelConstants.ChunkMask;

                    writer.Write(chunkX * VoxelConstants.ChunkSize + localX);
                    writer.Write(y);
                    writer.Write(chunkZ * VoxelConstants.ChunkSize + localZ);
                    writer.Write(edit.Value);
                }
            }
        }

        public void ReadEdits(BinaryReader reader)
        {
            _columnEdits.Clear();

            int count = reader.ReadInt32();

            for (int i = 0; i < count; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                ushort block = reader.ReadUInt16();

                StoreEdit(x, y, z, block);
            }
        }

        public void SetBlock(int x, int y, int z, ushort block)
        {
            using var setBlockMarker = VoxelProfiler.SetBlock.Auto();

            if (y < 0 || y >= VoxelConstants.WorldHeight)
                return;

            var id = ChunkId.FromWorldPos(x, y, z);
            if (!_chunks.TryGetValue(id, out Chunk chunk))
                return;

            int localX = x & VoxelConstants.ChunkMask;
            int localY = y & VoxelConstants.ChunkMask;
            int localZ = z & VoxelConstants.ChunkMask;

            chunk[localX, localY, localZ] = block;
            StoreEdit(x, y, z, block);

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