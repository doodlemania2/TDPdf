// MainWindow — keyboard shortcuts.
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
        // Keyboard shortcuts
        // ============================================================

        /// <summary>
        /// #153: the four chords whose key is identified by a PUNCTUATION character — document
        /// zoom in / out (Ctrl) and app-wide chrome size up / down (Ctrl+Shift). A
        /// <c>KeyBinding</c> can only match a virtual key, i.e. a key position, which is a US
        /// layout assumption; these match the character the key actually types on whatever layout
        /// is active. The numpad twins stay as KeyBindings in MainWindow.xaml — the numpad is
        /// layout-independent — and reaching them here first simply runs the same action.
        /// </summary>
        /// <remarks>
        /// The ORDER is load-bearing and must not be rearranged: <see cref="KeyLayout.IsCtrlChar"/>
        /// deliberately ignores Shift (on most layouts Shift is how "+" is produced), so on a US
        /// keyboard Ctrl+Shift+= types "+" and would be swallowed as a zoom-in if the app-size
        /// chords were not tested first.
        /// </remarks>
        private bool TryPunctuationShortcut(KeyEventArgs e)
        {
            if (IsAltGrChord()) return false;   // AltGr types a character; it is never a zoom chord
            var mods = Keyboard.Modifiers;
            const ModifierKeys ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;

            if (KeyLayout.IsCtrlShiftChar(e.Key, '+', '=')
                || ((e.Key == Key.OemPlus || e.Key == Key.Add) && mods == ctrlShift))
            {
                AppScaleUp();
                return true;
            }
            if (KeyLayout.IsCtrlShiftChar(e.Key, '-')
                || ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && mods == ctrlShift))
            {
                AppScaleDown();
                return true;
            }
            if (KeyLayout.IsCtrlChar(e.Key, '+', '=')
                || ((e.Key == Key.OemPlus || e.Key == Key.Add) && mods == ModifierKeys.Control))
            {
                ChangeZoomByCommand(ZoomChange.In);
                return true;
            }
            if (KeyLayout.IsCtrlChar(e.Key, '-')
                || ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && mods == ModifierKeys.Control))
            {
                ChangeZoomByCommand(ZoomChange.Out);
                return true;
            }
            return false;
        }

        /// <summary>
        /// #153: rewrites the shortcut spellings that depend on the keyboard layout. "=" is a plain
        /// keypress on US but needs Shift on German, where "+" is the unshifted one instead — and
        /// since the bindings above accept whichever key TYPES the character, the labels have to
        /// follow suit or the app advertises a chord that does nothing on that machine. Called once
        /// at startup; a layout switched mid-session is rare enough to leave alone.
        /// </summary>
        private void ApplyLayoutShortcutLabels()
        {
            string zin = KeyLayout.ZoomInChar(), zout = KeyLayout.ZoomOutChar();
            if (FindName("ZoomInMenuItem") is MenuItem zoomIn) zoomIn.InputGestureText = $"Ctrl+{zin}";
            if (FindName("AppSizeLargerMenuItem") is MenuItem bigger) bigger.InputGestureText = $"Ctrl+Shift+{zin}";
            if (FindName("AppSizeSmallerMenuItem") is MenuItem smaller) smaller.InputGestureText = $"Ctrl+Shift+{zout}";
            if (FindName("ZoomInButton") is Button zoomInBtn) zoomInBtn.ToolTip = $"Zoom In (Ctrl+{zin})";
            if (FindName("KsAppSizeKeys") is TextBlock appSizeKeys) appSizeKeys.Text = $"Ctrl+Shift+{zin} / {zout}";
            // The rendered keyboard board relabels its own punctuation caps as it is built
            // (KbCapText in KeyboardMapOverlay.cs) — it is created lazily, on first use.
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Alt+Space: the window's system menu, which the custom chrome would otherwise lose.
            // The native frame gets it from Windows; full screen has no title bar to anchor it to.
            if (e.Key == Key.System && e.SystemKey == Key.Space && Keyboard.Modifiers == ModifierKeys.Alt
                && !_useNativeWindowFrame && !_fullScreen)
            {
                e.Handled = true;
                ShowWindowSystemMenuFromKeyboard();
                return;
            }

            // A manual (non-OLE) mouse-captured drag doesn't get Escape-to-cancel for free the way
            // DragDrop.DoDragDrop would — release capture ourselves; LostMouseCapture on the chip
            // does the rest (drops the ghost window, clears drag state, no transfer attempted).
            if (_isDraggingTab && e.Key == Key.Escape)
            {
                Mouse.Capture(null);
                e.Handled = true;
                return;
            }

            // Electronic Signature field placement is a mode the Send dialog lent out; Esc hands it
            // back with the fields as they were (MainWindow.ESignatureFields.cs).
            if (_eSignPlacement is not null && e.Key == Key.Escape)
            {
                EndESignPlacement(ESignPlacementEnd.Cancelled);
                e.Handled = true;
                return;
            }

            // Escape in a form text box cancels that edit before any window-level Esc step-down.
            if (e.Key == Key.Escape && Keyboard.FocusedElement is TextBox formTb && IsFormFieldElement(formTb))
            {
                FormTextBox_PreviewKeyDown(formTb, e);
                return;
            }

            // Escape while carrying a signature drops it (and its pointer ghost).
            if (e.Key == Key.Escape && _pendingSignature is not null)
            {
                CancelSignaturePlacement();
                e.Handled = true;
                return;
            }

            // While the visual keyboard is showing, holding Ctrl / Shift / Alt previews that
            // modifier layer on the board. (upstream KillerPDF v1.6.4)
            KbSyncLayerFromModifiers();

            // #153: the punctuation zoom / app-size chords, matched by the character the key TYPES
            // on the active layout rather than by its US position. Handled here, ABOVE the typing
            // and caret guards, because these were window-level KeyBindings until now and those
            // fired wherever the focus was — moving them into this handler must not quietly
            // narrow that.
            if (TryPunctuationShortcut(e))
            {
                e.Handled = true;
                return;
            }

            // AltGr is reported as Ctrl+Alt. In a text surface it types a character (@ { [ \\ ~ on
            // many layouts), so none of the chords below may see it.
            if (IsAltGrChord() && IsTypingTarget()) return;

            // Don't intercept keys when typing in a TextBox
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;
            // An inline bookmark rename (#133) owns the keyboard - let arrows / Delete / Home / End
            // edit the text rather than page the document or delete the bookmark.
            if (_bmRenaming) return;
            // Any other caret-bearing surface (find bar, sidebar page-jump box, form-field overlay,
            // editable zoom combo) owns its unmodified keys: a bare "d" must type a "d", never swap
            // to the Draw tool, and Delete / arrows / Home / End must edit the text rather than page
            // the document. Escape and the function keys are the deliberate exceptions - they stay
            // global so Esc can still close the find bar or leave full screen from inside it, and
            // F11 / F12 keep working. Modified chords (Ctrl+...) always fall through as before.
            // The check is structural rather than a list of fields, so a text surface added later
            // is covered by default.
            if (IsTypingTarget() && Keyboard.Modifiers == ModifierKeys.None
                && e.Key != Key.Escape && (e.Key < Key.F1 || e.Key > Key.F12))
                return;

            // The Pages panel owns Ctrl+A and Delete while it has keyboard focus (upstream
            // KillerPDF #289, #296). This has to be decided here rather than on the ListBox: this
            // handler is a PREVIEW on the window, so it tunnels down and reaches Ctrl+A first —
            // the list never saw the key, and Ctrl+A in the sidebar selected the document's TEXT
            // instead of its pages. Both keys are structural to the panel, so they are claimed
            // ahead of the general shortcut chain rather than added to it.
            if (_doc is not null && PageList.IsKeyboardFocusWithin
                && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
            {
                if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    PageList.SelectAll();
                    SetStatus($"Selected all {PageList.Items.Count} page(s)");
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None
                    && PageList.SelectedItems.Count > 0)
                {
                    // Same confirmed, reload-backed path as the context menu's Delete Page(s).
                    Delete_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    return;
                }
            }

            // Standard shortcuts (Ctrl+N/O/S/W/Z, Ctrl+Shift+S, Ctrl+P, Ctrl+F, F1, Alt+F/E/V/T/H)
            // are routed via CommandBindings and the Menu's access keys — no need to intercept
            // them here. We still handle the genuinely context-sensitive keys below.

            // An in-progress freeform polygon is the innermost thing Esc can back out of, so it
            // gets first refusal on the key — ahead of full screen, the search bar, the shortcut
            // overlay, and any tool step-down. Backspace walks the vertices back one at a time.
            // Both consume the key only when a polygon is actually being placed.
            if (e.Key == Key.Escape && _polyVertices.Count > 0)
            {
                ResolveShapePolygon(commit: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Back && _polyVertices.Count > 0)
            {
                ShapePolyBackspace();
                e.Handled = true;
                return;
            }

            // Full-screen (F11) and Document Info (F12) toggles. Esc leaves full-screen first,
            // before any other Esc behaviour (search bar / shortcut overlay), so the very first
            // Esc always drops the user back to the normal windowed layout.
            if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && _fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F12)
            {
                ShowDocumentInfoDialog();
                e.Handled = true;
                return;
            }
            // Shift+F4: same file-size flash as clicking the status line (upstream KillerPDF v1.7.2).
            // Bare F4 is left free, and Alt+F4 never reaches here — Alt arrives as Key.System.
            if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ShowCurrentFileSize();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _doc is not null)
            {
                ObserveOcr(OcrPagesToClipboard(SelectedPageIndicesForOcr()));
                e.Handled = true;
            }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                ShortcutOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            // Esc steps DOWN rather than straight out (upstream v1.6.6, adapted): the in-progress
            // polygon, full screen, the find bar and the shortcuts overlay each get first refusal
            // above; with nothing left to cancel, the last step drops back to the Select tool,
            // Acrobat-style. Unlike upstream we stop there - a further Esc does nothing, because
            // TDPdf's Esc has never quit the app and making it do so would be destructive.
            else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None
                     && _doc is not null && _currentTool != EditTool.Select)
            {
                SetTool(EditTool.Select);
                e.Handled = true;
            }
            // #153: matched by the character the key TYPES, not its position. Typing "?" holds
            // Shift on every layout, so the old exact-equality modifier test meant this only ever
            // fired for Ctrl+/ — even on a US keyboard. The positional check is kept as a fast
            // path (and as the Ctrl+/ spelling) for where it already worked.
            else if (KeyLayout.IsCtrlChar(e.Key, '?')
                     || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control))
            {
                ShortcutOverlay.Visibility = ShortcutOverlay.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;
                if (ShortcutOverlay.Visibility == Visibility.Visible) ApplyPersistedShortcutView();
                e.Handled = true;
            }
            // Ahead of the annotation case: with the Form tool out there is no selected annotation
            // to compete with, and Delete has to mean "remove the field I just clicked".
            else if (e.Key == Key.Delete && TryDeleteSelectedFormField())
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && _selectedAnnotation is not null)
            {
                DeleteSelected();
                e.Handled = true;
            }
            // Home / End jump to the first / last page (the Acrobat / Sumatra convention). Recorded on
            // the jump history so Alt+Left retraces the hop. (Upstream v1.6.4)
            else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.None && _doc is not null)
            {
                RecordNavJump();
                PageList.SelectedIndex = _doc.PageCount - 1;
                e.Handled = true;
            }
            // Ctrl+1 / Ctrl+2 / Ctrl+3 = actual size / fit width / fit page (Acrobat/Foxit). Ctrl+0
            // stays the existing 100% reset. (Upstream v1.6.4)
            else if (e.Key == Key.D1 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                BeginManualZoom();
                // Actual size — a true 100% in every view mode, because Zoom.ZoomLevel is true
                // zoom and MainWindow converts to the tile's layout scale (DisplayZoomFactor).
                Zoom.SetZoomLevel(1.0);
                e.Handled = true;
            }
            // Ctrl+2 / Ctrl+3 are an explicit fit choice, so they are remembered for the next
            // document opened (upstream v1.7.1).
            else if (e.Key == Key.D2 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                SaveDefaultFitMode(ZoomFitMode.Width);
                FitToWidth();
                e.Handled = true;
            }
            else if (e.Key == Key.D3 && Keyboard.Modifiers == ModifierKeys.Control && _doc is not null)
            {
                SaveDefaultFitMode(ZoomFitMode.Page);
                FitToPage();
                e.Handled = true;
            }
            // Ctrl+R / Ctrl+Shift+R rotate the selected pages clockwise / counter-clockwise
            // (upstream v1.8.5). Rotation was reachable from the toolbar and the Pages panel but had
            // no key of its own; bare R is the Redact tool, so the modified pair is free.
            // RotatePages_Click already works off PageList.SelectedItems and handles its own errors.
            else if (e.Key == Key.R && _doc is not null
                     && (Keyboard.Modifiers == ModifierKeys.Control
                         || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            {
                RotatePages_Click(Keyboard.Modifiers == ModifierKeys.Control ? 90 : -90);
                e.Handled = true;
            }
            // Jump history: Alt+Left / Alt+Right retrace bookmark / link / jump-box / Home-End hops,
            // browser-style. Alt makes the key arrive as Key.System with the real key in SystemKey.
            else if (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: true);
                e.Handled = true;
            }
            else if (e.Key == Key.System && e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                NavHistoryGo(back: false);
                e.Handled = true;
            }
            // Menu key / Shift+F10 opens the right-click menu at the current selection (Windows
            // keyboard-accessibility convention). (Upstream v1.6.4)
            else if ((e.Key == Key.Apps
                      || (e.Key == Key.System && e.SystemKey == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
                     && _doc is not null)
            {
                OpenContextMenuAtSelection();
                e.Handled = true;
            }
            // Arrow keys and PgUp/PgDn navigate to the previous / next page. Handled here at the window
            // (Preview) level with e.Handled so paging works the same whether the page canvas or a sidebar
            // thumbnail has focus — otherwise a focused PageList would page its own selection instead. PgUp/
            // PgDn never reorder pages (that stays on the toolbar Move Up / Down buttons).
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null && PageList.Items.Count > 1
                     && (e.Key == Key.Left || e.Key == Key.Up || e.Key == Key.Right || e.Key == Key.Down
                         || e.Key == Key.PageUp || e.Key == Key.PageDown))
            {
                int dir = (e.Key == Key.Left || e.Key == Key.Up || e.Key == Key.PageUp) ? -1 : 1;
                if (NavigatePageStep(dir))   // one spread at a time in Two-Page mode (#120)
                    PageList.ScrollIntoView(PageList.SelectedItem);
                e.Handled = true;
            }
            // Shift+N pairs with Ctrl+I: it toggles whether night mode inverts PICTURES too — the
            // option on the moon's right-click menu, and the thing that makes a scanned page (one
            // full-page image) usable in night mode. TDPdf keeps Ctrl+I for the mode itself rather
            // than taking upstream's bare N, because our single-letter keys are all tool shortcuts.
            // Shift+N is free: TrySelectToolByKey is gated on ModifierKeys.None, and nothing else
            // binds a Shift+letter. The typing guard above only returns for unmodified keys, so the
            // caret check is repeated here — a capital N in the find box must type an N.
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Shift && _doc is not null
                     && !IsTypingTarget()
                     && ShortcutOverlay.Visibility != Visibility.Visible)
            {
                ToggleDocInvertImages(!_docInvertImages);
                e.Handled = true;
            }
            // B toggles the Two-Page book layout (#193, upstream binds the same bare key). Verified
            // free against TrySelectToolByKey and every other branch in this handler before taking
            // it — our single-key space is dense (V, P, digits, T/X/I/H/K/U/S/D/E/G/C) but B was
            // unclaimed. Same shape as the Shift+N row above: gated on a document being open, the
            // shortcut overlay being closed, and the caret not sitting in a text surface (the
            // unmodified-key typing guard at the top of this method already returned in that case,
            // but the check is repeated so the branch stands on its own).
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && !IsTypingTarget()
                     && Keyboard.FocusedElement is not ComboBox
                     && ShortcutOverlay.Visibility != Visibility.Visible)
            {
                ToggleTwoPageBook(!_twoPageBook);
                e.Handled = true;
            }
            // Unmodified tool keys, last so every context-sensitive key above keeps priority.
            // Only while a document is open and no overlay owns the keyboard; the typing guard at
            // the top of this method already made sure the caret isn't in a text surface.
            // A focused drop-down (the zoom combo, a PDF choice field) is excluded on top of that:
            // it isn't a caret surface, but WPF gives it letter type-ahead, and jumping to an option
            // by typing must keep working.
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && Keyboard.FocusedElement is not ComboBox
                     && TrySelectToolByKey(e.Key))
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// TDPdf's single-key tool map. Adapted from upstream KillerPDF v1.6.6, not copied: our tool
        /// set differs (no Stamp, Line is a Shape sub-mode, Transform is a dialog, and we add Pan,
        /// Erase, Edit Text, Edit Image, Strikethrough and Underline), so only the *principles*
        /// carry over — V for Select (the Photoshop / Illustrator / Figma convention) and digits
        /// that mirror the toolbar left to right.
        ///
        /// The toolbar runs Select, Pan, Text, Edit Text, Edit Image, Insert Image, Highlight,
        /// Strikethrough, Underline, Shape, Draw, Erase | Signature, Crop. The ten mark-making tools
        /// between Pan and that separator take 1-9 then 0, in exactly that order; the two navigation
        /// modes before them (Select, Pan) and the two tools past the separator (Signature, Crop)
        /// are letter-only, so the digit run has a principled start and end instead of stopping
        /// arbitrarily. Mnemonic letters double up wherever one is free. Number-row and numpad
        /// digits both map.
        ///
        /// Returns true when the key selected a tool, so the caller can mark the event handled.
        /// </summary>
        private bool TrySelectToolByKey(Key key)
        {
            switch (key)
            {
                case Key.V: SetTool(EditTool.Select); return true;
                case Key.P: SetTool(EditTool.Pan); return true;
                case Key.T: case Key.D1: case Key.NumPad1: SetTool(EditTool.Text); return true;
                case Key.X: case Key.D2: case Key.NumPad2: SetTool(EditTool.EditText); return true;
                case Key.D3: case Key.NumPad3: SetTool(EditTool.EditImage); return true;
                case Key.I: case Key.D4: case Key.NumPad4: SetTool(EditTool.Image); return true;
                case Key.H: case Key.D5: case Key.NumPad5: SetTool(EditTool.Highlight); return true;
                case Key.K: case Key.D6: case Key.NumPad6: SetTool(EditTool.Strikethrough); return true;
                case Key.U: case Key.D7: case Key.NumPad7: SetTool(EditTool.Underline); return true;
                case Key.S: case Key.D8: case Key.NumPad8: SetTool(EditTool.Shape); return true;
                case Key.D: case Key.D9: case Key.NumPad9: SetTool(EditTool.Draw); return true;
                case Key.E: case Key.D0: case Key.NumPad0: SetTool(EditTool.Erase); return true;
                // Signature routes through the button handler so the saved-signature popup opens,
                // exactly as clicking the toolbar button does.
                case Key.G: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: ToolCrop_Click(this, new RoutedEventArgs()); return true;
                case Key.R: SetTool(EditTool.Redact); return true;
                case Key.F: SetTool(EditTool.Form); return true;
                // M for Measure. Verified free before taking it: no other `case Key.M` exists in
                // the app, and the only M binding anywhere is the Merge button's Alt+M ACCESS key,
                // which cannot collide because this method is only ever reached with
                // ModifierKeys.None. Mirrored in KeyboardMapOverlay's KbMap and in the LIST view's
                // TOOLS section in MainWindow.xaml — those two drifted apart once and it was
                // reported as a bug, so a new key goes into all three or none.
                case Key.M: SetTool(EditTool.Measure); return true;
                default: return false;
            }
        }

        /// <summary>
        /// True when the keyboard focus is sitting in a text-entry surface, so the unmodified keys
        /// belong to whatever is being typed. Covers every typing target hosted by the main window:
        /// the annotation text box and the inline PDF-text editor (both also tracked by
        /// _activeTextBox), the find bar's search box, the sidebar page-jump box, the bookmark
        /// inline-rename box (also tracked by _bmRenaming), the interactive form-field overlays, and
        /// the editable zoom combo. Checked by control type rather than by field identity so a new
        /// text surface is protected the moment it exists.
        /// </summary>
        /// <summary>
        /// True for AltGr: Windows reports it as Ctrl+Alt, and while the synthetic Ctrl half is the
        /// only modifier WPF has registered the right Alt key is still physically down.
        /// </summary>
        private static bool IsAltGrChord()
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return false;
            return (mods & ModifierKeys.Alt) != 0 || Keyboard.IsKeyDown(Key.RightAlt);
        }

        private static bool IsTypingTarget() =>
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.PasswordBox
            || Keyboard.FocusedElement is ComboBox { IsEditable: true };
    }
}
