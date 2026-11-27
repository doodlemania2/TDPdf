namespace TDPdf.Services
{
    internal enum MonitorColorDecision
    {
        /// <summary>The setting is off: no transform.</summary>
        SettingOff,
        /// <summary>Windows reports no profile for the monitor: no transform.</summary>
        NoProfile,
        /// <summary>The profile could not be read as an ICC profile: no transform.</summary>
        Unreadable,
        /// <summary>The monitor is HDR / Advanced Color; Windows manages it: no transform.</summary>
        AdvancedColor,
        /// <summary>The monitor profile is sRGB: pixels are already right, no transform.</summary>
        Srgb,
        /// <summary>A real, non-sRGB profile: convert from sRGB to it.</summary>
        Match,
    }

    /// <summary>What was seen for one window's monitor, enough to tell whether it changed.</summary>
    internal readonly record struct MonitorColorState(long MonitorId, string? ProfilePath, string? ProfileId, bool AdvancedColor);

    /// <summary>
    /// The decision about monitor colour matching, apart from any Windows call so it can be tested.
    /// The rule: match automatically when Windows reports a non-sRGB profile for the monitor the
    /// window is on, leave everything alone for sRGB, HDR or no profile, and always honour the setting.
    /// </summary>
    internal static class ColorMonitorPolicy
    {
        public static MonitorColorDecision Decide(bool settingOn, bool advancedColor, string? profilePath, IccProfile? profile)
        {
            if (!settingOn) return MonitorColorDecision.SettingOff;
            if (advancedColor) return MonitorColorDecision.AdvancedColor;
            if (string.IsNullOrWhiteSpace(profilePath)) return MonitorColorDecision.NoProfile;
            if (profile is null) return MonitorColorDecision.Unreadable;
            return profile.IsSrgbLike() ? MonitorColorDecision.Srgb : MonitorColorDecision.Match;
        }

        /// <summary>True when the transform has to be decided again: another monitor, another profile or a mode change.</summary>
        public static bool NeedsRefresh(MonitorColorState? previous, MonitorColorState current) =>
            previous is not { } p || p != current;

        /// <summary>A short line for Settings saying what is in effect and why.</summary>
        public static string Describe(MonitorColorDecision d, string? profileName, string? nativeUnavailable) => d switch
        {
            MonitorColorDecision.SettingOff => "Off. Pages are shown as plain sRGB.",
            MonitorColorDecision.NoProfile => "Windows reports no colour profile for this monitor, so pages are shown as plain sRGB.",
            MonitorColorDecision.Unreadable => "This monitor's colour profile could not be read, so pages are shown as plain sRGB.",
            MonitorColorDecision.AdvancedColor => "This monitor uses HDR, which Windows manages itself, so TDPdf leaves colours alone.",
            MonitorColorDecision.Srgb => "This monitor uses an sRGB profile, so pages are shown unchanged.",
            _ => string.IsNullOrEmpty(nativeUnavailable)
                ? $"Matching this monitor's profile{(string.IsNullOrWhiteSpace(profileName) ? "" : ": " + profileName)}."
                : $"Matching this monitor's profile{(string.IsNullOrWhiteSpace(profileName) ? "" : ": " + profileName)}. {nativeUnavailable}",
        };
    }
}
