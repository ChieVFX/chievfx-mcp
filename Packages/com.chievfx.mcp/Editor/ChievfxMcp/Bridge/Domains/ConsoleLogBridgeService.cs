#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ConsoleLogBridgeService : BridgeDomainServiceBase
    {
        // Rich-text style tags Unity inlines into console messages. Stripped (open + close) from
        // get-logs output only when both halves of a pair are present, so partial/literal markup
        // is left untouched. Gated by ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs (default on).
        private static readonly Regex BoldOpenRegex = new("<b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BoldCloseRegex = new("</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ColorOpenRegex = new("<color=[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ColorCloseRegex = new("</color>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Unity asset-import failures surface as an Error whose text ends in a terse "Import Error Code:(N)".
        // The code alone is meaningless to an agent, so decode known ones into a plain-English hint that
        // also conveys severity (is it fatal / recoverable?).
        private static readonly Regex ImportErrorCodeRegex = new(@"Import Error Code:\s*\((\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string? TryDecodeImportError(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return null;
            }

            var match = ImportErrorCodeRegex.Match(message!);
            if (!match.Success)
            {
                return null;
            }

            var code = match.Groups[1].Value;
            return code switch
            {
                "4" => "Import code 4 = SourceAssetDB mtime mismatch: usually benign, triggered by files changed outside Unity (e.g. git stash/checkout/pull). Re-run assets-refresh (a full AssetDatabase.Refresh reconciles the mtime); not fatal on its own.",
                _ => $"Unity asset import error code {code}. Fetch this id via console-get-logs-single for the importer detail/stack to judge severity.",
            };
        }

        private const string UnityConsoleTruncationMark = "<message truncated>";

        // Generic frames of async state machines repeat ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null" per type
        // argument: 300-600 characters a frame that say nothing; the assembly name before them stays.
        private static readonly Regex AssemblyQualifierRegex = new(
            @",\s*Version=[0-9.]+,\s*Culture=[^,\]]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SourceLinkRegex = new(
            "<a href=\"([^\"]*)\" line=\"(\\d+)\">[^<]*</a>(?::\\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public object Clear()
        {
            lock (RuntimeState.LogLock)
            {
                RuntimeState.LogEntries.Clear();
            }

            Debug.ClearDeveloperConsole();
            return new { cleared = true };
        }

        public object Get(JToken args)
        {
            var maxEntries = ClampInt(ReadInt(args, "maxEntries", HardLogMaxEntries), 1, HardLogMaxEntries);
            var lastMinutes = Math.Max(0, ReadInt(args, "lastMinutes", DefaultLogLastMinutes));
            var (levels, contains, filterNote) = ResolveLogFilters(args);
            var caseSensitive = ReadBool(args, "caseSensitive", false);
            var sinceEventId = ReadSinceEventId(args);
            // `dedupe` is the current name for duplicate collapsing; `stack` is the legacy alias. Default on, except when
            // paging from a cursor: a collapsed group sits at its latest repeat, so the logs right after the cursor
            // were not the first rows and paging by the last row's eventId skipped the rest.
            var dedupe = ReadBool(args, "dedupe", ReadBool(args, "stack", !sinceEventId.HasValue));
            var includeUnityConsole = ReadBool(args, "includeUnityConsole", true);
            var includeEditorLog = ReadBool(args, "includeEditorLog", false);
            var includeStack = ReadBool(args, "includeStack", false);
            var sinceTimestamp = ReadSinceTimestamp(args, out var sinceTimestampUnparseable);
            var stripStyleTags = ReadStripStyleTags();
            var cutoff = lastMinutes > 0 ? DateTime.UtcNow.AddMinutes(-lastMinutes) : DateTime.MinValue;

            var filtered = SnapshotFilteredEntries(
                includeUnityConsole,
                includeEditorLog,
                cutoff,
                levels,
                contains,
                caseSensitive,
                sinceEventId,
                sinceTimestamp);

            var notes = new List<string>();
            if (!string.IsNullOrEmpty(filterNote))
            {
                notes.Add(filterNote!);
            }

            if (sinceEventId.HasValue)
            {
                notes.Add("sinceEventId matches only bridge-captured logs (each carries an event cursor); Unity Console / Editor.log rows without a cursor are excluded.");
                // A full cache whose oldest log is newer than the cursor has evicted logs right after it: say so instead of
                // listing from the cache start as if nothing came between.
                if (TryGetEvictedGap(sinceEventId.Value, out var oldestCachedEventId))
                {
                    notes.Add($"The bridge keeps the last {MaxLogEntries} logs and the oldest kept is #{oldestCachedEventId}: logs between sinceEventId {sinceEventId.Value} and it "
                              + "were dropped from this list (a busy game logs that many in minutes); console-get-logs-single e<eventId> still reads one by id.");
                }
            }

            if (sinceTimestampUnparseable)
            {
                notes.Add("sinceTimestampUtc could not be parsed as an ISO-8601 UTC timestamp and was ignored.");
            }

            var matched = filtered.Count;
            var working = dedupe ? CollapseDuplicates(filtered) : filtered.Select(e => (entry: e, repeats: 1)).ToList();
            var groupCount = working.Count;
            // With a cursor the rows right after it come first (page forward with the last row's eventId); without one, the newest.
            var selected = sinceEventId.HasValue
                ? working.Take(maxEntries).ToList()
                : working.Skip(Math.Max(0, groupCount - maxEntries)).ToList();
            if (sinceEventId.HasValue && groupCount > selected.Count)
            {
                notes.Add(dedupe
                    ? $"{selected.Count} groups are listed, each at its latest repeat, so logs right after the cursor may be further down; dedupe:false lists them in order for paging by the last row's eventId."
                    : $"The {selected.Count} logs right after the cursor are listed; for the next ones pass sinceEventId: the last row's eventId.");
            }

            var entries = new List<Dictionary<string, object>>();
            var dropped = groupCount - selected.Count;
            var textTruncated = false;
            var textBudget = MaxToolTextChars;

            foreach (var (entry, repeats) in selected)
            {
                var output = CreateCompactEntryOutput(entry, repeats, stripStyleTags, includeStack, ref textTruncated);
                var estimatedSize = EstimateTextSize(output);
                if (estimatedSize > textBudget)
                {
                    dropped++;
                    continue;
                }

                textBudget -= estimatedSize;
                entries.Add(output);
            }

            return new
            {
                count = entries.Count,
                matched,
                groups = groupCount,
                dropped,
                // truncated: rows were left out (see dropped); textTruncated: a listed message or stack was shortened.
                truncated = dropped > 0,
                textTruncated,
                // Current high-water event cursor. Pass it back as sinceEventId next call to fetch only newer logs.
                lastEventId = EventJournal.CurrentEventId(),
                filterNote = notes.Count > 0 ? string.Join(" ", notes) : (string?)null,
                entries
            };
        }

        private static bool TryGetEvictedGap(long sinceEventId, out long oldestCachedEventId)
        {
            lock (RuntimeState.LogLock)
            {
                oldestCachedEventId = RuntimeState.LogEntries.Count > 0 ? RuntimeState.LogEntries[0].EventId : 0;
                return RuntimeState.LogEntries.Count >= MaxLogEntries && oldestCachedEventId > sinceEventId + 1;
            }
        }

        private static long? ReadSinceEventId(JToken args)
        {
            var sinceEventId = ReadNullableInt(args, "sinceEventId");
            return sinceEventId.HasValue ? Math.Max(0, sinceEventId.Value) : (long?)null;
        }

        private static DateTime? ReadSinceTimestamp(JToken args, out bool unparseable)
        {
            unparseable = false;
            var value = ReadProperty(args, "sinceTimestampUtc");
            if (value == null || value.Type == JTokenType.Null)
            {
                return null;
            }

            // Newtonsoft deserializes a valid ISO-8601 string into a Date token; an unparseable one
            // stays a String token. Handle both, and treat an unspecified/local kind as UTC.
            if (value.Type == JTokenType.Date)
            {
                try
                {
                    var date = value.Value<DateTime>();
                    return date.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
                        : date.ToUniversalTime();
                }
                catch (Exception)
                {
                    unparseable = true;
                    return null;
                }
            }

            if (value.Type == JTokenType.String)
            {
                var text = value.Value<string>();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
                {
                    return parsed;
                }
            }

            unparseable = true;
            return null;
        }

        public object GetSingle(JToken args)
        {
            var id = ReadString(args, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("'id' is required (obtain it from console-get-logs).", "id");
            }

            // events-wait prints eventId:834877 and events-check-since #834877; console-get-logs ids are e834877.
            var bareEventId = id!.Trim().TrimStart('#');
            if (bareEventId.Length > 0 && bareEventId.All(char.IsDigit))
            {
                id = "e" + bareEventId;
            }

            var snapshot = ReadConsoleSnapshot(
                ReadBool(args, "includeUnityConsole", true),
                ReadBool(args, "includeEditorLog", false),
                out _,
                out _,
                out _);
            // An id names one entry; a content-hash id (older listings) resolves to the latest entry with that text.
            var match = snapshot.LastOrDefault(candidate => string.Equals(ComputeLogEntryId(candidate), id, StringComparison.OrdinalIgnoreCase))
                ?? snapshot.LastOrDefault(candidate => string.Equals(ComputeContentHash(candidate), id, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                return new
                {
                    found = false,
                    id,
                    hint = "No log entry with that id is currently buffered. Re-run console-get-logs to refresh ids."
                };
            }

            var textTruncated = false;
            var entry = CreateLogEntryOutput(
                match,
                StackTraceMode.Full,
                trimMessage: false,
                stripStyleTags: ReadStripStyleTags(),
                ref textTruncated);
            return new
            {
                found = true,
                textTruncated,
                entry
            };
        }

        private static List<LogEntryDto> SnapshotFilteredEntries(
            bool includeUnityConsole,
            bool includeEditorLog,
            DateTime cutoff,
            HashSet<string> levels,
            string? contains,
            bool caseSensitive,
            long? sinceEventId,
            DateTime? sinceTimestamp)
        {
            var snapshot = ReadConsoleSnapshot(includeUnityConsole, includeEditorLog, out _, out _, out _);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            return snapshot
                .Where(entry => entry.Timestamp >= cutoff)
                .Where(entry => !sinceTimestamp.HasValue || (entry.HasTimestamp && entry.Timestamp >= sinceTimestamp.Value))
                .Where(entry => !sinceEventId.HasValue || entry.EventId > sinceEventId.Value)
                .Where(entry => levels.Contains(entry.LogType))
                .Where(entry => string.IsNullOrEmpty(contains) || entry.Message.IndexOf(contains!, comparison) >= 0)
                .ToList();
        }

        // One row per duplicate group (ComputeDuplicateGroupKey), placed at its latest occurrence and carrying that
        // occurrence's id, time and cursor, so rows read in chronological order of their last appearance.
        internal static List<(LogEntryDto entry, int repeats)> CollapseDuplicates(List<LogEntryDto> entries)
        {
            var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < entries.Count; i++)
            {
                var key = ComputeDuplicateGroupKey(entries[i]);
                lastIndex[key] = i;
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            return lastIndex
                .OrderBy(pair => pair.Value)
                .Select(pair => (entries[pair.Value], counts[pair.Key]))
                .ToList();
        }

        // A call that logged more than maxEntries keeps its warnings and errors first, then its first and last plain logs:
        // taking the first maxEntries dropped the script's own line written after a burst of game logs, with no sign of it.
        // omittedNote says how many were left out and where to read them.
        internal static object[] CreateLogOutputs(int startIndex, bool includeLogs, string? logType, bool includeStackTrace, int maxEntries, out string? omittedNote)
        {
            omittedNote = null;
            if (!includeLogs)
            {
                return Array.Empty<object>();
            }

            var stackTraceMode = includeStackTrace ? StackTraceMode.Full : StackTraceMode.None;
            var levels = string.IsNullOrWhiteSpace(logType)
                ? null
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { logType! };
            var truncated = false;
            var written = GetLogEntriesSince(startIndex)
                .Where(entry => levels == null || levels.Contains(entry.LogType))
                .ToArray();
            var kept = SelectKeptLogs(written, maxEntries);
            if (kept.Length < written.Length)
            {
                omittedNote = $"logs: {kept.Length} of the {written.Length} logs written during the call are shown (warnings and errors first, then the first and the last plain logs). "
                              + $"All of them: console-get-logs sinceEventId:{written[0].EventId - 1} levels:[\"Log\",\"Warning\",\"Error\",\"Exception\",\"Assert\"] dedupe:false maxEntries:{Math.Min(written.Length, MaxLogEntries)}.";
            }

            return kept
                // Same text as console-get-logs shows: a project logger's <b><color> prefix stripped by the same policy.
                .Select(entry => CreateLogEntryOutput(entry, stackTraceMode, trimMessage: true, ReadStripStyleTags(), ref truncated))
                .Cast<object>()
                .ToArray();
        }

        // In written order: warnings, errors, exceptions and asserts take the slots first, plain logs fill the rest;
        // each kind over its share keeps its first and last entries.
        internal static LogEntryDto[] SelectKeptLogs(LogEntryDto[] written, int maxEntries)
        {
            if (written.Length <= maxEntries)
            {
                return written;
            }

            var keep = new bool[written.Length];
            var important = new List<int>();
            var plain = new List<int>();
            for (var i = 0; i < written.Length; i++)
            {
                if (string.Equals(written[i].LogType, nameof(LogType.Log), StringComparison.OrdinalIgnoreCase))
                {
                    plain.Add(i);
                }
                else
                {
                    important.Add(i);
                }
            }

            var keptImportant = MarkFirstAndLast(important, maxEntries, keep);
            MarkFirstAndLast(plain, maxEntries - keptImportant, keep);
            return written.Where((_, index) => keep[index]).ToArray();
        }

        private static int MarkFirstAndLast(List<int> indices, int slots, bool[] keep)
        {
            if (slots <= 0)
            {
                return 0;
            }

            var count = Math.Min(slots, indices.Count);
            var fromStart = indices.Count <= slots ? indices.Count : (slots + 1) / 2;
            for (var i = 0; i < fromStart; i++)
            {
                keep[indices[i]] = true;
            }

            for (var i = 0; i < count - fromStart; i++)
            {
                keep[indices[indices.Count - 1 - i]] = true;
            }

            return count;
        }

        // The last value read on the main thread: EditorPrefs answers only there, and a timed-out async script's reply
        // (with its logs) is built on a worker thread.
        private static volatile bool lastStripStyleTags = true;

        private static bool ReadStripStyleTags()
        {
            if (UnityEditorInternal.InternalEditorUtility.CurrentThreadIsMainThread())
            {
                lastStripStyleTags = ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs;
            }

            return lastStripStyleTags;
        }

        internal static void CollectLog(string condition, string stackTrace, LogType type)
        {
            // Write the event first so the entry can carry its event-journal cursor; console-get-logs
            // uses that cursor for sinceEventId freshness filtering.
            var marker = TryParseLogMarker(condition);
            var eventId = EventJournal.Write(
                "log",
                marker == null ? "message" : "marker",
                type.ToString(),
                condition,
                marker: marker,
                data: marker == null
                    ? null
                    : new Dictionary<string, object?> { ["locationMarker"] = marker });

            lock (RuntimeState.LogLock)
            {
                RuntimeState.AddLogEntry(new LogEntryDto(type.ToString(), condition, DateTime.UtcNow, stackTrace, eventId), MaxLogEntries);
            }
        }

        // The beacon may sit after a project logger's prefix ("[UnityLogs] MCPEventReachedLocation(x)", rich-text tags), so it
        // is looked for anywhere in the first line rather than only at its start.
        internal static string? TryParseLogMarker(string condition)
        {
            var newline = condition.IndexOf('\n');
            var firstLine = newline >= 0 ? condition.Substring(0, newline) : condition;
            var start = firstLine.IndexOf(BridgeRuntimeState.LogMarkerPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            var nameStart = start + BridgeRuntimeState.LogMarkerPrefix.Length;
            var end = firstLine.IndexOf(BridgeRuntimeState.LogMarkerSuffix, nameStart, StringComparison.Ordinal);
            if (end <= nameStart)
            {
                return null;
            }

            return BridgeEventJournal.NormalizeMarker(firstLine.Substring(nameStart, end - nameStart));
        }
    }
}
