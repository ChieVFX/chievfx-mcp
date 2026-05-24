#nullable enable
#if CHIEVFX_MCP_HAS_UGUI
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Chievfx.Mcp.Editor.Tests
{
    public static class ChievfxMcpUguiRuntimeQaFixture
    {
        public const string ScenePath = "Assets/Scenes/ChievfxMcpUguiRuntimeQaFixture.unity";
        public const string OverlayCanvasName = "QaOverlayCanvas";
        public const string TopCanvasName = "QaOverlayTopCanvas";
        public const string CameraCanvasName = "QaCameraCanvas";
        public const string WorldCanvasName = "QaWorldCanvas";
        public const string BottomButtonPath = OverlayCanvasName + "/BottomHitPanel/BottomButton";
        public const string TopButtonPath = TopCanvasName + "/TopHitPanel/TopButton";
        public const string SliderPath = OverlayCanvasName + "/FixtureSlider";
        public const string TogglePath = OverlayCanvasName + "/FixtureToggle";
        public const string DisabledButtonPath = OverlayCanvasName + "/DisabledButton";
        public const string HiddenInactiveButtonPath = OverlayCanvasName + "/HiddenInactiveButton";
        public const string ScrollRectPath = OverlayCanvasName + "/FixtureScrollRect";
        public static int ButtonClickCount { get; set; }

        public static readonly Vector2 CenterProbeNormalized = new(0.5f, 0.5f);
        public static readonly Vector2 DisabledProbeNormalized = new(0.75f, 0.25f);
        public static readonly Vector2 OutsideProbeNormalized = new(1.2f, 0.5f);

        [MenuItem("ChievFX/MCP/uGUI Runtime QA/Rebuild Fixture Scene")]
        public static void RebuildFixtureSceneAsset()
        {
            BuildScene(saveSceneAsset: true);
        }

        public static Scene BuildScene(bool saveSceneAsset)
        {
            ButtonClickCount = 0;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "ChievfxMcpUguiRuntimeQaFixture";

            var camera = CreateQaCamera();
            CreateEventSystem();

            var overlayCanvas = CreateCanvas(OverlayCanvasName, RenderMode.ScreenSpaceOverlay, sortingOrder: 0, camera: null);
            CreateCenteredButtonPanel(overlayCanvas.transform, "BottomHitPanel", "BottomButton", "BOTTOM HIT", new Color(0.75f, 0.12f, 0.10f, 0.82f), new Color(1f, 0.30f, 0.25f, 0.95f));
            CreateSlider(overlayCanvas.transform);
            CreateToggle(overlayCanvas.transform);
            CreateDisabledButton(overlayCanvas.transform);
            CreateHiddenInactiveButton(overlayCanvas.transform);
            CreateScrollRect(overlayCanvas.transform);

            var cameraCanvas = CreateCanvas(CameraCanvasName, RenderMode.ScreenSpaceCamera, sortingOrder: 20, camera: camera);
            CreateButton(cameraCanvas.transform, "CameraCanvasButton", "CAMERA CANVAS", new Vector2(0.25f, 0.52f), new Vector2(190f, 56f), new Color(0.16f, 0.42f, 0.95f, 0.86f), interactable: true);

            var worldCanvas = CreateCanvas(WorldCanvasName, RenderMode.WorldSpace, sortingOrder: 30, camera: camera);
            var worldRect = (RectTransform)worldCanvas.transform;
            worldRect.sizeDelta = new Vector2(260f, 120f);
            worldCanvas.transform.position = new Vector3(2.2f, 0.7f, 4.0f);
            worldCanvas.transform.localScale = Vector3.one * 0.01f;
            CreateButton(worldCanvas.transform, "WorldSpaceButton", "WORLD SPACE", new Vector2(0.5f, 0.5f), new Vector2(220f, 70f), new Color(0.95f, 0.62f, 0.10f, 0.88f), interactable: true);

            var topCanvas = CreateCanvas(TopCanvasName, RenderMode.ScreenSpaceOverlay, sortingOrder: 100, camera: null);
            CreateCenteredButtonPanel(topCanvas.transform, "TopHitPanel", "TopButton", "TOP HIT", new Color(0.08f, 0.48f, 0.90f, 0.70f), new Color(0.18f, 0.70f, 1f, 0.95f));

            if (saveSceneAsset)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                {
                    AssetDatabase.CreateFolder("Assets", "Scenes");
                }

                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.Refresh();
            }

            return scene;
        }

        private static Camera CreateQaCamera()
        {
            var cameraObject = new GameObject("QaRuntimeCamera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.06f, 0.08f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 3f;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            cameraObject.tag = "MainCamera";
            return camera;
        }

        private static void CreateEventSystem()
        {
            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
        }

        private static Canvas CreateCanvas(string name, RenderMode renderMode, int sortingOrder, Camera? camera)
        {
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = renderMode;
            canvas.overrideSorting = true;
            canvas.sortingOrder = sortingOrder;
            canvas.worldCamera = camera;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(800f, 600f);
            scaler.matchWidthOrHeight = 0.5f;
            return canvas;
        }

        private static void CreateCenteredButtonPanel(Transform parent, string panelName, string buttonName, string text, Color panelColor, Color buttonColor)
        {
            var panel = CreatePanel(parent, panelName, new Vector2(0.5f, 0.5f), new Vector2(340f, 180f), panelColor);
            CreateButton(panel.transform, buttonName, text, new Vector2(0.5f, 0.5f), new Vector2(260f, 108f), buttonColor, interactable: true);
        }

        private static GameObject CreatePanel(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, worldPositionStays: false);
            var rect = (RectTransform)panel.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            var image = panel.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return panel;
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchor, Vector2 size, Color color, bool interactable)
        {
            var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, worldPositionStays: false);
            var rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;

            var image = buttonObject.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            var button = buttonObject.GetComponent<Button>();
            button.interactable = interactable;
            button.targetGraphic = image;
            AddText(buttonObject.transform, "Text", label, Color.white, 20, TextAnchor.MiddleCenter);
            return button;
        }

        private static void CreateSlider(Transform parent)
        {
            var sliderObject = DefaultControls.CreateSlider(new DefaultControls.Resources());
            sliderObject.name = "FixtureSlider";
            sliderObject.transform.SetParent(parent, worldPositionStays: false);
            SetRect(sliderObject, new Vector2(0.5f, 0.25f), new Vector2(320f, 44f));
            sliderObject.GetComponent<Slider>().value = 0.65f;
            TintGraphics(sliderObject, new Color(0.25f, 0.95f, 0.50f, 0.95f));
            AddText(sliderObject.transform, "QaLabel", "SLIDER 65%", Color.white, 14, TextAnchor.MiddleLeft, new Vector2(0.5f, 1f), new Vector2(320f, 28f), new Vector2(0f, 28f));
        }

        private static void CreateToggle(Transform parent)
        {
            var toggleObject = DefaultControls.CreateToggle(new DefaultControls.Resources());
            toggleObject.name = "FixtureToggle";
            toggleObject.transform.SetParent(parent, worldPositionStays: false);
            SetRect(toggleObject, new Vector2(0.25f, 0.75f), new Vector2(190f, 44f));
            var toggle = toggleObject.GetComponent<Toggle>();
            toggle.isOn = true;
            var label = toggleObject.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = "TOGGLE ON";
                label.color = Color.white;
                label.raycastTarget = false;
            }
            TintGraphics(toggleObject, new Color(0.70f, 0.45f, 1f, 0.95f));
        }

        private static void CreateDisabledButton(Transform parent)
        {
            CreateButton(parent, "DisabledButton", "DISABLED", DisabledProbeNormalized, new Vector2(210f, 70f), new Color(0.45f, 0.45f, 0.45f, 0.85f), interactable: false);
        }

        private static void CreateHiddenInactiveButton(Transform parent)
        {
            var hidden = CreateButton(parent, "HiddenInactiveButton", "HIDDEN", new Vector2(0.10f, 0.10f), new Vector2(180f, 60f), new Color(1f, 0f, 1f, 0.95f), interactable: true);
            hidden.gameObject.SetActive(false);
        }

        private static void CreateScrollRect(Transform parent)
        {
            var scrollObject = DefaultControls.CreateScrollView(new DefaultControls.Resources());
            scrollObject.name = "FixtureScrollRect";
            scrollObject.transform.SetParent(parent, worldPositionStays: false);
            SetRect(scrollObject, new Vector2(0.20f, 0.28f), new Vector2(250f, 140f));
            TintGraphics(scrollObject, new Color(0.12f, 0.62f, 0.72f, 0.80f));

            var scrollRect = scrollObject.GetComponent<ScrollRect>();
            if (scrollRect.content != null)
            {
                scrollRect.content.sizeDelta = new Vector2(220f, 360f);
                for (var i = 0; i < 8; i++)
                {
                    AddText(scrollRect.content, "Row" + i, "Scrollable row " + i, Color.white, 14, TextAnchor.MiddleLeft, new Vector2(0.5f, 1f), new Vector2(210f, 34f), new Vector2(0f, -24f - i * 38f));
                }
            }
        }

        private static Text AddText(Transform parent, string name, string value, Color color, int fontSize, TextAnchor alignment)
        {
            return AddText(parent, name, value, color, fontSize, alignment, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        }

        private static Text AddText(Transform parent, string name, string value, Color color, int fontSize, TextAnchor alignment, Vector2 anchor, Vector2 size, Vector2 anchoredPosition)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, worldPositionStays: false);
            var rect = (RectTransform)textObject.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size == Vector2.zero ? new Vector2(0f, 0f) : size;
            if (size == Vector2.zero)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            var text = textObject.GetComponent<Text>();
            text.text = value;
            text.color = color;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return text;
        }

        private static void SetRect(GameObject target, Vector2 anchor, Vector2 size)
        {
            var rect = (RectTransform)target.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        private static void TintGraphics(GameObject root, Color accent)
        {
            foreach (var graphic in root.GetComponentsInChildren<Graphic>(includeInactive: true))
            {
                if (graphic is Text text)
                {
                    text.color = Color.white;
                    text.raycastTarget = false;
                    continue;
                }

                graphic.color = Color.Lerp(graphic.color, accent, 0.45f);
            }
        }

    }
}
#endif
