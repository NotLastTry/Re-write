using UnityEngine;

namespace Voxels
{
    /// <summary>
    /// Структура как компактная 3D-сетка блоков. В мире она запекается в воксели
    /// чанка, поэтому ломается и стримится как обычный мир.
    /// </summary>
    [CreateAssetMenu(menuName = "Voxels/Structure", fileName = "Structure")]
    public sealed class StructureDefinition : ScriptableObject
    {
        public Vector3Int Size = new Vector3Int(8, 6, 8);

        /// <summary>Блок структуры, который ставится на surface + 1.</summary>
        public Vector3Int Anchor = new Vector3Int(4, 0, 4);

        public ushort[] Blocks = new ushort[8 * 6 * 8];

        [Header("Правила размещения")]
        [Range(0f, 1f)] public float Rarity = 0.002f;

        /// <summary>Plains, Forest, Desert, Tundra, Beach, Mountains.</summary>
        public bool[] Biomes = new bool[6];

        public int MinSurface;
        public int MaxSurface = 120;
        public bool Rotate = true;

        [Header("Подсыпка")]
        public bool NeedsFoundation;
        public ushort FoundationBlock = BlockType.Stone;

        public int Volume => Size.x * Size.y * Size.z;

        public ushort Get(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= Size.x || y >= Size.y || z >= Size.z)
                return BlockType.Air;

            int index = (y * Size.z + z) * Size.x + x;

            return index >= 0 && index < Blocks.Length ? Blocks[index] : BlockType.Air;
        }

        public void Set(int x, int y, int z, ushort block)
        {
            if (x < 0 || y < 0 || z < 0 || x >= Size.x || y >= Size.y || z >= Size.z)
                return;

            EnsureSize();

            int index = (y * Size.z + z) * Size.x + x;
            Blocks[index] = block;
        }

        public void Resize(Vector3Int size)
        {
            Size = new Vector3Int(
                Mathf.Max(1, size.x),
                Mathf.Max(1, size.y),
                Mathf.Max(1, size.z));

            Anchor = new Vector3Int(
                Mathf.Clamp(Anchor.x, 0, Size.x - 1),
                Mathf.Clamp(Anchor.y, 0, Size.y - 1),
                Mathf.Clamp(Anchor.z, 0, Size.z - 1));

            Blocks = new ushort[Volume];
        }

        public void EnsureSize()
        {
            if (Blocks == null || Blocks.Length != Volume)
                Blocks = new ushort[Volume];
        }

        void OnValidate()
        {
            Size = new Vector3Int(Mathf.Max(1, Size.x), Mathf.Max(1, Size.y), Mathf.Max(1, Size.z));

            if (Biomes == null || Biomes.Length != 6)
                Biomes = new bool[6];

            EnsureSize();
        }
    }
}
