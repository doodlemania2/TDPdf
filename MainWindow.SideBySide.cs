// MainWindow — split view (#232): two TDPdf windows snapped side by side, and the read-only window.
//
// There is no in-window split. A "split" is a second TDPdf window (each window is its own process,
// see MainWindow.TabDrag.cs) placed on the other half of the current monitor. Two documents: the
// chosen tab moves to the new window, exactly as a tear-off does. The same document in both: the
// second window opens it READ-ONLY, so two processes never write one file, and it reloads when the
// editing window saves. The layout rules are in Services/SplitLayout.cs, the read-only command table
// in Services/ReadOnlyGate.cs, the switches in Services/WindowLaunch.cs.
//
// Ending a split is manual on purpose: nothing moves when a window closes. "End Split View" puts this
// window back where it was and closes the read-only view it opened; closing either window by hand
// leaves the other where it is.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TDPdf.Diagnostics;
using TDPdf.Services;
using static TDPdf.Services.WindowPlacementMath;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        // ── Split view: placing the two windows ────────────────────────────────────────────────

        /// <summary>This window's rectangle before the split moved it, so End Split View can put it back.</summary>
        private (Box Bounds, bool Maximized)? _splitRestore;
        /// <summary>The window this one split with, when it is still running.</summary>
        private int? _splitPartnerPid;
        private bool _splitPartnerIsView;

        private static bool IsProcessAlive(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch { return false; }
        }

        private Box CurrentWindowBox()
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            return new Box(Left, Top, w, h);
        }

        /// <summary>The two halves for this window's monitor, or null with a status line saying why not.</summary>
        private SplitPlan? PlanSplit()
        {
            if (_fullScreen) { SetStatus("Leave full screen to use split view."); return null; }
            var plan = SplitLayout.Compute(CurrentWindowBox(), MonitorWorkAreasDip(), MinWidth, MinHeight);
            if (plan is null) SetStatus("There is not enough room on this screen for two windows.");
            return plan;
        }

        /// <summary>Moves this window to its half. Remembers the first placement so End Split View can undo it.</summary>
        private void ApplySplitBounds(Box b)
        {
            if (_splitRestore is null)
            {
                bool maximized = WindowState == WindowState.Maximized;
                Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (!r.IsEmpty) _splitRestore = (new Box(r.Left, r.Top, r.Width, r.Height), maximized);
            }
            // The WM_GETMINMAXINFO hook only clamps a maximized window, so leave maximize first.
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Left = b.X; Top = b.Y; Width = b.Width; Height = b.Height;
        }

        /// <summary>Starts another TDPdf window on <paramref name="path"/>; returns its process id or null.</summary>
        private int? StartSeparateWindow(string path, bool readOnly, Box? bounds)
        {
            try
            {
                // ProcessPath, not Assembly.Location: that is empty for a single-file exe.
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                foreach (var a in WindowLaunch.BuildArgs(path, readOnly, bounds)) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                return p?.Id;
            }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "SplitView.Start", recoverable: true);
                SetStatus($"Could not open a new window: {ex.Message}");
                return null;
            }
        }

        /// <summary>Open in Split View: the tab moves to a new window on the other half of the screen.</summary>
        private async Task SplitMoveTabAsync(DocumentContext ctx)
        {
            if (ReadOnlyGate.ProcessIsReadOnly) { SetStatus(ReadOnlyGate.RefusedStatus); return; }
            if (_tabs.Count < 2 && !_tabs.Contains(ctx)) return;
            if (_tabs.Count < 2)
            {
                SetStatus("Open a second document here first, or use View Side by Side to see this one twice.");
                return;
            }
            var plan = PlanSplit();
            if (plan is not { } p) return;
            string? path = await ResolveTransferPathAsync(ctx);
            if (path is null) return;   // declined or failed; ResolveTransferPathAsync said why
            string name = ctx.DisplayName;
            if (StartSeparateWindow(path, readOnly: false, p.Partner) is not int pid) return;
            ApplySplitBounds(p.This);
            _splitPartnerPid = pid; _splitPartnerIsView = false;
            Telemetry.TrackEvent("SplitView.Opened", new Dictionary<string, string> { ["Kind"] = "MoveTab" });
            SetStatus($"Opened \"{name}\" beside this window.");
            RemoveTabSilently(ctx, keepFile: path);
        }

        /// <summary>View Side by Side: the same document in a read-only window on the other half.</summary>
        private void SplitViewCopy(DocumentContext ctx)
        {
            string? path = ctx.OriginalPath;
            if (ctx.Doc is null || string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                SetStatus("Save this document first, then view it side by side.");
                return;
            }
            var plan = PlanSplit();
            if (plan is not { } p) return;
            if (StartSeparateWindow(path, readOnly: true, p.Partner) is not int pid) return;
            ApplySplitBounds(p.This);
            _splitPartnerPid = pid; _splitPartnerIsView = true;
            Telemetry.TrackEvent("SplitView.Opened", new Dictionary<string, string> { ["Kind"] = "ReadOnlyCopy" });
            SetStatus(ctx.IsDirty
                ? "Opened a read-only view beside this window. It shows the last saved version and updates when you save."
                : "Opened a read-only view beside this window. It updates when you save.");
        }

        private bool SplitActive => _splitRestore is not null || (_splitPartnerPid is int pid && IsProcessAlive(pid));

        /// <summary>End Split View: a read-only view closes itself; an editing window goes back where it was and closes the view it opened.</summary>
        private void EndSplitView()
        {
            if (ReadOnlyGate.ProcessIsReadOnly) { Close(); return; }
            if (_splitPartnerIsView && _splitPartnerPid is int pid && IsProcessAlive(pid))
            {
                // Best effort: if the view is busy or already gone, it simply stays open.
                _ = Task.Run(() => WindowTransferServer.TryCloseView(pid, out _));
            }
            _splitPartnerPid = null; _splitPartnerIsView = false;
            if (_splitRestore is { } r)
            {
                _splitRestore = null;
                var areas = MonitorWorkAreasDip();
                var back = Restore(r.Bounds, areas, MinWidth, MinHeight);
                if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
                if (back is Box b) { Left = b.X; Top = b.Y; Width = b.Width; Height = b.Height; }
                if (r.Maximized) WindowState = WindowState.Maximized;
            }
            SetStatus("Split view ended.");
        }

        private Task<bool> CloseViewRequestedAsync()
        {
            if (!ReadOnlyGate.ProcessIsReadOnly) return Task.FromResult(false);
            Close();
            return Task.FromResult(true);
        }

        // ── Menus ──────────────────────────────────────────────────────────────────────────────

        private MenuItem? _splitViewMenu;

        private void InitSideBySide()
        {
            if (_windowTransferServer is not null) _windowTransferServer.CloseViewHandler = CloseViewRequestedAsync;

            // View ▸ Split View, built when it opens so the list of other tabs is current.
            foreach (var top in MainMenu.Items.OfType<MenuItem>())
            {
                if (ReadOnlyGate.Normalize(top.Header as string) != "view") continue;
                _splitViewMenu = new MenuItem { Header = "Split View", Icon = MakeMenuGlyph("", false) };
                AutomationProperties.SetName(_splitViewMenu, "Split View");
                _splitViewMenu.Items.Add(new MenuItem { Header = "…" });
                _splitViewMenu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, _splitViewMenu)) RebuildSplitViewMenu(); };
                top.Items.Add(new Separator());
                top.Items.Add(_splitViewMenu);
                break;
            }

            if (!ReadOnlyGate.ProcessIsReadOnly) return;
            InitReadOnlyWindow();
        }

        private void RebuildSplitViewMenu()
        {
            if (_splitViewMenu is null) return;
            _splitViewMenu.Items.Clear();
            bool ro = ReadOnlyGate.ProcessIsReadOnly;
            bool hasDoc = _ctx.Doc is not null;

            var move = MakeMenuItem("Open in Split View", (_, _) => _ = SplitMoveTabAsync(_ctx), null,
                "Move this tab to a new window beside this one", "");
            move.IsEnabled = hasDoc && !ro && _tabs.Count > 1;
            _splitViewMenu.Items.Add(move);

            var copy = MakeMenuItem("View Side by Side", (_, _) => SplitViewCopy(_ctx), null,
                "Show this document in a read-only window beside this one. It updates when you save.", "");
            copy.IsEnabled = hasDoc && _ctx.OriginalPath is not null;
            _splitViewMenu.Items.Add(copy);

            var other = new MenuItem { Header = "Show Another Tab in Split View" };
            var others = _tabs.Where(t => !ReferenceEquals(t, _ctx) && t.Doc is not null).ToList();
            foreach (var t in others)
            {
                var tab = t;
                string name = string.IsNullOrEmpty(tab.DisplayName) ? "Untitled.pdf" : tab.DisplayName;
                var item = new MenuItem { Header = EscapeMenuHeader(name) };
                item.Click += (_, _) => _ = SplitMoveTabAsync(tab);
                other.Items.Add(item);
            }
            other.IsEnabled = !ro && others.Count > 0;
            _splitViewMenu.Items.Add(other);

            _splitViewMenu.Items.Add(new Separator());
            var end = MakeMenuItem(ro ? "Close Split View" : "End Split View", (_, _) => EndSplitView(), null,
                ro ? "Close this read-only window" : "Put this window back and close the read-only view beside it", "");
            end.IsEnabled = ro || SplitActive;
            _splitViewMenu.Items.Add(end);
        }

        /// <summary>The split-view entries for a tab's right-click menu.</summary>
        private void AddSplitViewItemsToTabMenu(ContextMenu menu, DocumentContext ctx)
        {
            if (!ReadOnlyGate.ProcessIsReadOnly)
            {
                var move = MakeMenuItem("Open in Split View", (_, _) => _ = SplitMoveTabAsync(ctx), null,
                    "Move this tab to a new window beside this one", "");
                move.IsEnabled = _tabs.Count > 1;
                menu.Items.Add(move);
            }
            var copy = MakeMenuItem("View Side by Side", (_, _) => SplitViewCopy(ctx), null,
                "Show this document in a read-only window beside this one. It updates when you save.", "");
            copy.IsEnabled = ctx.Doc is not null && ctx.OriginalPath is not null;
            menu.Items.Add(copy);
        }

        // ── The read-only window ───────────────────────────────────────────────────────────────

        private static readonly string[] ReadOnlyDisabledToolbarButtons =
        {
            "SaveAsBtn", "ToolTextBtn", "ToolEditTextBtn", "ToolEditImageBtn", "ToolImageBtn", "ToolHighlightBtn",
            "ToolStrikeBtn", "ToolUnderlineBtn", "ToolShapeBtn", "ToolDrawBtn", "ToolEraseBtn", "ToolSignatureBtn",
            "ToolStampBtn", "ToolCropBtn", "ToolMeasureBtn", "ToolFormBtn", "ToolRedactBtn",
        };

        private readonly Dictionary<DocumentContext, int> _roStale = new();
        private readonly Dictionary<string, FileSystemWatcher> _roWatchers = new(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer? _roTimer;
        private DateTime _roDueAt = DateTime.MinValue;
        private bool _roForce, _roBusy, _roReloading;
        private int _roTick;
        private DateTime _roLastRevert = DateTime.MinValue;

        private void InitReadOnlyWindow()
        {
            foreach (var name in ReadOnlyDisabledToolbarButtons)
                if (FindName(name) is Button b) b.IsEnabled = false;

            if (FindName("FooterRightGroup") is StackPanel footer)
            {
                var badge = new Border
                {
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "This window shows the document without changing it. Make changes in the other window; this one updates when you save there.",
                    Child = new TextBlock
                    {
                        Text = "Read-only view",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                badge.SetResourceReference(Border.BorderBrushProperty, "WarningOrange");
                ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "WarningOrange");
                AutomationProperties.SetName(badge, "Read-only view");
                footer.Children.Insert(0, badge);
            }

            // Re-applied after every submenu opens, so a handler that enables an item (File menu, E-signature)
            // cannot switch a blocked command back on.
            MainMenu.AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(ReadOnlyMenuOpened), true);

            _roTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _roTimer.Tick += ReadOnlyTick;
            _roTimer.Start();
            UpdateWindowTitle();
        }

        private void ReadOnlyMenuOpened(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not MenuItem opened) return;
            bool topLevel = opened.Parent is Menu;
            if (topLevel && ReadOnlyGate.IsViewOnlyMenu(opened.Header as string ?? "")) return;
            if (!topLevel && IsInsideViewOnlyMenu(opened)) return;
            foreach (var child in opened.Items.OfType<MenuItem>())
                if (!ReadOnlyGate.IsMenuCommandAllowed(child.Header as string)) child.IsEnabled = false;
        }

        private static bool IsInsideViewOnlyMenu(MenuItem item)
        {
            DependencyObject? cur = item;
            while (cur is MenuItem m)
            {
                if (m.Parent is Menu) return ReadOnlyGate.IsViewOnlyMenu(m.Header as string ?? "");
                cur = m.Parent;
            }
            return false;
        }

        /// <summary>A command tried to change the document in a read-only window: say so and put the file's version back.</summary>
        private void ReadOnlyMutationAttempted()
        {
            SetStatus(ReadOnlyGate.RefusedStatus);
            if (_roReloading || (DateTime.UtcNow - _roLastRevert).TotalSeconds < 2) return;
            _roLastRevert = DateTime.UtcNow;
            _roStale[_ctx] = 0;
            _roForce = true;
            _roDueAt = DateTime.UtcNow;
        }

        // ── Reload when the editing window saves ───────────────────────────────────────────────

        private void SyncReadOnlyWatchers()
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _tabs)
                if (t.Doc is not null && !string.IsNullOrEmpty(t.OriginalPath) && File.Exists(t.OriginalPath))
                    wanted.Add(Path.GetFullPath(t.OriginalPath));
            foreach (var gone in _roWatchers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                try { _roWatchers[gone].Dispose(); } catch { }
                _roWatchers.Remove(gone);
            }
            foreach (var path in wanted)
            {
                if (_roWatchers.ContainsKey(path)) continue;
                try
                {
                    var w = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        EnableRaisingEvents = true,
                    };
                    string watched = path;
                    FileSystemEventHandler onChange = (_, _) => Dispatcher.BeginInvoke((Action)(() => ReadOnlyFileEvent(watched)));
                    w.Changed += onChange; w.Created += onChange; w.Deleted += onChange;
                    w.Renamed += (_, _) => Dispatcher.BeginInvoke((Action)(() => ReadOnlyFileEvent(watched)));
                    _roWatchers[path] = w;
                    ReadOnlyFileEvent(path);   // it may have been saved before the watcher existed; the fingerprint check decides
                }
                catch { /* a folder that cannot be watched is simply not followed */ }
            }
        }

        private void ReadOnlyFileEvent(string path)
        {
            foreach (var t in _tabs)
                if (!string.IsNullOrEmpty(t.OriginalPath) && FilePathPolicy.SamePath(t.OriginalPath, path) && !_roStale.ContainsKey(t))
                    _roStale[t] = 0;
            _roDueAt = DateTime.UtcNow.AddMilliseconds(ReadOnlyGate.ReloadDebounceMs);   // each event restarts the wait
        }

        private async void ReadOnlyTick(object? sender, EventArgs e)
        {
            if (_roBusy) return;
            if (_roTick++ % 3 == 0) SyncReadOnlyWatchers();
            if (_roStale.Count == 0 || DateTime.UtcNow < _roDueAt || !_roStale.ContainsKey(_ctx)) return;
            _roBusy = true;
            try { await ReloadStaleActiveTabAsync(); }
            catch (Exception ex) { Telemetry.TrackCrash(ex, "ReadOnly.Reload", recoverable: true); _roStale.Remove(_ctx); }
            finally { _roBusy = false; }
        }

        private void ScheduleReadOnlyRetry(DocumentContext ctx, int tries)
        {
            if (tries >= ReadOnlyGate.ReloadMaxRetries) { _roStale.Remove(ctx); return; }
            _roStale[ctx] = tries + 1;
            _roDueAt = DateTime.UtcNow.AddMilliseconds(ReadOnlyGate.ReloadDebounceMs);
        }

        private async Task ReloadStaleActiveTabAsync()
        {
            var ctx = _ctx;
            int tries = _roStale[ctx];
            string? path = ctx.OriginalPath;
            bool force = _roForce;
            if (ctx.Doc is null || string.IsNullOrEmpty(path)) { _roStale.Remove(ctx); _roForce = false; return; }

            if (!force)
            {
                FileFingerprint? baseline = null;
                if (ctx.OriginalFingerprint is { } rec && FilePathPolicy.SamePath(rec.Path, path))
                {
                    try { baseline = await rec.Fingerprint; } catch { }
                }
                var state = await Task.Run(() => FileFingerprint.Compare(baseline, path));
                if (!ReferenceEquals(_ctx, ctx)) return;
                switch (state)
                {
                    case FileChangeState.Unchanged: _roStale.Remove(ctx); return;
                    case FileChangeState.Missing:
                    case FileChangeState.Unknown:
                        // A save replaces the file in steps, so give it a moment before concluding it is gone.
                        // If it never comes back (the editing window did Save As elsewhere), this window just
                        // keeps showing the last version it has.
                        if (tries + 1 >= ReadOnlyGate.ReloadMaxRetries && state == FileChangeState.Missing)
                            SetStatus("The original file was moved or deleted. This view keeps showing the last version.");
                        ScheduleReadOnlyRetry(ctx, tries);
                        return;
                }
            }

            _roForce = false;
            _roReloading = true;
            var sink = new List<(string Name, string Reason)>();
            _openFailureSink = sink;
            try
            {
                CaptureViewState();   // this tab's page, scroll and zoom
                await OpenFileAsync(path);
            }
            finally
            {
                _openFailureSink = null;
                _roReloading = false;
            }
            if (sink.Count > 0 || !ReferenceEquals(_ctx, ctx) || ctx.Doc is null)
            {
                ScheduleReadOnlyRetry(ctx, tries);   // half-written or locked: try again shortly
                return;
            }
            _roStale.Remove(ctx);

            // FinishOpenFileAsync starts from page 1 at the standing zoom. Put the reader back where they were
            // once its own layout pass has run.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!ReferenceEquals(_ctx, ctx) || ctx.Doc is null) return;
            int target = Math.Clamp(ctx.SelectedPageIndex, 0, Math.Max(0, ctx.Doc.PageCount - 1));
            if (PageList.SelectedIndex != target) PageList.SelectedIndex = target;
            ctx.ViewCaptured = true;   // CaptureViewState already stamped ViewModeAtCapture
            if (ctx.ViewModeAtCapture is not (ViewMode.Continuous or ViewMode.TwoPageContinuous)) RestoreTabZoom(ctx);   // the strips refit themselves
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!ReferenceEquals(_ctx, ctx)) return;
            PagePreviewPanel.ScrollToHorizontalOffset(ctx.ViewScrollH);
            PagePreviewPanel.ScrollToVerticalOffset(ctx.ViewScrollV);
            SetStatus("Updated: the other window saved this document.");
        }

        private void DisposeSideBySide()
        {
            _roTimer?.Stop();
            foreach (var w in _roWatchers.Values) { try { w.Dispose(); } catch { } }
            _roWatchers.Clear();
        }
    }
}
