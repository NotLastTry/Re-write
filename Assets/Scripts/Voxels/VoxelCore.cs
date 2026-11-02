using UnityEngine;

namespace Voxels
{
    /// <summary>Размерности мира. Всё, что зависит от размера чанка, берётся отсюда.</summary>
    public static class VoxelConstants
    {
        public const int ChunkSize = 32;          // вокселей по ребру чанка
        public const int ChunkShift = 5;          // log2(ChunkSize)
        public const int ChunkMask = ChunkSize - 1;
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        public const int WorldHeight = 128;
        public const int SeaLevel = 40;
        public const int SnowLine = 96;

        public const int ChunksInHeight = WorldHeight / ChunkSize;

        static VoxelConstants()
        {
            if ((1 << ChunkShift) != ChunkSize)
                Debug.LogError("[Voxels] ChunkShift не соответствует ChunkSize.");
        }
    }

    /// <summary>Идентификаторы блоков: 0 — воздух, остальное — твёрдые блоки.</summary>
    public static class BlockType
    {
        public const ushort Air = 0;
        public const ushort Stone = 1;
        public const ushort Dirt = 2;
        public const ushort Grass = 3;
        public const ushort Sand = 4;
        public const ushort Snow = 5;

        /// <summary>Отладочный блок: подсвечивает крутые склоны.</summary>
        public const ushort Debug = 6;

        /// <summary>Трава лесного биома — темнее обычной.</summary>
        public const ushort ForestGrass = 7;

        public const int Count = 8;

        public static bool IsSolid(ushort block) => block != Air;

        public static string GetName(ushort block)
        {
            switch (block)
            {
                case Air: return "Air";
                case Stone: return "Stone";
                case Dirt: return "Dirt";
                case Grass: return "Grass";
                case Sand: return "Sand";
                case Snow: return "Snow";
                case Debug: return "Debug";
                case ForestGrass: return "ForestGrass";
                default: return "Unknown";
            }
        }

        public static Color GetColor(ushort block)
        {
            switch (block)
            {
                case Stone: return new Color(0.42f, 0.42f, 0.45f);
                case Dirt: return new Color(0.36f, 0.26f, 0.16f);
                case Grass: return new Color(0.24f, 0.48f, 0.16f);
                case Sand: return new Color(0.78f, 0.72f, 0.52f);
                case Snow: return new Color(0.92f, 0.94f, 0.98f);
                case Debug: return new Color(1f, 0f, 1f);
                case ForestGrass: return new Color(0.16f, 0.34f, 0.12f);
                default: return Color.magenta;
            }
        }
    }

    /// <summary>
    /// Материалы блоков. Если в проекте есть Shader Graph с именем VoxelBlock
    /// (шейдер "Shader Graphs/VoxelBlock" или "Voxel/Block"), используется он —
    /// он умеет трипланар и AO из вершинных цветов. Иначе — HDRP/Lit с текстурами.
    /// </summary>
    public static class VoxelMaterials
    {
        static Material[] _cache;
        static bool _vertexColor;

        /// <summary>Текущий шейдер поддерживает вершинные цвета (AO).</summary>
        public static bool VertexColorSupported => _vertexColor;

        public static Material[] Get()
        {
            if (_cache != null)
                return _cache;

            Shader shader = Shader.Find("Voxel/Block");
            if (shader == null)
                shader = Shader.Find("Shader Graphs/VoxelBlock");

            _vertexColor = shader != null;

            if (shader == null)
                shader = Shader.Find("HDRP/Lit");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogError("[Voxels] Не найден шейдер для материалов блоков.");
                return null;
            }

            _cache = new Material[BlockType.Count];

            for (int i = 0; i < _cache.Length; i++)
            {
                var block = (ushort)i;
                var material = new Material(shader)
                {
                    name = "Voxel " + BlockType.GetName(block),
                    hideFlags = HideFlags.DontSave
                };

                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", Color.white);

                Texture2D texture = VoxelTextureFactory.Get(block);
                if (texture != null && material.HasProperty("_BaseColorMap"))
                    material.SetTexture("_BaseColorMap", texture);

                if (material.HasProperty("_Smoothness"))
                    material.SetFloat("_Smoothness", Smoothness(block));
                if (material.HasProperty("_Metallic"))
                    material.SetFloat("_Metallic", 0f);

                _cache[i] = material;
            }

            return _cache;
        }

        static float Smoothness(ushort block)
        {
            switch (block)
            {
                case BlockType.Stone: return 0.18f;
                case BlockType.Snow: return 0.35f;
                case BlockType.Sand: return 0.03f;
                case BlockType.Dirt: return 0.04f;
                case BlockType.Grass: return 0.08f;
                case BlockType.ForestGrass: return 0.08f;
                default: return 0.1f;
            }
        }
    }
}