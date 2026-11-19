using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Voxels
{
    /// <summary>
    /// Экран выбора мира. Все элементы — объекты сцены, ссылки назначаются
    /// в инспекторе (или инструментом Tools → Voxels → Create World Menu UI).
    /// </summary>
    public sealed class WorldMenu : MonoBehaviour
    {
        [SerializeField] GameObject _root;
        [SerializeField] RectTransform _listContent;
        [SerializeField] GameObject _rowPrefab;
        [SerializeField] InputField _nameInput;
        [SerializeField] InputField _seedInput;
        [SerializeField] Button _continueButton;
        [SerializeField] Text _status;

        VoxelEngine _engine;

        void Awake()
        {
            _engine = FindAnyObjectByType<VoxelEngine>();

            if (_root != null)
                _root.SetActive(false);
        }

        public void Show()
        {
            if (_engine == null)
                _engine = FindAnyObjectByType<VoxelEngine>();

            if (_root != null)
                _root.SetActive(true);

            Refresh();
        }

        public void Hide()
        {
            if (_root != null)
                _root.SetActive(false);
        }

        public void Refresh()
        {
            if (_listContent == null)
                return;

            for (int i = _listContent.childCount - 1; i >= 0; i--)
            {
                Transform child = _listContent.GetChild(i);

                if (child.gameObject == _rowPrefab)
                    continue;

                Destroy(child.gameObject);
            }

            List<WorldInfo> worlds = WorldStore.ListWorlds();

            foreach (WorldInfo info in worlds)
            {
                if (_rowPrefab == null)
                    break;

                GameObject row = Instantiate(_rowPrefab, _listContent);
                row.SetActive(true);

                WorldEntryUI entry = row.GetComponent<WorldEntryUI>();
                if (entry != null)
                    entry.Bind(info, this);
            }

            bool hasLast = !string.IsNullOrEmpty(WorldStore.LastWorld) && WorldStore.Exists(WorldStore.LastWorld);

            if (_continueButton != null)
                _continueButton.interactable = hasLast;

            if (_status != null)
                _status.text = hasLast ? $"Последний мир: {WorldStore.LastWorld}" : "Сохранённых миров пока нет.";
        }

        public void LoadWorld(string name)
        {
            if (_engine != null)
                _engine.LoadWorld(name);
        }

        public void CreateWorld()
        {
            string name = _nameInput != null && !string.IsNullOrWhiteSpace(_nameInput.text)
                ? _nameInput.text
                : "Новый мир";

            name = WorldStore.UniqueName(name);

            int seed = 0;
            if (_seedInput != null)
                int.TryParse(_seedInput.text, out seed);

            if (seed == 0)
                seed = new System.Random().Next(1, int.MaxValue);

            if (_engine != null)
                _engine.StartNewWorld(name, seed);
        }

        public void ContinueLast()
        {
            string last = WorldStore.LastWorld;

            if (!string.IsNullOrEmpty(last) && WorldStore.Exists(last))
                LoadWorld(last);
        }
    }
}
