using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace TDPdf.Services
{
    /// <summary>The two DjVuLibre programs TDPdf runs.</summary>
    internal sealed record DjvuTools(string Ddjvu, string Djvused);

    /// <summary>
    /// Finds DjVuLibre. A build that includes it carries <c>ddjvu.exe</c>, <c>djvused.exe</c> and the DLLs they
    /// need as embedded resources named <c>TDPdf.DjvuNative.&lt;file&gt;</c> (see TDPdf.csproj), extracted on
    /// first use to a per-version folder the way <see cref="OcrNativeBootstrap"/> does: a copy already there is
    /// kept only if its SHA-256 matches the embedded one, and each file is written to a unique temp name and
    /// moved into place. A build without them falls back to a DjVuLibre the person installed under Program
    /// Files (a folder only an administrator can write to). Nothing is ever taken from the PATH or from the
    /// book's own folder.
    /// </summary>
    internal static class DjvuNativeBootstrap
    {
        private const string Prefix = "TDPdf.DjvuNative.";
        private static readonly object Gate = new();
        private static DjvuTools? _tools;
        private static bool _resolved;

        /// <summary>True when this build carries DjVuLibre inside it.</summary>
        internal static bool IsBundled => typeof(DjvuNativeBootstrap).Assembly.GetManifestResourceNames()
            .Any(n => n.StartsWith(Prefix, StringComparison.Ordinal));

        /// <summary>The programs to run, or null when this PC has no DjVuLibre TDPdf may use.</summary>
        internal static DjvuTools? Locate()
        {
            lock (Gate)
            {
                if (_resolved) return _tools;
                _tools = TryExtract() ?? TryInstalled();
                _resolved = true;
                return _tools;
            }
        }

        private static DjvuTools? TryExtract()
        {
            try
            {
                var asm = typeof(DjvuNativeBootstrap).Assembly;
                var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
                if (names.Count == 0) return null;
                string version = asm.GetName().Version?.ToString() ?? "0";
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TDPdf", "djvu", version);
                Directory.CreateDirectory(dir);
                foreach (string res in names)
                {
                    string file = res[Prefix.Length..];
                    if (file.Contains('/') || file.Contains('\\') || file.Contains("..", StringComparison.Ordinal)) continue;
                    Extract(asm, res, Path.Combine(dir, file));
                }
                return At(dir);
            }
            catch { return null; }
        }

        private static DjvuTools? TryInstalled()
        {
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string pf = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(pf)) continue;
                if (At(Path.Combine(pf, "DjVuLibre")) is { } t) return t;
            }
            return null;
        }

        private static DjvuTools? At(string dir)
        {
            string a = Path.Combine(dir, "ddjvu.exe"), s = Path.Combine(dir, "djvused.exe");
            return File.Exists(a) && File.Exists(s) ? new DjvuTools(a, s) : null;
        }

        private static void Extract(Assembly asm, string resource, string target)
        {
            using var src = asm.GetManifestResourceStream(resource);
            if (src is null) return;
            if (File.Exists(target))
            {
                byte[] expected = SHA256.HashData(src);
                byte[] actual;
                using (var existing = File.OpenRead(target)) actual = SHA256.HashData(existing);
                if (CryptographicOperations.FixedTimeEquals(expected, actual)) return;
                src.Position = 0;
            }
            string tmp = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    src.CopyTo(dst);
                File.Move(tmp, target, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }
}
