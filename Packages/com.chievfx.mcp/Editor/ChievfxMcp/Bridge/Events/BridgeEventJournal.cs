#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace Chievfx.Mcp.Editor
{
    internal sealed class BridgeEventJournal
    {
        // Every console line reaches this class through Application.logMessageReceivedThreaded, so Write
        // sits on Unity's main thread inside LogStringToConsole. It used to re-read, re-deserialize,
        // re-serialize and atomically rewrite the whole events.json per event; with the stream at its
        // steady-state cap (MaxEventEntries records, ~300 KB) that measured ~42 ms per Debug.Log, which
        // is what made log-heavy frames stall. The in-memory stream is now the authority and the file is
        // a debounced mirror of it: writes are pure memory, disk work is coalesced to one rewrite per
        // FlushIntervalMs regardless of how many events landed in between.
        //
        // The interval is matched to the Python server's EVENTS_WAIT_POLL_SECONDS (50 ms) so events-wait
        // observes no added latency. Anything that hands a consumer an event cursor force-flushes first
        // (see Flush callers) so a cursor is never newer than the file backing it.
        private const int FlushIntervalMs = 50;

        private readonly object eventLock = new();
        private readonly Stopwatch flushClock = Stopwatch.StartNew();

        // Null until first use / after a domain reload, at which point it is rehydrated from disk.
        private BridgeEventStream? stream;
        private bool dirty;
        private long lastFlushMs = -FlushIntervalMs;

        // Reservation high-water mark. Deliberately distinct from stream.lastEventId (the highest id
        // actually appended): NextEventId hands out an id before the matching Write lands, and that
        // reserved id must survive the collision check below.
        private long lastEventId;

        public long NextEventId()
        {
            lock (eventLock)
            {
                EnsureLoadedLocked();
                var nextEventId = CurrentEventId() + 1;
                Interlocked.Exchange(ref lastEventId, nextEventId);
                return nextEventId;
            }
        }

        public long CurrentEventId()
        {
            return Interlocked.Read(ref lastEventId);
        }

        // Returns the event id assigned to the written record so callers (e.g. console log capture)
        // can carry the cursor alongside the entry. Returns the id even if the durable write throws.
        public long Write(
            string source,
            string type,
            string level,
            string message,
            string? marker = null,
            string? operationId = null,
            Dictionary<string, object?>? data = null)
        {
            return Write(NextEventId(), source, type, level, message, marker, operationId, data);
        }

        public long Write(
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
                    var loaded = EnsureLoadedLocked();
                    var recordedLastEventId = loaded.lastEventId;
                    if (eventId <= recordedLastEventId)
                    {
                        eventId = recordedLastEventId + 1;
                        eventRecord.eventId = eventId;
                    }

                    if (eventId > CurrentEventId())
                    {
                        Interlocked.Exchange(ref lastEventId, eventId);
                    }

                    loaded.schemaVersion = 1;
                    loaded.lastEventId = Math.Max(recordedLastEventId, eventId);
                    loaded.events.Add(eventRecord);
                    TrimEventStream(loaded);
                    dirty = true;
                    FlushLocked(force: false);
                }
            }
            catch
            {
                // Log callbacks may run off-thread; event writes must never recurse through Unity logging.
            }

            return eventId;
        }

        // Mirrors the in-memory stream to disk immediately. Call before handing a consumer anything that
        // carries an event cursor, and before the process may lose its statics (domain reload / stop).
        public void Flush()
        {
            lock (eventLock)
            {
                try
                {
                    FlushLocked(force: true);
                }
                catch
                {
                    // Never let a failed mirror write escape into Unity logging (it would recurse).
                }
            }
        }

        // Debounced drain, driven off the editor tick so a burst that ends mid-frame still reaches disk
        // promptly without every event paying for its own rewrite.
        public void FlushIfDue()
        {
            lock (eventLock)
            {
                try
                {
                    FlushLocked(force: false);
                }
                catch
                {
                    // See Flush().
                }
            }
        }

        public void RestoreCursorFromStream()
        {
            lock (eventLock)
            {
                // Flush before dropping the cached stream: EnsureStarted (and therefore this method) also
                // runs from the editor tick, so discarding unflushed events here would lose them.
                try
                {
                    FlushLocked(force: true);
                }
                catch
                {
                    // See Flush().
                }

                stream = null;
                EnsureLoadedLocked();
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

                var loaded = EnsureLoadedLocked();
                loaded.schemaVersion = 1;
                loaded.lastEventId = Math.Max(loaded.lastEventId, CurrentEventId());
                dirty = true;
                try
                {
                    FlushLocked(force: true);
                }
                catch
                {
                    // See Flush().
                }
            }
        }

        // Caller must hold eventLock.
        private BridgeEventStream EnsureLoadedLocked()
        {
            if (stream != null)
            {
                return stream;
            }

            var loaded = ReadEventStream();
            loaded.events ??= new List<BridgeEventRecord>();

            // Collapse the file's lastEventId, its truncation watermark and every record id into the one
            // "highest id actually recorded" value the collision check in Write relies on.
            var durableLastEventId = GetDurableLastEventId(loaded);
            loaded.lastEventId = durableLastEventId;
            if (durableLastEventId > CurrentEventId())
            {
                Interlocked.Exchange(ref lastEventId, durableLastEventId);
            }

            stream = loaded;
            return loaded;
        }

        // Caller must hold eventLock.
        private void FlushLocked(bool force)
        {
            if (!dirty || stream == null)
            {
                return;
            }

            var nowMs = flushClock.ElapsedMilliseconds;
            if (!force && nowMs - lastFlushMs < FlushIntervalMs)
            {
                return;
            }

            BridgeRuntimeState.WriteAllTextAtomic(
                ChievfxMcpToolPolicy.BridgeEventPath,
                SerializeWithinBudget(stream));
            dirty = false;
            lastFlushMs = nowMs;
        }

        private static string SerializeWithinBudget(BridgeEventStream stream)
        {
            var json = JsonConvert.SerializeObject(stream, BridgeRuntimeState.JsonOptions);
            while (json.Length > MaxEventStreamChars && stream.events.Count > 0)
            {
                // Drop a proportional batch, not one record per pass: the previous one-at-a-time loop
                // re-serialized the entire (up to MaxEventStreamChars) stream after every single removal.
                var averageRecordChars = Math.Max(1, json.Length / stream.events.Count);
                var overflowChars = json.Length - MaxEventStreamChars;
                var dropCount = Math.Min(stream.events.Count, (overflowChars / averageRecordChars) + 1);
                for (var i = 0; i < dropCount; i++)
                {
                    stream.truncatedBeforeEventId = Math.Max(stream.truncatedBeforeEventId, stream.events[0].eventId);
                    stream.events.RemoveAt(0);
                }

                json = JsonConvert.SerializeObject(stream, BridgeRuntimeState.JsonOptions);
            }

            return json;
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
