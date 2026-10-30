using System.Globalization;
using TDPdf.Services;

/// <summary>
/// Pins the Hebrew visual-order burn: <see cref="RtlTextLayout"/>. Upstream KillerPDF #435.
/// </summary>
/// <remarks>
/// PdfSharpCore does no bidi, so a Hebrew annotation that reads correctly on screen saved with its
/// letters in typed order — backwards. The burn now draws a pure-Hebrew line element by element
/// from the right edge. The properties that must hold:
///
///   * <b>only Hebrew lines are reordered.</b> Latin and digits inside one keep their left-to-right
///     order through the bidi rules; an Arabic line keeps the path it always had (no shaping).
///   * <b>the order really is reversed</b>, with and without letter spacing.
///   * <b>the extent does not move.</b> The line covers exactly the span its left-to-right draw
///     would, so the box, fill and underline still line up with the ink.
///   * <b>edge spaces and punctuation stay put.</b> Only the span between the first and last
///     Hebrew letter is reversed, as the bidi algorithm does inside a left-to-right paragraph.
///   * <b>niqqud stays on its letter.</b> Reversing chars instead of text elements would put a
///     vowel point before its base.
/// </remarks>
internal static class RtlLayout
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── Hebrew visual order on save (upstream #435) ───────────────");

        const double eps = 1e-9;

        // ── detection ─────────────────────────────────────────────────────────────────────
        Check("pure Hebrew is right-to-left", RtlTextLayout.IsHebrewRightToLeftLine("שלום"), "");
        Check("Hebrew with spaces and punctuation is right-to-left",
              RtlTextLayout.IsHebrewRightToLeftLine("שלום, עולם!"), "");
        Check("Hebrew with niqqud is right-to-left", RtlTextLayout.IsHebrewRightToLeftLine("שָׁלוֹם"), "");
        Check("Hebrew presentation forms count as Hebrew",
              RtlTextLayout.IsHebrewRightToLeftLine("שׁבּ"), "");
        // #435 follow-up: mixed lines now go through the bidi subset instead of logical order.
        Check("Hebrew + Latin is reordered", RtlTextLayout.IsHebrewRightToLeftLine("שלום world"), "");
        Check("Hebrew + digits is reordered", RtlTextLayout.IsHebrewRightToLeftLine("שלום 123"), "");
        Check("Hebrew + Arabic is NOT reordered (needs shaping)",
              !RtlTextLayout.IsHebrewRightToLeftLine("שלום مرحبا"), "");
        Check("Arabic is NOT reordered (needs shaping)", !RtlTextLayout.IsHebrewRightToLeftLine("مرحبا"), "");
        Check("Latin is NOT reordered", !RtlTextLayout.IsHebrewRightToLeftLine("hello"), "");
        Check("empty / punctuation-only is NOT reordered",
              !RtlTextLayout.IsHebrewRightToLeftLine("") && !RtlTextLayout.IsHebrewRightToLeftLine(" ,.!")
              && !RtlTextLayout.IsHebrewRightToLeftLine(null), "");

        // ── order ─────────────────────────────────────────────────────────────────────────
        // Distinct advances per letter, so a mirrored offset cannot coincide with an unmirrored
        // one by accident: alef 1, bet 2, gimel 3.
        static double Adv(string s) => s[0] switch { 'א' => 1, 'ב' => 2, 'ג' => 3, _ => 10 };

        var flat = RtlTextLayout.Layout("אבג", 0, Adv);
        Check("no spacing: first-typed letter is rightmost",
              flat.Count == 3
              && Math.Abs(flat[0].X - 5) < eps && Math.Abs(flat[1].X - 3) < eps && Math.Abs(flat[2].X - 0) < eps,
              string.Join(", ", flat.Select(p => $"{p.Cluster}@{p.X}")));

        var spaced = RtlTextLayout.Layout("אבג", 2, Adv);
        Check("spacing 2: offsets reversed with the gaps between",
              spaced.Count == 3
              && Math.Abs(spaced[0].X - 9) < eps && Math.Abs(spaced[1].X - 5) < eps && Math.Abs(spaced[2].X - 0) < eps,
              string.Join(", ", spaced.Select(p => $"{p.Cluster}@{p.X}")));

        var nan = RtlTextLayout.Layout("אבג", double.NaN, Adv);
        Check("NaN spacing degrades to none",
              nan.Count == 3 && Math.Abs(nan[0].X - 5) < eps && Math.Abs(nan[2].X) < eps, "");

        // ── extent ────────────────────────────────────────────────────────────────────────
        // The RTL draw must cover exactly the left-to-right span: leftmost ink at 0, rightmost
        // advance ending at TextLetterSpacing.Width, and each element at the mirror of where the
        // left-to-right layout would put it.
        foreach (double sp in new[] { 0.0, 2.0, -0.5 })
        {
            const string word = "גבא בג";
            var rtl = RtlTextLayout.Layout(word, sp, Adv);
            var ltr = TextLetterSpacing.Layout(word, sp, Adv);
            double ltrWidth = sp == 0
                ? TextLetterSpacing.Clusters(word).Sum(Adv)   // what the whole-string burn covers
                : TextLetterSpacing.Width(word, sp, s => double.NaN, Adv);
            double left = rtl.Min(p => p.X);
            double right = rtl.Max(p => p.X + Adv(p.Cluster));
            bool mirrored = rtl.Count == ltr.Count && rtl.Select((p, i) =>
                Math.Abs(p.X - (ltrWidth - ltr[i].X - Adv(ltr[i].Cluster))) < eps).All(b => b);
            Check($"spacing {sp}: RTL extent equals the LTR extent",
                  Math.Abs(left) < eps && Math.Abs(right - ltrWidth) < eps,
                  $"[{left}, {right}] vs [0, {ltrWidth}]");
            Check($"spacing {sp}: every element sits at the mirror of its LTR offset", mirrored, "");
        }

        // ── edge neutrals ─────────────────────────────────────────────────────────────────
        // A space or punctuation mark outside the first..last Hebrew letter sits against the
        // left-to-right paragraph edge, so WPF keeps it where it was typed; only the span between
        // the letters is reversed. Advances: alef 1, bet 2, anything else 10.
        var period = RtlTextLayout.Layout("אב.", 0, Adv);
        Check("trailing period stays on the right",
              period.Count == 3 && Math.Abs(period[2].X - 3) < eps
              && Math.Abs(period[0].X - 2) < eps && Math.Abs(period[1].X) < eps,
              string.Join(", ", period.Select(p => $"{p.Cluster}@{p.X}")));
        var lead = RtlTextLayout.Layout(" אב", 0, Adv);
        Check("leading space stays on the left",
              lead.Count == 3 && Math.Abs(lead[0].X) < eps
              && Math.Abs(lead[1].X - 12) < eps && Math.Abs(lead[2].X - 10) < eps,
              string.Join(", ", lead.Select(p => $"{p.Cluster}@{p.X}")));
        var inner = RtlTextLayout.Layout("א, ב", 0, Adv);
        Check("punctuation between Hebrew letters is reversed with them",
              inner.Count == 4 && Math.Abs(inner[3].X) < eps && Math.Abs(inner[0].X - 22) < eps,
              string.Join(", ", inner.Select(p => $"{p.Cluster}@{p.X}")));

        // ── niqqud ────────────────────────────────────────────────────────────────────────
        // shin + qamats + shin dot, lamed, vav + holam, final mem: four letters, seven chars.
        const string shalom = "שָׁלוֹם";
        var marks = RtlTextLayout.Layout(shalom, 0, s => 1);
        static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;
        Check("niqqud: one element per letter, marks kept with their base",
              marks.Count == 4 && marks.All(p => !IsMark(p.Cluster[0]))
              && marks[0].Cluster == "שָׁ" && marks[2].Cluster == "וֹ",
              string.Join(" | ", marks.Select(p => $"{p.Cluster.Length}@{p.X}")));
        Check("niqqud: the base letter + marks are drawn as a unit, rightmost first",
              marks.Count == 4 && Math.Abs(marks[0].X - 3) < eps && Math.Abs(marks[3].X) < eps, "");

        // ── mixed lines: the bidi subset (#435 follow-up) ─────────────────────────────────
        // Expected strings are what WPF shows, read left to right on screen.
        static string Rev(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
        void V(string label, string text, bool rtl, string expected)
        {
            string got = RtlTextLayout.VisualText(text, rtl);
            Check($"{(rtl ? "RTL" : "LTR")} box: {label}", got == expected, $"got \"{got}\" want \"{expected}\"");
        }
        const string shin = "שלום", olam = "עולם";
        string SH = Rev(shin), OL = Rev(olam);

        V("Hebrew then number: the number joins the Hebrew run", "שלום 123", false, "123 " + SH);
        V("number then Hebrew", "123 שלום", false, "123 " + SH);
        V("Hebrew, number, Hebrew", "שלום 123 עולם", false, OL + " 123 " + SH);
        V("Hebrew then Latin", "שלום world", false, SH + " world");
        V("Latin, Hebrew, Latin", "abc שלום def", false, "abc " + SH + " def");
        V("two Hebrew words inside Latin", "abc שלום עולם def", false, "abc " + OL + " " + SH + " def");
        V("thousands and decimal separators stay in the number", "שלום 1,234.5 עולם", false, OL + " 1,234.5 " + SH);
        V("percent sign stays with its number", "שלום 50% עולם", false, OL + " 50% " + SH);
        V("currency sign stays with its number", "שלום $5 עולם", false, OL + " $5 " + SH);
        V("date separators stay in the number", "שלום 1/2/2026", false, "1/2/2026 " + SH);
        V("leading minus in Latin context stays put", "abc -12 שלום", false, "abc -12 " + SH);
        V("minus after Hebrew is neutral, as UAX #9 says", "שלום -12", false, "12- " + SH);
        V("two numbers in Hebrew keep their own order", "שלום 12 34 עולם", false, OL + " 34 12 " + SH);
        V("brackets in Latin context are not mirrored", "abc (שלום) def", false, "abc (" + SH + ") def");
        V("brackets inside a Hebrew run are mirrored", "שלום (עולם) שלום", false,
          SH + " (" + OL + ") " + SH);
        V("trailing space stays trailing", "שלום 5 ", false, "5 " + SH + " ");
        V("pure Latin is unchanged", "hello, world 42", false, "hello, world 42");

        V("Hebrew then number", "שלום 123", true, "123 " + SH);
        V("Hebrew then Latin", "שלום world", true, "world " + SH);
        V("Latin, Hebrew, Latin", "abc שלום def", true, "def " + SH + " abc");
        V("separators stay in the number", "שלום 1,234.5", true, "1,234.5 " + SH);
        V("Latin in brackets", "שלום (abc)", true, "(abc) " + SH);
        V("edge period goes to the left", "שלום.", true, "." + SH);
        V("pure Latin stays left to right", "abc def", true, "abc def");

        // Layout agrees with VisualText, and keeps the extent of the left-to-right draw.
        {
            const string mixed = "אב 12 c";
            static double A(string s) => s[0] switch { 'א' => 1, 'ב' => 2, '1' => 3, '2' => 4, 'c' => 5, _ => 10 };
            var lay = RtlTextLayout.Layout(mixed, 0, A);
            var order = lay.OrderBy(p => p.X).Select(p => p.Cluster);
            Check("mixed Layout places clusters in VisualText order",
                  string.Concat(order) == RtlTextLayout.VisualText(mixed),
                  string.Join(", ", lay.Select(p => $"{p.Cluster}@{p.X}")));
            double right = lay.Max(p => p.X + A(p.Cluster));
            Check("mixed Layout extent equals the left-to-right sum",
                  Math.Abs(lay.Min(p => p.X)) < eps && Math.Abs(right - TextLetterSpacing.Clusters(mixed).Sum(A)) < eps,
                  $"right {right}");
            var paren = RtlTextLayout.Layout("א(ב)ג", 0, s => 1);
            Check("Layout returns the mirrored glyph inside a right-to-left run",
                  paren[1].Cluster == ")" && paren[3].Cluster == "(", string.Join(" ", paren.Select(p => p.Cluster)));
        }

        // ── wrapped lines: directions come from the whole paragraph ───────────────────────
        {
            // "שלום 123 עולם" wrapped after the Hebrew word: on its own, "123 עולם" would put the
            // number on the left (sos is L), but in the paragraph the number follows Hebrew, so
            // WPF shows it on the right of the second line.
            const string para = "שלום 123 עולם";
            int start = para.IndexOf('1');
            string line2 = para.Substring(start);
            Check("wrapped line: number after Hebrew in the paragraph sits right of the next word",
                  RtlTextLayout.VisualText(para, start, line2.Length) == OL + " 123",
                  RtlTextLayout.VisualText(para, start, line2.Length));
            Check("wrapped line alone would read differently (proves the paragraph matters)",
                  RtlTextLayout.VisualText(line2) == "123 " + OL, RtlTextLayout.VisualText(line2));

            // LocateWrappedLines maps TextLetterSpacing.Wrap output back onto the paragraphs.
            const string text = "שלום 123 עולם abc\nsecond שלום\r\nthird";
            var wrapped = TextLetterSpacing.Wrap(text, 9, s => s.Length);
            var loc = RtlTextLayout.LocateWrappedLines(text, wrapped);
            bool ok = loc is not null && loc.Count == wrapped.Count;
            for (int i = 0; ok && i < wrapped.Count; i++)
                ok = loc![i].Paragraph.Substring(loc[i].Start, wrapped[i].Length) == wrapped[i];
            Check("LocateWrappedLines finds every wrapped line in its paragraph", ok,
                  string.Join(" | ", wrapped));
            Check("LocateWrappedLines puts lines in the right paragraphs",
                  ok && loc![0].Paragraph == "שלום 123 עולם abc" && loc[^1].Paragraph == "third", "");
            Check("LocateWrappedLines refuses lines that are not the text's",
                  RtlTextLayout.LocateWrappedLines("abc def", new[] { "xyz" }) is null, "");

            // Multiple hard lines resolve independently.
            Check("each hard line is its own paragraph",
                  RtlTextLayout.VisualText("abc שלום") == "abc " + SH
                  && RtlTextLayout.VisualText("שלום abc") == SH + " abc", "");
        }
    }
}
