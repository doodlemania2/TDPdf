// MainWindow — copy, cut and paste for marks and pictures (#226).
//
// What the clipboard holds, and how "paste pastes what was copied last" is decided:
//   * Copying marks puts ONE private format on the system clipboard (MarksFormat, a JSON string)
//     and nothing else, so no other app ever sees the marks as text. That is also what makes a
//     paste in a second TDPdf window work.
//   * This window remembers its own copy as well, together with the clipboard sequence number the
//     copy produced (GetClipboardSequenceNumber). If the number still matches at paste time, nothing
//     has been copied since, and the remembered marks are pasted without touching the clipboard.
//     If it changed, the system clipboard is read and whatever is on it WINS: marks copied in another
//     TDPdf window, then a picture, then plain text. So copying a sentence in a browser after copying
//     a mark pastes the sentence, and copying a mark after a sentence pastes the mark.
//   * Ctrl+C with marks selected copies the marks; with no marks selected it copies the selected
//     text exactly as it always did. The two cannot both be live: selecting a mark clears the text
//     selection and starting a text selection clears the marks, so the most recent gesture decides.
//
// Where a paste lands: on the CURRENT page, 12 DIP further down and right for each repeated paste of
// the same thing, kept on the page (Services/MarkArrange.PasteShift). A pasted group is selected, so
// it can be nudged or moved straight away. One paste is one undo step.
//
// Rotation: every mark carries its own quarter-turn (text boxes, signatures and pictures), and the
// copy carries it across, so a text box turned on a rotated page pastes turned the same way.

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Imaging;
using TDPdf.Services;

namespace TDPdf
{
    public partial class MainWindow : Window
    {
        /// <summary>The private clipboard format marks travel in. Versioned so a later layout can coexist.</summary>
        private const string MarksFormat = "TDPdf.Marks.v1";

        /// <summary>Upper bounds on what a paste will accept, so a hostile clipboard cannot exhaust memory.</summary>
        private const int MaxClipboardJsonChars = 160_000_000;
        private const int MaxClipboardMarks = 2000;
        private const int MaxPastedImageBytes = 64 * 1024 * 1024;
        private const int MaxPastedTextChars = 20_000;

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        /// <summary>This window's last copy of marks.</summary>
        private sealed class MarkClip
        {
            public string Id = Guid.NewGuid().ToString("N");
            public List<PageAnnotation> Marks = new();
            /// <summary>The clipboard sequence number straight after the copy; 0 when the system clipboard could not be written.</summary>
            public uint Sequence;
        }

        private MarkClip? _markClip;
        private string? _pasteClipId;
        private int _pasteStep;

        // ── Serialisation ───────────────────────────────────────────────────────────────────

        private sealed class PointJson : JsonConverter<Point>
        {
            public override Point Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
            {
                double x = 0, y = 0;
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("point");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number) { reader.Skip(); continue; }
                    if (name.Equals("X", StringComparison.OrdinalIgnoreCase)) x = reader.GetDouble();
                    else if (name.Equals("Y", StringComparison.OrdinalIgnoreCase)) y = reader.GetDouble();
                }
                return new Point(x, y);
            }

            public override void Write(Utf8JsonWriter w, Point v, JsonSerializerOptions o)
            {
                w.WriteStartObject();
                w.WriteNumber("X", Finite(v.X));
                w.WriteNumber("Y", Finite(v.Y));
                w.WriteEndObject();
            }
        }

        private sealed class RectJson : JsonConverter<Rect>
        {
            public override Rect Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
            {
                double x = 0, y = 0, w = 0, h = 0;
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("rect");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number) { reader.Skip(); continue; }
                    double v = reader.GetDouble();
                    if (name.Equals("X", StringComparison.OrdinalIgnoreCase)) x = v;
                    else if (name.Equals("Y", StringComparison.OrdinalIgnoreCase)) y = v;
                    else if (name.Equals("Width", StringComparison.OrdinalIgnoreCase)) w = Math.Max(0, v);
                    else if (name.Equals("Height", StringComparison.OrdinalIgnoreCase)) h = Math.Max(0, v);
                }
                return new Rect(x, y, w, h);
            }

            public override void Write(Utf8JsonWriter w, Rect v, JsonSerializerOptions o)
            {
                bool empty = v.IsEmpty;
                w.WriteStartObject();
                w.WriteNumber("X", empty ? 0 : Finite(v.X));
                w.WriteNumber("Y", empty ? 0 : Finite(v.Y));
                w.WriteNumber("Width", empty ? 0 : Finite(v.Width));
                w.WriteNumber("Height", empty ? 0 : Finite(v.Height));
                w.WriteEndObject();
            }
        }

        private static double Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

        private static readonly JsonSerializerOptions MarkJson = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new PointJson(), new RectJson() }
        };

        private sealed class ClipEnvelope
        {
            public int Version { get; set; } = 1;
            public string Id { get; set; } = "";
            public List<ClipItem> Items { get; set; } = new();
        }

        private sealed class ClipItem
        {
            public string Type { get; set; } = "";
            public string Json { get; set; } = "";
        }

        /// <summary>The kinds a copy carries. Edited PDF text and image edits are changes to the page itself, not marks.</summary>
        private static readonly Dictionary<string, Type> ClipTypes = new()
        {
            ["Text"] = typeof(TextAnnotation),
            ["Ink"] = typeof(InkAnnotation),
            ["Highlight"] = typeof(HighlightAnnotation),
            ["Markup"] = typeof(MarkupAnnotation),
            ["Shape"] = typeof(ShapeAnnotation),
            ["Signature"] = typeof(SignatureAnnotation),
            ["Image"] = typeof(ImageAnnotation),
            ["Stamp"] = typeof(StampAnnotation),
        };

        private static bool IsCopyable(PageAnnotation a) => ClipTypes.ContainsValue(a.GetType());

        private static string SerializeClip(MarkClip clip)
        {
            var env = new ClipEnvelope { Id = clip.Id };
            foreach (var m in clip.Marks)
            {
                string? type = ClipTypes.FirstOrDefault(kv => kv.Value == m.GetType()).Key;
                if (type is null) continue;
                env.Items.Add(new ClipItem { Type = type, Json = JsonSerializer.Serialize(m, m.GetType(), MarkJson) });
            }
            return JsonSerializer.Serialize(env, MarkJson);
        }

        /// <summary>Marks from a clipboard payload, or null when it is not a well-formed TDPdf copy.</summary>
        private static MarkClip? DeserializeClip(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxClipboardJsonChars) return null;
            try
            {
                var env = JsonSerializer.Deserialize<ClipEnvelope>(json, MarkJson);
                if (env is null || env.Version != 1 || env.Items.Count == 0 || env.Items.Count > MaxClipboardMarks)
                    return null;
                var clip = new MarkClip { Id = string.IsNullOrEmpty(env.Id) ? Guid.NewGuid().ToString("N") : env.Id };
                foreach (var item in env.Items)
                {
                    if (!ClipTypes.TryGetValue(item.Type, out var type)) return null;
                    if (JsonSerializer.Deserialize(item.Json, type, MarkJson) is not PageAnnotation mark) return null;
                    clip.Marks.Add(mark);
                }
                return clip;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        // ── Copy and cut ────────────────────────────────────────────────────────────────────

        /// <summary>Ctrl+C and the menu's Copy: the selected marks, or the selected text when no mark is selected.</summary>
        private void CopyMarksOrText()
        {
            if (HasMarkSelection && !IsTypingTarget()) CopyMarks(cut: false);
            else CopySelectedText();
        }

        private void CopyMarks(bool cut)
        {
            var marks = _markSelection.Where(IsCopyable).ToList();
            if (marks.Count == 0)
            {
                SetStatus(HasMarkSelection ? "Edited text cannot be copied. Copy a text box or another mark instead" : "No marks selected");
                return;
            }

            // Page order, so a pasted group stacks the way it did.
            int page = marks[0].PageIndex;
            if (_annotations.TryGetValue(page, out var pageList))
                marks = marks.OrderBy(m => pageList.IndexOf(m)).ToList();

            var clip = new MarkClip { Marks = marks.Select(m => m.Clone()).ToList() };
            try
            {
                var data = new DataObject();
                data.SetData(MarksFormat, SerializeClip(clip));
                Clipboard.SetDataObject(data, true);
                clip.Sequence = GetClipboardSequenceNumber();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException
                                           or NotSupportedException or OutOfMemoryException)
            {
                // The system clipboard is locked or refused it: this window can still paste its own copy.
                clip.Sequence = 0;
            }
            _markClip = clip;
            _pasteClipId = null;
            _pasteStep = 0;

            if (cut)
            {
                DeleteSelected();
                SetStatus(marks.Count == 1 ? "Cut 1 mark" : $"Cut {marks.Count} marks");
            }
            else SetStatus(marks.Count == 1 ? "Copied 1 mark" : $"Copied {marks.Count} marks");
        }

        private void CutMarks()
        {
            if (HasMarkSelection) CopyMarks(cut: true);
        }

        // ── Paste ───────────────────────────────────────────────────────────────────────────

        private bool CanPasteNow()
        {
            if (_doc is null) return false;
            if (_markClip is not null) return true;
            try { return Clipboard.ContainsData(MarksFormat) || Clipboard.ContainsImage() || Clipboard.ContainsText(); }
            catch (ExternalException) { return false; }
        }

        /// <summary>Ctrl+V and the menu's Paste. See the file header for what "last copied" means.</summary>
        private void PasteFromClipboard()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            CommitActiveTextBox();

            uint sequence = GetClipboardSequenceNumber();
            if (_markClip is { Sequence: > 0 } own && own.Sequence == sequence)
            {
                PasteMarks(own, pageIdx);
                return;
            }

            IDataObject? data = null;
            for (int attempt = 0; attempt < 3 && data is null; attempt++)
            {
                try { data = Clipboard.GetDataObject(); }
                catch (ExternalException) { System.Threading.Thread.Sleep(30); }
            }
            if (data is null)
            {
                // Locked: fall back to this window's own copy rather than doing nothing.
                if (_markClip is not null) PasteMarks(_markClip, pageIdx);
                else SetStatus("The clipboard is busy. Try again");
                return;
            }

            try
            {
                if (data.GetDataPresent(MarksFormat) && data.GetData(MarksFormat) is string json)
                {
                    var clip = DeserializeClip(json);
                    if (clip is not null) { PasteMarks(clip, pageIdx); return; }
                    SetStatus("The copied marks could not be read");
                    return;
                }

                byte[]? image = ReadClipboardImage(data);
                if (image is not null)
                {
                    BeginPasteOf("seq:" + sequence);
                    try { PlaceImageBytes(image, PasteAnchor(pageIdx), pageIdx, "Picture pasted"); }
                    catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
                    { SetStatus("The copied picture could not be read"); }
                    return;
                }

                if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text
                    && !string.IsNullOrWhiteSpace(text))
                {
                    BeginPasteOf("seq:" + sequence);
                    PasteTextAsBox(text, pageIdx);
                    return;
                }
            }
            catch (ExternalException) { SetStatus("The clipboard is busy. Try again"); return; }

            SetStatus("Nothing to paste");
        }

        /// <summary>Counts repeated pastes of the same clipboard content so each lands a step further along.</summary>
        private void BeginPasteOf(string id)
        {
            if (id != _pasteClipId) { _pasteClipId = id; _pasteStep = 0; }
            _pasteStep++;
        }

        /// <summary>Where a picture or text pasted from another app goes: near the top-left, stepping with repeats.</summary>
        private Point PasteAnchor(int pageIdx)
        {
            double o = 24 + MarkArrange.PasteStep * (_pasteStep - 1);
            var (w, h) = EnsureRenderDims(pageIdx);
            if (w > 0 && o > w / 2) o = 24;
            if (h > 0 && o > h / 2) o = 24;
            return new Point(o, o);
        }

        private static byte[]? ReadClipboardImage(IDataObject data)
        {
            // "PNG" keeps transparency, which the plain bitmap format of most apps loses.
            try
            {
                if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png && png.Length is > 0 and <= MaxPastedImageBytes)
                    return png.ToArray();
            }
            catch (Exception ex) when (ex is ExternalException or InvalidCastException or NotSupportedException) { }

            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp
                    && bmp.PixelWidth > 0 && bmp.PixelHeight > 0
                    && (long)bmp.PixelWidth * bmp.PixelHeight * 4 <= MaxPastedImageBytes * 4L)
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex) when (ex is ExternalException or InvalidCastException or NotSupportedException
                                           or InvalidOperationException) { }
            return null;
        }

        private void PasteTextAsBox(string text, int pageIdx)
        {
            if (text.Length > MaxPastedTextChars) text = text[..MaxPastedTextChars];
            var (pw, _) = EnsureRenderDims(pageIdx);
            var anchor = PasteAnchor(pageIdx);
            var box = new TextAnnotation
            {
                PageIndex = pageIdx,
                Position = anchor,
                Content = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n'),
                FontSize = _textFontSize,
                FontName = _textFontFamily,
                Bold = _textBold,
                Italic = _textItalic,
                Underline = _textUnderline,
                LetterSpacing = _textLetterSpacing,
                // Wrap to something readable; height grows to fit.
                Width = Math.Clamp(pw > 0 ? pw - anchor.X - 24 : 400, 120, 420)
            };
            box.SetColor(_textColor);
            AddAnnotation(box);
            RenderAllAnnotations(pageIdx);
            SetTool(EditTool.Select);
            SelectAnnotation(box, MarkBounds(box));
            SetStatus("Text pasted as a text box");
        }

        private void PasteMarks(MarkClip clip, int pageIdx)
        {
            if (clip.Marks.Count == 0) return;
            BeginPasteOf(clip.Id);

            var pasted = clip.Marks.Select(m => m.Clone()).ToList();
            var union = UnionOfMarks(pasted);
            var (pw, ph) = EnsureRenderDims(pageIdx);
            var (dx, dy) = IsUsableRect(union)
                ? MarkArrange.PasteShift((union.X, union.Y, union.Width, union.Height), pw, ph, _pasteStep)
                : (MarkArrange.PasteStep * _pasteStep, MarkArrange.PasteStep * _pasteStep);

            RegroupPasted(pasted);

            PushPageSnapshot(pageIdx);
            if (!_annotations.TryGetValue(pageIdx, out var list))
                _annotations[pageIdx] = list = [];
            foreach (var m in pasted)
            {
                TranslateMark(m, m.Clone(), dx, dy);
                m.PageIndex = pageIdx;
                list.Add(m);
            }
            MarkDirty();
            if (_currentTool != EditTool.Select) SetTool(EditTool.Select);
            RenderAllAnnotations(pageIdx);
            SelectMarks(pasted);
            SetStatus(pasted.Count == 1 ? "Pasted 1 mark" : $"Pasted {pasted.Count} marks");
        }

        /// <summary>
        /// A pasted copy must not join the group its original is still in. Marks that were grouped
        /// together in the copy stay grouped together, under one fresh id per original group.
        /// </summary>
        private static void RegroupPasted(List<PageAnnotation> pasted)
        {
            var fresh = new Dictionary<string, string>();
            foreach (var m in pasted)
            {
                if (m.GroupId is not { } old) continue;
                if (!fresh.TryGetValue(old, out var id)) fresh[old] = id = Guid.NewGuid().ToString("N");
                m.GroupId = id;
            }
        }
    }
}
