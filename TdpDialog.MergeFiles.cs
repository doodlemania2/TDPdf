// TdpDialog: File > Merge Files (#242). The list of files, drag to reorder, folders and drops, and
// "Arrange pages": every page of every file as a thumbnail to reorder or leave out before merging.
// The ordering rules live in Services/MergePlan.cs; this is only the window around them.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using TDPdf.Services;

namespace TDPdf
{
    internal static partial class TdpDialog
    {
        /// <summary>
        /// Merge Files. <paramref name="openDocument"/> (a saved snapshot of the tab that was open) starts
        /// the list when there is one. Returns the plan to merge, or null when cancelled. Working copies
        /// the dialog made for files that are not in the returned plan are deleted here; the caller
        /// deletes the ones that are, once the merge is over.
        /// </summary>
        public static MergePlan? ShowMergeFiles(Window owner, MergeSource? openDocument)
            => new MergeFilesUi(owner, openDocument).Run();

        private sealed class MergeFilesUi
        {
            private const string FileDragFormat = "TDPdf.MergeFile";
            private const string PageDragFormat = "TDPdf.MergePage";

            private readonly Window _owner;
            private readonly MergeSource? _openDocument;
            private readonly MergePlan _plan = new();
            private readonly List<MergeSource> _madeHere = new();
            private readonly Dictionary<MergePage, BitmapSource> _thumbs = new();
            private readonly SemaphoreSlim _thumbSlots = new(2);
            private readonly CancellationTokenSource _closing = new();

            private Window _win = null!;
            private ListBox _fileList = null!;
            private ListBox _pageList = null!;
            private Border _pagePanel = null!;
            private TextBlock _summary = null!;
            private TextBlock _status = null!;
            private Button _addFiles = null!, _addFolder = null!, _remove = null!, _up = null!, _down = null!;
            private Button _arrange = null!, _mergeBtn = null!, _cancelBtn = null!;
            private Button _pageEarlier = null!, _pageLater = null!, _resetPages = null!;

            private ObservableCollection<PageRow> _pageRows = new();
            private MergePlan? _result;
            private CancellationTokenSource? _loadCts;
            private bool _loading;

            private Point _dragStart;
            private object? _dragCandidate;

            internal MergeFilesUi(Window owner, MergeSource? openDocument)
            {
                _owner = owner;
                _openDocument = openDocument;
            }

            // ── Window ────────────────────────────────────────────────────────────────────────

            internal MergePlan? Run()
            {
                if (_openDocument is not null) _plan.AddSource(_openDocument, 0);

                var (win, root) = CreateShell(_owner, "Merge Files", 700);
                _win = win;

                var content = new StackPanel();
                content.Children.Add(Pad(MakeLabel(
                    "Files are merged in the order listed. Add files or folders, or drop them here, and drag a file to move it."),
                    top: 16, bottom: 8));

                // Files and their buttons.
                _fileList = new ListBox
                {
                    Height = 190,
                    SelectionMode = SelectionMode.Extended,
                    AllowDrop = true,
                    Background = Brush("BgPanel"),
                    BorderBrush = Brush("BorderDim"),
                    Foreground = Brush("TextPrimary"),
                };
                AutomationProperties.SetName(_fileList, "Files to merge");
                _fileList.SelectionChanged += (_, _2) => UpdateButtons();
                _fileList.PreviewMouseLeftButtonDown += FileList_PreviewMouseLeftButtonDown;
                _fileList.PreviewMouseMove += FileList_PreviewMouseMove;
                _fileList.DragOver += FileList_DragOver;
                _fileList.Drop += FileList_Drop;

                _addFiles = MakeThemedButton("Add files...", false);
                _addFolder = MakeThemedButton("Add folder...", false);
                _remove = MakeThemedButton("Remove", false);
                _up = MakeThemedButton("Move up", false);
                _down = MakeThemedButton("Move down", false);
                foreach (var b in new[] { _addFiles, _addFolder, _remove, _up, _down })
                {
                    b.Margin = new Thickness(8, 0, 0, 6);
                    b.HorizontalContentAlignment = HorizontalAlignment.Left;
                }
                _addFiles.Click += async (_, _2) => await AddFilesAsync();
                _addFolder.Click += async (_, _2) => await AddFolderAsync();
                _remove.Click += (_, _2) => RemoveSelected();
                _up.Click += (_, _2) => MoveSelectedFile(-1);
                _down.Click += (_, _2) => MoveSelectedFile(1);

                var side = new StackPanel();
                foreach (var b in new[] { _addFiles, _addFolder, _remove, _up, _down }) side.Children.Add(b);

                var filesRow = new Grid();
                filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                filesRow.Children.Add(_fileList);
                Grid.SetColumn(side, 1);
                filesRow.Children.Add(side);
                content.Children.Add(Pad(filesRow, bottom: 4));

                _status = MakeLabel("", secondary: true);
                content.Children.Add(Pad(_status, bottom: 2));
                _summary = MakeLabel("", secondary: true);
                content.Children.Add(Pad(_summary, bottom: 8));

                _arrange = MakeThemedButton("Arrange pages...", false);
                _arrange.Margin = new Thickness(0);
                _arrange.HorizontalAlignment = HorizontalAlignment.Left;
                _arrange.Click += (_, _2) => TogglePages();
                content.Children.Add(Pad(_arrange, bottom: 8));

                BuildPagePanel();
                content.Children.Add(Pad(_pagePanel, bottom: 4));

                root.Children.Add(new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = ScrollBodyMaxHeight(_owner, 700) - 70,
                    Content = content,
                });

                _mergeBtn = MakeThemedButton("Merge", accent: true);
                _mergeBtn.IsDefault = true;
                _mergeBtn.Click += (_, _2) => { if (_mergeBtn.IsEnabled) { _result = _plan; _win.Close(); } };
                _cancelBtn = MakeThemedButton("Cancel", accent: false);
                _cancelBtn.IsCancel = true;   // Esc: stops a load in progress, otherwise closes
                _cancelBtn.Click += (_, _2) =>
                {
                    if (_loading) { _loadCts?.Cancel(); return; }
                    _win.Close();
                };
                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                btnPanel.Children.Add(_mergeBtn);
                btnPanel.Children.Add(_cancelBtn);
                root.Children.Add(new Border { Padding = new Thickness(16, 8, 16, 16), Child = btnPanel });

                _win.Closing += (_, e) =>
                {
                    if (_loading) { e.Cancel = true; _loadCts?.Cancel(); }
                };
                _win.Closed += (_, _2) => _closing.Cancel();

                RefreshFiles();
                UpdateButtons();
                _win.ShowDialog();

                CleanUp();
                return _result;
            }

            /// <summary>Working copies made here for files that did not make it into the result.</summary>
            private void CleanUp()
            {
                foreach (var s in _madeHere)
                {
                    if (_result is not null && _result.Sources.Contains(s)) continue;
                    TempFilePolicy.TryDelete(s.WorkPath);
                }
            }

            // ── Adding ────────────────────────────────────────────────────────────────────────

            private async Task AddFilesAsync()
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "PDF files|*.pdf",
                    Title = "Add PDFs to merge",
                    Multiselect = true,
                };
                if (dlg.ShowDialog(_win) != true) return;
                await AddPathsAsync(dlg.FileNames);
            }

            private async Task AddFolderAsync()
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add every PDF in a folder" };
                if (dlg.ShowDialog(_win) != true) return;
                await AddPathsAsync(new[] { dlg.FolderName });
            }

            /// <summary>
            /// Reads each file on a background thread, one at a time, so the window stays responsive
            /// however long the list is; Esc stops after the file being read. A file that needs a
            /// password asks for it here.
            /// </summary>
            private async Task AddPathsAsync(IEnumerable<string> paths)
            {
                if (_loading) return;
                var given = paths.ToList();
                SetLoading(true, "Looking for PDFs...");
                _loadCts = new CancellationTokenSource();
                var ct = _loadCts.Token;
                var failures = new List<(string Name, string Reason)>();
                MergeExpansion expansion;
                try
                {
                    expansion = await Task.Run(() => MergeInputs.Expand(
                        given,
                        Directory.Exists,
                        dir => Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                        })), ct);

                    for (int i = 0; i < expansion.Files.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string file = expansion.Files[i];
                        string name = Path.GetFileName(file);
                        _status.Text = $"Reading {name}  ({i + 1} of {expansion.Files.Count}).  Press Esc to stop.";

                        int id = _plan.NextSourceId();
                        string display = MergeInputs.DisplayName(file);
                        string? password = null;
                        MergeSourceLoader.Loaded loaded;
                        while (true)
                        {
                            string? pw = password;
                            loaded = await Task.Run(() => MergeSourceLoader.Load(
                                id, file, display, pw, () => TempFilePolicy.NewPath("mergein")), ct);
                            if (loaded.Outcome != MergeSourceLoader.Outcome.NeedsPassword) break;
                            password = PromptPassword(_win, file);
                            if (password is null) break;   // skipped
                        }

                        if (loaded.Outcome == MergeSourceLoader.Outcome.Ok && loaded.Source is { } src)
                        {
                            if (src.IsTemporary) _madeHere.Add(src);
                            _plan.AddSource(src);
                            RefreshFiles();
                        }
                        else if (loaded.Outcome == MergeSourceLoader.Outcome.NeedsPassword)
                        {
                            failures.Add((name, "needs a password, and none was given"));
                        }
                        else
                        {
                            failures.Add((name, loaded.Error ?? "could not be read"));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Esc: what was added stays; the rest is simply not read.
                    expansion = new MergeExpansion(Array.Empty<string>(), Array.Empty<string>());
                }
                finally
                {
                    SetLoading(false, "");
                    RefreshFiles();
                }

                string? report = OpenBatchReport.Summarize(expansion.Files.Count, failures);
                string notPdf = expansion.Skipped.Count > 0
                    ? $"{expansion.Skipped.Count} item{(expansion.Skipped.Count == 1 ? " was" : "s were")} not a PDF and {(expansion.Skipped.Count == 1 ? "was" : "were")} left out."
                    : "";
                if (report is not null || notPdf.Length > 0)
                    Show(_win, string.Join("\n\n", new[] { report, notPdf }.Where(s => !string.IsNullOrEmpty(s))),
                        "Merge Files", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            private void SetLoading(bool loading, string status)
            {
                _loading = loading;
                _status.Text = status;
                UpdateButtons();
            }

            // ── File list ─────────────────────────────────────────────────────────────────────

            private void RefreshFiles()
            {
                var selected = SelectedSourceIds();
                _fileList.Items.Clear();
                foreach (var s in _plan.Sources)
                {
                    var title = new TextBlock
                    {
                        Text = s.IsOpenDocument ? $"{s.DisplayName}  (open now)" : s.DisplayName,
                        Foreground = Brush("TextPrimary"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    string detail = $"{s.PageCount} page{(s.PageCount == 1 ? "" : "s")}";
                    var sub = new TextBlock
                    {
                        Text = s.IsTemporary && !s.IsOpenDocument ? detail + "  (password removed for this merge)" : detail,
                        Foreground = Brush("TextSecondary"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    var stack = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
                    stack.Children.Add(title);
                    stack.Children.Add(sub);
                    var item = new ListBoxItem { Content = stack, Tag = s.Id, IsSelected = selected.Contains(s.Id) };
                    AutomationProperties.SetName(item, $"{s.DisplayName}, {s.PageCount} pages");
                    _fileList.Items.Add(item);
                }
                UpdateSummary();
                if (_pagePanel.Visibility == Visibility.Visible) RefreshPages();
                UpdateButtons();
            }

            private HashSet<int> SelectedSourceIds()
            {
                var ids = new HashSet<int>();
                foreach (var o in _fileList.SelectedItems)
                    if (o is ListBoxItem { Tag: int id }) ids.Add(id);
                return ids;
            }

            private void RemoveSelected()
            {
                foreach (int id in SelectedSourceIds())
                {
                    var s = _plan.Find(id);
                    if (s is null) continue;
                    _plan.RemoveSource(id);
                    if (_madeHere.Remove(s)) TempFilePolicy.TryDelete(s.WorkPath);
                }
                RefreshFiles();
            }

            private void MoveSelectedFile(int by)
            {
                var ids = SelectedSourceIds();
                if (ids.Count != 1) return;
                int from = _plan.Sources.ToList().FindIndex(s => s.Id == ids.First());
                if (from < 0) return;
                _plan.MoveSource(from, from + by);
                RefreshFiles();
            }

            private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                _dragStart = e.GetPosition(null);
                _dragCandidate = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.Tag;
            }

            private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not int id) return;
                if (!FarEnough(e.GetPosition(null))) return;
                _dragCandidate = null;
                DragDrop.DoDragDrop(_fileList, new DataObject(FileDragFormat, id), DragDropEffects.Move);
            }

            private void FileList_DragOver(object sender, DragEventArgs e)
            {
                e.Effects = e.Data.GetDataPresent(FileDragFormat) ? DragDropEffects.Move
                          : e.Data.GetDataPresent(DataFormats.FileDrop) && !_loading ? DragDropEffects.Copy
                          : DragDropEffects.None;
                e.Handled = true;
            }

            private async void FileList_Drop(object sender, DragEventArgs e)
            {
                e.Handled = true;
                if (e.Data.GetDataPresent(FileDragFormat) && e.Data.GetData(FileDragFormat) is int id)
                {
                    int from = _plan.Sources.ToList().FindIndex(s => s.Id == id);
                    int to = IndexAt(_fileList, e.GetPosition(_fileList));
                    if (from >= 0) _plan.MoveSource(from, to < 0 ? _plan.Sources.Count - 1 : to);
                    RefreshFiles();
                }
                else if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
                {
                    await AddPathsAsync(dropped);
                }
            }

            // ── Pages ─────────────────────────────────────────────────────────────────────────

            private void BuildPagePanel()
            {
                var panel = new StackPanel();
                panel.Children.Add(MakeLabel(
                    "Untick a page to leave it out, and drag a page to move it. Pages keep their files' form fields, fonts and bookmarks.",
                    secondary: true));

                var template = new DataTemplate();
                var grid = new FrameworkElementFactory(typeof(StackPanel));
                grid.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                grid.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
                grid.SetBinding(UIElement.OpacityProperty, new Binding(nameof(PageRow.Opacity)));

                var check = new FrameworkElementFactory(typeof(CheckBox));
                check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new Binding(nameof(PageRow.Included)) { Mode = BindingMode.TwoWay });
                check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                check.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 10, 0));
                check.SetValue(AutomationProperties.NameProperty, "Include this page");
                grid.AppendChild(check);

                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(FrameworkElement.WidthProperty, 76.0);
                border.SetValue(FrameworkElement.HeightProperty, 100.0);
                border.SetValue(Border.BackgroundProperty, Brush("BgDark"));
                border.SetValue(Border.BorderBrushProperty, Brush("BorderDim"));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                var img = new FrameworkElementFactory(typeof(Image));
                img.SetBinding(Image.SourceProperty, new Binding(nameof(PageRow.Thumb)));
                img.SetValue(Image.StretchProperty, Stretch.Uniform);
                border.AppendChild(img);
                grid.AppendChild(border);

                var label = new FrameworkElementFactory(typeof(TextBlock));
                label.SetBinding(TextBlock.TextProperty, new Binding(nameof(PageRow.Label)));
                label.SetValue(TextBlock.ForegroundProperty, Brush("TextPrimary"));
                label.SetValue(TextBlock.FontSizeProperty, 12.0);
                label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                label.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
                grid.AppendChild(label);
                template.VisualTree = grid;

                // Only the rows on screen get containers (Standard virtualisation), and a container
                // that is created asks for its thumbnail while one that is scrolled away stops.
                var baseStyle = Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style;
                var itemStyle = new Style(typeof(ListBoxItem), baseStyle);
                itemStyle.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler(PageItem_Loaded)));
                itemStyle.Setters.Add(new EventSetter(FrameworkElement.UnloadedEvent, new RoutedEventHandler(PageItem_Unloaded)));
                itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

                _pageList = new ListBox
                {
                    Height = 300,
                    ItemTemplate = template,
                    ItemContainerStyle = itemStyle,
                    ItemsSource = _pageRows,
                    SelectionMode = SelectionMode.Single,
                    AllowDrop = true,
                    Background = Brush("BgPanel"),
                    BorderBrush = Brush("BorderDim"),
                    Foreground = Brush("TextPrimary"),
                    Margin = new Thickness(0, 6, 0, 6),
                };
                VirtualizingPanel.SetIsVirtualizing(_pageList, true);
                VirtualizingPanel.SetVirtualizationMode(_pageList, VirtualizationMode.Standard);
                ScrollViewer.SetCanContentScroll(_pageList, true);
                AutomationProperties.SetName(_pageList, "Pages to merge");
                _pageList.SelectionChanged += (_, _2) => UpdateButtons();
                _pageList.PreviewMouseLeftButtonDown += PageList_PreviewMouseLeftButtonDown;
                _pageList.PreviewMouseMove += PageList_PreviewMouseMove;
                _pageList.DragOver += (_, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(PageDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
                _pageList.Drop += PageList_Drop;
                panel.Children.Add(_pageList);

                _pageEarlier = MakeThemedButton("Move earlier", false);
                _pageLater = MakeThemedButton("Move later", false);
                _resetPages = MakeThemedButton("Back to file order", false);
                foreach (var b in new[] { _pageEarlier, _pageLater, _resetPages }) b.Margin = new Thickness(0, 0, 8, 0);
                _pageEarlier.Click += (_, _2) => MoveSelectedPage(-1);
                _pageLater.Click += (_, _2) => MoveSelectedPage(1);
                _resetPages.Click += (_, _2) => { _plan.ResetArrangement(); RefreshFiles(); };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(_pageEarlier);
                row.Children.Add(_pageLater);
                row.Children.Add(_resetPages);
                panel.Children.Add(row);

                _pagePanel = new Border { Child = panel, Visibility = Visibility.Collapsed };
            }

            private void TogglePages()
            {
                bool show = _pagePanel.Visibility != Visibility.Visible;
                _pagePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                _arrange.Content = show ? "Hide pages" : "Arrange pages...";
                if (show) RefreshPages();
                UpdateButtons();
            }

            private void RefreshPages()
            {
                int selected = _pageList.SelectedIndex;
                _pageRows = new ObservableCollection<PageRow>(_plan.Pages.Select(e => new PageRow(this, e)));
                _pageList.ItemsSource = _pageRows;
                if (selected >= 0 && selected < _pageRows.Count) _pageList.SelectedIndex = selected;
            }

            private void MoveSelectedPage(int by)
            {
                int from = _pageList.SelectedIndex;
                if (from < 0) return;
                int to = _plan.MovePage(from, from + by);
                RefreshPages();
                _pageList.SelectedIndex = to;
                _pageList.ScrollIntoView(_pageList.SelectedItem);
                UpdateButtons();
                UpdateSummary();
            }

            private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                _dragStart = e.GetPosition(null);
                bool onCheck = FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is not null;
                var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
                _dragCandidate = !onCheck && item is not null ? _pageList.ItemContainerGenerator.IndexFromContainer(item) : null;
            }

            private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not int index || index < 0) return;
                if (!FarEnough(e.GetPosition(null))) return;
                _dragCandidate = null;
                DragDrop.DoDragDrop(_pageList, new DataObject(PageDragFormat, index), DragDropEffects.Move);
            }

            private void PageList_Drop(object sender, DragEventArgs e)
            {
                e.Handled = true;
                if (!e.Data.GetDataPresent(PageDragFormat) || e.Data.GetData(PageDragFormat) is not int from) return;
                int to = IndexAt(_pageList, e.GetPosition(_pageList));
                if (to < 0) to = _pageRows.Count - 1;
                int landed = _plan.MovePage(from, to);
                RefreshPages();
                _pageList.SelectedIndex = landed;
                UpdateSummary();
                UpdateButtons();
            }

            private void PageItem_Loaded(object sender, RoutedEventArgs e)
            {
                if ((sender as FrameworkElement)?.DataContext is PageRow row) row.BecameVisible();
            }

            private void PageItem_Unloaded(object sender, RoutedEventArgs e)
            {
                if ((sender as FrameworkElement)?.DataContext is PageRow row) row.Visible = false;
            }

            // ── State ─────────────────────────────────────────────────────────────────────────

            private void UpdateSummary()
            {
                int files = _plan.Sources.Count;
                int included = _plan.IncludedCount;
                _summary.Text = files == 0
                    ? "Nothing to merge yet."
                    : $"{files} file{(files == 1 ? "" : "s")}, {included} page{(included == 1 ? "" : "s")} will be merged"
                      + (included < _plan.TotalPages ? $" ({_plan.TotalPages - included} left out)." : ".");
                _mergeBtn.Content = included > 0 ? $"Merge {included} page{(included == 1 ? "" : "s")}" : "Merge";
                _mergeBtn.IsEnabled = included > 0 && !_loading;
            }

            private void UpdateButtons()
            {
                bool idle = !_loading;
                int selected = _fileList.SelectedItems.Count;
                _addFiles.IsEnabled = idle;
                _addFolder.IsEnabled = idle;
                _remove.IsEnabled = idle && selected > 0;
                int index = selected == 1 ? _fileList.SelectedIndex : -1;
                _up.IsEnabled = idle && index > 0;
                _down.IsEnabled = idle && index >= 0 && index < _fileList.Items.Count - 1;
                _arrange.IsEnabled = idle && _plan.Sources.Count > 0;
                int p = _pageList?.SelectedIndex ?? -1;
                if (_pageEarlier is not null)
                {
                    _pageEarlier.IsEnabled = idle && p > 0;
                    _pageLater.IsEnabled = idle && p >= 0 && p < _plan.TotalPages - 1;
                    _resetPages.IsEnabled = idle && _plan.Arranged;
                }
                if (_summary is not null && _mergeBtn is not null) UpdateSummary();
            }

            // ── Helpers ───────────────────────────────────────────────────────────────────────

            private bool FarEnough(Point now) =>
                Math.Abs(now.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(now.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;

            private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
            {
                while (d is not null)
                {
                    if (d is T hit) return hit;
                    d = d is Visual or System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(d)
                        : LogicalTreeHelper.GetParent(d);
                }
                return null;
            }

            /// <summary>The index of the row under <paramref name="at"/>, or -1 when it is over empty space.</summary>
            private static int IndexAt(ListBox list, Point at)
            {
                var hit = VisualTreeHelper.HitTest(list, at)?.VisualHit as DependencyObject;
                var item = FindAncestor<ListBoxItem>(hit);
                return item is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(item);
            }

            private string NameOf(int sourceId) => _plan.Find(sourceId)?.DisplayName ?? "";

            /// <summary>One page in the arrangement list.</summary>
            private sealed class PageRow : INotifyPropertyChanged
            {
                private readonly MergeFilesUi _ui;
                private BitmapSource? _thumb;
                private bool _loading;

                internal PageRow(MergeFilesUi ui, MergePageEntry entry)
                {
                    _ui = ui;
                    Entry = entry;
                    if (ui._thumbs.TryGetValue(entry.Page, out var cached)) _thumb = cached;
                }

                internal MergePageEntry Entry { get; }
                internal bool Visible { get; set; }

                public string Label => $"{_ui.NameOf(Entry.SourceId)}, page {Entry.PageIndex + 1}";
                public double Opacity => Entry.Included ? 1.0 : 0.4;
                public BitmapSource? Thumb => _thumb;

                public bool Included
                {
                    get => Entry.Included;
                    set
                    {
                        if (Entry.Included == value) return;
                        _ui._plan.SetIncluded(Entry, value);
                        Raise(nameof(Included));
                        Raise(nameof(Opacity));
                        _ui.UpdateSummary();
                        _ui.UpdateButtons();
                    }
                }

                public event PropertyChangedEventHandler? PropertyChanged;
                private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

                internal void BecameVisible()
                {
                    Visible = true;
                    if (_thumb is not null || _loading) return;
                    _loading = true;

                    var source = _ui._plan.Find(Entry.SourceId);
                    if (source is null) { _loading = false; return; }
                    string path = source.WorkPath;
                    int index = Entry.PageIndex;
                    var token = _ui._closing.Token;
                    var dispatcher = _ui._win.Dispatcher;

                    _ = Task.Run(async () =>
                    {
                        bool got = false;
                        try
                        {
                            await _ui._thumbSlots.WaitAsync(token);
                            got = true;
                            // Scrolled away while it waited its turn: let it be asked for again later.
                            if (!Visible || token.IsCancellationRequested) { _loading = false; return; }

                            BitmapSource? bmp = null;
                            try
                            {
                                using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(160, 160));
                                using var page = reader.GetPageReader(index);
                                int w = page.GetPageWidth(), h = page.GetPageHeight();
                                byte[] bgra = page.GetImage();
                                PdfDocumentService.CompositeBgraOverWhite(bgra);
                                bmp = PageRaster.ToFrozenBitmap(bgra, w, h, 96, 96);
                            }
                            catch { /* a page that will not render simply has no thumbnail */ }

                            _ = dispatcher.BeginInvoke(new Action(() =>
                            {
                                _loading = false;
                                if (bmp is null) return;
                                _thumb = bmp;
                                _ui._thumbs[Entry.Page] = bmp;
                                Raise(nameof(Thumb));
                            }));
                        }
                        catch (OperationCanceledException) { _loading = false; }
                        catch { _loading = false; }
                        finally { if (got) _ui._thumbSlots.Release(); }
                    });
                }
            }
        }
    }
}
