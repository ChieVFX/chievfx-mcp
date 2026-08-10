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
        //
        // The debounce alone still left every flush re-serializing the whole stream twice. TrimEventStream
        // capped only by record count (MaxEventEntries), so with realistic message lengths the stream
        // overshot MaxEventStreamChars on essentially every flush and the char budget was enforced by
        // SerializeWithinBudget's serialize -> drop a batch -> re-serialize loop. Measured on a sustained
        // log burst that put the stream at its steady state (pinned at ~511 KB, 882 records): 2 full
        // Newtonsoft serializations per flush, 14.6 ms mean and 35.9 ms worst on the main thread, 20x a
        // second - 27% of wall clock spent rewriting events.json.
        //
        // So each record's JSON is now serialized once, when it is appended, and cached alongside it.
        // That makes the char budget exact (no measure-by-serializing), so trimming is pure bookkeeping,
        // and a flush just streams the cached fragments into the file instead of reflectively serializing
        // a thousand objects and materializing a ~500 KB string to hand to File.WriteAllText.
        // Same workload after: 1.5 ms mean and 1.5 ms worst per flush, 2.9% of wall clock, no gen0 GCs.
        private const int FlushIntervalMs = 50;

        // Upper bound on the envelope around the events array: the three scalar properties with their
        // names, braces and brackets. Held back from the char budget so the assembled document cannot
        // exceed MaxEventStreamChars no matter how many digits the ids have grown to.
        private const int EnvelopeReserveChars = 128;

        private readonly object eventLock = new();
        private readonly Stopwatch flushClock = Stopwatch.StartNew();

        // Null until first use / after a domain reload, at which point it is rehydrated from disk.
        private BridgeEventStream? stream;

        // Per-record serialized JSON, index-aligned with stream.events, plus the running sum of their
        // lengths. Both are maintained only under eventLock and only by AppendRecordLocked / TrimLocked,
        // which are the sole mutators of stream.events.
        private readonly List<string> eventFragments = new();
        private long fragmentChars;

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
                    AppendRecordLocked(loaded, eventRecord);
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

            // Rebuild the fragment cache for the rehydrated records. One-off per domain reload, and it
            // pays for itself immediately: every subsequent flush reuses these strings.
            eventFragments.Clear();
            fragmentChars = 0;
            foreach (var bridgeEvent in loaded.events)
            {
                var fragment = SerializeRecord(bridgeEvent);
                eventFragments.Add(fragment);
                fragmentChars += fragment.Length;
            }

            // A file written by an older build (or an externally edited one) can arrive over budget.
            // Mark it dirty so the trimmed stream reaches disk on the next flush rather than lingering.
            var loadedCount = loaded.events.Count;
            TrimLocked(loaded);
            if (loaded.events.Count != loadedCount)
            {
                dirty = true;
            }

            return loaded;
        }

        // Caller must hold eventLock.
        private void AppendRecordLocked(BridgeEventStream stream, BridgeEventRecord eventRecord)
        {
            // Serialized after the id collision check in Write has settled, so the cached fragment always
            // matches the record's final eventId.
            var fragment = SerializeRecord(eventRecord);
            stream.events.Add(eventRecord);
            eventFragments.Add(fragment);
            fragmentChars += fragment.Length;
            TrimLocked(stream);
        }

        // Drops the oldest records until the stream is inside both the record-count cap and the char
        // budget. Exact, because every record's serialized length is known - no trial serialization.
        // Caller must hold eventLock.
        private void TrimLocked(BridgeEventStream stream)
        {
            while (stream.events.Count > 0
                && (stream.events.Count > MaxEventEntries || AssembledChars(stream.events.Count) > MaxEventStreamChars))
            {
                stream.truncatedBeforeEventId = Math.Max(stream.truncatedBeforeEventId, stream.events[0].eventId);
                stream.events.RemoveAt(0);
                fragmentChars -= eventFragments[0].Length;
                eventFragments.RemoveAt(0);
            }
        }

        // Length of the document WriteStreamJsonLocked would produce: fragments, the commas between them,
        // and a conservative allowance for the envelope.
        private long AssembledChars(int recordCount)
        {
            return fragmentChars + Math.Max(0, recordCount - 1) + EnvelopeReserveChars;
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
                writer => WriteStreamJsonLocked(writer, stream));
            dirty = false;
            lastFlushMs = nowMs;
        }

        private static string SerializeRecord(BridgeEventRecord eventRecord)
        {
            return JsonConvert.SerializeObject(eventRecord, BridgeRuntimeState.JsonOptions);
        }

        // Writes the same document JsonConvert.SerializeObject(stream) would, from the cached per-record
        // fragments, straight into the destination writer - no intermediate string. Property order and
        // formatting must stay in step with BridgeEventStream; the round-trip test in
        // ChievfxMcpBridgeEventJournalTests asserts the two are byte-identical.
        // Caller must hold eventLock.
        private void WriteStreamJsonLocked(TextWriter writer, BridgeEventStream stream)
        {
            writer.Write("{\"schemaVersion\":");
            writer.Write(stream.schemaVersion.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"lastEventId\":");
            writer.Write(stream.lastEventId.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"truncatedBeforeEventId\":");
            writer.Write(stream.truncatedBeforeEventId.ToString(CultureInfo.InvariantCulture));
            writer.Write(",\"events\":[");
            for (var i = 0; i < eventFragments.Count; i++)
            {
                if (i > 0)
                {
                    writer.Write(',');
                }

                writer.Write(eventFragments[i]);
            }

            writer.Write("]}");
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
