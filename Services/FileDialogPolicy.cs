using System.IO;

namespace TDPdf.Services
{
    /// <summary>Why a file or folder dialog is being shown. Each purpose remembers its own folder.</summary>
    internal enum FileDialogPurpose
    {
        Open,
        Merge,
        MergeFiles,
        MergeFolder,
        Compare,
        SaveAs,
        SaveFlattened,
        SearchablePdf,
        ExtractText,
        ExportImages,
        ExportTables,
        ExtractPagesFolder,
        Attachment,
        AttachmentsFolder,
        SignedDownload,
        SignaturePicture,
        SignatureExport,
        Picture,
        FormData,
    }

    /// <summary>
    /// The pure half of the file dialogs: the fixed GUID for each purpose, the filter strings and
    /// the recent-folder list. WPF-free so tests/PdfCore can pin it.
    /// </summary>
    internal static class FileDialogPolicy
    {
        /// <summary>How many recent folders are kept in settings.</summary>
        public const int RecentFoldersKept = 8;

        /// <summary>How many folders are offered in the dialog's left-hand list (the document's own folder included).</summary>
        public const int PlacesShown = 5;

        // Windows keys its remembered folder on this GUID, so a value must never change once shipped.
        private static readonly Dictionary<FileDialogPurpose, Guid> Guids = new()
        {
            [FileDialogPurpose.Open] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1001"),
            [FileDialogPurpose.Merge] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1002"),
            [FileDialogPurpose.MergeFiles] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1003"),
            [FileDialogPurpose.MergeFolder] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1004"),
            [FileDialogPurpose.Compare] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1005"),
            [FileDialogPurpose.SaveAs] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1006"),
            [FileDialogPurpose.SaveFlattened] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1007"),
            [FileDialogPurpose.SearchablePdf] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1008"),
            [FileDialogPurpose.ExtractText] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1009"),
            [FileDialogPurpose.ExportImages] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100a"),
            [FileDialogPurpose.ExportTables] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100b"),
            [FileDialogPurpose.ExtractPagesFolder] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100c"),
            [FileDialogPurpose.Attachment] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100d"),
            [FileDialogPurpose.AttachmentsFolder] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100e"),
            [FileDialogPurpose.SignedDownload] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a100f"),
            [FileDialogPurpose.SignaturePicture] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1010"),
            [FileDialogPurpose.SignatureExport] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1011"),
            [FileDialogPurpose.Picture] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1012"),
            [FileDialogPurpose.FormData] = new("6c1f3a10-5a0e-4b52-9f0a-0e7d2b6a1013"),
        };

        /// <summary>The fixed GUID for a purpose (the dialog's ClientGuid).</summary>
        public static Guid GuidFor(FileDialogPurpose purpose) => Guids[purpose];

        /// <summary>
        /// Splits a WPF filter string ("PDF files|*.pdf|All files|*.*") into name and pattern
        /// pairs. A trailing name with no pattern is dropped.
        /// </summary>
        public static List<(string Name, string Pattern)> ParseFilter(string? filter)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(filter)) return result;
            var parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
                if (parts[i + 1].Length > 0) result.Add((parts[i], parts[i + 1]));
            return result;
        }

        /// <summary>
        /// Builds a WPF filter string from pairs. The inverse of <see cref="ParseFilter"/>.
        /// </summary>
        public static string BuildFilter(IEnumerable<(string Name, string Pattern)> entries) =>
            string.Join("|", entries.Select(e => e.Name + "|" + e.Pattern));

        /// <summary>
        /// The folders to list beside Quick Access: the current document's folder first, then the
        /// recent ones, each folder once (case-insensitive), at most <paramref name="max"/>.
        /// </summary>
        public static List<string> PlacesFor(string? documentFolder, IEnumerable<string> recent, int max = PlacesShown)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in new[] { documentFolder }.Concat(recent))
            {
                string? n = FilePathPolicy.NormalizeFolder(raw);
                if (n is not null && seen.Add(n)) result.Add(n);
                if (result.Count >= max) break;
            }
            return result;
        }

        /// <summary>The recent-folder list after <paramref name="folder"/> was used: it moves to the front.</summary>
        public static List<string> AddRecent(IEnumerable<string> list, string? folder, int keep = RecentFoldersKept)
        {
            var result = FilePathPolicy.NormalizeDistinct(list);
            string? n = FilePathPolicy.NormalizeFolder(folder);
            if (n is null) return result;
            result.RemoveAll(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase));
            result.Insert(0, n);
            if (result.Count > keep) result.RemoveRange(keep, result.Count - keep);
            return result;
        }

        /// <summary>The folder a dialog result lives in (a folder result is its own folder).</summary>
        public static string? FolderOf(string? path, bool isFolder)
        {
            string? n = FilePathPolicy.Normalize(path);
            if (n is null) return null;
            return isFolder ? n : Path.GetDirectoryName(n);
        }

        /// <summary>Reads the '|'-joined setting value.</summary>
        public static List<string> ParseRecent(string? stored) =>
            string.IsNullOrEmpty(stored) ? new List<string>() : FilePathPolicy.NormalizeDistinct(stored.Split('|'));

        /// <summary>Writes the '|'-joined setting value ('|' is illegal in Windows paths).</summary>
        public static string SerializeRecent(IEnumerable<string> list) => string.Join("|", list);
    }
}
