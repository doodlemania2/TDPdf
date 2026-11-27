// MainWindow — offering the last session back at launch (#240).
// The save/merge side and RestoreSessionAsync live in MainWindow.xaml.cs; this is the launch-time
// decision and the non-modal bar that carries it.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>
        /// True while a saved session exists that this window has neither restored nor dismissed:
        /// TDPdf was started with a file, or the restore bar has not been answered. Closing the
        /// window then leaves the saved session as it is rather than replacing it.
        /// </summary>
        private bool _sessionPending;

        private Border? _sessionBar;

        /// <summary>
        /// Launch with no file: restores the saved session straight away when "Always restore last
        /// session" is on, otherwise offers it in a bar. Nothing is offered when every saved file
        /// has gone, and that is said instead.
        /// </summary>
        private async Task OfferSessionRestoreAsync()
        {
            string mode = TDPdf.Properties.Settings.Default.ReopenSession;
            if (mode == "No" || _sessionContributed.Count == 0) return;

            var (present, missing) = TDPdf.Services.SessionState.SplitExisting(
                _sessionContributed, System.IO.File.Exists, System.IO.Path.GetTempPath());
            if (present.Count == 0)
            {
                ReportSessionSkipped(0, missing);
                return;
            }
            if (mode == "Yes")
            {
                await RestoreSessionAsync();
                return;
            }
            _sessionPending = true;
            ShowSessionRestoreBar(present.Count);
        }

        private void ShowSessionRestoreBar(int tabCount)
        {
            HideSessionRestoreBar();
            if (PagePreviewPanel.Parent is not Grid previewGrid) return;

            var label = new TextBlock
            {
                Text = tabCount == 1 ? "Restore last session (1 tab)" : $"Restore last session ({tabCount} tabs)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            var restore = new Button
            {
                Content = "Restore",
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("ToolbarButtonAccent"),
                ToolTip = "Reopen the tabs that were open when TDPdf last closed"
            };
            AutomationProperties.SetName(restore, "Restore last session");
            var dismiss = new Button
            {
                Content = "Dismiss",
                Padding = new Thickness(12, 3, 12, 3),
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Forget the last session and start fresh"
            };
            AutomationProperties.SetName(dismiss, "Dismiss");

            restore.Click += async (_, _) =>
            {
                restore.IsEnabled = dismiss.IsEnabled = false;
                await RestoreSessionAsync();
            };
            dismiss.Click += (_, _) =>
            {
                _sessionPending = false;   // answered: closing now saves what is open, replacing the old session
                HideSessionRestoreBar();
                SetStatus("Last session dismissed");
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 6) };
            row.Children.Add(label);
            row.Children.Add(restore);
            row.Children.Add(dismiss);

            _sessionBar = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = row
            };
            _sessionBar.SetResourceReference(Border.BackgroundProperty, "BgPanel");
            _sessionBar.SetResourceReference(Border.BorderBrushProperty, "BorderDim");
            AutomationProperties.SetName(_sessionBar, "Restore last session");
            AutomationProperties.SetLiveSetting(_sessionBar, AutomationLiveSetting.Polite);
            Panel.SetZIndex(_sessionBar, 90);
            previewGrid.Children.Add(_sessionBar);
        }

        private void HideSessionRestoreBar()
        {
            if (_sessionBar is null) return;
            (_sessionBar.Parent as Panel)?.Children.Remove(_sessionBar);
            _sessionBar = null;
        }
    }
}
