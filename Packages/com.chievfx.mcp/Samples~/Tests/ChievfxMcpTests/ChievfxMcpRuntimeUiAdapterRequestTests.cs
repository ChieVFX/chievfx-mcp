#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    /// <summary>
    /// Adapters must never have to interpret a coordinate space. The registry resolves x/y, isNormalized and
    /// space:"screenshot" once and hands adapters absolute Screen pixels; when it did not, a screenshot-space
    /// click was Y-flipped for the echo and the hit test but reached the adapter unflipped, so the dispatched
    /// PointerEventData carried a mirrored pressPosition while the tool reported success.
    /// </summary>
    public sealed class ChievfxMcpRuntimeUiAdapterRequestTests
    {
        [Test]
        public void ScreenshotSpaceIsResolvedToFlippedScreenPixels()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var request = new JObject
            {
                ["x"] = 0.5f,
                ["y"] = 0.25f,
                ["space"] = "screenshot",
            };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest((JObject)request.DeepClone());

            var screenPosition = ReadScreenPosition(adapterRequest);
            Assert.AreEqual(screenSize.x * 0.5f, screenPosition.x, 0.01f);
            Assert.AreEqual(screenSize.y * 0.75f, screenPosition.y, 0.01f, "Screenshot Y is top-left origin and must be flipped.");
            AssertCoordinateKeysStripped(adapterRequest);
        }

        [Test]
        public void NormalizedInputIsResolvedToScreenPixels()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var request = new JObject
            {
                ["x"] = 0.25f,
                ["y"] = 0.75f,
                ["isNormalized"] = true,
            };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest((JObject)request.DeepClone());

            var screenPosition = ReadScreenPosition(adapterRequest);
            Assert.AreEqual(screenSize.x * 0.25f, screenPosition.x, 0.01f);
            Assert.AreEqual(screenSize.y * 0.75f, screenPosition.y, 0.01f);
            AssertCoordinateKeysStripped(adapterRequest);
        }

        [Test]
        public void PixelInputSurvivesUnchanged()
        {
            var request = new JObject { ["x"] = 120f, ["y"] = 340f };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest((JObject)request.DeepClone());

            var screenPosition = ReadScreenPosition(adapterRequest);
            Assert.AreEqual(120f, screenPosition.x, 0.01f);
            Assert.AreEqual(340f, screenPosition.y, 0.01f);
        }

        [Test]
        public void TargetOnlyRequestIsPassedThroughWithoutAPosition()
        {
            var request = new JObject { ["path"] = "Canvas/Screen/Button" };

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterInteractionRequest((JObject)request.DeepClone());

            Assert.AreEqual("Canvas/Screen/Button", adapterRequest["path"]?.Value<string>());
            Assert.IsNull(adapterRequest["screenPosition"]);
        }

        [Test]
        public void DragScreenshotSpaceIsResolvedForBothEnds()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var request = new JObject
            {
                ["x"] = 0.3f,
                ["y"] = 0.2f,
                ["toX"] = 0.7f,
                ["toY"] = 0.2f,
                ["space"] = "screenshot",
            };
            var geometry = ChievfxMcpRuntimeUiAdapterRegistry.ReadRuntimeDragGeometry(request, new List<string>());

            var adapterRequest = ChievfxMcpRuntimeUiAdapterRegistry.CreateAdapterDragRequest((JObject)request.DeepClone(), geometry);

            Assert.AreEqual(screenSize.x * 0.3f, adapterRequest["x"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.8f, adapterRequest["y"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.x * 0.7f, adapterRequest["toX"]!.Value<float>(), 0.01f);
            Assert.AreEqual(screenSize.y * 0.8f, adapterRequest["toY"]!.Value<float>(), 0.01f);
            Assert.IsNull(adapterRequest["space"]);
            Assert.IsNull(adapterRequest["isNormalized"]);
        }

        [Test]
        public void UnresolvedCoordinateSpaceReachingAnAdapterIsRejected()
        {
            var request = new JObject { ["x"] = 0.5f, ["y"] = 0.5f, ["space"] = "screenshot" };

            Assert.Throws<System.ArgumentException>(
                () => ChievfxMcpRuntimeUiInteractionInput.EnsureNoUnresolvedCoordinateSpace(request));
        }

        [Test]
        public void ScreenSpaceIsAcceptedByTheAdapterGuard()
        {
            var request = new JObject { ["x"] = 0.5f, ["y"] = 0.5f, ["space"] = "screen" };

            Assert.DoesNotThrow(() => ChievfxMcpRuntimeUiInteractionInput.EnsureNoUnresolvedCoordinateSpace(request));
        }

        private static Vector2 ReadScreenPosition(JToken adapterRequest)
        {
            var screenPosition = adapterRequest["screenPosition"] as JObject;
            Assert.IsNotNull(screenPosition, "Adapters are handed an absolute screenPosition.");
            return new Vector2(screenPosition!["x"]!.Value<float>(), screenPosition["y"]!.Value<float>());
        }

        private static void AssertCoordinateKeysStripped(JToken adapterRequest)
        {
            Assert.IsNull(adapterRequest["x"]);
            Assert.IsNull(adapterRequest["y"]);
            Assert.IsNull(adapterRequest["space"]);
            Assert.IsNull(adapterRequest["isNormalized"]);
        }
    }
}
