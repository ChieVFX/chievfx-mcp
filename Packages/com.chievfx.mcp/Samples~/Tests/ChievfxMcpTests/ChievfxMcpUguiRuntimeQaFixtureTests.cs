#nullable enable
#if CHIEVFX_MCP_HAS_UGUI
using System;
using System.Collections.Generic;
using System.Linq;
using Chievfx.Mcp.Editor;
using Chievfx.Mcp.Editor;
using Chievfx.Mcp.Extensions.Ugui;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpUguiRuntimeQaFixtureTests
    {
        [UnityTearDown]
        public System.Collections.IEnumerator TearDown()
        {
            if (Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [Test]
        public void RuntimeStatusAdvertisesReadAndInteractionScopes()
        {
            RequireUgui();

            var status = ReadResource("chievfx://extensions/chievfx.ugui/status");

            Assert.IsNotNull(status["ugui"]);
            Assert.IsNotNull(status["context"]);
            Assert.IsNotNull(status["currentHierarchy"]);
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeQaFixtureResourcesExposeExpectedCanvasesAndControls()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var canvases = ReadResource("chievfx://extensions/chievfx.ugui/runtime/canvases");
            var canvasRows = Rows(canvases, "canvases");
            Assert.GreaterOrEqual(canvasRows.Length, 4);
            CollectionAssert.Contains(canvasRows.Select(row => row["renderMode"]).ToArray(), "ScreenSpaceOverlay");
            CollectionAssert.Contains(canvasRows.Select(row => row["renderMode"]).ToArray(), "ScreenSpaceCamera");
            CollectionAssert.Contains(canvasRows.Select(row => row["renderMode"]).ToArray(), "WorldSpace");

            var visibleTree = ReadResource("chievfx://extensions/chievfx.ugui/runtime/visible-tree");
            var visiblePaths = Rows(visibleTree, "canvases")
                .SelectMany(canvas => Rows(canvas, "elements"))
                .Select(row => (string)row["path"]!)
                .ToArray();
            Assert.IsTrue(visiblePaths.Any(path => path.EndsWith(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath, StringComparison.Ordinal)));
            Assert.IsFalse(visiblePaths.Any(path => path.Contains("HiddenInactiveButton", StringComparison.Ordinal)));

            var rows = CollectControlFindRows("ugui");
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.SliderPath, StringComparison.Ordinal)));
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.TogglePath, StringComparison.Ordinal)));
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.ScrollRectPath, StringComparison.Ordinal)));
            Assert.IsFalse(rows.Any(row => ((string)row["path"]!).Contains("HiddenInactiveButton", StringComparison.Ordinal)));
        }

        [UnityTest]
        public System.Collections.IEnumerator ControlFindWildcardsFilterMatchesPathSegments()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var result = (Dictionary<string, object?>)ChievfxMcpRuntimeUiAdapterRegistry.ControlFind(
                JObject.Parse("{'framework':'ugui','wildcards':'*TopButton'}"))!;
            var paths = Rows(result, "controls").Select(row => (string)row["path"]!).ToArray();
            Assert.GreaterOrEqual(paths.Length, 1);
            Assert.IsTrue(paths.All(path => path.Contains("TopButton", StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(paths.Any(path => path.Contains("BottomButton", StringComparison.OrdinalIgnoreCase)));
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeProbeMatchesVisibleTopCanvasAtCenterMarker()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var probe = RunExtensionTool(
                "ui-runtime-probe",
                ProbeScreenArgs(RectCenterScreenPoint(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath)));
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual("bottom-left", Row(probe, "probe")["origin"]);
            var hits = Rows(Row(probe, "ugui"), "hits");
            Assert.GreaterOrEqual(hits.Length, 2, string.Join("; ", StringArray(probe, "warnings")));

            var top = hits[0];
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath, (string)top["path"]!);
            var bottomIndex = Array.FindIndex(hits, row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.BottomButtonPath, StringComparison.Ordinal));
            Assert.GreaterOrEqual(bottomIndex, 1);
            Assert.AreEqual(100, top["sortingOrder"]);
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeProbeReportsOutsideBoundsWithoutHit()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var probe = RunExtensionTool("ui-runtime-probe", "{'x':1.2,'y':0.5,'isNormalized':true}");
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual(0, Row(probe, "ugui")["count"]);
            Assert.IsTrue(StringArray(probe, "warnings").Any(warning => warning.Contains("outside current screen/game-view bounds", StringComparison.Ordinal)));
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeProbeWarnsAndReturnsEmptyStackWithoutEventSystem()
        {
            RequireUgui();
            OpenFixtureScene();
            UnityEngine.Object.DestroyImmediate(UnityEngine.Object.FindAnyObjectByType<EventSystem>()!.gameObject);

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var probe = RunExtensionTool("ui-runtime-probe", "{'x':0.5,'y':0.5,'isNormalized':true}");
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual(0, Row(probe, "ugui")["count"]);
            Assert.IsTrue(StringArray(probe, "warnings").Any(warning => warning.Contains("No active EventSystem.current", StringComparison.Ordinal)));
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeProbeIncludesDisabledControlsButExcludesInactiveHiddenControls()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var disabledProbe = RunExtensionTool(
                "ui-runtime-probe",
                ProbeScreenArgs(RectCenterScreenPoint(ChievfxMcpUguiRuntimeQaFixture.DisabledButtonPath)));
            var disabledTop = Rows(Row(disabledProbe, "ugui"), "hits")[0];
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.DisabledButtonPath, (string)disabledTop["path"]!);
            Assert.AreEqual(false, disabledTop["interactable"]);

            var hiddenProbe = RunExtensionTool("ui-runtime-probe", "{'x':0.1,'y':0.1,'isNormalized':true}");
            var hiddenStack = Rows(Row(hiddenProbe, "ugui"), "hits");
            Assert.IsFalse(hiddenStack.Any(row => ((string)row["path"]!).Contains("HiddenInactiveButton", StringComparison.Ordinal)));
        }

        [Test]
        public void RuntimeMutatingToolsRejectOutsidePlayMode()
        {
            RequireUgui();
            OpenFixtureScene();

            Assert.Throws<InvalidOperationException>(() =>
                RunTool("ugui-runtime-set-control-value", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.SliderPath + "','value':0.5,'allowStateMutation':true}"));
            Assert.Throws<InvalidOperationException>(() =>
                RunTool("ugui-runtime-select", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TogglePath + "','allowStateMutation':true}"));
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeInteractionToolsClickSetSelectAndDragSafely()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;
            GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath)!
                .GetComponent<Button>()
                .onClick
                .AddListener(() => ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount++);

            var click = RunUiRuntimeClick("{'path':'" + ChievfxMcpUguiRuntimeQaFixture.TopButtonPath + "','framework':'ugui'}");
            Assert.AreEqual(1, ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount);
            Assert.AreEqual(true, click["anyClicked"]);
            Assert.IsNotNull(UguiClickSection(click)["handler"]);
            Assert.IsNotNull(UguiClickSection(click)["selectedAfter"]);

            var slider = GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.SliderPath)!.GetComponent<Slider>();
            var setSlider = RunUiRuntimeSetControlValue(
                "{'path':'" + ChievfxMcpUguiRuntimeQaFixture.SliderPath + "','framework':'ugui','value':0.25}");
            Assert.AreEqual(0.25f, slider.value, 0.001f);
            Assert.IsNotNull(setSlider["targetStateBefore"]);
            Assert.IsNotNull(setSlider["targetStateAfter"]);

            var toggle = GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.TogglePath)!.GetComponent<Toggle>();
            RunUiRuntimeSetControlValue(
                "{'path':'" + ChievfxMcpUguiRuntimeQaFixture.TogglePath + "','framework':'ugui','value':false}");
            Assert.AreEqual(false, toggle.isOn);

            var select = RunTool("ugui-runtime-select", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TogglePath + "','allowStateMutation':true}");
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.TogglePath, (string)Row(select, "selectedObjectAfter")["path"]!);

            var dragStart = SliderScreenPoint(slider, 0.1f);
            var dragEnd = SliderScreenPoint(slider, 0.9f);
            RunUiRuntimeDrag(
                "{'path':'" + ChievfxMcpUguiRuntimeQaFixture.SliderPath + "','framework':'ugui','x':" + dragStart.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'y':" + dragStart.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'toX':" + dragEnd.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'toY':" + dragEnd.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
            Assert.Greater(slider.value, 0.7f);
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeClickSkipsPanelRaycasterAndClicksButtonUnderneath()
        {
            RequireUgui();
            OpenFixtureScene();
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var eventSystem = UnityEngine.Object.FindAnyObjectByType<EventSystem>()!;
            AddOptionalComponent(eventSystem.gameObject, "UnityEngine.UIElements.PanelRaycaster");
            AddOptionalComponent(eventSystem.gameObject, "UnityEngine.UIElements.PanelEventHandler");
            Canvas.ForceUpdateCanvases();
            yield return null;

            GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath)!
                .GetComponent<Button>()
                .onClick
                .AddListener(() => ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount++);

            var screenPosition = RectCenterScreenPoint(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath);
            var click = RunUiRuntimeClick(
                ClickAtScreenPositionArgs(screenPosition, framework: "ugui"));
            Assert.AreEqual(1, ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount);
            StringAssert.EndsWith(
                ChievfxMcpUguiRuntimeQaFixture.TopButtonPath,
                (string)Row(UguiClickSection(click), "target")["path"]!);
            Assert.AreEqual(true, UguiClickSection(click)["clicked"]);
        }

        private static Dictionary<string, object?>[] CollectControlFindRows(string framework)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var all = new List<Dictionary<string, object?>>();
            var page = 1;
            while (true)
            {
                var result = (Dictionary<string, object?>)ChievfxMcpRuntimeUiAdapterRegistry.ControlFind(
                    JObject.Parse("{'framework':'" + framework + "','page':" + page.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"))!;
                all.AddRange(Rows(result, "controls"));
                var totalPages = Convert.ToInt32(result["totalPages"], System.Globalization.CultureInfo.InvariantCulture);
                if (page >= totalPages)
                {
                    break;
                }

                page++;
            }

            return all.ToArray();
        }

        private static Dictionary<string, object?> RunExtensionTool(string toolName, string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryRunTool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { toolName, JObject.Parse(argsJson), null };
            Assert.IsTrue((bool)method!.Invoke(null, parameters)!);
            return (Dictionary<string, object?>)parameters[2]!;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpUguiExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static Dictionary<string, object?> RunUiRuntimeClick(string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            return (Dictionary<string, object?>)ChievfxMcpRuntimeUiAdapterRegistry.RuntimeClick(JObject.Parse(argsJson))!;
        }

        private static Dictionary<string, object?> RunUiRuntimeDrag(string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            return (Dictionary<string, object?>)ChievfxMcpRuntimeUiAdapterRegistry.RuntimeDrag(JObject.Parse(argsJson))!;
        }

        private static Dictionary<string, object?> RunUiRuntimeSetControlValue(string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            return (Dictionary<string, object?>)ChievfxMcpRuntimeUiAdapterRegistry.RuntimeSetControlValue(JObject.Parse(argsJson))!;
        }

        private static Dictionary<string, object?> UguiClickSection(Dictionary<string, object?> result)
        {
            return Rows(result, "frameworks").First(row => string.Equals((string?)row["framework"], "ugui", StringComparison.Ordinal));
        }

        private static Dictionary<string, object?> ReadResource(string uri)
        {
            return (Dictionary<string, object?>)ChievfxMcpUguiExtension.ReadResourceForTests(uri)!;
        }

        private static void OpenFixtureScene()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                ChievfxMcpUguiRuntimeQaFixture.ScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);
            ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount = 0;
        }

        private static void RequireUgui()
        {
            var status = ReadResource("chievfx://extensions/chievfx.ugui/status");
            if (!status.TryGetValue("ugui", out _))
            {
                Assert.Ignore((string)status["reason"]!);
            }
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> source, string key)
        {
            return ((object[])source[key]!).Cast<Dictionary<string, object?>>().ToArray();
        }

        private static string[] StringArray(Dictionary<string, object?> source, string key)
        {
            return ((object[])source[key]!).Cast<string>().ToArray();
        }

        private static Vector2 SliderScreenPoint(Slider slider, float normalizedX)
        {
            var rect = (RectTransform)slider.transform;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Vector2.Lerp(
                RectTransformUtility.WorldToScreenPoint(null, corners[0]),
                RectTransformUtility.WorldToScreenPoint(null, corners[3]),
                normalizedX);
        }

        private static Vector2 RectCenterScreenPoint(string path)
        {
            var rect = (RectTransform)GameObject.Find(path)!.transform;
            return RectTransformUtility.WorldToScreenPoint(null, rect.position);
        }

        private static string ProbeScreenArgs(Vector2 screenPosition, int? page = null)
        {
            var json = "{'x':"
                + screenPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",'y':"
                + screenPosition.y.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return page.HasValue
                ? json + ",'page':" + page.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"
                : json + "}";
        }

        private static string ScreenPositionArgs(Vector2 screenPosition)
        {
            return "{'screenPosition':{'x':"
                + screenPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",'y':"
                + screenPosition.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "}}";
        }

        private static string ClickAtScreenPositionArgs(
            Vector2 screenPosition,
            string? framework = null)
        {
            var frameworkArg = string.IsNullOrWhiteSpace(framework)
                ? string.Empty
                : ",'framework':'" + framework + "'";
            return "{'x':"
                + screenPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",'y':"
                + screenPosition.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + frameworkArg
                + "}";
        }

        private static void AddOptionalComponent(GameObject gameObject, string typeName)
        {
            var componentType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, throwOnError: false))
                .FirstOrDefault(type => type != null);
            if (componentType != null && gameObject.GetComponent(componentType) == null)
            {
                gameObject.AddComponent(componentType);
            }
        }

        private static void DisableEventSystemInputModules()
        {
            foreach (var module in SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<BaseInputModule>(includeInactive: true)))
            {
                module.enabled = false;
            }
        }
    }
}
#endif
