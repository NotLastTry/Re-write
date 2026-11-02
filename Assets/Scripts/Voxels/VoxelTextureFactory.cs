using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// Процедурные текстуры блоков: базовый цвет + две октавы шума + редкие тёмные крапинки.
    /// Генерируются один раз и живут до конца игры; размер 64x64 достаточно для блочного мира.
    /// </summary>
    public static class VoxelTextureFactory
    {
        const int Size = 64;

        static readonly Texture2D[] Textures = new Texture2D[BlockType.Count];

        public static Texture2D Get(ushort block)
        {
            if (block >= Textures.Length)
                return null;

            if (Textures[block] != null)
                return Textures[block];

            Texture2D texture = Create(block);
            Textures[block] = texture;
            return texture;
        }

        static Texture2D Create(ushort block)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, false)
            {
                name = "Voxel " + BlockType.GetName(block),
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4
            };

            Color baseColor = BlockType.GetColor(block);
            float seed = block * 37.17f + 11.3f;

            var pixels = new Color32[Size * Size];
            var random = new System.Random(block * 7919 + 13);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float coarse = Mathf.PerlinNoise((x + seed) * 0.06f, (y + seed) * 0.06f);
                    float fine = Mathf.PerlinNoise((x + seed) * 0.25f, (y + seed) * 0.25f);

                    float shade = 0.78f + coarse * 0.28f + fine * 0.12f;

                    // редкие тёмные вкрапления, чтобы поверхность не была однотонной
                    if (random.NextDouble() < 0.02)
                        shade *= 0.75f;

                    Color color = baseColor * shade;
                    color.a = 1f;
                    pixels[y * Size + x] = color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }
    }
}
