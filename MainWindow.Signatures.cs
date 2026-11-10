// MainWindow — signatures.
// Drawing, importing, saving and placing signatures, plus the signatures.json store.
// Extracted verbatim from MainWindow.xaml.cs.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using TDPdf.Diagnostics;
using TDPdf.Services;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // ============================================================
        // Signatures
        // ============================================================

        // A signatures.json that could not be read. It has been copied to _signatureDamagedBackup
        // (null if even that failed); until the user adds a signature nothing is written over it.
        private bool _signatureFileDamaged;
        private string? _signatureDamagedBackup;
        private bool _signatureDamageAnnounced;

        private void LoadSignatures()
        {
            try
            {
                // One-shot migration from the legacy beside-EXE location.
                if (!File.Exists(SignatureFile) && File.Exists(LegacySignatureFile))
                {
                    try
                    {
                        Directory.CreateDirectory(SignatureDir);
                        File.Copy(LegacySignatureFile, SignatureFile, overwrite: false);
                    }
                    catch { /* best effort */ }
                }

                if (File.Exists(SignatureFile))
                {
                    var json = File.ReadAllText(SignatureFile);
                    _savedSignatures = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
                }
            }
            catch
            {
                _savedSignatures = [];
                if (File.Exists(SignatureFile))
                {
                    _signatureFileDamaged = true;
                    _signatureDamagedBackup = SignatureStoreFile.TryBackUpDamaged(SignatureFile, DateTime.Now);
                }
            }
        }

        /// <summary>
        /// Says once, when the signature picker first opens, that the saved signatures could not be
        /// read and where the damaged file went. Deferred so the picker draws first.
        /// </summary>
        private void AnnounceDamagedSignatureFile()
        {
            if (!_signatureFileDamaged || _signatureDamageAnnounced) return;
            _signatureDamageAnnounced = true;
            string msg = _signatureDamagedBackup is not null
                ? $"Your saved signatures could not be read, so none are shown. The unreadable file was kept as:\n\n{_signatureDamagedBackup}\n\nSignatures you add now are saved normally."
                : "Your saved signatures could not be read, and a backup copy of the unreadable file could not be made. " +
                  "Signatures you add now will work for this session but will not be saved, so the original file is not overwritten.";
            Dispatcher.BeginInvoke(new Action(() =>
                TdpDialog.Show(this, msg, "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void PersistSignatures()
        {
            // An unreadable file stays untouched until there is something to put in its place and a
            // backup of it exists; writing an empty list over it would destroy what might be recoverable.
            if (_signatureFileDamaged && (_savedSignatures.Count == 0 || _signatureDamagedBackup is null)) return;
            try
            {
                var json = JsonSerializer.Serialize(_savedSignatures, new JsonSerializerOptions { WriteIndented = true });
                SignatureStoreFile.WriteAtomic(SignatureFile, json);
                _signatureFileDamaged = false;   // readable again; the backup keeps the old bytes
            }
            catch { /* best effort */ }
        }

        private void ShowSignaturePopup()
        {
            HideSignaturePopup();
            AnnounceDamagedSignatureFile();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Signature Pictures",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 6)
            });

            // Saved signatures
            if (_savedSignatures.Count > 0)
            {
                var scroll = new ScrollViewer
                {
                    MaxHeight = 260,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                var listPanel = new StackPanel();

                foreach (var sig in _savedSignatures)
                {
                    var sigCopy = sig; // capture for lambda
                    var item = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = BrushResource("BorderDim"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(4, 2, 4, 2),
                        Padding = new Thickness(4),
                        Cursor = Cursors.Hand,
                        Height = 60,
                        Width = 220,
                        Focusable = true   // Tab / arrows reach it; Enter or Space picks it
                    };
                    AutomationProperties.SetName(item, string.IsNullOrWhiteSpace(sigCopy.Name) ? "Saved signature" : sigCopy.Name);
                    AutomationProperties.SetHelpText(item, "Press Enter or Space to pick this signature, then click the page to place it.");

                    // Render mini signature preview
                    if (sigCopy.ImageData is not null)
                    {
                        try
                        {
                            var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                            var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                            using (var imageStream = new System.IO.MemoryStream(imgBytes))
                            {
                                bmpImg.BeginInit();
                                bmpImg.StreamSource = imageStream;
                                bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bmpImg.EndInit();
                            }
                            if (bmpImg.CanFreeze) bmpImg.Freeze();
                            item.Child = new System.Windows.Controls.Image
                            {
                                Source = bmpImg,
                                Width = 210, Height = 50,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                        }
                        catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                    }
                    else
                    {
                        var canvas = new Canvas
                        {
                            Width = 210, Height = 50,
                            Background = Brushes.Transparent,
                            IsHitTestVisible = false
                        };
                        RenderSignaturePreview(canvas, sigCopy, 210, 50);
                        item.Child = canvas;
                    }

                    item.MouseLeftButtonDown += (s, e) => PickSignature(sigCopy);
                    item.KeyDown += (s, e) =>
                    {
                        if (e.Key is Key.Enter or Key.Space)
                        {
                            e.Handled = true;
                            PickSignature(sigCopy);
                        }
                    };
                    item.GotKeyboardFocus += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                    item.LostKeyboardFocus += (s, e) =>
                        ((Border)s!).BorderBrush = BrushResource("BorderDim");
                    item.MouseEnter += (s, e) =>
                        ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                    item.MouseLeave += (s, e) =>
                        ((Border)s!).BorderBrush = BrushResource("BorderDim");

                    // Wrap in grid with delete button
                    var itemGrid = new Grid();
                    itemGrid.Children.Add(item);

                    var delBtn = new Button
                    {
                        Content = "\ue711",
                        FontSize = 10,
                        Width = 18, Height = 18,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 0, 2, 0),
                        Background = BrushResource("BgHover"),
                        Foreground = (SolidColorBrush)FindResource("DangerRed"),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("ToolbarButton")
                    };
                    delBtn.Click += (s, e) =>
                    {
                        _savedSignatures.Remove(sigCopy);
                        PersistSignatures();
                        ShowSignaturePopup(); // refresh
                    };
                    itemGrid.Children.Add(delBtn);
                    listPanel.Children.Add(itemGrid);
                }
                scroll.Content = listPanel;
                stack.Children.Add(scroll);
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "No saved signature pictures",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 4, 4, 8),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            // Separator
            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(4, 4, 4, 4)
            });

            // Create Signature button
            var createBtn = new Button
            {
                Content = "Create Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = (SolidColorBrush)FindResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            createBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenSignatureCreator();
            };
            stack.Children.Add(createBtn);

            // Type Signature button — renders typed text in a handwriting font.
            var typeBtn = new Button
            {
                Content = "Type Signature",
                Style = (Style)FindResource("DarkButton"),
                Background = BrushResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            typeBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                OpenTypedSignatureCreator();
            };
            stack.Children.Add(typeBtn);

            // Import image button
            var importBtn = new Button
            {
                Content = "Import Image",
                Style = (Style)FindResource("DarkButton"),
                Background = BrushResource("AccentGreenDim"),
                Foreground = (SolidColorBrush)FindResource("AccentGreen"),
                BorderBrush = (SolidColorBrush)FindResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(4, 2, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            importBtn.Click += (s, e) =>
            {
                HideSignaturePopup();
                ImportImageSignature();
            };
            stack.Children.Add(importBtn);

            _signaturePopup = new Border
            {
                Background = BrushResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 80, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 12, Opacity = 0.5, ShadowDepth = 4
                }
            };

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
            }
        }

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_signaturePopup);
                _signaturePopup = null;
            }
        }

        /// <summary>Fallback drawn-signature canvas, mirroring <see cref="SavedSignature"/>'s initializers.</summary>
        private const double DefaultSigCanvasW = 400;
        private const double DefaultSigCanvasH = 150;

        // Upstream v1.7.1 (#181): signatures.json is plain JSON on disk and an explicit 0 in it
        // OVERRIDES SavedSignature's property initializers, so a legacy or hand-edited entry can carry
        // a zero (or non-finite) canvas size. Everything downstream divides by it — the preview scale,
        // the placed annotation's SourceWidth, the resize drag — and ±∞/NaN then gets persisted onto
        // the annotation, after which every later render crashes WPF. Read the dimensions through
        // these so the standard canvas stands in wherever the stored value is unusable.
        private static double SigCanvasW(SavedSignature sig)
            => IsFinitePositive(sig.CanvasWidth) ? sig.CanvasWidth : DefaultSigCanvasW;

        private static double SigCanvasH(SavedSignature sig)
            => IsFinitePositive(sig.CanvasHeight) ? sig.CanvasHeight : DefaultSigCanvasH;

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double sigW = SigCanvasW(sig), sigH = SigCanvasH(sig);
            double scaleX = targetW / sigW;
            double scaleY = targetH / sigH;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sigW * scale) / 2;
            double offsetY = (targetH - sigH * scale) / 2;

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void OpenSignatureCreator()
        {
            var win = new Window
            {
                Title = "Create Signature",
                Width = 460, Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };

            // Outer chrome
            var outerChrome = new Border
            {
                Background      = BrushResource("BgDark"),
                BorderBrush     = BrushResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                Background   = BrushResource("BgPanel"),
                Padding      = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text       = "Create Signature",
                Foreground = BrushResource("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content         = "",
                FontFamily      = new FontFamily("Segoe MDL2 Assets"),
                FontSize        = 10,
                Width           = 28, Height = 28,
                Background      = System.Windows.Media.Brushes.Transparent,
                Foreground      = BrushResource("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor          = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = BrushResource("DangerRed");
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = BrushResource("TextSecondary");
            closeWinBtn.Click += (_, _2) => win.Close();
            AutomationProperties.SetName(closeWinBtn, "Close");
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

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

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
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
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = new Button
            {
                Content = "Clear",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgHover"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = BrushResource("AccentGreenDim"),
                Foreground = BrushResource("AccentGreen"),
                BorderBrush = BrushResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    TdpDialog.Show(this, "Draw a signature first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"Signature {_savedSignatures.Count + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _savedSignatures.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = GrabbingCursor;   // carrying a signature until it is dropped
                SetStatus("Signature picture saved. Click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.ShowDialog();
        }

        // "Type a signature": the user types their name, picks a handwriting font and
        // ink color, and we rasterize it to a transparent PNG. That PNG is stored as a
        // SavedSignature.ImageData, so it flows through the exact same persistence,
        // placement, on-canvas render, and PDF-bake paths as an imported-image signature.
        private void OpenTypedSignatureCreator()
        {
            // Curated handwriting fonts that ship with Windows; keep only those installed.
            var preferred = new[] { "Segoe Script", "Segoe Print", "Gabriola", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Monotype Corsiva" };
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fam in Fonts.SystemFontFamilies)
            {
                if (!string.IsNullOrEmpty(fam.Source)) installed.Add(fam.Source);
                foreach (var n in fam.FamilyNames.Values) installed.Add(n);
            }
            var available = preferred.Where(installed.Contains).ToList();
            if (available.Count == 0) available.Add("Segoe Script"); // best-effort; WPF substitutes if absent

            string selectedFont = available[0];
            var blackInk = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
            var blueInk = new SolidColorBrush(Color.FromRgb(0x12, 0x2A, 0x88));
            blackInk.Freeze(); blueInk.Freeze();
            SolidColorBrush inkBrush = blackInk;

            var win = new Window
            {
                Title = "Type Signature",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            var outerChrome = new Border
            {
                Background = BrushResource("BgDark"),
                BorderBrush = BrushResource("AccentGreenDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var rootStack = new StackPanel();

            // Title bar (draggable)
            var titleBar = new Border
            {
                Background = BrushResource("BgPanel"),
                Padding = new Thickness(14, 8, 8, 8),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = "Type Signature",
                Foreground = BrushResource("AccentGreen"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Width = 28, Height = 28,
                Background = Brushes.Transparent,
                Foreground = BrushResource("TextSecondary"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeWinBtn.MouseEnter += (_, _2) => closeWinBtn.Foreground = BrushResource("DangerRed");
            closeWinBtn.MouseLeave += (_, _2) => closeWinBtn.Foreground = BrushResource("TextSecondary");
            closeWinBtn.Click += (_, _2) => win.Close();
            AutomationProperties.SetName(closeWinBtn, "Close");
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            contentArea.Children.Add(new TextBlock
            {
                Text = "Type your name, then choose a style:",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(12, 12, 12, 4)
            });

            var nameBox = new TextBox
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 16,
                Background = BrushResource("BgPanel"),
                Foreground = BrushResource("TextPrimary"),
                CaretBrush = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(12, 0, 12, 8)
            };
            TdpDialog.UseThemedTextMenu(nameBox);
            contentArea.Children.Add(nameBox);

            // Live preview
            var previewBorder = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(4),
                Height = 110,
                Margin = new Thickness(12, 0, 12, 8)
            };
            var previewBox = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(16, 8, 16, 8) };
            var previewText = new TextBlock
            {
                Text = "Your name",
                FontFamily = new FontFamily(selectedFont),
                FontSize = 64,
                Foreground = BrushResource("TextSecondary")
            };
            previewBox.Child = previewText;
            previewBorder.Child = previewBox;
            contentArea.Children.Add(previewBorder);

            // Style (font) picker
            contentArea.Children.Add(new TextBlock
            {
                Text = "Style",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Margin = new Thickness(12, 0, 12, 2)
            });
            var fontPanel = new WrapPanel { Margin = new Thickness(8, 0, 8, 8) };
            var fontButtons = new List<(Button btn, TextBlock label, string font)>();
            foreach (var font in available)
            {
                var lbl = new TextBlock
                {
                    Text = "Abc",
                    FontFamily = new FontFamily(font),
                    FontSize = 22,
                    Foreground = Brushes.Black
                };
                var b = new Button
                {
                    Content = lbl,
                    Style = (Style)FindResource("DarkButton"),
                    Background = Brushes.White,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(4),
                    Cursor = Cursors.Hand,
                    ToolTip = font
                };
                fontButtons.Add((b, lbl, font));
                fontPanel.Children.Add(b);
            }
            contentArea.Children.Add(fontPanel);

            // Ink color picker
            var inkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };
            inkRow.Children.Add(new TextBlock
            {
                Text = "Ink",
                Foreground = BrushResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            });
            var inkButtons = new List<(Button btn, SolidColorBrush brush)>();
            Button MakeInkButton(string text, SolidColorBrush brush)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new Border
                {
                    Width = 14, Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = brush,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = BrushResource("TextPrimary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var b = new Button
                {
                    Content = sp,
                    Style = (Style)FindResource("DarkButton"),
                    Background = BrushResource("BgHover"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(4, 0, 0, 0),
                    Cursor = Cursors.Hand
                };
                return b;
            }
            var blackBtn = MakeInkButton("Black", blackInk);
            var blueBtn = MakeInkButton("Blue", blueInk);
            inkButtons.Add((blackBtn, blackInk));
            inkButtons.Add((blueBtn, blueInk));
            inkRow.Children.Add(blackBtn);
            inkRow.Children.Add(blueBtn);
            contentArea.Children.Add(inkRow);

            // --- shared refresh helpers (closures capture selectedFont / inkBrush) ---
            void RefreshPreview()
            {
                var name = nameBox.Text ?? "";
                bool empty = string.IsNullOrWhiteSpace(name);
                previewText.Text = empty ? "Your name" : name;
                previewText.FontFamily = new FontFamily(selectedFont);
                previewText.Foreground = empty ? BrushResource("TextSecondary") : inkBrush;
                foreach (var (b, lbl, font) in fontButtons)
                {
                    lbl.Text = empty ? "Abc" : name;
                    bool sel = font == selectedFont;
                    b.BorderBrush = sel ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
                foreach (var (b, brush) in inkButtons)
                {
                    bool sel = ReferenceEquals(brush, inkBrush);
                    b.BorderBrush = sel ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                    b.BorderThickness = new Thickness(sel ? 2 : 1);
                }
            }

            foreach (var (b, _, font) in fontButtons)
                b.Click += (_, _2) => { selectedFont = font; RefreshPreview(); };
            blackBtn.Click += (_, _2) => { inkBrush = blackInk; RefreshPreview(); };
            blueBtn.Click += (_, _2) => { inkBrush = blueInk; RefreshPreview(); };
            nameBox.TextChanged += (_, _2) => RefreshPreview();
            RefreshPreview();

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgHover"),
                Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas")
            };
            cancelBtn.Click += (_, _2) => win.Close();
            cancelBtn.IsCancel = true;   // Esc cancels (Enter is handled on nameBox below)
            var saveBtn = new Button
            {
                Content = "Save Signature",
                Style = (Style)FindResource("DarkButton"),
                Padding = new Thickness(16, 6, 16, 6),
                Background = BrushResource("AccentGreenDim"),
                Foreground = BrushResource("AccentGreen"),
                BorderBrush = BrushResource("AccentGreen"),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.SemiBold
            };

            void DoSave()
            {
                var text = (nameBox.Text ?? "").Trim();
                if (text.Length == 0)
                {
                    TdpDialog.Show(this, "Type your name first.", "TDPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var (base64, w, h) = RenderTypedSignaturePng(text, new FontFamily(selectedFont), inkBrush);
                var saved = new SavedSignature
                {
                    Name = text.Length > 40 ? text.Substring(0, 40) : text,
                    CanvasWidth = w,
                    CanvasHeight = h,
                    ImageData = base64
                };
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = GrabbingCursor;   // carrying a signature until it is dropped
                SetStatus("Signature picture saved. Click on the page to place it");
                win.Close();
            }
            saveBtn.Click += (_, _2) => DoSave();
            nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; DoSave(); } };

            btnPanel.Children.Add(cancelBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);
            outerChrome.Child = rootStack;
            win.Content = outerChrome;
            win.Loaded += (_, _2) => nameBox.Focus();
            win.ShowDialog();
        }

        /// <summary>
        /// Rasterizes typed text in the given handwriting font and ink color to a
        /// transparent PNG, rendered at 2× for crisp placement/print. Returns the base-64
        /// PNG plus its logical width/height (used as the signature's source dimensions
        /// so its aspect ratio is preserved when placed and resized).
        /// </summary>
        private static (string base64, double width, double height) RenderTypedSignaturePng(string text, FontFamily fontFamily, Brush inkBrush)
        {
            const double fontSize = 96;
            const double pad = 24;
            const double scale = 2.0;

            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                inkBrush,
                1.0);

            double w = Math.Max(ft.WidthIncludingTrailingWhitespace, 1) + pad * 2;
            double h = Math.Max(ft.Height, 1) + pad * 2;

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawText(ft, new Point(pad, pad));

            var rtb = new RenderTargetBitmap(
                (int)Math.Ceiling(w * scale),
                (int)Math.Ceiling(h * scale),
                96 * scale, 96 * scale,
                PixelFormats.Pbgra32);
            rtb.Render(dv);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (Convert.ToBase64String(ms.ToArray()), w, h);
        }

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
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (bmp.CanFreeze) bmp.Freeze();
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _savedSignatures.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = GrabbingCursor;   // carrying a signature until it is dropped
                SetStatus("Image loaded. Click on the page to place it");
                ShowSignaturePopup(); // refresh to show the new entry
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Failed to import image:\n{ex.Message}", "TDPdf",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------
        // Placement ghost (upstream #468): a faint copy of the pending signature follows the pointer
        // over the page, the wheel resizes it, and a click drops it at that size.
        // ------------------------------------------------------------------

        private const double SigPlaceMinScale = 0.15, SigPlaceMaxScale = 2.0;
        private double _sigPlaceScale = 0.5;
        private Canvas? _sigGhost;
        private bool _sigGhostLeaveHooked;

        private void PickSignature(SavedSignature sig)
        {
            _pendingSignature = sig;
            HideSignaturePopup();
            _annotationCanvas.Cursor = GrabbingCursor;   // carrying a signature until it is dropped
            SetStatus("Click on the page to place your signature picture. Mouse wheel resizes it, Esc cancels");
        }

        /// <summary>Drops a pending signature without placing it.</summary>
        private void CancelSignaturePlacement()
        {
            _pendingSignature = null;
            RemoveSignatureGhost();
            _annotationCanvas.Cursor = Cursors.Hand;
            SetStatus("Signature placement cancelled");
        }

        private void RemoveSignatureGhost()
        {
            if (_sigGhost is null) return;
            _annotationCanvas.Children.Remove(_sigGhost);
            _sigGhost = null;
        }

        /// <summary>Builds (or rebuilds at a new scale) the ghost for the pending signature.</summary>
        private Canvas? BuildSignatureGhost(SavedSignature sig, double scale)
        {
            double w = SigCanvasW(sig) * scale, h = SigCanvasH(sig) * scale;
            if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return null;
            var host = new Canvas
            {
                Width = w, Height = h,
                Opacity = 0.45,
                IsHitTestVisible = false,
                Tag = "SignatureGhost"
            };
            if (sig.ImageData is not null)
            {
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(Convert.FromBase64String(sig.ImageData)))
                    {
                        bmp.BeginInit();
                        bmp.StreamSource = ms;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                    }
                    if (bmp.CanFreeze) bmp.Freeze();
                    host.Children.Add(new System.Windows.Controls.Image
                    {
                        Source = bmp, Width = w, Height = h,
                        Stretch = Stretch.Uniform, IsHitTestVisible = false
                    });
                }
                catch { return null; }
            }
            else
            {
                foreach (var stroke in sig.Strokes)
                {
                    if (stroke.Count < 2) continue;
                    var poly = new Polyline
                    {
                        Stroke = Brushes.Black,
                        StrokeThickness = 2 * scale,   // as the placed signature draws it
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false
                    };
                    foreach (var pt in stroke) poly.Points.Add(new Point(pt.X * scale, pt.Y * scale));
                    host.Children.Add(poly);
                }
            }
            return host;
        }

        /// <summary>Moves the ghost to the pointer, creating it when needed. Called from Canvas_MouseMove.</summary>
        private void UpdateSignatureGhost(Point pos)
        {
            if (_pendingSignature is null || _currentTool != EditTool.Signature) { RemoveSignatureGhost(); return; }
            if (!_sigGhostLeaveHooked)
            {
                _sigGhostLeaveHooked = true;
                _annotationCanvas.MouseLeave += (_, _) => RemoveSignatureGhost();
            }
            // A re-render clears the canvas, taking the ghost with it; put it back.
            if (_sigGhost is not null && !_annotationCanvas.Children.Contains(_sigGhost)) _sigGhost = null;
            if (_sigGhost is null)
            {
                _sigGhost = BuildSignatureGhost(_pendingSignature, _sigPlaceScale);
                if (_sigGhost is null) return;
                Panel.SetZIndex(_sigGhost, 1000);
                _annotationCanvas.Children.Add(_sigGhost);
            }
            Canvas.SetLeft(_sigGhost, pos.X);
            Canvas.SetTop(_sigGhost, pos.Y);
        }

        /// <summary>
        /// Wheel over the page while carrying a signature resizes the ghost. Returns true when the
        /// wheel was consumed; Ctrl+wheel is left alone so it still zooms the document.
        /// </summary>
        private bool TryResizeSignatureGhost(MouseWheelEventArgs e)
        {
            if (_pendingSignature is null || _currentTool != EditTool.Signature) return false;
            if (Keyboard.Modifiers != ModifierKeys.None || !_annotationCanvas.IsMouseOver) return false;
            double next = Math.Clamp(_sigPlaceScale * Math.Pow(1.0015, e.Delta), SigPlaceMinScale, SigPlaceMaxScale);
            if (next != _sigPlaceScale)
            {
                _sigPlaceScale = next;
                var at = e.GetPosition(_annotationCanvas);
                RemoveSignatureGhost();       // rebuilt at the new size by the update below
                UpdateSignatureGhost(at);
                SetStatus($"Signature size {(int)Math.Round(_sigPlaceScale * 100)}%. Click to place, Esc to cancel");
            }
            return true;
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            Telemetry.TrackEvent("Annotation.PlaceStarted",
                new Dictionary<string, string> { ["Type"] = "Signature" });
            var sig = _pendingSignature;
            double scale = Math.Clamp(_sigPlaceScale, SigPlaceMinScale, SigPlaceMaxScale);
            // Spent before anything below can re-enter (a focused form field losing focus re-renders
            // the page): one click stamps exactly one signature.
            _pendingSignature = null;
            RemoveSignatureGhost();

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                // #181: never copy an unusable stored canvas size onto the annotation — SourceWidth
                // is a divisor in the resize drag, and a 0 there produces an infinite Scale that is
                // then saved on the annotation and crashes every subsequent render.
                SourceWidth = SigCanvasW(sig),
                SourceHeight = SigCanvasH(sig),
                ImageData = sig.ImageData
            };

            // Drawn signature — convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([..stroke.Select(p => new Point(p.X, p.Y))]);
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);
            // Auto-select so the user can immediately drag/resize/delete the new signature
            // without having to switch to Select first (Reddit/KillerPDF feedback).
            double sigW = annot.SourceWidth * annot.Scale;
            double sigH = annot.SourceHeight * annot.Scale;
            SetTool(EditTool.Select);
            SelectAnnotation(annot, new Rect(annot.Position.X, annot.Position.Y, sigW, sigH));
            SetStatus("Signature picture placed. Drag the corner handle to resize, or Delete to remove");
            Telemetry.TrackEvent("Annotation.PlaceCompleted",
                new Dictionary<string, string> { ["Type"] = "Signature" });
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Insert Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default scale: fit within 250 canvas pixels on the longest axis
                const double MaxCanvasDim = 250;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus("Image placed. Drag the corner handle to resize, or switch to Select to move/delete");
            }
            catch (Exception ex)
            {
                TdpDialog.Show(this, $"Could not load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
