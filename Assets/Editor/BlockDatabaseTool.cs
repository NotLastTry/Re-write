using UnityEditor;
using UnityEngine;
using Voxels;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Создаёт по одному BlockDefinition на блок в Assets/Resources/Blocks.
    /// База при запуске сама подхватывает эти ассеты через Resources.LoadAll.
    /// </summary>
    public static class BlockDatabaseTool
    {
        const string Folder = "Assets/Resources/Blocks";

        [MenuItem("Tools/Voxels/Create Block Definition Assets")]
        static void CreateDefinitions()
        {
            EnsureFolder("Assets", "Resources");
            EnsureFolder("Assets/Resources", "Blocks");

            int created = 0;

            for (int i = 0; i < BlockType.Count; i++)
            {
                var id = (ushort)i;
                string displayName = BlockType.GetName(id);
                string path = $"{Folder}/Block_{i:00}_{displayName}.asset";

                if (AssetDatabase.LoadAssetAtPath<BlockDefinition>(path) != null)
                    continue;

                BlockDefinition definition = BlockDatabase.CreateDefaultDefinition(id);
                definition.hideFlags = HideFlags.None;

                AssetDatabase.CreateAsset(definition, path);
                created++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Voxels] Описаний блоков создано: {created}. Папка: {Folder}");
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + child))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
