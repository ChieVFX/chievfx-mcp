#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor.Tests
{
    // screenshot-camera by a common name took a disabled helper camera and returned a white frame without a word;
    // gameobject-find namePattern without wildcards found nothing while dozens of names contained the text.
    public sealed class ChievfxMcpCameraAndPatternTests
    {
        private readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in created)
            {
                Object.DestroyImmediate(gameObject);
            }

            created.Clear();
        }

        [Test]
        public void CameraByNameTakesTheActiveOneAndNamesTheOthers()
        {
            var inactive = Create("R11SameNameCamera");
            inactive.AddComponent<Camera>();
            inactive.SetActive(false);
            var active = Create("R11SameNameCamera");
            var activeCamera = active.AddComponent<Camera>();

            var warnings = new List<string>();
            var chosen = ScreenshotBridgeService.FindCameraByNameOrDefault("R11SameNameCamera", GameObjectBridgeService.GetGameObjectQueryContext(null), warnings);

            Assert.AreSame(activeCamera, chosen);
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("2 cameras are named 'R11SameNameCamera'", warnings[0]);
            StringAssert.Contains("(inactive)", warnings[0]);
        }

        [Test]
        public void UnknownCameraNameSaysTheDefaultWasTaken()
        {
            var warnings = new List<string>();
            ScreenshotBridgeService.FindCameraByNameOrDefault("R11NoSuchCamera", GameObjectBridgeService.GetGameObjectQueryContext(null), warnings);

            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains("No camera is named 'R11NoSuchCamera'", warnings[0]);
        }

        [Test]
        public void WholeNamePatternSuggestsWildcards()
        {
            var objects = new List<GameObject> { Create("Player_Body"), Create("Net_Player"), Create("Car") };

            StringAssert.Contains("2 names contain 'Player' - try namePattern:'*Player*'", GameObjectBridgeService.DescribeWholeNamePattern("Player", objects));
            Assert.IsNull(GameObjectBridgeService.DescribeWholeNamePattern("*Player", objects));
            Assert.IsNull(GameObjectBridgeService.DescribeWholeNamePattern("Truck", objects));
            StringAssert.Contains("not a regex", GameObjectBridgeService.DescribeWholeNamePattern("(?i)player|car", objects));
            StringAssert.Contains("namePattern:'*player*'", GameObjectBridgeService.DescribeWholeNamePattern("(?i)player|car", objects));
        }

        [Test]
        public void CameraFieldsSetWithALayerMaskInTheSamePatchStay()
        {
            var camera = Create("R24PatchCamera").AddComponent<Camera>();
            camera.backgroundColor = Color.red;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.cullingMask = -1;

            GameObjectBridgeService.ApplyComponentPropertyPatch(camera, new JObject
            {
                ["m_CullingMask"] = 0,
                ["m_BackGroundColor"] = new JObject { ["r"] = 0.1, ["g"] = 0.2, ["b"] = 0.3, ["a"] = 1.0 },
                ["m_ClearFlags"] = 2,
            }, false);

            Assert.AreEqual(0, camera.cullingMask);
            Assert.AreEqual(0.2f, camera.backgroundColor.g, 0.001f);
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
        }

        [Test]
        public void SerializedColourAndVectorsReadBackExactly()
        {
            var camera = Create("R25PreciseCamera").AddComponent<Camera>();
            var colour = new Color(0.19215687f, 0.3019608f, 0.4745098f, 0f);
            camera.backgroundColor = colour;
            camera.rect = new Rect(0.125f, 0.0625f, 0.333333343f, 1f);
            var serialized = new SerializedObject(camera);

            CollectionAssert.AreEqual(new[] { colour.r, colour.g, colour.b, colour.a }, ReadNumbers(serialized.FindProperty("m_BackGroundColor")));
            CollectionAssert.AreEqual(new[] { 0.125f, 0.0625f, 0.333333343f, 1f }, ReadNumbers(serialized.FindProperty("m_NormalizedViewPortRect")));
        }

        private static float[] ReadNumbers(SerializedProperty property)
        {
            var truncated = false;
            var text = (string)GameObjectBridgeService.ReadSerializedPropertyValue(property, ref truncated)!;
            return Regex.Matches(text, @"-?\d+(\.\d+)?(E[-+]?\d+)?").Cast<Match>().Select(match => float.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        }

        private GameObject Create(string name)
        {
            var gameObject = new GameObject(name);
            created.Add(gameObject);
            return gameObject;
        }
    }
}
