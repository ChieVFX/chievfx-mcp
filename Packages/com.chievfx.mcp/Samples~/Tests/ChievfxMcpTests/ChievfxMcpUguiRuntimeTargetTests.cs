#nullable enable
using System.Collections.Generic;
using System.Linq;
using Chievfx.Mcp.Extensions.Ugui;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor.Tests
{
    /// <summary>
    /// Covers the two things a probe/click result has to get right about a target that is not a plain
    /// Button: whether it can react, and which of several same-path objects the caller meant.
    /// </summary>
    public sealed class ChievfxMcpUguiRuntimeTargetTests
    {
        private readonly List<GameObject> created = new();

        [SetUp]
        public void SetUp()
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var gameObject in created)
            {
                if (gameObject != null)
                {
                    Object.DestroyImmediate(gameObject);
                }
            }

            created.Clear();
        }

        [Test]
        public void InteractionEnabledIgnoresUnrelatedDisabledBehaviours()
        {
            var target = CreateUiObject("EventTriggerOnly");
            target.AddComponent<EventTrigger>();
            target.AddComponent<Animator>().enabled = false;

            var enabled = UguiRuntimeHelpers.ResolveInteractionEnabled(target, UguiSharedHelpers.GetDependencyStatus(), out var disabled);

            Assert.IsTrue(enabled, "A disabled Animator says nothing about whether the click can be handled.");
            Assert.IsEmpty(disabled);
        }

        [Test]
        public void InteractionEnabledNamesTheDisabledHandler()
        {
            var target = CreateUiObject("EventTriggerOnly");
            target.AddComponent<EventTrigger>().enabled = false;

            var enabled = UguiRuntimeHelpers.ResolveInteractionEnabled(target, UguiSharedHelpers.GetDependencyStatus(), out var disabled);

            Assert.IsFalse(enabled);
            CollectionAssert.AreEqual(new[] { "EventTrigger" }, disabled);
        }

        [Test]
        public void InteractionEnabledReportsADisabledGraphic()
        {
            var target = CreateUiObject("Blocker");
            target.GetComponent<Image>().enabled = false;

            var enabled = UguiRuntimeHelpers.ResolveInteractionEnabled(target, UguiSharedHelpers.GetDependencyStatus(), out var disabled);

            Assert.IsFalse(enabled);
            CollectionAssert.Contains(disabled, "Image");
        }

        [Test]
        public void EventHandlerTypeNamesReportHandlersThatAreNotControls()
        {
            var target = CreateUiObject("EventTriggerOnly");
            target.AddComponent<EventTrigger>();

            var handlers = UguiRuntimeHelpers.GetEventHandlerTypeNames(target, System.Array.Empty<string>());

            CollectionAssert.Contains(handlers, "EventTrigger");
        }

        [Test]
        public void EventHandlerTypeNamesSkipComponentsAlreadyReportedAsControls()
        {
            var target = CreateUiObject("Btn");
            target.AddComponent<Button>();

            var handlers = UguiRuntimeHelpers.GetEventHandlerTypeNames(target, new[] { "Button" });

            CollectionAssert.DoesNotContain(handlers, "Button");
        }

        [Test]
        public void RuntimePathLookupFindsEveryObjectSharingThePathIncludingInactiveOnes()
        {
            var parent = CreateUiObject("List");
            var inactive = CreateUiObject("Item", parent.transform);
            inactive.SetActive(false);
            var active = CreateUiObject("Item", parent.transform);

            var matches = UguiRuntimeHelpers.FindGameObjectsByRuntimePath("List/Item");

            CollectionAssert.AreEquivalent(new[] { inactive, active }, matches);
        }

        [Test]
        public void PathTargetingPrefersTheActiveObjectAndSaysWhatWasAmbiguous()
        {
            // A pooled list holds many deactivated copies under one shared path; Transform.Find hands back
            // the first of them, and interactions dispatched at a deactivated object do nothing.
            var parent = CreateUiObject("List");
            var inactive = CreateUiObject("Item", parent.transform);
            inactive.SetActive(false);
            var active = CreateUiObject("Item", parent.transform);
            var warnings = new List<string>();

            var resolved = UguiRuntimeHelpers.ResolveRuntimeTargetFromArgs(
                new JObject { ["path"] = "List/Item" },
                "path",
                "instanceId",
                warnings);

            Assert.AreSame(active, resolved);
            Assert.IsTrue(warnings.Any(warning => warning.Contains("matched 2 objects")), string.Join(" | ", warnings));
        }

        [Test]
        public void PathTargetingWarnsWhenEveryMatchIsInactive()
        {
            var parent = CreateUiObject("List");
            var inactive = CreateUiObject("Item", parent.transform);
            inactive.SetActive(false);
            var warnings = new List<string>();

            var resolved = UguiRuntimeHelpers.ResolveRuntimeTargetFromArgs(
                new JObject { ["path"] = "List/Item" },
                "path",
                "instanceId",
                warnings);

            Assert.AreSame(inactive, resolved);
            Assert.IsTrue(warnings.Any(warning => warning.Contains("inactive")), string.Join(" | ", warnings));
        }

        [Test]
        public void PathTargetingStaysQuietForAnUnambiguousActiveTarget()
        {
            var parent = CreateUiObject("List");
            CreateUiObject("Item", parent.transform);
            var warnings = new List<string>();

            var resolved = UguiRuntimeHelpers.ResolveRuntimeTargetFromArgs(
                new JObject { ["path"] = "List/Item" },
                "path",
                "instanceId",
                warnings);

            Assert.IsNotNull(resolved);
            Assert.IsEmpty(warnings);
        }

        private GameObject CreateUiObject(string name, Transform? parent = null)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            if (parent != null)
            {
                gameObject.transform.SetParent(parent, false);
            }
            else
            {
                created.Add(gameObject);
            }

            gameObject.AddComponent<Image>();
            return gameObject;
        }
    }
}
