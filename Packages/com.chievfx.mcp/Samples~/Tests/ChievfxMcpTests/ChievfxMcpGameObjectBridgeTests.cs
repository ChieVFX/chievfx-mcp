#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpGameObjectBridgeTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void DuplicateDefaultsToSiblingWithChildHierarchy()
        {
            var parent = new GameObject("Parent");
            var source = new GameObject("Source");
            source.transform.SetParent(parent.transform, false);
            new GameObject("Child").transform.SetParent(source.transform, false);

            var result = Duplicate("{'path':'Parent/Source','newName':'SourceCopy'}");
            var clone = GameObject.Find((string)result["path"]!);

            Assert.AreEqual("Parent/SourceCopy", (string)result["path"]!);
            Assert.AreSame(parent.transform, clone.transform.parent);
            Assert.AreEqual(1, clone.transform.childCount);
            Assert.AreEqual("Child", clone.transform.GetChild(0).name);
            Assert.AreEqual(true, result["includeChildren"]);
            Assert.AreEqual(1, result["duplicatedCount"]);
        }

        [Test]
        public void DuplicateCanCreateRootOnlyCopyUnderTargetParent()
        {
            var source = new GameObject("Source");
            new GameObject("Child").transform.SetParent(source.transform, false);
            var targetParent = new GameObject("TargetParent");

            var result = Duplicate("{'path':'Source','newName':'Shallow','parentPath':'TargetParent','includeChildren':false}");
            var clone = GameObject.Find((string)result["path"]!);

            Assert.AreEqual("TargetParent/Shallow", (string)result["path"]!);
            Assert.AreSame(targetParent.transform, clone.transform.parent);
            Assert.AreEqual(0, clone.transform.childCount);
            Assert.AreEqual(false, result["includeChildren"]);
            Assert.AreEqual("TargetParent", result["parentPath"]);
        }

        [Test]
        public void DuplicateCanCreateMultipleCopies()
        {
            new GameObject("Source");

            var result = Duplicate("{'path':'Source','newName':'Copy','count':3}");
            var duplicates = ((JArray)result["duplicates"]!).Cast<JObject>().ToArray();

            Assert.AreEqual(3, result["duplicatedCount"]);
            Assert.AreEqual(3, duplicates.Length);
            Assert.AreEqual((string)duplicates[0]["path"]!, (string)result["path"]!);
            Assert.AreEqual((int)duplicates[0]["instanceId"]!, (int)result["instanceId"]!);
            CollectionAssert.AreEqual(new[] { "Copy 1", "Copy 2", "Copy 3" }, duplicates.Select(row => (string)row["name"]!).ToArray());
        }

        private static JObject Duplicate(string argsJson)
        {
            var serviceType = typeof(ChievfxMcpBridge).Assembly.GetType("Chievfx.Mcp.Editor.GameObjectBridgeService", throwOnError: true)!;
            var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
            var method = serviceType.GetMethod("Duplicate", BindingFlags.Public | BindingFlags.Instance)!;
            var result = method.Invoke(service, new object[] { JObject.Parse(argsJson) })!;
            return JObject.FromObject(result);
        }
    }
}
