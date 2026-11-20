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
    internal sealed class ConsoleLogBridgeService : BridgeDomainServiceBase
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

        private static readonly Regex SourceLinkRegex = new(
            "<a href=\"([^\"]*)\" line=\"(\\d+)\">[^<]*</a>(?::\\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string? StripStyleTags(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            var result = message!;
            result = StripTagPair(result, BoldOpenRegex, BoldCloseRegex);
            result = StripTagPair(result, ColorOpenRegex, ColorCloseRegex);
            return result;
        }

        private static string StripTagPair(string text, Regex openTag, Regex closeTag)
        {
            if (!openTag.IsMatch(text) || !closeTag.IsMatch(text))
            {
                return text;
            }

            return closeTag.Replace(openTag.Replace(text, string.Empty), string.Empty);
        }

        private static readonly Dictionary<string, string[]> LogLevelAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ConsoleErrors"] = new[]
            {
                LogType.Error.ToString(),
                LogType.Exception.ToString(),
                LogType.Assert.ToString(),
            },
            ["ConsoleIssues"] = new[]
            {
                LogType.Error.ToString(),
                LogType.Exception.ToString(),
                LogType.Assert.ToString(),
                LogType.Warning.ToString(),
            },
        };

        private static HashSet<string> ReadLogLevels(JToken args)
        {
            var levels = ReadArray(args, "levels");
            if (levels is JArray levelsArray && levelsArray.Count > 0)
            {
                return ExpandLogLevelAliases(levelsArray
                    .Select(level => level.Type == JTokenType.String ? level.Value<string>() : null)
                    .Where(level => !string.IsNullOrWhiteSpace(level))
                    .Select(level => level!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            var legacyFilter = ReadString(args, "logTypeFilter");
            if (!string.IsNullOrWhiteSpace(legacyFilter))
            {
                return ExpandLogLevelAliases(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { legacyFilter! });
            }

            return DefaultIssueLogLevels();
        }

        private static HashSet<string> DefaultIssueLogLevels()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                LogType.Error.ToString(),
                LogType.Exception.ToString(),
                LogType.Assert.ToString(),
                LogType.Warning.ToString(),
            };
        }

        private static HashSet<string> ExpandLogLevelAliases(HashSet<string> levels)
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var level in levels)
            {
                if (LogLevelAliases.TryGetValue(level, out var aliasLevels))
                {
                    foreach (var aliasLevel in aliasLevels)
                    {
                        expanded.Add(aliasLevel);
                    }

                    continue;
                }

                expanded.Add(level);
            }

            return expanded;
        }

        // Agents often pass contains:"error" expecting Unity console severity, not message substring search.
        // Exact single-token values are reinterpreted as level filters so Assert/Warning rows still match.
        internal static bool TryInterpretContainsAsSeverityLevels(string? contains, out HashSet<string> severityLevels, out string note)
        {
            severityLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            note = string.Empty;
            if (string.IsNullOrWhiteSpace(contains))
            {
                return false;
            }

            var token = contains!.Trim();
            if (token.IndexOf(' ') >= 0)
            {
                return false;
            }

            switch (token.ToLowerInvariant())
            {
                case "error":
                case "errors":
                    severityLevels.UnionWith(LogLevelAliases["ConsoleErrors"]);
                    note = "contains matched Unity console error severity (Error, Exception, Assert), not message text.";
                    return true;
                case "exception":
                case "exceptions":
                    severityLevels.UnionWith(LogLevelAliases["ConsoleErrors"]);
                    note = "contains matched Unity console exception severity (Exception, Error, Assert), not message text.";
                    return true;
                case "warning":
                case "warnings":
                    severityLevels.Add(LogType.Warning.ToString());
                    note = "contains matched Warning severity, not message text.";
                    return true;
                case "issue":
                case "issues":
                case "problem":
                case "problems":
                    severityLevels.UnionWith(LogLevelAliases["ConsoleIssues"]);
                    note = "contains matched console issue severity (Error, Exception, Assert, Warning), not message text.";
                    return true;
                default:
                    return false;
            }
        }

        private static (HashSet<string> levels, string? contains, string? filterNote) ResolveLogFilters(JToken args)
        {
            var levels = ReadLogLevels(args);
            var contains = ReadString(args, "contains");
            if (!TryInterpretContainsAsSeverityLevels(contains, out var severityLevels, out var filterNote))
            {
                return (levels, contains, null);
            }

            levels.UnionWith(severityLevels);
            return (levels, null, filterNote);
        }

        private static Dictionary<string, object> CreateLogEntryOutput(LogEntryDto entry, StackTraceMode stackTraceMode, ref bool truncated)
        {
            return CreateLogEntryOutput(entry, stackTraceMode, trimMessage: true, stripStyleTags: false, ref truncated);
        }

        private static Dictionary<string, object> CreateLogEntryOutput(LogEntryDto entry, StackTraceMode stackTraceMode, bool trimMessage, bool stripStyleTags, ref bool truncated)
        {
            var message = StripSourceLinks((stripStyleTags ? StripStyleTags(entry.Message) : entry.Message) ?? string.Empty);
            // The Unity Console cuts very long entries itself and marks the cut; the text below is then not the whole entry.
            if (message.Contains(UnityConsoleTruncationMark, StringComparison.Ordinal)
                || (entry.StackTrace?.Contains(UnityConsoleTruncationMark, StringComparison.Ordinal) ?? false))
            {
                truncated = true;
            }

            var output = new Dictionary<string, object>
            {
                ["id"] = ComputeLogEntryId(entry),
            };
            if (entry.HasTimestamp)
            {
                output["time"] = entry.Timestamp.ToString("o", CultureInfo.InvariantCulture);
            }

            if (entry.EventId > 0)
            {
                output["eventId"] = entry.EventId;
            }

            output["level"] = entry.LogType;
            output["msg"] = trimMessage ? TrimText(message, MaxLogMessageChars, ref truncated) : message;

            var importHint = TryDecodeImportError(entry.Message);
            if (importHint != null)
            {
                output["hint"] = importHint;
            }

            var stack = FormatStackTrace(StripSourceLinks(entry.StackTrace), stackTraceMode, ref truncated);
            if (!string.IsNullOrEmpty(stack))
            {
                output["stack"] = stack;
            }

            return output;
        }

        // Unity Console stack lines carry clickable links: (<a href="Assets/X.cs" line="7">Assets/X.cs</a>:7). The path
        // shows twice and the markup eats the text budget before the frame that matters; keep path:line.
        internal static string StripSourceLinks(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.IndexOf("<a href=", StringComparison.Ordinal) < 0)
            {
                return text ?? string.Empty;
            }

            return SourceLinkRegex.Replace(text, "$1:$2");
        }

        // Compact-mode row: id (and optional repeats) come first for fast scanning, then the time and event cursor
        // of the latest occurrence (UTC, only when the source records them), then level + first-line msg.
        private static Dictionary<string, object> CreateCompactEntryOutput(LogEntryDto entry, int repeats, bool stripStyleTags, bool includeStack, ref bool truncated)
        {
            var output = new Dictionary<string, object>
            {
                ["id"] = ComputeLogEntryId(entry),
            };
            if (repeats > 1)
            {
                output["repeats"] = repeats;
            }

            if (entry.HasTimestamp)
            {
                output["time"] = entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }

            if (entry.EventId > 0)
            {
                output["eventId"] = entry.EventId;
            }

            var message = stripStyleTags ? StripStyleTags(entry.Message) : entry.Message;
            output["level"] = entry.LogType;
            // The whole message on one line: a log body on later lines ("[OnlineEvents] message: {" + JSON) showed only its
            // first line while contains matched the rest, and two different bodies looked like the same row.
            output["msg"] = TrimText(JoinLines(message), MaxLogMessageChars, ref truncated);
            var importHint = TryDecodeImportError(entry.Message);
            if (importHint != null)
            {
                output["hint"] = importHint;
            }

            // includeStack lets callers get the trace inline instead of racing console-get-logs-single,
            // whose id can be evicted by a per-frame exception before the follow-up call lands.
            if (includeStack)
            {
                var stack = FormatStackTrace(entry.StackTrace, StackTraceMode.Full, ref truncated);
                if (!string.IsNullOrEmpty(stack))
                {
                    output["stack"] = stack;
                }
            }

            return output;
        }

        private static string JoinLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var lines = text!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
        }

        private static string ExtractFirstNonEmptyLine(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var span = text!.AsSpan();
            var start = 0;
            while (start < span.Length)
            {
                var nl = span.Slice(start).IndexOfAny('\r', '\n');
                var lineLength = nl < 0 ? span.Length - start : nl;
                if (lineLength > 0)
                {
                    var line = span.Slice(start, lineLength);
                    if (!line.IsWhiteSpace())
                    {
                        return line.ToString();
                    }
                }

                if (nl < 0)
                {
                    return string.Empty;
                }

                start += lineLength + 1;
            }

            return string.Empty;
        }

        // Id of one entry, not of its text: two identical "popup_closed" logs are two entries, and
        // console-get-logs-single must return the one that was listed. Bridge-captured entries use their event
        // cursor, Unity Console and Editor.log entries their line in that source; only an entry with neither falls
        // back to a content hash.
        private static string ComputeLogEntryId(LogEntryDto entry)
        {
            if (entry.EventId > 0)
            {
                return "e" + entry.EventId.ToString(CultureInfo.InvariantCulture);
            }

            return entry.SourceId ?? ComputeContentHash(entry);
        }

        // First 5 SHA-1 bytes of (level|msg|stack): the id format before ids were per entry, still accepted by
        // console-get-logs-single so an id from an older listing keeps resolving to the latest matching entry.
        private static string ComputeContentHash(LogEntryDto entry)
        {
            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var seed = (entry.LogType ?? string.Empty) + "\0" + (entry.Message ?? string.Empty) + "\0" + (entry.StackTrace ?? string.Empty);
            var bytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(seed));
            var sb = new System.Text.StringBuilder(10);
            for (var i = 0; i < 5; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static readonly Regex HexNumberRegex = new(@"0x[0-9a-fA-F]+", RegexOptions.Compiled);
        private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.Compiled);

        // Repeats of one error differ in numbers a logger appends to the text ("Stack generation time: total:
        // 412 ms" against "360 ms") and in line offsets of the trace. Grouping on the exact headline plus the rest
        // with numbers masked collapses those, while different errors keep apart: the headline must match exactly,
        // and two NullReferenceExceptions from different call sites still differ in the method names of their
        // traces.
        internal static string ComputeDuplicateGroupKey(LogEntryDto entry)
        {
            var message = entry.Message ?? string.Empty;
            var headline = ExtractFirstNonEmptyLine(message);
            var headlineEnd = message.IndexOf(headline, StringComparison.Ordinal) + headline.Length;
            var rest = (headlineEnd < message.Length ? message.Substring(headlineEnd) : string.Empty)
                + "\n" + (entry.StackTrace ?? string.Empty);
            var maskedRest = NumberRegex.Replace(HexNumberRegex.Replace(rest, "0x#"), "#");
            return (entry.LogType ?? string.Empty) + "\0" + headline + "\0" + maskedRest.Trim();
        }

        private static string FormatStackTrace(string stackTrace, StackTraceMode mode, ref bool truncated)
        {
            if (mode == StackTraceMode.None || string.IsNullOrEmpty(stackTrace))
            {
                return string.Empty;
            }

            if (mode == StackTraceMode.FirstLine)
            {
                var firstLine = stackTrace
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .FirstOrDefault() ?? string.Empty;
                return TrimText(firstLine, MaxLogMessageChars, ref truncated);
            }

            return TrimText(stackTrace, MaxStackTraceChars, ref truncated);
        }

        private static int EstimateTextSize(Dictionary<string, object> output)
        {
            return output.Sum(pair =>
            {
                var valueText = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                return pair.Key.Length + valueText.Length;
            });
        }


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
            var stripStyleTags = ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs;
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
                stripStyleTags: ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs,
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

        private static List<LogEntryDto> ReadConsoleSnapshot(
            bool includeUnityConsole,
            bool includeEditorLog,
            out int unityConsoleCount,
            out int editorLogCount,
            out int bridgeCacheCount)
        {
            List<LogEntryDto> snapshot;
            lock (RuntimeState.LogLock)
            {
                snapshot = RuntimeState.LogEntries.ToList();
            }

            bridgeCacheCount = snapshot.Count;
            unityConsoleCount = 0;
            editorLogCount = 0;
            if (!includeUnityConsole)
            {
                if (!includeEditorLog)
                {
                    return snapshot;
                }
            }
            else
            {
                var unityConsoleEntries = ReadUnityConsoleEntries().ToArray();
                unityConsoleCount = unityConsoleEntries.Length;
                snapshot = ExcludeCapturedConsoleEntries(unityConsoleEntries, snapshot).Concat(snapshot).ToList();
            }

            if (includeEditorLog)
            {
                var editorLogEntries = ReadUnityEditorLogEntries().ToArray();
                editorLogCount = editorLogEntries.Length;
                snapshot.AddRange(editorLogEntries);
            }

            return snapshot;
        }

        // The Unity Console also holds every log the bridge captured, so reading both listed each such log twice:
        // once with its cursor and time, once as a console row without either. Keep a console row only when no
        // captured entry accounts for it, matching texts newest first, so what remains is what was logged before
        // capture began (or fell out of the capture buffer); it is placed before the captured entries.
        internal static List<LogEntryDto> ExcludeCapturedConsoleEntries(IReadOnlyList<LogEntryDto> consoleEntries, List<LogEntryDto> capturedEntries)
        {
            var captured = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in capturedEntries)
            {
                var key = (entry.Message ?? string.Empty).Trim();
                captured[key] = captured.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var kept = new List<LogEntryDto>();
            for (var i = consoleEntries.Count - 1; i >= 0; i--)
            {
                var key = (consoleEntries[i].Message ?? string.Empty).Trim();
                if (captured.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    captured[key] = remaining - 1;
                    continue;
                }

                kept.Add(consoleEntries[i]);
            }

            kept.Reverse();
            return kept;
        }

        private static IEnumerable<LogEntryDto> ReadUnityConsoleEntries()
        {
            var editorAssembly = typeof(EditorWindow).Assembly;
            var logEntriesType = editorAssembly.GetType("UnityEditor.LogEntries");
            if (logEntriesType == null)
            {
                return Array.Empty<LogEntryDto>();
            }

            var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var getEntry = logEntriesType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "GetEntryInternal", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 && parameters[0].ParameterType == typeof(int);
                });
            if (getCount == null || getEntry == null)
            {
                return Array.Empty<LogEntryDto>();
            }

            var entryParameterType = getEntry.GetParameters()[1].ParameterType;
            var logEntryType = entryParameterType.IsByRef
                ? entryParameterType.GetElementType()
                : entryParameterType;
            if (logEntryType == null)
            {
                return Array.Empty<LogEntryDto>();
            }

            var start = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var end = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var count = Convert.ToInt32(getCount.Invoke(null, null), CultureInfo.InvariantCulture);
            var entries = new List<LogEntryDto>(Math.Min(count, MaxLogEntries));
            start?.Invoke(null, null);
            try
            {
                for (var i = Math.Max(0, count - MaxLogEntries); i < count; i++)
                {
                    var entry = Activator.CreateInstance(logEntryType);
                    if (entry == null)
                    {
                        continue;
                    }

                    var parameters = new[] { (object)i, entry };
                    var ok = Convert.ToBoolean(getEntry.Invoke(null, parameters), CultureInfo.InvariantCulture);
                    if (!ok)
                    {
                        continue;
                    }

                    entry = parameters[1];

                    var message = ReadReflectedString(entry, "condition")
                        ?? ReadReflectedString(entry, "message")
                        ?? string.Empty;
                    if (string.IsNullOrEmpty(message))
                    {
                        continue;
                    }

                    var logType = ReadUnityConsoleLogType(entry, message);
                    var stackTrace = ReadReflectedString(entry, "stackTrace") ?? ReadReflectedString(entry, "stacktrace") ?? string.Empty;
                    // Unity 6 keeps the trace inside message and marks where it starts; split it off so the row's
                    // text matches the captured log and the trace lands in stack, as for captured entries.
                    var callstackStart = ReadReflectedInt(entry, "callstackTextStartUTF16");
                    if (string.IsNullOrEmpty(stackTrace) && callstackStart > 0 && callstackStart < message.Length)
                    {
                        stackTrace = message.Substring(callstackStart);
                        message = message.Substring(0, callstackStart).TrimEnd('\r', '\n');
                    }

                    var lineIndex = ReadReflectedMember(entry, "globalLineIndex") is int globalLineIndex ? globalLineIndex : i;
                    var repeatCount = Math.Max(1, ReadReflectedInt(entry, "count"));
                    for (var repeat = 0; repeat < repeatCount; repeat++)
                    {
                        var sourceId = "c" + lineIndex.ToString(CultureInfo.InvariantCulture)
                            + (repeat > 0 ? "." + repeat.ToString(CultureInfo.InvariantCulture) : string.Empty);
                        entries.Add(new LogEntryDto(logType, message, DateTime.UtcNow, stackTrace, sourceId: sourceId, hasTimestamp: false));
                    }
                }
            }
            catch
            {
                return entries;
            }
            finally
            {
                end?.Invoke(null, null);
            }

            return entries;
        }

        private static IEnumerable<LogEntryDto> ReadUnityEditorLogEntries()
        {
            var logPath = GetUnityEditorLogPath();
            if (!File.Exists(logPath))
            {
                return Array.Empty<LogEntryDto>();
            }

            try
            {
                var latestBlock = new List<LogEntryDto>();
                var currentBlock = new List<LogEntryDto>();
                var lineNumber = 0;
                foreach (var line in File.ReadLines(logPath))
                {
                    lineNumber++;
                    var entry = TryParseEditorLogLine(line, lineNumber);
                    if (entry == null)
                    {
                        if (currentBlock.Count > 0)
                        {
                            latestBlock = currentBlock;
                            currentBlock = new List<LogEntryDto>();
                        }

                        continue;
                    }

                    currentBlock.Add(entry);
                }

                if (currentBlock.Count > 0)
                {
                    latestBlock = currentBlock;
                }

                return latestBlock
                    .Skip(Math.Max(0, latestBlock.Count - MaxLogEntries))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<LogEntryDto>();
            }
        }

        private static string GetUnityEditorLogPath()
        {
            var logPath = Application.consoleLogPath;
            if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            {
                return logPath;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
        }

        private static LogEntryDto? TryParseEditorLogLine(string line, int lineNumber)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            var level = TryClassifyEditorLogLine(line);
            if (level == null)
            {
                return null;
            }

            return new LogEntryDto(
                level,
                line,
                DateTime.UtcNow,
                string.Empty,
                sourceId: "l" + lineNumber.ToString(CultureInfo.InvariantCulture),
                hasTimestamp: false);
        }

        private static string? TryClassifyEditorLogLine(string line)
        {
            if (line.IndexOf(": warning ", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" warning ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Warning.ToString();
            }

            if (line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf(" error ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Error.ToString();
            }

            if (line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Exception.ToString();
            }

            return null;
        }

        private static string? ReadReflectedString(object source, string name)
        {
            var value = ReadReflectedMember(source, name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int ReadReflectedInt(object source, string name)
        {
            var value = ReadReflectedMember(source, name);
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static object? ReadReflectedMember(object source, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = source.GetType();
            try
            {
                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(source);
                }

                var field = type.GetField(name, flags);
                return field?.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        // The console row's mode flags say what Unity logged it as. The text is only a fallback: a Debug.Log that
        // mentions "error" is still a log, and a plain Debug.Log must stay inside a levels:["Log"] filter.
        private static string ReadUnityConsoleLogType(object entry, string message)
        {
            var typeValue = ReadReflectedMember(entry, "type") ?? ReadReflectedMember(entry, "logType");
            if (typeValue is LogType logType)
            {
                return logType.ToString();
            }

            var typeText = Convert.ToString(typeValue, CultureInfo.InvariantCulture);
            var typeTextLogType = ReadUnityConsoleLogTypeName(typeText);
            if (typeTextLogType != null)
            {
                return typeTextLogType;
            }

            if (!string.IsNullOrWhiteSpace(typeText)
                && Enum.TryParse<LogType>(typeText, true, out var parsedLogType))
            {
                return parsedLogType.ToString();
            }

            var modeValue = ReadReflectedMember(entry, "mode");
            if (modeValue != null)
            {
                var modeTextLogType = ReadUnityConsoleLogTypeName(Convert.ToString(modeValue, CultureInfo.InvariantCulture));
                if (modeTextLogType != null)
                {
                    return modeTextLogType;
                }

                var modeLogType = ClassifyConsoleMode(Convert.ToInt32(modeValue, CultureInfo.InvariantCulture));
                if (modeLogType != null)
                {
                    return modeLogType;
                }
            }

            return TryClassifyEditorLogLine(message) ?? LogType.Log.ToString();
        }

        // UnityEditor.ConsoleWindow.Mode bits.
        private const int ConsoleModeExceptionBits = 1 << 17;
        private const int ConsoleModeAssertBits = (1 << 1) | (1 << 21);
        private const int ConsoleModeErrorBits = 1 | (1 << 4) | (1 << 6) | (1 << 8) | (1 << 11) | (1 << 20) | (1 << 22);
        private const int ConsoleModeWarningBits = (1 << 7) | (1 << 9) | (1 << 12);
        private const int ConsoleModeLogBits = (1 << 2) | (1 << 10);

        internal static string? ClassifyConsoleMode(int mode)
        {
            if ((mode & ConsoleModeExceptionBits) != 0)
            {
                return LogType.Exception.ToString();
            }

            if ((mode & ConsoleModeAssertBits) != 0)
            {
                return LogType.Assert.ToString();
            }

            if ((mode & ConsoleModeErrorBits) != 0)
            {
                return LogType.Error.ToString();
            }

            if ((mode & ConsoleModeWarningBits) != 0)
            {
                return LogType.Warning.ToString();
            }

            return (mode & ConsoleModeLogBits) != 0 ? LogType.Log.ToString() : null;
        }

        private static string? ReadUnityConsoleLogTypeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value!.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Warning.ToString();
            }

            if (value.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Error.ToString();
            }

            if (value.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return LogType.Exception.ToString();
            }

            return null;
        }


        // A position in the stream of added logs, not in the buffer: it keeps growing after the buffer is full.
        internal static int GetLogEntryCount()
        {
            lock (RuntimeState.LogLock)
            {
                return RuntimeState.LogEntriesAdded;
            }
        }

        private static LogEntryDto[] GetLogEntriesSince(int startIndex)
        {
            lock (RuntimeState.LogLock)
            {
                var firstBufferedIndex = RuntimeState.LogEntriesAdded - RuntimeState.LogEntries.Count;
                var safeStartIndex = ClampInt(startIndex - firstBufferedIndex, 0, RuntimeState.LogEntries.Count);
                return RuntimeState.LogEntries.Skip(safeStartIndex).ToArray();
            }
        }

        internal static object[] CreateLogOutputs(int startIndex, bool includeLogs, string? logType, bool includeStackTrace, int maxEntries)
        {
            if (!includeLogs)
            {
                return Array.Empty<object>();
            }

            var stackTraceMode = includeStackTrace ? StackTraceMode.Full : StackTraceMode.None;
            var levels = string.IsNullOrWhiteSpace(logType)
                ? null
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { logType! };
            var truncated = false;
            return GetLogEntriesSince(startIndex)
                .Where(entry => levels == null || levels.Contains(entry.LogType))
                .Take(maxEntries)
                // Same text as console-get-logs shows: a project logger's <b><color> prefix stripped by the same policy.
                .Select(entry => CreateLogEntryOutput(entry, stackTraceMode, trimMessage: true, ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs, ref truncated))
                .Cast<object>()
                .ToArray();
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
