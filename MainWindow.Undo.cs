// MainWindow — snapshot-based undo helpers.
// Pushing and trimming per-page annotation snapshots on the undo stack.
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
        // Snapshot-based undo helpers
        // ============================================================

        private void PushUndo(UndoEntry entry)
        {
            _undoStack.AddLast(entry);
            while (_undoStack.Count > MaxUndoEntries)
                _undoStack.RemoveFirst();
            _redoStack.Clear();
        }

        /// <summary>
        /// Deep-clone the current annotation list for <paramref name="pageIdx"/> and push it
        /// onto the undo stack. Must be called BEFORE the mutation. Clears redo and trims to cap.
        /// </summary>
        private void PushPageSnapshot(int pageIdx)
        {
            if (pageIdx < 0) return;
            var snapshot = _annotations.TryGetValue(pageIdx, out var list)
                ? list.Select(a => a.Clone()).ToList()
                : new List<PageAnnotation>();
            PushUndo(new UndoEntry(UndoKind.PageSnapshot, pageIdx, PageAnnotations: snapshot));
        }

        /// <summary>
        /// Pops the top entry if (and only if) it is a PageSnapshot for the given page.
        /// Used to discard no-op snapshots when a move/resize gesture ended without movement.
        /// </summary>
        private void DropTopSnapshotIfFor(int pageIdx)
        {
            if (_undoStack.Count == 0) return;
            var top = _undoStack.Last!.Value;
            if (top.Kind == UndoKind.PageSnapshot && top.PageIdx == pageIdx)
                _undoStack.RemoveLast();
        }

        private static List<PageAnnotation> CloneList(List<PageAnnotation>? src) =>
            src is null ? new List<PageAnnotation>() : src.Select(a => a.Clone()).ToList();

        // Snapshot-based undo/redo.
        // PageSnapshot: deep-cloned annotation list for one page captured BEFORE the mutation.
        // Document: full PDF byte snapshot (PushDocUndo) for crop, transform, text-content edits,
        //   bookmark and form-field authoring; acts as a history barrier (clears mixed-kind entries
        //   to avoid restoring page snapshots onto a different document). Page insert / delete /
        //   reorder / duplicate / merge / rotate take NO document snapshot and are not undoable;
        //   they keep the page snapshots valid instead, by renumbering them (ApplyPageIndexMap) or
        //   remapping them through the turn (RemapAnnotationSnapshots).
        private enum UndoKind { PageSnapshot, Document }
        private readonly record struct UndoEntry(
            UndoKind Kind,
            int PageIdx = -1,
            byte[]? DocBytes = null,
            List<PageAnnotation>? PageAnnotations = null);
        private const int MaxUndoEntries = 100;
        private LinkedList<UndoEntry> _undoStack => _ctx.UndoStack;
        private LinkedList<UndoEntry> _redoStack => _ctx.RedoStack;
        private void UndoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (ShouldDeferUndoToTextBox()) return;
            Undo_Click(sender, e);
        }

        private static List<UndoEntry> RemapPageSnapshotIndices(LinkedList<UndoEntry> history, int[] map)
        {
            var result = new List<UndoEntry>(history.Count);
            foreach (var entry in history)
            {
                if (entry.Kind != UndoKind.PageSnapshot) { result.Add(entry); continue; }
                int page = TDPdf.Services.PageIndexRemap.Map(map, entry.PageIdx);
                if (page == TDPdf.Services.PageIndexRemap.Removed) continue;
                result.Add(entry with { PageIdx = page });
            }
            return result;
        }

        private static void ReplaceHistory(LinkedList<UndoEntry> history, List<UndoEntry> entries)
        {
            history.Clear();
            foreach (var entry in entries)
            {
                // ApplyUndoRedoStep re-stamps PageIndex on restore as well; keeping the snapshot's
                // own copies in step means nothing ever reads a stale number off one.
                if (entry.Kind == UndoKind.PageSnapshot && entry.PageAnnotations is { } annotations)
                    foreach (var annotation in annotations) annotation.PageIndex = entry.PageIdx;
                history.AddLast(entry);
            }
        }

        private static void RemapAnnotationSnapshots(
            LinkedList<UndoEntry> history,
            int pageIndex,
            int delta,
            double oldW,
            double oldH)
        {
            for (var node = history.First; node is not null; node = node.Next)
            {
                if (node.Value.Kind == UndoKind.PageSnapshot
                    && node.Value.PageIdx == pageIndex
                    && node.Value.PageAnnotations is { } annotations)
                {
                    TDPdf.Services.AnnotationRotate.Remap(annotations, delta, oldW, oldH);
                }
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            ApplyUndoRedoStep(_undoStack, _redoStack, "undo");
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            ApplyUndoRedoStep(_redoStack, _undoStack, "redo");
        }

        private void RedoCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            Redo_Click(sender, e);
        }

        /// <summary>
        /// Pops one entry from <paramref name="source"/>, captures the current state matching
        /// that entry's kind into <paramref name="target"/> (so the inverse step is available),
        /// and applies the popped entry to the document/annotation state.
        /// </summary>
        private void ApplyUndoRedoStep(LinkedList<UndoEntry> source, LinkedList<UndoEntry> target, string label)
        {
            if (source.Count == 0)
            {
                SetStatus(label == "undo" ? "Nothing to undo" : "Nothing to redo");
                return;
            }

            // Cancel any active transient gesture/edit before mutating state.
            bool canceledTextEditor = _activeTextBox is not null;
            CancelActiveGesture();
            if (canceledTextEditor)
            {
                SetStatus("Text edit canceled");
                return;
            }

            var entry = source.Last!.Value;
            source.RemoveLast();

            if (entry.Kind == UndoKind.PageSnapshot)
            {
                int pageIdx = entry.PageIdx;
                // Capture the inverse step for redo (or re-undo)
                var current = _annotations.TryGetValue(pageIdx, out var curList)
                    ? curList.Select(a => a.Clone()).ToList()
                    : new List<PageAnnotation>();
                target.AddLast(new UndoEntry(UndoKind.PageSnapshot, pageIdx, PageAnnotations: current));

                var restored = CloneList(entry.PageAnnotations);
                foreach (var a in restored) a.PageIndex = pageIdx;
                _annotations[pageIdx] = restored;
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty();
                SetStatus(label == "undo" ? "Undid annotation change" : "Redid annotation change");
            }
            else // Document
            {
                if (entry.DocBytes is null) return;
                // Capture current document bytes for the inverse step.
                byte[]? currentBytes = null;
                if (_doc is not null)
                {
                    if (HasPendingFormValues) WriteFormValuesToDocument();   // redo must not lose them either
                    using var ms = new System.IO.MemoryStream();
                    _doc.Save(ms);
                    currentBytes = ms.ToArray();
                }
                if (currentBytes is not null)
                    target.AddLast(new UndoEntry(UndoKind.Document, DocBytes: currentBytes));

                int selectedIdx = PageList.SelectedIndex;
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"tdpdf_undo_{Guid.NewGuid():N}.pdf");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                _currentFile = tempPath;
                // Document edits are a history barrier: page snapshots from before/after refer
                // to a different page layout. Drop them on both stacks (except the just-captured
                // inverse Document entry on `target`).
                ClearPageSnapshotsOnly(source);
                ClearPageSnapshotsExceptLast(target);
                _annotations.Clear();
                ESignDropFieldsForLayoutChange(_ctx);
                ClearFormState();
                ClearMeasurement();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty();
                RefreshPageList();
                LoadOutlines();   // the reopened _doc has its own outline tree; rebuild the panel (#133)
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                SetStatus(label == "undo" ? "Undid document change" : "Redid document change");
            }
        }

        private static void ClearPageSnapshotsOnly(LinkedList<UndoEntry> list)
        {
            var node = list.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Kind == UndoKind.PageSnapshot)
                    list.Remove(node);
                node = next;
            }
        }

        private static void ClearPageSnapshotsExceptLast(LinkedList<UndoEntry> list)
        {
            // Preserve the most recently added Document inverse entry (always the tail) but
            // strip any prior PageSnapshot entries that would refer to the wrong layout.
            var tail = list.Last;
            var node = list.First;
            while (node is not null && node != tail)
            {
                var next = node.Next;
                if (node.Value.Kind == UndoKind.PageSnapshot)
                    list.Remove(node);
                node = next;
            }
        }

        private static List<UndoEntry> CloneHistory(LinkedList<UndoEntry> history) =>
            history.Select(e => e.PageAnnotations is { } list
                ? e with { PageAnnotations = list.Select(a => a.Clone()).ToList() }
                : e).ToList();

    }
}
