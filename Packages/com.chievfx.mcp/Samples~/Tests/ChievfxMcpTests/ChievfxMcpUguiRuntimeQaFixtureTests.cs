#nullable enable
#if CHIEVFX_MCP_HAS_UGUI
using System;
using System.Collections.Generic;
using System.Linq;
using Chievfx.Mcp.Extensions.Ugui;
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

            Assert.AreEqual(true, status["runtimeReadsInScope"]);
            Assert.AreEqual(true, status["runtimeInteractionInScope"]);
            CollectionAssert.Contains((object[])status["tools"]!, "ugui-runtime-probe-screen-position");
            CollectionAssert.Contains((object[])status["tools"]!, "ugui-runtime-click");
            CollectionAssert.Contains((object[])status["tools"]!, "ugui-runtime-set-control-value");
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

            var interactables = ReadResource("chievfx://extensions/chievfx.ugui/runtime/interactables");
            var rows = Rows(interactables, "interactables");
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.SliderPath, StringComparison.Ordinal)));
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.TogglePath, StringComparison.Ordinal)));
            Assert.IsTrue(rows.Any(row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.ScrollRectPath, StringComparison.Ordinal)));
            Assert.IsFalse(rows.Any(row => ((string)row["path"]!).Contains("HiddenInactiveButton", StringComparison.Ordinal)));
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

            var probe = RunTool(
                "ugui-runtime-probe-screen-position",
                ScreenPositionArgs(RectCenterScreenPoint(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath)));
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual("bottom-left", Row(probe, "coordinateConvention")["origin"]);
            var stack = Rows(probe, "stack");
            Assert.GreaterOrEqual(stack.Length, 2, string.Join("; ", StringArray(probe, "warnings")));

            var top = Row(probe, "top");
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.TopButtonPath, (string)top["path"]!);
            var bottomIndex = Array.FindIndex(stack, row => ((string)row["path"]!).EndsWith(ChievfxMcpUguiRuntimeQaFixture.BottomButtonPath, StringComparison.Ordinal));
            Assert.GreaterOrEqual(bottomIndex, 1);
            Assert.AreEqual(100, Row(top, "sorting")["sortingOrder"]);
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

            var probe = RunTool("ugui-runtime-probe-screen-position", "{'normalized':{'x':1.2,'y':0.5}}");
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual(0, probe["count"]);
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

            var probe = RunTool("ugui-runtime-probe-screen-position", "{'normalized':{'x':0.5,'y':0.5}}");
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual(0, probe["count"]);
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

            var disabledProbe = RunTool(
                "ugui-runtime-probe-screen-position",
                ScreenPositionArgs(RectCenterScreenPoint(ChievfxMcpUguiRuntimeQaFixture.DisabledButtonPath)));
            var disabledTop = Row(disabledProbe, "top");
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.DisabledButtonPath, (string)disabledTop["path"]!);
            Assert.AreEqual(false, disabledTop["interactable"]);

            var hiddenProbe = RunTool("ugui-runtime-probe-screen-position", "{'normalized':{'x':0.1,'y':0.1}}");
            var hiddenStack = Rows(hiddenProbe, "stack");
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

            var dryRun = RunTool("ugui-runtime-click", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TopButtonPath + "','dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual(0, ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount);
            Assert.IsNotNull(dryRun["intendedHandler"]);

            var click = RunTool("ugui-runtime-click", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TopButtonPath + "','allowStateMutation':true}");
            Assert.AreEqual(1, ChievfxMcpUguiRuntimeQaFixture.ButtonClickCount);
            Assert.IsTrue(click.ContainsKey("selectedObjectBefore"));
            Assert.IsTrue(click.ContainsKey("selectedObjectAfter"));

            var slider = GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.SliderPath)!.GetComponent<Slider>();
            var setSlider = RunTool("ugui-runtime-set-control-value", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.SliderPath + "','value':0.25,'invokeCallbacks':false,'allowStateMutation':true}");
            Assert.AreEqual(0.25f, slider.value, 0.001f);
            Assert.IsNotNull(setSlider["targetStateBefore"]);
            Assert.IsNotNull(setSlider["targetStateAfter"]);

            var toggle = GameObject.Find(ChievfxMcpUguiRuntimeQaFixture.TogglePath)!.GetComponent<Toggle>();
            RunTool("ugui-runtime-set-control-value", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TogglePath + "','value':false,'invokeCallbacks':false,'allowStateMutation':true}");
            Assert.AreEqual(false, toggle.isOn);

            var select = RunTool("ugui-runtime-select", "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.TogglePath + "','allowStateMutation':true}");
            StringAssert.EndsWith(ChievfxMcpUguiRuntimeQaFixture.TogglePath, (string)Row(select, "selectedObjectAfter")["path"]!);

            var dragStart = SliderScreenPoint(slider, 0.1f);
            var dragEnd = SliderScreenPoint(slider, 0.9f);
            RunTool(
                "ugui-runtime-drag",
                "{'targetPath':'" + ChievfxMcpUguiRuntimeQaFixture.SliderPath + "','startScreenPosition':{'x':" + dragStart.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'y':" + dragStart.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},'endScreenPosition':{'x':" + dragEnd.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",'y':" + dragEnd.y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},'allowStateMutation':true}");
            Assert.Greater(slider.value, 0.7f);
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpUguiExtension.RunToolForTests(toolName, argsJson)!;
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
            if (!Equals(status["available"], true))
            {
                Assert.Ignore((string)status["dependencyReason"]!);
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

        private static string ScreenPositionArgs(Vector2 screenPosition)
        {
            return "{'screenPosition':{'x':"
                + screenPosition.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",'y':"
                + screenPosition.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "}}";
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
