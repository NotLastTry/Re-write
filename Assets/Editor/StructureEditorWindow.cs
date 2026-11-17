using UnityEditor;
using UnityEngine;
using Voxels;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Оконный редактор структур: слой за слоем рисуем блоки палитрой,
    /// сохраняем в Assets/Resources/Structures как StructureDefinition.
    /// </summary>
    public sealed class StructureEditorWindow : EditorWindow
    {
        const int Cell = 22;
        const string Folder = "Assets/Resources/Structures";

        StructureDefinition _definition;
        Vector3Int _size = new Vector3Int(8, 6, 8);
        int _layer;
        ushort _brush = BlockType.Stone;
        Vector2 _scroll;

        Vector3 _previewPosition = new Vector3(0f, 80f, 0f);
        bool _showPreview = true;

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
        }

        void OnSceneGui(SceneView sceneView)
        {
            if (_definition == null || !_showPreview)
                return;

            _definition.EnsureSize();

            Vector3 origin = _previewPosition - (Vector3)_definition.Anchor;
            Handles.matrix = Matrix4x4.Translate(origin);

            for (int y = 0; y < _definition.Size.y; y++)
            {
                for (int z = 0; z < _definition.Size.z; z++)
                {
                    for (int x = 0; x < _definition.Size.x; x++)
                    {
                        ushort block = _definition.Get(x, y, z);

                        if (block == BlockType.Air)
                            continue;

                        Color color = BlockType.GetColor(block);
                        color.a = 0.85f;
                        Handles.color = color;

                        Handles.DrawWireCube(
                            new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                            new Vector3(0.96f, 0.96f, 0.96f));
                    }
                }
            }

            Handles.color = Color.yellow;
            Handles.DrawWireDisc((Vector3)_definition.Anchor + Vector3.up * 0.05f, Vector3.up, 0.7f);
            Handles.Label((Vector3)_definition.Anchor + Vector3.up * 0.4f, "Anchor");

            Handles.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
            Handles.DrawWireDisc(_previewPosition, Vector3.up, 1.2f);

            Handles.matrix = Matrix4x4.identity;
        }

        [MenuItem("Tools/Voxels/Structure Editor")]
        static void Open()
        {
            GetWindow<StructureEditorWindow>("Structure Editor");
        }

        void OnGUI()
        {
            _definition = (StructureDefinition)EditorGUILayout.ObjectField(
                "Структура", _definition, typeof(StructureDefinition), false);

            if (_definition == null)
            {
                if (GUILayout.Button("Создать новую структуру"))
                    CreateStructure();

                EditorGUILayout.HelpBox("Создай структуру или выбери существующий ассет.", MessageType.Info);
                return;
            }

            _definition.EnsureSize();

            if (_definition.Biomes == null || _definition.Biomes.Length != 6)
                _definition.Biomes = new bool[6];

            _layer = Mathf.Clamp(_layer, 0, _definition.Size.y - 1);

            DrawSizeControls();
            DrawPalette();
            DrawPlacementRules();

            EditorGUILayout.Space();
            _layer = EditorGUILayout.IntSlider("Слой Y", _layer, 0, _definition.Size.y - 1);

            DrawGrid();

            EditorGUILayout.Space();

            _showPreview = EditorGUILayout.Toggle("Предпросмотр в Scene", _showPreview);
            _previewPosition = EditorGUILayout.Vector3Field("Позиция предпросмотра", _previewPosition);

            if (GUI.changed)
                SceneView.RepaintAll();

            EditorGUILayout.Space();

            if (GUILayout.Button("Сохранить"))
            {
                EditorUtility.SetDirty(_definition);
                AssetDatabase.SaveAssets();
            }

            if (GUILayout.Button("Создать ещё одну"))
                CreateStructure();
        }

        void DrawSizeControls()
        {
            EditorGUILayout.BeginHorizontal();
            _size = EditorGUILayout.Vector3IntField("Размер", _size);

            if (GUILayout.Button("Применить", GUILayout.Width(90)))
            {
                Undo.RecordObject(_definition, "Resize Structure");
                _definition.Resize(_size);
                EditorUtility.SetDirty(_definition);
                _layer = Mathf.Clamp(_layer, 0, _definition.Size.y - 1);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Объём: {_definition.Volume} блоков", GUILayout.Width(160));

            if (GUILayout.Button("Очистить слой"))
            {
                Undo.RecordObject(_definition, "Clear Structure Layer");

                for (int z = 0; z < _definition.Size.z; z++)
                for (int x = 0; x < _definition.Size.x; x++)
                    _definition.Set(x, _layer, z, BlockType.Air);

                EditorUtility.SetDirty(_definition);
            }

            EditorGUILayout.EndHorizontal();
        }

        void DrawPalette()
        {
            BlockDatabase database = BlockDatabase.Ensure();
            BlockDefinition[] blocks = database != null ? database.Blocks : null;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Палитра");

            if (blocks == null)
                return;

            const int perRow = 6;
            int inRow = 0;

            EditorGUILayout.BeginHorizontal();

            foreach (BlockDefinition block in blocks)
            {
                if (block == null || block.Id == BlockType.Air)
                    continue;

                Color previous = GUI.backgroundColor;

                if (_brush == block.Id)
                    GUI.backgroundColor = Color.green;
                else
                    GUI.backgroundColor = block.Color;

                if (GUILayout.Button(block.DisplayName, GUILayout.Width(82)))
                    _brush = block.Id;

                GUI.backgroundColor = previous;

                if (++inRow % perRow == 0)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField($"Кисть: {BlockType.GetName(_brush)}");
        }

        void DrawPlacementRules()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Размещение");

            _definition.Rarity = EditorGUILayout.Slider("Редкость", _definition.Rarity, 0f, 1f);
            _definition.MinSurface = EditorGUILayout.IntField("Мин. высота", _definition.MinSurface);
            _definition.MaxSurface = EditorGUILayout.IntField("Макс. высота", _definition.MaxSurface);
            _definition.Rotate = EditorGUILayout.Toggle("Случайный поворот", _definition.Rotate);

            EditorGUILayout.LabelField("Биомы");

            for (int i = 0; i < _definition.Biomes.Length; i++)
            {
                string label = System.Enum.GetName(typeof(Biome), i);
                _definition.Biomes[i] = EditorGUILayout.Toggle(label, _definition.Biomes[i]);
            }

            if (GUI.changed)
                EditorUtility.SetDirty(_definition);
        }

        void DrawGrid()
        {
            EditorGUILayout.LabelField($"Слой {_layer} из {_definition.Size.y - 1}");

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int z = 0; z < _definition.Size.z; z++)
            {
                EditorGUILayout.BeginHorizontal();

                for (int x = 0; x < _definition.Size.x; x++)
                {
                    Rect rect = GUILayoutUtility.GetRect(Cell, Cell, GUILayout.Width(Cell), GUILayout.Height(Cell));
                    ushort block = _definition.Get(x, _layer, z);

                    Color color = block == BlockType.Air
                        ? new Color(0.16f, 0.16f, 0.16f)
                        : BlockType.GetColor(block);

                    if (block != BlockType.Air)
                        color = Color.Lerp(color, Color.black, 0.2f);

                    EditorGUI.DrawRect(rect, color);

                    if (block != BlockType.Air)
                        GUI.Label(rect, block.ToString());

                    Event current = Event.current;

                    if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
                    {
                        Undo.RecordObject(_definition, "Paint Structure");

                        ushort next = current.button == 1 ? BlockType.Air : _brush;
                        _definition.Set(x, _layer, z, next);
                        EditorUtility.SetDirty(_definition);

                        current.Use();
                        Repaint();
                        SceneView.RepaintAll();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.LabelField("ЛКМ — поставить, ПКМ — стереть.");
        }

        void CreateStructure()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                    AssetDatabase.CreateFolder("Assets", "Resources");

                AssetDatabase.CreateFolder("Assets/Resources", "Structures");
            }

            string path = AssetDatabase.GenerateUniqueAssetPath(Folder + "/Structure.asset");
            var structure = ScriptableObject.CreateInstance<StructureDefinition>();
            structure.Size = _size;
            structure.Anchor = new Vector3Int(_size.x / 2, 0, _size.z / 2);
            structure.Biomes = new bool[6];
            structure.EnsureSize();

            AssetDatabase.CreateAsset(structure, path);
            AssetDatabase.SaveAssets();

            _definition = structure;
            _layer = 0;

            Selection.activeObject = structure;
        }
    }
}
