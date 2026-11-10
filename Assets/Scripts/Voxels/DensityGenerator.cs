using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxels
{
    /// <summary>
    /// Параметры 3D-плотности. Климат берётся из TerrainParams, чтобы биомы
    /// и уровень моря совпадали с обычным режимом.
    /// </summary>
    public struct DensityParams
    {
        public TerrainParams Climate;

        public float SeedDensity;
        public float SeedContinents;
        public float SeedErosion;
        public float SeedRidges;

        public float Amplitude;
        public float Frequency;
        public float VerticalFrequency;

        /// <summary>0 — старый рельеф, 1 — полностью сплайновый (в духе 1.18).</summary>
        public float SplineBlend;

        public float SeedCave;
        public float CaveFrequency;
        public float CaveThreshold;

        public int WorldHeight;
    }

    /// <summary>
    /// 3D-плотность: базовая высота из проверенного рельефа плюс объёмный шум.
    /// Именно шум даёт нависания, карнизы и неровные стены, которых нет у карты высот.
    /// </summary>
    public static class DensityMath
    {
        /// <summary>Кусочно-линейный сплайн смещения относительно уровня моря.</summary>
        static float OffsetSpline(float continents)
        {
            if (continents < -0.6f)
                return math.lerp(-44f, -28f, math.saturate((continents + 1f) / 0.4f));

            if (continents < -0.2f)
                return math.lerp(-28f, -10f, (continents + 0.6f) / 0.4f);

            if (continents < 0.1f)
                return math.lerp(-10f, 6f, (continents + 0.2f) / 0.3f);

            if (continents < 0.45f)
                return math.lerp(6f, 24f, (continents - 0.1f) / 0.35f);

            return math.lerp(24f, 52f, math.saturate((continents - 0.45f) / 0.55f));
        }

        /// <summary>
        /// Базовая высота: смесь прежнего рельефа и сплайнового (continentalness + erosion + ridges).
        /// </summary>
        public static float BaseHeight(int x, int z, DensityParams p)
        {
            float legacy = TerrainMath.Height(x, z, p.Climate);

            float2 pos = new float2(x, z);

            float continents = Noise.Fbm(pos * 0.0012f, 4, 2f, 0.5f, p.SeedContinents);
            float erosion = Noise.Fbm(pos * 0.0015f, 4, 2f, 0.5f, p.SeedErosion);
            float ridges = Noise.Ridged(pos * 0.004f, 3, p.SeedRidges);

            float offset = OffsetSpline(continents);
            float factor = math.lerp(1.3f, 0.55f, math.saturate(erosion * 0.5f + 0.5f));
            float mountains = ridges * 34f * math.smoothstep(-0.1f, 0.6f, continents) * factor;

            float splineHeight = p.Climate.SeaLevel + offset * factor + mountains;
            splineHeight = TerrainMath.Terrace(splineHeight, 7f);
            splineHeight = math.clamp(splineHeight, 4f, VoxelConstants.WorldHeight - 6f);

            return math.lerp(legacy, splineHeight, p.SplineBlend);
        }

        /// <summary>Биом: континентальность отделяет низины у воды, остальное — климат.</summary>
        public static byte SelectBiome(int height, int x, int z, DensityParams p)
        {
            float continents = Noise.Fbm(
                new float2(x * 0.0012f, z * 0.0012f), 4, 2f, 0.5f, p.SeedContinents);

            if (continents < -0.4f)
                return (byte)Biome.Beach;

            return TerrainMath.SelectBiome(height, x, z, p.Climate);
        }

        public static float Density(int x, int y, int z, DensityParams p)
            => DensityWithBase(BaseHeight(x, z, p), x, y, z, p);

        /// <summary>Плотность при уже посчитанной базовой высоте колонки.</summary>
        public static float DensityWithBase(float baseHeight, int x, int y, int z, DensityParams p)
        {
            float noise = Noise.Fbm3(
                new float3(x * p.Frequency, y * p.VerticalFrequency, z * p.Frequency),
                3, 2f, 0.5f, p.SeedDensity);

            return baseHeight + noise * p.Amplitude - y;
        }

        public static ushort BlockAt(int worldY, int surfaceHeight, byte biome)
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
    }

    /// <summary>Заполняет колонку по 3D-плотности: 4 чанка, высоты поверхности, биомы.</summary>
    [BurstCompile]
    public struct DensityColumnJob : IJobParallelFor
    {
        public DensityParams Params;
        public int OriginX;
        public int OriginZ;
        public int Size;
        public int WorldHeight;

        // Каждая колонка пишет много вокселей, поэтому ограничение по jobIndex снимается.
        [NativeDisableParallelForRestriction] public NativeArray<ushort> Voxels;

        public NativeArray<short> Surfaces;
        public NativeArray<byte> Biomes;

        public void Execute(int index)
        {
            int z = index / Size;
            int x = index - z * Size;

            int worldX = OriginX + x;
            int worldZ = OriginZ + z;

            int top = 1;
            float baseHeight = DensityMath.BaseHeight(worldX, worldZ, Params);

            for (int y = 0; y < WorldHeight; y++)
            {
                bool solid = DensityMath.DensityWithBase(baseHeight, worldX, y, worldZ, Params) > 0f;

                int chunkY = y >> VoxelConstants.ChunkShift;
                int localY = y & VoxelConstants.ChunkMask;
                int voxelIndex = chunkY * VoxelConstants.ChunkVolume
                               + (x * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + z;

                Voxels[voxelIndex] = solid ? BlockType.Stone : BlockType.Air;

                if (solid)
                    top = y;
            }

            byte biome = DensityMath.SelectBiome(top, worldX, worldZ, Params);

            // Вода только над поверхностью: пещеры ниже уровня моря остаются воздушными.
            for (int y = top + 1; y <= Params.Climate.SeaLevel && y < WorldHeight; y++)
            {
                int chunkY = y >> VoxelConstants.ChunkShift;
                int localY = y & VoxelConstants.ChunkMask;
                int voxelIndex = chunkY * VoxelConstants.ChunkVolume
                               + (x * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + z;

                Voxels[voxelIndex] = BlockType.Water;
            }

            Surfaces[index] = (short)top;
            Biomes[index] = biome;

            int first = math.max(0, top - 3);

            for (int y = first; y <= top; y++)
            {
                int chunkY = y >> VoxelConstants.ChunkShift;
                int localY = y & VoxelConstants.ChunkMask;
                int voxelIndex = chunkY * VoxelConstants.ChunkVolume
                               + (x * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + z;

                Voxels[voxelIndex] = DensityMath.BlockAt(y, top, biome);
            }

            // Шумовые карверы: вырезаем полости ниже поверхности.
            if (Params.CaveThreshold < 1f)
            {
                for (int y = 3; y < top - 4; y++)
                {
                    int chunkY = y >> VoxelConstants.ChunkShift;
                    int localY = y & VoxelConstants.ChunkMask;
                    int voxelIndex = chunkY * VoxelConstants.ChunkVolume
                                   + (x * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + z;

                    ushort current = Voxels[voxelIndex];
                    if (current == BlockType.Air || current == BlockType.Water)
                        continue;

                    float cave = Noise.Fbm3(
                        new float3(
                            worldX * Params.CaveFrequency,
                            y * Params.CaveFrequency * 1.6f,
                            worldZ * Params.CaveFrequency),
                        2, 2f, 0.5f, Params.SeedCave);

                    if (cave > Params.CaveThreshold)
                        Voxels[voxelIndex] = BlockType.Air;
                }
            }
        }
    }

    /// <summary>
    /// Генератор в режиме 3D-плотности. Хранит кэш высот/биомов для запросов
    /// на границах чанков и для спавна.
    /// </summary>
    public sealed class DensityGenerator
    {
        readonly Dictionary<long, short> _heightCache = new Dictionary<long, short>();
        readonly Dictionary<long, byte> _biomeCache = new Dictionary<long, byte>();

        public DensityParams Params = new DensityParams
        {
            Amplitude = 8f,
            Frequency = 0.012f,
            VerticalFrequency = 0.02f,
            SplineBlend = 0.5f,
            CaveFrequency = 0.02f,
            CaveThreshold = 0.6f,
            WorldHeight = VoxelConstants.WorldHeight
        };

        public float Amplitude
        {
            get => Params.Amplitude;
            set => Params.Amplitude = value;
        }

        public float Frequency
        {
            get => Params.Frequency;
            set => Params.Frequency = value;
        }

        public float VerticalFrequency
        {
            get => Params.VerticalFrequency;
            set => Params.VerticalFrequency = value;
        }

        public float SplineBlend
        {
            get => Params.SplineBlend;
            set => Params.SplineBlend = value;
        }

        public float CaveThreshold
        {
            get => Params.CaveThreshold;
            set => Params.CaveThreshold = value;
        }

        public float CaveFrequency
        {
            get => Params.CaveFrequency;
            set => Params.CaveFrequency = value;
        }

        public void Configure(TerrainParams climate, int worldSeed)
        {
            Params.Climate = climate;
            Params.SeedContinents = worldSeed * 1.31f + 101f;
            Params.SeedErosion = worldSeed * 2.17f + 211f;
            Params.SeedRidges = worldSeed * 3.73f + 307f;
            Params.SeedCave = worldSeed * 4.19f + 401f;
        }

        public void FillColumn(int chunkX, int chunkZ, ushort[][] voxels, short[] heights, byte[] biomes)
        {
            const int size = VoxelConstants.ChunkSize;
            const int volume = VoxelConstants.ChunkVolume;
            int chunkCount = VoxelConstants.ChunksInHeight;

            var voxelsNative = new NativeArray<ushort>(chunkCount * volume, Allocator.TempJob);
            var surfaces = new NativeArray<short>(size * size, Allocator.TempJob);
            var biomesNative = new NativeArray<byte>(size * size, Allocator.TempJob);

            try
            {
                var job = new DensityColumnJob
                {
                    Params = Params,
                    OriginX = chunkX * size,
                    OriginZ = chunkZ * size,
                    Size = size,
                    WorldHeight = VoxelConstants.WorldHeight,
                    Voxels = voxelsNative,
                    Surfaces = surfaces,
                    Biomes = biomesNative
                };

                job.Schedule(size * size, 16).Complete();

                for (int chunkY = 0; chunkY < chunkCount; chunkY++)
                    voxelsNative.GetSubArray(chunkY * volume, volume).CopyTo(voxels[chunkY]);

                surfaces.CopyTo(heights);
                biomesNative.CopyTo(biomes);
            }
            finally
            {
                voxelsNative.Dispose();
                surfaces.Dispose();
                biomesNative.Dispose();
            }

            CacheColumn(chunkX, chunkZ, heights, biomes);
        }

        void CacheColumn(int chunkX, int chunkZ, short[] heights, byte[] biomes)
        {
            const int size = VoxelConstants.ChunkSize;
            int originX = chunkX * size;
            int originZ = chunkZ * size;

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    long key = ((long)(originX + x) << 32) ^ (uint)(originZ + z);
                    _heightCache[key] = heights[z * size + x];
                    _biomeCache[key] = biomes[z * size + x];
                }
            }
        }

        public int GetSurfaceHeight(int worldX, int worldZ)
        {
            long key = ((long)worldX << 32) ^ (uint)worldZ;
            if (_heightCache.TryGetValue(key, out short cached))
                return cached;

            int height = (int)math.floor(DensityMath.BaseHeight(worldX, worldZ, Params));
            height = math.clamp(height, 1, VoxelConstants.WorldHeight - 1);

            _heightCache[key] = (short)height;
            return height;
        }

        public byte GetBiome(int worldX, int worldZ)
        {
            long key = ((long)worldX << 32) ^ (uint)worldZ;
            if (_biomeCache.TryGetValue(key, out byte cached))
                return cached;

            byte biome = DensityMath.SelectBiome(GetSurfaceHeight(worldX, worldZ), worldX, worldZ, Params);
            _biomeCache[key] = biome;
            return biome;
        }

        public ushort GetBlock(int worldX, int worldY, int worldZ)
        {
            if (worldY < 0)
                return BlockType.Stone;
            if (worldY >= VoxelConstants.WorldHeight)
                return BlockType.Air;

            ushort block = DensityMath.BlockAt(worldY, GetSurfaceHeight(worldX, worldZ), GetBiome(worldX, worldZ));

            // На границе стриминга уровень моря тоже вода, иначе видны
            // вертикальные стены воды на краю загруженной области.
            if (block == BlockType.Air && worldY <= VoxelConstants.SeaLevel)
                return BlockType.Water;

            return block;
        }

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

        public void ClearCaches()
        {
            _heightCache.Clear();
            _biomeCache.Clear();
        }
    }
}
