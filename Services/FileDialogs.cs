using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TDPdf.Services
{
    /// <summary>
    /// Every Open, Save and folder dialog in TDPdf goes through here. The dialogs stay the Windows
    /// ones; this adds what they lack: a remembered folder per purpose (a fixed ClientGuid each),
    /// TDPdf's recent folders in the left-hand list, and a check on the path that comes back.
    /// </summary>
    internal static partial class FileDialogs
    {
        /// <summary>Returns the folder of the document in the active tab (or null). Set by the main window.</summary>
        public static Func<string?>? DocumentFolderProvider { get; set; }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, (bool Exists, DateTime At)> Probes =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan ProbeLife = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ProbeWait = TimeSpan.FromMilliseconds(300);

        // ── Public entry points ───────────────────────────────────────────────────────────

        /// <summary>Shows an Open dialog for <paramref name="purpose"/>. False when cancelled or the path is refused.</summary>
        public static bool ShowOpen(OpenFileDialog dlg, FileDialogPurpose purpose, Window owner)
        {
            Prepare(dlg, purpose);
            if (dlg.ShowDialog(owner) != true) return false;
            if (!PathsAreUsable(owner, dlg.FileNames)) return false;
            Remember(dlg.FileName, isFolder: false);
            return true;
        }

        /// <summary>Shows a Save dialog for <paramref name="purpose"/>. False when cancelled or the path is refused.</summary>
        public static bool ShowSave(SaveFileDialog dlg, FileDialogPurpose purpose, Window owner)
        {
            Prepare(dlg, purpose);
            if (dlg.ShowDialog(owner) != true) return false;
            if (!PathsAreUsable(owner, new[] { dlg.FileName })) return false;
            Remember(dlg.FileName, isFolder: false);
            return true;
        }

        /// <summary>Shows a folder dialog for <paramref name="purpose"/>. False when cancelled or the path is refused.</summary>
        public static bool ShowFolder(OpenFolderDialog dlg, FileDialogPurpose purpose, Window owner)
        {
            Prepare(dlg, purpose);
            if (dlg.ShowDialog(owner) != true) return false;
            if (!PathsAreUsable(owner, new[] { dlg.FolderName })) return false;
            Remember(dlg.FolderName, isFolder: true);
            return true;
        }

        /// <summary>
        /// Checks the paths a dialog returned with <see cref="FilePathPolicy.CheckDialogPath"/> and
        /// tells the user why one was refused.
        /// </summary>
        public static bool PathsAreUsable(Window owner, IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                string? problem = FilePathPolicy.CheckDialogPath(p);
                if (problem is null) continue;
                TdpDialog.Show(owner, problem, "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Looks the recent folders up in the background so the first dialog of the session does not
        /// wait on them. Safe to call at start-up and ignore.
        /// </summary>
        public static void WarmUp() => _ = Task.Run(() => { try { CheckedPlaces(); } catch { /* best effort */ } });

        // ── Folder memory ─────────────────────────────────────────────────────────────────

        /// <summary>Records the folder of a path the user just picked, for the left-hand list.</summary>
        public static void Remember(string? path, bool isFolder)
        {
            try
            {
                if (TDPdf.Properties.Settings.Default.DontRememberRecentFiles) return;   // same privacy switch as recent files
                string? folder = FileDialogPolicy.FolderOf(path, isFolder);
                if (folder is null) return;
                var list = FileDialogPolicy.AddRecent(
                    FileDialogPolicy.ParseRecent(TDPdf.Properties.Settings.Default.RecentFolders), folder);
                TDPdf.Properties.Settings.Default.RecentFolders = FileDialogPolicy.SerializeRecent(list);
                TDPdf.Properties.Settings.Default.Save();
                lock (Gate) Probes[folder] = (true, DateTime.UtcNow);   // it was just used, so it exists
            }
            catch { /* remembering a folder must never break a save */ }
        }

        private static void Prepare(CommonItemDialog dlg, FileDialogPurpose purpose)
        {
            // Windows keeps one last folder per ClientGuid, so each purpose opens where it was last used.
            dlg.ClientGuid = FileDialogPolicy.GuidFor(purpose);
            try
            {
                foreach (var folder in CheckedPlaces())
                    dlg.CustomPlaces.Add(new FileDialogCustomPlace(folder));
            }
            catch { /* the list is a convenience */ }
        }

        /// <summary>
        /// The folders to list: the current document's, then recent ones, keeping only those that
        /// answer within a short timeout so an offline share is skipped rather than waited on.
        /// </summary>
        internal static List<string> CheckedPlaces()
        {
            string? docFolder = null;
            try
            {
                string? doc = DocumentFolderProvider?.Invoke();
                docFolder = doc;
            }
            catch { /* no document */ }

            var candidates = FileDialogPolicy.PlacesFor(docFolder,
                FileDialogPolicy.ParseRecent(TDPdf.Properties.Settings.Default.RecentFolders),
                max: FileDialogPolicy.RecentFoldersKept + 1);

            var tasks = new List<(string Folder, Task<bool> Probe)>();
            var result = new List<string>();
            var now = DateTime.UtcNow;
            foreach (var folder in candidates)
            {
                (bool Exists, DateTime At) hit;
                bool known;
                lock (Gate) known = Probes.TryGetValue(folder, out hit) && now - hit.At < ProbeLife;
                if (known) continue;
                string f = folder;
                var t = Task.Run(() =>
                {
                    bool ok;
                    try { ok = Directory.Exists(f); } catch { ok = false; }
                    lock (Gate) Probes[f] = (ok, DateTime.UtcNow);   // a late answer still fills the cache
                    return ok;
                });
                tasks.Add((folder, t));
            }
            // One shared wait for every probe, so a dead share costs at most ProbeWait in total.
            try { Task.WaitAll(tasks.Select(t => (Task)t.Probe).ToArray(), ProbeWait); } catch { }
            foreach (var (folder, probe) in tasks)
            {
                if (!probe.IsCompleted)
                    lock (Gate) Probes[folder] = (false, DateTime.UtcNow);   // timed out: skip it for now
            }
            foreach (var folder in candidates)
            {
                bool exists;
                lock (Gate) exists = Probes.TryGetValue(folder, out var hit) && hit.Exists;
                if (exists) result.Add(folder);
                if (result.Count >= FileDialogPolicy.PlacesShown) break;
            }
            return result;
        }
    }
}
