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
    }
}
