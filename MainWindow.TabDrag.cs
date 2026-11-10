// MainWindow — cross-window tab drag.
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
        // Cross-window tab drag (Services/WindowTransfer.cs)
        // ============================================================
        // Each running TDPdf.exe is its own window AND its own process — MainWindow is only ever
        // constructed once per process (see App.xaml.cs). So "move a tab to another window" and
        // "tear a tab off into a new window" both mean handing a FILE PATH to a different OS
        // process, not reparenting a live in-memory document — there is no such thing as a live
        // TextAnnotation object crossing a process boundary. A dirty tab is therefore saved to its
        // real file first (prompting, exactly like closing a dirty tab already does) so the target
        // window opens the same on-disk content the source was showing; what does NOT survive the
        // move is in-progress undo/redo history, which is an acceptable, explicit trade for the
        // alternative of either silently flattening annotations behind the user's back or building
        // a full cross-process document-object serializer.

        private void BeginTabDrag(DocumentContext ctx, Border chip)
        {
            _isDraggingTab = true;
            chip.CaptureMouse();
            string label = string.IsNullOrEmpty(ctx.DisplayName) ? "Untitled.pdf" : ctx.DisplayName;
            _tabDragGhost = new TabDragGhost(label);
            _tabDragGhost.Show();
            Telemetry.TrackEvent("TabDrag.Started");
        }

        private void UpdateTabDrag(Point screenPos)
        {
            _tabDragGhost?.MoveTo(new Point(screenPos.X + 14, screenPos.Y + 18));
        }

        private void EndTabDrag(Point screenPos)
        {
            var ctx = _tabDragCandidate;
            _isDraggingTab = false;
            _tabDragGhost?.Close();
            _tabDragGhost = null;
            Mouse.Capture(null);
            if (ctx is null) return;

            int? pid = WindowHitTest.ProcessIdAtScreenPoint(screenPos);
            if (pid is int ownPid && ownPid == Environment.ProcessId)
            {
                // Dropped back on this same window. No in-strip reordering yet — just leave it.
                Telemetry.TrackEvent("TabDrag.DroppedSameWindow");
                return;
            }
            if (pid is int otherPid && WindowHitTest.IsOtherTdpdfProcess(otherPid))
                _ = TransferTabToWindowAsync(ctx, otherPid);
            else
                _ = TearOffTabToNewWindowAsync(ctx);
        }

        /// <summary>
        /// Resolves a portable, on-disk path for ctx's CURRENT content, saving first if needed.
        /// Returns null — having left ctx untouched and said why via SetStatus/a dialog — when the
        /// move should not proceed: an untitled tab with nowhere to save to, a declined save
        /// prompt, or a save failure.
        /// </summary>
        private async Task<string?> ResolveTransferPathAsync(DocumentContext ctx)
        {
            // Tab-switch chokepoint: settle whatever the CURRENTLY active tab has in flight before
            // touching _ctx, exactly like every other tab-switch path in the app.
            CommitActiveTextBox();
            ResolveShapePolygon(commit: true);
            if (!ReferenceEquals(_ctx, ctx)) ActivateContext(ctx);

            if (!ctx.IsDirty)
            {
                string? clean = ctx.OriginalPath ?? ctx.CurrentFile;
                if (clean is not null && File.Exists(clean)) return clean;
            }
            if (ctx.OriginalPath is null)
            {
                SetStatus("Save this document (Ctrl+Shift+S) before moving it to another window.");
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "Untitled" });
                return null;
            }
            var res = TdpDialog.ShowYesNo(this,
                "This document has unsaved changes. Save it and move it to the other window?",
                "Save && Move", "Cancel",
                "TDPdf", MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes)
            {
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "Declined" });
                return null;
            }
            if (!ConfirmSaveWithPendingRedactions() || !ConfirmSaveOverDigitalSignature(inPlace: true))
            {
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "Declined" });
                return null;
            }
            await SaveInPlaceAsync();
            // SaveInPlaceAsync already reported the failure via its own dialog/status.
            if (ctx.IsDirty)
            {
                Telemetry.TrackEvent("TabDrag.Blocked", new Dictionary<string, string> { ["Reason"] = "SaveFailed" });
                return null;
            }
            return ctx.OriginalPath;
        }

        private async Task TransferTabToWindowAsync(DocumentContext ctx, int destPid)
        {
            using var op = Telemetry.StartOperation("TabDrag.TransferToWindow");
            string? path = await ResolveTransferPathAsync(ctx);
            if (path is null) { op.Fail(); return; }   // ResolveTransferPathAsync already tracked why
            string displayName = ctx.DisplayName;
            var (ok, err) = await Task.Run(() =>
            {
                bool success = WindowTransferServer.TryImport(destPid, path!, out string? error);
                return (success, error);
            });
            if (ok)
            {
                SetStatus($"Moved \"{displayName}\" to another window.");
                RemoveTabSilently(ctx, keepFile: path);
            }
            else
            {
                // err is a pipe/IO failure string (timeout, broken pipe, "no response") — never a
                // path or document name — and TrackOperation's properties are scrubbed regardless.
                op.With("PipeError", err ?? "no response").Fail();
                SetStatus($"Could not move \"{displayName}\" to that window ({err ?? "no response"}).");
            }
        }

        private async Task TearOffTabToNewWindowAsync(DocumentContext ctx)
        {
            using var op = Telemetry.StartOperation("TabDrag.TearOff");
            string? path = await ResolveTransferPathAsync(ctx);
            if (path is null) { op.Fail(); return; }   // ResolveTransferPathAsync already tracked why
            string displayName = ctx.DisplayName;
            try
            {
                // Assembly.Location always reads empty for a single-file-published exe (IL3000),
                // so this has to be ProcessPath, not a Location fallback — and it needs no fallback
                // of its own since ProcessPath is reliably set for the process that is running us.
                string exePath = Environment.ProcessPath!;
                var psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
                psi.ArgumentList.Add("--new-window");
                psi.ArgumentList.Add(path!);
                Process.Start(psi);
                SetStatus($"Opened \"{displayName}\" in a new window.");
                RemoveTabSilently(ctx, keepFile: path);
            }
            catch (Exception ex)
            {
                // Spawning our own already-installed exe should never fail — if it does, that is
                // worth a real crash record (sanitized), not just a status line nobody sees again.
                Telemetry.TrackCrash(ex, "TabDrag.TearOff", recoverable: true);
                op.Fail(ex);
                SetStatus($"Could not open a new window: {ex.Message}");
            }
        }

        /// <summary>Handles an IMPORT request arriving on this window's WindowTransferServer pipe —
        /// i.e. another TDPdf window's drag landed on us. Runs on the UI thread (dispatched there by
        /// the server); the pipe thread blocks on the returned Task so it only replies OK once the
        /// tab genuinely exists here.</summary>
        private async Task<bool> ImportTabFromAnotherWindowAsync(string path)
        {
            using var op = Telemetry.StartOperation("TabDrag.Import");
            if (!_uiReady || !File.Exists(path))
            {
                op.With("Reason", !_uiReady ? "NotReady" : "PathMissing").Fail();
                return false;
            }
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                await OpenInTabAsync(path);
                bool ok = _ctx.Doc is not null;
                if (!ok) op.Fail();
                return ok;
            }
            catch (Exception ex)
            {
                Telemetry.TrackCrash(ex, "TabDrag.Import", recoverable: true);
                op.Fail(ex);
                return false;
            }
        }

        private void CancelDocumentWork(bool cancelWindowOperation)
        {
            _openCancellationTokenSource?.Cancel();
            _renderCancellationTokenSource?.Cancel();
            _secondaryRenderCts?.Cancel();
            _continuousRenderCts?.Cancel();
            _continuousSharpenCts?.Cancel();
            _continuousWindowCts?.Cancel();
            _continuousSharpenTimer?.Stop();
            if (cancelWindowOperation)
                _cancellableOpCts?.Cancel();
        }

        private void ClearContinuousRenderState()
        {
            _continuousSharpenTimer?.Stop();
            _continuousSharpPages.Clear();
            _continuousBaseBitmaps.Clear();
            _continuousSharpW = 0;
            _continuousTops.Clear();

            foreach (UIElement child in _continuousPanel.Children)
            {
                if (child is Border { Child: Image image })
                    image.Source = null;
            }
            _continuousPanel.Children.Clear();
        }

        private bool _releasedDocumentCollectionPending;

        private void QueueReleasedDocumentCollection()
        {
            if (_releasedDocumentCollectionPending) return;
            _releasedDocumentCollectionPending = true;
            _ = Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () =>
                {
                    _releasedDocumentCollectionPending = false;
                    GC.Collect(
                        GC.MaxGeneration,
                        GCCollectionMode.Optimized,
                        blocking: false,
                        compacting: false);
                });
        }

        /// <summary>
        /// Ctrl+Shift+W / tab right-click: closes every tab except <paramref name="keep"/>.
        /// Deliberately routed through <see cref="CloseTab"/> one tab at a time rather than
        /// shortcutting the teardown, so each unsaved document still gets the same prompt — and a
        /// "No" leaves that one tab open instead of aborting the whole sweep. CloseTab prompts about
        /// an inactive tab without activating it, so the kept tab stays on screen throughout.
        /// </summary>
        private void CloseOtherTabs(DocumentContext keep)
        {
            EnsureActiveTabRegistered();
            if (!_tabs.Contains(keep)) return;

            var others = _tabs.Where(t => !ReferenceEquals(t, keep)).ToList();
            if (others.Count == 0) return;

            // Invoked from an inactive tab's menu, the kept tab comes forward first, so that
            // closing the active one below never flashes a neighbour on screen in between. The
            // active tab is on its way out, so an unfinished polygon on it is dropped exactly as
            // CloseTab would drop it, rather than committed by the switch.
            if (!ReferenceEquals(_ctx, keep))
            {
                ResolveShapePolygon(commit: false);
                ActivateContext(keep);
            }

            foreach (var other in others)
            {
                CloseTab(other);
                if (!_tabs.Contains(keep)) return;   // defensive: the kept tab went away somehow
            }

            if (!ReferenceEquals(_ctx, keep)) ActivateContext(keep);
            RebuildTabStrip();

            int closed = others.Count - _tabs.Count + 1;
            SetStatus(closed <= 0
                ? "No other tabs were closed"
                : closed == 1 ? "Closed 1 other tab" : $"Closed {closed} other tabs");
        }

        /// <summary>Sets the active document's display name (tab header + title bar).</summary>
        private void SetDisplayName(string name)
        {
            _ctx.DisplayName = name;
            FileNameLabel.Text = name;
            UpdateTabChrome();
        }

        /// <summary>
        /// Called when a second process forwarded a file to this (primary) window
        /// via the single-instance pipe. Brings the window forward and opens the
        /// file in a new tab.
        /// </summary>
        /// <summary>A forwarded path that arrived before the window was wired up, replayed from Loaded.</summary>
        private string? _pendingExternalPath;

        public void OpenPathFromAnotherInstance(string? path)
        {
            // Upstream v1.7.4 (#202): the second launch forwards off the named-pipe thread and
            // marshals here, but WPF sets Application.Current.MainWindow from the Window
            // constructor — so this can run while ours is still executing, against fields the
            // manual-element-refs block has not assigned yet. Hold the path and let Loaded replay
            // it rather than dereferencing null and losing the file the user double-clicked.
            if (!_uiReady)
            {
                _pendingExternalPath = path;
                return;
            }

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                _ = OpenInTabAsync(path);
        }

        /// <summary>Replays whatever arrived during startup. A no-op in the normal case.</summary>
        private void FlushPendingExternalOpen()
        {
            if (_pendingExternalPath is null) return;
            var path = _pendingExternalPath;
            _pendingExternalPath = null;
            OpenPathFromAnotherInstance(path);
        }
    }
}
