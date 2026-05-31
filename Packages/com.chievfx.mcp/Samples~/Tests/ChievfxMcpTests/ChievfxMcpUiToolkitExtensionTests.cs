#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Chievfx.Mcp.Extensions.UiToolkit;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpUiToolkitExtensionTests
    {
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [Test]
        public void StatusResourceIsRegisteredAndReportsRuntimeSurface()
        {
            var status = (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.ReadResourceForTests("chievfx://extensions/chievfx.uitoolkit/status")!;

            Assert.AreEqual("uitoolkit", status["framework"]);
            if (status.ContainsKey("reason") && !status.ContainsKey("uitoolkit"))
            {
                return;
            }

            Assert.IsNotNull(status["context"]);
            Assert.IsNotNull(status["uitoolkit"]);
            Assert.IsNotNull(status["currentHierarchy"]);
            Assert.AreEqual(true, status["runtimeOnly"]);
        }

        [Test]
        public void RuntimeReadsAreGatedOutsidePlayModeAndIncludeCoordinateShape()
        {
            RequireUiToolkit();

            var status = (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.ReadResourceForTests("chievfx://extensions/chievfx.uitoolkit/runtime/status")!;

            Assert.AreEqual(false, status["runtimeAvailable"]);
            Assert.AreEqual(false, status["playMode"]);
            Assert.AreEqual(0, status["documentCount"]);
            Assert.AreEqual(0, status["panelCount"]);
            Assert.IsTrue(StringArray(status, "warnings").Any(warning => warning.Contains("Play Mode")));
            var coordinateConvention = Row(status, "coordinateConvention");
            Assert.AreEqual("bottom-left", coordinateConvention["origin"]);
            Assert.AreEqual(true, coordinateConvention["uiToolkitYInverted"]);
            Assert.IsNotNull(coordinateConvention["uiToolkitScreenPosition"]);

            var panels = ReadResource("chievfx://extensions/chievfx.uitoolkit/runtime/panels");
            Assert.AreEqual(false, panels["runtimeAvailable"]);
            Assert.AreEqual(0, panels["count"]);

            var visibleTree = ReadResource("chievfx://extensions/chievfx.uitoolkit/runtime/visible-tree");
            Assert.AreEqual(false, visibleTree["runtimeAvailable"]);
            Assert.AreEqual(0, visibleTree["count"]);
        }

        [Test]
        public void RuntimeProbeOutsidePlayModeReturnsStackShapeAndWarning()
        {
            RequireUiToolkit();

            var probe = (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.RunToolForTests(
                "uitoolkit-runtime-probe-screen-position",
                "{'normalized':{'x':0.5,'y':0.5}}")!;

            Assert.AreEqual(false, probe["runtimeAvailable"]);
            Assert.AreEqual(0, probe["count"]);
            Assert.IsEmpty((object[])probe["stack"]!);
            Assert.IsTrue(StringArray(probe, "warnings").Any(warning => warning.Contains("Play Mode")));
            var coordinateConvention = Row(probe, "coordinateConvention");
            Assert.AreEqual("bottom-left", coordinateConvention["origin"]);
            Assert.AreEqual(true, coordinateConvention["uiToolkitYInverted"]);
        }

        [Test]
        public void RuntimeInteractionToolIsRegisteredWithMutationScope()
        {
            RequireUiToolkit();

            var status = ReadResource("chievfx://extensions/chievfx.uitoolkit/status");

            Assert.AreEqual(true, status["runtimeOnly"]);
            Assert.IsNotNull(status["uitoolkit"]);
        }

        [Test]
        public void RuntimeInteractionMutationIsDeniedOutsidePlayMode()
        {
            RequireUiToolkit();

            var ex = Assert.Throws<InvalidOperationException>(() => ChievfxMcpUiToolkitExtension.RunToolForTests(
                "uitoolkit-runtime-interact",
                "{'action':'focus','name':'missing','dryRun':false,'allowStateMutation':true}"));

            StringAssert.Contains("Play Mode", ex!.Message);
        }

        [UnityTest]
        public IEnumerator RuntimeQaFixturePanelsExposeSortingTargetDisplayAndDocuments()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var panels = ReadResource("chievfx://extensions/chievfx.uitoolkit/runtime/panels");
            Assert.AreEqual(true, panels["runtimeAvailable"]);
            Assert.GreaterOrEqual((int)panels["count"]!, 3);

            var panelRows = Rows(panels, "panels");
            var documentNames = panelRows
                .SelectMany(panel => Rows(panel, "documents"))
                .Select(row => (string)row["name"]!)
                .ToArray();
            CollectionAssert.Contains(documentNames, ChievfxMcpUiToolkitRuntimeQaFixture.BottomDocumentName);
            CollectionAssert.Contains(documentNames, ChievfxMcpUiToolkitRuntimeQaFixture.TopDocumentName);
            CollectionAssert.Contains(documentNames, ChievfxMcpUiToolkitRuntimeQaFixture.SecondaryDocumentName);

            var topPanel = panelRows.First(panel => Rows(panel, "documents").Any(document => string.Equals((string)document["name"]!, ChievfxMcpUiToolkitRuntimeQaFixture.TopDocumentName, StringComparison.Ordinal)));
            var topDocument = Rows(topPanel, "documents").First(document => string.Equals((string)document["name"]!, ChievfxMcpUiToolkitRuntimeQaFixture.TopDocumentName, StringComparison.Ordinal));
            Assert.AreEqual(100, topDocument["sortingOrder"]);

            var secondaryPanel = panelRows.First(panel => Rows(panel, "documents").Any(document => string.Equals((string)document["name"]!, ChievfxMcpUiToolkitRuntimeQaFixture.SecondaryDocumentName, StringComparison.Ordinal)));
            Assert.AreEqual(1, Row(secondaryPanel, "panelSettings")["targetDisplay"]);
        }

        [UnityTest]
        public IEnumerator RuntimeQaFixtureVisibleTreeCapsAndInteractablesRespectVisibility()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var visibleTree = ReadResource("chievfx://extensions/chievfx.uitoolkit/runtime/visible-tree");
            Assert.AreEqual(true, visibleTree["runtimeAvailable"]);
            Assert.AreEqual(256, visibleTree["maxRowsPerDocument"]);

            var bottomDocument = Rows(visibleTree, "documents")
                .First(document => string.Equals((string)document["name"]!, ChievfxMcpUiToolkitRuntimeQaFixture.BottomDocumentName, StringComparison.Ordinal));
            Assert.AreEqual(true, bottomDocument["truncated"]);
            Assert.LessOrEqual(Rows(bottomDocument, "elements").Length, 256);

            var bottomNames = Rows(bottomDocument, "elements").Select(row => (string?)row["name"]).ToArray();
            CollectionAssert.Contains(bottomNames, ChievfxMcpUiToolkitRuntimeQaFixture.DisabledControlName);
            CollectionAssert.Contains(bottomNames, ChievfxMcpUiToolkitRuntimeQaFixture.PickingIgnoredName);
            CollectionAssert.DoesNotContain(bottomNames, ChievfxMcpUiToolkitRuntimeQaFixture.HiddenControlName);
            CollectionAssert.DoesNotContain(bottomNames, ChievfxMcpUiToolkitRuntimeQaFixture.VisibilityHiddenControlName);

            var interactables = ReadResource("chievfx://extensions/chievfx.uitoolkit/runtime/interactables");
            var interactableNames = Rows(interactables, "interactables").Select(row => (string?)row["name"]).ToArray();
            CollectionAssert.Contains(interactableNames, ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName);
            CollectionAssert.Contains(interactableNames, ChievfxMcpUiToolkitRuntimeQaFixture.ToggleName);
            CollectionAssert.DoesNotContain(interactableNames, ChievfxMcpUiToolkitRuntimeQaFixture.DisabledControlName);
            CollectionAssert.DoesNotContain(interactableNames, ChievfxMcpUiToolkitRuntimeQaFixture.PickingIgnoredName);
        }

        [UnityTest]
        public IEnumerator RuntimeProbeConvertsBottomLeftScreenYAndReturnsTopToBottomPickStack()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var screenPoint = NormalizedScreenPoint(ChievfxMcpUiToolkitRuntimeQaFixture.CenterProbeNormalized);
            var probe = RunTool("uitoolkit-runtime-probe-screen-position", NormalizedPositionArgs(ChievfxMcpUiToolkitRuntimeQaFixture.CenterProbeNormalized, maxRows: 12));

            Assert.AreEqual(true, probe["runtimeAvailable"]);
            Assert.AreEqual("bottom-left", Row(probe, "coordinateConvention")["origin"]);
            Assert.AreEqual(true, Row(probe, "coordinateConvention")["uiToolkitYInverted"]);
            Assert.AreEqual(screenPoint.y, FloatAt(Row(Row(probe, "coordinateConvention"), "screenPosition"), "y"), 0.1f);
            Assert.AreEqual(Screen.height - screenPoint.y, FloatAt(Row(Row(probe, "coordinateConvention"), "uiToolkitScreenPosition"), "y"), 0.1f);

            var stack = Rows(probe, "stack");
            Assert.GreaterOrEqual(stack.Length, 2, string.Join("; ", StringArray(probe, "warnings")));

            var top = Row(probe, "top");
            StringAssert.Contains(ChievfxMcpUiToolkitRuntimeQaFixture.TopHitName, (string)top["path"]!);
            Assert.AreEqual("uitoolkit", top["framework"]);
            Assert.AreEqual("IPanel.PickAll", Row(top, "raycastResult")["source"]);
            Assert.AreEqual(100, Row(top, "ordering")["sortingOrder"]);

            var topIndex = Array.FindIndex(stack, row => ((string)row["path"]!).Contains(ChievfxMcpUiToolkitRuntimeQaFixture.TopHitName, StringComparison.Ordinal));
            var bottomIndex = Array.FindIndex(stack, row => ((string)row["path"]!).Contains(ChievfxMcpUiToolkitRuntimeQaFixture.BottomHitName, StringComparison.Ordinal));
            Assert.AreEqual(0, topIndex);
            Assert.Greater(bottomIndex, topIndex);
        }

        [UnityTest]
        public IEnumerator RuntimeProbeMaxRowsTruncatesStackButKeepsPanelSummary()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var probe = RunTool("uitoolkit-runtime-probe-screen-position", NormalizedPositionArgs(ChievfxMcpUiToolkitRuntimeQaFixture.CenterProbeNormalized, maxRows: 1));

            Assert.AreEqual(1, probe["count"]);
            Assert.AreEqual(1, Rows(probe, "stack").Length);
            Assert.AreEqual(true, probe["truncated"]);
            Assert.GreaterOrEqual(Rows(probe, "panels").Length, 3);
        }

        [UnityTest]
        public IEnumerator RuntimeInteractionDryRunDefaultsToNoMutation()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var textField = (TextField)ChievfxMcpUiToolkitRuntimeQaFixture.FindElement(ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName);
            var before = textField.value;

            var interact = RunTool(
                "uitoolkit-runtime-interact",
                "{'action':'setValue','name':'" + ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName + "','value':'changed by dry run'}");

            Assert.AreEqual(true, interact["dryRun"]);
            Assert.AreEqual(before, textField.value);
            Assert.IsTrue(StringArray(interact, "warnings").Any(warning => warning.Contains("dryRun")));
            Assert.AreEqual(before, Row(interact, "targetStateAfter")["value"]);
        }

        [UnityTest]
        public IEnumerator RuntimeInteractionRequiresAllowStateMutationForRealDispatch()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var ex = Assert.Throws<InvalidOperationException>(() => ChievfxMcpUiToolkitExtension.RunToolForTests(
                "uitoolkit-runtime-interact",
                "{'action':'focus','name':'" + ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName + "','dryRun':false}"));

            StringAssert.Contains("allowStateMutation", ex!.Message);
        }

        [UnityTest]
        public IEnumerator RuntimeInteractionSetValueMutatesStandardControlWhenAllowed()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            var textField = (TextField)ChievfxMcpUiToolkitRuntimeQaFixture.FindElement(ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName);
            Assert.AreEqual("runtime value", textField.value);

            var interact = RunTool(
                "uitoolkit-runtime-interact",
                "{'action':'setValue','name':'" + ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName + "','value':'changed by tool','invokeCallbacks':false,'dryRun':false,'allowStateMutation':true}");

            Assert.AreEqual(false, interact["dryRun"]);
            Assert.AreEqual("changed by tool", textField.value);
            Assert.AreEqual("changed by tool", Row(interact, "targetStateAfter")["value"]);
            Assert.AreEqual(ChievfxMcpUiToolkitRuntimeQaFixture.TextFieldName, Row(interact, "target")["name"]);
        }

        [UnityTest]
        public IEnumerator MergedRuntimeUiProbeIncludesUiToolkitRowsWithUguiCompatibleFields()
        {
            RequireUiToolkit();
            OpenFixtureScene();

            yield return new EnterPlayMode();
            yield return PopulateAndSettleUiToolkit();

            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var probe = RunExtensionTool("runtime-ui-probe-screen-position", NormalizedPositionArgs(ChievfxMcpUiToolkitRuntimeQaFixture.CenterProbeNormalized, maxRows: 8));
            var coordinateConvention = Row(probe, "coordinateConvention");
            var input = Row(probe, "input");
            var expected = NormalizedScreenPoint(ChievfxMcpUiToolkitRuntimeQaFixture.CenterProbeNormalized);
            Assert.AreEqual("bottom-left", coordinateConvention["origin"]);
            Assert.AreEqual(expected.x, FloatAt(input, "x"), 0.25f);
            Assert.AreEqual(expected.y, FloatAt(input, "y"), 0.25f);

            var stack = Rows(probe, "stack");
            var top = stack.First(row => string.Equals((string)row["frameworkId"]!, "uitoolkit", StringComparison.Ordinal));

            Assert.AreEqual("UI Toolkit", top["frameworkName"]);
            Assert.AreEqual(100, top["adapterPriority"]);
            Assert.AreEqual(0, top["mergedStackIndex"]);
            Assert.AreEqual("uitoolkit", top["framework"]);
            Assert.IsNotNull(top["path"]);
            Assert.IsNotNull(top["input"]);
            Assert.IsNotNull(top["ordering"]);
            Assert.IsNotNull(top["raycastResult"]);
            Assert.IsNotNull(top["worldBound"]);
            Assert.IsNotNull(top["panelRef"]);
            Assert.IsNotNull(top["documentRefs"]);
        }

        private static void RequireUiToolkit()
        {
            var status = (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.ReadResourceForTests("chievfx://extensions/chievfx.uitoolkit/status")!;
            if (!status.TryGetValue("uitoolkit", out _))
            {
                Assert.Ignore((string)status["reason"]!);
            }
        }

        private static void OpenFixtureScene()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                ChievfxMcpUiToolkitRuntimeQaFixture.ScenePath,
                UnityEditor.SceneManagement.OpenSceneMode.Single);
        }

        private static IEnumerator PopulateAndSettleUiToolkit()
        {
            ChievfxMcpUiToolkitRuntimeQaFixture.PopulateRuntimeDocuments();
            yield return null;
            yield return null;
            yield return null;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static Dictionary<string, object?> ReadResource(string uri)
        {
            return (Dictionary<string, object?>)ChievfxMcpUiToolkitExtension.ReadResourceForTests(uri)!;
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

        private static float FloatAt(Dictionary<string, object?> source, string key)
        {
            return Convert.ToSingle(source[key], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Vector2 NormalizedScreenPoint(Vector2 normalized)
        {
            return new Vector2(normalized.x * Screen.width, normalized.y * Screen.height);
        }

        private static string NormalizedPositionArgs(Vector2 normalized, int? maxRows = null)
        {
            var json = "{'normalized':{'x':"
                + normalized.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",'y':"
                + normalized.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "}";
            return maxRows.HasValue
                ? json + ",'maxRows':" + maxRows.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"
                : json + "}";
        }
    }
}
