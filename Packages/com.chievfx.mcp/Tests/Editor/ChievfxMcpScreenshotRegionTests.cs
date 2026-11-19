#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    // screenshot-game-view crops to left/top/right/bottom and ui-runtime-click/drag/probe undo that crop
    // with the same four values. Both sides go through ScreenshotRegion, and these tests pin the contract
    // that makes a position read off a cropped PNG land on the right Screen pixel.
    public sealed class ChievfxMcpScreenshotRegionTests
    {
        [Test]
        public void MissingEdgesMeanTheFullView()
        {
            Assert.IsFalse(ScreenshotRegion.HasAnyArgument(new JObject()));
            Assert.IsTrue(ScreenshotRegion.Read(new JObject()).IsFull);
            Assert.IsTrue(ScreenshotRegion.Read(new JObject { ["left"] = null }).IsFull);
        }

        [Test]
        public void OmittedEdgesFallBackToTheFullViewEdge()
        {
            var region = ScreenshotRegion.Read(new JObject { ["left"] = 0.25, ["bottom"] = "0.5" });

            Assert.AreEqual(0.25f, region.Left, 1e-5f);
            Assert.AreEqual(0f, region.Top, 1e-5f);
            Assert.AreEqual(1f, region.Right, 1e-5f);
            Assert.AreEqual(0.5f, region.Bottom, 1e-5f);
            Assert.IsFalse(region.IsFull);
        }

        [TestCase(0.6, 0.0, 0.4, 1.0, "right")]
        [TestCase(0.0, 0.5, 1.0, 0.5, "bottom")]
        [TestCase(-0.5, 0.0, 1.0, 1.0, "left")]
        [TestCase(0.0, 0.0, 480.0, 1.0, "right")]
        public void SwappedOrOutOfRangeEdgesAreRejectedNotClamped(double left, double top, double right, double bottom, string offendingEdge)
        {
            var args = new JObject { ["left"] = left, ["top"] = top, ["right"] = right, ["bottom"] = bottom };

            var error = Assert.Throws<ArgumentException>(() => ScreenshotRegion.Read(args));
            StringAssert.Contains(offendingEdge, error!.Message);
        }

        [Test]
        public void CroppedPngPositionMapsBackOntoTheFullView()
        {
            var region = new ScreenshotRegion(0.5f, 0.25f, 1f, 0.75f);

            var topLeft = region.ToFullViewNormalized(0f, 0f);
            var centre = region.ToFullViewNormalized(0.5f, 0.5f);
            var bottomRight = region.ToFullViewNormalized(1f, 1f);

            Assert.AreEqual(new Vector2(0.5f, 0.25f), topLeft);
            Assert.AreEqual(new Vector2(0.75f, 0.5f), centre);
            Assert.AreEqual(new Vector2(1f, 0.75f), bottomRight);
            Assert.AreEqual(new Vector2(0.3f, 0.9f), ScreenshotRegion.Full.ToFullViewNormalized(0.3f, 0.9f), "The full region is the identity.");
        }

        [Test]
        public void PixelRectCoversTheRegionAndNeverCollapses()
        {
            var rect = new ScreenshotRegion(0.5f, 0.25f, 1f, 0.75f).ToPixelRect(1920, 1080);
            Assert.AreEqual(new RectInt(960, 270, 960, 540), rect);

            var hairline = new ScreenshotRegion(0.5f, 0.5f, 0.50001f, 0.50001f).ToPixelRect(1920, 1080);
            Assert.GreaterOrEqual(hairline.width, 1);
            Assert.GreaterOrEqual(hairline.height, 1);
            Assert.LessOrEqual(hairline.xMax, 1920);
            Assert.LessOrEqual(hairline.yMax, 1080);
        }

        [Test]
        public void ScreenshotSpaceClickHonoursTheCrop()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            // The centre of a PNG cropped to the right half, top-to-middle band of the view.
            var request = new JObject
            {
                ["x"] = 0.5f,
                ["y"] = 0.5f,
                ["space"] = "screenshot",
                ["left"] = 0.5f,
                ["top"] = 0f,
                ["right"] = 1f,
                ["bottom"] = 0.5f,
            };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest(request);

            var screenPosition = (JObject)adapterRequest["screenPosition"]!;
            Assert.AreEqual(screenSize.x * 0.75f, screenPosition["x"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.75f, screenPosition["y"]!.Value<float>(), 0.01f, "Full-view y 0.25 from the top is 0.75 from the bottom.");
            foreach (var key in new[] { "x", "y", "space", "left", "top", "right", "bottom" })
            {
                Assert.IsNull(adapterRequest[key], $"Adapters must never see '{key}'.");
            }
        }

        [Test]
        public void ScreenshotSpaceDragHonoursTheCropAtBothEnds()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var request = new JObject
            {
                ["x"] = 0f,
                ["y"] = 1f,
                ["toX"] = 1f,
                ["toY"] = 1f,
                ["space"] = "screenshot",
                ["left"] = 0.25f,
                ["top"] = 0.25f,
                ["right"] = 0.75f,
                ["bottom"] = 0.5f,
            };
            var geometry = ChievfxMcpRuntimeUiAdapterRegistry.ReadRuntimeDragGeometry(request, new List<string>());

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterDragRequest(request, geometry);

            Assert.AreEqual(screenSize.x * 0.25f, adapterRequest["x"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.5f, adapterRequest["y"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.x * 0.75f, adapterRequest["toX"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.5f, adapterRequest["toY"]!.Value<float>(), 0.01f);
            Assert.IsNull(adapterRequest["left"]);
            Assert.IsNull(adapterRequest["bottom"]);
        }

        [Test]
        public void ScreenshotSpaceWithoutACropStillMapsAgainstTheFullView()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var request = new JObject { ["x"] = 0.5f, ["y"] = 0.25f, ["space"] = "screenshot" };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest(request);

            var screenPosition = (JObject)adapterRequest["screenPosition"]!;
            Assert.AreEqual(screenSize.x * 0.5f, screenPosition["x"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.75f, screenPosition["y"]!.Value<float>(), 0.01f);
        }
    }
}
