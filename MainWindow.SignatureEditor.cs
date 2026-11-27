// MainWindow — the signature editor.
// Create Signature (drawn: ink colour and pen width), Import Image (photo clean-up: threshold,
// feather, ink colour), Edit on a saved signature, and "export as a PNG for the signing page".
// The pixel work is Services/SignatureInk.cs; this file only moves bitmaps in and out of it.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // The ink choices in the editor. The first is black, which is what every signature was before.
        private static readonly (string Hex, string Label)[] SignatureInkChoices =
        [
            ("#000000", "Black"), ("#122A88", "Blue"), ("#B00020", "Red"), ("#0B6B2E", "Green")
        ];

        private static readonly Dictionary<string, SolidColorBrush> SignatureBrushCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A frozen brush for a stored ink colour; black when it is missing or unreadable.</summary>
        private static SolidColorBrush SignatureInkBrush(string? hex)
        {
            var (r, g, b) = SignatureInk.ResolveColor(hex);
            string key = SignatureInk.FormatColor(r, g, b);
            if (!SignatureBrushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                SignatureBrushCache[key] = brush;
            }
            return brush;
        }

        /// <summary>Height, in signature units, of the date strip a placed signature really has (0 for none).</summary>
        private static double SigStrip(SignatureAnnotation sa) =>
            SignatureInk.EffectiveDateStrip(sa.DateText, sa.DateStrip, sa.SourceHeight);

        /// <summary>The date line of a signature as a text block, to be placed at the top of its strip. Null when it has none.</summary>
        private TextBlock? BuildSignatureDateBlock(SignatureAnnotation sa, double scale)
        {
            double strip = SigStrip(sa);
            if (strip <= 0 || sa.DateText is null) return null;
            return MakeSignatureDateBlock(sa.DateText, strip, sa.SourceWidth, scale, sa.ImageData is null ? sa.InkColor : null);
        }

        private static TextBlock MakeSignatureDateBlock(string text, double strip, double boxWidth, double scale, string? inkColor)
        {
            double font = SignatureInk.DateFontSize(strip, boxWidth) * scale;
            return new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = Math.Max(font, 1),
                Foreground = SignatureInkBrush(inkColor),
                Height = strip * scale,
                IsHitTestVisible = false
            };
        }

        // ------------------------------------------------------------------
        // Bitmap <-> buffer
        // ------------------------------------------------------------------

        /// <summary>Decodes any image WPF reads to a straight-alpha BGRA buffer, scaled down so its longest side is at most <paramref name="maxSide"/>.</summary>
        private static byte[] DecodeBgra(byte[] bytes, int maxSide, out int w, out int h)
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            BitmapSource src = bmp;
            int side = Math.Max(bmp.PixelWidth, bmp.PixelHeight);
            if (side > maxSide && maxSide > 0)
            {
                double f = (double)maxSide / side;
                src = new TransformedBitmap(src, new ScaleTransform(f, f));
            }
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth; h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            return px;
        }

        private static BitmapSource ToBitmap(byte[] px, int w, int h)
        {
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bs.Freeze();
            return bs;
        }

        private static byte[] SigEncodePng(BitmapSource bs)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static byte[] SigEncodeJpeg(BitmapSource bs)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------
        // Shared editor controls
        // ------------------------------------------------------------------

        private TextBlock EditorLabel(string text, Thickness margin) => new()
        {
            Text = text,
            Foreground = BrushResource("TextSecondary"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin
        };

        /// <summary>A row of ink swatches. <paramref name="includeOriginal"/> adds "Original" (null), which keeps a picture's own colours.</summary>
        private StackPanel BuildInkRow(string? initial, bool includeOriginal, Action<string?> onChange)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 12, 4) };
            row.Children.Add(EditorLabel("Ink", new Thickness(0, 0, 8, 0)));
            var buttons = new List<(Button Btn, string? Hex)>();
            void Mark(string? hex)
            {
                foreach (var (b, h) in buttons)
                {
                    bool sel = string.Equals(h, hex, StringComparison.OrdinalIgnoreCase);
                    b.BorderBrush = BrushResource(sel ? "AccentGreen" : "BorderDim");
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
            }
            void Add(string? hex, string label)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (hex is not null)
                    sp.Children.Add(new Border
                    {
                        Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                        Background = SignatureInkBrush(hex),
                        Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
                    });
                sp.Children.Add(new TextBlock
                {
                    Text = label, Foreground = BrushResource("TextPrimary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                });
                var btn = new Button
                {
                    Content = sp,
                    Style = (Style)FindResource("DarkButton"),
                    Background = BrushResource("BgHover"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand
                };
                System.Windows.Automation.AutomationProperties.SetName(btn, label + " ink");
                btn.Click += (_, _) => { Mark(hex); onChange(hex); };
                buttons.Add((btn, hex));
                row.Children.Add(btn);
            }
            if (includeOriginal) Add(null, "Original");
            foreach (var (hex, label) in SignatureInkChoices) Add(hex, label);
            Mark(initial);
            return row;
        }

        /// <summary>A labelled slider row; <paramref name="format"/> turns the value into the text shown beside it.</summary>
        private StackPanel BuildSliderRow(string label, double min, double max, double value, double tick,
            Func<double, string> format, Action<double> onChange)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 12, 4) };
            var lab = EditorLabel(label, new Thickness(0, 0, 8, 0));
            lab.Width = 72;
            row.Children.Add(lab);
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = value, Width = 220,
                TickFrequency = tick, IsSnapToTickEnabled = tick >= 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            var shown = EditorLabel(format(value), new Thickness(8, 0, 0, 0));
            slider.ValueChanged += (_, e) => { shown.Text = format(e.NewValue); onChange(e.NewValue); };
            row.Children.Add(slider);
            row.Children.Add(shown);
            return row;
        }

        private Button EditorButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = BrushResource(primary ? "AccentGreenDim" : "BgHover"),
                Foreground = BrushResource(primary ? "AccentGreen" : "TextPrimary"),
                BorderBrush = BrushResource(primary ? "AccentGreen" : "BorderDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            if (primary) b.FontWeight = FontWeights.SemiBold;
            return b;
        }

        // ------------------------------------------------------------------
        // Edit entry point
        // ------------------------------------------------------------------

        /// <summary>Opens a saved signature in the editor it was made in. Typed and older imported pictures edit like imports.</summary>
        private void EditSavedSignature(SavedSignature sig)
        {
            if (sig.ImageData is null) { OpenSignatureCreator(sig); return; }
            try
            {
                byte[] bytes = Convert.FromBase64String(sig.SourceImageData ?? sig.ImageData);
                OpenImageSignatureEditor(bytes, sig.Name, sig);
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"This signature picture could not be opened for editing:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ------------------------------------------------------------------
        // Drawn signature
        // ------------------------------------------------------------------

        private void OpenSignatureCreator(SavedSignature? editing = null)
        {
            // The shared dialog shell (design width 460 at scale 1), so this follows the app size
            // and stays on screen; its title bar already carries the close button.
            var (win, rootStack, closeWinBtn) = TdpDialog.CreateShellWithClose(this,
                editing is null ? "Create Signature" : "Edit Signature", 460);
            closeWinBtn.Click += (_, _2) => win.Close();

            var contentArea = new StackPanel();

            string inkHex = SignatureInk.TryParseColor(editing?.InkColor, out _, out _, out _) ? editing!.InkColor! : "#000000";
            double penWidth = SignatureInk.ResolvePenWidth(editing?.PenWidth);

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4),
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            var placeholder = new TextBlock
            {
                Text = "Draw your signature here",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14, FontStyle = FontStyles.Italic,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            drawCanvas.Children.Add(placeholder);

            // Drawing state. The polylines are kept so a change of ink or width restyles what is drawn.
            var strokes = new List<List<Point>>();
            var polys = new List<Polyline>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;

            Polyline NewPoly() => new()
            {
                Stroke = SignatureInkBrush(inkHex),
                StrokeThickness = penWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            void Restyle()
            {
                foreach (var p in polys)
                {
                    p.Stroke = SignatureInkBrush(inkHex);
                    p.StrokeThickness = penWidth;
                }
            }

            // Editing: bring the saved strokes in, fitted to this canvas once it has a size.
            if (editing is not null)
            {
                placeholder.Visibility = Visibility.Collapsed;
                canvasBorder.Loaded += (_, _) =>
                {
                    double aw = drawCanvas.ActualWidth, ah = drawCanvas.ActualHeight;
                    double f = aw > 0 && ah > 0 ? Math.Min(aw / SigCanvasW(editing), ah / SigCanvasH(editing)) : 1;
                    if (!double.IsFinite(f) || f <= 0) f = 1;
                    foreach (var s in editing.Strokes)
                    {
                        var pts = s.Select(q => new Point(q.X * f, q.Y * f)).ToList();
                        if (pts.Count < 2) continue;
                        strokes.Add(pts);
                        var poly = NewPoly();
                        foreach (var q in pts) poly.Points.Add(q);
                        polys.Add(poly);
                        drawCanvas.Children.Add(poly);
                    }
                };
            }

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = NewPoly();
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Clamp(pos.X, 0, drawCanvas.ActualWidth);
                pos.Y = Math.Clamp(pos.Y, 0, drawCanvas.ActualHeight);
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                {
                    strokes.Add(currentStroke);
                    if (currentPoly is not null) polys.Add(currentPoly);
                }
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);
            contentArea.Children.Add(BuildInkRow(inkHex, false, hex => { inkHex = hex ?? "#000000"; Restyle(); }));
            contentArea.Children.Add(BuildSliderRow("Pen width", 1, 8, Math.Clamp(penWidth, 1, 8), 0.5,
                v => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                v => { penWidth = v; Restyle(); }));

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = EditorButton("Clear", false);
            clearBtn.Margin = new Thickness(0, 0, 8, 0);
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                polys.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = EditorButton("Save Signature", true);
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    TdpDialog.Show(this, "Draw a signature first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = editing ?? new SavedSignature { Name = $"Signature {_savedSignatures.Count + 1}" };
                saved.CanvasWidth = cw;
                saved.CanvasHeight = ch;
                saved.InkColor = inkHex;
                saved.PenWidth = penWidth;
                saved.Strokes.Clear();
                foreach (var stroke in strokes)
                    saved.Strokes.Add(stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList());
                FinishSavedSignature(saved, editing is not null, "Signature picture saved. Click on the page to place it");
                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            win.ShowDialog();
        }

        /// <summary>Stores a new or edited signature. A new one is picked up for placing; an edited one just goes back to the picker.</summary>
        private void FinishSavedSignature(SavedSignature saved, bool wasEdit, string placeStatus)
        {
            if (!wasEdit) _savedSignatures.Add(saved);
            PersistSignatures();
            // The list was replaced by what was merged; pick the live entry with this id.
            var live = _savedSignatures.FirstOrDefault(x => x.Id == saved.Id) ?? saved;
            if (wasEdit)
            {
                SetStatus("Signature picture updated. Signatures already placed on pages are not changed");
                ShowSignaturePopup();
                return;
            }
            _pendingSignature = live;
            _annotationCanvas.Cursor = GrabbingCursor;   // carrying a signature until it is dropped
            SetStatus(placeStatus);
        }

        // ------------------------------------------------------------------
        // Imported picture
        // ------------------------------------------------------------------

        private void ImportImageSignature()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Import Signature Image"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                OpenImageSignatureEditor(File.ReadAllBytes(dlg.FileName), System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), null);
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Failed to import image:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenImageSignatureEditor(byte[] sourceBytes, string name, SavedSignature? editing)
        {
            // Full-size source (capped), and a small copy for the live preview.
            byte[] full = DecodeBgra(sourceBytes, SignatureInk.MaxProcessSide, out int fw, out int fh);
            byte[] small = full;
            int sw = fw, sh = fh;
            const int previewSide = 640;
            if (Math.Max(fw, fh) > previewSide)
                small = DecodeBgra(sourceBytes, previewSide, out sw, out sh);
            bool opaque = SignatureInk.IsFullyOpaque(full, fw, fh);
            // An older signature has no stored source, so its own picture is the source and its canvas size
            // is the picture's logical size (a typed signature is drawn at 2x): keep that ratio on save.
            double sizeFactor = editing is not null && editing.SourceImageData is null && fw > 0 ? SigCanvasW(editing) / fw : 1;
            if (!double.IsFinite(sizeFactor) || sizeFactor <= 0) sizeFactor = 1;

            // First open of a photo: start with the paper dropped. Re-opening starts from what was chosen.
            int threshold = editing is not null ? Math.Clamp(editing.Threshold ?? 0, 0, 255) : (opaque ? 150 : 0);
            int feather = editing is not null ? Math.Clamp(editing.Feather ?? 0, 0, 10) : 0;
            string? ink = editing is not null ? editing.InkColor : (opaque ? "#000000" : null);
            if (!SignatureInk.TryParseColor(ink, out _, out _, out _)) ink = null;

            var (win, rootStack, closeWinBtn) = TdpDialog.CreateShellWithClose(this,
                editing is null ? "Import Signature" : "Edit Signature", 520);
            closeWinBtn.Click += (_, _2) => win.Close();
            var contentArea = new StackPanel();

            var preview = new System.Windows.Controls.Image
            {
                Stretch = Stretch.Uniform, Height = 190, Margin = new Thickness(6),
                SnapsToDevicePixels = true
            };
            contentArea.Children.Add(new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(4),
                Margin = new Thickness(12, 12, 12, 4), Child = preview
            });

            bool queued = false;
            void Refresh()
            {
                if (queued) return;
                queued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                {
                    queued = false;
                    // The feather radius is a fraction of the image, so the preview matches the saved result.
                    var px = SignatureInk.Process(small, sw, sh, threshold, feather, ink);
                    preview.Source = ToBitmap(px, sw, sh);
                }));
            }

            contentArea.Children.Add(BuildSliderRow("Threshold", 0, 255, threshold, 1,
                v => v < 1 ? "Off" : ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture),
                v => { threshold = (int)v; Refresh(); }));
            contentArea.Children.Add(BuildSliderRow("Feather", 0, 10, feather, 1,
                v => v < 1 ? "Off" : ((int)v).ToString(System.Globalization.CultureInfo.InvariantCulture),
                v => { feather = (int)v; Refresh(); }));
            contentArea.Children.Add(BuildInkRow(ink, true, hex => { ink = hex; Refresh(); }));
            contentArea.Children.Add(new TextBlock
            {
                Text = "Threshold turns the paper transparent and keeps the ink. Feather softens the edge.",
                Foreground = BrushResource("TextSecondary"), FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 4)
            });

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };
            var cancelBtn = EditorButton("Cancel", false);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.IsCancel = true;
            cancelBtn.Click += (_, _2) => win.Close();
            var saveBtn = EditorButton("Save Signature", true);
            saveBtn.Click += (_, _2) =>
            {
                try
                {
                    var px = SignatureInk.Process(full, fw, fh, threshold, feather, ink);
                    int ow = fw, oh = fh;
                    // With the paper gone, trim to the ink so the signature places at its own size.
                    if (threshold > 0 && SignatureInk.InkBounds(px, fw, fh) is { } box)
                        px = SignatureInk.Crop(px, fw, fh, box, Math.Max(2, Math.Max(box.W, box.H) / 100), out ow, out oh);
                    else if (threshold > 0)
                    {
                        TdpDialog.Show(this, "Nothing is left at this threshold. Raise it to keep more of the ink.", "TDPdf",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var saved = editing ?? new SavedSignature { Name = string.IsNullOrWhiteSpace(name) ? "Signature" : name };
                    saved.ImageData = Convert.ToBase64String(SigEncodePng(ToBitmap(px, ow, oh)));
                    saved.CanvasWidth = ow * sizeFactor;
                    saved.CanvasHeight = oh * sizeFactor;
                    saved.InkColor = ink;
                    saved.Threshold = threshold > 0 ? threshold : null;
                    saved.Feather = feather > 0 ? feather : null;
                    saved.Strokes.Clear();
                    // Keep the picture as chosen so editing again starts from it. A photo is stored as a JPEG
                    // (a PNG of one is several times the size); anything with transparency stays a PNG.
                    // An older signature without one keeps what it had, which is the original picture.
                    if (saved.SourceImageData is null)
                    {
                        var srcBmp = ToBitmap(full, fw, fh);
                        saved.SourceImageData = Convert.ToBase64String(opaque
                            ? SigEncodeJpeg(new FormatConvertedBitmap(srcBmp, PixelFormats.Bgr24, null, 0))
                            : SigEncodePng(srcBmp));
                    }
                    FinishSavedSignature(saved, editing is not null, "Image loaded. Click on the page to place it");
                    win.Close();
                    if (editing is null) ShowSignaturePopup();   // show the new entry
                }
                catch (Exception ex)
                {
                    TdpDialog.Show(this, $"Failed to save the signature:\n{ex.Message}", "TDPdf",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            Refresh();
            win.ShowDialog();
        }

        // ------------------------------------------------------------------
        // Export as a PNG for the signing page's Upload tab
        // ------------------------------------------------------------------

        /// <summary>
        /// File ▸ Electronic Signature ▸ Export Signature Picture. Nothing is sent anywhere: the
        /// picture is written to a file the user then chooses on the signing page's Upload tab.
        /// </summary>
        private void ESignExportSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_savedSignatures.Count == 0)
            {
                TdpDialog.Show(this, "There are no saved signature pictures yet. Create or import one from the signature picker, then export it here.",
                    "Export Signature Picture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var sig = _savedSignatures.Count == 1 ? _savedSignatures[0] : ChooseSignatureToExport();
            if (sig is null) return;
            ExportSignaturePng(sig);
        }

        private SavedSignature? ChooseSignatureToExport()
        {
            var (win, rootStack, closeBtn) = TdpDialog.CreateShellWithClose(this, "Export Signature Picture", 380);
            closeBtn.Click += (_, _2) => win.Close();
            SavedSignature? chosen = null;
            var list = new StackPanel { Margin = new Thickness(12) };
            list.Children.Add(EditorLabel("Choose the signature to export as a PNG.", new Thickness(0, 0, 0, 6)));
            var scroll = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var items = new StackPanel();
            foreach (var sig in _savedSignatures)
            {
                var s = sig;
                UIElement thumb;
                if (s.ImageData is not null)
                {
                    try
                    {
                        var px = DecodeBgra(Convert.FromBase64String(s.ImageData), 400, out int w, out int h);
                        thumb = new System.Windows.Controls.Image { Source = ToBitmap(px, w, h), Stretch = Stretch.Uniform, Width = 210, Height = 50, IsHitTestVisible = false };
                    }
                    catch { thumb = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                }
                else
                {
                    var c = new Canvas { Width = 210, Height = 50, IsHitTestVisible = false };
                    RenderSignaturePreview(c, s, 210, 50);
                    thumb = c;
                }
                var btn = new Button
                {
                    Content = new Border { Background = Brushes.White, Padding = new Thickness(4), Child = thumb },
                    Style = (Style)FindResource("DarkButton"),
                    Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(2),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                System.Windows.Automation.AutomationProperties.SetName(btn, string.IsNullOrWhiteSpace(s.Name) ? "Saved signature" : s.Name);
                btn.Click += (_, _2) => { chosen = s; win.Close(); };
                items.Children.Add(btn);
            }
            scroll.Content = items;
            list.Children.Add(scroll);
            rootStack.Children.Add(list);
            win.ShowDialog();
            return chosen;
        }

        /// <summary>The signature as a transparent PNG trimmed to its ink, kept under the 5 MB the signing page accepts. Null when it has no ink.</summary>
        internal static byte[]? BuildSignaturePng(SavedSignature sig)
        {
            const int limit = 4_500_000;
            byte[] px; int w, h;
            if (sig.ImageData is not null)
                px = DecodeBgra(Convert.FromBase64String(sig.ImageData), 2000, out w, out h);
            else
            {
                var strokes = sig.Strokes
                    .Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X, p.Y)).ToList())
                    .ToList();
                var (r, g, b) = SignatureInk.ResolveColor(sig.InkColor);
                px = SignatureInk.RasterizeStrokes(strokes, SigCanvasW(sig), SigCanvasH(sig),
                    SignatureInk.ResolvePenWidth(sig.PenWidth), 3, r, g, b, out w, out h);
            }
            if (SignatureInk.InkBounds(px, w, h) is not { } box) return null;
            px = SignatureInk.Crop(px, w, h, box, Math.Max(4, Math.Max(box.W, box.H) / 50), out w, out h);

            BitmapSource bs = ToBitmap(px, w, h);
            byte[] png = SigEncodePng(bs);
            // Rare: a very detailed picture. Shrink until it fits.
            while (png.Length > limit && Math.Max(w, h) > 200)
            {
                bs = new TransformedBitmap(bs, new ScaleTransform(0.7, 0.7));
                w = bs.PixelWidth; h = bs.PixelHeight;
                png = SigEncodePng(bs);
            }
            return png;
        }

        private void ExportSignaturePng(SavedSignature sig)
        {
            byte[]? png;
            try { png = BuildSignaturePng(sig); }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"This signature picture could not be exported:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (png is null)
            {
                TdpDialog.Show(this, "This signature picture has no ink to export.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string baseName = string.Concat((string.IsNullOrWhiteSpace(sig.Name) ? "Signature" : sig.Name)
                .Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var dlg = new SaveFileDialog
            {
                Filter = "PNG image|*.png",
                Title = "Export Signature Picture",
                FileName = baseName + ".png",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                AtomicFileWriter.Write(staging => File.WriteAllBytes(staging, png), dlg.FileName, CancellationToken.None);
                SetStatus($"Signature picture saved to {dlg.FileName}");
                TdpDialog.Show(this,
                    "The signature picture was saved. On the signing page, choose the Upload tab and pick this file. Nothing was sent anywhere.",
                    "Export Signature Picture", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"The file could not be saved:\n{ex.Message}", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
