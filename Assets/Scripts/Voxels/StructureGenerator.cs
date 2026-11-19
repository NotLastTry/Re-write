using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// Детерминированные структуры: деревья, кактусы и валуны.
    /// Решение принимается по мировым координатам, поэтому соседние чанки
    /// «дорисовывают» свою часть одного и того же дерева без швов.
    /// </summary>
    public sealed class StructureGenerator
    {
        public bool Enabled = true;
        public int Seed;
        public StructureDefinition[] Definitions;

        const int Margin = 3;

        public void Place(World world, int chunkX, int chunkZ, ushort[][] voxels)
        {
            using var structuresMarker = VoxelProfiler.Structures.Auto();

            if (!Enabled)
                return;

            PlaceStructures(world, chunkX, chunkZ, voxels);

            const int size = VoxelConstants.ChunkSize;

            for (int z = -Margin; z < size + Margin; z++)
            {
                for (int x = -Margin; x < size + Margin; x++)
                {
                    int worldX = chunkX * size + x;
                    int worldZ = chunkZ * size + z;

                    // Высоту можно брать только у уже сгенерированной колонки,
                    // иначе дерево построится по приблизительной высоте и повиснет.
                    int columnChunkX = worldX >> VoxelConstants.ChunkShift;
                    int columnChunkZ = worldZ >> VoxelConstants.ChunkShift;

                    if (!world.HasColumn(columnChunkX, columnChunkZ))
                        continue;

                    float roll = Hash01(worldX, worldZ, Seed);

                    // 0.05 — максимальный шанс дерева/кактуса. Для остальных ~95%
                    // колонок высоту и биом вообще не считаем.
                    if (roll >= 0.05f)
                        continue;

                    int surface = world.GetSurfaceHeight(worldX, worldZ);
                    if (surface <= VoxelConstants.SeaLevel + 1 || surface >= VoxelConstants.WorldHeight - 12)
                        continue;

                    var biome = (Biome)world.GetBiome(worldX, worldZ);

                    switch (biome)
                    {
                        case Biome.Forest:
                            if (roll < 0.045f)
                                PlaceTree(world, worldX, worldZ, surface, 5);
                            break;

                        case Biome.Plains:
                            if (roll < 0.012f)
                                PlaceTree(world, worldX, worldZ, surface, 4);
                            break;

                        case Biome.Tundra:
                            if (roll < 0.006f)
                                PlaceTree(world, worldX, worldZ, surface, 6);
                            break;

                        case Biome.Desert:
                            if (roll < 0.02f)
                                PlaceCactus(world, worldX, worldZ, surface);
                            break;

                        case Biome.Mountains:
                            if (surface < VoxelConstants.SnowLine - 6 && roll < 0.015f)
                                PlaceBoulder(world, worldX, worldZ, surface);
                            break;
                    }
                }
            }
        }

        void PlaceTree(World world, int worldX, int worldZ, int surface, int baseHeight)
        {
            int height = baseHeight + (int)(Hash01(worldX + 17, worldZ - 31, Seed + 7) * 3f);

            for (int i = 1; i <= height; i++)
                Set(world, worldX, surface + i, worldZ, BlockType.Wood);

            int top = surface + height;

            for (int dy = -2; dy <= 2; dy++)
            {
                int radius = dy == 2 ? 1 : 2;

                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Mathf.Abs(dx) == radius && Mathf.Abs(dz) == radius)
                            continue;

                        Set(world, worldX + dx, top + dy, worldZ + dz, BlockType.Leaves);
                    }
                }
            }
        }

        void PlaceCactus(World world, int worldX, int worldZ, int surface)
        {
            int height = 2 + (int)(Hash01(worldX - 11, worldZ + 23, Seed + 13) * 3f);

            for (int i = 1; i <= height; i++)
                Set(world, worldX, surface + i, worldZ, BlockType.Cactus);
        }

        void PlaceBoulder(World world, int worldX, int worldZ, int surface)
        {
            int radius = 1 + (int)(Hash01(worldX + 41, worldZ + 5, Seed + 19) * 2f);

            for (int dy = 0; dy <= radius; dy++)
            {
                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dy * dy + dz * dz > radius * radius + 1)
                            continue;

                        Set(world, worldX + dx, surface + dy, worldZ + dz, BlockType.Stone);
                    }
                }
            }
        }

        static void Set(World world, int worldX, int worldY, int worldZ, ushort block)
        {
            world.PlaceRaw(worldX, worldY, worldZ, block);
        }

        void PlaceStructures(World world, int chunkX, int chunkZ, ushort[][] voxels)
        {
            if (Definitions == null || Definitions.Length == 0)
                return;

            int margin = 1;
            float maxRarity = 0f;

            foreach (StructureDefinition definition in Definitions)
            {
                if (definition == null)
                    continue;

                margin = Mathf.Max(margin, Mathf.Max(definition.Size.x, definition.Size.z) + 1);
                maxRarity = Mathf.Max(maxRarity, definition.Rarity);
            }

            if (maxRarity <= 0f)
                return;

            const int size = VoxelConstants.ChunkSize;

            for (int z = -margin; z < size + margin; z++)
            {
                for (int x = -margin; x < size + margin; x++)
                {
                    int worldX = chunkX * size + x;
                    int worldZ = chunkZ * size + z;

                    float columnRoll = Hash01(worldX, worldZ, Seed + 303);

                    // Ранний выход: для подавляющего большинства колонок
                    // высота и биом не нужны вовсе.
                    if (columnRoll >= maxRarity)
                        continue;

                    int anchorChunkX = worldX >> VoxelConstants.ChunkShift;
                    int anchorChunkZ = worldZ >> VoxelConstants.ChunkShift;

                    if (!world.HasColumn(anchorChunkX, anchorChunkZ))
                        continue;

                    int surface = world.GetSurfaceHeight(worldX, worldZ);
                    if (surface <= VoxelConstants.SeaLevel + 1 || surface >= VoxelConstants.WorldHeight - 4)
                        continue;

                    var biome = (Biome)world.GetBiome(worldX, worldZ);

                    for (int i = 0; i < Definitions.Length; i++)
                    {
                        StructureDefinition definition = Definitions[i];
                        if (definition == null)
                            continue;

                        bool[] biomes = definition.Biomes;
                        if (biomes == null || (int)biome >= biomes.Length || !biomes[(int)biome])
                            continue;

                        if (surface < definition.MinSurface || surface > definition.MaxSurface)
                            continue;

                        if (columnRoll >= definition.Rarity)
                            continue;

                        Stamp(world, definition, worldX, worldZ, surface, i);
                        break;
                    }
                }
            }
        }

        void Stamp(World world, StructureDefinition definition, int worldX, int worldZ, int surface, int index)
        {
            int rotation = definition.Rotate
                ? (int)(Hash01(worldX + 5, worldZ + 11, Seed + 401 + index) * 4f)
                : 0;

            Vector3Int size = definition.Size;
            Vector3Int anchor = definition.Anchor;

            int bottomY = surface + 1 - anchor.y;

            if (definition.NeedsFoundation)
                FillFoundation(world, definition, worldX, worldZ, bottomY, rotation);

            for (int y = 0; y < size.y; y++)
            {
                for (int z = 0; z < size.z; z++)
                {
                    for (int x = 0; x < size.x; x++)
                    {
                        ushort block = definition.Get(x, y, z);

                        if (block == BlockType.Air)
                            continue;

                        int ox = x - anchor.x;
                        int oz = z - anchor.z;
                        int rx = ox;
                        int rz = oz;

                        switch (rotation)
                        {
                            case 1: rx = oz; rz = -ox; break;
                            case 2: rx = -ox; rz = -oz; break;
                            case 3: rx = -oz; rz = ox; break;
                        }

                        Set(world,
                            worldX + rx,
                            surface + 1 + (y - anchor.y),
                            worldZ + rz,
                            block);
                    }
                }
            }
        }

        void FillFoundation(World world, StructureDefinition definition, int worldX, int worldZ,
            int bottomY, int rotation)
        {
            Vector3Int size = definition.Size;
            Vector3Int anchor = definition.Anchor;

            for (int z = 0; z < size.z; z++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    bool hasBlock = false;

                    for (int y = 0; y < size.y; y++)
                    {
                        if (definition.Get(x, y, z) != BlockType.Air)
                        {
                            hasBlock = true;
                            break;
                        }
                    }

                    if (!hasBlock)
                        continue;

                    int ox = x - anchor.x;
                    int oz = z - anchor.z;
                    int rx = ox;
                    int rz = oz;

                    switch (rotation)
                    {
                        case 1: rx = oz; rz = -ox; break;
                        case 2: rx = -ox; rz = -oz; break;
                        case 3: rx = -oz; rz = ox; break;
                    }

                    int wx = worldX + rx;
                    int wz = worldZ + rz;

                    int localSurface = world.GetSurfaceHeight(wx, wz);

                    for (int y = localSurface + 1; y < bottomY; y++)
                        Set(world, wx, y, wz, definition.FoundationBlock);
                }
            }
        }

        static float Hash01(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + z * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / 16777215f;
            }
        }
    }
}
