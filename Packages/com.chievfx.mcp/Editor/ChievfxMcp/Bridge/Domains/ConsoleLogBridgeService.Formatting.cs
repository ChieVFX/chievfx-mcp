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
    internal sealed partial class ConsoleLogBridgeService
    {
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
            var message = StripAssemblyQualifiers(StripSourceLinks((stripStyleTags ? StripStyleTags(entry.Message) : entry.Message) ?? string.Empty));
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

            var stack = FormatStackTrace(StripAssemblyQualifiers(StripSourceLinks(entry.StackTrace)), stackTraceMode, ref truncated);
            if (!string.IsNullOrEmpty(stack))
            {
                output["stack"] = stack;
            }
            else if (stackTraceMode != StackTraceMode.None && HasStackFramesInText(message))
            {
                output["stackNote"] = "The stack is in the message text above: the project's logger writes it there, not in the stack field.";
            }
            else if (stackTraceMode != StackTraceMode.None && IsStackCaptureOff(entry.LogType, out var logType))
            {
                output["stackNote"] = $"No stack: the project records none for {logType} logs (Project Settings > Player > Stack Trace Logging is None). "
                    + $"To get one, run Application.SetStackTraceLogType(LogType.{logType}, StackTraceLogType.ScriptOnly) in script-execute, repeat the action, then set it back to None.";
            }

            return output;
        }

        // Stack capture can be off per log type in the project (Player Settings): such entries never had a stack, and an
        // empty includeStack read as if the bridge had lost it.
        private static bool IsStackCaptureOff(string? entryLogType, out LogType logType)
        {
            return Enum.TryParse(entryLogType, out logType) && Application.GetStackTraceLogType(logType) == StackTraceLogType.None;
        }

        internal static string StripAssemblyQualifiers(string? text)
        {
            if (string.IsNullOrEmpty(text) || text!.IndexOf("PublicKeyToken=", StringComparison.Ordinal) < 0)
            {
                return text ?? string.Empty;
            }

            return AssemblyQualifierRegex.Replace(text, string.Empty);
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
            // first line while contains matched the rest, and two different bodies looked like the same row. A project logger
            // that writes its stack into the message text would fill the row with frames: those stay in console-get-logs-single.
            var joined = JoinLinesWithoutStackFrames(StripSourceLinks(message), out var droppedFrames);
            output["msg"] = TrimText(droppedFrames > 0 ? joined + $" (+{droppedFrames} stack lines: console-get-logs-single)" : joined, MaxLogMessageChars, ref truncated);
            var importHint = TryDecodeImportError(entry.Message);
            if (importHint != null)
            {
                output["hint"] = importHint;
            }

            // includeStack lets callers get the trace inline instead of racing console-get-logs-single,
            // whose id can be evicted by a per-frame exception before the follow-up call lands.
            if (includeStack)
            {
                var stack = FormatStackTrace(StripAssemblyQualifiers(entry.StackTrace), StackTraceMode.Full, ref truncated);
                if (!string.IsNullOrEmpty(stack))
                {
                    output["stack"] = stack;
                }
                else if (droppedFrames > 0)
                {
                    output["stack"] = "in the message text: the project's logger writes it there (console-get-logs-single shows it whole)";
                }
                else if (IsStackCaptureOff(entry.LogType, out var logType))
                {
                    output["stack"] = $"none recorded: Stack Trace Logging for {logType} is None in the project (console-get-logs-single says how to get one)";
                }
            }

            return output;
        }

        internal static string JoinLinesWithoutStackFrames(string? text, out int droppedFrames)
        {
            droppedFrames = 0;
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var kept = new List<string>();
            foreach (var rawLine in text!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (kept.Count > 0 && IsStackFrameLine(line))
                {
                    droppedFrames++;
                    continue;
                }

                kept.Add(line);
            }

            // "Log stack:" / "Exception stack:" headers left with nothing after them.
            while (droppedFrames > 0 && kept.Count > 1 && kept[kept.Count - 1].EndsWith("stack:", StringComparison.OrdinalIgnoreCase) && kept[kept.Count - 1].Length <= 40)
            {
                kept.RemoveAt(kept.Count - 1);
            }

            return string.Join(" ", kept);
        }

        // A project logger that writes the stack into the message: a frame line after the first line.
        internal static bool HasStackFramesInText(string? text)
        {
            JoinLinesWithoutStackFrames(text, out var droppedFrames);
            return droppedFrames > 0;
        }

        // .NET frames ("at Ns.Type.Method (...)", "at Ns.Type.Method in /path:line 5", "at Ns.Type.Method"), Unity frames
        // ("Ns.Type:Method (...) (at Assets/X.cs:12)") and a project logger's "Stack generation time: ..." footer.
        private static bool IsStackFrameLine(string line)
        {
            if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                var nameEnd = line.IndexOf(' ', 3);
                var name = nameEnd < 0 ? line.Substring(3) : line.Substring(3, nameEnd - 3);
                return name.IndexOf('.') > 0;
            }

            return (line.EndsWith(")", StringComparison.Ordinal) && line.IndexOf(" (at ", StringComparison.Ordinal) > 0)
                   || line.StartsWith("UnityEngine.Debug:", StringComparison.Ordinal)
                   || line.StartsWith("Stack generation time:", StringComparison.Ordinal);
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
    }
}
