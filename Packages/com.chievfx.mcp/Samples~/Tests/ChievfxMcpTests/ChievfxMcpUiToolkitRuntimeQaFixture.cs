#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Chievfx.Mcp.Editor.Tests
{
    public static class ChievfxMcpUiToolkitRuntimeQaFixture
    {
        public const string ScenePath = "Assets/Scenes/ChievfxMcpUiToolkitRuntimeQaFixture.unity";
        public const string PanelSettingsFolder = "Assets/Editor/ChievfxMcpTests/UiToolkitRuntimeQa";
        public const string BottomDocumentName = "QaUiToolkitBottomDocument";
        public const string TopDocumentName = "QaUiToolkitTopDocument";
        public const string SecondaryDocumentName = "QaUiToolkitSecondaryDisplayDocument";
        public const string BottomHitName = "BottomUiToolkitHit";
        public const string TopHitName = "TopUiToolkitHit";
        public const string DisabledControlName = "DisabledUiToolkitButton";
        public const string HiddenControlName = "HiddenUiToolkitButton";
        public const string VisibilityHiddenControlName = "VisibilityHiddenUiToolkitButton";
        public const string PickingIgnoredName = "PickingIgnoredUiToolkitPanel";
        public const string TextFieldName = "FocusableUiToolkitTextField";
        public const string ToggleName = "FocusableUiToolkitToggle";
        public const string CapContainerName = "VisibleTreeCapContainer";

        public static readonly Vector2 CenterProbeNormalized = new(0.25f, 0.75f);
        public static readonly Vector2 DisabledProbeNormalized = new(0.75f, 0.25f);

        [MenuItem("ChievFX/MCP/UI Toolkit Runtime QA/Rebuild Fixture Scene")]
        public static void RebuildFixtureSceneAsset()
        {
            BuildScene(saveSceneAsset: true);
        }

        public static Scene BuildScene(bool saveSceneAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "ChievfxMcpUiToolkitRuntimeQaFixture";

            CreateQaCamera();
            CreateDocument(BottomDocumentName, "BottomPanelSettings", sortingOrder: 0, targetDisplay: 0);
            CreateDocument(TopDocumentName, "TopPanelSettings", sortingOrder: 100, targetDisplay: 0);
            CreateDocument(SecondaryDocumentName, "SecondaryDisplayPanelSettings", sortingOrder: 40, targetDisplay: 1);

            if (saveSceneAsset)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                {
                    AssetDatabase.CreateFolder("Assets", "Scenes");
                }

                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return scene;
        }

        public static void PopulateRuntimeDocuments()
        {
            foreach (var document in RuntimeDocuments())
            {
                document.rootVisualElement.Clear();
                document.rootVisualElement.name = document.gameObject.name + "Root";
                document.rootVisualElement.style.position = Position.Relative;
            }

            PopulateBottomDocument(FindDocument(BottomDocumentName));
            PopulateTopDocument(FindDocument(TopDocumentName));
            PopulateSecondaryDocument(FindDocument(SecondaryDocumentName));
        }

        public static Vector2 BottomLeftScreenPoint(string elementName)
        {
            var element = FindElement(elementName);
            var center = element.worldBound.center;
            return new Vector2(center.x, Screen.height - center.y);
        }

        public static UIDocument[] RuntimeDocuments()
        {
            return UnityEngine.Object.FindObjectsByType<UIDocument>()
                .Where(document => document.gameObject.scene == SceneManager.GetActiveScene())
                .OrderBy(document => document.gameObject.name, StringComparer.Ordinal)
                .ToArray();
        }

        private static UIDocument FindDocument(string name)
        {
            return RuntimeDocuments().First(document => string.Equals(document.gameObject.name, name, StringComparison.Ordinal));
        }

        public static VisualElement FindElement(string name)
        {
            foreach (var document in RuntimeDocuments())
            {
                var element = document.rootVisualElement.Q(name);
                if (element != null)
                {
                    return element;
                }
            }

            throw new InvalidOperationException("UI Toolkit fixture element not found: " + name);
        }

        private static void PopulateBottomDocument(UIDocument document)
        {
            var root = document.rootVisualElement;
            root.Add(CreateHitButton(BottomHitName, "BOTTOM UITK HIT", CenterProbeNormalized, new Color(0.75f, 0.12f, 0.10f, 0.82f)));
            root.Add(CreateButton(DisabledControlName, "DISABLED UITK", DisabledProbeNormalized, new Vector2(220f, 64f), new Color(0.45f, 0.45f, 0.45f, 0.88f), button =>
            {
                button.SetEnabled(false);
            }));
            root.Add(CreateButton(HiddenControlName, "DISPLAY NONE", new Vector2(0.1f, 0.1f), new Vector2(200f, 54f), new Color(1f, 0f, 1f, 0.95f), button =>
            {
                button.style.display = DisplayStyle.None;
            }));
            root.Add(CreateButton(VisibilityHiddenControlName, "VISIBILITY HIDDEN", new Vector2(0.17f, 0.18f), new Vector2(220f, 54f), new Color(1f, 0.5f, 0f, 0.95f), button =>
            {
                button.style.visibility = Visibility.Hidden;
            }));

            var ignored = new VisualElement { name = PickingIgnoredName, pickingMode = PickingMode.Ignore };
            PositionElement(ignored, new Vector2(0.5f, 0.5f), new Vector2(420f, 220f));
            ignored.style.backgroundColor = new Color(1f, 1f, 0f, 0.10f);
            root.Add(ignored);

            var textField = new TextField("Focusable Text") { name = TextFieldName, value = "runtime value" };
            PositionElement(textField, new Vector2(0.25f, 0.80f), new Vector2(280f, 42f));
            root.Add(textField);

            var toggle = new Toggle("Focusable Toggle") { name = ToggleName, value = true };
            PositionElement(toggle, new Vector2(0.25f, 0.70f), new Vector2(220f, 38f));
            root.Add(toggle);

            var capContainer = new VisualElement { name = CapContainerName, pickingMode = PickingMode.Ignore };
            capContainer.style.display = DisplayStyle.Flex;
            for (var i = 0; i < 270; i++)
            {
                capContainer.Add(new Label("Cap row " + i) { name = "VisibleCapRow" + i, pickingMode = PickingMode.Ignore });
            }

            root.Add(capContainer);
        }

        private static void PopulateTopDocument(UIDocument document)
        {
            document.rootVisualElement.Add(CreateHitButton(TopHitName, "TOP UITK HIT", CenterProbeNormalized, new Color(0.08f, 0.48f, 0.90f, 0.88f)));
        }

        private static void PopulateSecondaryDocument(UIDocument document)
        {
            var root = document.rootVisualElement;
            root.Add(CreateButton("SecondaryDisplayButton", "DISPLAY 1 METADATA", new Vector2(0.82f, 0.82f), new Vector2(240f, 58f), new Color(0.15f, 0.72f, 0.90f, 0.88f)));
        }

        private static Button CreateHitButton(string name, string text, Vector2 normalizedBottomLeft, Color color)
        {
            return CreateButton(name, text, normalizedBottomLeft, new Vector2(280f, 108f), color);
        }

        private static Button CreateButton(string name, string text, Vector2 normalizedBottomLeft, Vector2 size, Color color, Action<Button>? configure = null)
        {
            var button = new Button { name = name, text = text };
            PositionElement(button, normalizedBottomLeft, size);
            button.style.backgroundColor = color;
            button.style.color = Color.white;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            configure?.Invoke(button);
            return button;
        }

        private static void PositionElement(VisualElement element, Vector2 normalizedBottomLeft, Vector2 size)
        {
            element.style.position = Position.Absolute;
            element.style.left = Length.Percent(normalizedBottomLeft.x * 100f);
            element.style.top = Length.Percent((1f - normalizedBottomLeft.y) * 100f);
            element.style.marginLeft = -size.x * 0.5f;
            element.style.marginTop = -size.y * 0.5f;
            element.style.width = size.x;
            element.style.height = size.y;
        }

        private static void CreateQaCamera()
        {
            var cameraObject = new GameObject("QaUiToolkitRuntimeCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.05f, 0.05f, 0.07f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 3f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraObject.tag = "MainCamera";
        }

        private static UIDocument CreateDocument(string name, string panelSettingsName, int sortingOrder, int targetDisplay)
        {
            var documentObject = new GameObject(name);
            var document = documentObject.AddComponent<UIDocument>();
            document.panelSettings = LoadOrCreatePanelSettings(panelSettingsName, sortingOrder, targetDisplay);
            SetMemberValue(document, "sortingOrder", (float)sortingOrder);
            return document;
        }

        private static PanelSettings LoadOrCreatePanelSettings(string name, int sortingOrder, int targetDisplay)
        {
            EnsureFolder(PanelSettingsFolder);
            var path = PanelSettingsFolder + "/" + name + ".asset";
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, path);
            }

            SetMemberValue(settings, "sortingOrder", sortingOrder);
            SetMemberValue(settings, "targetDisplay", targetDisplay);
            SetMemberValue(settings, "referenceResolution", new Vector2Int(800, 600));
            EditorUtility.SetDirty(settings);
            return settings;
        }

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static void SetMemberValue(object target, string memberName, object value)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
                return;
            }

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(target, value);
        }
    }
}
