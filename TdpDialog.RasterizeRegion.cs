// TdpDialog: the Rasterize Region options (#233). Same shell and button chrome as the other prompts.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TDPdf.Services;

namespace TDPdf
{
    internal static partial class TdpDialog
    {
        /// <summary>
        /// Rasterize Region: the resolution, and a plain statement of what this does and does not do.
        /// <paramref name="planAt"/> reports the picture's size in pixels at a resolution (null when
        /// the box is off the page). Returns the chosen dpi, or null when cancelled.
        /// </summary>
        public static int? ShowRasterizeRegion(Window? owner, Func<int, RegionRaster.RegionPlan?> planAt)
        {
            int? result = null;
            var (win, root) = CreateShell(owner, "Rasterize Region", 420);

            root.Children.Add(Pad(MakeLabel(
                "Turn this area into a picture. The text and graphics inside it are taken off the page, " +
                "so they cannot be searched, selected or copied from under the picture."), top: 16, bottom: 8));
            root.Children.Add(Pad(MakeLabel(
                "This is not redaction. The picture still shows everything that was in the area, and a " +
                "reader can see it. To hide text or images for good, use Redact.", secondary: true), bottom: 8));
            root.Children.Add(Pad(MakeLabel(
                "Comments, links and form fields on the page are left as they are.", secondary: true), bottom: 12));

            root.Children.Add(Pad(MakeLabel("Resolution:"), bottom: 2));
            var radios = new List<(RadioButton Radio, int Dpi)>();
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (int dpi in RegionRaster.DpiChoices)
            {
                string label = dpi == RegionRaster.DefaultDpi ? $"{dpi} dpi (good for printing)"
                             : dpi < RegionRaster.DefaultDpi ? $"{dpi} dpi (smaller file)"
                             : $"{dpi} dpi (finest detail, large)";
                var rb = new RadioButton
                {
                    Content = label,
                    IsChecked = dpi == RegionRaster.DefaultDpi,
                    GroupName = "rasterDpi",
                    Foreground = Brush("TextPrimary"),
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                AutomationProperties.SetName(rb, label);
                radios.Add((rb, dpi));
                panel.Children.Add(rb);
            }
            root.Children.Add(Pad(panel, bottom: 6));

            var size = MakeLabel("", secondary: true);
            root.Children.Add(Pad(size, bottom: 6));

            var okBtn = MakeThemedButton("Rasterize", accent: true);
            okBtn.IsDefault = true;
            var cancelBtn = MakeThemedButton("Cancel", accent: false);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => win.Close();

            int Chosen()
            {
                foreach (var (rb, dpi) in radios) if (rb.IsChecked == true) return dpi;
                return RegionRaster.DefaultDpi;
            }

            void Refresh()
            {
                var plan = planAt(Chosen());
                if (plan is null)
                {
                    size.Text = "That box is not on the page.";
                    size.Foreground = Brush("DangerRed");
                    okBtn.IsEnabled = false;
                }
                else if (plan.Pixels > RegionRaster.MaxPixels)
                {
                    size.Text = $"At this resolution the picture would be {plan.Width:N0} x {plan.Height:N0} pixels, which is too large. Choose a lower resolution.";
                    size.Foreground = Brush("DangerRed");
                    okBtn.IsEnabled = false;
                }
                else
                {
                    size.Text = $"The picture will be {plan.Width:N0} x {plan.Height:N0} pixels.";
                    size.Foreground = Brush("TextSecondary");
                    okBtn.IsEnabled = true;
                }
            }
            foreach (var (rb, _) in radios) rb.Checked += (_, _2) => Refresh();
            okBtn.Click += (_, _2) =>
            {
                if (!okBtn.IsEnabled) return;
                result = Chosen();
                win.Close();
            };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(new Border { Padding = new Thickness(16, 12, 16, 16), Child = btnPanel });
            Refresh();

            win.ShowDialog();
            return result;
        }
    }
}
