using System.Threading.Tasks;
using Unity.Mathematics;

namespace Voxels
{
    /// <summary>
    /// Пещеры: 3D клеточный автомат на грубой сетке (2 м) + маска пещерных зон
    /// + трилинейная интерполяция. Всё детерминировано от мировых координат,
    /// поэтому стыки колонок бесшовны и соседние чанки не нужны.
    /// </summary>
    public sealed class CaveGenerator
    {
        public bool Enabled = true;

        /// <summary>Размер ячейки CA в блоках (1, 2, 4, 8, 16).</summary>
        public int CellSize = 2;

        /// <summary>Число итераций сглаживания.</summary>
        public int Iterations = 2;

        /// <summary>Вероятность твёрдой ячейки внутри пещерной зоны.</summary>
        public float Fill = 0.55f;

        /// <summary>Соседей-твёрдых >= порога → ячейка твёрдая.</summary>
        public int SolidThreshold = 14;

        /// <summary>Соседей-твёрдых &lt;= порога → ячейка воздух.</summary>
        public int AirThreshold = 5;

        /// <summary>Сколько блоков от поверхности не трогать (5 — пещеры не выходят наверх).</summary>
        public int SurfaceMargin = 5;

        public uint Seed = 1;

        /// <summary>Масштаб пещерных систем: меньше — крупнее и реже.</summary>
        public float RegionScale = 0.045f;

        /// <summary>Порог маски: выше — зона пещер. Больше → меньше пещер.</summary>
        public float RegionThreshold = 0.3f;

        public void Carve(int chunkX, int chunkZ, short[] heights, ushort[][] voxels)
        {
            if (!Enabled)
                return;

            int cell = math.max(1, CellSize);
            if (VoxelConstants.ChunkSize % cell != 0)
                cell = 2;

            int cellsX = VoxelConstants.ChunkSize / cell;
            int cellsZ = cellsX;
            int pad = Iterations + 2;

            int maxSurface = 0;
            for (int i = 0; i < heights.Length; i++)
                maxSurface = math.max(maxSurface, heights[i]);

            int cellsY = math.clamp(maxSurface / cell + 2, 4, VoxelConstants.WorldHeight / cell);

            int gx = cellsX + pad * 2;
            int gy = cellsY + pad * 2;
            int gz = cellsZ + pad * 2;

            var current = new byte[gx * gy * gz];
            var next = new byte[current.Length];

            int cellOriginX = chunkX * cellsX;
            int cellOriginZ = chunkZ * cellsZ;

            // Заполнение: вне пещерных зон — сплошной камень,
            // внутри зоны — случайный шум, который сгладит автомат.
            Parallel.For(0, gy, gyIndex =>
            {
                int cellY = gyIndex - pad;
                for (int gzIndex = 0; gzIndex < gz; gzIndex++)
                {
                    int cellZ = cellOriginZ + gzIndex - pad;
                    int row = (gyIndex * gz + gzIndex) * gx;

                    for (int gxIndex = 0; gxIndex < gx; gxIndex++)
                    {
                        int cellX = cellOriginX + gxIndex - pad;

                        bool zone = ValueNoise(
                            cellX * RegionScale,
                            cellY * RegionScale,
                            cellZ * RegionScale,
                            Seed + 7919u) > RegionThreshold;

                        byte value = 1;

                        if (zone && Hash01(cellX, cellY, cellZ, Seed) >= Fill)
                            value = 0;

                        current[row + gxIndex] = value;
                    }
                }
            });

            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                System.Array.Copy(current, next, current.Length);

                Parallel.For(1, gy - 1, gyIndex =>
                {
                    for (int gzIndex = 1; gzIndex < gz - 1; gzIndex++)
                    {
                        int row = (gyIndex * gz + gzIndex) * gx;

                        for (int gxIndex = 1; gxIndex < gx - 1; gxIndex++)
                        {
                            int index = row + gxIndex;
                            int solid = -current[index];

                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int baseY = ((gyIndex + dy) * gz + gzIndex) * gx;

                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    int baseZ = baseY + dz * gx;

                                    for (int dx = -1; dx <= 1; dx++)
                                        solid += current[baseZ + gxIndex + dx];
                                }
                            }

                            byte value = current[index];
                            if (solid >= SolidThreshold)
                                value = 1;
                            else if (solid <= AirThreshold)
                                value = 0;

                            next[index] = value;
                        }
                    }
                });

                byte[] swap = current;
                current = next;
                next = swap;
            }

            RemoveFloatingIslands(current, gx, gy, gz, pad, 1);

            int chunkCount = voxels.Length;
            int area = VoxelConstants.ChunkSize * VoxelConstants.ChunkSize;

            Parallel.For(0, chunkCount * area, index =>
            {
                int chunkY = index / area;
                int rest = index - chunkY * area;
                int x = rest >> VoxelConstants.ChunkShift;
                int z = rest & VoxelConstants.ChunkMask;

                int surface = heights[z * VoxelConstants.ChunkSize + x];
                ushort[] target = voxels[chunkY];
                int baseY = chunkY * VoxelConstants.ChunkSize;

                for (int localY = 0; localY < VoxelConstants.ChunkSize; localY++)
                {
                    int y = baseY + localY;

                    if (y < 2 || y > surface - SurfaceMargin)
                        continue;

                    int voxelIndex = (x * VoxelConstants.ChunkSize + localY) * VoxelConstants.ChunkSize + z;
                    if (target[voxelIndex] == BlockType.Air)
                        continue;

                    // 1 = твёрдо, 0 = пещера: вырезаем там, где доля твёрдого меньше половины
                    if (Sample(current, gx, gy, gz, pad, cell, x, y, z) < 0.5f)
                        target[voxelIndex] = BlockType.Air;
                }
            });
        }

        static float Sample(byte[] grid, int gx, int gy, int gz, int pad, int cell, int x, int y, int z)
        {
            float fx = (float)x / cell + pad;
            float fy = (float)y / cell + pad;
            float fz = (float)z / cell + pad;

            int x0 = math.clamp((int)fx, 0, gx - 2);
            int y0 = math.clamp((int)fy, 0, gy - 2);
            int z0 = math.clamp((int)fz, 0, gz - 2);

            float tx = fx - x0;
            float ty = fy - y0;
            float tz = fz - z0;

            int x1 = x0 + 1;
            int y1 = y0 + 1;
            int z1 = z0 + 1;

            float c000 = grid[(y0 * gz + z0) * gx + x0];
            float c100 = grid[(y0 * gz + z0) * gx + x1];
            float c010 = grid[(y1 * gz + z0) * gx + x0];
            float c110 = grid[(y1 * gz + z0) * gx + x1];
            float c001 = grid[(y0 * gz + z1) * gx + x0];
            float c101 = grid[(y0 * gz + z1) * gx + x1];
            float c011 = grid[(y1 * gz + z1) * gx + x0];
            float c111 = grid[(y1 * gz + z1) * gx + x1];

            float c00 = math.lerp(c000, c100, tx);
            float c10 = math.lerp(c010, c110, tx);
            float c01 = math.lerp(c001, c101, tx);
            float c11 = math.lerp(c011, c111, tx);

            float c0 = math.lerp(c00, c10, ty);
            float c1 = math.lerp(c01, c11, ty);

            return math.lerp(c0, c1, tz);
        }

        /// <summary>Гладкий 3D value-noise на хеше — маска пещерных систем.</summary>
        static float ValueNoise(float x, float y, float z, uint seed)
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

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static float Lattice(int x, int y, int z, uint seed) => Hash01(x, y, z, seed) * 2f - 1f;

        static uint Hash(int x, int y, int z, uint seed)
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

        static float Hash01(int x, int y, int z, uint seed)
            => (Hash(x, y, z, seed) & 0xFFFFFFu) / 16777215f;

        /// <summary>
        /// Превращает в пещеру куски породы, не связанные с основным массивом.
        /// solidValue — какое значение в сетке означает «твёрдо».
        /// Куски, касающиеся полей сетки, не трогаем: соседняя колонка может
        /// посчитать их иначе, и тогда на стыке появится стена.
        /// Крупные куски тоже пропускаем — это уже не «мусор», а часть рельефа.
        /// </summary>
        static void RemoveFloatingIslands(byte[] grid, int gx, int gy, int gz, int pad, byte solidValue)
        {
            const int maxIslandCells = 128;

            var visited = new bool[grid.Length];
            var queue = new int[grid.Length];

            int head = 0;
            int tail = 0;

            // 1. Всё, что связано с границей сетки, — основной массив породы.
            for (int y = 0; y < gy; y++)
            {
                for (int z = 0; z < gz; z++)
                {
                    TryPush(0, y, z);
                    TryPush(gx - 1, y, z);
                }
            }

            for (int x = 0; x < gx; x++)
            {
                for (int z = 0; z < gz; z++)
                {
                    TryPush(x, 0, z);
                    TryPush(x, gy - 1, z);
                }
            }

            for (int x = 0; x < gx; x++)
            {
                for (int y = 0; y < gy; y++)
                {
                    TryPush(x, y, 0);
                    TryPush(x, y, gz - 1);
                }
            }

            if (tail == 0)
                return;

            while (head < tail)
            {
                int index = queue[head++];
                int x = index % gx;
                int rest = index / gx;
                int z = rest % gz;
                int y = rest / gz;

                TryPush(x - 1, y, z);
                TryPush(x + 1, y, z);
                TryPush(x, y - 1, z);
                TryPush(x, y + 1, z);
                TryPush(x, y, z - 1);
                TryPush(x, y, z + 1);
            }

            byte caveValue = solidValue == 1 ? (byte)0 : (byte)1;

            // 2. Оставшиеся куски — кандидаты. Удаляем только те, что целиком
            //    лежат внутри колонки: на полях обе колонки должны принять
            //    одинаковое решение, иначе на стыке образуется стена.
            for (int start = 0; start < grid.Length; start++)
            {
                if (grid[start] != solidValue || visited[start])
                    continue;

                int componentHead = 0;
                int componentTail = 0;
                bool touchesPadding = false;

                visited[start] = true;
                queue[componentTail++] = start;

                while (componentHead < componentTail)
                {
                    int index = queue[componentHead++];
                    int x = index % gx;
                    int rest = index / gx;
                    int z = rest % gz;
                    int y = rest / gz;

                    if (x < pad || x >= gx - pad ||
                        y < pad || y >= gy - pad ||
                        z < pad || z >= gz - pad)
                        touchesPadding = true;

                    PushComponent(x - 1, y, z);
                    PushComponent(x + 1, y, z);
                    PushComponent(x, y - 1, z);
                    PushComponent(x, y + 1, z);
                    PushComponent(x, y, z - 1);
                    PushComponent(x, y, z + 1);
                }

                if (touchesPadding || componentTail > maxIslandCells)
                    continue;

                for (int i = 0; i < componentTail; i++)
                    grid[queue[i]] = caveValue;

                void PushComponent(int x, int y, int z)
                {
                    if ((uint)x >= (uint)gx || (uint)y >= (uint)gy || (uint)z >= (uint)gz)
                        return;

                    int index = (y * gz + z) * gx + x;
                    if (grid[index] != solidValue || visited[index])
                        return;

                    visited[index] = true;
                    queue[componentTail++] = index;
                }
            }

            void TryPush(int x, int y, int z)
            {
                if ((uint)x >= (uint)gx || (uint)y >= (uint)gy || (uint)z >= (uint)gz)
                    return;

                int index = (y * gz + z) * gx + x;
                if (grid[index] != solidValue || visited[index])
                    return;

                visited[index] = true;
                queue[tail++] = index;
            }
        }
    }
}