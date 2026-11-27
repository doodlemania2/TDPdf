// MainWindow — Watermark and page numbers (3.0): the Tools ▸ Watermark… dialog with a live preview,
// the apply step (one undo step, through the same working-copy swap every page edit uses), and
// Remove Watermark. What is written, and how it is found again, is Services/WatermarkWriter.cs.

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PdfSharpCore.Pdf.IO;
using TDPdf.Diagnostics;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>The settings of the last watermark applied in this window, offered again next time.</summary>
        private WatermarkSpec? _lastWatermark;

        // ============================================================
        // Commands
        // ============================================================

        private void Watermark_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { SetStatus("Open a PDF first"); return; }
            CommitActiveTextBox();
            int pageCount = _doc.PageCount;
            int current = Math.Clamp(PageList.SelectedIndex, 0, pageCount - 1);

            // The preview works from a copy of the document as it is now, so the dialog never touches the
            // open one. A document that will not copy cannot be previewed, and is not watermarked blind.
            byte[] snapshot;
            try
            {
                using var ms = new MemoryStream();
                _doc.Save(ms);
                snapshot = ms.ToArray();
            }
            catch (Exception ex)
            {
                FaultSink.Report("Watermark.Snapshot", ex);
                TdpDialog.Show(this, $"The watermark preview could not be prepared:\n{ex.Message}", "Watermark", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var initial = CloneSpec(_lastWatermark) ?? new WatermarkSpec();
            var spec = ShowWatermarkDialog(initial, pageCount, current, snapshot);
            if (spec is null) return;
            _lastWatermark = CloneSpec(spec);

            WatermarkResult? result = null;
            bool done = ApplyDocumentContentEdit("Watermark", () =>
            {
                result = WatermarkWriter.Apply(_doc!, spec);
                return result.Watermarks + result.Numbered == 0 ? "Nothing was added" : DescribeWatermark(result);
            });
            if (done && result?.Notice is { } notice) SetStatusHeld(DescribeWatermark(result) + " " + notice, 6000);
        }

        private void RemoveWatermark_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { SetStatus("Open a PDF first"); return; }
            CommitActiveTextBox();
            if (WatermarkWriter.Count(_doc) == 0)
            {
                SetStatus("There is no watermark or page number added by TDPdf in this document");
                return;
            }
            int removed = 0;
            ApplyDocumentContentEdit("Remove Watermark", () =>
            {
                removed = WatermarkWriter.Remove(_doc!);
                return removed == 1 ? "Removed 1 watermark or page number" : $"Removed {removed} watermarks and page numbers";
            });
        }

        private static string DescribeWatermark(WatermarkResult r)
        {
            var parts = new List<string>();
            if (r.Watermarks > 0) parts.Add($"a watermark on {r.Watermarks} page{(r.Watermarks == 1 ? "" : "s")}");
            if (r.Numbered > 0) parts.Add($"page numbers on {r.Numbered} page{(r.Numbered == 1 ? "" : "s")}");
            return "Added " + string.Join(" and ", parts) + ".";
        }

        /// <summary>
        /// Runs a change to the pages' content as ONE undo step, the way Remove Crop does: snapshot for
        /// undo, edit the document, swap in the saved copy. Unlike a geometry change this one moves
        /// nothing, so the pending marks, redaction marks and Electronic Signature fields are all kept.
        /// </summary>
        private bool ApplyDocumentContentEdit(string title, Func<string> edit)
        {
            var redactions = _redactionMarks.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            var signFields = _ctx.ESignSession?.Fields.ToList();
            string status = "";
            bool ok = RunPageOperation(title, () =>
            {
                PushDocUndo(keepMarks: true);
                status = edit();
                SaveTempAndReload(keepAnnotations: true);
            });
            if (!ok) return false;

            // SaveTempAndReload clears these because a geometry edit would invalidate them; this edit did not.
            foreach (var (page, list) in redactions) _redactionMarks[page] = list;
            if (signFields is { Count: > 0 } && _ctx.ESignSession is { } session && session.Fields.Count == 0)
                session.Fields.AddRange(signFields);
            if (PendingRedactionCount > 0) ShowRedactSettings();
            if (PageList.SelectedIndex >= 0) RenderAllAnnotations(PageList.SelectedIndex);
            SetStatus(status);
            return true;
        }

        private static WatermarkSpec? CloneSpec(WatermarkSpec? s) => s is null ? null : new WatermarkSpec
        {
            HasWatermark = s.HasWatermark, Kind = s.Kind, Layer = s.Layer, Place = s.Place,
            Text = s.Text, FontFamily = s.FontFamily, FontSize = s.FontSize, Bold = s.Bold,
            ColorR = s.ColorR, ColorG = s.ColorG, ColorB = s.ColorB, RotationDegrees = s.RotationDegrees,
            ImageBytes = s.ImageBytes, ImagePixelWidth = s.ImagePixelWidth, ImagePixelHeight = s.ImagePixelHeight,
            ImageDpiX = s.ImageDpiX, ImageDpiY = s.ImageDpiY, ImageScalePercent = s.ImageScalePercent,
            Opacity = s.Opacity, PageNumbers = s.PageNumbers, PageNumberFormat = s.PageNumberFormat,
            NumberPlace = s.NumberPlace, NumberFontSize = s.NumberFontSize, Pages = s.Pages
        };

        // ============================================================
        // Preview
        // ============================================================

        /// <summary>
        /// Draws the current page the way it will look once the watermark is applied: the watermark is
        /// written into a one-page copy of the document, and the pending marks are burned on top of that
        /// — the order they really have, because a mark is drawn when the file is saved and a watermark
        /// is already part of the page. Returns the picture and any notice the writer has (a font that
        /// is not installed).
        /// </summary>
        private (BitmapSource? Image, string? Notice) RenderWatermarkPreview(WatermarkSpec spec, bool pageIncluded, int pageIndex, int pageCount, byte[] snapshot)
        {
            string? tempPath = null;
            try
            {
                using var ms = new MemoryStream(snapshot);
                var copy = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
                for (int i = copy.PageCount - 1; i >= 0; i--)
                    if (i != pageIndex) copy.Pages.RemoveAt(i);

                var one = CloneSpec(spec)!;
                one.Pages = pageIncluded ? new[] { 0 } : Array.Empty<int>();
                var result = WatermarkWriter.Apply(copy, one, null, _ => (pageIndex + 1, pageCount));

                if (_annotations.TryGetValue(pageIndex, out var marks) && marks.Count > 0)
                {
                    var text = new AnnotationTextLayout(
                        ta => { var sz = MeasureTextAnnotation(ta); return (sz.Width, sz.Height); },
                        (ta, maxWidth) => WrapTextToWidth(ta.Content, ta.FontSize, maxWidth, TextLayoutTypeface(ta), ta.LetterSpacing));
                    var clones = new List<PageAnnotation>(marks.Select(a => a.Clone()));
                    AnnotationBaker.Bake(copy, new[] { new KeyValuePair<int, List<PageAnnotation>>(0, clones) },
                                         _ => EnsureRenderDims(pageIndex), text);
                }

                tempPath = NewTempPath("wmprev");
                copy.Save(tempPath);
                var (dw, dh) = PdfPageGeometry.DisplaySize(copy.Pages[0]);
                int w = 560, h = (int)Math.Clamp(Math.Round(560 * dh / Math.Max(1, dw)), 100, 1400);
                var bgra = PdfiumInterop.RenderPageWithAnnotations(tempPath, 0, w, h);
                return (PageRaster.ToFrozenBitmap(bgra, w, h, 96, 96), result.Notice);
            }
            catch (Exception ex)
            {
                FaultSink.Report("Watermark.Preview", ex);
                return (null, null);
            }
            finally
            {
                if (tempPath is not null)
                {
                    TempFilePolicy.TryDelete(tempPath);
                    _ctx.TempFiles.Release(tempPath);
                }
            }
        }

        // ============================================================
        // The dialog
        // ============================================================

        private WatermarkSpec? ShowWatermarkDialog(WatermarkSpec initial, int pageCount, int currentPage, byte[] snapshot)
        {
            WatermarkSpec? result = null;
            var w = initial;                     // the working copy every control writes to
            var (win, root) = TdpDialog.CreateShell(this, "Watermark and Page Numbers", 780);
            var comboStyle = FindResource("DarkComboBox") as Style;

            // ---- small builders (the TdpDialog ones are private to that type) -----------------------
            TextBlock Label(string text, bool secondary = false) => new()
            {
                Text = text, FontSize = secondary ? 11 : 12, TextWrapping = TextWrapping.Wrap,
                Foreground = BrushResource(secondary ? "TextSecondary" : "TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
            };
            TextBox Box(string text, string name, double width = double.NaN)
            {
                var b = new TextBox
                {
                    Text = text, FontSize = 12, Width = width, Padding = new Thickness(5, 4, 5, 4),
                    Background = BrushResource("BgPanel"), Foreground = BrushResource("TextPrimary"),
                    BorderBrush = BrushResource("BorderDim"), BorderThickness = new Thickness(1),
                    CaretBrush = BrushResource("TextPrimary"), VerticalContentAlignment = VerticalAlignment.Center
                };
                TdpDialog.UseThemedTextMenu(b);
                AutomationProperties.SetName(b, name);
                return b;
            }
            ComboBox Combo(IEnumerable<string> items, int selected, string name, double width, bool editable = false)
            {
                var c = new ComboBox { Width = width, Height = 26, IsEditable = editable, VerticalContentAlignment = VerticalAlignment.Center };
                if (comboStyle is not null) c.Style = comboStyle;
                foreach (var it in items) c.Items.Add(it);
                c.SelectedIndex = selected;
                AutomationProperties.SetName(c, name);
                return c;
            }
            Button Btn(string text, bool accent)
            {
                var b = new Button
                {
                    Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
                    Background = BrushResource(accent ? "AccentGreenDim" : "BgPanel"),
                    Foreground = BrushResource(accent ? "AccentGreen" : "TextPrimary"),
                    BorderBrush = BrushResource(accent ? "AccentGreen" : "BorderDim"), BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                return b;
            }
            StackPanel Row(params UIElement[] kids)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                foreach (var k in kids) p.Children.Add(k);
                return p;
            }
            RadioButton Radio(string text, string group, bool on)
            {
                var r = new RadioButton { Content = text, GroupName = group, IsChecked = on, Foreground = BrushResource("TextPrimary"), FontSize = 12, Margin = new Thickness(0, 0, 14, 0) };
                AutomationProperties.SetName(r, text);
                return r;
            }
            CheckBox Check(string text, bool on)
            {
                var c = new CheckBox { Content = text, IsChecked = on, Foreground = BrushResource("TextPrimary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(c, text);
                return c;
            }

            var preview = new Image { Stretch = Stretch.Uniform, Width = 320, MaxHeight = 440, SnapsToDevicePixels = true };
            var previewNote = Label("", secondary: true);
            var notice = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = BrushResource("WarningOrange"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            var error = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = BrushResource("DangerRed"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
            var pagesError = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = BrushResource("DangerRed"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            Action schedule = () => { timer.Stop(); timer.Start(); };

            // Reads a typed number on leaving the box or pressing Enter: clamped into range, and put
            // back to the last good value when it is not a number.
            TextBox NumberBox(double value, double min, double max, string name, Action<double> set)
            {
                double lastGood = value;
                var b = Box(value.ToString("0.##", CultureInfo.CurrentCulture), name, 60);
                void Commit()
                {
                    if (TypedNumber.TryParse(b.Text, out double v))
                    {
                        lastGood = Math.Clamp(v, min, max);
                        set(lastGood);
                        schedule();
                    }
                    b.Text = lastGood.ToString("0.##", CultureInfo.CurrentCulture);
                }
                b.LostFocus += (_, _) => Commit();
                b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
                return b;
            }

            // ---- what to draw ----------------------------------------------------------------------
            var left = new StackPanel { Margin = new Thickness(20, 14, 12, 8) };
            var radioText = Radio("Text", "wmKind", w.HasWatermark && w.Kind == WatermarkKind.Text);
            var radioPicture = Radio("Picture", "wmKind", w.HasWatermark && w.Kind == WatermarkKind.Picture);
            var radioNone = Radio("No watermark", "wmKind", !w.HasWatermark);
            left.Children.Add(Row(Label("Watermark:"), radioText, radioPicture, radioNone));

            // Text watermark
            var textPanel = new StackPanel();
            var textBox = Box(w.Text, "Watermark text");
            textBox.TextChanged += (_, _) => { w.Text = textBox.Text; schedule(); };
            textPanel.Children.Add(textBox);
            var fonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            var fontBox = Combo(fonts, -1, "Font", 190, editable: true);
            fontBox.Text = w.FontFamily;      // kept as written, installed or not
            void ReadFont() { if (!string.IsNullOrWhiteSpace(fontBox.Text)) { w.FontFamily = fontBox.Text.Trim(); schedule(); } }
            fontBox.SelectionChanged += (_, _) => { if (fontBox.SelectedItem is string s) { w.FontFamily = s; schedule(); } };
            fontBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ReadFont()));
            var boldCheck = Check("Bold", w.Bold);
            boldCheck.Checked += (_, _) => { w.Bold = true; schedule(); };
            boldCheck.Unchecked += (_, _) => { w.Bold = false; schedule(); };
            textPanel.Children.Add(Row(Label("Font:"), fontBox, new Border { Width = 8 }, Label("Size:"),
                NumberBox(w.FontSize, WatermarkMath.MinFontSize, WatermarkMath.MaxFontSize, "Font size", v => w.FontSize = v), new Border { Width = 8 }, boldCheck));
            var swatches = new List<Border>();
            Color ActiveColor() => Color.FromRgb(w.ColorR, w.ColorG, w.ColorB);
            void ApplyColor(Color c) { w.ColorR = c.R; w.ColorG = c.G; w.ColorB = c.B; MarkActiveSwatch(swatches, c); schedule(); }
            var swatchRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new[] { Colors.Gray, Colors.Black, Colors.Red, Colors.DodgerBlue, Colors.LimeGreen, Colors.Orange })
            {
                var sw = MakeColorSwatch(c, ApplyColor);
                swatches.Add(sw);
                swatchRow.Children.Add(sw);
            }
            swatchRow.Children.Add(MakeCustomColorSwatch(ActiveColor, ApplyColor));
            MarkActiveSwatch(swatches, ActiveColor());
            textPanel.Children.Add(Row(Label("Colour:"), swatchRow));
            left.Children.Add(textPanel);

            // Picture watermark
            var picturePanel = new StackPanel();
            var pictureName = Label(w.ImageBytes is null ? "No picture chosen" : $"{w.ImagePixelWidth:0} x {w.ImagePixelHeight:0} px", secondary: true);
            var choose = Btn(w.ImageBytes is null ? "Choose picture…" : "Change…", false);
            choose.Margin = new Thickness(0, 0, 8, 0);
            choose.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog { Title = "Choose a picture for the watermark", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
                if (dlg.ShowDialog(win) != true) return;
                try
                {
                    if (new FileInfo(dlg.FileName).Length > MaxStampImageBytes) throw new InvalidDataException("That picture is larger than 20 MB.");
                    var bytes = File.ReadAllBytes(dlg.FileName);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    w.ImageBytes = bytes;
                    w.ImagePixelWidth = bmp.PixelWidth; w.ImagePixelHeight = bmp.PixelHeight;
                    // The resolution the picture was saved at (BitmapSource reports 96 when the file names none).
                    w.ImageDpiX = bmp.DpiX; w.ImageDpiY = bmp.DpiY;
                    pictureName.Text = $"{System.IO.Path.GetFileName(dlg.FileName)}: {bmp.PixelWidth} x {bmp.PixelHeight} px at {bmp.DpiX:0} dpi";
                    choose.Content = "Change…";
                    schedule();
                }
                catch (Exception ex)
                {
                    error.Text = $"Could not load the picture: {ex.Message}";
                    error.Visibility = Visibility.Visible;
                }
            };
            picturePanel.Children.Add(Row(choose, pictureName));
            picturePanel.Children.Add(Row(Label("Scale:"), NumberBox(w.ImageScalePercent, WatermarkMath.MinScalePercent, WatermarkMath.MaxScalePercent, "Picture scale", v => w.ImageScalePercent = v),
                                          Label("% of the picture's own size", secondary: true)));
            left.Children.Add(picturePanel);

            // Both kinds
            var commonPanel = new StackPanel();
            var placeBox = Combo(new[] { "Centre", "Diagonal", "Top", "Bottom" }, (int)w.Place, "Position", 110);
            placeBox.SelectionChanged += (_, _) => { if (placeBox.SelectedIndex >= 0) { w.Place = (WatermarkPlace)placeBox.SelectedIndex; schedule(); } };
            var layerBox = Combo(new[] { "Behind the page", "In front of the page" }, (int)w.Layer, "Layer", 150);
            layerBox.SelectionChanged += (_, _) => { if (layerBox.SelectedIndex >= 0) { w.Layer = (WatermarkLayer)layerBox.SelectedIndex; schedule(); } };
            commonPanel.Children.Add(Row(Label("Position:"), placeBox, new Border { Width = 8 }, Label("Layer:"), layerBox));
            commonPanel.Children.Add(Row(Label("Opacity:"), NumberBox(w.Opacity * 100, WatermarkMath.MinOpacityPercent, WatermarkMath.MaxOpacityPercent, "Opacity percent", v => w.Opacity = v / 100),
                                         Label("%"), new Border { Width = 8 }, Label("Extra turn:"),
                                         NumberBox(w.RotationDegrees, -180, 180, "Extra turn in degrees", v => w.RotationDegrees = v), Label("degrees", secondary: true)));
            left.Children.Add(commonPanel);

            void ShowKind()
            {
                bool on = radioNone.IsChecked != true;
                textPanel.Visibility = radioText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                picturePanel.Visibility = radioPicture.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                commonPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                w.HasWatermark = on;
                if (on) w.Kind = radioPicture.IsChecked == true ? WatermarkKind.Picture : WatermarkKind.Text;
                schedule();
            }
            radioText.Checked += (_, _) => ShowKind();
            radioPicture.Checked += (_, _) => ShowKind();
            radioNone.Checked += (_, _) => ShowKind();

            // ---- page numbers ------------------------------------------------------------------------
            left.Children.Add(new Border { Height = 1, Background = BrushResource("BorderDim"), Margin = new Thickness(0, 8, 0, 8) });
            var numbersCheck = Check("Add page numbers", w.PageNumbers);
            left.Children.Add(numbersCheck);
            var numbersPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            var formatBox = Box(w.PageNumberFormat, "Page number format", 170);
            formatBox.TextChanged += (_, _) => { w.PageNumberFormat = formatBox.Text; schedule(); };
            numbersPanel.Children.Add(Row(Label("Format:"), formatBox, Label("{n} = page, {N} = pages", secondary: true)));
            var numPlace = Combo(new[] { "Bottom centre", "Bottom left", "Bottom right", "Top centre", "Top left", "Top right" }, (int)w.NumberPlace, "Page number position", 130);
            numPlace.SelectionChanged += (_, _) => { if (numPlace.SelectedIndex >= 0) { w.NumberPlace = (PageNumberPlace)numPlace.SelectedIndex; schedule(); } };
            numbersPanel.Children.Add(Row(Label("Position:"), numPlace, new Border { Width = 8 }, Label("Size:"),
                NumberBox(w.NumberFontSize, WatermarkMath.MinNumberFontSize, WatermarkMath.MaxNumberFontSize, "Page number size", v => w.NumberFontSize = v)));
            numbersPanel.Visibility = w.PageNumbers ? Visibility.Visible : Visibility.Collapsed;
            left.Children.Add(numbersPanel);
            numbersCheck.Checked += (_, _) => { w.PageNumbers = true; numbersPanel.Visibility = Visibility.Visible; schedule(); };
            numbersCheck.Unchecked += (_, _) => { w.PageNumbers = false; numbersPanel.Visibility = Visibility.Collapsed; schedule(); };

            // ---- pages --------------------------------------------------------------------------------
            left.Children.Add(new Border { Height = 1, Background = BrushResource("BorderDim"), Margin = new Thickness(0, 8, 0, 8) });
            string initialPages = initial.Pages.Count > 0 && initial.Pages.All(p => p < pageCount)
                ? FormatPageList(initial.Pages) : pageCount == 1 ? "1" : $"1-{pageCount}";
            var pagesBox = Box(initialPages, "Pages", 160);
            pagesBox.TextChanged += (_, _) => schedule();
            left.Children.Add(Row(Label("Pages:"), pagesBox, Label($"of {pageCount}; for example 1-3,7", secondary: true)));
            left.Children.Add(pagesError);
            left.Children.Add(notice);
            left.Children.Add(error);

            // ---- preview column ----------------------------------------------------------------------
            var right = new StackPanel { Margin = new Thickness(8, 14, 20, 8), Width = 340 };
            right.Children.Add(Label($"Preview of page {currentPage + 1}, with your marks on top", secondary: true));
            right.Children.Add(new Border { BorderBrush = BrushResource("BorderDim"), BorderThickness = new Thickness(1), Background = BrushResource("BgPanel"), Child = preview, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
            right.Children.Add(previewNote);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            root.Children.Add(grid);

            var apply = Btn("Apply", true);
            apply.IsDefault = true;
            var cancel = Btn("Cancel", false);
            cancel.IsCancel = true;
            cancel.Click += (_, _) => win.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            root.Children.Add(new Border { Padding = new Thickness(16, 6, 16, 14), Child = buttons });

            // ---- behaviour ------------------------------------------------------------------------------
            bool ReadPages(out List<int> pages)
            {
                bool ok = PageRangeSpec.TryParse(pagesBox.Text, pageCount, out pages, out string perr);
                pagesError.Text = ok ? "" : perr;
                pagesError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
                return ok;
            }
            void Refresh()
            {
                timer.Stop();
                error.Visibility = Visibility.Collapsed;
                bool pagesOk = ReadPages(out var pages);
                apply.IsEnabled = pagesOk && w.HasContent;
                bool included = pagesOk && pages.Contains(currentPage);
                var (img, note) = RenderWatermarkPreview(w, included, currentPage, pageCount, snapshot);
                if (img is not null) preview.Source = img;
                previewNote.Text = !w.HasContent ? "Nothing to add yet" : !pagesOk ? "" : included ? "" : "This page is not in the list, so it is shown as it is";
                notice.Text = note ?? "";
                notice.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            }
            timer.Tick += (_, _) => Refresh();
            apply.Click += (_, _) =>
            {
                if (!ReadPages(out var pages)) return;
                if (!w.HasContent) return;
                w.Pages = pages;
                result = CloneSpec(w);
                win.Close();
            };
            win.Loaded += (_, _) =>
            {
                ShowKind();
                Refresh();
                if (radioText.IsChecked == true) { textBox.Focus(); textBox.SelectAll(); }
            };
            win.Closed += (_, _) => timer.Stop();
            win.ShowDialog();
            return result;
        }

        /// <summary>A 0-based page list as the typed form ("1-3,7").</summary>
        private static string FormatPageList(IReadOnlyList<int> pages)
        {
            var sorted = pages.Distinct().OrderBy(p => p).ToList();
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                parts.Add(j == i ? (sorted[i] + 1).ToString(CultureInfo.InvariantCulture)
                                 : $"{sorted[i] + 1}-{sorted[j] + 1}");
                i = j + 1;
            }
            return string.Join(",", parts);
        }
    }
}
