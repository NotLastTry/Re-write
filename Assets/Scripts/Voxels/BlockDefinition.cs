using UnityEngine;

namespace Voxels
{
    /// <summary>Описание одного блока: цвет, блеск, прозрачность, твёрдость.</summary>
    public sealed class BlockDefinition : ScriptableObject
    {
        public ushort Id;
        public string DisplayName = "Block";
        public Color Color = Color.magenta;
        public float Smoothness = 0.1f;
        public float Metallic;
        public bool IsSolid = true;
        public bool IsTransparent;
        public bool IsWater;
        public bool IsPlaceable = true;
        public float Hardness = 1f;
    }
}
