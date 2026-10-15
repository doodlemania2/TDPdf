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
///   * <b>only pure-Hebrew lines are reordered.</b> A mixed line (Latin or digits inside it) or an
///     Arabic line keeps the path it always had; reversing either whole would be worse than not.
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
        Check("Hebrew + Latin is NOT reordered", !RtlTextLayout.IsHebrewRightToLeftLine("שלום world"), "");
        Check("Hebrew + digits is NOT reordered", !RtlTextLayout.IsHebrewRightToLeftLine("שלום 123"), "");
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
    }
}
