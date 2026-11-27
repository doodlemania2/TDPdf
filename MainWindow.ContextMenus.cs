// MainWindow — the right-click menus (#237).
//
// Every right-click menu is built for what is under the pointer: the model in
// Services/ContextMenuModel decides which commands a context offers, in which groups and whether
// each can run; this file turns that plan into WPF rows and connects each command id to the code
// that already does it. Nothing here changes what a command does, only where and how it is offered.
//
// A menu is: an optional row of quick-action tiles, optional colour / opacity / width / markup rows
// for a mark, then the command list in the user's order (Services/MenuLayouts), then "Edit Menu…".
// The controls sit in MenuItem rows that stay open when used (style MenuControlRow); Left and Right
// move between the controls in a row and Enter or Space uses one, so the keyboard needs nothing else.

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>Maps a command id to the code that runs it; null when this menu has no such command.</summary>
        private delegate RoutedEventHandler? MenuRunner(string id);

        /// <summary>True while the menu was opened from the keyboard (Menu key, Shift+F10), so it describes the selection, not the pointer.</summary>
        private bool _ctxFromKeyboard;

        // ============================================================
        // Building any menu from the model
        // ============================================================

        private static string PlainName(string header) => header.Replace("_", string.Empty);

        /// <summary>Fills <paramref name="menu"/> for a context. Replaces whatever the menu held.</summary>
        private void FillMenu(ContextMenu menu, ContextMenuContext ctx, MenuRunner run, Action<ContextMenu>? footer = null)
        {
            menu.Items.Clear();
            var plan = ContextMenuModel.Plan(ctx);
            var layouts = MenuLayouts.Parse(TDPdf.Properties.Settings.Default.MenuLayouts);
            layouts.TryGetValue(ctx.Kind.ToString(), out var saved);
            plan = ContextMenuModel.Arrange(plan, MenuLayouts.Resolve(ContextMenuModel.ListIds(ctx.Kind), saved));

            bool any = false;
            if (plan.Quick.Count > 0)
            {
                menu.Items.Add(BuildQuickRow(menu, plan.Quick, run));
                any = true;
            }
            if (plan.Controls != MenuStyleControls.None)
                any |= AddMarkStyleRows(menu, plan.Controls);

            foreach (var group in plan.Groups)
            {
                if (group.Items.Count == 0) continue;
                if (any) menu.Items.Add(new Separator());
                foreach (var entry in group.Items)
                    menu.Items.Add(MakeEntryItem(entry, run, ctx));
                any = true;
            }

            footer?.Invoke(menu);

            if (ContextMenuModel.IsCustomisable(ctx.Kind))
            {
                var info = ContextMenuModel.Info(MenuId.EditMenu);
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(info.Header,
                    (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => EditMenuLayout(ctx.Kind)),
                    null, info.Help, info.Glyph));
            }
        }

        private MenuItem MakeEntryItem(MenuEntry entry, MenuRunner run, ContextMenuContext ctx)
        {
            var info = ContextMenuModel.Info(entry.Id);
            MenuItem item;
            if (entry.Id == MenuId.Arrange)
                item = BuildArrangeSubmenu(ctx.MarkCount);
            else
                item = MakeMenuItem(entry.Header, run(entry.Id) ?? ((_, _) => { }), info.Gesture, info.Help, info.Glyph, info.MirrorGlyph);
            if (!entry.Enabled) item.IsEnabled = false;   // never re-enable what the read-only gate switched off
            if (entry.Checked) item.IsChecked = true;
            return item;
        }

        // ============================================================
        // Quick-action row
        // ============================================================

        private MenuItem BuildQuickRow(ContextMenu menu, IReadOnlyList<MenuEntry> quick, MenuRunner run)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var tiles = new List<Button>();
            var ids = new List<string>();

            foreach (var entry in quick)
            {
                var info = ContextMenuModel.Info(entry.Id);
                string name = PlainName(entry.Header);
                var content = new StackPanel { Orientation = Orientation.Vertical, MinWidth = 46 };
                if (info.Glyph is { Length: > 0 } glyph)
                {
                    var g = MakeMenuGlyph(glyph, info.MirrorGlyph);
                    g.FontSize = 16;
                    g.Margin = new Thickness(0, 0, 0, 2);
                    content.Children.Add(g);
                }
                var caption = new TextBlock
                {
                    Text = info.Short ?? name,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                content.Children.Add(caption);

                var tile = new Button
                {
                    Style = (Style)FindResource("MenuTile"),
                    Content = content,
                    Focusable = false,
                    IsTabStop = false,
                    IsEnabled = entry.Enabled,
                    Margin = new Thickness(1, 0, 1, 0),
                    ToolTip = info.Gesture is { Length: > 0 } g2 ? $"{name} ({g2})" : name,
                };
                AutomationProperties.SetName(tile, name);
                AutomationProperties.SetHelpText(tile, info.Help ?? name);
                if (info.Gesture is { Length: > 0 } accel) AutomationProperties.SetAcceleratorKey(tile, accel);
                string id = entry.Id;
                tile.Click += (_, _) => RunQuick(menu, run, id, tile);
                strip.Children.Add(tile);
                tiles.Add(tile);
                ids.Add(id);
            }

            var row = new MenuItem
            {
                Style = (Style)FindResource("MenuControlRow"),
                Header = strip,
            };
            AutomationProperties.SetName(row, "Quick actions");
            WireRowKeys(row, tiles, i => RunQuick(menu, run, ids[i], tiles[i]));
            return row;
        }

        /// <summary>Closes the menu, then runs the command once focus is back where it was.</summary>
        private void RunQuick(ContextMenu menu, MenuRunner run, string id, object sender)
        {
            var handler = run(id);
            menu.IsOpen = false;
            if (handler is null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => handler(sender, new RoutedEventArgs()));
        }

        /// <summary>
        /// Keyboard use of a row of controls inside a menu item: Left and Right move a highlight between
        /// the enabled controls, Enter and Space use the highlighted one. Up and Down are left to the
        /// menu, which moves to the next row.
        /// </summary>
        private static void WireRowKeys(MenuItem row, IReadOnlyList<Control> stops, Action<int> activate)
        {
            int active = -1;
            void Mark(int index)
            {
                active = index;
                for (int i = 0; i < stops.Count; i++) stops[i].Tag = i == index ? "active" : null;
            }
            int Step(int from, int dir)
            {
                for (int n = 1; n <= stops.Count; n++)
                {
                    int i = ((from + dir * n) % stops.Count + stops.Count) % stops.Count;
                    if (stops[i].IsEnabled) return i;
                }
                return -1;
            }
            row.GotKeyboardFocus += (_, _) =>
            {
                if (active < 0) Mark(Step(-1, 1));
            };
            row.LostKeyboardFocus += (_, e) =>
            {
                if (e.NewFocus is DependencyObject d && ReferenceEquals(d, row)) return;
                Mark(-1);
            };
            row.PreviewKeyDown += (_, e) =>
            {
                if (stops.Count == 0) return;
                switch (e.Key)
                {
                    case Key.Left: { int n = Step(active < 0 ? 0 : active, -1); if (n >= 0) Mark(n); e.Handled = true; break; }
                    case Key.Right: { int n = Step(active < 0 ? stops.Count - 1 : active, 1); if (n >= 0) Mark(n); e.Handled = true; break; }
                    case Key.Home: { int n = Step(-1, 1); if (n >= 0) Mark(n); e.Handled = true; break; }
                    case Key.End: { int n = Step(stops.Count, -1); if (n >= 0) Mark(n); e.Handled = true; break; }
                    case Key.Enter:
                    case Key.Space:
                        if (active >= 0 && stops[active].IsEnabled) activate(active);
                        e.Handled = true;
                        break;
                }
            };
        }

        // ============================================================
        // Colour, opacity, width and markup rows for a mark
        // ============================================================

        private static readonly string[] SwatchNames =
            { "Red", "Brown", "Orange", "Gold", "Green", "Blue", "Purple", "Pink", "White", "Black" };

        private static Color? MenuMarkColor(PageAnnotation a) => a switch
        {
            InkAnnotation i => i.GetColor(),
            HighlightAnnotation h => h.GetColor(),
            ShapeAnnotation s => s.GetStrokeColor(),
            _ => null,
        };

        private static byte? MenuMarkOpacity(PageAnnotation a) => MenuMarkColor(a)?.A;

        private static double? MenuMarkWidth(PageAnnotation a) => a switch
        {
            InkAnnotation i => i.StrokeWidth,
            ShapeAnnotation s => s.StrokeWidth,
            _ => null,
        };

        private static TextBlock MenuRowLabel(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 11, MinWidth = 52, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return tb;
        }

        /// <summary>
        /// One change from a menu row. It goes through RestyleLive like every style-bar control, so a
        /// run of changes (a slider drag, several swatches) joins one undo step and a restyle of several
        /// marks reaches all of them.
        /// </summary>
        private void MenuRestyle(Action<PageAnnotation> change)
        {
            if (_styleTarget is not { } target) return;
            change(target);
            RestyleLive(target);
        }

        private bool AddMarkStyleRows(ContextMenu menu, MenuStyleControls controls)
        {
            if (_styleTarget is not { } target || !_markSelection.Contains(target)) return false;
            bool added = false;

            if (controls.HasFlag(MenuStyleControls.MarkupSwitch) && target is MarkupAnnotation mk)
            {
                menu.Items.Add(BuildMarkupSwitchRow(mk));
                added = true;
            }

            if (controls.HasFlag(MenuStyleControls.Swatches))
            {
                menu.Items.Add(BuildSwatchRow(target));
                added = true;
            }

            if (controls.HasFlag(MenuStyleControls.Opacity) && MenuMarkOpacity(target) is { } alpha)
            {
                string label = target is ShapeAnnotation ? "Line opacity" : "Opacity";
                menu.Items.Add(BuildMenuSliderRow(label, 10, 255, alpha, 5, v => $"{(int)Math.Round(v / 255.0 * 100)}%",
                    v => MenuRestyle(t =>
                    {
                        byte a = (byte)Math.Clamp((int)Math.Round(v), 10, 255);
                        switch (t)
                        {
                            case InkAnnotation ink: ink.ColorA = a; break;
                            case HighlightAnnotation hl: hl.ColorA = a; break;
                            case ShapeAnnotation sh: sh.StrokeA = a; break;
                        }
                    })));
                added = true;
            }

            if (controls.HasFlag(MenuStyleControls.Width) && MenuMarkWidth(target) is { } width)
            {
                menu.Items.Add(BuildMenuSliderRow("Width", 1, 20, Math.Clamp(width, 1, 20), 1, v => $"{v:F0} px",
                    v => MenuRestyle(t =>
                    {
                        switch (t)
                        {
                            case InkAnnotation ink: ink.StrokeWidth = Math.Round(v); break;
                            case ShapeAnnotation sh: sh.StrokeWidth = Math.Round(v); break;
                        }
                    })));
                added = true;
            }
            return added;
        }

        private MenuItem BuildSwatchRow(PageAnnotation target)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MenuRowLabel("Colour"));
            var current = MenuMarkColor(target);
            var swatches = new List<RadioButton>();
            for (int i = 0; i < SwatchColors.Length; i++)
            {
                var c = SwatchColors[i];
                var sw = new RadioButton
                {
                    Style = (Style)FindResource("MenuSwatch"),
                    Background = TdpDialog.FrozenSolidColorBrush(c),
                    GroupName = "menuColour",
                    Focusable = false,
                    IsTabStop = false,
                    IsChecked = current is { } cur && cur.R == c.R && cur.G == c.G && cur.B == c.B,
                    ToolTip = SwatchNames[i],
                };
                AutomationProperties.SetName(sw, SwatchNames[i]);
                sw.Click += (_, _) => ApplyMenuColour(c);
                swatches.Add(sw);
                panel.Children.Add(sw);
            }

            var row = new MenuItem { Style = (Style)FindResource("MenuControlRow"), Header = panel };
            AutomationProperties.SetName(row, "Colour");
            WireRowKeys(row, swatches, i =>
            {
                swatches[i].IsChecked = true;
                ApplyMenuColour(SwatchColors[i]);
            });
            return row;
        }

        private void ApplyMenuColour(Color c) => MenuRestyle(t =>
        {
            switch (t)
            {
                case InkAnnotation ink: ink.SetColor(Color.FromArgb(ink.ColorA, c.R, c.G, c.B)); break;
                case HighlightAnnotation hl: hl.SetColor(Color.FromArgb(hl.ColorA, c.R, c.G, c.B)); break;
                case ShapeAnnotation sh: sh.SetStrokeColor(Color.FromArgb(sh.StrokeA, c.R, c.G, c.B)); break;
            }
        });

        private MenuItem BuildMenuSliderRow(string label, double min, double max, double value, double keyStep,
                                        Func<double, string> format, Action<double> apply)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = value,
                Width = 130, VerticalAlignment = VerticalAlignment.Center,
                Focusable = false, IsTabStop = false,
                SmallChange = keyStep, LargeChange = keyStep * 4,
            };
            AutomationProperties.SetName(slider, label);
            var readout = new TextBlock { Text = format(value), FontSize = 11, MinWidth = 40, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            readout.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            slider.ValueChanged += (_, e) =>
            {
                readout.Text = format(e.NewValue);
                apply(e.NewValue);
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MenuRowLabel(label));
            panel.Children.Add(slider);
            panel.Children.Add(readout);
            var row = new MenuItem { Style = (Style)FindResource("MenuControlRow"), Header = panel };
            AutomationProperties.SetName(row, label);
            row.PreviewKeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Left: slider.Value = Math.Max(slider.Minimum, slider.Value - keyStep); e.Handled = true; break;
                    case Key.Right: slider.Value = Math.Min(slider.Maximum, slider.Value + keyStep); e.Handled = true; break;
                    case Key.Home: slider.Value = slider.Minimum; e.Handled = true; break;
                    case Key.End: slider.Value = slider.Maximum; e.Handled = true; break;
                }
            };
            return row;
        }

        private MenuItem BuildMarkupSwitchRow(MarkupAnnotation mk)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(MenuRowLabel("Style"));
            var options = new (MarkupStyle Style, string Name)[]
            {
                (MarkupStyle.Highlight, "Highlight"), (MarkupStyle.Underline, "Underline"), (MarkupStyle.Strikethrough, "Strikethrough"),
            };
            var buttons = new List<Button>();
            void Refresh()
            {
                for (int i = 0; i < buttons.Count; i++)
                    buttons[i].FontWeight = options[i].Style == mk.Style ? FontWeights.Bold : FontWeights.Normal;
            }
            for (int i = 0; i < options.Length; i++)
            {
                var (style, name) = options[i];
                var b = new Button
                {
                    Style = (Style)FindResource("MenuTile"),
                    Content = name,
                    FontSize = 11,
                    Focusable = false, IsTabStop = false,
                    Margin = new Thickness(1, 0, 1, 0),
                    ToolTip = $"Change this to {name.ToLowerInvariant()}",
                };
                AutomationProperties.SetName(b, name);
                b.Click += (_, _) => { SwitchMarkupStyle(mk, style); Refresh(); };
                buttons.Add(b);
                panel.Children.Add(b);
            }
            Refresh();
            var row = new MenuItem { Style = (Style)FindResource("MenuControlRow"), Header = panel };
            AutomationProperties.SetName(row, "Markup style");
            WireRowKeys(row, buttons, i => { SwitchMarkupStyle(mk, options[i].Style); Refresh(); });
            return row;
        }

        /// <summary>
        /// Turns a highlight into an underline or a strikethrough (or back) in place. A highlight is a
        /// translucent wash and a line is solid, so the opacity follows the new style's own default.
        /// </summary>
        private void SwitchMarkupStyle(MarkupAnnotation mk, MarkupStyle style)
        {
            if (mk.Style == style) return;
            MenuRestyle(t =>
            {
                if (t is not MarkupAnnotation m) return;
                bool wasHighlight = m.Style == MarkupStyle.Highlight;
                m.Style = style;
                if (wasHighlight != (style == MarkupStyle.Highlight))
                    m.ColorA = style == MarkupStyle.Highlight ? _highlightColor.A : _markupLineColor.A;
            });
            ShowStyleBarForSelection(mk);
        }

        // ============================================================
        // Raise and lower
        // ============================================================

        /// <summary>
        /// Highlights are multiplied into the page picture on screen, under every other mark, so
        /// raising or lowering a selection of only highlights would change nothing you can see.
        /// </summary>
        private bool MarksCanChangeOrder() => _markSelection.Any(m => !IsMultiplied(m));

        private void MoveSelectedMarks(int direction)
        {
            if (_markSelection.Count == 0) return;
            int page = _markSelection[0].PageIndex;
            if (!_annotations.TryGetValue(page, out var list)) return;
            var selected = _markSelection.ToList();
            Rect union = Rect.Empty;
            foreach (var m in selected) union = Rect.Union(union, MarkBounds(m));

            PushPageSnapshot(page);
            bool moved = MarkOrder.Move(list, selected, direction, other =>
            {
                var b = MarkBounds(other);
                return !union.IsEmpty && b.IntersectsWith(union);
            });
            if (!moved)
            {
                DropTopSnapshotIfFor(page);
                SetStatus(direction > 0 ? "Already on top" : "Already at the bottom");
                return;
            }
            MarkDirty();
            RenderAllAnnotations(page);
            SelectMarks(selected);
            SetStatus(direction > 0 ? "Raised" : "Lowered");
        }

        private void EditSelectedMark()
        {
            if (_markSelection.Count != 1) return;
            var mark = _markSelection[0];
            int page = mark.PageIndex;
            CommitActiveTextBox();
            switch (mark)
            {
                case TextAnnotation ta:
                    ClearSelection();
                    PlaceTextBox(ta.Position, page, ta);
                    break;
                case TextEditAnnotation tea:
                    var b = MarkBounds(tea);
                    ClearSelection();
                    EditTextAtPosition(new Point(b.X + b.Width / 2, b.Y + b.Height / 2), page);
                    break;
            }
        }

        // ============================================================
        // The page (canvas) menu
        // ============================================================

        private static MenuMarkKind MenuKindOf(PageAnnotation a) => a switch
        {
            MarkupAnnotation mk => mk.Style switch
            {
                MarkupStyle.Underline => MenuMarkKind.Underline,
                MarkupStyle.Strikethrough => MenuMarkKind.Strikethrough,
                _ => MenuMarkKind.MarkupHighlight,
            },
            HighlightAnnotation => MenuMarkKind.Highlight,
            InkAnnotation => MenuMarkKind.Drawing,
            ShapeAnnotation => MenuMarkKind.Shape,
            TextAnnotation => MenuMarkKind.TextBox,
            TextEditAnnotation => MenuMarkKind.EditedText,
            SignatureAnnotation => MenuMarkKind.Signature,
            StampAnnotation => MenuMarkKind.Stamp,
            ImageAnnotation => MenuMarkKind.Picture,
            _ => MenuMarkKind.None,
        };

        private bool HasTextSelectionNow => _currentTool == EditTool.Select && _txtSelAnchor != _txtSelFocus;

        /// <summary>What the canvas menu is about right now: the selection when opened by key, else what the pointer is on.</summary>
        private ContextMenuContext BuildCanvasContext()
        {
            ContextMenuKind kind;
            int page = PageList.SelectedIndex;
            bool onSelection = _ctxFromKeyboard
                || (_ctxClickPos is { } p && page >= 0 && TopMarkAt(page, p) is { } hit && _markSelection.Contains(hit));

            if (HasPictureSelection) kind = ContextMenuKind.Picture;
            else if (_markSelection.Count > 1 && onSelection) kind = ContextMenuKind.Marks;
            else if (_markSelection.Count == 1 && onSelection) kind = ContextMenuKind.Mark;
            else if (_currentTool == EditTool.Form && _selectedFormWidget is not null) kind = ContextMenuKind.FormField;
            else if (HasTextSelectionNow) kind = ContextMenuKind.Text;
            else kind = ContextMenuKind.Page;

            bool marks = kind is ContextMenuKind.Mark or ContextMenuKind.Marks;
            bool canRaise = false, canLower = false;
            if (marks && MarksCanChangeOrder() && _annotations.TryGetValue(_markSelection[0].PageIndex, out var list))
            {
                var indices = _markSelection.Select(m => list.IndexOf(m)).Where(i => i >= 0).ToList();
                canRaise = ContextMenuModel.CanMoveMarks(list.Count, indices, +1);
                canLower = ContextMenuModel.CanMoveMarks(list.Count, indices, -1);
            }

            return new ContextMenuContext
            {
                Kind = kind,
                MarkKinds = marks ? _markSelection.Select(MenuKindOf).ToList() : Array.Empty<MenuMarkKind>(),
                ReadOnly = ReadOnlyGate.ProcessIsReadOnly,
                CanPaste = CanPasteNow(),
                HasGroup = marks && SelectionIsGrouped,
                CanRaise = canRaise,
                CanLower = canLower,
                HasBoxSelection = _boxSelection is not null && _selectRect is not null,
                PagesSelected = PageList.SelectedItems.Count,
            };
        }

        private RoutedEventHandler? CanvasCommand(string id) => id switch
        {
            MenuId.Cut => (_, _) => CutMarks(),
            MenuId.Copy => (_, _) => CopyMarksOrText(),
            MenuId.Paste => (_, _) => PasteFromClipboard(),
            MenuId.Delete => (_, _) => DeleteSelected(),
            MenuId.Group => (_, _) => GroupSelected(),
            MenuId.Ungroup => (_, _) => UngroupSelected(),
            MenuId.Raise => (_, _) => MoveSelectedMarks(+1),
            MenuId.Lower => (_, _) => MoveSelectedMarks(-1),
            MenuId.Edit => (_, _) => EditSelectedMark(),
            MenuId.SavePicture => (_, _) => SavePictureAs(),
            MenuId.ReplacePicture => (_, _) => ReplacePicture(),
            MenuId.ExtractPicture => (_, _) => ExtractPictureImage(),
            MenuId.EditParagraph => (_, _) => EditParagraphFromMenu(),
            MenuId.Ocr => (_, _) => ObserveOcr(OcrPagesToClipboard(SelectedPageIndicesForOcr())),
            MenuId.Rasterize => (_, _) => BeginRasterizeRegion(),
            MenuId.Print => (s, e) => Print_Click(s, e),
            MenuId.Undo => (s, e) => Undo_Click(s, e),
            MenuId.ClearPage => (s, e) => ClearAnnotations_Click(s, e),
            MenuId.ToolSelect => (_, _) => SetTool(EditTool.Select),
            MenuId.ToolText => (_, _) => SetTool(EditTool.Text),
            MenuId.ToolEditText => (_, _) => SetTool(EditTool.EditText),
            MenuId.ToolEditPicture => (_, _) => SetTool(EditTool.EditImage),
            MenuId.ToolHighlight => (_, _) => SetTool(EditTool.Highlight),
            MenuId.ToolDraw => (_, _) => SetTool(EditTool.Draw),
            MenuId.ToolCrop => (_, _) => SetTool(EditTool.Crop),
            MenuId.ToolRedact => (_, _) => SetTool(EditTool.Redact),
            MenuId.RenameField => (_, _) => RenameSelectedFormField(),
            MenuId.DeleteField => (_, _) => DeleteSelectedFormField(),
            _ => null,
        };

        /// <summary>
        /// Wires the page's right-click menu. It is rebuilt every time it opens, for what the pointer
        /// is on, so one ContextMenu object is reused and refilled.
        /// </summary>
        private void BuildContextMenu()
        {
            _annotationCanvas.ContextMenu = new ContextMenu();
            _annotationCanvas.ContextMenuOpening += (_, e) =>
            {
                _ctxFromKeyboard = e.CursorLeft < 0;
                RefillCanvasMenu();
            };
        }

        private void RefillCanvasMenu()
        {
            if (_annotationCanvas.ContextMenu is not ContextMenu cm) return;
            FillMenu(cm, BuildCanvasContext(), CanvasCommand);
        }

        // Keyboard access to the right-click menu (Menu key / Shift+F10): opens the menu for the
        // selected mark, just under it, or centred on the page when nothing is selected. Placement is
        // changed just for this open and restored on close, so the mouse path keeps opening at the cursor.
        private void OpenContextMenuAtSelection()
        {
            if (_doc is null || _annotationCanvas.ContextMenu is not ContextMenu cm) return;
            _ctxFromKeyboard = true;
            Rect union = Rect.Empty;
            foreach (var m in _markSelection) union = Rect.Union(union, MarkBounds(m));
            if (!union.IsEmpty) _ctxClickPos = new Point(union.X + union.Width / 2, union.Y + union.Height / 2);
            RefillCanvasMenu();

            cm.PlacementTarget = _annotationCanvas;
            var prevPlacement = cm.Placement;
            var prevH = cm.HorizontalOffset;
            var prevV = cm.VerticalOffset;
            if (union.IsEmpty)
                cm.Placement = PlacementMode.Center;
            else
            {
                cm.Placement = PlacementMode.Relative;
                cm.HorizontalOffset = union.X;
                cm.VerticalOffset = union.Bottom;
            }
            void Restore(object? s, RoutedEventArgs a)
            {
                cm.Placement = prevPlacement;
                cm.HorizontalOffset = prevH;
                cm.VerticalOffset = prevV;
                _ctxFromKeyboard = false;
                cm.Closed -= Restore;
            }
            cm.Closed += Restore;
            cm.IsOpen = true;
        }

        // ============================================================
        // Thumbnails, the Rotate button, tabs, bookmarks, links, picture edits
        // ============================================================

        private ContextMenuContext BuildPageListContext(ContextMenuKind kind)
        {
            int[] selected = SelectedPageIndices();
            bool block = selected.Length > 1 && selected[^1] - selected[0] == selected.Length - 1;
            return new ContextMenuContext
            {
                Kind = kind,
                ReadOnly = ReadOnlyGate.ProcessIsReadOnly,
                PagesSelected = selected.Length,
                PagesMoveAsBlock = block,
                TwoColumnThumbnails = _pairedSidebar,
            };
        }

        private RoutedEventHandler? PageCommand(string id) => id switch
        {
            MenuId.InsertBlank => (s, e) => InsertBlankPage_Click(s, e),
            MenuId.Duplicate => (_, _) => DuplicatePages_Click(),
            MenuId.RotateCw => (_, _) => RotatePages_Click(90),
            MenuId.RotateCcw => (_, _) => RotatePages_Click(-90),
            MenuId.Rotate180 => (_, _) => RotatePages_Click(180),
            MenuId.Transform => (s, e) => ToolTransform_Click(s, e),
            MenuId.MoveUp => (s, e) => MoveUp_Click(s, e),
            MenuId.MoveDown => (s, e) => MoveDown_Click(s, e),
            MenuId.ExtractPages => (s, e) => Split_Click(s, e),
            MenuId.DeletePages => (s, e) => Delete_Click(s, e),
            MenuId.TwoColumn => (s, e) => PairedSidebar_Click(s, e),
            _ => null,
        };

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            FillMenu(menu, BuildPageListContext(ContextMenuKind.Thumbnail), PageCommand);
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        // Rotate was previously reachable only via right-click on a page thumbnail in the Pages
        // panel, reported back as "the rotate command is missing" when someone went looking for it
        // on the toolbar instead. Left-click here does the common case (clockwise); right-click
        // opens the same turn buttons the page-list menu offers, so the two entry points stay in sync.
        private void RotateBtn_Click(object sender, RoutedEventArgs e) => RotatePages_Click(90);

        private void RotateBtn_RightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var menu = new ContextMenu();
            FillMenu(menu, BuildPageListContext(ContextMenuKind.Toolbar), PageCommand);
            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>The tab chip's menu, refilled each time it opens so it reflects the tabs and the saved order.</summary>
        private void AttachTabMenu(FrameworkElement chip, DocumentContext ctx)
        {
            chip.ContextMenu = new ContextMenu();
            chip.ContextMenuOpening += (_, _) =>
            {
                if (chip.ContextMenu is not ContextMenu menu) return;
                var plan = new ContextMenuContext
                {
                    Kind = ContextMenuKind.Tab,
                    ReadOnly = ReadOnlyGate.ProcessIsReadOnly,
                    TabCount = _tabs.Count,
                    TabHasPath = ctx.OriginalPath is not null,
                    TabHasDocument = ctx.Doc is not null,
                };
                FillMenu(menu, plan, id => id switch
                {
                    MenuId.CloseTab => (_, _) => CloseTab(ctx),
                    MenuId.CloseOthers => (_, _) => CloseOtherTabs(ctx),
                    MenuId.MoveToNewWindow => (_, _) => _ = TearOffTabToNewWindowAsync(ctx),
                    MenuId.SplitMove => (_, _) => _ = SplitMoveTabAsync(ctx),
                    MenuId.SideBySide => (_, _) => SplitViewCopy(ctx),
                    // OriginalPath, not the working path: after a decrypt-on-open or a structural edit the
                    // working file is a temp copy, and revealing %TEMP% is not what "containing folder" means.
                    MenuId.OpenFolder => (_, _) => RevealInExplorer(ctx.OriginalPath),
                    _ => null,
                });
            };
        }

        private static void AttachLinkMenu(MainWindow self, FrameworkElement overlay, LinkMenuKind kind, string? target, Action remove)
        {
            overlay.ContextMenu = new ContextMenu();
            overlay.ContextMenuOpening += (_, _) =>
            {
                if (overlay.ContextMenu is not ContextMenu menu) return;
                self.FillMenu(menu, new ContextMenuContext { Kind = ContextMenuKind.Link, Link = kind, ReadOnly = ReadOnlyGate.ProcessIsReadOnly },
                    id => id switch
                    {
                        MenuId.CopyEmail when target is not null => (_, _) => Clipboard.SetText(target["mailto:".Length..]),
                        MenuId.CopyUrl when target is not null => (_, _) => Clipboard.SetText(target),
                        MenuId.RemoveLink => (_, _) => remove(),
                        _ => null,
                    });
            };
        }

        private void ShowImageEditMenu(ImageEditAnnotation edit)
        {
            var menu = new ContextMenu();
            FillMenu(menu, new ContextMenuContext { Kind = ContextMenuKind.EditedPicture, ReadOnly = ReadOnlyGate.ProcessIsReadOnly },
                id => id switch
                {
                    MenuId.ReplaceImage => (_, _) => ReplaceImageEdit(edit),
                    MenuId.DeleteImage => (_, _) => DeleteImageEdit(edit),
                    MenuId.ResetImageSize => (_, _) => ResetImageEditSize(edit),
                    _ => null,
                },
                footer: m =>
                {
                    m.Items.Add(new Separator());
                    m.Items.Add(new MenuItem { Header = "Resize: drag the green handle", IsEnabled = false });
                });
            menu.PlacementTarget = _annotationCanvas;
            menu.IsOpen = true;
        }

        // ============================================================
        // Edit Menu…
        // ============================================================

        private static string MenuKindTitle(ContextMenuKind kind) => kind switch
        {
            ContextMenuKind.Page => "page menu",
            ContextMenuKind.Text => "text selection menu",
            ContextMenuKind.Mark => "mark menu",
            ContextMenuKind.Marks => "several marks menu",
            ContextMenuKind.Picture => "picture menu",
            ContextMenuKind.FormField => "form field menu",
            ContextMenuKind.EditedPicture => "picture edit menu",
            ContextMenuKind.Thumbnail => "page thumbnail menu",
            ContextMenuKind.Tab => "tab menu",
            ContextMenuKind.Sidebar => "bookmarks menu",
            ContextMenuKind.Toolbar => "Rotate button menu",
            ContextMenuKind.Link => "link menu",
            _ => "menu",
        };

        private void EditMenuLayout(ContextMenuKind kind)
        {
            var layouts = MenuLayouts.Parse(TDPdf.Properties.Settings.Default.MenuLayouts);
            layouts.TryGetValue(kind.ToString(), out var saved);
            var editor = new MenuLayoutEditor(ContextMenuModel.ListIds(kind), saved);
            bool ok = TdpDialog.ShowEditMenu(this, MenuKindTitle(kind), editor, id =>
            {
                var info = ContextMenuModel.Info(id);
                return (PlainName(info.Header), info.Glyph);
            });
            if (!ok) return;
            layouts[kind.ToString()] = editor.ToLayout();
            SaveMenuLayouts(layouts);
        }

        private static void SaveMenuLayouts(IReadOnlyDictionary<string, KindLayout> layouts)
        {
            try
            {
                TDPdf.Properties.Settings.Default.MenuLayouts = MenuLayouts.Serialize(layouts);
                TDPdf.Properties.Settings.Default.Save();
            }
            catch { /* the arrangement still holds for this session */ }
        }

        /// <summary>The Settings section for menus: shortcuts on or off, and putting every menu back.</summary>
        private void AddMenuSettingsSection(StackPanel panel)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Menus",
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 8)
            });
            var shortcuts = new CheckBox
            {
                Content = "Show keyboard shortcuts in right-click menus",
                IsChecked = TDPdf.Properties.Settings.Default.ShowMenuShortcuts,
                Foreground = BrushResource("TextPrimary"),
                ToolTip = "Show Ctrl+C, Delete and the other shortcut keys beside the items in right-click menus.",
                Margin = new Thickness(0, 0, 0, 8)
            };
            void Changed(object? s, RoutedEventArgs e)
            {
                try
                {
                    TDPdf.Properties.Settings.Default.ShowMenuShortcuts = shortcuts.IsChecked == true;
                    TDPdf.Properties.Settings.Default.Save();
                }
                catch { /* the choice still holds for this session */ }
            }
            shortcuts.Checked += Changed;
            shortcuts.Unchecked += Changed;
            panel.Children.Add(shortcuts);

            var reset = new Button
            {
                Content = "Reset all menus to default",
                Style = (Style)FindResource("DarkButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Put every right-click menu back in its original order and show every item.",
                Margin = new Thickness(0, 0, 0, 4)
            };
            AutomationProperties.SetName(reset, "Reset all menus to default");
            reset.Click += (_, _) =>
            {
                SaveMenuLayouts(new Dictionary<string, KindLayout>());
                SetStatus("Right-click menus are back in their default order");
            };
            panel.Children.Add(reset);
            panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        }
    }
}
