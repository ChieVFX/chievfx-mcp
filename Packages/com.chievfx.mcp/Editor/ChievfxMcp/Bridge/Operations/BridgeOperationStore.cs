#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeOperationStore
    {
        private readonly BridgeEventJournal eventJournal;

        // Last parsed "state" per record file, keyed by full path and validated against the file's write
        // time. See ReadCachedOperationState for why a cache hit also requires a terminal state.
        private readonly Dictionary<string, OperationStateCacheEntry> stateCache = new(StringComparer.Ordinal);
        private double lastCleanupTime = double.NegativeInfinity;

        public BridgeOperationStore(BridgeEventJournal eventJournal)
        {
            this.eventJournal = eventJournal;
        }

        private readonly struct OperationStateCacheEntry
        {
            public OperationStateCacheEntry(DateTime writeTimeUtc, string state)
            {
                WriteTimeUtc = writeTimeUtc;
                State = state;
            }

            public DateTime WriteTimeUtc { get; }

            public string State { get; }
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

        // One pass over the operation records per heartbeat. Returns how many are still active - the count
        // published as activeOperationCount - and does the janitorial work (TTL deletion, stale marking,
        // overflow trimming) only when that is due.
        //
        // This was two methods called back to back from the heartbeat, CleanupRecords() and
        // CountActiveRecords(). Each enumerated the directory and read + JToken.Parse'd every record file
        // just to pull out its one-word "state", so every record was fully parsed twice a second for as
        // long as it was retained. Saturated at MaxOperationRecords that measured 25.9 ms per heartbeat -
        // 51.8 ms/s, 5.2% of wall clock - and it grew with session length, because terminal records linger
        // for OperationRecordTtlMinutes.
        //
        // Now the scan happens once and consults stateCache, so a record that has not been rewritten since
        // it was last parsed and is already terminal is never parsed again. Only genuinely active records
        // (normally a handful) are re-read.
        public int RefreshRecords()
        {
            if (!Directory.Exists(ChievfxMcpToolPolicy.BridgeOperationDirectory))
            {
                stateCache.Clear();
                return 0;
            }

            var now = EditorApplication.timeSinceStartup;
            var runCleanup = now - lastCleanupTime >= OperationCleanupCadenceSeconds;
            if (runCleanup)
            {
                lastCleanupTime = now;
            }

            // DirectoryInfo.GetFiles rather than Directory.GetFiles + new FileInfo(path): the write times
            // this pass runs on then come from the directory enumeration itself, instead of costing a
            // separate stat per record.
            var files = new DirectoryInfo(ChievfxMcpToolPolicy.BridgeOperationDirectory)
                .GetFiles("*.json")
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToArray();

            var nowUtc = DateTime.UtcNow;
            var ttlCutoff = nowUtc.AddMinutes(-OperationRecordTtlMinutes);
            var staleCutoff = nowUtc.AddMinutes(-StaleOperationMinutes);
            var activeCount = 0;

            for (var i = 0; i < files.Length; i++)
            {
                var info = files[i];
                var terminal = IsTerminalOperationState(ReadCachedOperationState(info));

                if (runCleanup && ((terminal && info.LastWriteTimeUtc < ttlCutoff) || i >= MaxOperationRecords))
                {
                    TryDeleteFile(info.FullName);
                    stateCache.Remove(info.FullName);
                    continue;
                }

                // Between sweeps a record that has stalled past the threshold stays counted as active until
                // the next one marks it. It genuinely still is non-terminal, and the lag is bounded by
                // OperationCleanupCadenceSeconds against a threshold of StaleOperationMinutes.
                if (!terminal && info.LastWriteTimeUtc < staleCutoff && runCleanup)
                {
                    WriteOperationRecord(Path.GetFileNameWithoutExtension(info.Name), new Dictionary<string, object?>
                    {
                        ["state"] = "stale",
                        ["completedAtUtc"] = nowUtc.ToString("o", CultureInfo.InvariantCulture),
                        ["progressMessage"] = "Operation record became stale before completion."
                    });

                    // Rewritten by the line above, so the cached entry is out of date; it is terminal now
                    // either way, which is why it does not count towards activeCount.
                    stateCache.Remove(info.FullName);
                    continue;
                }

                if (!terminal)
                {
                    activeCount++;
                }
            }

            if (runCleanup)
            {
                PruneStateCache(files);
            }

            return activeCount;
        }

        // Terminal states are final, so a terminal record whose file has not been rewritten since it was
        // parsed cannot have changed. Requiring the cached state to be terminal - rather than trusting the
        // write time alone - keeps this correct where filesystem timestamp granularity is coarse: the
        // transitions that matter (running -> completed) always re-read.
        private string ReadCachedOperationState(FileInfo info)
        {
            if (stateCache.TryGetValue(info.FullName, out var cached)
                && cached.WriteTimeUtc == info.LastWriteTimeUtc
                && IsTerminalOperationState(cached.State))
            {
                return cached.State;
            }

            var state = ReadOperationState(info.FullName);
            stateCache[info.FullName] = new OperationStateCacheEntry(info.LastWriteTimeUtc, state);
            return state;
        }

        // Drops entries for records that no longer exist, so the cache cannot outgrow the directory over a
        // long session. Runs on the sweep cadence; the set is capped at MaxOperationRecords.
        private void PruneStateCache(FileInfo[] files)
        {
            var live = new HashSet<string>(files.Select(info => info.FullName), StringComparer.Ordinal);
            foreach (var path in stateCache.Keys.Where(path => !live.Contains(path)).ToArray())
            {
                stateCache.Remove(path);
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
