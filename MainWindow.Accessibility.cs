// MainWindow — Tools ▸ Check Accessibility (#243).
// A non-modal panel that lists what TDPdf's PDF/UA machine checks found, jumps to each problem on its page,
// offers the four simple fixes (language, title, show title in the title bar, mark as tagged), and lists what only a
// person can check. The checking itself is Services/PdfUaValidator.cs; the fixes are Services/PdfUaFixes.cs.
// Checking is read-only. A fix is one undo step (PushDocUndo) and marks the document changed.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        private Window? _accessibilityWindow;
        private StackPanel? _accessibilityBody;
        private CancellationTokenSource? _accessibilityCts;
        private (int Page, PdfUaBounds Bounds)? _uaOutline;

        private void CheckAccessibility_Click(object sender, RoutedEventArgs e) => ShowAccessibilityPanel();

        private void ShowAccessibilityPanel()
        {
            if (_doc is null) { TdpDialog.Show(this, "Open a PDF first."); return; }
            if (_accessibilityWindow is { IsLoaded: true })
            {
                _accessibilityWindow.Activate();
                RunAccessibilityCheck();
                return;
            }

            const double width = 520;
            var (win, body, closeBtn) = TdpDialog.CreateShellWithClose(this, "Check Accessibility", width);
            closeBtn.Click += (_, _) => win.Close();
            win.Foreground = BrushResource("TextPrimary");
            win.FontFamily = new FontFamily("Segoe UI");
            win.FontSize = 12;

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(240, TdpDialog.ScrollBodyMaxHeight(this, width) - 52)
            };
            var content = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
            scroller.Content = content;
            body.Children.Add(scroller);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 4, 16, 14) };
            var again = new Button
            {
                Content = "Check again", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0),
                Background = BrushResource("BgPanel"), Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"), Cursor = Cursors.Hand
            };
            again.Click += (_, _) => RunAccessibilityCheck();
            var done = new Button
            {
                Content = "Close", Padding = new Thickness(14, 5, 14, 5),
                Background = BrushResource("BgPanel"), Foreground = BrushResource("TextPrimary"),
                BorderBrush = BrushResource("BorderDim"), Cursor = Cursors.Hand, IsCancel = true
            };
            done.Click += (_, _) => win.Close();
            footer.Children.Add(again);
            footer.Children.Add(done);
            body.Children.Add(footer);

            _accessibilityWindow = win;
            _accessibilityBody = content;
            win.Closed += (_, _) =>
            {
                _accessibilityCts?.Cancel();
                _accessibilityWindow = null;
                _accessibilityBody = null;
                ClearAccessibilityOutline();
            };
            win.Show();
            RunAccessibilityCheck();
        }

        /// <summary>Checks the open document as it is now (unsaved changes included) and fills the panel.</summary>
        private async void RunAccessibilityCheck()
        {
            var panel = _accessibilityBody;
            var doc = _doc;
            if (panel is null || doc is null) return;
            CommitActiveTextBox();
            ClearAccessibilityOutline();
            _accessibilityCts?.Cancel();
            var cts = _accessibilityCts = new CancellationTokenSource();

            panel.Children.Clear();
            panel.Children.Add(Muted("Checking…"));

            string snapshot = TempFilePolicy.NewPath("pdfua");
            PdfUaReport? report = null;
            string? error = null;
            try
            {
                // A snapshot of the document in memory, so the checks see unsaved edits and never touch the file.
                doc.Save(snapshot);
                report = await Task.Run(() => PdfUaValidator.Check(snapshot, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { error = ex.Message; }
            finally { TempFilePolicy.TryDelete(snapshot); }

            if (cts.IsCancellationRequested || !ReferenceEquals(panel, _accessibilityBody)) return;
            panel.Children.Clear();
            if (report is null)
            {
                panel.Children.Add(Muted("This document could not be checked: " + error));
                return;
            }
            FillAccessibilityPanel(panel, report);
        }

        private TextBlock Muted(string text) => new()
        {
            Text = text, Foreground = BrushResource("TextSecondary"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };

        private void FillAccessibilityPanel(StackPanel panel, PdfUaReport report)
        {
            var verdictBrush = report.Verdict switch
            {
                PdfUaVerdict.PassedMachineChecks => BrushResource("AccentGreen"),
                PdfUaVerdict.PartlyChecked => BrushResource("WarningOrange"),
                _ => BrushResource("DangerRed"),
            };
            panel.Children.Add(new TextBlock
            {
                Text = report.VerdictText, Foreground = verdictBrush, FontWeight = FontWeights.SemiBold,
                FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4)
            });
            int errors = report.Findings.Count(f => f.Severity == PdfUaSeverity.Error);
            int warnings = report.Findings.Count(f => f.Severity == PdfUaSeverity.Warning);
            panel.Children.Add(Muted(
                $"{errors} problem{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}. " +
                "A pass here means TDPdf's machine checks found nothing; it is not a statement that the file meets the standard, " +
                "and the list at the bottom still needs a person." +
                (report.ClaimsPdfUa ? "" : " The file does not say it is PDF/UA, so it was checked against PDF/UA-1.")));

            foreach (var group in report.Findings.Where(f => f.Severity != PdfUaSeverity.Note || f.Checkpoint == "-")
                         .GroupBy(f => f.Checkpoint).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = group.Key == "-" ? "Could not check" : $"{report.StandardName}, Matterhorn {group.Key}",
                    Foreground = BrushResource("AccentGreen"), FontFamily = new FontFamily("Consolas"),
                    FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4)
                });
                foreach (var f in group) panel.Children.Add(BuildFindingRow(f));
            }

            if (report.Findings.Count == 0)
                panel.Children.Add(Muted("Nothing to report."));

            var coverage = report.Coverage.Where(c => c.StartsWith("Not checked", StringComparison.Ordinal)).ToList();
            if (coverage.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "NOT CHECKED", Foreground = BrushResource("AccentGreen"), FontFamily = new FontFamily("Consolas"),
                    FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4)
                });
                foreach (var c in coverage) panel.Children.Add(Muted(c));
            }

            var manual = new Expander
            {
                Header = "Check by hand", Foreground = BrushResource("TextPrimary"), Margin = new Thickness(0, 14, 0, 0)
            };
            var list = new StackPanel { Margin = new Thickness(8, 6, 0, 0) };
            foreach (var m in report.ManualChecks)
                list.Children.Add(new TextBlock { Text = "•  " + m, Foreground = BrushResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            manual.Content = list;
            panel.Children.Add(manual);
        }

        private UIElement BuildFindingRow(PdfUaFinding f)
        {
            var row = new Border
            {
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 3),
                Background = BrushResource("BgPanel"), CornerRadius = new CornerRadius(3),
                BorderBrush = BrushResource("BorderDim"), BorderThickness = new Thickness(1),
                Cursor = f.PageIndex is null ? Cursors.Arrow : Cursors.Hand
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = f.Message, TextWrapping = TextWrapping.Wrap,
                Foreground = f.Severity == PdfUaSeverity.Error ? BrushResource("TextPrimary") : BrushResource("TextSecondary")
            });
            if (f.PageIndex is { } pg)
                text.Children.Add(new TextBlock { Text = $"Page {pg + 1}", Foreground = BrushResource("TextSecondary"), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(text);

            if (f.Fix != PdfUaFix.None)
            {
                var fix = new Button
                {
                    Content = FixLabel(f.Fix), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3),
                    VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                    Background = BrushResource("AccentGreen"), Foreground = Brushes.White, BorderBrush = BrushResource("AccentGreen")
                };
                fix.Click += (_, e) => { e.Handled = true; ApplyAccessibilityFix(f.Fix); };
                Grid.SetColumn(fix, 1);
                grid.Children.Add(fix);
            }
            row.Child = grid;

            if (f.PageIndex is { } page)
                row.MouseLeftButtonUp += (_, _) => GoToAccessibilityFinding(page, f.Bounds);
            return row;
        }

        private static string FixLabel(PdfUaFix fix) => fix switch
        {
            PdfUaFix.SetLanguage => "Set language…",
            PdfUaFix.SetTitle => "Set title…",
            PdfUaFix.SetDisplayDocTitle => "Show title",
            PdfUaFix.MarkTagged => "Mark as tagged",
            _ => "Fix",
        };

        // ── Fixes ─────────────────────────────────────────────────────────────────────

        private void ApplyAccessibilityFix(PdfUaFix fix)
        {
            var doc = _doc;
            if (doc is null) return;
            string? value = null;
            if (fix == PdfUaFix.SetLanguage)
            {
                value = TdpDialog.PromptText(_accessibilityWindow ?? this, "Document language",
                    "The language the text is written in, as a tag such as en-US, fr-CA or es:", PdfUaFixes.SuggestLanguage())?.Trim();
                if (value is null) return;
                if (!PdfUaValidator.IsLanguageTag(value))
                {
                    TdpDialog.Show(_accessibilityWindow ?? this, "That is not a language tag. Use letters, with dashes between parts, such as en-US.");
                    return;
                }
            }
            else if (fix == PdfUaFix.SetTitle)
            {
                value = TdpDialog.PromptText(_accessibilityWindow ?? this, "Document title",
                    "A title that says what the document is:", PdfUaFixes.SuggestTitle(_ctx.DisplayName))?.Trim();
                if (value is null) return;
                if (value.Length == 0)
                {
                    TdpDialog.Show(_accessibilityWindow ?? this, "The title cannot be empty.");
                    return;
                }
            }

            // The undo snapshot is taken before the change, so one Ctrl+Z puts the document back as it was.
            PushDocUndo();
            if (!PdfUaFixes.Apply(doc, fix, value))
            {
                TdpDialog.Show(_accessibilityWindow ?? this, "That change could not be made to this document.");
                return;
            }
            MarkDirty(true);
            SetStatus(fix switch
            {
                PdfUaFix.SetLanguage => $"Document language set to {value}",
                PdfUaFix.SetTitle => "Document title set",
                PdfUaFix.SetDisplayDocTitle => "The window title will show the document title",
                _ => "Document marked as tagged",
            });
            RunAccessibilityCheck();
        }

        // ── Going to a finding ────────────────────────────────────────────────────────

        private void GoToAccessibilityFinding(int page, PdfUaBounds? bounds)
        {
            if (_doc is null || page < 0 || page >= _doc.PageCount) return;
            _uaOutline = bounds is { } b ? (page, b) : null;
            if (PageList.SelectedIndex != page) PageList.SelectedIndex = page;   // the page render draws the outline
            else RenderAllAnnotations(page);
        }

        private void ClearAccessibilityOutline()
        {
            if (_uaOutline is null) return;
            int page = _uaOutline.Value.Page;
            _uaOutline = null;
            if (_doc is not null && page == PageList.SelectedIndex && page >= 0 && page < _doc.PageCount)
                RenderAllAnnotations(page);
        }

        /// <summary>Called as a page's overlay is rebuilt: draws the outline of the finding last clicked in the panel, when it is on this page.</summary>
        private void RestoreAccessibilityOutline(int pageIndex)
        {
            if (_uaOutline is not { } o || o.Page != pageIndex || _doc is null) return;
            if (!_renderDims.TryGetValue(pageIndex, out var dims)) return;
            var (pdfW, pdfH) = PdfPageGeometry.DisplaySize(_doc.Pages[pageIndex]);
            if (pdfW <= 0 || pdfH <= 0) return;
            double sx = dims.Item1 / pdfW, sy = dims.Item2 / pdfH;
            var rect = new Rectangle
            {
                Fill = ThemeBrushes.Wash("DangerRed", 50, freeze: true),
                Stroke = ThemeBrushes.Wash("DangerRed", 230, freeze: true),
                StrokeThickness = 2,
                Width = Math.Max(o.Bounds.W * sx + 6, 8),
                Height = Math.Max(o.Bounds.H * sy + 6, 8),
                IsHitTestVisible = false,
                Tag = "AccessibilityOutline"
            };
            Canvas.SetLeft(rect, o.Bounds.X * sx - 3);
            Canvas.SetTop(rect, o.Bounds.Y * sy - 3);
            _annotationCanvas.Children.Add(rect);
        }
    }
}
