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
            // Another editor's Editor.log is not this one's history: with two editors open it listed the other project's lines.
            var logPath = ChievfxMcpEditorHealth.FindSessionLogPath();
            if (logPath == null || !File.Exists(logPath))
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
    }
}
