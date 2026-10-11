using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TDPdf.Services
{
    /// <summary>
    /// The envelopes this device sent, newest first, in
    /// <c>%LOCALAPPDATA%\TDPdf\documenso-requests.json</c> (design §5.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists so Signature Requests can show this device's sends first and refresh them with
    /// one cheap <c>get-many</c>. It is a convenience index, not a record: the server is the truth,
    /// and a missing, corrupt or hand-edited file simply reads as empty (Signature Requests then
    /// falls back to <c>GET /envelope</c>). The same "plain JSON, tolerate anything" discipline as
    /// <c>signatures.json</c>.
    /// </para>
    /// <para>
    /// <b>No credential and no recipient PII.</b> Names and email addresses come from the API at
    /// display time, never from disk. The title is kept because the list would be unreadable
    /// without it, and it is the user's own document on the user's own disk.
    /// </para>
    /// <para>Capped at <see cref="Cap"/> entries; the oldest fall off.</para>
    /// No WPF types: tests/PdfCore compiles this file.
    /// </remarks>
    internal static class DocumensoRequestStore
    {
        internal const int Cap = 200;

        internal static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf", "documenso-requests.json");

        internal sealed record Entry(string EnvelopeId, string Title, DateTime SentUtc, int RecipientCount);

        private sealed class FileShape
        {
            public int Version { get; set; } = 1;
            public List<EntryShape>? Requests { get; set; }
        }

        private sealed class EntryShape
        {
            public string? EnvelopeId { get; set; }
            public string? Title { get; set; }
            public DateTime SentUtc { get; set; }
            public int RecipientCount { get; set; }
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        /// <summary>Everything recorded, newest first. Empty — never an exception — on any problem.</summary>
        internal static List<Entry> Load(string? path = null)
        {
            try
            {
                path ??= DefaultPath;
                if (!File.Exists(path)) return new List<Entry>();
                var f = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
                return (f?.Requests ?? new List<EntryShape>())
                    .Where(e => !string.IsNullOrWhiteSpace(e?.EnvelopeId))
                    .Select(e => new Entry(e.EnvelopeId!.Trim(), e.Title ?? "", DateTime.SpecifyKind(e.SentUtc, DateTimeKind.Utc),
                        Math.Max(0, e.RecipientCount)))
                    .GroupBy(e => e.EnvelopeId, StringComparer.Ordinal).Select(g => g.First())
                    .OrderByDescending(e => e.SentUtc)
                    .Take(Cap)
                    .ToList();
            }
            catch
            {
                return new List<Entry>();
            }
        }

        /// <summary>
        /// Records a send. Replaces an entry with the same id; trims to <see cref="Cap"/>. Returns
        /// false when the file could not be written — which costs nothing but the "this device"
        /// shortcut, so callers report nothing.
        /// </summary>
        internal static bool Add(Entry entry, string? path = null)
        {
            path ??= DefaultPath;
            string part = path + ".part";
            try
            {
                var list = Load(path).Where(e => e.EnvelopeId != entry.EnvelopeId).ToList();
                list.Insert(0, entry);
                var shape = new FileShape
                {
                    Requests = list.OrderByDescending(e => e.SentUtc).Take(Cap).Select(e => new EntryShape
                    {
                        EnvelopeId = e.EnvelopeId, Title = e.Title, SentUtc = e.SentUtc, RecipientCount = e.RecipientCount,
                    }).ToList(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(part, JsonSerializer.Serialize(shape, Json));
                DocumensoClient.MoveIntoPlace(part, path);
                return true;
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                return false;
            }
        }
    }
}
