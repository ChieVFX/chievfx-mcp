#nullable enable
using System.Collections.Generic;
using NUnit.Framework;
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
        }

        private GameObject Create(string name)
        {
            var gameObject = new GameObject(name);
            created.Add(gameObject);
            return gameObject;
        }
    }
}
