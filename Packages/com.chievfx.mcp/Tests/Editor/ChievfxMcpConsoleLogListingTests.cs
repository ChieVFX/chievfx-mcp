#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    // console-get-logs listing: duplicate groups, per-entry ids, times and cursors, the Unity Console rows that
    // repeat captured logs, and the two truncation flags. Logs go through CollectLog under a unique token, so the
    // session's captured logs are read around but never cleared.
    public sealed class ChievfxMcpConsoleLogListingTests
    {
        private readonly ConsoleLogBridgeService service = new();

        [Test]
        public void DuplicateGroupsIgnoreNumbersAfterTheHeadlineOnly()
        {
            var entries = new List<LogEntryDto>
            {
                Entry("Error", "[] ControlHorn: no AudioSource!\nStack generation time: total: 412 ms", "Horn:Update () (at Horn.cs:10)"),
                Entry("Error", "[] ControlHorn: no AudioSource!\nStack generation time: total: 360 ms", "Horn:Update () (at Horn.cs:12)"),
                Entry("Exception", "NullReferenceException: Object reference not set to an instance of an object", "Car:Drive () (at Car.cs:5)"),
                Entry("Exception", "NullReferenceException: Object reference not set to an instance of an object", "Phone:Open () (at Phone.cs:5)"),
                Entry("Log", "Loaded 5 items", string.Empty),
                Entry("Log", "Loaded 6 items", string.Empty),
            };

            var groups = ConsoleLogBridgeService.CollapseDuplicates(entries);

            Assert.AreEqual(5, groups.Count, string.Join(" | ", groups.Select(group => group.entry.Message + " x" + group.repeats)));
            Assert.AreEqual(2, groups.Single(group => group.entry.Message.StartsWith("[] ControlHorn", StringComparison.Ordinal)).repeats);
            Assert.AreEqual(2, groups.Count(group => group.entry.LogType == "Exception"), "NREs from different call sites stay apart.");
            Assert.AreEqual(2, groups.Count(group => group.entry.Message.StartsWith("Loaded", StringComparison.Ordinal)), "A number in the headline keeps entries apart.");
        }

        [Test]
        public void DuplicateGroupsSitAtTheirLatestOccurrence()
        {
            var first = Entry("Log", "alpha", string.Empty);
            var beta = Entry("Log", "beta", string.Empty);
            var second = Entry("Log", "alpha", string.Empty);

            var groups = ConsoleLogBridgeService.CollapseDuplicates(new List<LogEntryDto> { first, beta, second });

            Assert.AreSame(beta, groups[0].entry);
            Assert.AreSame(second, groups[1].entry, "The row carries the latest occurrence.");
            Assert.AreEqual(2, groups[1].repeats);
        }

        [Test]
        public void ConsoleRowsAlreadyCapturedAreDropped()
        {
            var olderOnlyInConsole = Entry("Log", "logged before capture", string.Empty);
            var repeatedOlder = Entry("Log", "same text", string.Empty);
            var repeatedCaptured = Entry("Log", "same text", string.Empty);
            var captured = Entry("Error", "captured error", string.Empty);
            var console = new List<LogEntryDto> { olderOnlyInConsole, repeatedOlder, repeatedCaptured, Entry("Error", "captured error ", string.Empty) };

            var kept = ConsoleLogBridgeService.ExcludeCapturedConsoleEntries(console, new List<LogEntryDto> { Entry("Log", "same text", string.Empty), captured });

            CollectionAssert.AreEqual(new[] { olderOnlyInConsole, repeatedOlder }, kept);
        }

        [Test]
        public void ConsoleModeFlagsDecideTheLevel()
        {
            Assert.AreEqual("Log", ConsoleLogBridgeService.ClassifyConsoleMode(1 << 2));
            Assert.AreEqual("Log", ConsoleLogBridgeService.ClassifyConsoleMode(1 << 10));
            Assert.AreEqual("Error", ConsoleLogBridgeService.ClassifyConsoleMode(8405248), "Debug.LogError as Unity 6 reports it.");
            Assert.AreEqual("Exception", ConsoleLogBridgeService.ClassifyConsoleMode((1 << 17) | (1 << 8)));
            Assert.AreEqual("Warning", ConsoleLogBridgeService.ClassifyConsoleMode(1 << 9));
            Assert.AreEqual("Assert", ConsoleLogBridgeService.ClassifyConsoleMode(1 << 21));
            Assert.IsNull(ConsoleLogBridgeService.ClassifyConsoleMode(1 << 14));
        }

        [Test]
        public void IdenticalLogsKeepTheirOwnIdsTimesAndCursors()
        {
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            ConsoleLogBridgeService.CollectLog("popup_closed " + token, string.Empty, LogType.Log);
            ConsoleLogBridgeService.CollectLog("popup_closed " + token, string.Empty, LogType.Log);

            var listed = Entries(service.Get(new JObject
            {
                ["contains"] = token,
                ["levels"] = new JArray("Log"),
                ["dedupe"] = false,
                ["includeUnityConsole"] = false,
            }));

            Assert.AreEqual(2, listed.Length);
            var ids = listed.Select(row => (string)row["id"]).ToArray();
            CollectionAssert.AllItemsAreUnique(ids);
            Assert.IsTrue(ids.All(id => id.StartsWith("e", StringComparison.Ordinal)), string.Join(", ", ids));
            Assert.IsTrue(listed.All(row => row.ContainsKey("time") && row.ContainsKey("eventId")));

            var single = Member(service.GetSingle(new JObject { ["id"] = ids[1] }), "entry") as Dictionary<string, object>;
            Assert.IsNotNull(single);
            Assert.AreEqual(listed[1]["eventId"], single!["eventId"], "console-get-logs-single returns the entry that was listed.");

            var deduped = Entries(service.Get(new JObject
            {
                ["contains"] = token,
                ["levels"] = new JArray("Log"),
                ["includeUnityConsole"] = false,
            }));
            Assert.AreEqual(1, deduped.Length);
            Assert.AreEqual(2, deduped[0]["repeats"]);
            Assert.AreEqual(ids[1], deduped[0]["id"], "A group row carries the latest entry's id.");
        }

        [Test]
        public void ShortenedTextIsNotReportedAsAnIncompleteList()
        {
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            ConsoleLogBridgeService.CollectLog(token + " " + new string('x', McpLimits.MaxLogMessageChars + 50), string.Empty, LogType.Log);

            var result = service.Get(new JObject
            {
                ["contains"] = token,
                ["levels"] = new JArray("Log"),
                ["includeUnityConsole"] = false,
            });

            Assert.AreEqual(0, Member(result, "dropped"));
            Assert.AreEqual(false, Member(result, "truncated"));
            Assert.AreEqual(true, Member(result, "textTruncated"));
        }

        [Test]
        public void MarkerIsFoundAfterAProjectLoggerPrefix()
        {
            Assert.AreEqual("r4mark", ConsoleLogBridgeService.TryParseLogMarker("[UnityLogs] MCPEventReachedLocation(r4mark)"));
            Assert.AreEqual("plain", ConsoleLogBridgeService.TryParseLogMarker("MCPEventReachedLocation(plain)\nstack line"));
            Assert.IsNull(ConsoleLogBridgeService.TryParseLogMarker("no beacon here"));
            Assert.IsNull(ConsoleLogBridgeService.TryParseLogMarker("first line\nMCPEventReachedLocation(late)"), "Only the first line can carry the beacon.");
        }

        [Test]
        public void SourceLinksShrinkToPathAndLine()
        {
            var line = "Foo.Bar () (at <a href=\"Assets/Scripts/Foo.cs\" line=\"107\">Assets/Scripts/Foo.cs</a>:107)";
            Assert.AreEqual("Foo.Bar () (at Assets/Scripts/Foo.cs:107)", ConsoleLogBridgeService.StripSourceLinks(line));
        }

        [Test]
        public void LogsOfACallAreFoundAfterTheBufferIsFull()
        {
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            for (var i = 0; i <= McpLimits.MaxLogEntries; i++)
            {
                ConsoleLogBridgeService.CollectLog(token + " filler " + i, string.Empty, LogType.Log);
            }

            var start = ConsoleLogBridgeService.GetLogEntryCount();
            ConsoleLogBridgeService.CollectLog(token + " mine", string.Empty, LogType.Warning);
            var logs = ConsoleLogBridgeService.CreateLogOutputs(start, true, null, includeStackTrace: false, maxEntries: 10, out _);

            Assert.AreEqual(1, logs.Length, "script-execute reads its logs from this start: a full buffer kept the count still and lost them.");
            StringAssert.Contains(token + " mine", Newtonsoft.Json.JsonConvert.SerializeObject(logs[0]));
        }

        [Test]
        public void ACallOverTheLogCapKeepsItsWarningsAndItsLastLogs()
        {
            var written = new List<LogEntryDto>();
            for (var i = 0; i < 80; i++)
            {
                written.Add(Entry("Log", "noise " + i, string.Empty));
            }

            written.Insert(30, Entry("Error", "error in the middle", string.Empty));
            written.Add(Entry("Warning", "my tail", string.Empty));

            var kept = ConsoleLogBridgeService.SelectKeptLogs(written.ToArray(), 10);

            Assert.AreEqual(10, kept.Length);
            Assert.AreEqual("noise 0", kept[0].Message, "the first plain logs stay");
            Assert.IsTrue(kept.Any(entry => entry.Message == "error in the middle"));
            Assert.AreEqual("my tail", kept[kept.Length - 1].Message, "the script's own line after a burst of logs stays");
            Assert.AreEqual("noise 79", kept[kept.Length - 2].Message, "the last plain logs stay");
            CollectionAssert.AreEqual(kept.Select(entry => written.IndexOf(entry)).OrderBy(index => index), kept.Select(entry => written.IndexOf(entry)), "kept in written order");
        }

        [Test]
        public void ACallUnderTheLogCapKeepsEverything()
        {
            var written = new[] { Entry("Log", "a", string.Empty), Entry("Warning", "b", string.Empty) };
            Assert.AreSame(written, ConsoleLogBridgeService.SelectKeptLogs(written, 10));
        }

        [Test]
        public void LogsOfACallAreReadOffTheMainThread()
        {
            // A timed-out async script's reply, logs included, is built on a worker thread, where EditorPrefs throws.
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            var start = ConsoleLogBridgeService.GetLogEntryCount();
            ConsoleLogBridgeService.CollectLog("<b>" + token + "</b>", string.Empty, LogType.Log);

            var logs = System.Threading.Tasks.Task.Run(() => ConsoleLogBridgeService.CreateLogOutputs(start, true, null, includeStackTrace: false, maxEntries: 10, out _)).Result;

            Assert.IsTrue(logs.Any(log => Newtonsoft.Json.JsonConvert.SerializeObject(log).Contains(token)), "the call's log is in its reply");
        }

        [Test]
        public void AListRowDropsTheStackALoggerWroteIntoTheMessage()
        {
            var message = "[] Request error[1] timeout\nLog stack:\n"
                          + "at Common.Logging.Utils.StackTraceUtils.ExtractStackTraceInternal (<a href=\"Assets/Scripts/Common/Logging/Utils/StackTraceUtils.cs\" line=\"107\">Assets/Scripts/Common/Logging/Utils/StackTraceUtils.cs</a>:107)\n"
                          + "Net.Requests:Send () (at Assets/Scripts/Net/Requests.cs:40)\n"
                          + "at UnityEngine.Object.Internal_CloneSingleWithParent_Injected\n"
                          + "at UnityEngine.Object.Instantiate[ControlHorn] in /Users/bokken/build/UnityEngineObject.bindings.cs:line 668\n"
                          + "Stack generation time: total: 332 extract: 275 format: 57 us";

            var row = ConsoleLogBridgeService.JoinLinesWithoutStackFrames(ConsoleLogBridgeService.StripSourceLinks(message), out var dropped);

            Assert.AreEqual("[] Request error[1] timeout", row);
            Assert.AreEqual(5, dropped);
            Assert.AreEqual("we waited at least 3 s", ConsoleLogBridgeService.JoinLinesWithoutStackFrames("we waited\nat least 3 s", out _), "prose that starts with 'at' stays");
        }

        [Test]
        public void AMultiLineMessageIsListedOnOneLine()
        {
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            ConsoleLogBridgeService.CollectLog("[OnlineEvents] message: {\n\"" + token + "\": true\n}\n", string.Empty, LogType.Log);

            var listed = Entries(service.Get(new JObject
            {
                ["contains"] = token,
                ["levels"] = new JArray("Log"),
                ["includeUnityConsole"] = false,
            }));

            Assert.AreEqual("[OnlineEvents] message: { \"" + token + "\": true }", listed.Single()["msg"]);
        }

        [Test]
        public void TheSingleEntryTakesAnEventIdAsPrintedByEvents()
        {
            var token = "ChievfxLogListing_" + Guid.NewGuid().ToString("N");
            ConsoleLogBridgeService.CollectLog(token, string.Empty, LogType.Log);
            var listed = Entries(service.Get(new JObject { ["contains"] = token, ["levels"] = new JArray("Log"), ["includeUnityConsole"] = false }));
            var eventId = listed.Single()["eventId"].ToString();

            foreach (var id in new[] { eventId, "#" + eventId })
            {
                Assert.IsNotNull(Member(service.GetSingle(new JObject { ["id"] = id }), "entry"), id);
            }
        }

        private static LogEntryDto Entry(string level, string message, string stack)
        {
            return new LogEntryDto(level, message, DateTime.UtcNow, stack);
        }

        private static Dictionary<string, object>[] Entries(object result)
        {
            return ((IEnumerable)Member(result, "entries")!).Cast<Dictionary<string, object>>().ToArray();
        }

        private static object? Member(object source, string name)
        {
            return source.GetType().GetProperty(name)?.GetValue(source);
        }
    }
}
