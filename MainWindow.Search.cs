// MainWindow — Search (Ctrl+F).
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
        // Search (Ctrl+F)
        // ============================================================

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 260,
                    Height = 28,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Background = BrushResource("BgPanel"),
                    Foreground = BrushResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("AccentGreen"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(_searchBox, "Search text");
                AutomationProperties.SetHelpText(_searchBox, "Enter text to find in the current PDF. Press Enter for next result and Shift Enter for previous result.");
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                _searchStatus = new TextBlock
                {
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                AutomationProperties.SetName(_searchStatus, "Search status");
                AutomationProperties.SetHelpText(_searchStatus, "Search result status");
                AutomationProperties.SetLiveSetting(_searchStatus, AutomationLiveSetting.Polite);

                var closeBtn = new Button
                {
                    Content = "\ue711",  // MDL2 Cancel glyph \u2014 matches ToolbarButton font
                    Margin = new Thickness(4, 0, 0, 0),
                    Style = (Style)FindResource("ToolbarButton"),
                    ToolTip = "Close search (Esc)"
                };
                AutomationProperties.SetName(closeBtn, "Close search");
                AutomationProperties.SetHelpText(closeBtn, "Close the search bar. Shortcut Escape.");
                closeBtn.Click += (s, e) => CloseSearchBar();

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };

                // Marking every hit at once. Styled like the Redact bar's own destructive
                // action rather than like the rest of the search bar, because that is what it is
                // the front door to — even though this press itself destroys nothing.
                var danger = (SolidColorBrush)FindResource("DangerRed");
                _searchRedactBtn = new Button
                {
                    Content = "Redact matches",
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(8, 2, 8, 2),
                    Cursor = Cursors.Hand,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    IsEnabled = false,
                    Background = new SolidColorBrush(Color.FromArgb(40, danger.Color.R, danger.Color.G, danger.Color.B)),
                    Foreground = danger,
                    BorderBrush = danger,
                    BorderThickness = new Thickness(1),
                    ToolTip = "Mark every match for redaction. Nothing is removed until you review the marks " +
                              "and press Redact Permanently."
                };
                AutomationProperties.SetName(_searchRedactBtn, "Redact matches");
                AutomationProperties.SetHelpText(_searchRedactBtn,
                    "Marks every search match for redaction so they can be reviewed. Nothing is removed until Redact Permanently is pressed.");
                _searchRedactBtn.Click += (_, _) => RedactAllSearchMatches();

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8)
                };
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(_searchRedactBtn);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    Background = BrushResource("BgDark"),
                    BorderBrush = (SolidColorBrush)FindResource("BorderDim"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(0, 0, 4, 4),
                    Padding = new Thickness(4),
                    Child = panel,
                    Margin = new Thickness(0, 0, 16, 0)
                };

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    Panel.SetZIndex(_searchBar, 100);
                    previewGrid.Children.Add(_searchBar);
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "Enter = next  Shift+Enter = prev";
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is not null)
                _searchBar.Visibility = Visibility.Collapsed;
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // The previous query is superseded the moment the text changes; stop its work now
            // rather than when the debounce fires.
            CancelSearch(_ctx);

            var text = _searchBox?.Text ?? "";
            if (text.Length >= 2)
            {
                // Debounce typing: one search for a burst of keystrokes.
                if (_searchDebounce is null)
                {
                    _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(250)
                    };
                    _searchDebounce.Tick += async (_, _) =>
                    {
                        _searchDebounce!.Stop();
                        var q = _searchBox?.Text ?? "";
                        if (q.Length >= 2) await RunSearch(q);
                    };
                }
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
            else
            {
                _searchDebounce?.Stop();
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
                UpdateSearchRedactState();
            }
        }

        /// <summary>Stops <paramref name="ctx"/>'s running search and invalidates any result still on its way back.</summary>
        private static void CancelSearch(DocumentContext ctx)
        {
            ctx.SearchGeneration++;
            try { ctx.SearchCts?.Cancel(); } catch (ObjectDisposedException) { }
            ctx.SearchCts = null;
        }

        /// <summary>The bulk-redact button only means anything while there are hits to act on.</summary>
        private void UpdateSearchRedactState()
        {
            if (_searchRedactBtn is not null)
                _searchRedactBtn.IsEnabled = _allSearchRects.Count > 0;
        }

        /// <summary>
        /// Searches the active tab's document on a worker thread. The extraction and matching
        /// (<see cref="TDPdf.Services.PdfTextSearch.Index"/>) run off the UI thread with their own
        /// PdfPig instance; only the result is brought back, and only if it is still the current
        /// query of the document it was computed for. Results are stored on THAT tab's context,
        /// never the active one, so switching tabs mid-search cannot cross them over.
        /// </summary>
        private async Task RunSearch(string query)
        {
            var ctx = _ctx;
            CancelSearch(ctx);   // also bumps the generation: results of older queries are dropped

            ClearSearchHighlights();
            ctx.AllSearchRects.Clear();
            ctx.SearchResultPages.Clear();
            ctx.SearchPageCursor = -1;

            string? file = ctx.CurrentFile;
            if (string.IsNullOrWhiteSpace(query) || file is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                UpdateSearchRedactState();
                return;
            }

            UpdateSearchRedactState();
            if (_searchStatus != null) _searchStatus.Text = "Searching...";

            var cts = new CancellationTokenSource();
            ctx.SearchCts = cts;
            int generation = ctx.SearchGeneration;
            var token = cts.Token;

            TDPdf.Services.PdfTextSearch.SearchOutcome? outcome = null;
            bool failed = false;
            try
            {
                var index = ctx.SearchIndex = TDPdf.Services.PdfTextSearch.Index.For(ctx.SearchIndex, file);
                outcome = await Task.Run(() => index.Search(query.ToLowerInvariant(), token), token);
            }
            catch (OperationCanceledException) { }
            catch { failed = true; }
            finally
            {
                if (ReferenceEquals(ctx.SearchCts, cts)) ctx.SearchCts = null;
                cts.Dispose();
            }

            // Superseded, or the document changed under it: not this tab's answer any more.
            if (token.IsCancellationRequested || ctx.SearchGeneration != generation
                || ctx.CurrentFile != file || ctx.Doc is null)
                return;

            bool active = ReferenceEquals(ctx, _ctx);
            if (failed || outcome is null)
            {
                if (active)
                {
                    if (_searchStatus != null) _searchStatus.Text = "Search error";
                    UpdateSearchRedactState();
                }
                return;
            }

            foreach (var kv in outcome.Rects) ctx.AllSearchRects[kv.Key] = kv.Value;
            ctx.SearchResultPages.AddRange(outcome.Pages);

            // A background tab keeps its results; the status line and the page jump belong to the visible one.
            if (!active) return;

            UpdateSearchRedactState();
            try
            {
                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                // Start from current page or the first page with results
                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                int totalHits = outcome.Hits;
                if (_searchStatus != null)
                    _searchStatus.Text = totalHits == 1
                        ? $"1 match ({_searchResultPages.Count} page)"
                        : $"{totalHits} matches ({_searchResultPages.Count} page{(_searchResultPages.Count != 1 ? "s" : "")})";

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;  // triggers SelectionChanged -> HighlightSearchResultsOnCurrentPage
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
                UpdateSearchRedactState();
            }
        }

        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            int curPage = PageList.SelectedIndex;
            if (!_allSearchRects.ContainsKey(curPage)) return;
            if (!_renderDims.ContainsKey(curPage)) return;

            var (renderW, renderH) = _renderDims[curPage];

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile!);
                var page = pigDoc.GetPage(curPage + 1);
                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                foreach (var (left, bottom, right, top) in _allSearchRects[curPage])
                    AddSearchHighlight(left, bottom, right, top, sx, sy, renderH);
            }
            catch { }
        }

        private void SearchNextResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor + 1) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void SearchPrevResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor - 1 + _searchResultPages.Count) % _searchResultPages.Count;
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void AddSearchHighlight(double left, double bottom, double right, double top,
            double sx, double sy, double renderH)
        {
            double cx = left  * sx;
            double cy = renderH - (top * sy);
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            var rect = new Rectangle
            {
                Fill = TdpDialog.FrozenSolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                Stroke = TdpDialog.FrozenSolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                StrokeThickness = 1,
                Width = Math.Max(cw, 4),
                Height = Math.Max(ch, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            _annotationCanvas.Children.Add(rect);
        }

        private void ClearSearchHighlights()
        {
            var toRemove = _annotationCanvas.Children.OfType<Rectangle>()
                .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
            foreach (var r in toRemove)
                _annotationCanvas.Children.Remove(r);
            if (_searchStatus is not null)
                _searchStatus.Text = "";
        }
    }
}
