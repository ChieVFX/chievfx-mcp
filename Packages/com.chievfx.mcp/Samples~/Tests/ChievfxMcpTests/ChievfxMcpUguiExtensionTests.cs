#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chievfx.Mcp.Editor;
using Chievfx.Mcp.Extensions.Ugui;
using Newtonsoft.Json.Linq;
using UnityEditor;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpUguiExtensionTests
    {
        [SetUp]
        public void SetUp()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            ChievfxMcpUguiExtension.SetPreferInputSystemUiModuleOverrideForTests(null);
            ChievfxMcpUguiExtension.SetRuntimeReadAllowedOverrideForTests(null);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset("Assets/Editor/ChievfxMcpTests/Generated");
        }

        [UnityTearDown]
        public System.Collections.IEnumerator UnityTearDown()
        {
            if (Application.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [Test]
        public void CanvasEnsureCreatesCanvasScalerRaycasterAndEventSystem()
        {
            RequireUgui();

            var result = RunTool(
                "ugui-canvas-ensure",
                "{'name':'HudCanvas','rect':{'preset':'fill'}}");

            var canvas = Row(result, "canvas");
            Assert.AreEqual("HudCanvas", canvas["name"]);
            var canvasObject = GameObject.Find("HudCanvas");
            Assert.IsNotNull(canvasObject.GetComponent<Canvas>());
            Assert.IsNotNull(canvasObject.GetComponent<CanvasScaler>());
            Assert.IsNotNull(canvasObject.GetComponent<GraphicRaycaster>());
            Assert.IsNotNull(Row(result, "eventSystem"));
            AssertEventSystemInputModuleMatchesProject();
            Assert.IsFalse(result.ContainsKey("uri"));
            Assert.IsFalse(result.ContainsKey("operation"));
        }

        [Test]
        public void ElementCreateBuildsButtonAndSliderWithCanonicalComponents()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var button = RunTool(
                "ugui-create-control",
                "{'controlType':'button','name':'PlayButton','parentPath':'UiRoot','text':'Play','rect':{'preset':'center','size':{'x':220,'y':64}}}");
            var slider = RunTool(
                "ugui-create-control",
                "{'controlType':'slider','name':'VolumeSlider','parentPath':'UiRoot','rect':{'preset':'dock-bottom','size':{'x':240,'y':48}}}");

            CollectionAssert.Contains(StringArray(Row(button, "target"), "components"), "Button");
            CollectionAssert.Contains(StringArray(Row(slider, "target"), "components"), "Slider");
            Assert.AreNotEqual(0, (int)Row(button, "target")["instanceId"]!);
            StringAssert.Contains("UiRoot/PlayButton", (string)Row(button, "target")["path"]!);
        }

        [Test]
        public void SplitCreateToolsCoverSimpleImageProgressbarAndTmpCreate()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var empty = RunTool("ugui-create-simple", "{'name':'Plain','parentPath':'UiRoot'}");
            CollectionAssert.DoesNotContain(StringArray(Row(empty, "target"), "components"), "Image");

            var image = RunTool("ugui-create-simple", "{'name':'Tinted','parentPath':'UiRoot','image':{'color':'#ff0000','raycastTarget':false}}");
            CollectionAssert.Contains(StringArray(Row(image, "target"), "components"), "Image");

            var progress = RunTool("ugui-create-control", "{'controlType':'progressbar','name':'Progress','parentPath':'UiRoot','value':0.25}");
            Assert.AreEqual("progressbar", progress["controlType"]);
            Assert.IsNotNull(GameObject.Find("Progress/Fill"));

            if (TextMeshProLoaded())
            {
                var tmp = RunTool("ugui-textmeshpro-set-or-create", "{'paths':['UiRoot/Plain'],'isCreate':true,'text':'Created','fontSize':18}");
                Assert.AreEqual(1, tmp["updatedCount"]);
                Assert.IsNotNull(FindComponentByTypeName("TextMeshProUGUI"));
            }
        }

        [Test]
        public void RectUpdateSupportsFillAndAnchorSizePresets()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'name':'Box','parentPath':'UiRoot'}");

            var fill = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'preset':'fill','margin':8}}");
            Assert.AreEqual(1, fill["updatedCount"]);
            var fillGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var fillRows = ((object[])fillGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var fillRect = Row(fillRows.Single(), "rectTransform");
            AssertVector(fillRect, "anchorMin", 0f, 0f);
            AssertVector(fillRect, "anchorMax", 1f, 1f);
            AssertVector(fillRect, "offsetMin", 8f, 8f);

            var anchored = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'preset':'anchor-size','anchorMin':{'x':0.25,'y':0.25},'anchorMax':{'x':0.75,'y':0.75},'size':{'x':100,'y':50}}}");
            var anchoredGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var anchoredRows = ((object[])anchoredGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var anchoredRect = Row(anchoredRows.Single(), "rectTransform");
            AssertVector(anchoredRect, "anchorMin", 0.25f, 0.25f);
            AssertVector(anchoredRect, "anchorMax", 0.75f, 0.75f);
            Assert.IsFalse(anchored.ContainsKey("warnings"));

            var rectGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var rectRows = ((object[])rectGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            AssertVector(Row(rectRows.Single(), "rectTransform"), "anchorMin", 0.25f, 0.25f);

            var updated = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'preset':'center','position':{'x':12,'y':24},'size':{'x':80,'y':40}}}");
            Assert.AreEqual(1, updated["updatedCount"]);
            var updatedRect = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var updatedRows = ((object[])updatedRect["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            AssertVector(Row(updatedRows.Single(), "rectTransform"), "anchoredPosition", 12f, 24f);
        }

        [Test]
        public void RectUpdateAppliesRawRectTransformOverrides()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'name':'Box','parentPath':'UiRoot'}");

            var stretched = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'preset':'stretch','offsetMin':{'x':3,'y':4},'offsetMax':{'x':-5,'y':-6}}}");
            Assert.IsFalse(stretched.ContainsKey("warnings"));
            var stretchedGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var stretchedRows = ((object[])stretchedGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var stretchedRect = Row(stretchedRows.Single(), "rectTransform");
            AssertVector(stretchedRect, "offsetMin", 3f, 4f);
            AssertVector(stretchedRect, "offsetMax", -5f, -6f);

            RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'preset':'center','anchoredPosition':{'x':7,'y':8},'sizeDelta':{'x':90,'y':45}}}");
            var centeredGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var centeredRows = ((object[])centeredGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var centeredRect = Row(centeredRows.Single(), "rectTransform");
            AssertVector(centeredRect, "anchoredPosition", 7f, 8f);
            AssertVector(centeredRect, "sizeDelta", 90f, 45f);

            RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Box'],'rect':{'anchorMin':{'x':0.2,'y':0.3},'anchorMax':{'x':0.8,'y':0.9},'anchoredPosition':{'x':11,'y':12},'sizeDelta':{'x':70,'y':35}}}");
            var rawAnchorGet = RunTool("ugui-rect-get", "{'paths':['UiRoot/Box']}");
            var rawAnchorRows = ((object[])rawAnchorGet["rects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var rawAnchorRect = Row(rawAnchorRows.Single(), "rectTransform");
            AssertVector(rawAnchorRect, "anchorMin", 0.2f, 0.3f);
            AssertVector(rawAnchorRect, "anchorMax", 0.8f, 0.9f);
            AssertVector(rawAnchorRect, "anchoredPosition", 11f, 12f);
            AssertVector(rawAnchorRect, "sizeDelta", 70f, 35f);
        }

        [Test]
        public void LayoutToolsConfigureParentsChildrenAndWarnRectUpdates()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'name':'Stack','parentPath':'UiRoot','rect':{'preset':'fill'}}");
            RunTool("ugui-create-simple", "{'name':'Child','parentPath':'UiRoot/Stack','rect':{'preset':'center','size':{'x':40,'y':20}}}");

            var groupResult = RunTool(
                "ugui-layout-group-set",
                "{'paths':['UiRoot/Stack'],'layoutGroup':'vertical','spacing':6,'padding':{'left':4,'right':5,'top':6,'bottom':7},'childAlignment':'upper-left','childControlWidth':true,'childControlHeight':true,'childForceExpandWidth':true,'childForceExpandHeight':false}");
            Assert.AreEqual(1, groupResult["updatedCount"]);
            var group = GameObject.Find("Stack").GetComponent<VerticalLayoutGroup>();
            Assert.IsNotNull(group);
            Assert.AreEqual(6f, group.spacing, 0.001f);
            Assert.AreEqual(4, group.padding.left);
            Assert.IsTrue(group.childControlWidth);
            Assert.IsFalse(group.childForceExpandHeight);

            var elementResult = RunTool(
                "ugui-layout-element-set",
                "{'paths':['UiRoot/Stack/Child'],'preferredHeight':44,'flexibleWidth':1}");
            Assert.AreEqual(1, elementResult["updatedCount"]);
            var element = GameObject.Find("Child").GetComponent<LayoutElement>();
            Assert.IsNotNull(element);
            Assert.AreEqual(44f, element.preferredHeight, 0.001f);
            Assert.AreEqual(1f, element.flexibleWidth, 0.001f);

            var rectResult = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Stack/Child'],'rect':{'preset':'stretch'}}");
            Assert.AreEqual(1, rectResult["layoutDrivenCount"]);
            Assert.IsTrue(StringArray(rectResult, "warnings").Any(warning => warning.Contains("ugui-layout-group-set", StringComparison.Ordinal)));

            RunTool("ugui-layout-element-set", "{'paths':['UiRoot/Stack/Child'],'ignoreLayout':true}");
            var ignoredRectResult = RunTool(
                "ugui-rect-update",
                "{'paths':['UiRoot/Stack/Child'],'rect':{'preset':'stretch'}}");
            Assert.AreEqual(0, ignoredRectResult["layoutDrivenCount"]);

            var rebuildResult = RunTool("ugui-layout-rebuild", "{'paths':['UiRoot/Stack']}");
            Assert.AreEqual(1, rebuildResult["rebuiltCount"]);
        }

        [Test]
        public void ScrollRectCreateBuildsWiredLayoutHierarchy()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var result = RunTool(
                "ugui-scrollrect-create",
                "{'name':'ItemsScroll','parentPath':'UiRoot','direction':'vertical','contentLayout':'vertical','spacing':5,'contentSizeFitter':true,'rect':{'preset':'fill'}}");

            Assert.AreEqual(true, result["success"]);
            Assert.AreEqual("vertical", result["direction"]);
            Assert.AreEqual("vertical", result["contentLayout"]);
            var scrollRoot = GameObject.Find("ItemsScroll");
            var scrollRect = scrollRoot.GetComponent<ScrollRect>();
            Assert.IsNotNull(scrollRect);
            Assert.IsTrue(scrollRect.vertical);
            Assert.IsFalse(scrollRect.horizontal);
            Assert.IsNotNull(scrollRect.viewport);
            Assert.IsNotNull(scrollRect.content);
            Assert.AreEqual("Viewport", scrollRect.viewport.name);
            Assert.AreEqual("Content", scrollRect.content.name);
            Assert.IsNotNull(scrollRect.viewport.GetComponent<RectMask2D>());
            Assert.IsNotNull(scrollRect.content.GetComponent<VerticalLayoutGroup>());
            var fitter = scrollRect.content.GetComponent<ContentSizeFitter>();
            Assert.IsNotNull(fitter);
            Assert.AreEqual(ContentSizeFitter.FitMode.PreferredSize, fitter.verticalFit);
        }

        [Test]
        public void GridCreateBuildsColoredCells()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var result = RunTool(
                "ugui-grid-create",
                "{'name':'Palette','parentPath':'UiRoot','count':4,'cellNamePrefix':'Swatch','cellType':'image','cellSize':{'x':32,'y':24},'constraintCount':2,'colors':['#ff0000','#00ff00']}");

            Assert.AreEqual(true, result["success"]);
            Assert.AreEqual(4, result["cellCount"]);
            var grid = GameObject.Find("Palette");
            var layout = grid.GetComponent<GridLayoutGroup>();
            Assert.IsNotNull(layout);
            Assert.AreEqual(2, layout.constraintCount);
            Assert.AreEqual(4, grid.transform.childCount);
            Assert.AreEqual("Swatch 1", grid.transform.GetChild(0).name);
            var firstImage = grid.transform.GetChild(0).GetComponent<Image>();
            Assert.AreEqual(1f, firstImage.color.r, 0.001f);
            Assert.AreEqual(0f, firstImage.color.g, 0.001f);
        }

        [Test]
        public void SiblingDrawOrderSetMovesTargetsPrecisely()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Back','parentPath':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Middle','parentPath':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Front','parentPath':'UiRoot'}");

            var first = RunTool("ugui-sibling-draworder-set", "{'paths':['UiRoot/Front'],'index':0}");

            Assert.AreEqual(1, first["updatedCount"]);
            Assert.AreEqual(0, GameObject.Find("Front").transform.GetSiblingIndex());
            var firstOrder = Rows(first, "siblingOrder");
            Assert.AreEqual("3/3", firstOrder[0]["showing"]);
            Assert.AreEqual("Front", firstOrder[2]["0:"]);

            var after = RunTool("ugui-sibling-draworder-set", "{'paths':['UiRoot/Back'],'placement':'after','siblingPath':'UiRoot/Middle'}");

            Assert.AreEqual(1, after["updatedCount"]);
            Assert.Greater(GameObject.Find("Back").transform.GetSiblingIndex(), GameObject.Find("Middle").transform.GetSiblingIndex());
        }

        [Test]
        public void SiblingDrawOrderSetCompactsLargeSiblingOrder()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            for (var i = 0; i < 15; i++)
            {
                RunTool("ugui-create-simple", "{'name':'Item" + i + "','parentPath':'UiRoot'}");
            }

            var result = RunTool("ugui-sibling-draworder-set", "{'paths':['UiRoot/Item12'],'index':4}");
            var order = Rows(result, "siblingOrder");

            Assert.AreEqual("10/15", order[0]["showing"]);
            Assert.AreEqual(true, order[1]["truncated"]);
            Assert.AreEqual("...", order[order.Length - 1]["...6_more"]);
            Assert.IsTrue(order.Any(row => row.TryGetValue("4:", out var name) && Equals(name, "Item12")));
        }

        [Test]
        public void InspectOutputIncludesElements()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-control", "{'controlType':'button','name':'PlayButton','parentPath':'UiRoot'}");

            var inspect = RunTool("ugui-ui-hierarchy", "{'paths':['UiRoot'],'maxResults':16}");

            Assert.GreaterOrEqual(Convert.ToInt32(inspect["count"]), 1);
            Assert.IsNotEmpty((object[])inspect["roots"]!);
            var canvas = ((object[])inspect["roots"]!).Cast<Dictionary<string, object?>>().Single();
            var elements = ((object[])canvas["children"]!).Cast<Dictionary<string, object?>>().ToArray();
            Assert.IsTrue(elements.Any(element => (string)element["name"]! == "PlayButton"));

            var detail = RunTool("ugui-ui-find", "{'paths':['UiRoot/PlayButton'],'includeDetails':true}");
            var objects = ((object[])detail["objects"]!).Cast<Dictionary<string, object?>>().ToArray();
            var button = objects.Single();
            Assert.AreEqual("PlayButton", button["name"]);
            var screenRect = Row(button, "screenRect");
            Assert.AreEqual("bottom-left", screenRect["origin"]);
            Assert.AreEqual("pixels", screenRect["units"]);
            Assert.IsNotNull(Row(screenRect, "rect"));
            Assert.IsNotNull(Row(screenRect, "center"));
            Assert.IsFalse(screenRect.ContainsKey("normalized"));

            var normalizedDetail = RunTool("ugui-ui-find", "{'paths':['UiRoot/PlayButton'],'includeDetails':true,'normalizedCoords':true}");
            var normalizedButton = ((object[])normalizedDetail["objects"]!).Cast<Dictionary<string, object?>>().Single();
            var normalizedRect = Row(normalizedButton, "screenRect");
            Assert.AreEqual("normalized", normalizedRect["units"]);
            Assert.IsNotNull(Row(normalizedRect, "rect"));
            Assert.IsNotNull(Row(normalizedRect, "center"));
            Assert.IsFalse(normalizedRect.ContainsKey("pixel"));
        }

        [Test]
        public void TextCreateFallsBackToLegacyWhenTmpUnavailable()
        {
            var result = (Dictionary<string, object?>)ChievfxMcpUguiExtension.ResolveTextBackendForTests("tmp", tmpConfigured: false);

            Assert.AreEqual("legacy", result["textBackend"]);
            Assert.IsTrue(StringArray(result, "warnings").Any(warning => warning.Contains("TMP text backend unavailable")));
        }

        [Test]
        public void TextCreateUsesTmpWhenPackageTypeIsLoaded()
        {
            RequireUgui();
            if (!TextMeshProLoaded())
            {
                Assert.Ignore("TMP package/type is not loaded in this project.");
            }

            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var result = RunTool(
                "ugui-create-simple",
                "{'name':'TmpLabel','parentPath':'UiRoot'}");
            RunTool(
                "ugui-textmeshpro-set-or-create",
                "{'paths':['UiRoot/TmpLabel'],'isCreate':true,'text':'Hello'}");

            Assert.IsNotNull(FindComponentByTypeName("TextMeshProUGUI"));
        }

        [Test]
        public void TmpCreateSupportsImageTargetsChildPlacementAndAlignmentAliases()
        {
            RequireUgui();
            if (!TextMeshProLoaded())
            {
                Assert.Ignore("TMP package/type is not loaded in this project.");
            }

            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Panel','parentPath':'UiRoot'}");

            var result = RunTool(
                "ugui-textmeshpro-set-or-create",
                "{'paths':['UiRoot/Panel'],'isCreate':true,'placement':'child','childName':'Caption','text':'Hello','alignment':'middle center'}");

            Assert.AreEqual(1, result["updatedCount"]);
            Assert.AreEqual(1, result["createdCount"]);
            Assert.IsNotNull(GameObject.Find("UiRoot/Panel/Caption"));

            var get = RunTool("ugui-textmeshpro-get", "{'paths':['UiRoot/Panel/Caption']}");
            var text = Rows(get, "texts").Single();
            Assert.AreEqual("Hello", text["text"]);
            Assert.AreEqual("Center", text["alignment"]);
        }

        [Test]
        public void SlicedImageWarnsWhenSpriteBorderIsMissing()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Panel','parentPath':'UiRoot'}");
            var spritePath = CreateTempSpriteTexture("zero-border-panel.png", Vector4.zero);

            var result = RunTool(
                "ugui-image-set",
                "{'targetPath':'UiRoot/Panel','spritePath':'" + spritePath + "','imageType':'Sliced'}");
            var readiness = (Dictionary<string, object?>)ChievfxMcpUguiExtension.ReadResourceForTests("chievfx://extensions/chievfx.ugui/sprite/" + spritePath)!;

            Assert.IsTrue(StringArray(result, "warnings").Any(warning => warning.Contains("non-zero sprite border")));
            Assert.IsTrue(StringArray(readiness, "warnings").Any(warning => warning.Contains("Sprite border is zero")));
        }

        [Test]
        public void ImageSetAutoSelectsSimpleForZeroBorderSprite()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Panel','parentPath':'UiRoot'}");
            var spritePath = CreateTempSpriteTexture("auto-zero-border-panel.png", Vector4.zero);

            var result = RunTool(
                "ugui-image-set",
                "{'targetPath':'UiRoot/Panel','spritePath':'" + spritePath + "','imageType':'Auto'}");

            Assert.AreEqual("Simple", Row(result, "image")["imageType"]);
            Assert.IsFalse(result.ContainsKey("warnings"));
        }

        [Test]
        public void ImageSetAutoSelectsSlicedForBorderedSprite()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Panel','parentPath':'UiRoot'}");
            var spritePath = CreateTempSpriteTexture("auto-bordered-panel.png", new Vector4(2f, 2f, 2f, 2f));

            var result = RunTool(
                "ugui-image-set",
                "{'targetPath':'UiRoot/Panel','spritePath':'" + spritePath + "','imageType':'Auto'}");

            Assert.AreEqual("Sliced", Row(result, "image")["imageType"]);
            Assert.IsFalse(result.ContainsKey("warnings"));
        }

        [Test]
        public void PrimitiveImageCreateNormalizesFlexiblePathToPng()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            var result = RunTool(
                "ugui-image-primitive-create",
                "{'path':'Assets/Editor/ChievfxMcpTests/Generated/primitive-source.jpeg','name':'Primitive','parentPath':'UiRoot','primitiveType':'rect','width':8,'height':8}");

            Assert.AreEqual("Assets/Editor/ChievfxMcpTests/Generated/primitive-source.png", result["path"]);
            Assert.AreEqual("UiRoot/Primitive", result["gameObjectPath"]);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Sprite>((string)result["path"]!));
        }

        [Test]
        public void ImageSetWritesColorThroughSerializedColorField()
        {
            RequireUgui();
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");
            RunTool("ugui-create-simple", "{'image':{},'name':'Panel','parentPath':'UiRoot'}");

            var result = RunTool(
                "ugui-image-set",
                "{'targetPath':'UiRoot/Panel','color':'#3366cc80'}");

            var color = Row(result, "image")["color"] as Dictionary<string, float>;
            Assert.IsNotNull(color);
            Assert.AreEqual(0.2f, Convert.ToSingle(color!["r"]), 0.001f);
            Assert.AreEqual(0.4f, Convert.ToSingle(color["g"]), 0.001f);
            Assert.AreEqual(0.8f, Convert.ToSingle(color["b"]), 0.001f);
            Assert.AreEqual(0.502f, Convert.ToSingle(color["a"]), 0.001f);

            var image = GameObject.Find("UiRoot/Panel").GetComponent<Image>();
            var serialized = new SerializedObject(image);
            var serializedColor = serialized.FindProperty("m_Color").colorValue;
            Assert.AreEqual(0.2f, serializedColor.r, 0.001f);
            Assert.AreEqual(0.4f, serializedColor.g, 0.001f);
            Assert.AreEqual(0.8f, serializedColor.b, 0.001f);
            Assert.AreEqual(0.502f, serializedColor.a, 0.001f);
        }

        [Test]
        public void CanvasEnsureRemovesStandaloneModuleWhenInputSystemModulePreferred()
        {
            RequireUgui();
            var eventSystem = CreateEventSystemWithStandaloneAndInputSystemModules();

            ChievfxMcpUguiExtension.SetPreferInputSystemUiModuleOverrideForTests(true);
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            Assert.IsNotNull(eventSystem.GetComponent(RequireLoadedType("UnityEngine.InputSystem.UI.InputSystemUIInputModule")));
            Assert.IsNull(eventSystem.GetComponent(RequireLoadedType("UnityEngine.EventSystems.StandaloneInputModule")));
            Assert.AreEqual(1, CountBaseInputModules(eventSystem));
        }

        [Test]
        public void CanvasEnsureRemovesInputSystemModuleWhenStandaloneModulePreferred()
        {
            RequireUgui();
            var eventSystem = CreateEventSystemWithStandaloneAndInputSystemModules();

            ChievfxMcpUguiExtension.SetPreferInputSystemUiModuleOverrideForTests(false);
            RunTool("ugui-canvas-ensure", "{'name':'UiRoot'}");

            Assert.IsNotNull(eventSystem.GetComponent(RequireLoadedType("UnityEngine.EventSystems.StandaloneInputModule")));
            Assert.IsNull(eventSystem.GetComponent(RequireLoadedType("UnityEngine.InputSystem.UI.InputSystemUIInputModule")));
            Assert.AreEqual(1, CountBaseInputModules(eventSystem));
        }

        [Test]
        public void RuntimeStatusIsGatedOutsidePlayModeAndIncludesCoordinateShape()
        {
            RequireUgui();

            var status = (Dictionary<string, object?>)ChievfxMcpUguiExtension.ReadResourceForTests("chievfx://extensions/chievfx.ugui/runtime/status")!;

            Assert.AreEqual(false, status["runtimeAvailable"]);
            Assert.AreEqual(false, status["playMode"]);
            Assert.AreEqual(0, status["canvasCount"]);
            Assert.IsTrue(StringArray(status, "warnings").Any(warning => warning.Contains("Play Mode")));
            var coordinateConvention = Row(status, "coordinateConvention");
            Assert.AreEqual("bottom-left", coordinateConvention["origin"]);
            Assert.IsNotNull(coordinateConvention["screenSize"]);
            Assert.IsNotNull(coordinateConvention["normalizedPosition"]);
        }

        [Test]
        public void RuntimeProbeOutsidePlayModeThrows()
        {
            RequireUgui();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RunExtensionTool("ui-runtime-probe", "{'x':0.5,'y':0.5,'isNormalized':true}"));

            StringAssert.Contains("Play Mode", ex!.Message);
            StringAssert.Contains("probe", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [UnityTest]
        public System.Collections.IEnumerator RuntimeProbeReturnsTopToBottomStackForOverlappingControls()
        {
            RequireUgui();

            RunTool("ugui-canvas-ensure", "{'name':'UiRoot','rect':{'preset':'anchor-size','anchorMin':{'x':0.5,'y':0.5},'anchorMax':{'x':0.5,'y':0.5},'size':{'x':800,'y':600}}}");
            RunTool("ugui-create-control", "{'controlType':'button','name':'BottomButton','parentPath':'UiRoot','text':'Bottom','rect':{'preset':'center','size':{'x':280,'y':120}}}");
            RunTool("ugui-create-control", "{'controlType':'button','name':'TopButton','parentPath':'UiRoot','text':'Top','rect':{'preset':'center','size':{'x':280,'y':120}}}");
            DisableEventSystemInputModules();

            yield return new EnterPlayMode();
            yield return null;
            Canvas.ForceUpdateCanvases();
            yield return null;

            var probe = RunExtensionTool("ui-runtime-probe", "{'x':0.5,'y':0.5,'isNormalized':true}");
            Assert.AreEqual(true, probe["runtimeAvailable"]);
            var hits = Rows(Row(probe, "ugui"), "hits");
            Assert.GreaterOrEqual(hits.Length, 2, string.Join("; ", StringArray(probe, "warnings")));

            var topIndex = Array.FindIndex(hits, row => ((string)row["path"]!).Contains("TopButton"));
            var bottomIndex = Array.FindIndex(hits, row => ((string)row["path"]!).Contains("BottomButton"));
            Assert.GreaterOrEqual(topIndex, 0);
            Assert.GreaterOrEqual(bottomIndex, 0);
            Assert.Less(topIndex, bottomIndex);
            Assert.AreEqual("bottom-left", Row(probe, "probe")["origin"]);
            Assert.IsNotNull(hits[topIndex]["controls"]);
        }

        private static Dictionary<string, object?> RunExtensionTool(string toolName, string argsJson)
        {
            ChievfxMcpRuntimeUiAdapterRegistry.EnsureRegistered();
            var method = typeof(ChievfxMcpExtensionRegistry).GetMethod(
                "TryRunTool",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(method);
            var parameters = new object?[] { toolName, JObject.Parse(argsJson), null };
            Assert.IsTrue((bool)method!.Invoke(null, parameters)!);
            return (Dictionary<string, object?>)parameters[2]!;
        }

        private static Dictionary<string, object?> RunTool(string toolName, string argsJson)
        {
            return (Dictionary<string, object?>)ChievfxMcpUguiExtension.RunToolForTests(toolName, argsJson)!;
        }

        private static void RequireUgui()
        {
            var status = (Dictionary<string, object?>)ChievfxMcpUguiExtension.ReadResourceForTests("chievfx://extensions/chievfx.ugui/status")!;
            if (!status.TryGetValue("ugui", out _))
            {
                Assert.Ignore((string)status["reason"]!);
            }
        }

        private static Dictionary<string, object?> Row(Dictionary<string, object?> source, string key)
        {
            return (Dictionary<string, object?>)source[key]!;
        }

        private static Dictionary<string, object?>[] Rows(Dictionary<string, object?> source, string key)
        {
            return ((object[])source[key]!).Cast<Dictionary<string, object?>>().ToArray();
        }

        private static string[] StringArray(Dictionary<string, object?> source, string key)
        {
            return ((object[])source[key]!).Cast<string>().ToArray();
        }

        private static bool TextMeshProLoaded()
        {
            var status = (Dictionary<string, object?>)ChievfxMcpUguiExtension.ReadResourceForTests("chievfx://extensions/chievfx.ugui/status")!;
            return Equals(Row(status, "textMeshPro")["loaded"], true);
        }

        private static string CreateTempSpriteTexture(string filename, Vector4 border)
        {
            const string folder = "Assets/Editor/ChievfxMcpTests/Generated";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/Editor/ChievfxMcpTests", "Generated");
            }

            var path = folder + "/" + filename;
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = Enumerable.Repeat(Color.white, 64).ToArray();
            texture.SetPixels(pixels);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
            importer.spritePixelsPerUnit = 100f;
            importer.spriteBorder = border;
            importer.SaveAndReimport();
            return path;
        }

        private static void AssertVector(Dictionary<string, object?> source, string key, float x, float y)
        {
            var row = (Dictionary<string, float>)source[key]!;
            Assert.AreEqual(x, row["x"], 0.001f);
            Assert.AreEqual(y, row["y"], 0.001f);
        }

        private static GameObject CreateEventSystemWithStandaloneAndInputSystemModules()
        {
            var eventSystemType = RequireLoadedType("UnityEngine.EventSystems.EventSystem");
            var standaloneInputModuleType = RequireLoadedType("UnityEngine.EventSystems.StandaloneInputModule");
            var inputSystemUiInputModuleType = RequireLoadedType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent(eventSystemType);
            eventSystem.AddComponent(standaloneInputModuleType);
            eventSystem.AddComponent(inputSystemUiInputModuleType);
            return eventSystem;
        }

        private static int CountBaseInputModules(GameObject target)
        {
            var baseInputModuleType = RequireLoadedType("UnityEngine.EventSystems.BaseInputModule");
            return target.GetComponents(baseInputModuleType).Length;
        }

        private static void DisableEventSystemInputModules()
        {
            var baseInputModuleType = RequireLoadedType("UnityEngine.EventSystems.BaseInputModule");
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                foreach (var module in root.GetComponentsInChildren(baseInputModuleType, true).OfType<Behaviour>())
                {
                    module.enabled = false;
                }
            }
        }

        private static Type RequireLoadedType(string fullName)
        {
            var type = FindLoadedType(fullName);
            if (type == null)
            {
                Assert.Ignore(fullName + " is not loaded in this project.");
            }

            return type!;
        }

        private static Component? FindComponentByTypeName(string typeName)
        {
            return SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .FirstOrDefault(component => component != null && component.GetType().Name == typeName);
        }

        private static void AssertEventSystemInputModuleMatchesProject()
        {
            if (FindLoadedType("UnityEngine.InputSystem.UI.InputSystemUIInputModule") == null
                || !ProjectPrefersInputSystemUiModule())
            {
                return;
            }

            Assert.IsNotNull(FindComponentByTypeName("InputSystemUIInputModule"));
        }

        private static bool ProjectPrefersInputSystemUiModule()
        {
            var activeInputHandling = typeof(UnityEditor.PlayerSettings).GetProperty("activeInputHandling");
            var value = activeInputHandling?.GetValue(null)?.ToString();
            return value != null
                && value.IndexOf("InputSystem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Type? FindLoadedType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }
    }
}
