using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // Save Flattened options        (#234)
        // ============================================================
        //
        // A small step before the file dialog: pick the resolution the pages are rasterised at
        // (Automatic is what Save Flattened has always chosen per page, see FlattenResolution) and
        // see the pixel size and scale it gives for the first page and for the largest page.
        // Cancelling here, like Esc, ends the command before any file is touched.
        // The choice is remembered (Settings.FlattenDpi, 0 = Automatic).

        /// <summary>
        /// Asks for the flatten resolution. Returns the choice (0 = Automatic) or null when the
        /// user cancelled. <paramref name="autoFirst"/> and <paramref name="autoLargest"/> are the Automatic DPI of the first page
        /// and of the largest page, for the readout.
        /// </summary>
        private double? ShowFlattenOptionsDialog(IReadOnlyList<PdfPageSize> pageSizes, double autoFirst, double autoLargest)
        {
            if (pageSizes.Count == 0) return 0;
            var sizes = pageSizes.Select(p => (p.WidthPoint, p.HeightPoint)).ToList();
            int largest = FlattenResolution.LargestPage(sizes);
            if (largest < 0) largest = 0;

            double remembered = 0;
            try { remembered = FlattenResolution.NormaliseChoice(TDPdf.Properties.Settings.Default.FlattenDpi); } catch { }

            var bgPanel       = BrushResource("BgPanel");
            var borderDim     = BrushResource("BorderDim");
            var textPrimary   = BrushResource("TextPrimary");
            var textSecondary = BrushResource("TextSecondary");
            var accent        = BrushResource("AccentGreen");

            var (win, shellBody) = TdpDialog.CreateShell(this, "Save Flattened", 420);
            win.ShowInTaskbar = false;
            win.Foreground = textPrimary;
            win.FontFamily = new FontFamily("Segoe UI");
            win.FontSize = 12;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "Every page becomes a picture, so nothing stays editable or selectable.",
                Foreground = textSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Resolution", Foreground = textSecondary, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var box = new ComboBox { Style = (Style)FindResource("DarkComboBox"), Height = 28 };
            box.Items.Add("Automatic (follows each page's own images)");
            foreach (double d in FlattenResolution.FixedChoices) box.Items.Add($"{d:0} dpi");
            box.SelectedIndex = remembered > 0 ? Array.IndexOf(FlattenResolution.FixedChoices, remembered) + 1 : 0;
            root.Children.Add(box);

            var readout = new TextBlock
            {
                Foreground = textSecondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
            };
            root.Children.Add(readout);

            double Choice() => box.SelectedIndex <= 0 ? 0 : FlattenResolution.FixedChoices[box.SelectedIndex - 1];

            string Line(string label, int index, double autoDpi)
            {
                var (w, h) = sizes[index];
                double dpi = FlattenResolution.ForChoice(Choice(), autoDpi, w, h);
                var (px, py) = FlattenResolution.PixelSize(w, h, dpi);
                return $"{label} (page {index + 1}): {px} × {py} px, {dpi:0} dpi, {FlattenResolution.ScalePercent(dpi)}% scale.";
            }

            void Update()
            {
                readout.Text = largest == 0
                    ? Line("Output", 0, autoFirst)
                    : Line("First page", 0, autoFirst) + "\n" + Line("Largest page", largest, autoLargest);
            }
            box.SelectionChanged += (_, _) => Update();
            Update();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel", Width = 96, Height = 30, Margin = new Thickness(0, 0, 8, 0),
                Background = bgPanel, Foreground = textPrimary, BorderBrush = borderDim, Cursor = Cursors.Hand, IsCancel = true
            };
            var okBtn = new Button
            {
                Content = "Continue", Width = 96, Height = 30, Background = accent, Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold, BorderBrush = accent, Cursor = Cursors.Hand, IsDefault = true
            };
            buttons.Children.Add(cancelBtn);
            buttons.Children.Add(okBtn);
            root.Children.Add(buttons);
            shellBody.Children.Add(root);

            cancelBtn.Click += (_, _) => win.DialogResult = false;
            okBtn.Click += (_, _) => win.DialogResult = true;
            win.Loaded += (_, _) => box.Focus();
            if (win.ShowDialog() != true) return null;

            double chosen = Choice();
            try
            {
                TDPdf.Properties.Settings.Default.FlattenDpi = chosen;
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* a settings write failure must not stop the save */ }
            return chosen;
        }
    }
}
