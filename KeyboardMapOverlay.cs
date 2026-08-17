using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TDPdf
{
    // ============================================================
    // Visual keyboard for the F1 / Ctrl+? shortcuts overlay.  (upstream KillerPDF v1.6.4)
    //
    // The overlay card (ShortcutOverlay in MainWindow.xaml) normally shows the hand-authored two-
    // column reference list. A LIST / KEYBOARD toggle in its header flips to this rendered board:
    // every key that carries a binding lights up, color-coded by category; holding a real Ctrl /
    // Shift / Alt (or clicking a layer button) previews that modifier layer; hovering a lit key
    // shows its action. The chosen view is persisted in Settings.ShortcutView.
    //
    // Adapted from upstream: TDPdf has no localization layer, so labels/section names are plain
    // English literals (not Str_* resource keys); the map reflects TDPdf's REAL bindings (as of
    // 1.19 that includes the single-key tool shortcuts below — TDPdf's own map, not upstream's;
    // still no F5-F9 view shortcuts); brushes are TDPdf's palette (AccentGreen / TextMuted /
    // BgPanel / BorderDim / TextPrimary), wired with SetResourceReference so Dark / Light /
    // HighContrast switches repaint the board live. Category colors ride the KsCat* theme brushes.
    // ============================================================
    public partial class MainWindow
    {
        private enum KbLayer { Base, Ctrl, CtrlShift, Shift, Alt }

        private KbLayer _kbLayer = KbLayer.Base;
        private bool _kbBuilt;
        private TextBlock? _kbDetail;
        private TextBlock? _kbHoverAct;   // caption of the key under the mouse (marquee restart on layer switch)
        private string? _kbHoverId;
        private readonly Dictionary<string, (Border Cap, TextBlock Act, Rectangle Bar)> _kbKeys = new();
        private readonly Dictionary<KbLayer, Button> _kbLayerBtns = new();

        private static readonly FontFamily KbMonoFont = new("Consolas");

        /// <summary>Card width caps for the two overlay views. The list value must match
        /// ShortcutCard's MaxWidth in MainWindow.xaml — it is restored here when switching back
        /// from the wider keyboard board (#177).</summary>
        private const double ShortcutListMaxWidth = 900;
        private const double KeyboardViewMaxWidth = 1080;

        // ── Binding tables ─────────────────────────────────────────────────────────────────────
        // key id -> (category, action caption). Category maps 1:1 to a KsCat* theme brush and to
        // the section name shown in the hover detail. Captions are plain English (no Loc()).
        // Every entry mirrors a REAL TDPdf binding (verified against MainWindow.xaml KeyBindings,
        // OnPreviewKeyDown / TrySelectToolByKey, and the outline-tree F2 rename). Ctrl+Scroll zoom
        // and middle-drag pan have no single keycap, so they live only in the list view.
        private static readonly Dictionary<KbLayer, Dictionary<string, (string Cat, string Label)>> KbMap = new()
        {
            [KbLayer.Base] = new()
            {
                ["F1"] = ("Help", "About TDPdf"),
                ["F2"] = ("Edit", "Rename bookmark"),
                ["F11"] = ("View", "Full screen"),
                ["F12"] = ("File", "Document info"),
                ["Home"] = ("Nav", "First page"),  ["End"] = ("Nav", "Last page"),
                ["PgUp"] = ("Nav", "Previous page"), ["PgDn"] = ("Nav", "Next page"),
                ["Left"] = ("Nav", "Previous page"), ["Right"] = ("Nav", "Next page"),
                ["Up"] = ("Nav", "Previous page"),   ["Down"] = ("Nav", "Next page"),
                ["Del"] = ("Edit", "Delete annotation"),
                ["Enter"] = ("Edit", "Confirm"),
                ["Esc"] = ("Edit", "Cancel / back to Select"),
                ["Menu"] = ("Edit", "Context menu"),
                // #193: the only bare letter outside the tool set — B was the one still unclaimed.
                ["B"] = ("View", "Book layout (Two-Page)"),
                // Tool keys (1.19). Digits mirror the toolbar left to right across the ten
                // mark-making tools; Select / Pan / Signature / Crop are letter-only. Every id here
                // must also exist in KbRows below or the keycap silently never lights.
                ["V"] = ("Tools", "Select"),            ["P"] = ("Tools", "Pan / hand"),
                ["D1"] = ("Tools", "Text"),             ["T"] = ("Tools", "Text"),
                ["D2"] = ("Tools", "Edit existing text"), ["X"] = ("Tools", "Edit existing text"),
                ["D3"] = ("Tools", "Edit existing image"),
                ["D4"] = ("Tools", "Insert image"),      ["I"] = ("Tools", "Insert image"),
                ["D5"] = ("Tools", "Highlight"),        ["H"] = ("Tools", "Highlight"),
                ["D6"] = ("Tools", "Strikethrough"),    ["K"] = ("Tools", "Strikethrough"),
                ["D7"] = ("Tools", "Underline"),        ["U"] = ("Tools", "Underline"),
                ["D8"] = ("Tools", "Shape"),            ["S"] = ("Tools", "Shape"),
                ["D9"] = ("Tools", "Draw"),             ["D"] = ("Tools", "Draw"),
                ["D0"] = ("Tools", "Eraser"),           ["E"] = ("Tools", "Eraser"),
                ["G"] = ("Tools", "Signature"),         ["C"] = ("Tools", "Crop"),
            },
            [KbLayer.Ctrl] = new()
            {
                ["O"] = ("File", "Open"),           ["S"] = ("File", "Save"),
                ["W"] = ("File", "Close file"),     ["N"] = ("File", "New document"),
                ["P"] = ("File", "Print"),
                ["F"] = ("Search", "Find"),         ["A"] = ("Search", "Select all text"),
                ["Z"] = ("Edit", "Undo"),           ["Y"] = ("Edit", "Redo"),
                ["C"] = ("Edit", "Copy text"),
                ["D0"] = ("View", "Reset zoom"),    ["D1"] = ("View", "Actual size"),
                ["D2"] = ("View", "Fit width"),     ["D3"] = ("View", "Fit page"),
                ["Equals"] = ("View", "Zoom in"),   ["Minus"] = ("View", "Zoom out"),
                ["I"] = ("View", "Invert colors"),
            },
            [KbLayer.CtrlShift] = new()
            {
                ["S"] = ("File", "Save as"),
                ["Z"] = ("Edit", "Redo"),
                ["O"] = ("Ocr", "OCR page to clipboard"),
                ["Equals"] = ("View", "App size larger"),
                ["Minus"] = ("View", "App size smaller"),
                ["D0"] = ("View", "Reset app size"),
            },
            [KbLayer.Shift] = new()
            {
                ["F10"] = ("Edit", "Context menu"),
                ["F4"] = ("File", "Show file size"),
                ["Enter"] = ("Search", "Previous result"),
                // Pairs with Ctrl+I: whether night mode inverts pictures too (needed on a scan).
                ["N"] = ("View", "Invert images too"),
            },
            [KbLayer.Alt] = new()
            {
                ["Left"] = ("Nav", "Back"), ["Right"] = ("Nav", "Forward"),
            },
        };

        // ── Physical layout ────────────────────────────────────────────────────────────────────
        // (id, cap text, width units). id "" = spacer. Numpad omitted (the numpad digits are aliases
        // for the number row, both for zoom and for the tool keys, so one row of caps says it all).
        private static readonly (string Id, string Cap, double W)[][] KbRows =
        [
            [("Esc","Esc",1), ("","",0.8), ("F1","F1",1),("F2","F2",1),("F3","F3",1),("F4","F4",1), ("","",0.6),
             ("F5","F5",1),("F6","F6",1),("F7","F7",1),("F8","F8",1), ("","",0.6),
             ("F9","F9",1),("F10","F10",1),("F11","F11",1),("F12","F12",1)],
            [("Grave","`",1),("D1","1",1),("D2","2",1),("D3","3",1),("D4","4",1),("D5","5",1),("D6","6",1),
             ("D7","7",1),("D8","8",1),("D9","9",1),("D0","0",1),("Minus","-",1),("Equals","=",1),("Back","⌫",2),
             ("","",0.6), ("Ins","Ins",1),("Home","Home",1),("PgUp","PgUp",1)],
            [("Tab","Tab",1.5),("Q","Q",1),("W","W",1),("E","E",1),("R","R",1),("T","T",1),("Y","Y",1),("U","U",1),
             ("I","I",1),("O","O",1),("P","P",1),("LBr","[",1),("RBr","]",1),("Bslash","\\",1.5),
             ("","",0.6), ("Del","Del",1),("End","End",1),("PgDn","PgDn",1)],
            [("Caps","Caps",1.8),("A","A",1),("S","S",1),("D","D",1),("F","F",1),("G","G",1),("H","H",1),("J","J",1),
             ("K","K",1),("L","L",1),("Semi",";",1),("Quote","'",1),("Enter","Enter",2.2)],
            [("Shift","Shift",2.3),("Z","Z",1),("X","X",1),("C","C",1),("V","V",1),("B","B",1),("N","N",1),("M","M",1),
             ("Comma",",",1),("Period",".",1),("Slash","/",1),("RShift","Shift",2.7),
             ("","",1.6), ("Up","↑",1)],
            [("Ctrl","Ctrl",1.5),("Win","Win",1.2),("Alt","Alt",1.5),("Space","",6.8),("RAlt","Alt",1.5),("Menu","☰",1),("RCtrl","Ctrl",1.5),
             ("","",0.6), ("Left","←",1),("Down","↓",1),("Right","→",1)],
        ];

        private static readonly (KbLayer Layer, string Caption)[] KbLayerButtons =
        [
            (KbLayer.Base, "BASE"), (KbLayer.Ctrl, "CTRL"), (KbLayer.CtrlShift, "CTRL+SHIFT"),
            (KbLayer.Shift, "SHIFT"), (KbLayer.Alt, "ALT"),
        ];

        // Modifier keycaps that light up per layer (they define it rather than carry a binding).
        private static readonly Dictionary<KbLayer, string[]> KbLayerMods = new()
        {
            [KbLayer.Base] = [], [KbLayer.Ctrl] = ["Ctrl", "RCtrl"],
            [KbLayer.CtrlShift] = ["Ctrl", "RCtrl", "Shift", "RShift"],
            [KbLayer.Shift] = ["Shift", "RShift"], [KbLayer.Alt] = ["Alt", "RAlt"],
        };

        private static string KbSectionName(string cat) => cat switch
        {
            "File" => "File", "Tools" => "Tools", "Edit" => "Editing",
            "Nav" => "Navigation", "View" => "View", "Search" => "Search & Select",
            "Help" => "Help", _ => "OCR",
        };

        // A chromeless button template that honors Background / BorderBrush / BorderThickness /
        // Padding from the templated parent (so the SetResourceReference calls below repaint it).
        private static ControlTemplate KbButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        // ── View toggle (LIST / KEYBOARD) ──────────────────────────────────────────────────────

        private void KsViewList_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(keyboard: false, persist: true);
        private void KsViewKeyboard_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(keyboard: true, persist: true);

        /// <summary>Shows the list or the keyboard inside the shortcuts overlay card. Called on
        /// every overlay open with the persisted choice, and by the two toggle captions.</summary>
        private void ApplyShortcutView(bool keyboard, bool persist = false)
        {
            if (keyboard && !_kbBuilt) BuildKeyboardView();
            ShortcutListHost.Visibility     = keyboard ? Visibility.Collapsed : Visibility.Visible;
            ShortcutKeyboardHost.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;
            // #177: only the KEYBOARD view needs a wider cap than the card's XAML default. The list
            // used to be re-clamped to 640 on every open, which undid the card's own MaxWidth and
            // squeezed ~40 two-column rows into a narrow strip; it now keeps the XAML width and
            // uses whatever the window allows.
            ShortcutCard.MaxWidth           = keyboard ? KeyboardViewMaxWidth : ShortcutListMaxWidth;
            KsViewListBtn.SetResourceReference(ForegroundProperty, keyboard ? "TextSecondary" : "AccentGreen");
            KsViewKeyboardBtn.SetResourceReference(ForegroundProperty, keyboard ? "AccentGreen" : "TextSecondary");
            if (keyboard) SetKbLayer(KbLayer.Base);
            if (persist)
            {
                Properties.Settings.Default.ShortcutView = keyboard ? "keyboard" : "list";
                try { Properties.Settings.Default.Save(); } catch { /* persistence is best-effort */ }
            }
        }

        private void ApplyPersistedShortcutView() =>
            ApplyShortcutView(Properties.Settings.Default.ShortcutView == "keyboard");

        // ── Board construction (once, lazily) ──────────────────────────────────────────────────

        private void BuildKeyboardView()
        {
            _kbBuilt = true;
            var host = ShortcutKeyboardHost;
            host.Children.Clear();
            _kbKeys.Clear();
            _kbLayerBtns.Clear();

            // Layer captions row.
            var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (layer, caption) in KbLayerButtons)
            {
                var b = new Button
                {
                    Content = caption, FontFamily = KbMonoFont, FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                    FocusVisualStyle = null, Template = KbButtonTemplate(),   // no stock hover chrome
                };
                b.SetResourceReference(BackgroundProperty, "BgPanel");
                b.SetResourceReference(ForegroundProperty, "TextSecondary");
                b.SetResourceReference(BorderBrushProperty, "BorderDim");
                var l = layer;
                b.Click += (_, _2) => SetKbLayer(l);
                _kbLayerBtns[layer] = b;
                layerRow.Children.Add(b);
            }
            var hint = new TextBlock
            {
                Text = "hold Ctrl / Shift / Alt to preview a layer",
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            layerRow.Children.Add(hint);
            host.Children.Add(layerRow);

            // The board. A DownOnly Viewbox keeps it fitting smaller windows without scrollbars.
            const double U = 46;   // one key unit incl. its 4px gap
            var board = new StackPanel();
            foreach (var row in KbRows)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var (id, cap, w) in row)
                {
                    if (id.Length == 0) { r.Children.Add(new Border { Width = U * w }); continue; }
                    var capText = new TextBlock
                    {
                        Text = KbCapText(id, cap), FontFamily = KbMonoFont,   // symbols render via font fallback
                        FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0),
                    };
                    capText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                    var act = new TextBlock
                    {
                        FontSize = 8.5, HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed,
                        RenderTransform = new TranslateTransform(),
                    };
                    var actHost = new Border   // clips the caption so it can marquee on hover
                    {
                        ClipToBounds = true, VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(2, 0, 2, 5), Child = act,
                    };
                    var bar = new Rectangle
                    {
                        Height = 3, VerticalAlignment = VerticalAlignment.Bottom, RadiusX = 1.5, RadiusY = 1.5,
                        Margin = new Thickness(3, 0, 3, 0), Visibility = Visibility.Collapsed,
                    };
                    var inner = new Grid();
                    inner.Children.Add(capText);
                    inner.Children.Add(actHost);
                    inner.Children.Add(bar);
                    var key = new Border
                    {
                        Width = U * w - 4, Height = 44, CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 0),
                        Child = inner,
                    };
                    key.SetResourceReference(Border.BackgroundProperty, "BgPanel");
                    key.SetResourceReference(Border.BorderBrushProperty, "BorderDim");
                    // Hover: the keycap lifts a few pixels.
                    var lift = new TranslateTransform();
                    key.RenderTransform = lift;
                    string keyId = id;
                    key.MouseEnter += (_, _2) =>
                    {
                        _kbHoverAct = act; _kbHoverId = keyId;
                        KbShowDetail(keyId);
                        if (KbMap[_kbLayer].ContainsKey(keyId))   // only keys with a binding lift; dummies stay put
                        {
                            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(90)));
                            KbMarqueeStart(act);   // a cut-off caption scrolls, marquee-style
                        }
                    };
                    key.MouseLeave += (_, _2) =>
                    {
                        _kbHoverAct = null; _kbHoverId = null;
                        if (_kbDetail is not null) _kbDetail.Text = " ";
                        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)));
                        KbMarqueeStop(act);
                    };
                    _kbKeys[id] = (key, act, bar);
                    r.Children.Add(key);
                }
                board.Children.Add(r);
            }
            host.Children.Add(new Viewbox
            {
                Child = board, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            _kbDetail = new TextBlock
            {
                Text = " ", FontFamily = KbMonoFont, FontSize = 12.5,
                Margin = new Thickness(2, 10, 0, 0), Height = 18,
            };
            _kbDetail.SetResourceReference(TextBlock.ForegroundProperty, "AccentGreen");
            host.Children.Add(_kbDetail);
        }

        /// <summary>
        /// #153: the board below is a fixed ANSI picture, which is fine for letters (positional by
        /// nature) but wrong for the two punctuation caps TDPdf actually binds — on a German
        /// keyboard the key we treat as "=" is lettered "+". Only those two are re-lettered from
        /// the live layout; re-drawing the whole board per layout (QWERTZ / AZERTY key order, the
        /// extra ISO key) is a much bigger job and is deliberately not attempted.
        /// </summary>
        private static string KbCapText(string id, string fallback) => id switch
        {
            "Equals" => CharCap(Key.OemPlus, fallback),
            "Minus" => CharCap(Key.OemMinus, fallback),
            _ => fallback,
        };

        private static string CharCap(Key key, string fallback)
        {
            char c = Services.KeyLayout.CharFor(key, shift: false);
            return c == '\0' ? fallback : c.ToString();
        }

        private void KbShowDetail(string id)
        {
            if (_kbDetail is null) return;
            if (KbMap[_kbLayer].TryGetValue(id, out var b))
                _kbDetail.Text = $"{KbSectionName(b.Cat)} :: {b.Label}";
            else
                _kbDetail.Text = " ";
        }

        // ── Caption marquee (hover a lit key whose caption is cut off) ─────────────────────────

        /// <summary>Scrolls a truncated caption back and forth inside its clipped host while the
        /// key is hovered. No-op when the full text already fits.</summary>
        private static void KbMarqueeStart(TextBlock act)
        {
            if (act.Visibility != Visibility.Visible || act.Parent is not Border host) return;
            // Measure with a probe TextBlock, NOT FormattedText: the probe inherits the same text
            // formatting mode as the live control, so its width matches what actually renders.
            var probe = new TextBlock
            {
                Text = act.Text, FontFamily = act.FontFamily, FontSize = act.FontSize,
                FontStyle = act.FontStyle, FontWeight = act.FontWeight, FontStretch = act.FontStretch,
            };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(act));
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = probe.DesiredSize.Width - host.ActualWidth;
            if (over <= 0.5) return;
            // Reparent the caption into a Canvas for the ride: a Canvas measures children with
            // INFINITE space, so the TextBlock escapes WPF's layout clip and renders the whole
            // caption; the host border clips the viewport.
            double h = act.ActualHeight;
            act.TextTrimming = TextTrimming.None;
            host.Child = null;
            var cv = new Canvas { Height = h };
            cv.Children.Add(act);
            Canvas.SetLeft(act, 0);
            Canvas.SetTop(act, 0);
            host.Child = cv;
            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -over, TimeSpan.FromMilliseconds(Math.Max(600, over * 40)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(350) });
        }

        private static void KbMarqueeStop(TextBlock act)
        {
            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            act.TextTrimming = TextTrimming.CharacterEllipsis;
            if (act.Parent is Canvas cv && cv.Parent is Border host)
            {
                cv.Children.Clear();
                host.Child = act;   // back to the plain centered, ellipsized layout
            }
        }

        // ── Layer painting ─────────────────────────────────────────────────────────────────────

        private void SetKbLayer(KbLayer layer)
        {
            _kbLayer = layer;
            if (!_kbBuilt) return;
            var map = KbMap[layer];
            foreach (var kv in _kbKeys)
            {
                var vis = kv.Value;
                if (map.TryGetValue(kv.Key, out var b))
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "KsCat" + b.Cat);
                    vis.Bar.SetResourceReference(Shape.FillProperty, "KsCat" + b.Cat);
                    vis.Bar.Visibility = Visibility.Visible;
                    vis.Act.Text = b.Label;
                    vis.Act.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + b.Cat);
                    vis.Act.Visibility = Visibility.Visible;
                }
                else
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "BorderDim");
                    vis.Bar.Visibility = Visibility.Collapsed;
                    vis.Act.Visibility = Visibility.Collapsed;
                }
            }
            // Modifier caps that define the layer glow accent; the layer captions follow suit.
            string[] allMods = ["Ctrl", "RCtrl", "Shift", "RShift", "Alt", "RAlt"];
            foreach (var m in allMods)
                if (_kbKeys.TryGetValue(m, out var vis))
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty,
                        Array.IndexOf(KbLayerMods[layer], m) >= 0 ? "AccentGreen" : "BorderDim");
            foreach (var kv in _kbLayerBtns)
            {
                kv.Value.SetResourceReference(ForegroundProperty, kv.Key == layer ? "AccentGreen" : "TextSecondary");
                kv.Value.SetResourceReference(BorderBrushProperty, kv.Key == layer ? "AccentGreen" : "BorderDim");
            }
            // Layer changed while a key is hovered (holding Ctrl / Shift / Alt): restart that key's
            // marquee for its NEW caption — MouseEnter alone never re-fires. Deferred one layout pass
            // so the caption text and size reflect the new layer before measuring.
            if (_kbHoverAct is not null && _kbHoverId is not null)
            {
                KbMarqueeStop(_kbHoverAct);
                KbShowDetail(_kbHoverId);
                var act = _kbHoverAct;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(act, _kbHoverAct)) KbMarqueeStart(act);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Maps the live modifier state to a layer while the keyboard view is showing —
        /// called from OnPreviewKeyDown / OnPreviewKeyUp so holding Ctrl / Shift / Alt previews
        /// that layer.</summary>
        private void KbSyncLayerFromModifiers()
        {
            if (!_kbBuilt || ShortcutKeyboardHost.Visibility != Visibility.Visible) return;
            var m = Keyboard.Modifiers;
            var layer = m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlShift
                      : m.HasFlag(ModifierKeys.Control) ? KbLayer.Ctrl
                      : m.HasFlag(ModifierKeys.Alt) ? KbLayer.Alt
                      : m.HasFlag(ModifierKeys.Shift) ? KbLayer.Shift
                      : KbLayer.Base;
            if (layer != _kbLayer) SetKbLayer(layer);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            KbSyncLayerFromModifiers();
        }
    }
}
