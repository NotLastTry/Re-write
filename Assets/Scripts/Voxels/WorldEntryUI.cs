using UnityEngine;
using UnityEngine.UI;

namespace Voxels
{
    /// <summary>Строка списка миров: имя, кнопки загрузки и удаления.</summary>
    public sealed class WorldEntryUI : MonoBehaviour
    {
        [SerializeField] Text _label;
        [SerializeField] Button _loadButton;
        [SerializeField] Button _deleteButton;

        WorldMenu _menu;
        string _worldName;

        public void Bind(WorldInfo info, WorldMenu menu)
        {
            _menu = menu;
            _worldName = info.Name;

            if (_label != null)
            {
                string date = info.LastPlayed.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                _label.text = $"{info.Name}   —   сид {info.Seed}, правок {info.EditCount}, {date}";
            }

            if (_loadButton != null)
            {
                _loadButton.onClick.RemoveAllListeners();
                _loadButton.onClick.AddListener(Load);
            }

            if (_deleteButton != null)
            {
                _deleteButton.onClick.RemoveAllListeners();
                _deleteButton.onClick.AddListener(Delete);
            }
        }

        void Load()
        {
            if (_menu != null)
                _menu.LoadWorld(_worldName);
        }

        void Delete()
        {
            WorldStore.Delete(_worldName);

            if (_menu != null)
                _menu.Refresh();
        }
    }
}
