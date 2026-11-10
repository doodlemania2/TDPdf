using System.Windows;
using Microsoft.Win32;

namespace TDPdf.Services
{
    public enum Theme
    {
        Light,
        Dark,
        System
    }

    internal static class ThemeManager
    {
        private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string AppsUseLightTheme = "AppsUseLightTheme";

        public static Theme RequestedTheme { get; private set; } = Theme.System;
        public static Theme EffectiveTheme { get; private set; } = Theme.Dark;
        public static event EventHandler? ThemeChanged;

        public static void Initialize(Theme requestedTheme)
        {
            RequestedTheme = requestedTheme;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            Apply(requestedTheme);
        }

        public static void Cleanup()
        {
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        }

        public static void Apply(Theme requestedTheme)
        {
            RequestedTheme = requestedTheme;
            EffectiveTheme = requestedTheme == Theme.System ? GetSystemTheme() : requestedTheme;
            SwapDictionary(SystemParameters.HighContrast ? "HighContrast" : EffectiveTheme.ToString());
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static Theme GetSystemTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue(AppsUseLightTheme) is int value && value == 0 ? Theme.Dark : Theme.Light;
            }
            catch
            {
                return Theme.Light;
            }
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle)
            {
                return;
            }

            var app = Application.Current;
            if (app?.Dispatcher.CheckAccess() == true)
            {
                Apply(RequestedTheme);
            }
            else
            {
                app?.Dispatcher.BeginInvoke(new Action(() => Apply(RequestedTheme)));
            }
        }

        private static void SwapDictionary(string themeName)
        {
            var resources = Application.Current.Resources.MergedDictionaries;
            for (int i = resources.Count - 1; i >= 0; i--)
            {
                var source = resources[i].Source?.OriginalString;
                if (source != null && source.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0)
                    resources.RemoveAt(i);
            }

            resources.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{themeName}.xaml", UriKind.Absolute)
            });
        }
    }
}
