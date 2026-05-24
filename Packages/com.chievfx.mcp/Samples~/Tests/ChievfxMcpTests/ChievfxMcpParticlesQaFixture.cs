#nullable enable
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
using System.IO;
using Chievfx.Mcp.Extensions.Particles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor.Tests
{
    public static class ChievfxMcpParticlesQaFixture
    {
        public const string ScenePath = "Assets/Scenes/ChievfxMcpParticlesQaFixture.unity";
        public const string CameraName = "QaParticlesCamera";
        public const string RootName = "QaParticleSystems";
        public const string MagicPath = RootName + "/MagicGlowLoop";
        public const string SparksPath = RootName + "/SparkBurst";
        public const string SmokePath = RootName + "/SmokePuff";
        public const string MaterialPath = "Assets/Editor/ChievfxMcpTests/ParticlesQaFixtureMaterial.mat";

        [MenuItem("ChievFX/MCP/ParticleSystem QA/Rebuild Fixture Scene")]
        public static void RebuildFixtureSceneAsset()
        {
            BuildScene(saveSceneAsset: true);
        }

        public static Scene BuildScene(bool saveSceneAsset)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "ChievfxMcpParticlesQaFixture";

            CreateCamera();
            var materialPath = saveSceneAsset ? EnsureFixtureMaterialAsset() : string.Empty;
            var root = new GameObject(RootName);

            CreateFixtureSystem(root.transform, "MagicGlowLoop", "magic-glow", new Vector3(0f, 0.15f, 0f), 0);
            CreateFixtureSystem(root.transform, "SparkBurst", "spark-burst", new Vector3(-1.25f, -0.35f, 0f), 10);
            CreateFixtureSystem(root.transform, "SmokePuff", "smoke-puff", new Vector3(1.25f, -0.35f, 0f), -10);

            foreach (var renderer in root.GetComponentsInChildren<ParticleSystemRenderer>(includeInactive: true))
            {
                renderer.sharedMaterial = string.IsNullOrWhiteSpace(materialPath)
                    ? new Material(Shader.Find("Sprites/Default"))
                    : AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                renderer.sortingOrder += 20;
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

        private static void CreateCamera()
        {
            var cameraObject = new GameObject(CameraName);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.015f, 0.015f, 0.025f, 1f);
            camera.orthographic = true;
            camera.orthographicSize = 2.4f;
            camera.transform.position = new Vector3(0f, 0f, -8f);
            camera.transform.rotation = Quaternion.identity;
            cameraObject.tag = "MainCamera";
        }

        private static void CreateFixtureSystem(Transform parent, string name, string preset, Vector3 position, int sortingOrder)
        {
            var created = ChievfxMcpParticlesExtension.RunToolForTests(
                "particles-system-create",
                "{'name':'" + name + "','parentPath':'" + RootName + "','preset':'" + preset + "','position':{'x':"
                    + position.x.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",'y':"
                    + position.y.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",'z':"
                    + position.z.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "}}");
            _ = created;

            var system = parent.Find(name)!.GetComponent<ParticleSystem>();
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            renderer.sortingOrder = sortingOrder;
        }

        private static string EnsureFixtureMaterialAsset()
        {
            var folder = Path.GetDirectoryName(MaterialPath)!.Replace("\\", "/");
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Sprites/Default"))
                {
                    name = "QaParticlesMaterial",
                };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }

            return MaterialPath;
        }
    }
}
#endif
