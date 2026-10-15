// MainWindow — interactive AcroForm field overlays.
// Builds the WPF overlay controls that let a PDF form be filled in place, and writes
// the values back. Extracted verbatim from MainWindow.xaml.cs.

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
        // PDF Form Field Overlays (interactive AcroForm filling)
        // ============================================================
        // Ported from upstream KillerPDF v1.4.2 form filling, adapted to TDPdf's
        // multi-tab DocumentContext: pending values live on the active context
        // (_formTextValues/_formCheckValues/_formRadioValues) so they survive tab
        // switches, and the overlay controls reuse the same PDF-point → canvas
        // coordinate conversion as the link overlays (GetPageLinks/RenderPageLinks).
        // Supported field types: text (/Tx), checkbox & radio (/Btn), dropdown (/Ch).
        // On save the values are baked into the PDF field dictionaries with
        // regenerated /AP /N appearance streams (and /NeedAppearances as a fallback)
        // so other viewers display them. All parsing is wrapped in try/catch so a
        // malformed AcroForm can never crash open or save.

        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<string> Options,
            // Upstream KillerPDF #158: a comb field is /Tx with the Comb flag (/Ff bit 25) AND a
            // /MaxLen — the printed row of equal-width boxes forms are so fond of. GetPageFormFields
            // only ever sets IsComb together with MaxLen > 0 (and never with IsMultiLine, which the
            // spec makes mutually exclusive), so anything downstream may divide by MaxLen whenever
            // IsComb is true. MaxLen is also the typing cap.
            bool   IsComb,
            int    MaxLen,
            // A /Btn with the Pushbutton flag (/Ff bit 17) holds no value and must never get a
            // fill-in control. Without this it fell through to the text-field branch and a form's
            // Submit / Print / Reset button became an editable box that wrote a /V on save.
            bool   IsPushButton = false,
            // /Opt entries may be [export, display] pairs: the list shows the display string but
            // /V must carry the EXPORT value. Options holds what the user sees, OptionExports the
            // value at the same index that gets written back. For a plain string entry the two are
            // identical, which is why every existing single-string form still behaves the same.
            List<string>? OptionExports = null,
            // #242: true when the field's /AA additional-action JavaScript formats it as a number
            // (Acrobat and LiveCycle both write AFNumber_*). Form-aware OCR uses it to restrict
            // recognition to digits and separators, where O/l/S are the usual misreads.
            bool   IsNumeric = false);

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// Removes any stale form overlays first (tagged <see cref="FormOverlayTag"/>)
        /// without wiping non-form children, so it is safe to call repeatedly.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex < 0 || pageIndex >= _doc.PageCount) return;
            if (canvasW <= 0 || canvasH <= 0) return;

            // Remove stale overlays without wiping the entire canvas.
            for (int i = _annotationCanvas.Children.Count - 1; i >= 0; i--)
                if (_annotationCanvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    _annotationCanvas.Children.RemoveAt(i);

            List<FormFieldInfo> fields;
            try { fields = GetPageFormFields(pageIndex, canvasW, canvasH); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RenderFormFields: {ex}"); return; }
            if (fields.Count == 0) return;

            // Authoring mode. Live controls are the wrong thing while the Form tool is out: they
            // swallow the press that placing a new field needs, so a field could never be drawn
            // over or beside an existing one — and what an author wants to see is where the fields
            // ARE, with their names, not a box to type in.
            if (_currentTool == EditTool.Form)
            {
                RenderFormFieldOutlines(fields);
                return;
            }

            var greenBrush = BrushResource("AccentGreen");
            var darkBrush  = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            // Fixed light field appearance: these controls overlay the (white) rendered
            // PDF page and represent document content, so they stay light regardless of
            // the app theme rather than using chrome brushes.
            var fieldBg    = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));
            var focusBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // -- Push button ---------------------------------------------------
                // Holds no value, so there is nothing to fill in and no overlay to draw. Left to
                // the rendered page, where its own appearance stream already shows the button.
                // This must come FIRST: a pushbutton is neither checkbox nor radio nor /Ch, so it
                // otherwise satisfied the text-field test below and became an editable box.
                if (f.IsPushButton) continue;

                // -- Text field ----------------------------------------------------
                if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Use the shorter canvas dimension as the font size reference so that
                    // rotated fields (where Cw and Ch are swapped vs. portrait) don't blow up.
                    double fieldShort = Math.Min(f.Cw, f.Ch);
                    double fontSize = f.IsMultiLine ? fieldShort * 0.18 : fieldShort * 0.65;
                    fontSize = Math.Max(10, fontSize);
                    // #158: a comb field types one character per printed cell. WPF has no comb
                    // TextBox, so the overlay approximates the cell walk: a monospace face (Consolas'
                    // advance is ~0.55em) sized so one advance is at most one cell wide, capped at
                    // MaxLen characters, and left-padded by half a cell minus half a glyph so the
                    // first character lands in the middle of cell 0 rather than against its left
                    // wall. It is an approximation on screen only — the SAVED appearance stream
                    // below places each glyph exactly at its cell centre. IsComb guarantees
                    // MaxLen > 0 (see FormFieldInfo), so the division is safe.
                    double combCellW = f.IsComb ? f.Cw / f.MaxLen : 0;
                    if (f.IsComb) fontSize = Math.Max(9, Math.Min(fontSize, combCellW / 0.55));
                    var tb = new TextBox
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Text            = cur,
                        MaxLength       = f.IsComb ? f.MaxLen : 0,   // 0 = unlimited (WPF default)
                        IsReadOnly      = f.IsReadOnly,
                        AcceptsReturn   = f.IsMultiLine,
                        TextWrapping    = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        Background      = fieldBg,
                        Foreground      = Brushes.Black,
                        CaretBrush      = Brushes.Black,
                        BorderBrush     = greenBrush,
                        BorderThickness = new Thickness(1),
                        FontSize        = fontSize,
                        Padding         = f.IsComb
                            ? new Thickness(Math.Max(0, combCellW / 2 - fontSize * 0.275), 0, 0, 0)
                            : new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (f.IsComb) tb.FontFamily = new FontFamily("Consolas");
                    tb.GotFocus  += (_, _) => tb.BorderBrush = focusBrush;
                    tb.LostFocus += (_, _) => tb.BorderBrush = greenBrush;
                    int capturedKey = f.ObjNum;
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(); };
                    ctrl = tb;
                }
                // -- Dropdown / choice --------------------------------------------
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag        = FormOverlayTag,
                        Width      = f.Cw,
                        Height     = f.Ch,
                        IsEnabled  = !f.IsReadOnly,
                        Foreground = Brushes.Black,
                        FontSize   = Math.Max(10, Math.Min(f.Cw, f.Ch) * 0.65),
                        ToolTip    = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (var opt in f.Options) combo.Items.Add(opt);

                    // The list shows display strings; /V carries export values. Select by INDEX so
                    // the two never have to be equal: match the stored/current value against the
                    // exports first, then fall back to the display text for files whose /V already
                    // holds the label (and for plain-string /Opt, where the two are the same).
                    var exports = f.OptionExports ?? f.Options;
                    int selIdx = exports.IndexOf(cur);
                    if (selIdx < 0) selIdx = f.Options.IndexOf(cur);
                    combo.SelectedIndex = selIdx;   // -1 when /V matches nothing: leave it unset

                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        int i = combo.SelectedIndex;
                        if (i < 0) return;
                        // Write the export value, which is what other viewers read back.
                        _formTextValues[capturedKey] = i < exports.Count ? exports[i] : f.Options[i];
                        MarkDirty();
                    };
                    ctrl = combo;
                }
                // -- Checkbox ------------------------------------------------------
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.ObjNum, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox — WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = greenBrush,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        int capturedKey = f.ObjNum;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty();
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }
                // -- Radio button --------------------------------------------------
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width  = inner,
                        Height = inner,
                        Fill   = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = greenBrush,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag        = FormOverlayTag,
                        Width      = f.Cw,
                        Height     = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor     = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child      = grid,
                        ToolTip    = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = new();
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty();
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                // Upstream v1.7.0 (#156): field overlays sit BELOW the annotation layer. TDPdf puts
                // annotations and form overlays on the SAME canvas, and RenderAllAnnotations paints
                // the annotations and then calls RestoreFormOverlays — a Canvas paints later children
                // on top, so a signature dropped on a fill-in field disappeared behind the field's own
                // near-opaque control. Ordering cannot fix it here (this method removes and re-adds
                // every stale overlay on each call, so the fields always end up last), but ZIndex can:
                // annotations render at the default 0, so -1 puts the fields under them without
                // touching a single annotation path. Clicking a covered field still works — every
                // annotation visual is IsHitTestVisible=false, so none of them swallows the click that
                // reaches the field beneath, and the selection chrome (borders, resize handles) stays
                // at 0 and therefore still outranks the fields.
                Panel.SetZIndex(ctrl, -1);
                _annotationCanvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus($"Page {pageIndex + 1} of {_doc.PageCount} - contains fillable form fields");
        }

        /// <summary>
        /// Draws each field as a labelled outline, for the Form tool's authoring mode.
        /// </summary>
        /// <remarks>
        /// Every element is tagged <see cref="FormOverlayTag"/> like the live controls it stands in
        /// for, so the same sweep at the top of RenderFormFields clears it on the next render.
        /// </remarks>
        private void RenderFormFieldOutlines(List<FormFieldInfo> fields)
        {
            var green = (SolidColorBrush)FindResource("AccentGreen");
            var wash = new SolidColorBrush(Color.FromArgb(28, green.Color.R, green.Color.G, green.Color.B));

            int selectedObj = _selectedFormWidget is null ? int.MinValue : GetObjectNumber(_selectedFormWidget);

            foreach (var f in fields)
            {
                if (!IsFinitePositive(f.Cw) || !IsFinitePositive(f.Ch)) continue;
                bool selected = f.ObjNum == selectedObj;

                var box = new Rectangle
                {
                    Width = f.Cw,
                    Height = f.Ch,
                    Fill = wash,
                    Stroke = green,
                    StrokeThickness = selected ? 2.5 : 1,
                    StrokeDashArray = selected ? null : new DoubleCollection { 3, 2 },
                    IsHitTestVisible = false,
                    Tag = FormOverlayTag,
                };
                Canvas.SetLeft(box, f.Cx);
                Canvas.SetTop(box, f.Cy);
                _annotationCanvas.Children.Add(box);

                if (string.IsNullOrEmpty(f.FieldName)) continue;
                var label = new TextBlock
                {
                    Text = f.FieldName,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 9,
                    Foreground = Brushes.White,
                    Background = green,
                    Padding = new Thickness(3, 0, 3, 0),
                    IsHitTestVisible = false,
                    Tag = FormOverlayTag,
                };
                // Above the box where there is room, tucked inside the top where there is not, so a
                // field at the very top of the page still says which one it is.
                Canvas.SetLeft(label, f.Cx);
                Canvas.SetTop(label, f.Cy >= 13 ? f.Cy - 12 : f.Cy + 1);
                _annotationCanvas.Children.Add(label);
            }
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas
        /// coordinates. Walks the parent chain for each widget to resolve inherited
        /// /FT, /T, /V, /Ff, and /Opt, and maps the widget /Rect (PDF point space,
        /// bottom-left origin, unrotated) to canvas space accounting for page /Rotate —
        /// the same coordinate model as the link overlays.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex < 0 || pageIndex >= _doc.PageCount) return result;

            var page = _doc.Pages[pageIndex];
            // Resolve the box the overlay's bitmap was actually rendered from: PDFium rasterizes the
            // CropBox (falling back to the MediaBox), so field /Rect coordinates must be mapped
            // relative to THAT box's own lower-left origin and size. GetVisiblePageBox also walks the
            // page tree for an inherited box and never touches the create-on-read page.MediaBox /
            // page.CropBox / page.Width getters — reading those returned an empty rectangle for an
            // inherited box, which used to drop the page onto a hardcoded A4 size and shift every
            // field overlay (worst near the top of the page) on US Letter and other non-A4 documents.
            //
            // The box is deliberately NOT rotated here: field /Rect coords live in unrotated user
            // space, and PdfRectToCanvas maps them onto the already-rotated bitmap. (That is also why
            // page.Width/Height are unusable — PdfSharpCore swaps them for 90/270 pages.)
            var box = GetVisiblePageBox(page);
            int rotation = PdfPageGeometry.Rotation(page);

            try
            {
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return result;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Widget")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    // Map the widget rect onto the Docnet/PDFium bitmap the canvas mirrors — the same
                    // conversion the link overlays use.
                    var (cx, cy, cw, ch) = PdfRectToCanvas(box, rotation, canvasW, canvasH, rx1, ry1, rx2, ry2);
                    // Upstream v1.7.1 (#181): a malformed widget rectangle must not reach a WPF Width
                    // or Height property — WPF throws for NaN and infinity, which took the viewer down
                    // when a page click rebuilt the form overlay. "cw < 2" is no filter for those:
                    // "∞ < 2" is false and every comparison with NaN is false, so both used to sail
                    // straight through into the TextBox / ComboBox / checkbox / radio sizes below.
                    if (!IsFinite(cx) || !IsFinite(cy) ||
                        !IsFinitePositive(cw) || !IsFinitePositive(ch) ||
                        cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes.
                    string ft = "", name = "", curVal = "";
                    int flags = 0;
                    int maxLen = 0;   // #158: /MaxLen, the comb cell count
                    var options = new List<string>();
                    var optionExports = new List<string>();

                    PdfDictionary? node = ann;
                    while (node is not null)
                    {
                        if (string.IsNullOrEmpty(ft) && node.Elements["/FT"] is not null)
                            ft = node.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name) && node.Elements["/T"] is PdfString ts)
                            name = ts.Value;
                        if (string.IsNullOrEmpty(curVal) && node.Elements["/V"] is not null)
                        {
                            var vElem = node.Elements["/V"];
                            curVal = vElem is PdfString vs ? vs.Value : vElem?.ToString() ?? "";
                        }
                        if (flags == 0 && node.Elements["/Ff"] is PdfInteger fi)
                            flags = fi.Value;
                        // #158: /MaxLen is inheritable exactly like /Ff, so it gets the same
                        // parent-chain walk — a comb field very often carries its /Ff and /MaxLen on
                        // the parent field node and only the /Rect on the widget.
                        if (maxLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml)
                            maxLen = ml.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                // A pair is [export, display]: show the second, save the first.
                                // A bare string is both at once.
                                if (o is PdfString ps2) { options.Add(ps2.Value); optionExports.Add(ps2.Value); }
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                {
                                    string export  = (pa2.Elements[0] as PdfString)?.Value ?? "";
                                    string display = (pa2.Elements[1] as PdfString)?.Value ?? "";
                                    options.Add(display);
                                    optionExports.Add(export);
                                }
                            }
                        }

                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    if (string.IsNullOrEmpty(ft)) ft = "/Tx";

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    // #158: Comb is /Ff bit 25 (1 << 24) and only means anything on a /Tx field that
                    // also declares how many cells it has. The spec makes comb and multiline mutually
                    // exclusive, so a field that (wrongly) sets both stays on the ordinary multiline
                    // path. Requiring maxLen > 0 here is what lets every consumer divide by MaxLen.
                    bool isComb      = ft.Contains("Tx") && (flags & (1 << 24)) != 0
                                       && maxLen > 0 && !isMultiLine;
                    bool isPushBtn   = ft.Contains("Btn") && (flags & (1 << 16)) != 0;
                    bool isRadio     = ft.Contains("Btn") && !isPushBtn && (flags & (1 << 15)) != 0;
                    bool isCheckBox  = ft.Contains("Btn") && !isPushBtn && !isRadio;

                    // The "on" value for this widget (radio/checkbox selected state) is the
                    // /AP /N key that is not /Off.
                    string onValue = "/Yes";
                    try
                    {
                        var apDict = ann.Elements.GetDictionary("/AP");
                        var nDict  = apDict?.Elements.GetDictionary("/N");
                        if (nDict is not null)
                            foreach (var k in nDict.Elements.Keys)
                                if (k != "/Off") { onValue = k; break; }
                    }
                    catch { }

                    // #242: the field's format action, walked up the parent chain like /Ff and
                    // /MaxLen because /AA is inherited the same way. Read only as a signal — the
                    // JavaScript itself is never executed.
                    bool isNumeric = false;
                    try
                    {
                        node = ann;
                        while (node is not null && !isNumeric)
                        {
                            var aaDict = node.Elements.GetDictionary("/AA");
                            var fmtDict = aaDict?.Elements.GetDictionary("/F");
                            if (fmtDict?.Elements["/JS"] is PdfString jsStr)
                                isNumeric = FormOcrPolicy.LooksNumeric(jsStr.Value);
                            var api = node.Elements["/Parent"];
                            if (api is null) break;
                            node = api as PdfDictionary ?? DerefItem(api) as PdfDictionary;
                        }
                    }
                    catch { /* a malformed /AA must never break field parsing */ }

                    int objNum = GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options,
                        isComb, maxLen, isPushBtn, optionExports, isNumeric));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields: {ex}"); }

            return result;
        }

        /// <summary>
        /// Writes all pending form values back into the PDF document's AcroForm field
        /// dictionaries. Called from <see cref="DrawAnnotationsOnDocument"/> just before
        /// saving so values are persisted. Sets /V (and /AS for buttons) and regenerates
        /// /AP /N appearance streams; also sets /NeedAppearances as a fallback.
        /// </summary>
        private void WriteFormValuesToDocument()
        {
            if (_doc is null) return;
            if (_formTextValues.Count == 0 && _formCheckValues.Count == 0 && _formRadioValues.Count == 0) return;

            try
            {
                for (int p = 0; p < _doc.PageCount; p++)
                {
                    var page = _doc.Pages[p];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int i = 0; i < annotsArr.Elements.Count; i++)
                    {
                        PdfItem? elem = annotsArr.Elements[i];
                        PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Widget")) continue;

                        int objNum = GetObjectNumber(elem);
                        if (objNum < 0) objNum = -(p * 10000 + i);

                        // Walk parent chain to find the canonical field dict (owns /FT).
                        PdfDictionary? fieldDict = ann;
                        PdfDictionary? node = ann;
                        while (node is not null)
                        {
                            if (node.Elements["/FT"] is not null) { fieldDict = node; break; }
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Field rect for AP stream sizing.
                        var rectArr = ann.Elements.GetArray("/Rect");
                        double fieldW = 100, fieldH = 20;
                        if (rectArr?.Elements.Count >= 4)
                        {
                            double rx1 = rectArr.Elements.GetReal(0), ry1 = rectArr.Elements.GetReal(1);
                            double rx2 = rectArr.Elements.GetReal(2), ry2 = rectArr.Elements.GetReal(3);
                            fieldW = Math.Abs(rx2 - rx1);
                            fieldH = Math.Abs(ry2 - ry1);
                        }

                        // Resolve /DA for font name/size (walk parent chain).
                        string? daStr = null;
                        node = ann;
                        while (node is not null && daStr is null)
                        {
                            if (node.Elements["/DA"] is PdfString ds) daStr = ds.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Upstream v1.7.1 (#180): /Ff is inheritable exactly like /DA, and bit 13
                        // (4096) is Multiline. The overlay already decoded it (GetPageFormFields),
                        // but the appearance writer never saw it: a multiline field has to lay its
                        // value out in lines from the top of the box, a single-line one draws one
                        // vertically centred line.
                        //
                        // #158: /MaxLen rides along on the same walk — it is inheritable in exactly
                        // the same way, and a comb field's appearance needs both it and bit 25.
                        // The loop now stops only once BOTH have been found (or the chain runs out);
                        // the per-value `== 0` guards mean the extra iterations can never change
                        // which /Ff wins, so non-comb fields resolve identically to before.
                        int fieldFlags = 0;
                        int combLen = 0;
                        node = ann;
                        while (node is not null && (fieldFlags == 0 || combLen == 0))
                        {
                            if (fieldFlags == 0 && node.Elements["/Ff"] is PdfInteger fi) fieldFlags = fi.Value;
                            if (combLen == 0 && node.Elements["/MaxLen"] is PdfInteger ml) combLen = ml.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }
                        // Multiline is a /Tx-only flag — a /Ch choice field uses that bit position
                        // for nothing — so gate on the field type the way GetPageFormFields does,
                        // including its "missing /FT means /Tx" default.
                        string ffType = fieldDict?.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(ffType)) ffType = "/Tx";
                        bool isMultiLine = ffType.Contains("Tx") && (fieldFlags & 4096) != 0;
                        // #158: same gating as GetPageFormFields — /Tx only, needs a positive
                        // /MaxLen, and never together with multiline.
                        bool isComb = ffType.Contains("Tx") && (fieldFlags & (1 << 24)) != 0
                                      && combLen > 0 && !isMultiLine;

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH, isMultiLine,
                                isComb ? combLen : 0);
                        }
                        else if (_formCheckValues.TryGetValue(objNum, out var checkVal) && fieldDict is not null)
                        {
                            string onVal = WidgetOnValue(ann);
                            fieldDict.Elements["/V"]  = new PdfName(checkVal ? onVal : "/Off");
                            fieldDict.Elements["/AS"] = new PdfName(checkVal ? onVal : "/Off");
                            ann.Elements["/AS"]       = new PdfName(checkVal ? onVal : "/Off");
                            GenerateCheckBoxAppearance(ann, checkVal, onVal, fieldW, fieldH);
                        }
                        else if (_formRadioValues.Count > 0 && fieldDict is not null)
                        {
                            string ft2 = fieldDict.Elements["/FT"]?.ToString() ?? "";
                            if (ft2.Contains("Btn"))
                            {
                                // Find /T on the parent field node.
                                string fieldName2 = "";
                                var n2 = fieldDict;
                                while (n2 is not null && string.IsNullOrEmpty(fieldName2))
                                {
                                    if (n2.Elements["/T"] is PdfString ts2) fieldName2 = ts2.Value;
                                    var pi2 = n2.Elements["/Parent"];
                                    if (pi2 is null) break;
                                    n2 = pi2 as PdfDictionary ?? DerefItem(pi2) as PdfDictionary;
                                }
                                if (_formRadioValues.TryGetValue(fieldName2, out var radioSel))
                                {
                                    fieldDict.Elements["/V"] = new PdfName(radioSel);
                                    string onVal2 = WidgetOnValue(ann);
                                    ann.Elements["/AS"] = new PdfName(onVal2 == radioSel ? onVal2 : "/Off");
                                }
                            }
                        }
                    }
                }

                // Belt-and-suspenders: also set NeedAppearances in case any AP generation failed.
                try
                {
                    var acroForm = _doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    if (acroForm is not null)
                        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WriteFormValuesToDocument: {ex}"); }
        }

        /// <summary>Returns the on-state value (/AP /N key that is not /Off) for a button widget.</summary>
        private static string WidgetOnValue(PdfDictionary widgetAnn)
        {
            try
            {
                var apDict = widgetAnn.Elements.GetDictionary("/AP");
                var nDict  = apDict?.Elements.GetDictionary("/N");
                if (nDict is not null)
                    foreach (var k in nDict.Elements.Keys)
                        if (k != "/Off") return k;
            }
            catch { }
            return "/Yes";
        }

        /// <summary>
        /// Generates a /AP /N form XObject appearance stream for a text field and sets it
        /// on the widget annotation, so the typed value shows in other viewers.
        /// </summary>
        /// <param name="combLen">
        /// Upstream KillerPDF #158. Cell count of a comb field (/Ff bit 25 with a /MaxLen), which
        /// draws one character per evenly-spaced cell instead of one continuous run. 0 — the default
        /// every existing caller keeps — means "not a comb field" and leaves the ordinary
        /// single-line / multiline path below completely untouched.
        /// </param>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH, bool isMultiLine,
            int combLen = 0)
        {
            try
            {
                const double pad = 2;   // left/right inset, matching the Td origin below

                // #140: the shared path below writes the value as a WinAnsi literal against the
                // field's /DA base font, which is NOT embedded — so anything WinAnsi cannot express
                // was folded to '?' and saved that way. /V kept the real text, so the value looked
                // correct in TDPdf and was wrong in every other viewer, in print, and in flatten.
                // A value that needs more than WinAnsi is drawn instead through PdfSharpCore, which
                // embeds a covering font. Everything representable keeps the byte-identical output
                // it has always produced, so comb (#158) and multiline (#180) are untouched.
                if (NeedsEmbeddedFont(text)
                    && TryGenerateUnicodeFieldAppearance(widgetAnn, text, da, fieldW, fieldH,
                                                        isMultiLine, combLen, pad))
                    return;

                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                // The "no taller than 85% of the box" clamp is a single-line rule: a multiline
                // field is as tall as it needs to be for several lines, so applying it there
                // blows the text up to the height of the whole box.
                fontSize = isMultiLine ? Math.Max(6, fontSize)
                                       : Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // #158: comb — one character per evenly-spaced cell, the way Acrobat fills the
                // printed boxes. Each glyph gets its OWN text matrix placing it at its cell's
                // centre: Tm sets the absolute position, so no leading/advance accumulates between
                // them and the run cannot drift out of the cells. The horizontal offset backs off
                // half a glyph from the cell centre (Helvetica-class average advance is ~0.55em, so
                // half is ~0.275em), and the width cap keeps a wide glyph from spilling into its
                // neighbour. Spaces are skipped: they paint nothing and would only cost operators.
                // The shared single-run path below cannot express this — it would bunch the whole
                // value into the left of cell 0. Note this returns before that path, and every
                // non-comb caller passes combLen == 0, so nothing here can affect them.
                if (combLen > 0)
                {
                    double cellW = fieldW / combLen;
                    fontSize = Math.Max(6, Math.Min(fontSize, Math.Min(fieldH * 0.85, cellW * 1.4)));
                    string oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                    if (oneLine.Length > combLen) oneLine = oneLine[..combLen];
                    double combY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                    if (combY < 1) combY = 1;

                    // Invariant, like every number written into a content stream below: string
                    // interpolation formats with the OS culture, and the comma decimal separator of
                    // de-DE and most European locales is not a valid PDF number token — the whole
                    // appearance stream then fails to execute in a strict viewer (upstream v1.7.4).
                    var csb = new System.Text.StringBuilder();
                    csb.Append(FormattableString.Invariant($"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n"));
                    csb.Append(FormattableString.Invariant($"BT\n{fontName} {fontSize:F2} Tf\n0 g\n"));
                    for (int i = 0; i < oneLine.Length; i++)
                    {
                        if (oneLine[i] == ' ') continue;
                        double gx = i * cellW + cellW / 2 - fontSize * 0.275;
                        // Same EscapePdfString the run path uses, so WinAnsi folding and (, ), \
                        // escaping are identical for a comb cell and an ordinary field.
                        csb.Append(FormattableString.Invariant(
                            $"1 0 0 1 {gx:F2} {combY:F2} Tm\n({EscapePdfString(oneLine[i].ToString())}) Tj\n"));
                    }
                    csb.Append("ET\nQ\nEMC");

                    var combXobj = BuildFormXObject(fontName, fieldW, fieldH, csb.ToString());
                    if (combXobj is null) return;
                    AttachAppearance(widgetAnn, combXobj);
                    return;
                }

                // Tj shows a string; it has no concept of a line break, so a value with newlines
                // in it drew as one run with the breaks swallowed (they survived into the literal
                // as \r / \n escapes and painted nothing). Lay the value out into lines and show
                // each one, moving down by the leading between them.
                List<string> lines = isMultiLine
                    ? WrapFieldText(text, Math.Max(1, fieldW - pad * 2), fontSize)
                    : new List<string> { text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') };

                double leading = fontSize * 1.16;
                // PDF baselines are measured from the bottom of the field rect. Multiline text
                // starts at the top and runs down; a single line stays vertically centred.
                double textY = isMultiLine ? fieldH - fontSize
                                           : (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                // Invariant: a comma decimal from the OS culture is not a valid PDF number token.
                var sb = new System.Text.StringBuilder();
                sb.Append(FormattableString.Invariant($"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n"));
                sb.Append(FormattableString.Invariant(
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n{leading:F2} TL\n{pad:F2} {textY:F2} Td\n"));
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) sb.Append("T*\n");   // down one leading, back to the left inset
                    sb.Append($"({EscapePdfString(lines[i])}) Tj\n");
                }
                sb.Append("ET\nQ\nEMC");

                // Lines past the bottom of the box are clipped by the "re W n" above, the same
                // way a viewer clips an over-full field.
                var xobj = BuildFormXObject(fontName, fieldW, fieldH, sb.ToString());
                if (xobj is null) return;
                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Splits a multiline field's value into the lines its appearance should draw: the value's
        /// own line breaks first, then greedy word-wrap to the field's inner width.
        /// </summary>
        /// <remarks>
        /// Measured with Arial, which is metric-compatible with the Helvetica the generated
        /// appearance stream asks for, so the wrap lands where the drawn glyphs do.
        /// </remarks>
        private static List<string> WrapFieldText(string text, double innerWidth, double fontSize)
        {
            var typeface = new Typeface("Arial");
            double Width(string s) => new FormattedText(
                s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, 1.0).Width;

            var lines = new List<string>();
            foreach (var para in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string current = string.Empty;
                foreach (var word in para.Split(' '))
                {
                    string candidate = current.Length == 0 ? word : current + " " + word;
                    // A single word wider than the field can't be broken any further — let it
                    // run on and be clipped rather than dropping it onto an empty line.
                    if (current.Length > 0 && Width(candidate) > innerWidth)
                    {
                        lines.Add(current);
                        current = word;
                    }
                    else current = candidate;
                }
                lines.Add(current);
            }
            return lines;
        }

        /// <summary>
        /// Generates /AP /N (checked) and /Off (unchecked) appearance streams for a
        /// checkbox/radio widget and sets them on the annotation. Both states are always
        /// generated; /AS selects the active one.
        /// </summary>
        private void GenerateCheckBoxAppearance(PdfDictionary widgetAnn, bool isChecked, string onVal, double fieldW, double fieldH)
        {
            _ = isChecked; // both AP states always generated; /AS selects the active one
            try
            {
                double m  = Math.Min(fieldW, fieldH) * 0.1;
                double iw = fieldW - m * 2;
                double ih = fieldH - m * 2;

                // Checked: ZapfDingbats "4" = check mark, centred in the field.
                double fs = Math.Min(iw, ih) * 0.85;
                double tx = (fieldW - fs * 0.6) / 2;
                double ty = (fieldH - fs) / 2 + fs * 0.15;

                // Invariant: a comma decimal from the OS culture is not a valid PDF number token.
                string checkedContent = FormattableString.Invariant(
                    $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ");
                string offContent     = "q\nQ"; // empty — just clears

                var checkedXobj = BuildFormXObject("/ZaDb", fieldW, fieldH, checkedContent, isZaDb: true);
                var offXobj     = BuildFormXObject("/ZaDb", fieldW, fieldH, offContent,     isZaDb: true);
                if (checkedXobj is null || offXobj is null) return;

                var nDict = new PdfDictionary(_doc);
                nDict.Elements[onVal]  = checkedXobj.Reference;
                nDict.Elements["/Off"] = offXobj.Reference;

                var apDict = new PdfDictionary(_doc);
                apDict.Elements["/N"] = nDict;
                widgetAnn.Elements["/AP"] = apDict;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateCheckBoxAppearance: {ex}"); }
        }

        /// <summary>
        /// Creates an indirect PdfDictionary stream object representing a Form XObject,
        /// suitable for use as an /AP /N appearance stream.
        /// </summary>
        /// <summary>
        /// True when <paramref name="text"/> contains a character the WinAnsi appearance path
        /// cannot represent, and would therefore write as '?'. Deliberately mirrors
        /// <see cref="EscapePdfString"/>'s decision exactly — anything below U+0100 passes straight
        /// through, and above that only what <c>WinAnsiHighMap</c> folds — so the two can never
        /// disagree about which values are safe for the literal path.
        /// </summary>
        private static bool NeedsEmbeddedFont(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (c >= 256 && !WinAnsiHighMap.ContainsKey(c)) return true;
            return false;
        }

        /// <summary>
        /// Draws a form field's /AP /N appearance through PdfSharpCore so the value is set in a real
        /// EMBEDDED font (Type0 / Identity-H with a subset and a /ToUnicode map) instead of a WinAnsi
        /// literal. Used only for values the literal path would mangle (#140).
        /// </summary>
        /// <returns>
        /// False when no covering font could be resolved, which lets the caller fall through to the
        /// legacy path — a '?' appearance is poor, but it beats a field with no appearance at all.
        /// </returns>
        private bool TryGenerateUnicodeFieldAppearance(PdfDictionary widgetAnn, string text, string? da,
            double fieldW, double fieldH, bool isMultiLine, int combLen, double pad)
        {
            if (_doc is null || fieldW <= 0 || fieldH <= 0) return false;
            try
            {
                var (_, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = isMultiLine ? Math.Max(6, fontSize)
                                       : Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // Same family choice the annotation burn-in makes, so a value rendered here and the
                // same text placed as an annotation resolve to one face rather than two.
                var font = TdpFontResolver.TryCreate(
                    FontCoverage.PickFamily(PdfFontStyle.DefaultFamily, text), fontSize, XFontStyle.Regular);
                if (font is null) return false;

                var form = new XForm(_doc, new XSize(fieldW, fieldH));
                using (var gfx = XGraphics.FromForm(form))
                {
                    // XGraphics is top-down where the hand-written stream is bottom-up, so the
                    // layout is expressed as rectangles plus an alignment rather than baselines.
                    // The clip matches the legacy path's "re W n": an over-full value is cut off at
                    // the field edge exactly as a viewer would cut it.
                    gfx.IntersectClip(new XRect(0, 0, fieldW, fieldH));

                    if (combLen > 0)
                    {
                        // #158: one character per printed cell. Centre each in its own cell so the
                        // run cannot drift, matching the per-glyph Tm the legacy comb path uses.
                        double cellW = fieldW / combLen;
                        fontSize = Math.Max(6, Math.Min(fontSize, Math.Min(fieldH * 0.85, cellW * 1.4)));
                        var combFont = TdpFontResolver.TryCreate(
                            FontCoverage.PickFamily(PdfFontStyle.DefaultFamily, text), fontSize, XFontStyle.Regular);
                        if (combFont is null) return false;

                        string oneLine = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                        if (oneLine.Length > combLen) oneLine = oneLine[..combLen];
                        for (int i = 0; i < oneLine.Length; i++)
                        {
                            if (oneLine[i] == ' ') continue;
                            gfx.DrawString(oneLine[i].ToString(), combFont, XBrushes.Black,
                                new XRect(i * cellW, 0, cellW, fieldH), XStringFormats.Center);
                        }
                    }
                    else if (isMultiLine)
                    {
                        // Wrapping and the top-down start are XTextFormatter's job here; the legacy
                        // path does the same with WrapFieldText plus TL/T*.
                        new PdfSharpCore.Drawing.Layout.XTextFormatter(gfx).DrawString(text, font, XBrushes.Black,
                            new XRect(pad, 0, Math.Max(1, fieldW - pad * 2), fieldH));
                    }
                    else
                    {
                        gfx.DrawString(text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '),
                            font, XBrushes.Black,
                            new XRect(pad, 0, Math.Max(1, fieldW - pad * 2), fieldH),
                            XStringFormats.CenterLeft);
                    }
                }

                // Read the XObject only after the XGraphics is disposed: the content stream is
                // finalized on dispose, and an XForm must not be touched once it has been drawn.
                AttachAppearance(widgetAnn, form.PdfForm);
                return true;
            }
            catch (Exception ex)
            {
                // Never let this fail the save — fall back to the legacy literal path.
                System.Diagnostics.Debug.WriteLine($"TryGenerateUnicodeFieldAppearance: {ex}");
                return false;
            }
        }

        private PdfDictionary? BuildFormXObject(string fontName, double w, double h, string content, bool isZaDb = false)
        {
            if (_doc is null) return null;

            byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes(content);

            var xobj = new PdfDictionary(_doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(_doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            // Inline font resource — avoids adding top-level objects for every field.
            var fontEntry = new PdfDictionary(_doc);
            fontEntry.Elements["/Type"]     = new PdfName("/Font");
            fontEntry.Elements["/Subtype"]  = new PdfName("/Type1");
            fontEntry.Elements["/BaseFont"] = isZaDb ? new PdfName("/ZapfDingbats") : new PdfName("/Helvetica");
            if (!isZaDb)
                fontEntry.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

            var fontDict = new PdfDictionary(_doc);
            fontDict.Elements[fontName] = fontEntry;

            var res = new PdfDictionary(_doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            // Upstream v1.7.1 (#180): CreateStream, not a hand-attached PdfStream. It is the only
            // path that also writes /Length, which every PDF stream is required to carry. The old
            // reflection helper built PdfDictionary.PdfStream directly and assigned it through the
            // Stream property, which skips that one line — so every /AP /N appearance TDPdf
            // generated for a text field or checkbox went out with no /Length and the saved file
            // was structurally invalid. PdfSharpCore's own parser refuses such a stream ("Cannot
            // retrieve stream length."), and strict viewers report a damaged structure; PDFium-based
            // viewers scan on to endstream and cope, which is why it went unnoticed on screen. The
            // Debug.Assert in PdfDictionary.WriteObject that would have caught it is compiled out of
            // Release builds.
            xobj.CreateStream(bytes);

            _doc.Internals.AddObject(xobj);
            return xobj;
        }

        /// <summary>Sets /AP /N on a widget annotation to the given form XObject (indirect ref).</summary>
        private static void AttachAppearance(PdfDictionary widgetAnn, PdfDictionary xobj)
        {
            var apDict = new PdfDictionary();
            apDict.Elements["/N"] = xobj.Reference;
            widgetAnn.Elements["/AP"] = apDict;
        }

        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract the font
        /// resource name and point size.
        /// </summary>
        private static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i];
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        // Upstream v1.7.1 (#180): the generated appearance streams declare /WinAnsiEncoding, but the
        // escape that fed them replaced every character above U+00FF with '?'. WinAnsi is code page
        // 1252, whose 0x80-0x9F block holds exactly the characters that were being thrown away —
        // curly quotes and apostrophes, en/em dashes, bullets, ellipses — so a field pasted in from
        // a word processor came out full of question marks ("Hunter?s Mark").
        //
        // Upstream calls Encoding.GetEncoding(1252). We deliberately do NOT: on .NET (Core) code
        // page 1252 is not built in, and GetEncoding(1252) throws ArgumentException unless
        // CodePagesEncodingProvider is registered — which this app does not do anywhere (see the
        // remark on PdfDocumentService.FileHasEncryption, which avoids 1252 for the same reason).
        // In a static initializer that throw would surface as a TypeInitializationException on the
        // save path. This table is the complete set of CP1252 code points above U+00FF, so it is
        // equivalent for every character WinAnsi can actually represent, needs no provider
        // registration, and cannot throw. Below U+0100 CP1252 and Latin-1 agree, so those pass
        // through untouched exactly as before; anything with no WinAnsi slot (CJK and the like)
        // still falls back to '?', the same as the old behaviour.
        private static readonly Dictionary<char, char> WinAnsiHighMap = new()
        {
            ['\u20AC'] = '\u0080',   // euro sign
            ['\u201A'] = '\u0082',   // single low-9 quotation mark
            ['\u0192'] = '\u0083',   // latin small letter f with hook
            ['\u201E'] = '\u0084',   // double low-9 quotation mark
            ['\u2026'] = '\u0085',   // horizontal ellipsis
            ['\u2020'] = '\u0086',   // dagger
            ['\u2021'] = '\u0087',   // double dagger
            ['\u02C6'] = '\u0088',   // modifier letter circumflex accent
            ['\u2030'] = '\u0089',   // per mille sign
            ['\u0160'] = '\u008A',   // capital S with caron
            ['\u2039'] = '\u008B',   // single left-pointing angle quotation mark
            ['\u0152'] = '\u008C',   // capital ligature OE
            ['\u017D'] = '\u008E',   // capital Z with caron
            ['\u2018'] = '\u0091',   // left single quotation mark
            ['\u2019'] = '\u0092',   // right single quotation mark (the curly apostrophe)
            ['\u201C'] = '\u0093',   // left double quotation mark
            ['\u201D'] = '\u0094',   // right double quotation mark
            ['\u2022'] = '\u0095',   // bullet
            ['\u2013'] = '\u0096',   // en dash
            ['\u2014'] = '\u0097',   // em dash
            ['\u02DC'] = '\u0098',   // small tilde
            ['\u2122'] = '\u0099',   // trade mark sign
            ['\u0161'] = '\u009A',   // small s with caron
            ['\u203A'] = '\u009B',   // single right-pointing angle quotation mark
            ['\u0153'] = '\u009C',   // small ligature oe
            ['\u017E'] = '\u009E',   // small z with caron
            ['\u0178'] = '\u009F',   // capital Y with diaeresis
            // No WinAnsi slot of their own, but these are the folds the platform's own best-fit
            // table applies, and word processors emit them constantly.
            ['\u2010'] = '-',        // hyphen
            ['\u2011'] = '-',        // non-breaking hyphen
            ['\u2012'] = '-',        // figure dash
            ['\u2015'] = '\u0097',   // horizontal bar -> em dash
            ['\u2032'] = '\'',       // prime -> apostrophe
            ['\u2033'] = '"',        // double prime -> quotation mark
        };

        /// <summary>Escapes a string for use in a PDF literal string (parentheses syntax).</summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char raw in s)
            {
                // Fold to the single WinAnsi byte the appearance stream's /WinAnsiEncoding will read
                // BEFORE escaping, so a mapped character that happens to need escaping still gets it.
                char c = raw < 256 ? raw
                       : WinAnsiHighMap.TryGetValue(raw, out var mapped) ? mapped
                       : '?';
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns true if <paramref name="element"/> is inside a form-field overlay control
        /// (tagged <see cref="FormOverlayTag"/>). Used to let WPF handle mouse events for the
        /// TextBox / checkbox / radio / ComboBox controls natively instead of the canvas tools.
        /// </summary>
        private static bool IsFormFieldElement(DependencyObject? element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
