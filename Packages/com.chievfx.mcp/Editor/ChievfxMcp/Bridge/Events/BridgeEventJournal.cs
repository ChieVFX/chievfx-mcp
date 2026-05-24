#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeEventJournal
    {
        private readonly object eventLock = new();
        private long lastEventId;

        public long NextEventId()
        {
            lock (eventLock)
            {
                var stream = ReadEventStream();
                var nextEventId = Math.Max(CurrentEventId(), GetDurableLastEventId(stream)) + 1;
                Interlocked.Exchange(ref lastEventId, nextEventId);
                return nextEventId;
            }
        }

        public long CurrentEventId()
        {
            return Interlocked.Read(ref lastEventId);
        }

        public void Write(
            string source,
            string type,
            string level,
            string message,
            string? marker = null,
            string? operationId = null,
            Dictionary<string, object?>? data = null)
        {
            Write(NextEventId(), source, type, level, message, marker, operationId, data);
        }

        public void Write(
            long eventId,
            string source,
            string type,
            string level,
            string message,
            string? marker = null,
            string? operationId = null,
            Dictionary<string, object?>? data = null)
        {
            try
            {
                var eventRecord = new BridgeEventRecord
                {
                    eventId = eventId,
                    timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    source = NormalizeEventText(source, 64),
                    type = NormalizeEventText(type, 128),
                    level = NormalizeEventText(level, 32),
                    message = NormalizeEventText(message, MaxEventMessageChars),
                    marker = NormalizeMarker(marker),
                    operationId = string.IsNullOrWhiteSpace(operationId) ? null : operationId,
                    data = NormalizeEventData(data)
                };

                lock (eventLock)
                {
                    var stream = ReadEventStream();
                    var durableLastEventId = GetDurableLastEventId(stream);
                    if (eventId <= durableLastEventId)
                    {
                        eventId = durableLastEventId + 1;
                        eventRecord.eventId = eventId;
                    }

                    if (eventId > CurrentEventId())
                    {
                        Interlocked.Exchange(ref lastEventId, eventId);
                    }

                    stream.schemaVersion = 1;
                    stream.lastEventId = Math.Max(durableLastEventId, eventId);
                    stream.events.Add(eventRecord);
                    TrimEventStream(stream);
                    var json = JsonConvert.SerializeObject(stream, BridgeRuntimeState.JsonOptions);
                    while (json.Length > MaxEventStreamChars && stream.events.Count > 0)
                    {
                        stream.truncatedBeforeEventId = Math.Max(stream.truncatedBeforeEventId, stream.events[0].eventId);
                        stream.events.RemoveAt(0);
                        json = JsonConvert.SerializeObject(stream, BridgeRuntimeState.JsonOptions);
                    }

                    BridgeRuntimeState.WriteAllTextAtomic(ChievfxMcpToolPolicy.BridgeEventPath, json);
                }
            }
            catch
            {
                // Log callbacks may run off-thread; event writes must never recurse through Unity logging.
            }
        }

        public void RestoreCursorFromStream()
        {
            lock (eventLock)
            {
                var durableLastEventId = GetDurableLastEventId(ReadEventStream());
                if (durableLastEventId > CurrentEventId())
                {
                    Interlocked.Exchange(ref lastEventId, durableLastEventId);
                }
            }
        }

        public void EnsureStreamFile()
        {
            lock (eventLock)
            {
                if (File.Exists(ChievfxMcpToolPolicy.BridgeEventPath))
                {
                    return;
                }

                var stream = new BridgeEventStream
                {
                    schemaVersion = 1,
                    lastEventId = CurrentEventId()
                };
                BridgeRuntimeState.WriteAllTextAtomic(
                    ChievfxMcpToolPolicy.BridgeEventPath,
                    JsonConvert.SerializeObject(stream, BridgeRuntimeState.JsonOptions));
            }
        }

        private static BridgeEventStream ReadEventStream()
        {
            if (!File.Exists(ChievfxMcpToolPolicy.BridgeEventPath))
            {
                return new BridgeEventStream();
            }

            try
            {
                return JsonConvert.DeserializeObject<BridgeEventStream>(
                        File.ReadAllText(ChievfxMcpToolPolicy.BridgeEventPath),
                        BridgeRuntimeState.JsonOptions)
                    ?? new BridgeEventStream();
            }
            catch
            {
                return new BridgeEventStream();
            }
        }

        private static long GetDurableLastEventId(BridgeEventStream stream)
        {
            var lastId = Math.Max(stream.lastEventId, stream.truncatedBeforeEventId);
            if (stream.events == null)
            {
                stream.events = new List<BridgeEventRecord>();
                return lastId;
            }

            foreach (var bridgeEvent in stream.events)
            {
                if (bridgeEvent.eventId > lastId)
                {
                    lastId = bridgeEvent.eventId;
                }
            }

            return lastId;
        }

        private static void TrimEventStream(BridgeEventStream stream)
        {
            while (stream.events.Count > MaxEventEntries)
            {
                stream.truncatedBeforeEventId = Math.Max(stream.truncatedBeforeEventId, stream.events[0].eventId);
                stream.events.RemoveAt(0);
            }
        }

        private static string NormalizeEventText(string? text, int maxChars)
        {
            var value = text ?? string.Empty;
            if (value.Length <= maxChars)
            {
                return value;
            }

            return value.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }

        internal static string? NormalizeMarker(string? marker)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                return null;
            }

            var trimmed = marker!.Trim();
            if (trimmed.Length > MaxEventMarkerChars
                || trimmed.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                return null;
            }

            return trimmed;
        }

        private static Dictionary<string, object?>? NormalizeEventData(Dictionary<string, object?>? data)
        {
            if (data == null || data.Count == 0)
            {
                return null;
            }

            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in data)
            {
                normalized[pair.Key] = pair.Value is string text
                    ? NormalizeEventText(text, MaxEventDataStringChars)
                    : pair.Value;
            }

            return normalized;
        }
    }
}
