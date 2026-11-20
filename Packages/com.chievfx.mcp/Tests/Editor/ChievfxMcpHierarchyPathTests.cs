#nullable enable
using System;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor.Tests
{
    // Hierarchy paths (gameobject-find path:, transform-get, scene/.../go/{path}) resolve the same objects as before, without
    // building every object's path: on a Play Mode world with tens of thousands of objects that froze the editor for a minute.
    public sealed class ChievfxMcpHierarchyPathTests
    {
        private GameObject root = null!;
        private GameObjectQueryContext context = null!;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("PathTestRoot");
            var first = Child(root, "Item");
            Child(first, "Leaf1");
            var second = Child(root, "Item");
            Child(second, "Leaf");
            Child(root, "Single");
            Child(Child(root, "A/B"), "Inner");
            var many = Child(root, "Many");
            for (var index = 0; index < 3000; index++)
            {
                Child(many, "Dup");
            }

            context = new GameObjectQueryContext { Source = "test", Roots = new[] { root } };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
        }

        [Test]
        public void EveryCanonicalPathResolvesToItsObject()
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform.parent != null && transform.parent.name == "Many" && transform.GetSiblingIndex() % 500 != 0)
                {
                    continue;
                }

                var path = GameObjectBridgeService.GetHierarchyPath(transform.gameObject, context);
                Assert.AreSame(transform.gameObject, GameObjectBridgeService.ResolveGameObjectByPath(context, path), path);
            }
        }

        [Test]
        public void IndexedEscapedAndUnindexedPathsResolve()
        {
            Assert.AreEqual("Leaf", GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Item[2]/Leaf").name);
            Assert.AreEqual("Leaf", GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Item/Leaf").name, "An unindexed path that leads to one object resolves.");
            Assert.AreEqual("Inner", GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/A\\/B/Inner").name);
            Assert.AreEqual("Single", GameObjectBridgeService.ResolveGameObjectByPath(context, "/PathTestRoot/Single/").name);
        }

        [Test]
        public void AComponentIdNamesItsGameObject()
        {
            var body = Child(root, "Body");
            var rigidbody = body.AddComponent<Rigidbody>();
            Assert.AreSame(body, GameObjectBridgeService.ResolveGameObjectByInstanceId(context, UnityObjectIdentity.GetLegacyInstanceId(rigidbody)),
                "component-get prints references by the component's id; GameObject tools take it as its object.");
        }

        [Test]
        public void ABaseComponentTypeMatchesItsSubclasses()
        {
            Assert.IsTrue(GameObjectBridgeService.ComponentTypeMatches(typeof(MeshCollider), "Collider"), "GetComponent<Collider>() finds a MeshCollider.");
            Assert.IsTrue(GameObjectBridgeService.ComponentTypeMatches(typeof(MeshCollider), "UnityEngine.MeshCollider"));
            Assert.IsFalse(GameObjectBridgeService.ComponentTypeMatches(typeof(MeshCollider), "BoxCollider"));
        }

        [Test]
        public void AmbiguousAndMissingPathsAreRefused()
        {
            StringAssert.Contains("is ambiguous", Assert.Throws<InvalidOperationException>(() => GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Item"))!.Message);
            StringAssert.Contains("No GameObject found", Assert.Throws<InvalidOperationException>(() => GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Nope"))!.Message);
            StringAssert.Contains("No GameObject found", Assert.Throws<InvalidOperationException>(() => GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Item[3]"))!.Message);
        }

        [Test]
        public void ThousandsOfSameNamedSiblingsResolveQuickly()
        {
            var stopwatch = Stopwatch.StartNew();
            var dup = GameObjectBridgeService.ResolveGameObjectByPath(context, "PathTestRoot/Many/Dup[2999]");
            stopwatch.Stop();

            Assert.AreEqual(2998, dup.transform.GetSiblingIndex());
            Assert.Less(stopwatch.ElapsedMilliseconds, 500, "Path lookup walks the path, not every object's path.");
        }

        private static GameObject Child(GameObject parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent.transform, false);
            return child;
        }
    }
}
