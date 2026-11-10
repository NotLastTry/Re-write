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

        static readonly ushort[] Palette =
        {
            BlockType.Stone,
            BlockType.Dirt,
            BlockType.Grass,
            BlockType.Sand,
            BlockType.Snow,
            BlockType.Water
        };

        static readonly Key[] Digits =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6
        };

        VoxelEngine _engine;
        int _selected;

        void Start()
        {
            _engine = Object.FindAnyObjectByType<VoxelEngine>();
        }

        void Update()
        {
            if (_engine == null || _engine.CurrentWorld == null)
                return;

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (keyboard == null || mouse == null)
                return;

            for (int i = 0; i < Digits.Length; i++)
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
                Vector3 outside = hit.point + hit.normal * 0.5f;
                _engine.CurrentWorld.SetBlock(
                    Mathf.FloorToInt(outside.x),
                    Mathf.FloorToInt(outside.y),
                    Mathf.FloorToInt(outside.z),
                    Palette[_selected]);
            }
        }
    }
}
