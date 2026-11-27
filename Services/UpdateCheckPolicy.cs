namespace TDPdf.Services
{
    /// <summary>
    /// The two decisions <see cref="UpdateCheck"/> makes that need no registry, network or file:
    /// whether enough time has passed since the last check, and whether the file on disk is newer
    /// than the running build. Separate so tests/PdfCore can pin them off Windows.
    /// </summary>
    internal static class UpdateCheckPolicy
    {
        /// <summary>
        /// True when a check is due: there is no usable stamp, it is at least
        /// <paramref name="interval"/> old, or it lies in the future.
        /// </summary>
        /// <param name="stamp">The stored round-trip ("O") UTC timestamp, or null when none is stored.</param>
        internal static bool IntervalElapsed(string? stamp, DateTime nowUtc, TimeSpan interval)
        {
            if (stamp is null) return true;
            if (!DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var last)) return true;
            // A stamp in the future means the clock moved backwards; treat it as due rather
            // than letting a bad clock disable checking until it catches up.
            return nowUtc - last >= interval || last > nowUtc;
        }

        /// <summary>
        /// True when <paramref name="fileVersion"/> (the EXE's file version string) parses and is
        /// newer than <paramref name="running"/>.
        /// </summary>
        internal static bool IsNewerThanRunning(string? fileVersion, Version running) =>
            Version.TryParse(fileVersion, out var onDisk) && onDisk > running;
    }
}
