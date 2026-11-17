using UnityEngine;
using UnityEngine.InputSystem;

namespace Voxels
{
    /// <summary>
    /// Копание и установка блоков: луч из камеры в чанк-коллайдер,
    /// ЛКМ — убрать блок, ПКМ — поставить выбранный. 1..6 — выбор блока.
    /// </summary>
    public sealed class BlockInteraction : MonoBehaviour
    {
        [SerializeField] float _reach = 8f;

        static readonly ushort[] DefaultPalette =
        {
            BlockType.Stone,
            BlockType.Dirt,
            BlockType.Grass,
            BlockType.Sand,
            BlockType.Snow,
            BlockType.Water,
            BlockType.Wood,
            BlockType.Leaves,
            BlockType.Cactus
        };

        static readonly Key[] Digits =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9
        };

        VoxelEngine _engine;
        ushort[] _palette = DefaultPalette;
        int _selected;

        void Start()
        {
            _engine = Object.FindAnyObjectByType<VoxelEngine>();
            BlockDatabase.Ensure();
            RebuildPalette();
        }

        void RebuildPalette()
        {
            BlockDatabase database = BlockDatabase.Current;
            if (database == null || database.Blocks == null)
                return;

            var ids = new System.Collections.Generic.List<ushort>();

            foreach (BlockDefinition definition in database.Blocks)
            {
                if (definition != null && definition.IsPlaceable && definition.Id != BlockType.Air)
                    ids.Add(definition.Id);
            }

            if (ids.Count > 0)
                _palette = ids.ToArray();
        }

        void Update()
        {
            if (_engine == null || _engine.CurrentWorld == null)
                return;

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard == null || mouse == null)
                return;

            for (int i = 0; i < Digits.Length && i < _palette.Length; i++)
            {
                if (keyboard[Digits[i]].wasPressedThisFrame)
                    _selected = i;
            }

            if (Cursor.lockState != CursorLockMode.Locked)
                return;

            bool breakBlock = mouse.leftButton.wasPressedThisFrame;
            bool placeBlock = mouse.rightButton.wasPressedThisFrame;

            if (!breakBlock && !placeBlock)
                return;

            if (!Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, _reach))
                return;

            if (breakBlock)
            {
                Vector3 inside = hit.point - hit.normal * 0.01f;
                _engine.CurrentWorld.SetBlock(
                    Mathf.FloorToInt(inside.x),
                    Mathf.FloorToInt(inside.y),
                    Mathf.FloorToInt(inside.z),
                    BlockType.Air);
            }
            else
            {
                if (_palette.Length == 0)
                    return;

                _selected = Mathf.Clamp(_selected, 0, _palette.Length - 1);

                Vector3 outside = hit.point + hit.normal * 0.5f;
                _engine.CurrentWorld.SetBlock(
                    Mathf.FloorToInt(outside.x),
                    Mathf.FloorToInt(outside.y),
                    Mathf.FloorToInt(outside.z),
                    _palette[_selected]);
            }
        }
    }
}
