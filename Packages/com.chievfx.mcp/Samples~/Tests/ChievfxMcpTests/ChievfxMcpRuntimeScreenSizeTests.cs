#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpRuntimeScreenSizeTests
    {
        [Test]
        public void ResolvePrefersGameViewTargetSizeOverScreenSize()
        {
            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(
                new Vector2(2340f, 1080f),
                new Vector2(1414f, 1036f),
                () => new[] { new Vector2(2340f, 1080f) },
                out var source);

            Assert.AreEqual(new Vector2(2340f, 1080f), resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.GameViewSource, source);
        }

        [Test]
        public void ResolveFallsBackToLargestCanvasWhenGameViewSizeIsUnavailable()
        {
            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(
                null,
                new Vector2(1414f, 1036f),
                () => new[] { new Vector2(800f, 600f), new Vector2(2340f, 1080f) },
                out var source);

            Assert.AreEqual(new Vector2(2340f, 1080f), resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.CanvasSource, source);
        }

        [Test]
        public void ResolveFallsBackToScreenSizeWhenNothingElseIsUsable()
        {
            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(
                new Vector2(0f, 0f),
                new Vector2(1414f, 1036f),
                () => Array.Empty<Vector2>(),
                out var source);

            Assert.AreEqual(new Vector2(1414f, 1036f), resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.ScreenSource, source);
        }

        [Test]
        public void ResolveIgnoresDegenerateCandidateSizes()
        {
            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(
                new Vector2(float.NaN, 1080f),
                new Vector2(1414f, 1036f),
                () => new[] { new Vector2(0f, 0f), new Vector2(float.PositiveInfinity, 100f) },
                out var source);

            Assert.AreEqual(new Vector2(1414f, 1036f), resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.ScreenSource, source);
        }

        [Test]
        public void ResolveSurvivesThrowingCanvasEnumeration()
        {
            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(
                null,
                new Vector2(1414f, 1036f),
                () => throw new InvalidOperationException("canvas walk failed"),
                out var source);

            Assert.AreEqual(new Vector2(1414f, 1036f), resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.ScreenSource, source);
        }

        // The live editor path: this is the value every normalized<->pixel conversion divides by, so it has to
        // match what the Game View actually renders at rather than the window Screen.* reports.
        [Test]
        public void ResolveMatchesGameViewTargetSizeInEditor()
        {
            var targetSize = ChievfxMcpRuntimeScreenSize.TryGetGameViewTargetSize();
            if (targetSize == null)
            {
                Assert.Ignore("No Game View target size available in this editor session.");
            }

            var resolved = ChievfxMcpRuntimeScreenSize.Resolve(out var source);

            Assert.AreEqual(targetSize!.Value, resolved);
            Assert.AreEqual(ChievfxMcpRuntimeScreenSize.GameViewSource, source);
        }

        [Test]
        public void DescribeResolvedSourceIsNullWhenScreenSizeAlreadyAgrees()
        {
            Assert.IsNull(ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(ChievfxMcpRuntimeScreenSize.UnityScreenSize));
        }

        [Test]
        public void DescribeResolvedSourceNamesSourceWhenScreenSizeDisagrees()
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.UnityScreenSize;
            var disagreeing = new Vector2(screenSize.x + 137f, screenSize.y + 41f);

            Assert.IsNotNull(ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(disagreeing));
        }

        [Test]
        public void ResolveWithCanvasSupplierDoesNotEnumerateWhenGameViewSizeIsAvailable()
        {
            if (ChievfxMcpRuntimeScreenSize.TryGetGameViewTargetSize() == null)
            {
                Assert.Ignore("No Game View target size available in this editor session.");
            }

            var enumerations = new List<int>();
            ChievfxMcpRuntimeScreenSize.Resolve(
                () =>
                {
                    enumerations.Add(1);
                    return Array.Empty<Vector2>();
                },
                out _);

            Assert.IsEmpty(enumerations);
        }
    }
}
