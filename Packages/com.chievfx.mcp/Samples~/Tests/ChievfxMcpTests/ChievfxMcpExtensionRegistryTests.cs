#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpExtensionRegistryTests
    {
        [Test]
        public void RegisterExtensionRejectsCorePromptNameCollision()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = "bad.prompt.collision." + Guid.NewGuid().ToString("N"),
                DisplayName = "Bad Prompt Collision",
            };
            descriptor.Prompts.Add(
                new ChievfxMcpPromptDescriptor
                {
                    Name = "unity-scene-review",
                    Title = "Bad duplicate",
                    Description = "Should be rejected.",
                });

            var exception = Assert.Throws<InvalidOperationException>(
                () => ChievfxMcpExtensionRegistry.RegisterExtension(descriptor));

            StringAssert.Contains("already reserved or registered", exception!.Message);
        }

        [Test]
        public void RegisterExtensionRejectsCoreResourceIdCollision()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = "bad.resource.collision." + Guid.NewGuid().ToString("N"),
                DisplayName = "Bad Resource Collision",
            };
            descriptor.Resources.Add(
                new ChievfxMcpResourceDescriptor
                {
                    Id = ChievfxMcpCoreMetadata.Resources.First().Id,
                    Uri = "chievfx://extensions/bad.resource/collision",
                    Name = "Bad duplicate",
                    Description = "Should be rejected.",
                });

            var exception = Assert.Throws<InvalidOperationException>(
                () => ChievfxMcpExtensionRegistry.RegisterExtension(descriptor));

            StringAssert.Contains("already reserved or registered", exception!.Message);
        }

        [Test]
        public void CoreMetadataHasUniqueResourceAndPromptCapabilities()
        {
            Assert.AreEqual(
                ChievfxMcpCoreMetadata.Resources.Count,
                ChievfxMcpCoreMetadata.Resources.Select(resource => resource.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(
                ChievfxMcpCoreMetadata.ResourceTemplates.Count,
                ChievfxMcpCoreMetadata.ResourceTemplates.Select(template => template.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(
                ChievfxMcpCoreMetadata.Resources.Count,
                ChievfxMcpCoreMetadata.Resources.Select(resource => resource.Uri).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(
                ChievfxMcpCoreMetadata.ResourceTemplates.Count,
                ChievfxMcpCoreMetadata.ResourceTemplates.Select(template => template.UriTemplate).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(
                ChievfxMcpCoreMetadata.Prompts.Count,
                ChievfxMcpCoreMetadata.Prompts.Select(prompt => prompt.Name).Distinct(StringComparer.Ordinal).Count());
        }

        [Test]
        public void TryReadResourceMatchesExtensionResourceTemplate()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = "test.template.match." + suffix,
                DisplayName = "Template Match Test",
                ResourceReader = uri => new { uri },
            };
            descriptor.ResourceTemplates.Add(
                new ChievfxMcpResourceTemplateDescriptor
                {
                    Id = "test-template-match-" + suffix,
                    UriTemplate = "chievfx://extensions/test.template.match." + suffix + "/items/{itemId}",
                    Name = "Template match",
                    Description = "Template matching test.",
                });

            ChievfxMcpExtensionRegistry.RegisterExtension(descriptor);

            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryReadResource",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { "chievfx://extensions/test.template.match." + suffix + "/items/example", null };
            var matched = (bool)method!.Invoke(null, parameters);

            Assert.IsTrue(matched);
            Assert.IsNotNull(parameters[1]);
        }

        [Test]
        public void FirstPartyLoaderRegistersUguiAndUiToolkitWithoutEditorAsmdefReference()
        {
            ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();

            var summaries = GetRegisteredExtensionSummaries();
            Assert.IsTrue(summaries.Any(summary => string.Equals(ReadString(summary, "id"), "chievfx.ugui", StringComparison.Ordinal)));
            Assert.IsTrue(summaries.Any(summary => string.Equals(ReadString(summary, "id"), "chievfx.uitoolkit", StringComparison.Ordinal)));
            Assert.IsTrue(summaries.Any(summary => string.Equals(ReadString(summary, "id"), "chievfx.runtime-ui", StringComparison.Ordinal)));

            var editorAsmdef = File.ReadAllText("Packages/com.chievfx.mcp/Editor/ChievfxMcp/Chievfx.Mcp.Editor.asmdef");
            StringAssert.DoesNotContain("\"Chievfx.Mcp.Extensions.Ugui\"", editorAsmdef);
            StringAssert.DoesNotContain("\"Chievfx.Mcp.Extensions.UiToolkit\"", editorAsmdef);
        }

        [Test]
        public void UguiManifestSplitsDesignAndRuntimeControlCategories()
        {
            ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();

            var manifest = JObject.FromObject(GetExtensionManifest());
            var ugui = manifest["extensions"]!
                .First(extension => string.Equals((string?)extension["id"], "chievfx.ugui", StringComparison.Ordinal));
            var tools = (JArray)ugui["tools"]!;
            var resources = (JArray)ugui["resources"]!;
            var resourceTemplates = (JArray)ugui["resourceTemplates"]!;
            var prompts = (JArray)ugui["prompts"]!;

            Assert.AreEqual("ugui-design", CategoryFor(tools, "name", "ugui-create-simple"));
            Assert.AreEqual("ugui-design", CategoryFor(tools, "name", "ugui-image-set"));
            Assert.AreEqual("ugui-runtime-control", CategoryFor(tools, "name", "ugui-runtime-probe-screen-position"));
            Assert.AreEqual("ugui-runtime-control", CategoryFor(tools, "name", "ugui-runtime-click"));
            Assert.AreEqual("ugui-runtime-control", CategoryFor(tools, "name", "ugui-runtime-drag"));
            Assert.AreEqual("ugui-runtime-control", CategoryFor(tools, "name", "ugui-runtime-set-control-value"));
            Assert.AreEqual("ugui-design", CategoryFor(resources, "id", "ugui-status"));
            Assert.AreEqual("ugui-runtime-control", CategoryFor(resources, "id", "ugui-runtime-status"));
            Assert.AreEqual("ugui-design", CategoryFor(resourceTemplates, "id", "ugui-sprite-readiness"));
            Assert.AreEqual("ugui-design", CategoryFor(prompts, "name", "ugui-authoring-review"));
        }

        [Test]
        public void ControlExtensionPromotesPlayModeSetToEssentialsAndRequired()
        {
            ChievfxMcpFirstPartyExtensionLoader.EnsureLoaded();

            CollectionAssert.Contains(ChievfxMcpToolPolicy.RequiredToolIds, "editor-playmode-set");

            var manifest = JObject.FromObject(GetExtensionManifest());
            var control = manifest["extensions"]!
                .First(extension => string.Equals((string?)extension["id"], "chievfx.control", StringComparison.Ordinal));
            var tools = (JArray)control["tools"]!;

            Assert.AreEqual("Essentials", CategoryFor(tools, "name", "editor-playmode-set"));
        }

        [Test]
        public void RuntimeUiAdapterRegistryStatusListsRegisteredAdapter()
        {
            var frameworkId = "test.runtimeui.status." + Guid.NewGuid().ToString("N");
            try
            {
                ChievfxMcpRuntimeUiAdapterRegistry.Register(new TestRuntimeUiAdapter(frameworkId, "Status Adapter", 7));

                var status = ReadExtensionResource("chievfx://extensions/chievfx.runtime-ui/status");
                var adapters = Rows(status, "adapters");

                Assert.IsTrue(adapters.Any(row => string.Equals((string)row["frameworkId"]!, frameworkId, StringComparison.Ordinal)));
                var adapter = adapters.First(row => string.Equals((string)row["frameworkId"]!, frameworkId, StringComparison.Ordinal));
                Assert.AreEqual(7, adapter["priority"]);
            }
            finally
            {
                ChievfxMcpRuntimeUiAdapterRegistry.Unregister(frameworkId);
            }
        }

        [Test]
        public void RuntimeUiMergedProbeOutsidePlayModeThrows()
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RunExtensionTool("ui-runtime-probe", "{'normalized':{'x':0.5,'y':0.5}}"));

            StringAssert.Contains("Play Mode", ex!.Message);
            StringAssert.Contains("probe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Test]
        public void RuntimeUiProbeResourceUriIsNotRegistered()
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryReadResource",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { "chievfx://extensions/chievfx.runtime-ui/runtime/probe-screen-position", null };
            Assert.IsFalse((bool)method!.Invoke(null, parameters)!);
        }

        [Test]
        public void GameViewCameraFallbackMetadataDocumentsOverlayWorkaround()
        {
            var method = typeof(ChievfxMcpBridge).GetMethod(
                "CreateGameViewCameraFallbackMetadata",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var metadata = (Dictionary<string, object?>)method!.Invoke(
                null,
                new object[]
                {
                    1920,
                    1080,
                    "Main Camera",
                    false,
                    1,
                    "attempted-temporary-screen-space-camera",
                    new[] { "GameView.m_RenderTexture was unavailable." }
                })!;

            Assert.AreEqual("camera.render", metadata["captureSource"]);
            Assert.AreEqual(false, metadata["renderTextureAvailable"]);
            Assert.AreEqual(1920, metadata["pngWidth"]);
            Assert.AreEqual(1080, metadata["pngHeight"]);
            Assert.AreEqual(1, metadata["screenSpaceOverlayCanvasCount"]);
            StringAssert.Contains("Screen Space Camera", (string)metadata["screenSpaceOverlayWorkaround"]!);
            StringAssert.Contains("screenshot-editor-window", (string)metadata["screenSpaceOverlayWorkaround"]!);
            Assert.IsTrue(((string[])metadata["warnings"]!).Any(warning => warning.Contains("m_RenderTexture")));
        }

        [Test]
        public void CurrentSceneMaterialProfileSummaryIncludesTextureLinks()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var texture = new Texture2D(1, 1) { name = "MaterialProfileSummaryTexture" };
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            Assert.IsNotNull(shader);
            var material = new Material(shader!) { name = "MaterialProfileSummaryMaterial" };
            material.mainTexture = texture;
            var gameObject = new GameObject("MaterialProfileSummaryRenderer", typeof(MeshRenderer));

            try
            {
                gameObject.GetComponent<MeshRenderer>().sharedMaterial = material;

                var summary = ReadBridgeResource("chievfx://scene/all/material-profile/summary");

                Assert.AreEqual(1, summary["materialCount"]);
                Assert.AreEqual(80, summary["maxTextureLinks"]);
                Assert.AreEqual(false, summary["textureLinksTruncated"]);
                var textureLinks = ((System.Collections.IEnumerable)summary["textureLinks"]!).Cast<Dictionary<string, object?>>().ToArray();
                Assert.AreEqual(1, textureLinks.Length);
                Assert.AreEqual("MaterialProfileSummaryMaterial", textureLinks[0]["materialName"]);
                Assert.AreEqual("MaterialProfileSummaryTexture", textureLinks[0]["textureName"]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(texture);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void CurrentSceneMaterialProfileCountsRendererReferencesBeyondLocationCap()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            Assert.IsNotNull(shader);
            var material = new Material(shader!) { name = "MaterialProfileManyRendererMaterial" };
            var gameObjects = new List<GameObject>();

            try
            {
                for (var i = 0; i < 350; i++)
                {
                    var gameObject = new GameObject("MaterialProfileRenderer" + i.ToString("000"), typeof(MeshRenderer));
                    gameObject.GetComponent<MeshRenderer>().sharedMaterial = material;
                    gameObjects.Add(gameObject);
                }

                var summary = ReadBridgeResource("chievfx://scene/all/material-profile/summary");

                Assert.AreEqual(350, summary["rendererMaterialSlotCount"]);
                Assert.AreEqual(350, summary["rendererMaterialReferenceCount"]);
                var shaderRows = ((System.Collections.IEnumerable)summary["countByShader"]!).Cast<Dictionary<string, object?>>().ToArray();
                Assert.AreEqual(1, shaderRows.Length);
                Assert.AreEqual(350, shaderRows[0]["rendererReferenceCount"]);
            }
            finally
            {
                foreach (var gameObject in gameObjects)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }

                UnityEngine.Object.DestroyImmediate(material);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static object[] GetRegisteredExtensionSummaries()
        {
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "GetRegisteredExtensionSummaries",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return ((System.Collections.IEnumerable)method!.Invoke(null, Array.Empty<object>())!).Cast<object>().ToArray();
        }

        private static object GetExtensionManifest()
        {
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "BuildManifest",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return method!.Invoke(null, Array.Empty<object>())!;
        }

        private static string? CategoryFor(JArray rows, string keyName, string keyValue)
        {
            return (string?)rows.First(row => string.Equals((string?)row[keyName], keyValue, StringComparison.Ordinal))["category"];
        }

        private static Dictionary<string, object?> ReadBridgeResource(string uri)
        {
            var method = typeof(ChievfxMcpBridge).GetMethod(
                "ReadResourceUri",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            return (Dictionary<string, object?>)method!.Invoke(null, new object[] { uri })!;
        }

        private static Dictionary<string, object?> ReadExtensionResource(string uri)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryReadResource",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { uri, null };
            Assert.AreEqual(true, method!.Invoke(null, parameters));
            return (Dictionary<string, object?>)parameters[1]!;
        }

        private static Dictionary<string, object?> RunExtensionTool(string toolName, string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryRunTool",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { toolName, JObject.Parse(argsJson), null };
            Assert.AreEqual(true, method!.Invoke(null, parameters));
            return (Dictionary<string, object?>)parameters[2]!;
        }

        private static Dictionary<string, object?> Hit(string path, int sortingOrder, int documentDepth, int hitOrder)
        {
            return new Dictionary<string, object?>
            {
                ["path"] = path,
                ["ordering"] = new Dictionary<string, object?>
                {
                    ["sortingOrder"] = sortingOrder,
                    ["documentDepth"] = documentDepth,
                    ["hitOrder"] = hitOrder,
                },
            };
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> source, string key)
        {
            return ((System.Collections.IEnumerable)source[key]!).Cast<Dictionary<string, object?>>().ToArray();
        }

        private static string? ReadString(object source, string propertyName)
        {
            return source.GetType().GetProperty(propertyName)?.GetValue(source) as string;
        }

        private sealed class TestRuntimeUiAdapter : IChievfxMcpRuntimeUiAdapter
        {
            private readonly Dictionary<string, object?>[] hits;
            private readonly bool available;

            public TestRuntimeUiAdapter(
                string frameworkId,
                string frameworkName,
                int priority,
                params Dictionary<string, object?>[] hits)
                : this(frameworkId, frameworkName, priority, available: true, hits)
            {
            }

            public TestRuntimeUiAdapter(
                string frameworkId,
                string frameworkName,
                int priority,
                bool available,
                params Dictionary<string, object?>[] hits)
            {
                FrameworkId = frameworkId;
                FrameworkName = frameworkName;
                Priority = priority;
                this.available = available;
                this.hits = hits;
            }

            public string FrameworkId { get; }

            public string FrameworkName { get; }

            public int Priority { get; }

            public bool Available => available;

            public object? Status => new Dictionary<string, object?>
            {
                ["available"] = available,
                ["testAdapter"] = true,
            };

            public IEnumerable<string> Resources => new[] { "chievfx://extensions/" + FrameworkId + "/runtime/test" };

            public object? ProbeScreenPosition(JToken request)
            {
                if (!available)
                {
                    throw new InvalidOperationException("Unavailable adapter should not be probed.");
                }

                return new Dictionary<string, object?>
                {
                    ["runtimeAvailable"] = true,
                    ["count"] = hits.Length,
                    ["stack"] = hits.Select((hit, index) =>
                    {
                        var row = new Dictionary<string, object?>(hit, StringComparer.Ordinal)
                        {
                            ["stackIndex"] = index,
                        };
                        return row;
                    }).ToArray(),
                    ["top"] = hits.FirstOrDefault(),
                    ["warnings"] = Array.Empty<string>(),
                };
            }
        }
    }
}

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpControlExtensionTests
    {
        private static readonly List<object> AddedInputDevices = new();

        [TearDown]
        public void TearDown()
        {
            Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.SetPlayModeOverrideForTests(null);
            RemoveAddedInputDevices();
        }

        [Test]
        public void ControlStatusDocumentsMutationGate()
        {
            var status = (Dictionary<string, object?>)Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.ReadResourceForTests("chievfx://extensions/chievfx.control/status")!;

            Assert.AreEqual("chievfx.control", status["extensionId"]);
            Assert.AreEqual("com.unity.inputsystem", status["package"]);
            Assert.AreEqual(true, status["requiresPlayModeForMutation"]);
            Assert.AreEqual(true, status["requiresAllowStateMutation"]);
            CollectionAssert.Contains((object[])status["tools"]!, "editor-playmode-set");
        }

        [Test]
        public void EditorPlayModeSetReportsRequestedState()
        {
            Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.SetPlayModeOverrideForTests(false);

            var result = RunControlTool("editor-playmode-set", "{'isPlaying':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual("requested", result["status"]);
            Assert.AreEqual(true, result["requestedIsPlaying"]);
        }

        [Test]
        public void ControlStatusDocumentsTouchAndResultMetadata()
        {
            RequireInputSystem();

            var status = (Dictionary<string, object?>)Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.ReadResourceForTests("chievfx://extensions/chievfx.control/status")!;
            CollectionAssert.Contains((object[])status["tools"]!, "input-control-touch-event");
            Assert.IsNotNull(status["touchscreen"]);

            var result = RunControlTool("input-control-mouse-event", "{'action':'move','screenPosition':{'x':123,'y':456},'dryRun':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual("bottom-left", Row(result, "coordinateConvention")["origin"]);
            Assert.AreEqual("screen-pixels", Row(result, "coordinateConvention")["unit"]);
            Assert.AreEqual(true, Row(result, "mutationGate")["requiresPlayMode"]);
            Assert.AreEqual(true, Row(result, "mutationGate")["requiresAllowStateMutation"]);
            Assert.AreEqual(1, result["queuedEventCount"]);
            Assert.AreEqual(123f, FloatAt(Row(Rows(result, "queuedEvents")[0], "position"), "x"), 0.001f);
        }

        [Test]
        public void ControlKeyboardTapDryRunQueuesDownAndUp()
        {
            RequireInputSystem();

            var result = RunControlTool("input-control-keyboard-event", "{'action':'tap','key':'Space','dryRun':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual("dry-run", result["status"]);
            Assert.AreEqual(2, result["queuedEventCount"]);
            var events = Rows(result, "queuedEvents");
            Assert.AreEqual("down", events[0]["action"]);
            Assert.AreEqual("up", events[1]["action"]);
        }

        [Test]
        public void ControlKeyboardMutationOutsidePlayModeIsRejected()
        {
            RequireInputSystem();
            Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.SetPlayModeOverrideForTests(false);

            var result = RunControlTool("input-control-keyboard-event", "{'action':'down','key':'Space','allowStateMutation':true}");

            Assert.AreEqual(false, result["ok"]);
            Assert.AreEqual(false, result["mutated"]);
            Assert.IsTrue(StringArray(result, "validationErrors").Any(error => error.Contains("Play Mode")));
        }

        [Test]
        public void ControlMouseMoveRequiresPositionOrDelta()
        {
            RequireInputSystem();

            var result = RunControlTool("input-control-mouse-event", "{'action':'move','dryRun':true}");

            Assert.AreEqual(false, result["ok"]);
            Assert.IsTrue(StringArray(result, "validationErrors").Any(error => error.Contains("requires position")));
        }

        [Test]
        public void ControlMouseTapWithPositionQueuesMoveThenButtonEvents()
        {
            RequireInputSystem();

            var result = RunControlTool("input-control-mouse-event", "{'action':'tap','screenPosition':{'x':123,'y':234},'dryRun':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual(3, result["queuedEventCount"]);
            var events = Rows(result, "queuedEvents");
            Assert.AreEqual("move", events[0]["action"]);
            Assert.AreEqual("down", events[1]["action"]);
            Assert.AreEqual("up", events[2]["action"]);
            Assert.AreEqual(123f, FloatAt(Row(events[0], "position"), "x"), 0.001f);
            Assert.AreEqual(234f, FloatAt(Row(events[0], "position"), "y"), 0.001f);
        }

        [Test]
        public void ControlMouseGestureDefaultsToDryRunAndInterpolates()
        {
            RequireInputSystem();

            var result = RunControlTool("input-control-mouse-gesture", "{'delta':{'x':0,'y':-120},'durationMs':32,'steps':2}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual(true, result["dryRun"]);
            Assert.AreEqual("dry-run", result["status"]);
            Assert.AreEqual(4, result["queuedEventCount"]);
            Assert.AreEqual(2, result["steps"]);
        }

        [Test]
        public void ControlKeyboardMutationSuccessReturnsCompactPayload()
        {
            RequireInputSystem();
            Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.SetPlayModeOverrideForTests(true);

            var result = RunControlTool("input-control-keyboard-event", "{'action':'down','key':'Space','allowStateMutation':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual("success", result["status"]);
            Assert.AreEqual("Keyboard", result["device"]);
            Assert.AreEqual("down", result["action"]);
            Assert.IsFalse(result.ContainsKey("tool"));
            Assert.IsFalse(result.ContainsKey("queuedEvents"));
            Assert.IsFalse(result.ContainsKey("dependency"));
        }

        [Test]
        public void ControlTouchTapDryRunQueuesBeganAndEnded()
        {
            RequireInputSystem();
            EnsureInputDevice("Touchscreen");

            var result = RunControlTool("input-control-touch-event", "{'action':'tap','touchId':7,'screenPosition':{'x':210,'y':320},'dryRun':true}");

            Assert.AreEqual(true, result["ok"]);
            Assert.AreEqual(false, result["mutated"]);
            Assert.AreEqual(2, result["queuedEventCount"]);
            Assert.AreEqual(7, result["touchId"]);
            var events = Rows(result, "queuedEvents");
            Assert.AreEqual("down", events[0]["action"]);
            Assert.AreEqual("up", events[1]["action"]);
            Assert.AreEqual(210f, FloatAt(Row(events[0], "position"), "x"), 0.001f);
            Assert.AreEqual("bottom-left", Row(result, "coordinateConvention")["origin"]);
        }

        private static Dictionary<string, object?> RunControlTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static void RequireInputSystem()
        {
            var status = (Dictionary<string, object?>)Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension.ReadResourceForTests("chievfx://extensions/chievfx.control/status")!;
            if (!Equals(status["available"], true))
            {
                Assert.Ignore("Input System package/types are not loaded in this project.");
            }
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> row, string name)
        {
            return ((object[])row[name]!).Cast<Dictionary<string, object?>>().ToArray();
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> row, string name)
        {
            return (Dictionary<string, object?>)row[name]!;
        }

        private static string[] StringArray(Dictionary<string, object?> row, string name)
        {
            return ((object[])row[name]!).Cast<string>().ToArray();
        }

        private static float FloatAt(Dictionary<string, object?> row, string name)
        {
            return Convert.ToSingle(row[name], System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void EnsureInputDevice(string layout)
        {
            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            Assert.IsNotNull(inputSystemType);
            var deviceType = FindType("UnityEngine.InputSystem." + layout);
            Assert.IsNotNull(deviceType);
            if (deviceType!.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) != null)
            {
                return;
            }

            var device = AddInputDevice(inputSystemType!, deviceType, layout);
            if (device != null)
            {
                AddedInputDevices.Add(device);
            }
        }

        private static object? AddInputDevice(Type inputSystemType, Type deviceType, string layout)
        {
            var addDeviceMethods = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "AddDevice")
                .Select(method => new { Method = method, Parameters = method.GetParameters() })
                .ToArray();

            var layoutOverload = addDeviceMethods
                .Where(candidate =>
                    !candidate.Method.IsGenericMethodDefinition
                    && candidate.Parameters.Length == 3
                    && candidate.Parameters.All(parameter => parameter.ParameterType == typeof(string)))
                .Select(candidate => candidate.Method)
                .FirstOrDefault();
            if (layoutOverload != null)
            {
                return layoutOverload.Invoke(null, new object?[] { layout, null, null });
            }

            var genericStringOverload = addDeviceMethods
                .Where(candidate =>
                    candidate.Method.IsGenericMethodDefinition
                    && candidate.Method.GetGenericArguments().Length == 1
                    && candidate.Parameters.Length == 1
                    && candidate.Parameters[0].ParameterType == typeof(string))
                .Select(candidate => candidate.Method)
                .FirstOrDefault();
            if (genericStringOverload != null)
            {
                return genericStringOverload.MakeGenericMethod(deviceType).Invoke(null, new object?[] { layout });
            }

            var availableSignatures = string.Join(", ", addDeviceMethods.Select(candidate => candidate.Method.ToString()));
            Assert.Fail("Supported InputSystem.AddDevice overload not found. Available: " + availableSignatures);
            return null;
        }

        private static void RemoveAddedInputDevices()
        {
            if (AddedInputDevices.Count == 0)
            {
                return;
            }

            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            var removeDevice = inputSystemType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "RemoveDevice" && method.GetParameters().Length == 1);
            foreach (var device in AddedInputDevices.ToArray())
            {
                try
                {
                    removeDevice?.Invoke(null, new[] { device });
                }
                catch (TargetInvocationException)
                {
                }
            }

            AddedInputDevices.Clear();
        }

        private static Type? FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }
    }
}
