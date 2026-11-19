using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Voxels;

namespace Voxels.EditorTools
{
    /// <summary>
    /// Один раз собирает экран выбора мира в текущей сцене:
    /// Canvas, список миров, поля нового мира и префаб строки.
    /// Дальше всё это — обычные объекты сцены, которые можно править руками.
    /// </summary>
    public static class WorldMenuSetupTool
    {
        [MenuItem("Tools/Voxels/Create World Menu UI")]
        static void CreateMenu()
        {
            if (Object.FindAnyObjectByType<WorldMenu>() != null)
            {
                Debug.LogWarning("[Voxels] WorldMenu уже есть в сцене.");
                return;
            }

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            EnsureEventSystem();

            var canvasObject = new GameObject("World Menu Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(WorldMenu));

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            var root = new GameObject("Root", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(canvasObject.transform, false);
            Stretch(root.GetComponent<RectTransform>());
            root.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 0.97f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(root.transform, false);

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(940f, 760f);
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.14f, 0.98f);

            var layout = panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 24, 24, 24);
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;

            CreateText(panel.transform, "Выбор мира", font, 34, TextAnchor.MiddleLeft, 56f);
            CreateText(panel.transform, "Новый мир", font, 20, TextAnchor.MiddleLeft, 30f);

            var createRow = new GameObject("New World Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            createRow.transform.SetParent(panel.transform, false);
            createRow.GetComponent<LayoutElement>().preferredHeight = 46f;

            var createLayout = createRow.GetComponent<HorizontalLayoutGroup>();
            createLayout.spacing = 8f;
            createLayout.childControlWidth = true;
            createLayout.childControlHeight = true;
            createLayout.childForceExpandWidth = false;

            InputField nameInput = CreateInput(createRow.transform, "Имя мира", "Новый мир", font);
            nameInput.GetComponent<LayoutElement>().flexibleWidth = 1f;

            InputField seedInput = CreateInput(createRow.transform, "Сид (0 = случайный)", "0", font);
            seedInput.GetComponent<LayoutElement>().preferredWidth = 190f;
            seedInput.GetComponent<LayoutElement>().flexibleWidth = 0f;

            Button createButton = CreateButton(createRow.transform, "Создать", font, 150f);

            Button continueButton = CreateButton(panel.transform, "Продолжить последний мир", font, 0f);
            continueButton.GetComponent<LayoutElement>().preferredHeight = 46f;

            CreateText(panel.transform, "Сохранённые миры", font, 20, TextAnchor.MiddleLeft, 30f);

            var scrollObject = new GameObject("Worlds", typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            scrollObject.transform.SetParent(panel.transform, false);
            scrollObject.GetComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 0.9f);
            scrollObject.GetComponent<LayoutElement>().preferredHeight = 380f;

            var scroll = scrollObject.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            viewport.transform.SetParent(scrollObject.transform, false);
            Stretch(viewport.GetComponent<RectTransform>());
            viewport.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);
            viewport.GetComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);

            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = new Vector2(6f, 0f);
            contentRect.offsetMax = new Vector2(-6f, 0f);

            var contentLayout = content.GetComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(4, 4, 4, 4);
            contentLayout.spacing = 4f;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = contentRect;

            GameObject rowPrefab = CreateRowTemplate(content.transform, font);
            rowPrefab.SetActive(false);

            Text status = CreateText(panel.transform, "Сохранённых миров пока нет.", font, 16, TextAnchor.MiddleLeft, 28f);
            status.color = new Color(0.85f, 0.75f, 0.45f);

            var menu = canvasObject.GetComponent<WorldMenu>();

            var serialized = new SerializedObject(menu);
            serialized.FindProperty("_root").objectReferenceValue = root;
            serialized.FindProperty("_listContent").objectReferenceValue = contentRect;
            serialized.FindProperty("_rowPrefab").objectReferenceValue = rowPrefab;
            serialized.FindProperty("_nameInput").objectReferenceValue = nameInput;
            serialized.FindProperty("_seedInput").objectReferenceValue = seedInput;
            serialized.FindProperty("_continueButton").objectReferenceValue = continueButton;
            serialized.FindProperty("_status").objectReferenceValue = status;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            UnityEventTools.AddPersistentListener(createButton.onClick, menu.CreateWorld);
            UnityEventTools.AddPersistentListener(continueButton.onClick, menu.ContinueLast);

            Undo.RegisterCreatedObjectUndo(canvasObject, "Create World Menu UI");
            Selection.activeObject = canvasObject;

            Debug.Log("[Voxels] Экран выбора мира собран. Сохрани сцену (Ctrl+S).");
        }

        static GameObject CreateRowTemplate(Transform parent, Font font)
        {
            var row = new GameObject("World Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement), typeof(WorldEntryUI));
            row.transform.SetParent(parent, false);
            row.GetComponent<LayoutElement>().preferredHeight = 38f;

            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;

            Text label = CreateText(row.transform, "Мир", font, 16, TextAnchor.MiddleLeft, 30f);
            label.GetComponent<LayoutElement>().flexibleWidth = 1f;

            Button loadButton = CreateButton(row.transform, "Загрузить", font, 110f);
            Button deleteButton = CreateButton(row.transform, "Удалить", font, 100f);

            var entry = row.GetComponent<WorldEntryUI>();
            var serialized = new SerializedObject(entry);
            serialized.FindProperty("_label").objectReferenceValue = label;
            serialized.FindProperty("_loadButton").objectReferenceValue = loadButton;
            serialized.FindProperty("_deleteButton").objectReferenceValue = deleteButton;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return row;
        }

        static void EnsureEventSystem()
        {
            EventSystem existing = Object.FindAnyObjectByType<EventSystem>();

            if (existing == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                return;
            }

            if (existing.GetComponent<InputSystemUIInputModule>() == null)
                existing.gameObject.AddComponent<InputSystemUIInputModule>();
        }

        static Text CreateText(Transform parent, string value, Font font, int size, TextAnchor anchor, float height)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.text = value;
            text.font = font;
            text.fontSize = size;
            text.color = Color.white;
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            go.GetComponent<LayoutElement>().preferredHeight = height;
            return text;
        }

        static Button CreateButton(Transform parent, string label, Font font, float width)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            go.GetComponent<Image>().color = new Color(0.20f, 0.42f, 0.62f);
            go.GetComponent<LayoutElement>().preferredHeight = 36f;

            if (width > 0f)
                go.GetComponent<LayoutElement>().preferredWidth = width;

            var button = go.GetComponent<Button>();

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo.GetComponent<RectTransform>());

            var text = textGo.GetComponent<Text>();
            text.text = label;
            text.font = font;
            text.fontSize = 17;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;

            return button;
        }

        static InputField CreateInput(Transform parent, string placeholder, string value, Font font)
        {
            var go = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField), typeof(LayoutElement));
            go.transform.SetParent(parent, false);

            go.GetComponent<Image>().color = new Color(0.16f, 0.17f, 0.20f);
            go.GetComponent<LayoutElement>().preferredHeight = 36f;

            var field = go.GetComponent<InputField>();

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo.GetComponent<RectTransform>(), 8f, 4f);

            var text = textGo.GetComponent<Text>();
            text.font = font;
            text.fontSize = 17;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;

            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            placeholderGo.transform.SetParent(go.transform, false);
            Stretch(placeholderGo.GetComponent<RectTransform>(), 8f, 4f);

            var placeholderText = placeholderGo.GetComponent<Text>();
            placeholderText.font = font;
            placeholderText.fontSize = 17;
            placeholderText.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
            placeholderText.alignment = TextAnchor.MiddleLeft;
            placeholderText.text = placeholder;

            field.textComponent = text;
            field.placeholder = placeholderText;
            field.text = value;

            return field;
        }

        static void Stretch(RectTransform rect, float horizontal = 0f, float vertical = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }
    }
}
