using System.IO;
using System.Security.Principal;

namespace TDPdf.Diagnostics
{
    /// <summary>
    /// Decides whether a path under <c>%ProgramData%</c> may be written, deleted or believed by a
    /// privileged (SYSTEM / administrator) process.
    /// </summary>
    /// <remarks>
    /// Any user can create <c>C:\ProgramData\TDPdf</c> before the installer runs. If it is a junction
    /// or a symlink, or simply owned by that user, then a SYSTEM-context <c>/install</c> appending to
    /// <c>install.log</c> or deleting <c>telemetry.dat</c> there acts on a target the user chose; and
    /// a <c>telemetry.disabled</c> file the user planted would switch off the device's telemetry.
    /// So privileged code only touches the folder (and a file that already exists in it) when neither
    /// is a reparse point and both are owned by SYSTEM or BUILTIN\Administrators, and it never
    /// follows a reparse point.
    /// </remarks>
    internal static class PrivilegedPathGuard
    {
        private const string SystemSid = "S-1-5-18";
        private const string AdministratorsSid = "S-1-5-32-544";

        /// <summary>True for the two owners a privileged process may trust.</summary>
        internal static bool IsTrustedOwnerSid(string? sid) =>
            string.Equals(sid, SystemSid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sid, AdministratorsSid, StringComparison.OrdinalIgnoreCase);

        /// <summary>True if the attributes mark a junction, symlink or other reparse point.</summary>
        internal static bool IsReparsePoint(FileAttributes attributes) =>
            (attributes & FileAttributes.ReparsePoint) != 0;

        /// <summary>
        /// True if <paramref name="info"/> exists, is not a reparse point and is owned by SYSTEM or
        /// BUILTIN\Administrators. Any failure to establish that is a "no".
        /// </summary>
        internal static bool IsTrusted(FileSystemInfo info)
        {
            try
            {
                info.Refresh();
                if (!info.Exists) return false;
                if (IsReparsePoint(info.Attributes) || info.LinkTarget is not null) return false;
                if (!OperatingSystem.IsWindows()) return false;
                IdentityReference? owner = info switch
                {
                    DirectoryInfo d => d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)),
                    FileInfo f => f.GetAccessControl().GetOwner(typeof(SecurityIdentifier)),
                    _ => null,
                };
                return IsTrustedOwnerSid(owner?.Value);
            }
            catch { return false; }
        }

        /// <summary>True if <paramref name="directory"/> itself is a trusted directory.</summary>
        internal static bool IsTrustedDirectory(string directory) => IsTrusted(new DirectoryInfo(directory));

        /// <summary>
        /// True if <paramref name="path"/> may be created, appended to or deleted by a privileged
        /// process: its folder is trusted, and the file, if it exists, is a trusted regular file.
        /// A file that does not exist yet is fine.
        /// </summary>
        internal static bool IsSafeToWrite(string path)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !IsTrustedDirectory(dir)) return false;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); }   // does not follow a link
                catch (FileNotFoundException) { return true; }
                catch (DirectoryNotFoundException) { return true; }
                if (IsReparsePoint(attrs)) return false;
                return IsTrusted(new FileInfo(path));
            }
            catch { return false; }
        }
    }
}
