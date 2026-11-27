using System.Windows;

namespace TDPdf
{
    // "What's new": the running version's release notes, shown once after an update and from Help.
    public partial class MainWindow
    {
        /// <summary>
        /// Opens the notes once after an update (never on a fresh install, and not when the user
        /// or an administrator turned it off). Call from the main window's first Loaded, before the
        /// update check stamps its first run.
        /// </summary>
        private void ShowWhatsNewIfUpdated(Version running)
        {
            if (!TDPdf.Services.WhatsNewState.ShouldShowAtLaunchAndRecord(running)) return;
            Dispatcher.BeginInvoke(new Action(() => TDPdf.Services.WhatsNewWindow.Show(this)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void HelpWhatsNew_Click(object sender, RoutedEventArgs e) =>
            TDPdf.Services.WhatsNewWindow.Show(this);

        /// <summary>The Settings box for the automatic what's new window.</summary>
        private void AddWhatsNewSettingsRow(System.Windows.Controls.Panel panel)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = "Show what's new after updates",
                IsChecked = TDPdf.Services.WhatsNewState.ShowAfterUpdates,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 12)
            };
            if (!TDPdf.Services.WhatsNewState.IsAllowedByPolicy())
            {
                box.IsEnabled = false;
                box.ToolTip = "Your organisation turned off the automatic what's new window.";
            }
            box.Checked += (_, _) => TDPdf.Services.WhatsNewState.ShowAfterUpdates = true;
            box.Unchecked += (_, _) => TDPdf.Services.WhatsNewState.ShowAfterUpdates = false;
            panel.Children.Add(box);
        }
    }
}
