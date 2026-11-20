#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Chievfx.Mcp.Input.PlayMode.Tests
{
    // Runtime uGUI tools against a live Play Mode scene: honest click results, DontDestroyOnLoad discovery,
    // Play Mode search defaults, set-value on wrapped controls, drag by raycast, unreachable targets, and
    // warnings that stay quiet unless they explain a miss.
    public sealed class ChievfxMcpRuntimeUiFeedbackPlayModeTests
    {
        private const string CanvasName = "FeedbackCanvas";
        private readonly List<UnityEngine.Object> cleanupObjects = new();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var item in cleanupObjects.Where(item => item != null))
            {
                UnityEngine.Object.Destroy(item);
            }

            cleanupObjects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator ClickOnPressOnlyEventTriggerIsNotReportedAsClicked()
        {
            var canvas = BuildScene();
            var pressOnly = CreateImage(canvas.transform, "PressOnly", new Vector2(-200f, 0f), new Vector2(120f, 80f));
            var downCount = 0;
            var upCount = 0;
            var trigger = pressOnly.AddComponent<EventTrigger>();
            AddTriggerEntry(trigger, EventTriggerType.PointerDown, () => downCount++);
            AddTriggerEntry(trigger, EventTriggerType.PointerUp, () => upCount++);

            var clickTrigger = CreateImage(canvas.transform, "ClickTrigger", new Vector2(200f, 0f), new Vector2(120f, 80f));
            var clickCount = 0;
            AddTriggerEntry(clickTrigger.AddComponent<EventTrigger>(), EventTriggerType.PointerClick, () => clickCount++);
            CreateImage(canvas.transform, "EmptyButton", new Vector2(0f, 150f), new Vector2(120f, 80f)).AddComponent<Button>();
            yield return null;

            var pressResult = RunRegistry("RuntimeClick", "{'framework':'ugui','path':'" + CanvasName + "/PressOnly'}");
            var pressSection = Rows(pressResult["frameworks"]).Single();
            Assert.AreEqual(false, pressResult["anyClicked"], Describe(pressResult));
            Assert.AreEqual(true, pressResult["anyResolved"], Describe(pressResult));
            Assert.AreEqual(1, downCount, "PointerDown entry should run once.");
            Assert.AreEqual(1, upCount, "PointerUp entry should run once.");
            CollectionAssert.AreEqual(
                new[] { "pointerDown:EventTrigger", "pointerUp:EventTrigger", "pointerClick:none" },
                Strings(pressSection["events"]),
                Describe(pressSection));
            Assert.IsTrue(Strings(pressResult["warnings"]).Any(warning => warning.Contains("nothing handles PointerClick")), Describe(pressResult));
            // This project has the Input System, so the advice names the real-input tools.
            Assert.IsTrue(Strings(pressResult["warnings"]).Any(warning => warning.Contains("use input-control-mouse-event")), Describe(pressResult));

            var pressByPosition = RunRegistry("RuntimeClick", PositionArgs(RectCenter(pressOnly)));
            Assert.AreEqual(false, pressByPosition["anyClicked"], "A press-only handler is hit by position but not reported as clicked. " + Describe(pressByPosition));
            Assert.AreEqual(2, downCount, "Clicking by position should reach the press-only EventTrigger.");

            var clickResult = RunRegistry("RuntimeClick", "{'framework':'ugui','path':'" + CanvasName + "/ClickTrigger'}");
            Assert.AreEqual(true, clickResult["anyClicked"], Describe(clickResult));
            Assert.AreEqual(1, clickCount);

            var emptyButton = RunRegistry("RuntimeClick", "{'framework':'ugui','path':'" + CanvasName + "/EmptyButton'}");
            Assert.AreEqual(false, emptyButton["anyClicked"], "A Button without onClick listeners runs nothing. " + Describe(emptyButton));
            Assert.IsTrue(Strings(emptyButton["warnings"]).Any(warning => warning.Contains("Button.onClick is empty")), Describe(emptyButton));
        }

        [UnityTest]
        public IEnumerator ClickByPositionReplaysTheInputModuleSequence()
        {
            var canvas = BuildScene();
            var buttonObject = CreateImage(canvas.transform, "RealButton", Vector2.zero, new Vector2(240f, 120f));
            var button = buttonObject.AddComponent<Button>();
            var onClickCount = 0;
            button.onClick.AddListener(() => onClickCount++);
            var recorder = buttonObject.AddComponent<FeedbackPointerRecorder>();
            var label = CreateImage(buttonObject.transform, "Label", Vector2.zero, new Vector2(120f, 40f));
            yield return null;

            var position = RectCenter(label);
            var result = RunRegistry("RuntimeClick", PositionArgs(position));
            var section = Rows(result["frameworks"]).Single();

            Assert.AreEqual(true, result["anyClicked"], Describe(result));
            Assert.AreEqual(1, onClickCount, "Button.onClick should fire once.");
            CollectionAssert.AreEqual(new[] { "down", "up", "click" }, recorder.Events);
            Assert.AreSame(buttonObject, recorder.UpPointerPress, "pointerPress is the object that took PointerDown.");
            Assert.AreSame(label, recorder.UpRawPointerPress, "rawPointerPress is the raycast hit.");
            Assert.AreSame(buttonObject, recorder.UpPointerClick);
            Assert.IsTrue(recorder.UpEligibleForClick);
            Assert.AreSame(label, recorder.PressRaycastObject, "pointerPressRaycast carries the hit under the pointer.");
            Assert.AreEqual(1, recorder.ClickCount);
            Assert.AreEqual(PointerEventData.InputButton.Left, recorder.Button);
            Assert.Less(Vector2.Distance(position, recorder.PressPosition), 1f, "pressPosition is the tapped point.");
            Assert.AreEqual(1, recorder.ExitCount, "The tap ends with PointerExit, as a lifted finger does.");
            StringAssert.EndsWith("RealButton", (string)Row(section["handlerObject"])["path"]!);
        }

        [UnityTest]
        public IEnumerator SearchesReachDontDestroyOnLoad()
        {
            BuildScene();
            var ddolCanvas = CreateCanvas("FeedbackDdolCanvas", sortingOrder: 30, withRaycaster: true);
            CreateImage(ddolCanvas.transform, "DdolButton", new Vector2(0f, 150f), new Vector2(160f, 60f)).AddComponent<Button>();
            var hidden = CreateImage(ddolCanvas.transform, "DdolHidden", new Vector2(0f, -150f), new Vector2(160f, 60f));
            hidden.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(ddolCanvas.gameObject);
            yield return null;
            Assert.AreEqual("DontDestroyOnLoad", ddolCanvas.gameObject.scene.name);

            var controls = RunRegistry("ControlFind", "{'framework':'ugui','wildcards':'*DdolButton*'}");
            Assert.IsTrue(Rows(controls["controls"]).Any(row => ((string)row["path"]!).EndsWith("DdolButton", StringComparison.Ordinal)), Describe(controls));

            var find = RunExtensionTool("ugui-ui-find", "{'nameContains':'DdolButton'}");
            Assert.AreEqual(1, ToInt(find["totalMatches"]), Describe(find));

            var hiddenByPath = RunExtensionTool("ugui-ui-find", "{'paths':['FeedbackDdolCanvas/DdolHidden']}");
            Assert.AreEqual(1, ToInt(hiddenByPath["count"]), "An inactive object in DontDestroyOnLoad resolves by path. " + Describe(hiddenByPath));

            var bogusPath = RunExtensionTool("ugui-ui-find", "{'paths':['Bogus/NoSuch/Path']}");
            Assert.AreEqual(0, ToInt(bogusPath["totalMatches"]), "A path that resolves nothing must not fall back to every element. " + Describe(bogusPath));
            Assert.IsTrue(Strings(bogusPath["warnings"]).Any(warning => warning.Contains("Nothing exists at path 'Bogus/NoSuch/Path'")), Describe(bogusPath));

            var gameObjectFind = RunGameObjectFind("{'namePattern':'DdolButton'}");
            Assert.AreEqual(1, ToInt(Member(gameObjectFind, "totalMatches")), "gameobject-find searches DontDestroyOnLoad in Play Mode.");

            var hiddenByPattern = RunGameObjectFind("{'namePattern':'DdolHidden'}");
            Assert.AreEqual(0, ToInt(Member(hiddenByPattern, "totalMatches")));
            Assert.AreEqual(1, ToInt(Member(hiddenByPattern, "inactiveHidden")), "A search names the inactive matches it left out.");
            var hiddenExact = RunGameObjectFind("{'path':'FeedbackDdolCanvas/DdolHidden','includeDetails':true}");
            Assert.AreEqual(1, ToInt(Member(hiddenExact, "totalMatches")), "An exact target finds an inactive object without includeInactive.");

            var canvases = ReadResource("chievfx://extensions/chievfx.ugui/runtime/canvases");
            Assert.IsTrue(Rows(canvases["canvases"]).Any(row => Equals(row["name"], "FeedbackDdolCanvas")), Describe(canvases));
        }

        [UnityTest]
        public IEnumerator PlayModeSearchListsActiveElementsByDefault()
        {
            var canvas = BuildScene();
            CreateImage(canvas.transform, "FindMeActive", new Vector2(-100f, 0f), new Vector2(60f, 60f));
            CreateImage(canvas.transform, "FindMeInactive", new Vector2(100f, 0f), new Vector2(60f, 60f)).SetActive(false);
            yield return null;

            var byDefault = RunExtensionTool("ugui-ui-find", "{'nameContains':'FindMe'}");
            Assert.AreEqual(1, ToInt(byDefault["totalMatches"]), Describe(byDefault));
            Assert.AreEqual(false, byDefault["includeInactive"]);
            Assert.IsTrue(Strings(byDefault["warnings"]).Any(warning => warning.StartsWith("1 inactive match", StringComparison.Ordinal)), Describe(byDefault));

            var withInactive = RunExtensionTool("ugui-ui-find", "{'nameContains':'FindMe','includeInactive':true}");
            Assert.AreEqual(2, ToInt(withInactive["totalMatches"]), Describe(withInactive));

            var explicitInactive = RunExtensionTool("ugui-ui-find", "{'paths':['" + CanvasName + "/FindMeInactive']}");
            Assert.AreEqual(1, ToInt(explicitInactive["count"]), "An explicit path is returned even when inactive. " + Describe(explicitInactive));

            var hierarchy = RunExtensionTool("ugui-ui-hierarchy", "{}");
            var names = FlattenHierarchyNames(Rows(hierarchy["roots"])).ToArray();
            CollectionAssert.Contains(names, "FindMeActive");
            CollectionAssert.DoesNotContain(names, "FindMeInactive");
        }

        [UnityTest]
        public IEnumerator ClickByPathUnderAWindowSaysWhatCoversTheControl()
        {
            // A modal's full-screen backdrop on a higher canvas: the tool reaches the button by path, a player cannot.
            var canvas = BuildScene();
            var clicks = 0;
            var button = CreateImage(canvas.transform, "CoveredButton", new Vector2(0f, 0f), new Vector2(160f, 60f)).AddComponent<Button>();
            button.onClick.AddListener(() => clicks++);
            var label = CreateImage(button.transform, "Label", Vector2.zero, new Vector2(100f, 30f));
            yield return null;

            var uncovered = RunRegistry("RuntimeClick", "{'framework':'ugui','path':'" + CanvasName + "/CoveredButton'}");
            Assert.IsFalse(Strings(uncovered["warnings"]).Any(warning => warning.Contains("is covered")), "Its own child label is not a cover. " + Describe(uncovered));
            var openRows = Rows(RunRegistry("ControlFind", "{'framework':'ugui','wildcards':'*CoveredButton*'}")["controls"]);
            Assert.IsFalse(openRows.Single().ContainsKey("coveredBy"), Describe(openRows.Single()));

            var window = CreateCanvas("FeedbackWindowCanvas", sortingOrder: 500, withRaycaster: true);
            var backdrop = CreateImage(window.transform, "Backdrop", Vector2.zero, new Vector2(4000f, 4000f));
            yield return null;

            var covered = RunRegistry("RuntimeClick", "{'framework':'ugui','path':'" + CanvasName + "/CoveredButton'}");
            Assert.AreEqual(2, clicks, "The tool still clicks by path. " + Describe(covered));
            var warning = Strings(covered["warnings"]).Single(text => text.Contains("is covered"));
            StringAssert.Contains("FeedbackWindowCanvas/Backdrop", warning);
            StringAssert.Contains("sorting order 500", warning);

            var coveredRows = Rows(RunRegistry("ControlFind", "{'framework':'ugui','wildcards':'*CoveredButton*'}")["controls"]);
            Assert.AreEqual("FeedbackWindowCanvas/Backdrop", coveredRows.Single()["coveredBy"], Describe(coveredRows.Single()));
            Assert.IsNotNull(label);
            Assert.IsNotNull(backdrop);
        }

        [UnityTest]
        public IEnumerator SetControlValueReachesWrappedSliderAndExplainsMisses()
        {
            var canvas = BuildScene();
            var row = CreateImage(canvas.transform, "SettingRow", new Vector2(0f, 120f), new Vector2(400f, 60f));
            row.AddComponent<FeedbackSettingWrapper>();
            var sliderRoot = CreateRect(row.transform, "SliderRoot", Vector2.zero, new Vector2(300f, 40f));
            var slider = CreateRect(sliderRoot.transform, "Slider", Vector2.zero, new Vector2(300f, 40f)).AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.2f;
            CreateImage(canvas.transform, "PlainRow", new Vector2(0f, -120f), new Vector2(400f, 60f));
            yield return null;

            var wrapped = RunRegistry("RuntimeSetControlValue", "{'framework':'ugui','path':'" + CanvasName + "/SettingRow','value':0.75}");
            Assert.AreEqual(true, wrapped["resolved"], Describe(wrapped));
            Assert.AreEqual(0.75f, slider.value, 0.0001f);
            Assert.IsTrue(Strings(wrapped["warnings"]).Any(warning => warning.Contains("no settable control of its own")), Describe(wrapped));

            var plain = RunRegistry("RuntimeSetControlValue", "{'path':'" + CanvasName + "/PlainRow','value':1}");
            Assert.AreEqual(false, plain["resolved"], Describe(plain));
            Assert.AreEqual(true, plain["playMode"], "playMode must be reported on a miss too.");
            var plainWarning = Strings(plain["warnings"]).Single();
            StringAssert.Contains("exists but has no control", plainWarning);
            StringAssert.Contains("Image", plainWarning);

            var missing = RunRegistry("RuntimeSetControlValue", "{'path':'" + CanvasName + "/Nope','value':1}");
            Assert.AreEqual(false, missing["resolved"], Describe(missing));
            Assert.AreEqual(true, missing["playMode"]);
            StringAssert.StartsWith("Nothing exists at path:'" + CanvasName + "/Nope'", Strings(missing["warnings"]).Single());
        }

        [UnityTest]
        public IEnumerator DragResolvesTargetsByRaycastLikeTheProbe()
        {
            var canvas = BuildScene();
            var slider = CreateSlider(canvas.transform, out var handle);
            var plain = CreateImage(canvas.transform, "PlainPanel", new Vector2(0f, -150f), new Vector2(300f, 80f));
            yield return null;
            Canvas.ForceUpdateCanvases();

            var handleCenter = RectCenter(handle);
            var probe = RunExtensionTool("ui-runtime-probe", "{'x':" + F(handleCenter.x) + ",'y':" + F(handleCenter.y) + "}");
            StringAssert.EndsWith("Handle", (string)Rows(Row(probe["ugui"])["hits"]).First()["path"]!, "The probe sees the handle first.");

            var drag = RunRegistry("RuntimeDrag", "{'framework':'ugui','x':" + F(handleCenter.x) + ",'y':" + F(handleCenter.y) + ",'deltaX':-120}");
            Assert.AreEqual(true, drag["anyDragged"], Describe(drag));
            Assert.Less(slider.value, 0.45f, "Dragging the handle left lowers the value.");

            var plainCenter = RectCenter(plain);
            var miss = RunRegistry("RuntimeDrag", "{'framework':'ugui','x':" + F(plainCenter.x) + ",'y':" + F(plainCenter.y) + ",'deltaX':50}");
            Assert.AreEqual(true, miss["anyResolved"], Describe(miss));
            Assert.AreEqual(false, miss["anyDragged"], Describe(miss));
            Assert.IsTrue(Strings(miss["warnings"]).Any(warning => warning.Contains("handles drag")), Describe(miss));
        }

        [UnityTest]
        public IEnumerator DragScrollsScrollRectContent()
        {
            var canvas = BuildScene();
            var viewport = CreateImage(canvas.transform, "ScrollView", Vector2.zero, new Vector2(300f, 200f));
            viewport.AddComponent<RectMask2D>();
            var content = CreateRect(viewport.transform, "Content", Vector2.zero, new Vector2(1200f, 200f));
            var scroll = viewport.AddComponent<ScrollRect>();
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = (RectTransform)content.transform;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Unrestricted;
            scroll.inertia = false;
            yield return null;
            Canvas.ForceUpdateCanvases();

            var before = scroll.content.anchoredPosition.x;
            var center = RectCenter(viewport);
            var drag = RunRegistry("RuntimeDrag", "{'framework':'ugui','x':" + F(center.x) + ",'y':" + F(center.y) + ",'deltaX':-100}");
            yield return null;

            Assert.AreEqual(true, drag["anyDragged"], Describe(drag));
            Assert.Less(scroll.content.anchoredPosition.x, before - 50f, "BeginDrag takes the gesture start, so the content follows the drag. " + Describe(drag));
        }

        [UnityTest]
        public IEnumerator TypeTextWarnsWhenThePlayerCannotReachTheField()
        {
            var canvas = BuildScene();
            var group = CreateRect(canvas.transform, "HiddenGroup", Vector2.zero, new Vector2(300f, 60f)).AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            var fieldObject = CreateImage(group.transform, "CommandInputField", Vector2.zero, new Vector2(300f, 60f));
            var text = CreateRect(fieldObject.transform, "Text", Vector2.zero, new Vector2(280f, 50f)).AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var inputField = fieldObject.AddComponent<InputField>();
            inputField.textComponent = text;
            yield return null;

            var typed = RunExtensionTool("ui-runtime-type-text", "{'framework':'ugui','path':'" + CanvasName + "/HiddenGroup/CommandInputField','text':'help'}");
            Assert.AreEqual(true, typed["resolved"], Describe(typed));
            Assert.AreEqual("help", inputField.text);
            var warnings = Strings(typed["warnings"]);
            Assert.IsTrue(warnings.Any(warning => warning.Contains("invisible to the player")), Describe(typed));
            Assert.IsTrue(warnings.Any(warning => warning.Contains("blocksRaycasts off")), Describe(typed));

            var cleared = RunExtensionTool("ui-runtime-type-text", "{'framework':'ugui','path':'" + CanvasName + "/HiddenGroup/CommandInputField','text':''}");
            Assert.AreEqual("help", cleared["textBefore"]);
            Assert.AreEqual(string.Empty, cleared["textAfter"], "Clearing reports an empty textAfter rather than dropping it.");
            Assert.IsFalse(Strings(cleared["warnings"]).Any(warning => warning.Contains("inactive")), "An active field is not reported inactive. " + Describe(cleared));

            group.gameObject.SetActive(false);
            var inactive = RunExtensionTool("ui-runtime-type-text", "{'framework':'ugui','path':'" + CanvasName + "/HiddenGroup/CommandInputField','text':'x'}");
            Assert.IsTrue(
                Strings(inactive["warnings"]).Any(warning => warning.Contains("inactive") && warning.Contains("HiddenGroup' is switched off")),
                "A field under a switched-off parent names that parent. " + Describe(inactive));
        }

        [UnityTest]
        public IEnumerator CanvasWarningsOnlyExplainMissesAndStacksHaveNoDuplicates()
        {
            var canvas = BuildScene();
            var buttonObject = CreateImage(canvas.transform, "QuietButton", new Vector2(-150f, 0f), new Vector2(160f, 80f));
            buttonObject.AddComponent<Button>().onClick.AddListener(() => { });
            CreateCanvas("InactiveFeedbackCanvas", sortingOrder: 5, withRaycaster: true).gameObject.SetActive(false);
            CreateCanvas("NoRaycasterFeedbackCanvas", sortingOrder: 6, withRaycaster: false);

            // A canvas without a raycaster holding a nested canvas: nothing here can be raycast, so the probe
            // falls back to RectTransform overlaps, where the nested canvas used to be walked twice.
            var host = CreateCanvas("NestedHostCanvas", sortingOrder: 50, withRaycaster: false);
            var nested = CreateRect(host.transform, "NestedChildCanvas", new Vector2(250f, 150f), new Vector2(120f, 120f)).AddComponent<Canvas>();
            var nestedImage = CreateImage(nested.transform, "NestedImage", Vector2.zero, new Vector2(100f, 100f));
            yield return null;
            Canvas.ForceUpdateCanvases();

            var click = RunRegistry("RuntimeClick", PositionArgs(RectCenter(buttonObject)));
            Assert.AreEqual(true, click["anyClicked"], Describe(click));
            Assert.IsFalse(Strings(click["warnings"]).Any(IsCanvasWarning), "A successful click carries no per-canvas warnings: " + string.Join(" | ", Strings(click["warnings"])));

            var status = ReadResource("chievfx://extensions/chievfx.ugui/runtime/status");
            Assert.IsFalse(Strings(status["warnings"]).Any(IsCanvasWarning), string.Join(" | ", Strings(status["warnings"])));

            var nestedCenter = RectCenter(nestedImage);
            var probe = RunExtensionTool("ui-runtime-probe", "{'x':" + F(nestedCenter.x) + ",'y':" + F(nestedCenter.y) + "}");
            var ugui = Row(probe["ugui"]);
            var paths = Rows(ugui["hits"]).Select(hit => (string)hit["path"]!).ToArray();
            CollectionAssert.AllItemsAreUnique(paths, "Hit rows: " + string.Join(", ", paths));
            CollectionAssert.Contains(paths, "NestedHostCanvas/NestedChildCanvas/NestedImage");
            var probeWarnings = Strings(ugui["warnings"]);
            Assert.IsTrue(probeWarnings.Any(warning => warning.Contains("'NestedHostCanvas/NestedChildCanvas' has no GraphicRaycaster")), string.Join(" | ", probeWarnings));
            Assert.IsTrue(probeWarnings.Any(warning => warning.Contains("not raycast hits")), string.Join(" | ", probeWarnings));

            var canvases = Rows(ReadResource("chievfx://extensions/chievfx.ugui/runtime/canvases")["canvases"]).ToArray();
            Assert.IsFalse(canvases.Any(row => row.ContainsKey("canvas")), "Canvas rows no longer repeat themselves under 'canvas'.");
            Assert.AreEqual("NestedHostCanvas", canvases.Single(row => Equals(row["name"], "NestedChildCanvas"))["parentCanvas"]);
        }

        private static bool IsCanvasWarning(string warning)
        {
            return warning.Contains("will not receive runtime raycasts")
                || warning.Contains("has no GraphicRaycaster")
                || warning.Contains("without worldCamera");
        }

        private Canvas BuildScene()
        {
            var scene = SceneManager.CreateScene("RuntimeUiFeedbackScene_" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(scene);
            var cameraObject = Track(new GameObject("FeedbackCamera"));
            cameraObject.AddComponent<Camera>().transform.position = new Vector3(0f, 0f, -10f);
            Track(new GameObject("FeedbackEventSystem")).AddComponent<EventSystem>();
            return CreateCanvas(CanvasName, sortingOrder: 10, withRaycaster: true);
        }

        private Canvas CreateCanvas(string name, int sortingOrder, bool withRaycaster)
        {
            var canvasObject = Track(new GameObject(name, typeof(RectTransform), typeof(Canvas)));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            if (withRaycaster)
            {
                canvasObject.AddComponent<GraphicRaycaster>();
            }

            return canvas;
        }

        private static GameObject CreateRect(Transform parent, string name, Vector2 anchoredPosition, Vector2 size)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)gameObject.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            return gameObject;
        }

        private static GameObject CreateImage(Transform parent, string name, Vector2 anchoredPosition, Vector2 size)
        {
            var gameObject = CreateRect(parent, name, anchoredPosition, size);
            gameObject.AddComponent<Image>().raycastTarget = true;
            return gameObject;
        }

        private static Slider CreateSlider(Transform parent, out GameObject handle)
        {
            var root = CreateRect(parent, "FeedbackSlider", new Vector2(0f, 100f), new Vector2(400f, 40f));
            CreateImage(root.transform, "Background", Vector2.zero, new Vector2(400f, 40f));
            var fillArea = CreateRect(root.transform, "Fill Area", Vector2.zero, new Vector2(380f, 20f));
            var fill = CreateImage(fillArea.transform, "Fill", Vector2.zero, Vector2.zero);
            var handleArea = CreateRect(root.transform, "Handle Slide Area", Vector2.zero, new Vector2(380f, 40f));
            handle = CreateImage(handleArea.transform, "Handle", Vector2.zero, new Vector2(30f, 40f));
            var slider = root.AddComponent<Slider>();
            slider.fillRect = (RectTransform)fill.transform;
            slider.handleRect = (RectTransform)handle.transform;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0.5f;
            return slider;
        }

        private static void AddTriggerEntry(EventTrigger trigger, EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }

        private static Vector2 RectCenter(GameObject gameObject)
        {
            var rect = (RectTransform)gameObject.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
        }

        private static string PositionArgs(Vector2 position)
        {
            return "{'framework':'ugui','x':" + F(position.x) + ",'y':" + F(position.y) + "}";
        }

        private static string F(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> FlattenHierarchyNames(IEnumerable<Dictionary<string, object?>> nodes)
        {
            foreach (var node in nodes)
            {
                yield return (string)node["name"]!;
                if (node.TryGetValue("children", out var children))
                {
                    foreach (var name in FlattenHierarchyNames(Rows(children)))
                    {
                        yield return name;
                    }
                }
            }
        }

        private GameObject Track(GameObject gameObject)
        {
            cleanupObjects.Add(gameObject);
            return gameObject;
        }

        private static Dictionary<string, object?> RunRegistry(string methodName, string argsJson)
        {
            EnsureRuntimeUiRegistry();
            var method = EditorType("Chievfx.Mcp.Editor.ChievfxMcpRuntimeUiAdapterRegistry")
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, methodName);
            return (Dictionary<string, object?>)method!.Invoke(null, new[] { ParseJson(argsJson) })!;
        }

        private static Dictionary<string, object?> RunExtensionTool(string toolName, string argsJson)
        {
            EnsureRuntimeUiRegistry();
            var method = EditorType("Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry").GetMethod("TryRunTool", BindingFlags.NonPublic | BindingFlags.Static);
            var parameters = new object?[] { toolName, ParseJson(argsJson), null };
            Assert.IsTrue((bool)method!.Invoke(null, parameters)!, toolName + " is not registered.");
            return (Dictionary<string, object?>)parameters[2]!;
        }

        private static Dictionary<string, object?> ReadResource(string uri)
        {
            EnsureRuntimeUiRegistry();
            var method = EditorType("Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry").GetMethod("TryReadResource", BindingFlags.NonPublic | BindingFlags.Static);
            var parameters = new object?[] { uri, null };
            Assert.IsTrue((bool)method!.Invoke(null, parameters)!, uri + " is not registered.");
            return (Dictionary<string, object?>)parameters[1]!;
        }

        private static object RunGameObjectFind(string argsJson)
        {
            var serviceType = EditorType("Chievfx.Mcp.Editor.GameObjectBridgeService");
            var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
            return serviceType.GetMethod("Find", BindingFlags.Public | BindingFlags.Instance)!.Invoke(service, new[] { ParseJson(argsJson) })!;
        }

        private static void EnsureRuntimeUiRegistry()
        {
            EditorType("Chievfx.Mcp.Editor.ChievfxMcpRuntimeUiAdapterRegistry")
                .GetMethod("EnsureRegistered", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }

        private static object ParseJson(string argsJson)
        {
            return EditorType("Newtonsoft.Json.Linq.JObject")
                .GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)!
                .Invoke(null, new object[] { argsJson })!;
        }

        private static Type EditorType(string fullName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(candidate => candidate != null);
            Assert.IsNotNull(type, fullName + " must be loaded in the Editor while PlayMode tests run.");
            return type!;
        }

        private static object? Member(object source, string name)
        {
            return source.GetType().GetProperty(name)?.GetValue(source);
        }

        private static Dictionary<string, object?> Row(object? value)
        {
            Assert.IsInstanceOf<Dictionary<string, object?>>(value);
            return (Dictionary<string, object?>)value!;
        }

        private static IEnumerable<Dictionary<string, object?>> Rows(object? value)
        {
            return value is IEnumerable enumerable && value is not string
                ? enumerable.OfType<Dictionary<string, object?>>()
                : Enumerable.Empty<Dictionary<string, object?>>();
        }

        private static string[] Strings(object? value)
        {
            return value is IEnumerable enumerable && value is not string
                ? enumerable.Cast<object?>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty).ToArray()
                : Array.Empty<string>();
        }

        private static int ToInt(object? value)
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static string Describe(Dictionary<string, object?> row)
        {
            return string.Join("; ", row.Select(pair => pair.Key + "=" + (pair.Value switch
            {
                string text => text,
                IEnumerable enumerable => "[" + string.Join(" | ", enumerable.Cast<object?>().Select(item => item is Dictionary<string, object?> nested ? "{" + Describe(nested) + "}" : Convert.ToString(item, CultureInfo.InvariantCulture))) + "]",
                null => "null",
                _ => Convert.ToString(pair.Value, CultureInfo.InvariantCulture),
            })));
        }
    }

    public sealed class FeedbackPointerRecorder : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IPointerExitHandler
    {
        public List<string> Events { get; } = new();

        public GameObject? UpPointerPress { get; private set; }

        public GameObject? UpRawPointerPress { get; private set; }

        public GameObject? UpPointerClick { get; private set; }

        public bool UpEligibleForClick { get; private set; }

        public GameObject? PressRaycastObject { get; private set; }

        public int ClickCount { get; private set; }

        public PointerEventData.InputButton Button { get; private set; }

        public Vector2 PressPosition { get; private set; }

        public int ExitCount { get; private set; }

        public void OnPointerDown(PointerEventData eventData)
        {
            Events.Add("down");
            PressRaycastObject = eventData.pointerPressRaycast.gameObject;
            PressPosition = eventData.pressPosition;
            Button = eventData.button;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            Events.Add("up");
            UpPointerPress = eventData.pointerPress;
            UpRawPointerPress = eventData.rawPointerPress;
            UpPointerClick = eventData.pointerClick;
            UpEligibleForClick = eventData.eligibleForClick;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            Events.Add("click");
            ClickCount = eventData.clickCount;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ExitCount++;
        }
    }

    public sealed class FeedbackSettingWrapper : MonoBehaviour
    {
    }
}
