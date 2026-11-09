using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxels
{
    /// <summary>Биом поверхности: определяется высотой, температурой и влажностью.</summary>
    public enum Biome : byte
    {
        Plains = 0,
        Forest = 1,
        Desert = 2,
        Tundra = 3,
        Beach = 4,
        Mountains = 5,
    }

    /// <summary>
    /// Детерминированный генератор рельефа: fBm Перлина + domain warp + ridged-горы + террасы,
    /// затем клеточно-автоматная осыпь, которая формирует обрывы и конусы осыпи.
    /// </summary>
    public sealed class TerrainGenerator
    {
        readonly Dictionary<long, short> _heightCache = new Dictionary<long, short>();
        readonly Dictionary<long, byte> _biomeCache = new Dictionary<long, byte>();

        readonly float _seedContinent;
        readonly float _seedMountain;
        readonly float _seedRidge;
        readonly float _seedWarpX;
        readonly float _seedWarpZ;
        readonly float _seedRock;
        readonly float _seedCliff;
        readonly float _seedTemperature;
        readonly float _seedHumidity;

        public TerrainGenerator(int seed = 1337)
        {
            Seed = seed;
            var random = new System.Random(seed);
            _seedContinent = Offset(random);
            _seedMountain = Offset(random);
            _seedRidge = Offset(random);
            _seedWarpX = Offset(random);
            _seedWarpZ = Offset(random);
            _seedRock = Offset(random);
            _seedCliff = Offset(random);
            _seedTemperature = Offset(random);
            _seedHumidity = Offset(random);
        }

        public int Seed { get; }

        /// <summary>Параметры климата для 3D-режима и других подсистем.</summary>
        public TerrainParams GetParams() => CreateParams();

        /// <summary>Считать высоты и осыпь Burst-джобами (быстрее в разы).</summary>
        public bool UseJobs = true;

        // --- настройки обрывов ---

        public bool CliffsEnabled = true;

        /// <summary>Итераций осыпи: больше — мягче склоны и шире конусы.</summary>
        public int CliffIterations = 2;

        /// <summary>Доля излишка уклона, которая переносится за итерацию.</summary>
        public float CliffStrength = 0.35f;

        /// <summary>Максимальный перепад высоты на один блок по горизонтали.</summary>
        public float CliffMaxSlope = 1.2f;

        /// <summary>Высота структурных обрывов (плато) в блоках.</summary>
        public float CliffUplift = 14f;

        /// <summary>Порог маски обрывов: выше — плато приподнято.</summary>
        public float CliffThreshold = 0.25f;

        /// <summary>Отладка: красить крутые склоны ярким блоком.</summary>
        public bool DebugSlope = false;

        /// <summary>Порог уклона для отладочной подсветки (блоков на блок).</summary>
        public int DebugSlopeThreshold = 3;

        /// <summary>Сколько ячеек поверхности просмотрено и сколько из них крутые (для проверки).</summary>
        public int DebugScannedCells;
        public int DebugSteepCells;

        /// <summary>Сколько колонок пришлось на каждый биом (для проверки).</summary>
        public readonly int[] BiomeCells = new int[6];

        public void ResetDebugStats()
        {
            DebugScannedCells = 0;
            DebugSteepCells = 0;
            System.Array.Clear(BiomeCells, 0, BiomeCells.Length);
        }

        static float Offset(System.Random random) => (float)random.NextDouble() * 1000f;

        /// <summary>Начальная высота поверхности (до осыпи) в мировых координатах.</summary>
        public float GetHeight(float worldX, float worldZ)
            => TerrainMath.Height(worldX, worldZ, CreateParams());

        TerrainParams CreateParams()
        {
            return new TerrainParams
            {
                SeedContinent = _seedContinent,
                SeedMountain = _seedMountain,
                SeedRidge = _seedRidge,
                SeedWarpX = _seedWarpX,
                SeedWarpZ = _seedWarpZ,
                SeedCliff = _seedCliff,
                SeedRock = _seedRock,
                SeedTemperature = _seedTemperature,
                SeedHumidity = _seedHumidity,
                CliffUplift = CliffUplift,
                CliffThreshold = CliffThreshold,
                SeaLevel = VoxelConstants.SeaLevel,
                SnowLine = VoxelConstants.SnowLine
            };
        }

        /// <summary>
        /// Высота поверхности. Для уже посчитанной колонки — точная (с осыпью),
        /// для ещё не сгенерированной — приблизительная по исходному рельефу.
        /// Приблизительная нужна только для мешей на границе загруженной области
        /// и исправляется, когда колонка реально генерируется.
        /// </summary>
        public int GetSurfaceHeight(int worldX, int worldZ)
        {
            long key = ((long)worldX << 32) ^ (uint)worldZ;
            if (_heightCache.TryGetValue(key, out short cached))
                return cached;

            int height = (int)math.floor(GetHeight(worldX, worldZ));
            height = math.clamp(height, 1, VoxelConstants.WorldHeight - 1);

            _heightCache[key] = (short)height;
            return height;
        }

        /// <summary>Считает высоты колонки с осыпью и кладёт их в массивы чанка.</summary>
        public void FillColumnHeights(int chunkX, int chunkZ, short[] heights, byte[] biomes)
        {
            if (UseJobs)
                FillColumnHeightsJobs(chunkX, chunkZ, heights, biomes);
            else
                FillColumnHeightsManaged(chunkX, chunkZ, heights, biomes);

            CacheColumn(chunkX, chunkZ, heights, biomes);

            if (DebugSlope)
                CountSteepCells(heights);
        }

        void FillColumnHeightsManaged(int chunkX, int chunkZ, short[] heights, byte[] biomes)
        {
            short[] column = ComputeColumnHeights(chunkX, chunkZ);
            System.Array.Copy(column, heights, column.Length);

            const int size = VoxelConstants.ChunkSize;
            int originX = chunkX * size;
            int originZ = chunkZ * size;

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = z * size + x;
                    biomes[index] = ComputeBiome(heights[index], originX + x, originZ + z);
                }
            }
        }

        void FillColumnHeightsJobs(int chunkX, int chunkZ, short[] heights, byte[] biomes)
        {
            const int size = VoxelConstants.ChunkSize;
            int originX = chunkX * size;
            int originZ = chunkZ * size;

            int iterations = CliffsEnabled ? math.clamp(CliffIterations, 0, 8) : 0;
            int pad = iterations == 0 ? 0 : iterations + 2;
            int grid = size + pad * 2;

            var heightGrid = new NativeArray<float>(grid * grid, Allocator.TempJob);
            var scratch = new NativeArray<float>(grid * grid, Allocator.TempJob);
            var hardness = new NativeArray<float>(grid * grid, Allocator.TempJob);
            var resultHeights = new NativeArray<short>(size * size, Allocator.TempJob);
            var resultBiomes = new NativeArray<byte>(size * size, Allocator.TempJob);

            try
            {
                var fieldJob = new HeightFieldJob
                {
                    Params = CreateParams(),
                    Grid = grid,
                    Pad = pad,
                    OriginX = originX,
                    OriginZ = originZ,
                    Heights = heightGrid,
                    Hardness = hardness
                };

                fieldJob.Schedule(grid * grid, 64).Complete();

                if (iterations > 0)
                {
                    var erosion = new CliffErosionJob
                    {
                        Grid = grid,
                        Iterations = iterations,
                        MaxSlope = math.max(0.1f, CliffMaxSlope),
                        Strength = math.saturate(CliffStrength),
                        Heights = heightGrid,
                        Scratch = scratch,
                        Hardness = hardness
                    };

                    erosion.Schedule().Complete();
                }

                var result = new ColumnResultJob
                {
                    Params = CreateParams(),
                    Grid = grid,
                    Pad = pad,
                    Size = size,
                    OriginX = originX,
                    OriginZ = originZ,
                    Heights = heightGrid,
                    ResultHeights = resultHeights,
                    ResultBiomes = resultBiomes
                };

                result.Schedule(size * size, 64).Complete();

                resultHeights.CopyTo(heights);
                resultBiomes.CopyTo(biomes);
            }
            finally
            {
                heightGrid.Dispose();
                scratch.Dispose();
                hardness.Dispose();
                resultHeights.Dispose();
                resultBiomes.Dispose();
            }
        }

        void CountSteepCells(short[] column)
        {
            const int size = VoxelConstants.ChunkSize;

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    DebugScannedCells++;

                    if (SlopeAt(column, x, z) >= DebugSlopeThreshold)
                        DebugSteepCells++;
                }
            }
        }

        /// <summary>
        /// Считает 32x32 финальных высот колонки. Исходный рельеф берётся с полями
        /// (pad = итерации + 2), чтобы граница не влияла на результат внутри колонки.
        /// </summary>
        short[] ComputeColumnHeights(int chunkX, int chunkZ)
        {
            const int size = VoxelConstants.ChunkSize;
            int originX = chunkX * size;
            int originZ = chunkZ * size;

            int iterations = CliffsEnabled ? math.clamp(CliffIterations, 0, 8) : 0;

            if (iterations == 0)
            {
                var flat = new short[size * size];

                Parallel.For(0, size * size, index =>
                {
                    int x = index & VoxelConstants.ChunkMask;
                    int z = index >> VoxelConstants.ChunkShift;
                    flat[z * size + x] = ToHeight(GetHeight(originX + x, originZ + z));
                });

                return flat;
            }

            int pad = iterations + 2;
            int grid = size + pad * 2;

            var height = new float[grid * grid];
            var next = new float[height.Length];
            var hardness = new float[height.Length];

            Parallel.For(0, grid, gz =>
            {
                int worldZ = originZ + gz - pad;

                for (int gx = 0; gx < grid; gx++)
                {
                    int worldX = originX + gx - pad;
                    int index = gz * grid + gx;

                    height[index] = GetHeight(worldX, worldZ);

                    float rock = Noise.Fbm(
                        new float2(worldX * 0.02f, worldZ * 0.02f),
                        2, 2f, 0.5f, _seedRock);
                    hardness[index] = math.saturate(rock * 0.5f + 0.5f);
                }
            });

            float slope = math.max(0.1f, CliffMaxSlope);
            float strength = math.saturate(CliffStrength);

            // Осыпь: на каждой итерации материал из ячейки съезжает к самому
            // низкому из четырёх соседей, если перепад больше угла откоса.
            // Правим второй буфер, чтобы порядок обхода не влиял на результат.
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                System.Array.Copy(height, next, height.Length);

                for (int gz = 1; gz < grid - 1; gz++)
                {
                    int row = gz * grid;

                    for (int gx = 1; gx < grid - 1; gx++)
                    {
                        int index = row + gx;
                        float h = height[index];

                        int lowest = index;
                        float lowestHeight = h;

                        int neighbour = index - 1;
                        if (height[neighbour] < lowestHeight)
                        {
                            lowestHeight = height[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index + 1;
                        if (height[neighbour] < lowestHeight)
                        {
                            lowestHeight = height[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index - grid;
                        if (height[neighbour] < lowestHeight)
                        {
                            lowestHeight = height[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index + grid;
                        if (height[neighbour] < lowestHeight)
                        {
                            lowestHeight = height[neighbour];
                            lowest = neighbour;
                        }

                        if (lowest == index)
                            continue;

                        float excess = h - (lowestHeight + slope);
                        if (excess <= 0f)
                            continue;

                        // Твёрдая порода почти не осыпается — остаются отвесные стены.
                        float move = excess * strength * (1f - hardness[index]);
                        if (move <= 0f)
                            continue;

                        next[index] -= move;
                        next[lowest] += move;
                    }
                }

                float[] swap = height;
                height = next;
                next = swap;
            }

            var result = new short[size * size];

            for (int z = 0; z < size; z++)
            {
                int row = (z + pad) * grid + pad;

                for (int x = 0; x < size; x++)
                    result[z * size + x] = ToHeight(height[row + x]);
            }

            return result;
        }

        void CacheColumn(int chunkX, int chunkZ, short[] column, byte[] biomes)
        {
            const int size = VoxelConstants.ChunkSize;
            int originX = chunkX * size;
            int originZ = chunkZ * size;

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    long key = ((long)(originX + x) << 32) ^ (uint)(originZ + z);
                    int height = column[z * size + x];
                    byte biome = biomes[z * size + x];

                    _heightCache[key] = (short)height;
                    _biomeCache[key] = biome;
                    BiomeCells[biome]++;
                }
            }
        }

        static short ToHeight(float height)
            => (short)math.clamp((int)math.round(height), 1, VoxelConstants.WorldHeight - 1);

        /// <summary>Заполняет чанк по готовым высотам и биомам колонки. true — если есть твёрдые блоки.</summary>
        public bool FillChunkFromHeights(ChunkId id, short[] heights, byte[] biomes, ushort[] voxels)
        {
            System.Array.Clear(voxels, 0, voxels.Length);

            const int size = VoxelConstants.ChunkSize;
            int originY = id.Y * size;

            if (originY >= VoxelConstants.WorldHeight)
                return false;

            bool hasBlocks = false;

            for (int x = 0; x < size; x++)
            {
                for (int z = 0; z < size; z++)
                {
                    int height = heights[z * size + x];
                    if (height < originY)
                        continue;

                    byte biome = biomes[z * size + x];
                    int slope = DebugSlope ? SlopeAt(heights, x, z) : 0;
                    bool markCliff = DebugSlope && slope >= DebugSlopeThreshold;

                    int maxLocalY = math.min(size - 1, height - originY);
                    for (int y = 0; y <= maxLocalY; y++)
                    {
                        int worldY = originY + y;
                        ushort block = BlockAt(worldY, height, biome);

                        if (markCliff && worldY >= height - 2)
                            block = BlockType.Debug;

                        voxels[(x * size + y) * size + z] = block;
                    }

                    hasBlocks = true;
                }
            }

            return hasBlocks;
        }

        /// <summary>Максимальный перепад высоты с четырьмя соседями (для отладки обрывов).</summary>
        static int SlopeAt(short[] heights, int x, int z)
        {
            const int size = VoxelConstants.ChunkSize;
            int height = heights[z * size + x];
            int slope = 0;

            if (x > 0)
                slope = math.max(slope, math.abs(height - heights[z * size + x - 1]));
            if (x < size - 1)
                slope = math.max(slope, math.abs(height - heights[z * size + x + 1]));
            if (z > 0)
                slope = math.max(slope, math.abs(height - heights[(z - 1) * size + x]));
            if (z < size - 1)
                slope = math.max(slope, math.abs(height - heights[(z + 1) * size + x]));

            return slope;
        }

        /// <summary>Блок в произвольной мировой точке (границы чанков, будущие коллизии).</summary>
        public ushort GetBlock(int worldX, int worldY, int worldZ)
        {
            if (worldY < 0)
                return BlockType.Stone;
            if (worldY >= VoxelConstants.WorldHeight)
                return BlockType.Air;

            return BlockAt(worldY, GetSurfaceHeight(worldX, worldZ), GetBiome(worldX, worldZ));
        }

        /// <summary>Биом колонки. Считается один раз на колонку и кэшируется.</summary>
        public byte GetBiome(int worldX, int worldZ)
        {
            long key = ((long)worldX << 32) ^ (uint)worldZ;
            if (_biomeCache.TryGetValue(key, out byte cached))
                return cached;

            byte biome = ComputeBiome(GetSurfaceHeight(worldX, worldZ), worldX, worldZ);
            _biomeCache[key] = biome;
            return biome;
        }

        /// <summary>
        /// Пустыня — сухо, тундра — холодно, лес — влажно, пляж — у воды,
        /// горы — высоко. Порог по высоте проверяется первым, поэтому снежные
        /// вершины остаются горами независимо от климата.
        /// </summary>
        byte ComputeBiome(int height, int worldX, int worldZ)
            => TerrainMath.SelectBiome(height, worldX, worldZ, CreateParams());

        /// <summary>Убирает колонку из кэша при выгрузке чанков.</summary>
        public void ForgetColumn(int chunkX, int chunkZ)
        {
            int originX = chunkX * VoxelConstants.ChunkSize;
            int originZ = chunkZ * VoxelConstants.ChunkSize;

            for (int z = originZ - 1; z <= originZ + VoxelConstants.ChunkSize; z++)
            {
                for (int x = originX - 1; x <= originX + VoxelConstants.ChunkSize; x++)
                {
                    long key = ((long)x << 32) ^ (uint)z;
                    _heightCache.Remove(key);
                    _biomeCache.Remove(key);
                }
            }
        }

        public void ClearHeightCache()
        {
            _heightCache.Clear();
            _biomeCache.Clear();
        }

        static ushort BlockAt(int worldY, int surfaceHeight, byte biome)
        {
            if (worldY > surfaceHeight)
                return BlockType.Air;

            bool surface = worldY == surfaceHeight;
            bool nearSurface = worldY > surfaceHeight - 4;

            if (surface)
            {
                switch ((Biome)biome)
                {
                    case Biome.Beach:
                    case Biome.Desert:
                        return BlockType.Sand;
                    case Biome.Tundra:
                        return BlockType.Snow;
                    case Biome.Forest:
                        return BlockType.ForestGrass;
                    case Biome.Mountains:
                        return surfaceHeight >= VoxelConstants.SnowLine
                            ? BlockType.Snow
                            : BlockType.Stone;
                    default:
                        return BlockType.Grass;
                }
            }

            switch ((Biome)biome)
            {
                case Biome.Beach:
                    return worldY > surfaceHeight - 3 ? BlockType.Sand : BlockType.Stone;
                case Biome.Desert:
                    return nearSurface ? BlockType.Sand : BlockType.Stone;
                case Biome.Mountains:
                    return BlockType.Stone;
                default:
                    return nearSurface ? BlockType.Dirt : BlockType.Stone;
            }
        }

        /// <summary>Квантование высоты по ступеням с плавным переходом — основа обрывов.</summary>
        static float Terrace(float height, float step)
            => TerrainMath.Terrace(height, step);
    }
}
