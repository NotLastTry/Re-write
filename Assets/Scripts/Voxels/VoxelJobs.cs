using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Voxels
{
    /// <summary>
    /// Параметры мира в виде blittable-структуры: её можно передавать в Burst-джобы.
    /// </summary>
    public struct TerrainParams
    {
        public float SeedContinent;
        public float SeedMountain;
        public float SeedRidge;
        public float SeedWarpX;
        public float SeedWarpZ;
        public float SeedCliff;
        public float SeedRock;
        public float SeedTemperature;
        public float SeedHumidity;

        public float CliffUplift;
        public float CliffThreshold;

        public int SeaLevel;
        public int SnowLine;
    }

    /// <summary>
    /// Чистые функции рельефа. Вызываются и из управляемого кода, и из Burst-джоб,
    /// поэтому формула гарантированно одна и та же.
    /// </summary>
    public static class TerrainMath
    {
        public static float Height(float worldX, float worldZ, TerrainParams p)
        {
            float2 pos = new float2(worldX, worldZ);

            float2 warp = new float2(
                Noise.Fbm(pos * 0.006f, 3, 2f, 0.5f, p.SeedWarpX),
                Noise.Fbm(pos * 0.006f, 3, 2f, 0.5f, p.SeedWarpZ));
            pos += warp * 14f;

            float continents = Noise.Fbm(pos * 0.0035f, 5, 2.03f, 0.5f, p.SeedContinent);

            float mountainMask = math.saturate(
                Noise.Fbm(pos * 0.0022f, 3, 2f, 0.5f, p.SeedMountain) * 0.5f + 0.5f);
            mountainMask *= mountainMask;

            float ridges = Noise.Ridged(pos * 0.008f, 4, p.SeedRidge);

            float height = p.SeaLevel + 6f
                         + continents * 18f
                         + ridges * 52f * mountainMask;

            height = Terrace(height, 7f);

            float cliffMask = Noise.Fbm(pos * 0.006f, 2, 2f, 0.5f, p.SeedCliff);
            if (cliffMask > p.CliffThreshold)
                height += p.CliffUplift;

            return math.clamp(height, 4f, VoxelConstants.WorldHeight - 6f);
        }

        public static float Terrace(float height, float step)
        {
            float steps = height / step;
            float fraction = steps - math.floor(steps);
            return (math.floor(steps) + math.smoothstep(0.35f, 0.65f, fraction)) * step;
        }

        public static byte SelectBiome(int height, int worldX, int worldZ, TerrainParams p)
        {
            if (height <= p.SeaLevel + 2)
                return (byte)Biome.Beach;

            if (height >= p.SnowLine - 8)
                return (byte)Biome.Mountains;

            float temperature = Noise.Fbm(
                new float2(worldX * 0.0015f, worldZ * 0.0015f),
                2, 2f, 0.5f, p.SeedTemperature);

            float humidity = Noise.Fbm(
                new float2(worldX * 0.002f, worldZ * 0.002f),
                2, 2f, 0.5f, p.SeedHumidity);

            if (temperature < -0.3f)
                return (byte)Biome.Tundra;

            if (humidity < -0.25f)
                return (byte)Biome.Desert;

            if (humidity > 0.15f)
                return (byte)Biome.Forest;

            return (byte)Biome.Plains;
        }
    }

    /// <summary>Считает исходные высоты и карту твёрдости для колонки с полями.</summary>
    [BurstCompile]
    public struct HeightFieldJob : IJobParallelFor
    {
        public TerrainParams Params;
        public int Grid;
        public int Pad;
        public int OriginX;
        public int OriginZ;

        public NativeArray<float> Heights;
        public NativeArray<float> Hardness;

        public void Execute(int index)
        {
            int z = index / Grid;
            int x = index - z * Grid;

            int worldX = OriginX + x - Pad;
            int worldZ = OriginZ + z - Pad;

            Heights[index] = TerrainMath.Height(worldX, worldZ, Params);

            float rock = Noise.Fbm(
                new float2(worldX * 0.02f, worldZ * 0.02f),
                2, 2f, 0.5f, Params.SeedRock);

            Hardness[index] = math.saturate(rock * 0.5f + 0.5f);
        }
    }

    /// <summary>Клеточно-автоматная осыпь по карте высот.</summary>
    [BurstCompile]
    public struct CliffErosionJob : IJob
    {
        public int Grid;
        public int Iterations;
        public float MaxSlope;
        public float Strength;

        public NativeArray<float> Heights;
        public NativeArray<float> Scratch;
        public NativeArray<float> Hardness;

        public void Execute()
        {
            if (Iterations <= 0)
                return;

            NativeArray<float> current = Heights;
            NativeArray<float> next = Scratch;
            bool swapped = false;

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                for (int i = 0; i < current.Length; i++)
                    next[i] = current[i];

                for (int z = 1; z < Grid - 1; z++)
                {
                    int row = z * Grid;

                    for (int x = 1; x < Grid - 1; x++)
                    {
                        int index = row + x;
                        float h = current[index];

                        int lowest = index;
                        float lowestHeight = h;

                        int neighbour = index - 1;
                        if (current[neighbour] < lowestHeight)
                        {
                            lowestHeight = current[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index + 1;
                        if (current[neighbour] < lowestHeight)
                        {
                            lowestHeight = current[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index - Grid;
                        if (current[neighbour] < lowestHeight)
                        {
                            lowestHeight = current[neighbour];
                            lowest = neighbour;
                        }

                        neighbour = index + Grid;
                        if (current[neighbour] < lowestHeight)
                        {
                            lowestHeight = current[neighbour];
                            lowest = neighbour;
                        }

                        if (lowest == index)
                            continue;

                        float excess = h - (lowestHeight + MaxSlope);
                        if (excess <= 0f)
                            continue;

                        float move = excess * Strength * (1f - Hardness[index]);
                        if (move <= 0f)
                            continue;

                        next[index] -= move;
                        next[lowest] += move;
                    }
                }

                NativeArray<float> swap = current;
                current = next;
                next = swap;
                swapped = !swapped;
            }

            if (swapped)
            {
                for (int i = 0; i < current.Length; i++)
                    Heights[i] = current[i];
            }
        }
    }

    /// <summary>Вырезает центральные 32x32 высот из сетки с полями и сразу выбирает биом.</summary>
    [BurstCompile]
    public struct ColumnResultJob : IJobParallelFor
    {
        public TerrainParams Params;
        public int Grid;
        public int Pad;
        public int Size;
        public int OriginX;
        public int OriginZ;

        [ReadOnly] public NativeArray<float> Heights;
        public NativeArray<short> ResultHeights;
        public NativeArray<byte> ResultBiomes;

        public void Execute(int index)
        {
            int z = index / Size;
            int x = index - z * Size;

            float value = Heights[(z + Pad) * Grid + (x + Pad)];
            int height = (int)math.clamp((int)math.round(value), 1, VoxelConstants.WorldHeight - 1);

            ResultHeights[index] = (short)height;
            ResultBiomes[index] = TerrainMath.SelectBiome(height, OriginX + x, OriginZ + z, Params);
        }
    }

    /// <summary>Burst-версия хеш-шума пещер.</summary>
    public static class CaveMath
    {
        public static float ValueNoise(float x, float y, float z, uint seed)
        {
            int xi = (int)math.floor(x);
            int yi = (int)math.floor(y);
            int zi = (int)math.floor(z);

            float tx = Smooth(x - xi);
            float ty = Smooth(y - yi);
            float tz = Smooth(z - zi);

            float c000 = Lattice(xi, yi, zi, seed);
            float c100 = Lattice(xi + 1, yi, zi, seed);
            float c010 = Lattice(xi, yi + 1, zi, seed);
            float c110 = Lattice(xi + 1, yi + 1, zi, seed);
            float c001 = Lattice(xi, yi, zi + 1, seed);
            float c101 = Lattice(xi + 1, yi, zi + 1, seed);
            float c011 = Lattice(xi, yi + 1, zi + 1, seed);
            float c111 = Lattice(xi + 1, yi + 1, zi + 1, seed);

            float c00 = math.lerp(c000, c100, tx);
            float c10 = math.lerp(c010, c110, tx);
            float c01 = math.lerp(c001, c101, tx);
            float c11 = math.lerp(c011, c111, tx);

            float c0 = math.lerp(c00, c10, ty);
            float c1 = math.lerp(c01, c11, ty);

            return math.lerp(c0, c1, tz);
        }

        public static float Smooth(float t) => t * t * (3f - 2f * t);

        public static float Lattice(int x, int y, int z, uint seed)
            => Hash01(x, y, z, seed) * 2f - 1f;

        public static uint Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u;
                h += (uint)y * 668265263u;
                h += (uint)z * 1274126177u;
                h += seed * 2654435761u;
                h ^= h >> 13;
                h *= 1274126177u;
                return h ^ (h >> 16);
            }
        }

        public static float Hash01(int x, int y, int z, uint seed)
            => (Hash(x, y, z, seed) & 0xFFFFFFu) / 16777215f;
    }

    /// <summary>Заполнение пещерной сетки: зона + случайный твёрдый/пустой.</summary>
    [BurstCompile]
    public struct CaveSeedJob : IJobParallelFor
    {
        public int Gx;
        public int Gy;
        public int Gz;
        public int Pad;
        public int CellSize;
        public int CellOriginX;
        public int CellOriginZ;

        public float RegionScale;
        public float RegionThreshold;
        public float Fill;
        public uint Seed;

        public NativeArray<byte> Grid;

        public void Execute(int index)
        {
            int y = index / (Gx * Gz);
            int rest = index - y * Gx * Gz;
            int z = rest / Gx;
            int x = rest - z * Gx;

            int cellX = CellOriginX + x - Pad;
            int cellY = y - Pad;
            int cellZ = CellOriginZ + z - Pad;

            float zoneNoise = CaveMath.ValueNoise(
                cellX * RegionScale,
                cellY * RegionScale,
                cellZ * RegionScale,
                Seed + 7919u);

            byte value = 1;

            if (zoneNoise > RegionThreshold &&
                CaveMath.Hash01(cellX, cellY, cellZ, Seed) >= Fill)
                value = 0;

            Grid[index] = value;
        }
    }

    /// <summary>Одна итерация 3D-автомата пещер по строкам.</summary>
    [BurstCompile]
    public struct CaveStepJob : IJobParallelFor
    {
        public int Gx;
        public int Gy;
        public int Gz;
        public int SolidThreshold;
        public int AirThreshold;

        [ReadOnly] public NativeArray<byte> Current;

        // Строки независимы: каждая пишет только свои ячейки (y, z),
        // поэтому ограничение «пиши только по jobIndex» здесь не нужно.
        [NativeDisableParallelForRestriction] public NativeArray<byte> Next;

        public void Execute(int rowIndex)
        {
            int zRows = Gz - 2;
            int y = rowIndex / zRows + 1;
            int z = rowIndex % zRows + 1;
            int row = (y * Gz + z) * Gx;

            for (int x = 1; x < Gx - 1; x++)
            {
                int index = row + x;
                int solid = -Current[index];

                for (int dy = -1; dy <= 1; dy++)
                {
                    int baseY = ((y + dy) * Gz + z) * Gx;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int baseZ = baseY + dz * Gx;

                        for (int dx = -1; dx <= 1; dx++)
                            solid += Current[baseZ + x + dx];
                    }
                }

                byte value = Current[index];

                if (solid >= SolidThreshold)
                    value = 1;
                else if (solid <= AirThreshold)
                    value = 0;

                Next[index] = value;
            }
        }
    }
}
