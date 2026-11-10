// MainWindow — tool settings bars (draw, crop, text, shape) and colour palette.
// Moved verbatim from MainWindow.xaml.cs.

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
        // Draw/Highlight settings bar
        // ============================================================

        private static readonly Color[] SwatchColors =
        [
            Colors.Red, Colors.SaddleBrown, Colors.Orange, Colors.Gold,
            Colors.LimeGreen, Colors.DodgerBlue, Colors.MediumPurple,
            Colors.DeepPink, Colors.White, Colors.Black
        ];

        // ── Custom color picker plumbing (shared by every annotation swatch row) ──

        private const int MaxRecentColors = 8;

        // The recently picked custom colors, most-recent first, parsed from the persisted setting.
        private static List<Color> LoadCustomColors()
        {
            var raw = TDPdf.Properties.Settings.Default.CustomColors;
            var list = new List<Color>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (TdpColorPicker.TryParseHex(part.Trim(), out Color c))
                    list.Add(c);
            return list;
        }

        // Record a freshly chosen color at the head of the recent list (RGB-deduped, capped) and persist.
        private static void RememberCustomColor(Color c)
        {
            var opaque = Color.FromRgb(c.R, c.G, c.B);
            var list = LoadCustomColors();
            list.RemoveAll(x => x.R == opaque.R && x.G == opaque.G && x.B == opaque.B);
            list.Insert(0, opaque);
            if (list.Count > MaxRecentColors) list.RemoveRange(MaxRecentColors, list.Count - MaxRecentColors);
            TDPdf.Properties.Settings.Default.CustomColors =
                string.Join(",", list.Select(x => $"#{x.R:X2}{x.G:X2}{x.B:X2}"));
            try { TDPdf.Properties.Settings.Default.Save(); } catch { /* persistence is best-effort */ }
        }

        // Open the themed RGB picker seeded with <paramref name="initial"/>. On OK the opaque pick is
        // returned and pushed onto the recent-colors palette; on Cancel returns false.
        private bool TryPickCustomColor(Color initial, out Color picked)
        {
            if (TdpColorPicker.TryPickColor(this, initial, LoadCustomColors(), out picked))
            {
                RememberCustomColor(picked);
                return true;
            }
            return false;
        }

        // A trailing "custom color" swatch for an annotation settings bar: a rainbow "+" tile that opens
        // the picker (seeded with <paramref name="current"/>) and hands the pick to <paramref name="apply"/>
        // exactly as clicking a fixed swatch would set the row's active color. The seed is read at
        // click time, not build time: the bars restyle in place rather than rebuilding (upstream
        // KillerPDF v1.8.80), so a color captured when the bar was built goes stale on the first pick.
        private Border MakeCustomColorSwatch(Func<Color> current, Action<Color> apply)
        {
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            Color[] stops =
            [
                Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xFF, 0x7F, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
                Color.FromRgb(0x00, 0xC8, 0x00), Color.FromRgb(0x00, 0xC8, 0xFF), Color.FromRgb(0x00, 0x40, 0xFF),
                Color.FromRgb(0x9C, 0x00, 0xFF)
            ];
            for (int i = 0; i < stops.Length; i++)
                rainbow.GradientStops.Add(new GradientStop(stops[i], i / (double)(stops.Length - 1)));

            var plus = new TextBlock
            {
                Text = "", // Segoe MDL2 "Add"
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 0, Opacity = 0.9 }
            };

            var swatch = new Border
            {
                Width = 18, Height = 18,
                Background = rainbow,
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Custom color…",
                Child = plus
            };
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                if (TryPickCustomColor(current(), out var pickedColor))
                    apply(pickedColor);
            };
            return swatch;
        }

        // One fixed color tile for an annotation settings bar's swatch row. Its ring is drawn by
        // MarkActiveSwatch, which the bar calls again after every pick instead of rebuilding itself.
        private Border MakeColorSwatch(Color c, Action<Color> apply)
        {
            var swatch = new Border
            {
                Width = 18, Height = 18,
                Background = TdpDialog.FrozenSolidColorBrush(c),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = c
            };
            swatch.MouseLeftButtonDown += (_, _) => apply(c);
            return swatch;
        }

        // Rings the swatch in <paramref name="row"/> whose RGB matches <paramref name="active"/> (alpha
        // is the opacity slider's business, not the swatch's) and un-rings the rest, in place.
        private void MarkActiveSwatch(IEnumerable<Border> row, Color active)
        {
            foreach (var swatch in row)
            {
                bool on = swatch.Tag is Color c && c.R == active.R && c.G == active.G && c.B == active.B;
                swatch.BorderBrush = on ? BrushResource("AccentGreen") : BrushResource("BorderDim");
                swatch.BorderThickness = new Thickness(on ? 2 : 1);
            }
        }

        private void ShowDrawSettings(EditTool tool)
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            // Restyle a selected ink/highlight, or edit tool defaults when nothing is selected.
            var inkTarget = _styleTarget as InkAnnotation;
            var hlTarget = _styleTarget as HighlightAnnotation;

            // The swatch row is re-ringed in place after a pick rather than the whole bar being
            // rebuilt — a rebuild flashed the bar and dropped whatever control had the mouse.
            var colorSwatches = new List<Border>();
            Color ActiveColor() =>
                inkTarget is not null ? Color.FromRgb(inkTarget.ColorR, inkTarget.ColorG, inkTarget.ColorB) :
                hlTarget is not null ? Color.FromRgb(hlTarget.ColorR, hlTarget.ColorG, hlTarget.ColorB) :
                tool == EditTool.Draw ? _drawColor
                : Color.FromRgb(MarkupToolColor(tool).R, MarkupToolColor(tool).G, MarkupToolColor(tool).B);

            void ApplyColor(Color c)
            {
                if (inkTarget is not null) { inkTarget.SetColor(Color.FromArgb(inkTarget.ColorA, c.R, c.G, c.B)); RestyleLive(inkTarget); }
                else if (hlTarget is not null) { hlTarget.SetColor(Color.FromArgb(hlTarget.ColorA, c.R, c.G, c.B)); RestyleLive(hlTarget); }
                else if (tool == EditTool.Draw) _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                else SetMarkupToolColor(tool, Color.FromArgb(MarkupToolColor(tool).A, c.R, c.G, c.B));
                MarkActiveSwatch(colorSwatches, ActiveColor());
            }
            void ApplyOpacity(byte a)
            {
                if (inkTarget is not null) { inkTarget.ColorA = a; RestyleLive(inkTarget); return; }
                if (hlTarget is not null) { hlTarget.ColorA = a; RestyleLive(hlTarget); return; }
                if (tool == EditTool.Draw) { _drawOpacity = a; _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B); }
                else
                {
                    var mc = MarkupToolColor(tool);
                    SetMarkupToolColor(tool, Color.FromArgb(a, mc.R, mc.G, mc.B));
                }
            }
            void ApplyDrawWidth(double w)
            {
                if (inkTarget is not null) { inkTarget.StrokeWidth = w; RestyleLive(inkTarget); return; }
                _drawWidth = w;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "Color:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches
            foreach (var color in SwatchColors)
            {
                var swatch = MakeColorSwatch(color, ApplyColor);
                colorSwatches.Add(swatch);
                panel.Children.Add(swatch);
            }
            MarkActiveSwatch(colorSwatches, ActiveColor());

            // Custom color: opens the full RGB picker, applied like a fixed swatch (opacity preserved).
            panel.Children.Add(MakeCustomColorSwatch(ActiveColor, ApplyColor));

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Size:",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                });
                double curDrawWidth = inkTarget?.StrokeWidth ?? _drawWidth;
                var sizeSlider = new Slider
                {
                    Minimum = 1, Maximum = 20, Value = curDrawWidth,
                    Width = 80, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1, IsSnapToTickEnabled = true
                };
                var sizeLabel = new TextBlock
                {
                    Text = $"{curDrawWidth:F0}px",
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
                };
                sizeSlider.ValueChanged += (s, e) => { sizeLabel.Text = $"{e.NewValue:F0}px"; ApplyDrawWidth(e.NewValue); };
                panel.Children.Add(sizeSlider);
                panel.Children.Add(sizeLabel);

                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });
            }

            // Opacity slider
            panel.Children.Add(new TextBlock
            {
                Text = "Opacity:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });
            byte currentOpacity = inkTarget?.ColorA ?? hlTarget?.ColorA
                ?? (tool == EditTool.Draw ? _drawOpacity : MarkupToolColor(tool).A);
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 80, VerticalAlignment = VerticalAlignment.Center
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0)
            };
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                ApplyOpacity(a);
            };
            panel.Children.Add(opacitySlider);
            panel.Children.Add(opacityLabel);

            _drawSettingsBar = new Border
            {
                Background = BrushResource("BgDark"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
            }
        }

        private void HideDrawSettings()
        {
            if (_drawSettingsBar is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }
        }

        // ============================================================
        // Crop settings bar
        // ============================================================

        private void ShowCropPopup()
        {
            if (_cropPopup is not null)
            {
                _cropPopup.Visibility = Visibility.Visible;
                UpdateRemoveCropEnabled();
                return;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = "Drag a crop rectangle",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            _cropApplyAllCheck = new CheckBox
            {
                Content = "Apply to all pages",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _cropApplyAllCheck.Checked += (s, e) => UpdateRemoveCropEnabled();
            _cropApplyAllCheck.Unchecked += (s, e) => UpdateRemoveCropEnabled();
            panel.Children.Add(_cropApplyAllCheck);

            var applyBtn = new Button
            {
                Content = "Apply crop",
                Style = (Style)FindResource("ToolbarButtonAccent"),
                ToolTip = "Apply the selected crop rectangle"
            };
            applyBtn.Click += ApplyCrop_Click;
            panel.Children.Add(applyBtn);

            var resetBtn = new Button
            {
                Content = "Reset",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Clear the current crop rectangle"
            };
            resetBtn.Click += (s, e) => ClearCropSelection();
            panel.Children.Add(resetBtn);

            _removeCropBtn = new Button
            {
                Content = "Remove crop",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Remove the existing crop (this page, or every page when \"Apply to all pages\" is checked)"
            };
            _removeCropBtn.Click += (s, e) => RemoveCrop();
            panel.Children.Add(_removeCropBtn);

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Style = (Style)FindResource("ToolbarButton"),
                ToolTip = "Cancel cropping"
            };
            cancelBtn.Click += (s, e) => SetTool(EditTool.Select);
            panel.Children.Add(cancelBtn);

            _cropPopup = new Border
            {
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_cropPopup, 101);
                previewArea.Children.Add(_cropPopup);
            }
            UpdateRemoveCropEnabled();
        }

        /// <summary>The pages the crop popup currently targets: every page when "Apply to all pages" is
        /// checked, otherwise the selected page, the same scope Apply crop uses.</summary>
        private int[] CropTargetPages()
        {
            if (_doc is null) return [];
            if (_cropApplyAllCheck?.IsChecked == true)
                return [.. Enumerable.Range(0, _doc.PageCount)];
            int cur = PageList.SelectedIndex;
            return cur >= 0 && cur < _doc.PageCount ? [cur] : [];
        }

        /// <summary>True when the page's visible box is a real crop, i.e. its /CropBox (own or inherited)
        /// differs from the /MediaBox. Raw dictionary reads only; the PdfPage box getters plant empty boxes.</summary>
        private static bool PageHasCrop(PdfPage page)
        {
            var crop = PdfPageGeometry.ReadInheritedPageBox(page, "/CropBox");
            if (crop is not { Width: > 1, Height: > 1 } c) return false;
            var media = PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox");
            return media is not { } m || Math.Abs(c.X - m.X) > 0.01 || Math.Abs(c.Y - m.Y) > 0.01
                || Math.Abs(c.Width - m.Width) > 0.01 || Math.Abs(c.Height - m.Height) > 0.01;
        }

        private void UpdateRemoveCropEnabled()
        {
            if (_removeCropBtn is null) return;
            var doc = _doc;
            _removeCropBtn.IsEnabled = doc is not null && CropTargetPages().Any(i => PageHasCrop(doc.Pages[i]));
        }

        /// <summary>Removes the crop from the targeted pages by deleting /CropBox (never touching /MediaBox),
        /// through the same document-undo path as other page-geometry edits.</summary>
        private void RemoveCrop()
        {
            if (_doc is null || _currentFile is null) return;
            var targets = CropTargetPages().Where(i => PageHasCrop(_doc.Pages[i])).ToArray();
            if (targets.Length == 0) { UpdateRemoveCropEnabled(); return; }

            RunPageOperation("Remove crop", () =>
            {
                var doc = _doc!;
                PushDocUndo();
                foreach (int pi in targets)
                {
                    var page = doc.Pages[pi];
                    page.Elements.Remove("/CropBox");
                    // A crop inherited from a /Pages ancestor survives removing the page's own entry;
                    // pin the page to its media box so the crop really is gone.
                    if (PdfPageGeometry.ReadInheritedPageBox(page, "/CropBox") is not null &&
                        PdfPageGeometry.ReadInheritedPageBox(page, "/MediaBox") is { } m)
                    {
                        var arr = new PdfArray();
                        arr.Elements.Add(new PdfReal(m.X));
                        arr.Elements.Add(new PdfReal(m.Y));
                        arr.Elements.Add(new PdfReal(m.Right));
                        arr.Elements.Add(new PdfReal(m.Top));
                        page.Elements["/CropBox"] = arr;
                    }
                }
                SaveTempAndReload();
                SetStatus($"Removed crop from {targets.Length} page{(targets.Length == 1 ? "" : "s")}");
            });
            UpdateRemoveCropEnabled();
        }

        private void HideCropPopup()
        {
            if (_cropPopup is not null)
                _cropPopup.Visibility = Visibility.Collapsed;
        }

        // ============================================================
        // Text tool settings bar
        // ============================================================

        private static readonly double[] TextFontSizes = [8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72];

        // Curated, not "every installed font": each of these ships with Windows and is already a
        // known-good entry in PdfFontStyle's PS-name map / FontCoverage's fallback chains, so picking
        // one here can never produce a family that fails to embed or that a colleague's machine lacks
        // when they later reopen the same file. FontCoverage.PickFamily still silently upgrades the
        // choice for a script it can't cover (CJK, Arabic, etc.) — this list is what a Latin-script
        // annotation actually gets held to, not a hard ceiling on what the saved PDF can contain.
        private static readonly string[] TextFontFamilies =
            [PdfFontStyle.DefaultFamily, "Arial", "Times New Roman", "Courier New", "Calibri"];

        private void ShowTextSettings()
        {
            HideTextSettings();

            // When a text annotation is selected, the bar restyles THAT box; otherwise it sets tool defaults.
            // TextEditAnnotation (the Edit-Text tool, for existing PDF text) shares the Size/Color
            // controls below but has no whiteout toggle — the whiteout there isn't optional, it's
            // what hides the original text — so editTarget skips the Fill section entirely.
            var target = _styleTarget as TextAnnotation;
            var editTarget = _styleTarget as TextEditAnnotation;
            double curSize = target?.FontSize ?? editTarget?.FontSize ?? _textFontSize;
            string curFont = target?.FontName ?? editTarget?.FontName ?? _textFontFamily;
            Color curColor = target?.GetColor() ?? editTarget?.GetColor() ?? _textColor;
            bool curBold = target?.Bold ?? editTarget?.Bold ?? _textBold;
            bool curItalic = target?.Italic ?? editTarget?.Italic ?? _textItalic;
            bool curFill = target?.HasFill ?? _textWhiteout;
            // #135 item 2. No editTarget fallback: letter spacing is a TextAnnotation property.
            // A TextEditAnnotation replaces existing PDF text in place, inside a whiteout sized to
            // the run it covers, so respacing it would push characters straight out of that box.
            double curSpacing = target?.LetterSpacing ?? _textLetterSpacing;
            Color curFillColor = target is { HasFill: true } ? target.GetFillColor() : _textFillColor;

            // None of target/editTarget selected means there's either nothing placed yet (tool
            // defaults only) OR a live, uncommitted TextBox is open (Text or Edit-Text tool) —
            // _styleTarget is null for the whole duration of a live edit (ClearSelection sets it
            // so on entry). Without pushing onto the live box too, every one of these silently
            // changed only the NEXT box's defaults while leaving the box on screen untouched.
            void ApplyFont(string f)
            {
                _textFontFamily = f;
                if (target is not null) { target.FontName = f; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.FontName = f; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            void ApplySize(double v)
            {
                _textFontSize = v;
                if (target is not null) { target.FontSize = v; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.FontSize = v; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            // Upstream KillerPDF v1.8.80: toggles, swatches and the fill checkbox restyle in place —
            // RestyleLive plus a repaint of the one control that changed — instead of rebuilding
            // the bar. A rebuild flashed it and, mid-typing, yanked the live box's bar out from
            // under the pointer. The swatch rows are filled in further down.
            var colorSwatches = new List<Border>();
            var fillSwatches = new List<Border>();
            StackPanel? fillSwatchRow = null;
            void ApplyColor(Color c)
            {
                _textColor = c;
                if (target is not null) { target.SetColor(c); RestyleLive(target); }
                else if (editTarget is not null) { editTarget.SetColor(c); RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
                MarkActiveSwatch(colorSwatches, c);
            }
            void ApplyBold(bool on)
            {
                _textBold = on;
                if (target is not null) { target.Bold = on; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.Bold = on; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            void ApplyItalic(bool on)
            {
                _textItalic = on;
                if (target is not null) { target.Italic = on; RestyleLive(target); }
                else if (editTarget is not null) { editTarget.Italic = on; RestyleLive(editTarget); }
                else UpdateActiveTextBoxStyle();
            }
            // #135 item 2. Live preview on the COMMITTED annotation (RestyleLive re-renders the page
            // and refreshes the selection box without rebuilding this bar, so the slider keeps its
            // mouse capture through a drag — the same treatment the shape tool's stroke-width
            // slider gets).
            //
            // There is deliberately no UpdateActiveTextBoxStyle() branch. A WPF TextBox has no
            // letter-spacing property and no way to fake one — it owns its own text layout — so
            // while a box is open for typing there is nothing to push the value onto and nothing
            // honest to show. The value is held in tool state and applied the moment the box
            // commits. See the matching comment in PlaceTextBox for what the user sees.
            void ApplySpacing(double v)
            {
                _textLetterSpacing = v;
                if (target is not null) { target.LetterSpacing = v; RestyleLive(target); }
            }
            void ApplyFill(bool on)
            {
                _textWhiteout = on;
                if (target is not null)
                {
                    target.HasFill = on;
                    if (on) target.SetFillColor(_textFillColor);
                    RestyleLive(target);
                }
                else UpdateActiveTextBoxFill();
                // Turning fill on reveals the fill colors that were built hidden alongside it.
                if (fillSwatchRow is not null) fillSwatchRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                MarkActiveSwatch(fillSwatches, _textFillColor);
            }
            void ApplyFillColor(Color c)
            {
                _textFillColor = c;
                if (target is not null) { target.HasFill = true; target.SetFillColor(c); RestyleLive(target); }
                else { _textWhiteout = true; UpdateActiveTextBoxFill(); }
                MarkActiveSwatch(fillSwatches, c);
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Bold / italic toggle, styled like the shape-kind toggles in ShowShapeSettings.
            void AddStyleToggle(string glyph, bool active, Action<bool> apply, string tip, FontWeight fw, FontStyle fs)
            {
                Button? btn = null;
                // Repainted in place on every click — the bar is no longer rebuilt to show it.
                void Paint()
                {
                    if (btn is null) return;
                    btn.Background = active
                        ? (SolidColorBrush)FindResource("AccentGreenDim")
                        : (SolidColorBrush)FindResource("BgHover");
                    btn.Foreground = active
                        ? (SolidColorBrush)FindResource("AccentGreen")
                        : (SolidColorBrush)FindResource("TextPrimary");
                }
                btn = new Button
                {
                    // A bare Button with no Style falls back to the OS default chrome, which does
                    // not reliably respect Background/Foreground on this theme — it rendered as an
                    // unreadable solid block. ToolbarButton's Template is a plain
                    // Border+ContentPresenter bound to Background/BorderBrush/Foreground via
                    // TemplateBinding, so it actually shows what's set below.
                    //
                    // Inactive state deliberately does NOT use a literal Transparent background —
                    // it read as an illegible solid black square against the settings bar's own
                    // near-black fill even after the Style fix above. Every other button in the app
                    // that reliably shows its glyph (the main toolbar, ColorPicker's OK/Cancel) uses
                    // a real, if subtle, panel color at rest instead of true transparency.
                    Style = (Style)FindResource("ToolbarButton"),
                    Padding = new Thickness(0),
                    Content = glyph,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = fw,
                    FontStyle = fs,
                    FontSize = 13,
                    Width = 26, Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = tip,
                    Cursor = Cursors.Hand,
                    // BgHover, not BgPanel: the settings bar's own background is BgPanel now, so
                    // matching it here would blend this control back into the bar (see Paint).
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1)
                };
                Paint();
                btn.Click += (_, _) => { active = !active; apply(active); Paint(); };
                panel.Children.Add(btn);
            }

            // Font family label
            panel.Children.Add(new TextBlock
            {
                Text = "Font:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Font family dropdown — a curated, always-installed list (see TextFontFamilies) rather
            // than every system font: every option here is a known-good entry in PdfFontStyle's
            // PS-name map and FontCoverage's fallback chains, so nothing pickable here can fail to
            // embed, and a colleague reopening the same file on a different machine always has it.
            var fontBox = new ComboBox
            {
                Width = 130, Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var family in TextFontFamilies)
                fontBox.Items.Add(family);
            fontBox.SelectedItem = TextFontFamilies.Contains(curFont) ? curFont : TextFontFamilies[0];
            fontBox.SelectionChanged += (_, _) =>
            {
                if (fontBox.SelectedItem is string f) ApplyFont(f);
            };
            fontBox.DropDownClosed += (_, _) => ReturnFocusToActiveTextBox();
            panel.Children.Add(fontBox);

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Font size label
            panel.Children.Add(new TextBlock
            {
                Text = "Size:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Font size dropdown
            var sizeBox = new ComboBox
            {
                Width = 64, Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var size in TextFontSizes)
                sizeBox.Items.Add(size.ToString("0"));
            sizeBox.Text = curSize.ToString("0");
            sizeBox.SelectionChanged += (_, _) =>
            {
                if (sizeBox.SelectedItem is string s && double.TryParse(s, out double v) && v > 0)
                    ApplySize(v);
            };
            sizeBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(sizeBox.Text, out double v) && v > 0)
                    ApplySize(v);
            };
            // Only on closing the LIST: typing a size into the editable box must keep focus there.
            sizeBox.DropDownClosed += (_, _) => ReturnFocusToActiveTextBox();
            panel.Children.Add(sizeBox);

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Bold / Italic
            AddStyleToggle("B", curBold, ApplyBold, "Bold", FontWeights.Bold, FontStyles.Normal);
            AddStyleToggle("I", curItalic, ApplyItalic, "Italic", FontWeights.Normal, FontStyles.Italic);

            if (editTarget is null)
            {
                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });

                // #135 item 2: letter spacing. Built as the shape bar's stroke-width slider is —
                // same MakeLabel, same Slider sizing, same trailing read-out — because it is the
                // same kind of control: a continuous value you drag and watch.
                //
                // The range runs NEGATIVE on purpose. The reason this feature exists is lining
                // characters up with the printed boxes on a preprinted form, and a form whose boxes
                // are tighter than the font's natural pitch needs tightening, not just loosening.
                panel.Children.Add(MakeLabel("Spacing:"));
                var spacingSlider = new Slider
                {
                    Minimum = TextLetterSpacing.SliderMin,
                    Maximum = TextLetterSpacing.SliderMax,
                    Value = Math.Clamp(curSpacing, TextLetterSpacing.SliderMin, TextLetterSpacing.SliderMax),
                    Width = 90, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = TextLetterSpacing.SliderStep, IsSnapToTickEnabled = true,
                    ToolTip = "Space between characters, in pixels. Negative tightens. "
                            + "Applies to the placed text box; it cannot be shown while you are typing."
                };
                AutomationProperties.SetName(spacingSlider, "Letter spacing");
                var spacingLabel = new TextBlock
                {
                    Text = $"{spacingSlider.Value:0.#}px",
                    Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0), MinWidth = 40
                };
                spacingSlider.ValueChanged += (_, _) =>
                {
                    spacingLabel.Text = $"{spacingSlider.Value:0.#}px";
                    ApplySpacing(spacingSlider.Value);
                };
                panel.Children.Add(spacingSlider);
                panel.Children.Add(spacingLabel);
            }

            // Separator
            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Color label
            panel.Children.Add(new TextBlock
            {
                Text = "Color:",
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            });

            // Color swatches (reuse same palette as draw tool)
            foreach (var color in SwatchColors)
            {
                var swatch = MakeColorSwatch(color, ApplyColor);
                colorSwatches.Add(swatch);
                panel.Children.Add(swatch);
            }
            MarkActiveSwatch(colorSwatches, curColor);

            // Custom color: opens the full RGB picker, applied like a fixed text-color swatch.
            panel.Children.Add(MakeCustomColorSwatch(() =>
            {
                Color now = target?.GetColor() ?? editTarget?.GetColor() ?? _textColor;
                return Color.FromRgb(now.R, now.G, now.B);
            }, ApplyColor));

            if (editTarget is null)
            {
                // Separator
                panel.Children.Add(new Rectangle
                {
                    Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                    Margin = new Thickness(8, 2, 8, 2)
                });

                // Whiteout fill toggle (+ fill color swatches when on).
                panel.Children.Add(MakeLabel("Fill:"));
                var fillToggle = new CheckBox
                {
                    Content = "On",
                    IsChecked = curFill,
                    Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "Paint an opaque background behind the text (whiteout)"
                };
                fillToggle.Checked += (_, _) => ApplyFill(true);
                fillToggle.Unchecked += (_, _) => ApplyFill(false);
                panel.Children.Add(fillToggle);

                // Always built, shown only while fill is on, so the checkbox can reveal it in place.
                fillSwatchRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Visibility = curFill ? Visibility.Visible : Visibility.Collapsed
                };
                foreach (var color in SwatchColors)
                {
                    var swatch = MakeColorSwatch(color, ApplyFillColor);
                    fillSwatches.Add(swatch);
                    fillSwatchRow.Children.Add(swatch);
                }
                MarkActiveSwatch(fillSwatches, curFillColor);
                fillSwatchRow.Children.Add(MakeCustomColorSwatch(() =>
                {
                    Color now = target is { HasFill: true } ? target.GetFillColor() : _textFillColor;
                    return Color.FromRgb(now.R, now.G, now.B);
                }, ApplyFillColor));
                panel.Children.Add(fillSwatchRow);
            }

            _textSettingsBar = new Border
            {
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
            }
        }

        private void HideTextSettings()
        {
            if (_textSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }
        }

        // ============================================================
        // Shape tool settings bar
        // ============================================================

        private static readonly Color[] ShapeStrokeColors =
        {
            Color.FromRgb(0, 0, 0),
            Color.FromRgb(255, 255, 255),
            Color.FromRgb(220, 50, 50),
            Color.FromRgb(50, 130, 220),
            Color.FromRgb(40, 170, 70),
            Color.FromRgb(245, 200, 60),
            Color.FromRgb(170, 90, 220),
            Color.FromRgb(255, 140, 40)
        };

        private void ShowShapeSettings()
        {
            HideShapeSettings();

            // Restyle a selected shape, or edit tool defaults when nothing is selected.
            var target = _styleTarget as ShapeAnnotation;
            Color curStroke = target?.GetStrokeColor() ?? _shapeStrokeColor;
            bool curHasFill = target?.HasFill ?? _shapeHasFill;
            Color curFillColor = target?.GetFillColor() ?? _shapeFillColor;
            double curWidth = target?.StrokeWidth ?? _shapeStrokeWidth;

            // Upstream KillerPDF v1.8.80: every control here restyles in place — RestyleLive plus a
            // repaint of the control that changed — rather than rebuilding the bar, which flashed
            // it. The kind toggles and swatch rows are filled in below.
            var kindToggles = new List<(Button Btn, System.Windows.Shapes.Path Icon, ShapeKind Kind)>();
            var strokeSwatches = new List<Border>();
            var fillSwatches = new List<Border>();
            StackPanel? fillSwatchRow = null;
            // A selected shape shows ITS kind even when a click only retargeted the tool default
            // (see ApplyKind) — exactly what rebuilding the bar used to show.
            void PaintKindToggles()
            {
                ShapeKind shown = target?.Kind ?? _shapeKind;
                foreach (var (btn, icon, kind) in kindToggles)
                {
                    var fg = (SolidColorBrush)FindResource(kind == shown ? "AccentGreen" : "TextPrimary");
                    icon.Stroke = fg;
                    btn.Foreground = fg;
                    btn.Background = (SolidColorBrush)FindResource(kind == shown ? "AccentGreenDim" : "BgHover");
                }
            }

            void ApplyKind(ShapeKind k)
            {
                // Leaving the Polygon sub-mode mid-placement would strand the vertices; settle
                // them first (see the unfinished-polygon rule above ShapePolyClick).
                if (k != ShapeKind.Polygon) ResolveShapePolygon(commit: true);
                _shapeKind = k;
                // Restyling an existing shape can't convert between the two-point kinds and a
                // polygon — the geometry models are different — so the kind toggles only retarget
                // the tool default when a polygon is (or would become) involved.
                if (target is not null && k != ShapeKind.Polygon && target.Kind != ShapeKind.Polygon)
                { target.Kind = k; RestyleLive(target); }
                PaintKindToggles();
            }
            void ApplyStroke(Color c)
            {
                _shapeStrokeColor = c;
                if (target is not null) { target.SetStrokeColor(c); RestyleLive(target); }
                MarkActiveSwatch(strokeSwatches, c);
            }
            void ApplyFill(bool on)
            {
                _shapeHasFill = on;
                if (target is not null) { target.HasFill = on; if (on) target.SetFillColor(_shapeFillColor); RestyleLive(target); }
                // Turning fill on reveals the fill colors that were built hidden alongside it.
                if (fillSwatchRow is not null) fillSwatchRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                MarkActiveSwatch(fillSwatches, _shapeFillColor);
            }
            void ApplyFillColor(Color c)
            {
                _shapeFillColor = c;
                if (target is not null) { target.HasFill = true; target.SetFillColor(c); RestyleLive(target); }
                else _shapeHasFill = true;
                MarkActiveSwatch(fillSwatches, c);
            }
            void ApplyWidth(double w)
            {
                _shapeStrokeWidth = w;
                if (target is not null) { target.StrokeWidth = w; RestyleLive(target); }
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            // Shape kind toggle
            panel.Children.Add(MakeLabel("Shape:"));
            // These four toggles depict geometry, so they are drawn as vectors rather than with
            // Segoe MDL2 glyphs as the rest of the toolbar does. Segoe MDL2 has no glyph that
            // reads as a plain line: Line shipped as "\uE739", which is Checkbox - an empty
            // square. Sitting two slots from the Rectangle button, users read it as a second
            // rectangle and reported the Line tool as missing (it never was). A drawn line
            // cannot be misread, and it cannot silently become the wrong icon again.
            static System.Windows.Shapes.Path MakeKindIcon(ShapeKind kind, Brush stroke)
            {
                // Geometry is authored in a 16x16 box, matching the Width/Height set below.
                Geometry geo = kind switch
                {
                    ShapeKind.Rectangle => new RectangleGeometry(new Rect(2, 4, 12, 8)),
                    ShapeKind.Ellipse   => new EllipseGeometry(new Rect(2, 4, 12, 8)),
                    ShapeKind.Line      => new LineGeometry(new Point(2.5, 12.5), new Point(13.5, 3.5)),
                    _                   => Geometry.Parse("M 2,12 L 4.5,4 L 11,6.5 L 14,11 Z"),
                };
                return new System.Windows.Shapes.Path
                {
                    Data = geo,
                    Stroke = stroke,
                    StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = null,
                    Width = 16, Height = 16,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true,
                };
            }
            void AddKindToggle(ShapeKind kind, string toolTip)
            {
                // Colors come from PaintKindToggles. BgHover, not Transparent, at rest: the settings
                // bar's own background is BgPanel, so a literal-transparent button would blend back
                // into it (see ShowTextSettings' AddStyleToggle, which hit exactly this).
                var icon = MakeKindIcon(kind, (SolidColorBrush)FindResource("TextPrimary"));
                var btn = new Button
                {
                    // See the matching comment on ShowTextSettings' AddStyleToggle: a bare Button
                    // with no Style uses the OS default chrome, not this Background/Foreground.
                    Style = (Style)FindResource("ToolbarButton"),
                    Padding = new Thickness(0),
                    Content = icon,
                    Width = 26, Height = 24,
                    Margin = new Thickness(2, 0, 2, 0),
                    ToolTip = toolTip,
                    Cursor = Cursors.Hand,
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(1)
                };
                AutomationProperties.SetName(btn, toolTip);
                btn.Click += (_, _) => ApplyKind(kind);
                kindToggles.Add((btn, icon, kind));
                panel.Children.Add(btn);
            }
            AddKindToggle(ShapeKind.Rectangle, "Rectangle");
            AddKindToggle(ShapeKind.Ellipse, "Ellipse");
            AddKindToggle(ShapeKind.Line, "Line");
            AddKindToggle(ShapeKind.Polygon,
                "Freeform polygon \u2014 click to place points, click the first point or double-click to close");
            PaintKindToggles();

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Stroke color
            panel.Children.Add(MakeLabel("Stroke:"));
            foreach (var color in ShapeStrokeColors)
            {
                var swatch = MakeColorSwatch(color, ApplyStroke);
                strokeSwatches.Add(swatch);
                panel.Children.Add(swatch);
            }
            MarkActiveSwatch(strokeSwatches, curStroke);

            // Custom stroke color: opens the full RGB picker, applied like a fixed stroke swatch.
            panel.Children.Add(MakeCustomColorSwatch(() =>
            {
                Color now = target?.GetStrokeColor() ?? _shapeStrokeColor;
                return Color.FromRgb(now.R, now.G, now.B);
            }, ApplyStroke));

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Fill toggle + color
            panel.Children.Add(MakeLabel("Fill:"));
            var fillToggle = new CheckBox
            {
                Content = "On",
                IsChecked = curHasFill,
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            fillToggle.Checked += (_, _) => ApplyFill(true);
            fillToggle.Unchecked += (_, _) => ApplyFill(false);
            panel.Children.Add(fillToggle);

            // Always built, shown only while fill is on, so the checkbox can reveal it in place.
            fillSwatchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Visibility = curHasFill ? Visibility.Visible : Visibility.Collapsed
            };
            foreach (var color in ShapeStrokeColors)
            {
                var swatch = MakeColorSwatch(color, ApplyFillColor);
                fillSwatches.Add(swatch);
                fillSwatchRow.Children.Add(swatch);
            }
            MarkActiveSwatch(fillSwatches, curFillColor);

            // Custom fill color: opens the full RGB picker, applied like a fixed fill swatch.
            fillSwatchRow.Children.Add(MakeCustomColorSwatch(() =>
            {
                Color now = target?.GetFillColor() ?? _shapeFillColor;
                return Color.FromRgb(now.R, now.G, now.B);
            }, ApplyFillColor));
            panel.Children.Add(fillSwatchRow);

            panel.Children.Add(new Rectangle
            {
                Width = 1, Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(8, 2, 8, 2)
            });

            // Stroke width
            panel.Children.Add(MakeLabel("Width:"));
            var widthSlider = new Slider
            {
                Minimum = 1, Maximum = 12,
                Value = curWidth,
                Width = 90, VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 1, IsSnapToTickEnabled = true
            };
            var widthLabel = new TextBlock
            {
                Text = $"{curWidth:0}px",
                Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0), MinWidth = 32
            };
            widthSlider.ValueChanged += (_, _) =>
            {
                widthLabel.Text = $"{widthSlider.Value:0}px";
                ApplyWidth(widthSlider.Value);
            };
            panel.Children.Add(widthSlider);
            panel.Children.Add(widthLabel);

            _shapeSettingsBar = new Border
            {
                // Was a hardcoded near-black (#1a1a1a) - darker than every other surface in the
                // app's own dark palette and dead wrong in Light/HighContrast (never actually
                // themed). BgPanel is the same resource already used for inputs/panels elsewhere.
                Background = (SolidColorBrush)FindResource("BgPanel"),
                BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_shapeSettingsBar, 100);
                previewArea.Children.Add(_shapeSettingsBar);
            }
        }

        private TextBlock MakeLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = (SolidColorBrush)FindResource("TextSecondary"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        private void HideShapeSettings()
        {
            if (_shapeSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_shapeSettingsBar);
                _shapeSettingsBar = null;
            }
        }

        // ============================================================
        // Free-form polygon placement (Shapes tool)
        // ============================================================
        // Upstream KillerPDF v1.6.5's fourth shape sub-mode. Vertices go down click by click on
        // the page the first click landed on; the shape closes when a click lands on the first
        // vertex's snap target (which lights up as the cursor nears it) or on a double-click.
        // Esc abandons the shape, Backspace removes the last vertex.
        //
        // UNFINISHED-POLYGON RULE: anything that takes the document out from under the gesture —
        // switching tool or shape sub-mode, switching tab, changing page, saving / flattening /
        // printing, closing the document — COMMITS a polygon that already has 3+ vertices and
        // DISCARDS anything smaller (which is not a shape yet). Only Esc discards outright. This
        // mirrors how an open text box is committed rather than thrown away (CommitActiveTextBox,
        // which is the chokepoint ResolveShapePolygon hangs off).

        /// <summary>
        /// One Shapes-tool click while the Polygon sub-mode is active: start the shape, add a
        /// vertex, or close it when the click lands on the first vertex's snap target.
        /// </summary>
        private void ShapePolyClick(int pageIdx, Point pos)
        {
            if (_polyVertices.Count == 0)
            {
                ClearSelection();
                _polyPage = pageIdx;

                _polyPreview = new Polyline
                {
                    Stroke = TdpDialog.FrozenSolidColorBrush(_shapeStrokeColor),
                    StrokeThickness = _shapeStrokeWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                _polyPreview.Points.Add(pos);

                // Rubber band from the last placed vertex to the cursor: same hue, half weight,
                // dashed, so it reads as provisional next to the committed edges.
                _polyRubber = new Polyline
                {
                    Stroke = TdpDialog.FrozenSolidColorBrush(Color.FromArgb(
                        (byte)Math.Max(70, _shapeStrokeColor.A / 2),
                        _shapeStrokeColor.R, _shapeStrokeColor.G, _shapeStrokeColor.B)),
                    StrokeThickness = Math.Max(1, _shapeStrokeWidth / 2),
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                };
                _polyRubber.Points.Add(pos);
                _polyRubber.Points.Add(pos);

                // Snap ring over the first vertex — hidden until a click there would actually close.
                _polySnapDot = new Ellipse
                {
                    Width = 14, Height = 14,
                    StrokeThickness = 2,
                    Stroke = (SolidColorBrush)FindResource("AccentGreen"),
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                Canvas.SetLeft(_polySnapDot, pos.X - 7);
                Canvas.SetTop(_polySnapDot, pos.Y - 7);

                _annotationCanvas.Children.Add(_polyPreview);
                _annotationCanvas.Children.Add(_polyRubber);
                _annotationCanvas.Children.Add(_polySnapDot);
                _polyVertices.Add(pos);
                SetStatus("Polygon: click to add points. Click the first point or double-click to close, "
                          + "Backspace removes the last point, Esc cancels");
                return;
            }

            if (pageIdx != _polyPage) return;   // the shape stays on the page it started on

            if (_polyVertices.Count >= 3 && (pos - _polyVertices[0]).Length <= ShapePolySnapPx)
            {
                CommitShapePolygon();
                return;
            }

            _polyVertices.Add(pos);
            _polyPreview?.Points.Add(pos);
            if (_polyRubber is not null) _polyRubber.Points[0] = pos;
        }

        /// <summary>
        /// Mouse-move while a polygon is being placed: track the rubber band and light the
        /// first-vertex snap ring once a click there would close the shape.
        /// </summary>
        private void UpdateShapePolyRubber(Point pos)
        {
            if (_polyRubber is not null && _polyRubber.Points.Count > 0)
                _polyRubber.Points[_polyRubber.Points.Count - 1] = pos;
            if (_polySnapDot is not null)
                _polySnapDot.Visibility =
                    _polyVertices.Count >= 3 && (pos - _polyVertices[0]).Length <= ShapePolySnapPx
                        ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Turns the in-progress vertices into a real <see cref="ShapeAnnotation"/>.</summary>
        private void CommitShapePolygon()
        {
            int page = _polyPage;
            var pts = new List<Point>(_polyVertices);
            ResetShapePolyState();   // clears the preview visuals BEFORE the page re-render below

            // A double-click close leaves a duplicate of the vertex the first click placed; drop
            // any trailing point that sits on top of its predecessor.
            while (pts.Count >= 2 && (pts[^1] - pts[^2]).Length < 2) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3 || page < 0) return;

            var sa = new ShapeAnnotation
            {
                PageIndex = page,
                Kind = ShapeKind.Polygon,
                StrokeWidth = _shapeStrokeWidth,
                HasFill = _shapeHasFill
            };
            sa.Points.AddRange(pts);
            sa.SetStrokeColor(_shapeStrokeColor);
            sa.SetFillColor(_shapeFillColor);
            AddAnnotation(sa);       // pushes the undo snapshot and marks the document dirty
            RenderAllAnnotations(page);
            SetStatus($"Polygon added ({pts.Count} points)");
        }

        /// <summary>
        /// Settles an in-progress polygon so it can never be left dangling. With
        /// <paramref name="commit"/> a shape that already has 3+ vertices is kept; everything
        /// else is discarded. Safe no-op when no polygon is being placed.
        /// </summary>
        private void ResolveShapePolygon(bool commit)
        {
            if (_polyVertices.Count == 0) return;
            if (commit && _polyVertices.Count >= 3) CommitShapePolygon();
            else
            {
                ResetShapePolyState();
                SetStatus("Polygon cancelled");
            }
        }

        /// <summary>Backspace: drop the last placed vertex; removing the only one cancels.</summary>
        private void ShapePolyBackspace()
        {
            if (_polyVertices.Count == 0) return;
            if (_polyVertices.Count == 1) { ResolveShapePolygon(commit: false); return; }
            _polyVertices.RemoveAt(_polyVertices.Count - 1);
            if (_polyPreview is not null && _polyPreview.Points.Count > 0)
                _polyPreview.Points.RemoveAt(_polyPreview.Points.Count - 1);
            if (_polyRubber is not null) _polyRubber.Points[0] = _polyVertices[^1];
            if (_polySnapDot is not null && _polyVertices.Count < 3)
                _polySnapDot.Visibility = Visibility.Collapsed;
        }

        /// <summary>Removes the preview visuals and clears the per-document placement state.</summary>
        private void ResetShapePolyState()
        {
            if (_polyPreview is not null) _annotationCanvas.Children.Remove(_polyPreview);
            if (_polyRubber is not null) _annotationCanvas.Children.Remove(_polyRubber);
            if (_polySnapDot is not null) _annotationCanvas.Children.Remove(_polySnapDot);
            _polyVertices.Clear();
            _polyPreview = null;
            _polyRubber = null;
            _polySnapDot = null;
            _polyPage = -1;
        }
    }
}
