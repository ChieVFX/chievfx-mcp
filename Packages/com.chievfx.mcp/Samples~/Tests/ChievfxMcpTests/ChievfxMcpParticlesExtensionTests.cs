#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chievfx.Mcp.Extensions.Particles;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpParticlesExtensionTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            StageUtility.GoToMainStage();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset("Assets/Editor/ChievfxMcpTests/GeneratedParticles");
            AssetDatabase.DeleteAsset("Assets/Editor/ChievfxMcpTests/GeneratedParticlePrefab.prefab");
        }

        [Test]
        public void StatusReportsParticlesCapability()
        {
            var status = Resource("chievfx://extensions/chievfx.particles/status");

            Assert.AreEqual("chievfx.particles", status["extensionId"]);
            Assert.IsTrue(status.ContainsKey("packageInstalled"));
            Assert.IsNotEmpty((string[])status["prompts"]!);
        }

        [Test]
        public void ManifestAdvertisesRuntimeAvailableParticlesCapabilities()
        {
            var status = Resource("chievfx://extensions/chievfx.particles/status");
            if (!Equals(true, status["available"]))
            {
                Assert.Ignore("ParticleSystem extension is unavailable in this project.");
            }

            var manifestPath = Path.Combine("Temp", "ChievfxMcpParticlesExtensionManifestTest.json");
            ChievfxMcpExtensionRegistry.ExportManifest(manifestPath);

            var manifest = File.ReadAllText(manifestPath);
            StringAssert.Contains("\"id\": \"chievfx.particles\"", manifest);
            StringAssert.Contains("\"name\": \"particles-system-create\"", manifest);
            StringAssert.Contains("\"name\": \"particles-module-patch\"", manifest);
            StringAssert.Contains("\"uri\": \"chievfx://extensions/chievfx.particles/systems\"", manifest);
            StringAssert.Contains("\"uriTemplate\": \"chievfx://extensions/chievfx.particles/system/{pathOrInstanceId}\"", manifest);
        }

        [Test]
        public void CreatePresetAndResourcesExposeCompactSystemDetail()
        {
            RequireParticles();

            var created = RunTool(
                "particles-system-create",
                "{'name':'SparkFx','preset':'spark-burst','position':{'x':1,'y':2,'z':3}}");
            var target = Row(created, "target");

            Assert.AreEqual("SparkFx", target["name"]);
            Assert.AreEqual("SparkFx", target["path"]);
            Assert.IsTrue(((string)created["detailUri"]!).Contains("chievfx://extensions/chievfx.particles/system/"));

            var systems = Resource("chievfx://extensions/chievfx.particles/systems");
            Assert.AreEqual(1, systems["count"]);
            var rows = ((object[])systems["systems"]!).Cast<Dictionary<string, object?>>().ToArray();
            Assert.AreEqual("SparkFx", rows[0]["name"]);

            var detail = Resource((string)created["detailUri"]!);
            Assert.AreEqual("SparkFx", Row(detail, "target")["name"]);
            Assert.IsNotNull(Row(Row(detail, "target"), "modules"));
        }

        [Test]
        public void ModulePatchRejectsUnknownFieldsAndAppliesAllowlistedMainField()
        {
            RequireParticles();
            RunTool("particles-system-create", "{'name':'PatchFx'}");

            var patched = RunTool(
                "particles-module-patch",
                "{'targetPath':'PatchFx','module':'main','fields':{'loop':true,'maxParticles':64}}");
            var main = Row(Row(patched, "target"), "main");
            Assert.AreEqual(true, main["loop"]);
            Assert.AreEqual(64, main["maxParticles"]);

            Assert.Throws<ArgumentException>(() => RunTool(
                "particles-module-patch",
                "{'targetPath':'PatchFx','module':'main','fields':{'unknownField':1}}"));
        }

        [Test]
        public void ModulePatchDryRunDoesNotMutateDirtySceneAndCreateRegistersUndo()
        {
            RequireParticles();

            RunTool("particles-system-create", "{'name':'UndoCreateFx'}");
            Assert.NotNull(GameObject.Find("UndoCreateFx"));
            Undo.PerformUndo();
            Assert.IsNull(GameObject.Find("UndoCreateFx"));

            RunTool("particles-system-create", "{'name':'DirtyFx'}");
            var system = GameObject.Find("DirtyFx")!.GetComponent<ParticleSystem>();
            var scenePath = "Assets/Editor/ChievfxMcpTests/GeneratedParticles/DirtyParticlesScene.unity";
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath));
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var dryRun = RunTool(
                "particles-module-patch",
                "{'targetPath':'DirtyFx','module':'main','fields':{'maxParticles':512},'dryRun':true}");
            Assert.AreEqual(true, dryRun["dryRun"]);
            Assert.AreEqual(256, system.main.maxParticles);
            Assert.IsFalse(SceneManager.GetActiveScene().isDirty);

            var applied = RunTool(
                "particles-module-patch",
                "{'targetPath':'DirtyFx','module':'main','fields':{'maxParticles':512},'dryRun':false}");
            Assert.AreEqual(false, applied["dryRun"]);
            Assert.AreEqual(512, system.main.maxParticles);
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);
        }

        [Test]
        public void SummaryAndDetailResourcesExposeCaps()
        {
            RequireParticles();
            for (var i = 0; i < 98; i++)
            {
                new GameObject("SummaryFx" + i.ToString("D3")).AddComponent<ParticleSystem>();
            }

            var systems = Resource("chievfx://extensions/chievfx.particles/systems");
            Assert.AreEqual(96, systems["count"]);
            Assert.AreEqual(true, systems["capped"]);
            Assert.AreEqual(96, systems["maxRows"]);
            Assert.AreEqual(96, Rows(systems, "systems").Length);

            var detailRoot = new GameObject("DetailRoot");
            detailRoot.AddComponent<ParticleSystem>();
            for (var i = 0; i < 26; i++)
            {
                var child = new GameObject("DetailChild" + i.ToString("D2"));
                child.transform.SetParent(detailRoot.transform);
                child.AddComponent<ParticleSystem>();
            }

            var detail = Resource("chievfx://extensions/chievfx.particles/system/DetailRoot");
            Assert.AreEqual(24, Rows(detail, "children").Length);
            Assert.AreEqual(true, detail["childrenCapped"]);
        }

        [Test]
        public void NamedPresetRecipesApplyExpectedModuleValues()
        {
            RequireParticles();

            RunTool("particles-system-create", "{'name':'PresetFx'}");

            var spark = RunTool("particles-preset-apply", "{'targetPath':'PresetFx','preset':'spark-burst'}");
            Assert.AreEqual("spark-burst", spark["preset"]);
            Assert.AreEqual(128, Row(Row(spark, "target"), "main")["maxParticles"]);
            Assert.AreEqual(0f, Row(Row(spark, "target"), "emission")["rateOverTime"]);

            var smoke = RunTool("particles-preset-apply", "{'targetPath':'PresetFx','preset':'smoke-puff'}");
            Assert.AreEqual(96, Row(Row(smoke, "target"), "main")["maxParticles"]);
            Assert.AreEqual("Sphere", Row(Row(smoke, "target"), "shape")["shapeType"]);

            var magic = RunTool("particles-preset-apply", "{'targetPath':'PresetFx','preset':'magic-glow'}");
            Assert.AreEqual(true, Row(Row(magic, "target"), "main")["loop"]);
            Assert.AreEqual(192, Row(Row(magic, "target"), "main")["maxParticles"]);
        }

        [Test]
        public void RendererSetAssignsMaterialAndPreviewSimulates()
        {
            RequireParticles();
            RunTool("particles-system-create", "{'name':'PreviewFx','preset':'magic-glow'}");
            var materialPath = CreateMaterial("ParticlePreviewMaterial.mat");

            var rendered = RunTool(
                "particles-renderer-set",
                "{'targetPath':'PreviewFx','materialPath':'" + materialPath + "','renderMode':'Billboard','sortingOrder':5}");
            var renderer = Row(rendered, "renderer");
            Assert.AreEqual(materialPath, renderer["materialPath"]);
            Assert.AreEqual(5, renderer["sortingOrder"]);

            var preview = RunTool(
                "particles-preview-control",
                "{'targetPath':'PreviewFx','action':'simulate','seconds':0.2,'restart':true}");
            Assert.AreEqual("simulate", preview["action"]);
            Assert.IsNotNull(Row(preview, "preview"));
        }

        [Test]
        public void PrefabStageResourcesAndToolsOperateOnCurrentPrefabRoot()
        {
            RequireParticles();
            var prefabRoot = new GameObject("ParticlePrefabRoot");
            prefabRoot.AddComponent<ParticleSystem>();
            var prefabPath = "Assets/Editor/ChievfxMcpTests/GeneratedParticlePrefab.prefab";
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            UnityEngine.Object.DestroyImmediate(prefabRoot);

            PrefabStageUtility.OpenPrefab(prefabPath);
            var systems = Resource("chievfx://extensions/chievfx.particles/systems");
            Assert.AreEqual("prefab-stage", Row(systems, "stage")["kind"]);
            Assert.AreEqual(prefabPath, Row(systems, "stage")["prefabAssetPath"]);
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.NotNull(stage);
            var stageRootName = stage!.prefabContentsRoot.name;
            Assert.IsTrue(Rows(systems, "systems").Any(row => Equals(stageRootName, row["path"])));

            var created = RunTool(
                "particles-system-create",
                "{'name':'NestedPrefabFx','parentPath':'" + stageRootName + "','preset':'spark-burst'}");
            Assert.AreEqual(stageRootName + "/NestedPrefabFx", Row(created, "target")["path"]);
        }

        [Test]
        public void SmokeFixtureBuildsPreviewableCameraComposedScene()
        {
            RequireParticles();
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
            ChievfxMcpParticlesQaFixture.BuildScene(saveSceneAsset: false);
            Assert.IsNotNull(GameObject.Find(ChievfxMcpParticlesQaFixture.CameraName)!.GetComponent<Camera>());

            var preview = RunTool(
                "particles-preview-control",
                "{'targetPath':'" + ChievfxMcpParticlesQaFixture.MagicPath + "','action':'simulate','seconds':0.6,'restart':true}");
            var previewState = Row(preview, "preview");
            Assert.Greater(Convert.ToInt32(previewState["particleCount"]), 0);

            var hints = ((object[])preview["screenshotReviewHints"]!).Cast<string>().ToArray();
            Assert.IsTrue(hints.Any(hint => hint.Contains("screenshot-editor-window", StringComparison.Ordinal)));
            Assert.IsTrue(hints.Any(hint => hint.Contains("screenshot-game-view", StringComparison.Ordinal)));
            Assert.IsTrue(hints.Any(hint => hint.Contains("screenshot-camera", StringComparison.Ordinal)));
#endif
        }

        private static Dictionary<string, object?> Resource(string uri)
        {
            return (Dictionary<string, object?>)ChievfxMcpParticlesExtension.ReadResourceForTests(uri)!;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpParticlesExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> source, string key)
        {
            return ((object[])source[key]!).Cast<Dictionary<string, object?>>().ToArray();
        }

        private static string CreateMaterial(string filename)
        {
            var folder = "Assets/Editor/ChievfxMcpTests/GeneratedParticles";
            Directory.CreateDirectory(folder);
            var materialPath = folder + "/" + filename;
            AssetDatabase.CreateAsset(new Material(Shader.Find("Sprites/Default")), materialPath);
            AssetDatabase.ImportAsset(materialPath);
            return materialPath;
        }

        private static void RequireParticles()
        {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
            if (Type.GetType("UnityEngine.ParticleSystem, UnityEngine.ParticleSystemModule") == null)
            {
                Assert.Ignore("UnityEngine.ParticleSystem type is not loaded in this project.");
            }
#else
            Assert.Ignore("com.unity.modules.particlesystem is not available in this project.");
#endif
        }
    }
}
