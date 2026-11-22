using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Voxels
{
    /// <summary>
    /// Подводный эффект: когда камера оказывается в блоке воды, включается
    /// глобальный Volume с плотным синим туманом HDRP. На поверхности он выключен,
    /// поэтому обычный туман сцены не затрагивается.
    /// </summary>
    public sealed class UnderwaterEffect : MonoBehaviour
    {
        [SerializeField] Color _color = new Color(0.05f, 0.22f, 0.35f);
        [SerializeField] float _meanFreePath = 12f;
        [SerializeField] bool _logUnderwater = true;

        VoxelEngine _engine;
        Volume _volume;
        VolumeProfile _profile;
        Fog _fog;
        bool _underwater;
        Color _lastColor;
        float _lastDistance = float.NaN;

        void Start()
        {
            _engine = Object.FindAnyObjectByType<VoxelEngine>();

            if (_engine != null)
            {
                _color = _engine.UnderwaterFogColor;
                _meanFreePath = _engine.UnderwaterFogDistance;
            }

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _fog = _profile.Add<Fog>(true);
            _fog.enabled.Override(true);
            _fog.colorMode.Override(FogColorMode.ConstantColor);
            _fog.color.Override(_color);
            _fog.meanFreePath.Override(_meanFreePath);
            _fog.baseHeight.Override(-1000f);
            _fog.maximumHeight.Override(1000f);
            _fog.maxFogDistance.Override(300f);

            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.weight = 1f;
            _volume.sharedProfile = _profile;
            _volume.enabled = false;
        }

        void Update()
        {
            if (_engine == null || _engine.CurrentWorld == null || _fog == null)
                return;

            // Настройки можно менять в инспекторе VoxelEngine на лету,
            // но переписываем профиль только при реальном изменении:
            // каждый Override помечает Volume грязным и стоит кадрового времени.
            Color color = _engine.UnderwaterFogColor;
            if (color != _lastColor)
            {
                _fog.color.Override(color);
                _lastColor = color;
            }

            float distance = _engine.UnderwaterFogDistance;
            if (!Mathf.Approximately(distance, _lastDistance))
            {
                _fog.meanFreePath.Override(distance);
                _lastDistance = distance;
            }

            Vector3 position = transform.position;

            ushort block = _engine.CurrentWorld.GetBlock(
                Mathf.FloorToInt(position.x),
                Mathf.FloorToInt(position.y),
                Mathf.FloorToInt(position.z));

            bool underwater = block == BlockType.Water;

            if (underwater == _underwater)
                return;

            _underwater = underwater;
            _volume.enabled = underwater;

            if (_logUnderwater)
                Debug.Log($"[Voxels] Под водой: {underwater} (блок {BlockType.GetName(block)})");
        }

        void OnDestroy()
        {
            if (_profile != null)
                Destroy(_profile);
        }
    }
}
