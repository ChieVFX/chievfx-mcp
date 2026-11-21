#nullable enable
using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor.Tests
{
    // A prefab its own editor scripts dirty while loading could be closed only by saving it; scene-list-available did not
    // say which scenes are in the build.
    public sealed class ChievfxMcpPrefabCloseAndSceneListTests
    {
        private const string FolderName = "ChievfxMcpPrefabCloseTmp";
        private const string Folder = "Assets/" + FolderName;
        private const string PrefabPath = Folder + "/DiscardProbe.prefab";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", FolderName);
            var root = new GameObject("DiscardProbe");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        [TearDown]
        public void TearDown()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                stage.ClearDirtiness();
                StageUtility.GoToMainStage();
            }

            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void DiscardChangesClosesADirtyStageAndLeavesTheAsset()
        {
            var service = new PrefabBridgeService();
            service.Open(new JObject { ["prefabPath"] = PrefabPath });
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            Assert.IsNotNull(stage);
            stage!.prefabContentsRoot.AddComponent<BoxCollider>();
            EditorSceneManager.MarkSceneDirty(stage.scene);

            var refused = Assert.Throws<InvalidOperationException>(() => service.Close(new JObject { ["saveBeforeClose"] = false }));
            StringAssert.Contains("discardChanges:true", refused!.Message);

            var closed = JObject.FromObject(service.Close(new JObject { ["discardChanges"] = true }));

            Assert.IsTrue(closed.Value<bool>("discardedChanges"));
            Assert.IsFalse(closed.Value<bool>("savedBeforeClose"));
            Assert.IsNull(PrefabStageUtility.GetCurrentPrefabStage());
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<BoxCollider>());
        }

        [Test]
        public void SaveAndDiscardTogetherAreRejected()
        {
            var service = new PrefabBridgeService();
            service.Open(new JObject { ["prefabPath"] = PrefabPath });

            Assert.Throws<ArgumentException>(() => service.Close(new JObject { ["saveBeforeClose"] = true, ["discardChanges"] = true }));
        }

        [Test]
        public void InBuildListsTheBuildSettingsScenesInOrder()
        {
            var listed = JObject.FromObject(new SceneBridgeService().ListAvailable(new JObject { ["inBuild"] = true, ["maxResults"] = 500 }));
            var scenes = (JArray)listed["scenes"]!;
            var buildScenes = EditorBuildSettings.scenes;

            Assert.AreEqual(buildScenes.Length, scenes.Count);
            var enabledIndex = 0;
            for (var i = 0; i < buildScenes.Length; i++)
            {
                Assert.AreEqual(buildScenes[i].path, scenes[i].Value<string>("path"));
                if (buildScenes[i].enabled)
                {
                    Assert.AreEqual(enabledIndex++, scenes[i].Value<int>("buildIndex"));
                }
            }
        }
    }
}
