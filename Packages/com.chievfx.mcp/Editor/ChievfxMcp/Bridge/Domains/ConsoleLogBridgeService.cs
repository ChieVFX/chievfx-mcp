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
            var message = (stripStyleTags ? StripStyleTags(entry.Message) : entry.Message) ?? string.Empty;
            var output = new Dictionary<string, object>
            {
                ["id"] = ComputeLogEntryId(entry),
                ["time"] = entry.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                ["level"] = entry.LogType,
                ["msg"] = trimMessage ? TrimText(message, MaxLogMessageChars, ref truncated) : message,
            };

            var importHint = TryDecodeImportError(entry.Message);
            if (importHint != null)
            {
                output["hint"] = importHint;
            }

            var stack = FormatStackTrace(entry.StackTrace, stackTraceMode, ref truncated);
            if (!string.IsNullOrEmpty(stack))
            {
                output["stack"] = stack;
            }

            return output;
        }

        // Compact-mode row: id (and optional repeats) come first for fast scanning, then level + first-line msg.
        // Drops `time` and any embedded stack-trace lines (Unity Console reflection inlines them into Message).
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

            var message = stripStyleTags ? StripStyleTags(entry.Message) : entry.Message;
            output["level"] = entry.LogType;
            output["msg"] = TrimText(ExtractFirstNonEmptyLine(message), MaxLogMessageChars, ref truncated);
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

        // Stable id: first 5 SHA-1 bytes of (level|msg|stack). Same content collapses to same id,
        // which doubles as the group key for compact stacking and as the lookup key for get-logs-single.
        private static string ComputeLogEntryId(LogEntryDto entry)
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
            // `dedupe` is the current name for duplicate collapsing; `stack` is the legacy alias. Default on.
            var dedupe = ReadBool(args, "dedupe", ReadBool(args, "stack", true));
            var includeUnityConsole = ReadBool(args, "includeUnityConsole", true);
            var includeEditorLog = ReadBool(args, "includeEditorLog", false);
            var includeStack = ReadBool(args, "includeStack", false);
            var sinceEventId = ReadSinceEventId(args);
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
            }

            if (sinceTimestampUnparseable)
            {
                notes.Add("sinceTimestampUtc could not be parsed as an ISO-8601 UTC timestamp and was ignored.");
            }

            var matched = filtered.Count;
            var working = dedupe ? CollapseDuplicates(filtered) : filtered.Select(e => (entry: e, repeats: 1)).ToList();
            var groupCount = working.Count;
            var selected = working
                .Skip(Math.Max(0, groupCount - maxEntries))
                .ToList();

            var entries = new List<Dictionary<string, object>>();
            var dropped = groupCount - selected.Count;
            var truncated = dropped > 0;
            var textBudget = MaxToolTextChars;

            foreach (var (entry, repeats) in selected)
            {
                var output = CreateCompactEntryOutput(entry, repeats, stripStyleTags, includeStack, ref truncated);
                var estimatedSize = EstimateTextSize(output);
                if (estimatedSize > textBudget)
                {
                    dropped++;
                    truncated = true;
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
                truncated,
                // Current high-water event cursor. Pass it back as sinceEventId next call to fetch only newer logs.
                lastEventId = EventJournal.CurrentEventId(),
                filterNote = notes.Count > 0 ? string.Join(" ", notes) : (string?)null,
                entries
            };
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

            var snapshot = ReadConsoleSnapshot(
                ReadBool(args, "includeUnityConsole", true),
                ReadBool(args, "includeEditorLog", false),
                out _,
                out _,
                out _);
            // Match latest occurrence so repeated logs return the most recent context.
            LogEntryDto? match = null;
            foreach (var candidate in snapshot)
            {
                if (string.Equals(ComputeLogEntryId(candidate), id, StringComparison.OrdinalIgnoreCase))
                {
                    match = candidate;
                }
            }

            if (match == null)
            {
                return new
                {
                    found = false,
                    id,
                    hint = "No log entry with that id is currently buffered. Re-run console-get-logs to refresh ids."
                };
            }

            var truncated = false;
            var entry = CreateLogEntryOutput(
                match,
                StackTraceMode.Full,
                trimMessage: false,
                stripStyleTags: ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs,
                ref truncated);
            return new
            {
                found = true,
                truncated,
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
                .Where(entry => !sinceTimestamp.HasValue || entry.Timestamp >= sinceTimestamp.Value)
                .Where(entry => !sinceEventId.HasValue || entry.EventId > sinceEventId.Value)
                .Where(entry => levels.Contains(entry.LogType))
                .Where(entry => string.IsNullOrEmpty(contains) || entry.Message.IndexOf(contains!, comparison) >= 0)
                .ToList();
        }

        private static List<(LogEntryDto entry, int repeats)> CollapseDuplicates(List<LogEntryDto> entries)
        {
            // Preserve first-seen order so chronology stays stable; carry latest representative for each id.
            var firstIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var representative = new Dictionary<string, LogEntryDto>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var id = ComputeLogEntryId(entry);
                if (!firstIndex.ContainsKey(id))
                {
                    firstIndex[id] = i;
                    counts[id] = 0;
                }

                representative[id] = entry;
                counts[id]++;
            }

            return firstIndex
                .OrderBy(pair => pair.Value)
                .Select(pair => (representative[pair.Key], counts[pair.Key]))
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
                snapshot.AddRange(unityConsoleEntries);
            }

            if (includeEditorLog)
            {
                var editorLogEntries = ReadUnityEditorLogEntries().ToArray();
                editorLogCount = editorLogEntries.Length;
                snapshot.AddRange(editorLogEntries);
            }

            return snapshot;
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
                    var repeatCount = Math.Max(1, ReadReflectedInt(entry, "count"));
                    for (var repeat = 0; repeat < repeatCount; repeat++)
                    {
                        entries.Add(new LogEntryDto(logType, message, DateTime.UtcNow, stackTrace));
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
                foreach (var line in File.ReadLines(logPath))
                {
                    var entry = TryParseEditorLogLine(line);
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

        private static LogEntryDto? TryParseEditorLogLine(string line)
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

            return new LogEntryDto(level, line, DateTime.UtcNow, string.Empty);
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

        private static string ReadUnityConsoleLogType(object entry, string message)
        {
            var messageLevel = TryClassifyEditorLogLine(message);
            if (messageLevel != null)
            {
                return messageLevel;
            }

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
            if (modeValue == null)
            {
                return LogType.Warning.ToString();
            }

            var modeTextLogType = ReadUnityConsoleLogTypeName(Convert.ToString(modeValue, CultureInfo.InvariantCulture));
            if (modeTextLogType != null)
            {
                return modeTextLogType;
            }

            var mode = Convert.ToInt32(modeValue, CultureInfo.InvariantCulture);
            if ((mode & (1 | 16 | 64 | 256 | 2048 | 1048576 | 4194304)) != 0)
            {
                return LogType.Error.ToString();
            }

            if ((mode & 2) != 0)
            {
                return LogType.Assert.ToString();
            }

            if ((mode & (128 | 512 | 4096)) != 0)
            {
                return LogType.Warning.ToString();
            }

            return LogType.Warning.ToString();
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


        internal static int GetLogEntryCount()
        {
            lock (RuntimeState.LogLock)
            {
                return RuntimeState.LogEntries.Count;
            }
        }

        private static LogEntryDto[] GetLogEntriesSince(int startIndex)
        {
            lock (RuntimeState.LogLock)
            {
                var safeStartIndex = ClampInt(startIndex, 0, RuntimeState.LogEntries.Count);
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
                .Select(entry => CreateLogEntryOutput(entry, stackTraceMode, ref truncated))
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
                RuntimeState.LogEntries.Add(new LogEntryDto(type.ToString(), condition, DateTime.UtcNow, stackTrace, eventId));
                if (RuntimeState.LogEntries.Count > MaxLogEntries)
                {
                    RuntimeState.LogEntries.RemoveRange(0, RuntimeState.LogEntries.Count - MaxLogEntries);
                }
            }
        }

        private static string? TryParseLogMarker(string condition)
        {
            var message = condition.Trim();
            if (!message.StartsWith(BridgeRuntimeState.LogMarkerPrefix, StringComparison.Ordinal)
                || !message.EndsWith(BridgeRuntimeState.LogMarkerSuffix, StringComparison.Ordinal))
            {
                return null;
            }

            var marker = message.Substring(
                BridgeRuntimeState.LogMarkerPrefix.Length,
                message.Length - BridgeRuntimeState.LogMarkerPrefix.Length - BridgeRuntimeState.LogMarkerSuffix.Length);
            return BridgeEventJournal.NormalizeMarker(marker);
        }

    }
}
