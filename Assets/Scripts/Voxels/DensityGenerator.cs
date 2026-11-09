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
        public float Amplitude;
        public float Frequency;
        public float VerticalFrequency;
        public int WorldHeight;
    }

    /// <summary>
    /// 3D-плотность: базовая высота из проверенного рельефа плюс объёмный шум.
    /// Именно шум даёт нависания, карнизы и неровные стены, которых нет у карты высот.
    /// </summary>
    public static class DensityMath
    {
        public static float Density(int x, int y, int z, DensityParams p)
            => DensityWithBase(TerrainMath.Height(x, z, p.Climate), x, y, z, p);

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
            float baseHeight = TerrainMath.Height(worldX, worldZ, Params.Climate);

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

            byte biome = TerrainMath.SelectBiome(top, worldX, worldZ, Params.Climate);

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

        public void Configure(TerrainParams climate)
        {
            Params.Climate = climate;
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

            int height = (int)math.floor(TerrainMath.Height(worldX, worldZ, Params.Climate));
            height = math.clamp(height, 1, VoxelConstants.WorldHeight - 1);

            _heightCache[key] = (short)height;
            return height;
        }

        public byte GetBiome(int worldX, int worldZ)
        {
            long key = ((long)worldX << 32) ^ (uint)worldZ;
            if (_biomeCache.TryGetValue(key, out byte cached))
                return cached;

            byte biome = TerrainMath.SelectBiome(GetSurfaceHeight(worldX, worldZ), worldX, worldZ, Params.Climate);
            _biomeCache[key] = biome;
            return biome;
        }

        public ushort GetBlock(int worldX, int worldY, int worldZ)
        {
            if (worldY < 0)
                return BlockType.Stone;
            if (worldY >= VoxelConstants.WorldHeight)
                return BlockType.Air;

            return DensityMath.BlockAt(worldY, GetSurfaceHeight(worldX, worldZ), GetBiome(worldX, worldZ));
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
