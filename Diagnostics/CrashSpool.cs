using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// A tiny on-disk queue for crash records, replayed at the next launch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This closes a gap the exporter's own disk-retry cannot. Disk-retry persists batches whose
    /// <em>send failed</em>. A crash record never gets that far: it is handed to the batch
    /// processor, the process dies milliseconds later, and the batch dies with it — no send was
    /// ever attempted, so there is nothing for retry to persist.
    /// </para>
    /// <para>
    /// That is not hypothetical. On 2026-08-20 a crash killed TDPdf repeatedly on a user's machine;
    /// the signature-placement crashes reported, but the text-box ones produced a relaunch with
    /// <em>no Crash event at all</em> — the process died before the flush won its race. The bug was
    /// found by reading around the hole rather than from the data.
    /// </para>
    /// <para>
    /// Records are written here <b>already sanitised</b> — <see cref="Telemetry"/> scrubs before
    /// calling — so the queue holds exception types, scrubbed messages and grouping keys, never
    /// document paths. It sits under the user's own LocalApplicationData beside the crash logs
    /// <see cref="CrashReporter"/> already writes, and is disclosed in PRIVACY.md.
    /// </para>
    /// <para>
    /// The two mechanisms compose: this covers "died before sending", the exporter's disk-retry
    /// covers "sent and failed". A replayed record is handed to the exporter and deleted from here
    /// immediately, because from that point retry owns it — keeping it in both places would
    /// duplicate the crash in SigNoz on the next launch.
    /// </para>
    /// </remarks>
    internal static class CrashSpool
    {
        /// <summary>Cap on queued records. A crash loop must not fill the user's disk.</summary>
        private const int MaxRecords = 50;

        /// <summary>Records older than this are dropped unsent — stale crash data helps nobody.</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

        private static readonly string s_dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TDPdf", "pending-crashes");

        internal sealed class Record
        {
            public string ExceptionType { get; set; } = "";
            public string Message { get; set; } = "";
            public string StackTrace { get; set; } = "";
            public string Source { get; set; } = "";
            public bool Recoverable { get; set; }
            public string GroupingKey { get; set; } = "";
            public string AppVersion { get; set; } = "";
            public DateTimeOffset TimestampUtc { get; set; }
        }

        /// <summary>
        /// Queues one already-sanitised crash record. Never throws — a failure to spool must not
        /// turn a recoverable crash into a second one.
        /// </summary>
        public static void Write(Record record)
        {
            try
            {
                Directory.CreateDirectory(s_dir);
                Trim();
                string path = Path.Combine(s_dir, $"crash-{Guid.NewGuid():N}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(record));
            }
            catch { /* swallow */ }
        }

        /// <summary>
        /// Replays and clears the queue. Call once at startup, AFTER the exporters are up, so a
        /// replayed record has somewhere to go.
        /// </summary>
        /// <param name="emit">
        /// Receives each record. Must not route back through the spool, or a record that cannot be
        /// sent would be re-queued on every launch forever.
        /// </param>
        public static void Replay(Action<Record> emit)
        {
            try
            {
                if (!Directory.Exists(s_dir)) return;

                foreach (var file in Directory.GetFiles(s_dir, "crash-*.json"))
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(file));
                        // Delete BEFORE emitting, not after. Once handed over, the exporter's own
                        // disk-retry owns delivery; keeping a copy here would report the same crash
                        // again next launch. And a record that somehow kills the emit path must not
                        // be able to kill every subsequent launch too.
                        TryDelete(file);

                        if (record is null) continue;
                        if (DateTimeOffset.UtcNow - record.TimestampUtc > MaxAge) continue;

                        emit(record);
                    }
                    catch { TryDelete(file); }
                }
            }
            catch { /* swallow */ }
        }

        /// <summary>Drops the oldest records once the queue is full.</summary>
        private static void Trim()
        {
            try
            {
                var files = new DirectoryInfo(s_dir).GetFiles("crash-*.json");
                if (files.Length < MaxRecords) return;

                Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                for (int i = 0; i <= files.Length - MaxRecords; i++)
                    TryDelete(files[i].FullName);
            }
            catch { /* swallow */ }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* swallow */ }
        }
    }
}
