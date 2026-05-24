#nullable enable
using System;
using System.Collections.Generic;
using Chievfx.Mcp.Extensions.Cameras;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor.Tests
{
    public static class ChievfxMcpCamerasQaFixture
    {
        public const string ScenePath = "Assets/Scenes/ChievfxMcpCamerasQaFixture.unity";
        public const string SequencerScenePath = "Assets/Scenes/ChievfxMcpSequencerCameraQaFixture.unity";
        public const string GeneratedFolder = "Assets/Editor/ChievfxMcpTests/GeneratedCameras";
        public const string TimelineAssetPath = GeneratedFolder + "/EndingSessionSlowMoZoom.playable";
        public const string CameraName = "QaGameplayCamera";
        public const string TargetName = "QaEndingTarget";
        public const string DirectorName = "QaEndingTimelineDirector";
        public const string SequencerGameplayCameraName = "QaSequencerGameplayCamera";
        public const string SequencerTargetName = "QaSequencerTarget";
        public const string SequencerName = "QaSequencerCamera";
        public const string SequencerWideShotName = "QaSequencerWide";
        public const string SequencerTightShotName = "QaSequencerTight";
        public const string SequencerBlendShotName = "QaSequencerBlendCheck";

        [MenuItem("ChievFX/MCP/Cameras QA/Rebuild Fixture Scene")]
        public static void RebuildFixtureSceneAsset()
        {
            BuildScene(saveSceneAsset: true);
        }

        [MenuItem("ChievFX/MCP/Cameras QA/Rebuild Sequencer Camera Fixture Scene")]
        public static void RebuildSequencerCameraFixtureSceneAsset()
        {
            BuildSequencerCameraScene(saveSceneAsset: true);
        }

        public static Scene BuildScene(bool saveSceneAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "ChievfxMcpCamerasQaFixture";

            CreateTarget(TargetName);
            CreateCamera(CameraName, new Vector3(0f, 1.4f, -6f), Quaternion.Euler(5f, 0f, 0f), 35f);

            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            if (IsAvailable(status, "timeline"))
            {
                RunTool("timeline-director-create", "{'name':'" + DirectorName + "','assetPath':'" + TimelineAssetPath + "','createAsset':true}");
            }

            if (IsAvailable(status, "cinemachine") && IsAvailable(status, "timeline"))
            {
                RunTool(
                    "timeline-shot-sequence-create",
                    "{'directorPath':'" + DirectorName + "','cameraPath':'" + CameraName + "','targetPath':'" + TargetName + "','assetPath':'" + TimelineAssetPath + "','trackName':'Ending SlowMo Zoom','shots':[{'name':'Ending Wide Shot','start':0,'duration':1.5,'fieldOfView':35,'distance':6,'priority':10},{'name':'Ending SlowMo Zoom','start':1.25,'duration':2.25,'fieldOfView':22,'distance':2.5,'priority':20}]}");
            }

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

        public static Scene BuildSequencerCameraScene(bool saveSceneAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "ChievfxMcpSequencerCameraQaFixture";

            CreateTarget(SequencerTargetName);
            CreateCamera(SequencerGameplayCameraName, new Vector3(0f, 1.5f, -7f), Quaternion.Euler(6f, 0f, 0f), 38f);

            var status = Resource("chievfx://extensions/chievfx.cameras/status");
            if (IsAvailable(status, "sequencerCamera"))
            {
                RunTool(
                    "cinemachine-sequencer-create",
                    "{'name':'" + SequencerName + "','targetPath':'" + SequencerTargetName + "','loop':false,'ensureBrain':true,'cameraPath':'" + SequencerGameplayCameraName + "','shots':[{'name':'" + SequencerWideShotName + "','hold':1.25,'fieldOfView':38,'distance':7,'priority':10,'blendStyle':'Cut','blendTime':0},{'name':'" + SequencerTightShotName + "','hold':0.8,'fieldOfView':24,'distance':3.25,'priority':20,'blendStyle':'EaseInOut','blendTime':0.35},{'name':'" + SequencerBlendShotName + "','hold':1.1,'fieldOfView':30,'distance':4.75,'priority':15,'blendStyle':'EaseInOut','blendTime':0.25}]}");
            }

            if (saveSceneAsset)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                {
                    AssetDatabase.CreateFolder("Assets", "Scenes");
                }

                EditorSceneManager.SaveScene(scene, SequencerScenePath);
                AssetDatabase.Refresh();
            }

            return scene;
        }

        private static void CreateTarget(string targetName)
        {
            var target = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            target.name = targetName;
            target.transform.position = new Vector3(0f, 1f, 0f);

            var fill = GameObject.CreatePrimitive(PrimitiveType.Plane);
            fill.name = "QaCameraGround";
            fill.transform.localScale = new Vector3(4f, 1f, 4f);
            fill.transform.position = Vector3.zero;

            var light = new GameObject("QaCameraKeyLight");
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            light.AddComponent<Light>().type = LightType.Directional;
        }

        private static void CreateCamera(string cameraName, Vector3 position, Quaternion rotation, float fieldOfView)
        {
            var cameraObject = new GameObject(cameraName);
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = position;
            cameraObject.transform.rotation = rotation;

            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.02f, 0.025f, 0.035f, 1f);
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
        }

        private static Dictionary<string, object?> Resource(string uri)
        {
            return (Dictionary<string, object?>)ChievfxMcpCamerasExtension.ReadResourceForTests(uri)!;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpCamerasExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static bool IsAvailable(Dictionary<string, object?> status, string key)
        {
            return status.TryGetValue(key, out var value)
                && value is Dictionary<string, object?> dependency
                && Equals(true, dependency["available"]);
        }
    }
}
