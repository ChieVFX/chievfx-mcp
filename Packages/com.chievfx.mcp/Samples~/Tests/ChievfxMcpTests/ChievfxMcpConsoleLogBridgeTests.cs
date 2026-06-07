#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpConsoleLogBridgeTests
    {
        private ConsoleLogBridgeService service = null!;

        [SetUp]
        public void SetUp()
        {
            service = new ConsoleLogBridgeService();
            service.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            service.Clear();
        }

        [Test]
        public void TryInterpretContainsAsSeverityLevels_MatchesExactErrorToken()
        {
            Assert.IsTrue(
                ConsoleLogBridgeService.TryInterpretContainsAsSeverityLevels("error", out var levels, out var note),
                "Expected exact contains token to map to severity levels.");

            CollectionAssert.AreEquivalent(
                new[] { "Error", "Exception", "Assert" },
                levels);
            StringAssert.Contains("severity", note);
        }

        [Test]
        public void TryInterpretContainsAsSeverityLevels_RejectsMessageSubstringSearch()
        {
            Assert.IsFalse(
                ConsoleLogBridgeService.TryInterpretContainsAsSeverityLevels("error CS0234", out _, out _),
                "Multi-word contains should stay a message filter.");
        }

        [Test]
        public void Get_ReinterpretsContainsErrorForAssertWithoutErrorText()
        {
            ConsoleLogBridgeService.CollectLog("Map must be contained in state", string.Empty, LogType.Assert);

            dynamic result = service.Get(new JObject
            {
                ["contains"] = "error",
                ["includeUnityConsole"] = false,
            });

            Assert.AreEqual(1, (int)result.count);
            Assert.IsNotNull(result.filterNote);
        }

        [Test]
        public void Get_KeepsMessageContainsForSpecificCompilerErrors()
        {
            ConsoleLogBridgeService.CollectLog("error CS0234: missing type", string.Empty, LogType.Error);
            ConsoleLogBridgeService.CollectLog("Map must be contained in state", string.Empty, LogType.Assert);

            dynamic result = service.Get(new JObject
            {
                ["contains"] = "error CS0234",
                ["includeUnityConsole"] = false,
            });

            Assert.AreEqual(1, (int)result.count);
            Assert.IsNull(result.filterNote);
        }

        [Test]
        public void Get_ExpandsConsoleErrorsLevelAlias()
        {
            ConsoleLogBridgeService.CollectLog("Map must be contained in state", string.Empty, LogType.Assert);
            ConsoleLogBridgeService.CollectLog("benign", string.Empty, LogType.Log);

            dynamic result = service.Get(new JObject
            {
                ["levels"] = new JArray("ConsoleErrors"),
                ["includeUnityConsole"] = false,
            });

            Assert.AreEqual(1, (int)result.count);
        }
    }
}
