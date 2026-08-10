#nullable enable
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Chievfx.Mcp.Editor.Tests
{
    public sealed class ChievfxMcpBridgeEventJournalTests
    {
        private string? savedEventStream;

        // The journal writes to the one bridge events.json path, so running this suite inside a live
        // editor would otherwise discard the session's event history. Put back whatever was there.
        // The running bridge holds its stream in memory and is the only writer, so its next flush
        // reconciles the file regardless of what these tests left in it.
        [SetUp]
        public void SetUp()
        {
            savedEventStream = File.Exists(ChievfxMcpToolPolicy.BridgeEventPath)
                ? File.ReadAllText(ChievfxMcpToolPolicy.BridgeEventPath)
                : null;
        }

        [TearDown]
        public void TearDown()
        {
            if (savedEventStream != null)
            {
                File.WriteAllText(ChievfxMcpToolPolicy.BridgeEventPath, savedEventStream);
            }
            else if (File.Exists(ChievfxMcpToolPolicy.BridgeEventPath))
            {
                File.Delete(ChievfxMcpToolPolicy.BridgeEventPath);
            }
        }

        // BridgeEventJournal assembles events.json by hand from cached per-record JSON fragments instead
        // of calling JsonConvert.SerializeObject on the whole stream (that cost two full serializations of
        // a ~512 KB document on every 50 ms flush). The hand-built envelope therefore has to stay in step
        // with BridgeEventStream's shape - add or reorder a property there and the mirror silently drifts.
        // Re-serializing what we read back is what catches that: it is byte-identical only if the envelope
        // still matches what Newtonsoft would have produced.
        [Test]
        public void Flush_WritesDocumentIdenticalToNewtonsoftSerialization()
        {
            var journal = new BridgeEventJournal();
            journal.Write("test", "journal-shape", "info", "envelope parity probe");
            journal.Flush();

            var written = File.ReadAllText(ChievfxMcpToolPolicy.BridgeEventPath);
            var parsed = JsonConvert.DeserializeObject<BridgeEventStream>(written, BridgeRuntimeState.JsonOptions);
            Assert.IsNotNull(parsed, "events.json should deserialize back into a BridgeEventStream.");

            Assert.AreEqual(
                JsonConvert.SerializeObject(parsed, BridgeRuntimeState.JsonOptions),
                written,
                "Hand-assembled events.json drifted from BridgeEventStream's serialized shape.");
        }

        [Test]
        public void Write_KeepsStreamWithinCharBudget()
        {
            var journal = new BridgeEventJournal();

            // Messages long enough that the record-count cap is not the binding constraint - the char
            // budget is. This is the steady state that used to pin the stream at MaxEventStreamChars and
            // make every flush pay for the serialize/trim/re-serialize loop.
            var message = new string('x', McpLimits.MaxEventMessageChars);
            for (var i = 0; i < McpLimits.MaxEventEntries + 50; i++)
            {
                journal.Write("test", "budget", "info", message);
            }

            journal.Flush();

            var written = File.ReadAllText(ChievfxMcpToolPolicy.BridgeEventPath);
            Assert.LessOrEqual(
                written.Length,
                McpLimits.MaxEventStreamChars,
                "events.json exceeded MaxEventStreamChars.");

            var parsed = JsonConvert.DeserializeObject<BridgeEventStream>(written, BridgeRuntimeState.JsonOptions);
            Assert.IsNotNull(parsed);
            Assert.Greater(parsed!.events.Count, 0, "Budget trimming should not empty the stream.");
            Assert.Greater(
                parsed.truncatedBeforeEventId,
                0,
                "Dropping records for the char budget should advance the truncation watermark.");
        }
    }
}
