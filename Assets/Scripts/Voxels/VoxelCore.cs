using UnityEngine;
using UnityEngine.Rendering;

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

        /// <summary>Вода.</summary>
        public const ushort Water = 8;

        public const ushort Wood = 9;
        public const ushort Leaves = 10;
        public const ushort Cactus = 11;

        public const int Count = 12;

        public static bool IsSolid(ushort block) => block != Air;

        public static string GetName(ushort block)
        {
            BlockDefinition definition = BlockDatabase.Current?.Get(block);
            if (definition != null)
                return definition.DisplayName;

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
                case Water: return "Water";
                case Wood: return "Wood";
                case Leaves: return "Leaves";
                case Cactus: return "Cactus";
                default: return "Unknown";
            }
        }

        public static Color GetColor(ushort block)
        {
            BlockDefinition definition = BlockDatabase.Current?.Get(block);
            if (definition != null)
                return definition.Color;

            switch (block)
            {
                case Stone: return new Color(0.42f, 0.42f, 0.45f);
                case Dirt: return new Color(0.36f, 0.26f, 0.16f);
                case Grass: return new Color(0.24f, 0.48f, 0.16f);
                case Sand: return new Color(0.78f, 0.72f, 0.52f);
                case Snow: return new Color(0.92f, 0.94f, 0.98f);
                case Debug: return new Color(1f, 0f, 1f);
                case ForestGrass: return new Color(0.16f, 0.34f, 0.12f);
                case Water: return new Color(0.10f, 0.30f, 0.55f);
                case Wood: return new Color(0.34f, 0.23f, 0.13f);
                case Leaves: return new Color(0.18f, 0.44f, 0.16f);
                case Cactus: return new Color(0.20f, 0.48f, 0.24f);
                default: return Color.magenta;
            }
        }

        public static float GetSmoothness(ushort block)
        {
            BlockDefinition definition = BlockDatabase.Current?.Get(block);
            if (definition != null)
                return definition.Smoothness;

            switch (block)
            {
                case Stone: return 0.18f;
                case Snow: return 0.35f;
                case Sand: return 0.03f;
                case Dirt: return 0.04f;
                case Grass: return 0.08f;
                case ForestGrass: return 0.08f;
                case Water: return 0.25f;
                case Wood: return 0.05f;
                case Leaves: return 0.03f;
                case Cactus: return 0.15f;
                default: return 0.1f;
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

        /// <summary>Альфа воды; задаётся из инспектора VoxelEngine.</summary>
        public static float WaterAlpha = 0.35f;

        /// <summary>Гладкость воды: низкая — меньше зеркального блеска неба сверху.</summary>
        public static float WaterSmoothness = 0.25f;

        /// <summary>Готовый ассет материала воды (HDRP/Lit Transparent). Если задан — используется он.</summary>
        public static Material WaterOverride;

        public static void ApplyWaterMaterial(Material material)
        {
            WaterOverride = material;

            if (_cache == null || _cache.Length <= BlockType.Water)
                return;

            if (material != null)
                _cache[BlockType.Water] = material;
        }

        /// <summary>Текущий шейдер поддерживает вершинные цвета (AO).</summary>
        public static bool VertexColorSupported => _vertexColor;

        /// <summary>Обновляет альфу уже созданного материала воды (без пересоздания мира).</summary>
        public static void ApplyWaterAlpha(float alpha)
        {
            WaterAlpha = Mathf.Clamp01(alpha);

            if (_cache == null || _cache.Length <= BlockType.Water)
                return;

            Material water = WaterOverride;

            if (water == null)
            {
                if (_cache == null || _cache.Length <= BlockType.Water)
                    return;

                water = _cache[BlockType.Water];
            }

            if (water == null || !water.HasProperty("_BaseColor"))
                return;

            Color color = water.GetColor("_BaseColor");
            color.a = WaterAlpha;
            water.SetColor("_BaseColor", color);

            if (water.HasProperty("_Smoothness"))
                water.SetFloat("_Smoothness", WaterSmoothness);
        }

        public static Material[] Get()
        {
            if (_cache != null)
                return _cache;

            Shader voxelShader = Shader.Find("Voxel/Block");
            if (voxelShader == null)
                voxelShader = Shader.Find("Shader Graphs/VoxelBlock");

            Shader litShader = Shader.Find("HDRP/Lit");
            if (litShader == null)
                litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null)
                litShader = Shader.Find("Standard");

            Shader fallback = voxelShader != null ? voxelShader : litShader;
            if (fallback == null)
            {
                Debug.LogError("[Voxels] Не найден шейдер для материалов блоков.");
                return null;
            }

            _vertexColor = voxelShader != null;

            _cache = new Material[BlockType.Count];

            for (int i = 0; i < _cache.Length; i++)
            {
                var block = (ushort)i;

                if (block == BlockType.Water && WaterOverride != null)
                {
                    _cache[i] = WaterOverride;
                    continue;
                }

                // Кастомный Shader Graph блока собран как opaque, поэтому
                // прозрачность воды на нём не включить. Для воды берём HDRP/Lit.
                Shader shader = block == BlockType.Water && litShader != null ? litShader : fallback;

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

                BlockDefinition definition = BlockDatabase.Current?.Get(block);
                bool isWater = definition != null ? definition.IsWater : block == BlockType.Water;
                bool transparent = definition != null ? definition.IsTransparent : isWater;

                // Прозрачный материал HDRP: alpha-blend, двусторонний, без сохранения спекуляра.
                if (transparent)
                {
                    material.SetFloat("_SurfaceType", 1f);
                    material.SetFloat("_BlendMode", 0f);
                    material.SetFloat("_EnableFogOnTransparent", 0f);
                    material.SetFloat("_TransparentSortPriority", 0f);
                    material.SetInt("_TransparentCullMode", 0);

                    if (material.HasProperty("_AlphaCutoffEnable"))
                        material.SetFloat("_AlphaCutoffEnable", 0f);
                    if (material.HasProperty("_TransparentZWrite"))
                        material.SetFloat("_TransparentZWrite", 0f);
                    if (material.HasProperty("_ZTestTransparent"))
                        material.SetInt("_ZTestTransparent", 4);

                    // Иначе HDRP оставляет спекуляр при opacity = 0:
                    // чёрная поверхность с бликом вместо прозрачности.
                    if (material.HasProperty("_EnableBlendModePreserveSpecularLighting"))
                        material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0f);

                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.EnableKeyword("_BLENDMODE_ALPHA");
                    material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");

                    material.renderQueue = (int)RenderQueue.Transparent;

                    Color tint = isWater
                        ? new Color(0.08f, 0.26f, 0.48f, WaterAlpha)
                        : new Color(BlockType.GetColor(block).r, BlockType.GetColor(block).g, BlockType.GetColor(block).b, WaterAlpha);

                    material.SetColor("_BaseColor", tint);
                }

                _cache[i] = material;
            }

            return _cache;
        }

        static float Smoothness(ushort block)
            => BlockType.GetSmoothness(block);
    }
}