#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeOperationStore
    {
        private readonly BridgeEventJournal eventJournal;

        public BridgeOperationStore(BridgeEventJournal eventJournal)
        {
            this.eventJournal = eventJournal;
        }

        public bool IsCancellationRequested(string id)
        {
            return File.Exists(GetCancelMarkerPath(id));
        }

        public void MarkRunning(string id, string toolName, int timeoutMs)
        {
            WriteOperationRecord(id, new Dictionary<string, object?>
            {
                ["toolName"] = toolName,
                ["state"] = "running",
                ["startedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["progressMessage"] = "Unity started the operation.",
                ["cancellable"] = true,
                ["timeoutMs"] = timeoutMs
            });
        }

        public void MarkWaiting(string id, string message, bool cancellable)
        {
            WriteOperationRecord(id, new Dictionary<string, object?>
            {
                ["state"] = "waiting",
                ["progressMessage"] = message,
                ["cancellable"] = cancellable
            });
        }

        public void MarkCancelRequested(string id, string message)
        {
            WriteOperationRecord(id, new Dictionary<string, object?>
            {
                ["state"] = "cancelRequested",
                ["progressMessage"] = message,
                ["cancellationRequested"] = true
            });
        }

        public void Complete(string id, string state, string message)
        {
            WriteOperationRecord(id, new Dictionary<string, object?>
            {
                ["state"] = state,
                ["completedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["progressMessage"] = message
            });
            RemoveCancelMarker(id);
        }

        public int CountActiveRecords()
        {
            if (!Directory.Exists(ChievfxMcpToolPolicy.BridgeOperationDirectory))
            {
                return 0;
            }

            return Directory.GetFiles(ChievfxMcpToolPolicy.BridgeOperationDirectory, "*.json")
                .Count(path => !IsTerminalOperationState(ReadOperationState(path)));
        }

        public void CleanupRecords()
        {
            if (!Directory.Exists(ChievfxMcpToolPolicy.BridgeOperationDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(ChievfxMcpToolPolicy.BridgeOperationDirectory, "*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToArray();
            var cutoff = DateTime.UtcNow.AddMinutes(-OperationRecordTtlMinutes);
            for (var i = 0; i < files.Length; i++)
            {
                var info = files[i];
                var state = ReadOperationState(info.FullName);
                var terminal = IsTerminalOperationState(state);
                var stale = !terminal && info.LastWriteTimeUtc < DateTime.UtcNow.AddMinutes(-StaleOperationMinutes);
                if ((terminal && info.LastWriteTimeUtc < cutoff) || i >= MaxOperationRecords)
                {
                    TryDeleteFile(info.FullName);
                    continue;
                }

                if (stale)
                {
                    WriteOperationRecord(Path.GetFileNameWithoutExtension(info.Name), new Dictionary<string, object?>
                    {
                        ["state"] = "stale",
                        ["completedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        ["progressMessage"] = "Operation record became stale before completion."
                    });
                }
            }
        }

        private static void RemoveCancelMarker(string id)
        {
            var markerPath = GetCancelMarkerPath(id);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }

        private static Dictionary<string, object?> ReadOperationRecord(string id)
        {
            var path = GetOperationPath(id);
            if (!File.Exists(path))
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["operationId"] = id,
                    ["queuedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
            }

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, object?>>(
                        File.ReadAllText(path),
                        BridgeRuntimeState.JsonOptions)
                    ?? new Dictionary<string, object?>(StringComparer.Ordinal) { ["operationId"] = id };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not read operation record '{id}'. {ex.GetBaseException().Message}");
                return new Dictionary<string, object?>(StringComparer.Ordinal) { ["operationId"] = id };
            }
        }

        private void WriteOperationRecord(string id, IDictionary<string, object?> fields)
        {
            Directory.CreateDirectory(ChievfxMcpToolPolicy.BridgeOperationDirectory);
            var eventId = eventJournal.NextEventId();
            var record = ReadOperationRecord(id);
            foreach (var field in fields)
            {
                record[field.Key] = field.Value;
            }

            record["operationId"] = id;
            record["updatedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            record["eventId"] = eventId;
            BridgeRuntimeState.WriteAllTextAtomic(
                GetOperationPath(id),
                JsonConvert.SerializeObject(record, BridgeRuntimeState.JsonOptions));
            WriteOperationEvent(eventId, id, record);
        }

        private void WriteOperationEvent(long eventId, string operationId, IReadOnlyDictionary<string, object?> record)
        {
            var state = record.TryGetValue("state", out var stateValue)
                ? Convert.ToString(stateValue, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
            var toolName = record.TryGetValue("toolName", out var toolValue)
                ? Convert.ToString(toolValue, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
            var message = record.TryGetValue("progressMessage", out var messageValue)
                ? Convert.ToString(messageValue, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;

            eventJournal.Write(
                eventId,
                "bridge",
                "request-state",
                OperationStateToEventLevel(state),
                string.IsNullOrWhiteSpace(message) ? $"Operation {operationId} {state}." : message,
                operationId: operationId,
                data: new Dictionary<string, object?>
                {
                    ["state"] = state,
                    ["toolName"] = toolName,
                    ["cancellable"] = record.TryGetValue("cancellable", out var cancellable) ? cancellable : null
                });
        }

        private static string OperationStateToEventLevel(string state)
        {
            return state switch
            {
                "failed" => "error",
                "cancelRequested" => "warning",
                "cancelled" => "warning",
                "stale" => "warning",
                _ => "info"
            };
        }

        private static string GetOperationPath(string id)
        {
            return Path.Combine(ChievfxMcpToolPolicy.BridgeOperationDirectory, id + ".json");
        }

        private static string GetCancelMarkerPath(string id)
        {
            return Path.Combine(ChievfxMcpToolPolicy.BridgeCancelDirectory, id + ".json");
        }

        private static bool IsTerminalOperationState(string state)
        {
            return string.Equals(state, "completed", StringComparison.Ordinal)
                || string.Equals(state, "failed", StringComparison.Ordinal)
                || string.Equals(state, "cancelled", StringComparison.Ordinal)
                || string.Equals(state, "stale", StringComparison.Ordinal);
        }

        private static string ReadOperationState(string path)
        {
            try
            {
                var root = JToken.Parse(File.ReadAllText(path));
                if (root is JObject rootObj
                    && rootObj["state"] is JToken state
                    && state.Type == JTokenType.String)
                {
                    return state.Value<string>() ?? string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }

            return string.Empty;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not delete '{path}'. {ex.GetBaseException().Message}");
            }
        }
    }
}
