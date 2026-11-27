namespace TDPdf.Services
{
    /// <summary>
    /// The decision behind "show what's new after an update", kept free of WPF and the registry so
    /// it can be tested. See <see cref="WhatsNewState"/> for where the inputs come from.
    /// </summary>
    internal static class WhatsNewPolicy
    {
        /// <summary>
        /// True when the notes should open by themselves on this launch.
        /// </summary>
        /// <param name="lastSeen">The version whose notes were last shown or skipped, or null / empty if none is stored.</param>
        /// <param name="hasPriorUse">
        /// True when this machine has run TDPdf before even though no version is stored. Builds
        /// before this feature stored none, so without it the first update to a build that has it
        /// would look like a fresh install and never show anything.
        /// </param>
        /// <param name="running">The running version.</param>
        /// <param name="userWantsIt">The "Show what's new after updates" box.</param>
        /// <param name="policyAllows">False when an administrator turned the popup off.</param>
        public static bool ShouldShow(string? lastSeen, bool hasPriorUse, Version running, bool userWantsIt, bool policyAllows)
        {
            if (!userWantsIt || !policyAllows) return false;
            if (string.IsNullOrWhiteSpace(lastSeen)) return hasPriorUse;   // none stored and never run: a fresh install
            return Version.TryParse(lastSeen, out var seen) && seen < running;
        }

        /// <summary>
        /// The value to store after a launch. Always the running version, except that an
        /// unreadable stored value is replaced too, so a damaged one cannot repeat the popup.
        /// </summary>
        public static string VersionToStore(Version running) => running.ToString();

        /// <summary>The oldest notes offered: v2.0.1.0 is the first file written for users.</summary>
        public static readonly Version OldestNotes = new(2, 0, 1, 0);

        /// <summary>At most this many versions are shown after a long gap between updates.</summary>
        public const int MaxVersionsShown = 10;

        /// <summary>
        /// The versions whose notes to show, newest first: every available version newer than
        /// <paramref name="lastSeen"/> and no newer than <paramref name="running"/>, so someone who
        /// skipped updates sees each one they missed. With nothing stored (or an unreadable value),
        /// only the running version. Versions older than <see cref="OldestNotes"/> are never offered.
        /// </summary>
        public static IReadOnlyList<Version> VersionsToShow(IEnumerable<Version> available, string? lastSeen, Version running)
        {
            Version? seen = Version.TryParse(lastSeen ?? "", out var v) ? v : null;
            var picked = available
                .Where(x => x >= OldestNotes && x <= running && (seen is null ? x == running : x > seen))
                .Distinct()
                .OrderByDescending(x => x)
                .Take(MaxVersionsShown)
                .ToList();
            return picked;
        }

        /// <summary>
        /// The version in an embedded notes resource name such as <c>TDPdf.ReleaseNotes.v2.3.0.0.md</c>,
        /// or null for any other name.
        /// </summary>
        public static Version? VersionFromResourceName(string name)
        {
            const string prefix = "TDPdf.ReleaseNotes.v", suffix = ".md";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)) return null;
            string core = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
            return Version.TryParse(core, out var v) && core.Split('.').Length == 4 ? v : null;
        }
    }
}
