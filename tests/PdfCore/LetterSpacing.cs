using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Pins the letter-spacing arithmetic: <see cref="TextLetterSpacing"/>. #135 item 2.
/// </summary>
/// <remarks>
/// Letter spacing is a feature that fails silently in the worst possible place. It is applied in
/// four separate code paths — the on-screen annotation, the measured selection box, the wrap, and
/// the PDF burn-in — and if any one of them disagrees with the others the text sits correctly on
/// screen and moves the moment you save, which is precisely the outcome the whole design exists to
/// prevent. None of that is reachable from here (three of the four are WPF), but the arithmetic all
/// four share is, and these are the properties that would be broken if it drifted:
///
///   * <b>spacing 0 changes nothing.</b> Not "comes out nearly the same" — it must not consult the
///     per-character measure at all, because a whole string kerns and a sum of characters does not.
///     Every annotation in every document the fleet has already saved is at spacing 0, so this is
///     the compatibility test, and it is written with the two measuring functions deliberately in
///     violent disagreement so that routing to the wrong one cannot possibly pass.
///   * <b>spacing goes BETWEEN characters, not after the last one.</b> n characters, n-1 gaps. A
///     trailing gap measures the box one whole unit wider than the ink, which drifts a centred or
///     right-aligned box and leaves a dead strip inside a whiteout fill.
///   * <b>negative spacing tightens</b>, symmetrically with positive.
///   * <b>wrap points move with spacing.</b> This is upstream's "spacing-aware wrapping": push the
///     characters apart and the line has to break earlier — on screen AND in the PDF, from this one
///     shared function, or the save reflows the paragraph.
///   * <b>PdfSharpCore's MeasureString really is a plain sum of advances.</b> The burn-in leans on
///     that: it draws a spaced line one character at a time and assumes the glyphs land where the
///     whole-string draw would have put them, plus the spacing. If that assumption ever stopped
///     holding, spaced text would save subtly wrong and nothing else would notice.
/// </remarks>
internal static class LetterSpacing
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("── letter spacing (#135 item 2) ──────────────────────────────");

        const double eps = 1e-9;

        // Two measures that can never be confused for one another: the "whole string" one reports a
        // number no sum of per-character advances could ever produce. If anything at spacing 0
        // reaches for the per-character function, the numbers below stop matching immediately.
        static double Whole(string s) => 1000 + s.Length;
        static double Cluster(string s) => s.Length;   // 1 per char, so "abcd" sums to 4

        // ── spacing 0 is the untouched, pre-feature path ───────────────────────────────────
        Check("spacing 0 uses the WHOLE-string measure, exactly",
              Math.Abs(TextLetterSpacing.Width("abcd", 0, Whole, Cluster) - Whole("abcd")) < eps,
              $"{TextLetterSpacing.Width("abcd", 0, Whole, Cluster)} vs {Whole("abcd")}");

        Check("spacing 0 is reported as 'none'", TextLetterSpacing.IsNone(0), "");

        // A persisted annotation is JSON the app did not necessarily write. Nonsense degrades to
        // the appearance the box has always had rather than reaching WPF and taking the page down.
        Check("NaN spacing degrades to 'none'",
              TextLetterSpacing.IsNone(double.NaN)
              && Math.Abs(TextLetterSpacing.Width("abcd", double.NaN, Whole, Cluster) - Whole("abcd")) < eps, "");
        Check("infinite spacing degrades to 'none'",
              TextLetterSpacing.IsNone(double.PositiveInfinity)
              && TextLetterSpacing.IsNone(double.NegativeInfinity), "");

        Check("any non-zero spacing is NOT 'none'",
              !TextLetterSpacing.IsNone(0.5) && !TextLetterSpacing.IsNone(-0.5), "");

        // ── between, not after ─────────────────────────────────────────────────────────────
        // "abcd" is 4 advances of 1 = 4, plus THREE gaps of 2 = 6. Ten, not twelve.
        Check("positive spacing: 4 chars get 3 gaps",
              Math.Abs(TextLetterSpacing.Width("abcd", 2, Whole, Cluster) - 10) < eps,
              TextLetterSpacing.Width("abcd", 2, Whole, Cluster).ToString("0.###"));

        Check("negative spacing tightens by the same rule",
              Math.Abs(TextLetterSpacing.Width("abcd", -0.5, Whole, Cluster) - (4 - 1.5)) < eps,
              TextLetterSpacing.Width("abcd", -0.5, Whole, Cluster).ToString("0.###"));

        // The single-character case is where a trailing gap would be most obvious, and where a
        // right-aligned box would drift by a full spacing unit if one were applied.
        Check("one character gets NO gap at all, at any spacing",
              Math.Abs(TextLetterSpacing.Width("a", 7, Whole, Cluster) - 1) < eps,
              TextLetterSpacing.Width("a", 7, Whole, Cluster).ToString("0.###"));

        // Spacing is inserted between EVERY pair, so widening it by d widens an n-character string
        // by exactly (n-1)*d — the property the burn-in and the on-screen preview both rely on.
        Check("width is linear in spacing with slope (n-1)",
              Math.Abs((TextLetterSpacing.Width("abcdef", 3, Whole, Cluster)
                        - TextLetterSpacing.Width("abcdef", 1, Whole, Cluster)) - 5 * 2) < eps, "");

        // ── Layout: the offsets the glyphs are actually drawn at ───────────────────────────
        var placed = TextLetterSpacing.Layout("abcd", 2, Cluster);
        Check("Layout places the first character at 0",
              placed.Count == 4 && Math.Abs(placed[0].X) < eps, "");
        Check("Layout steps by advance + spacing",
              Math.Abs(placed[1].X - 3) < eps && Math.Abs(placed[2].X - 6) < eps
              && Math.Abs(placed[3].X - 9) < eps,
              string.Join(",", placed.ConvertAll(p => p.X.ToString("0.#"))));
        // Last offset + its own advance == the measured width. If Layout leaked a trailing gap into
        // the extent, or Width counted one, these two would differ by exactly the spacing.
        Check("last glyph's right edge == the measured width",
              Math.Abs((placed[3].X + Cluster(placed[3].Cluster))
                       - TextLetterSpacing.Width("abcd", 2, Whole, Cluster)) < eps, "");

        // ── clusters, not chars ────────────────────────────────────────────────────────────
        // A surrogate pair is one character to the reader and two UTF-16 code units to a
        // foreach(char). Splitting on chars would drop a spacing gap through the middle of it and
        // draw each half as its own meaningless glyph.
        Check("a surrogate pair counts as ONE character",
              TextLetterSpacing.Clusters("a\U0001F600b").Count == 3,
              string.Join("|", TextLetterSpacing.Clusters("a\U0001F600b")));
        // Likewise a combining accent belongs hard against the letter it modifies. Written as a
        // base letter plus U+0301 rather than the precomposed "\u00e9", which is a single char
        // either way and would prove nothing.
        Check("a combining accent stays with its base letter",
              TextLetterSpacing.Clusters("e\u0301x").Count == 2,
              string.Join("|", TextLetterSpacing.Clusters("e\u0301x")));

        // ── spacing-aware wrapping ─────────────────────────────────────────────────────────
        // A second, plainer pair of measures for the wrap tests: every character (space included)
        // is 1 wide and a whole string is just its length, so "aa bb cc" is 8 wide. That makes the
        // wrap points readable by eye and pins any movement in them to spacing alone.
        const string para = "aa bb cc";
        static double Flat(string s) => s.Length;
        static List<string> WrapAt(double maxWidth, double spacing) =>
            TextLetterSpacing.Wrap(para, maxWidth, s => TextLetterSpacing.Width(s, spacing, Flat, Flat));

        var fits = WrapAt(8, 0);
        Check("fixture: unspaced, the paragraph fits a width of 8 on one line",
              fits.Count == 1 && fits[0] == para, string.Join("/", fits));

        // Push the characters apart by 1 and "aa bb" alone is 5 advances plus 4 gaps = 9, over the
        // width of 8 — so a break that did not exist now falls after every word.
        var loose = WrapAt(8, 1);
        Check("positive spacing moves the wrap point EARLIER",
              loose.Count == 3 && loose[0] == "aa" && loose[1] == "bb" && loose[2] == "cc",
              string.Join("/", loose));

        // Tightening moves it the other way. At a width of 5 the unspaced paragraph takes two
        // lines; at -0.5 the whole of it is 8 - 7*0.5 = 4.5 and fits on one.
        var two = WrapAt(5, 0);
        Check("fixture: unspaced, a width of 5 takes two lines",
              two.Count == 2 && two[0] == "aa bb" && two[1] == "cc", string.Join("/", two));
        var one = WrapAt(5, -0.5);
        Check("negative spacing moves the wrap point LATER",
              one.Count == 1 && one[0] == para, string.Join("/", one));

        // And the compatibility half of it: at spacing 0 the wrap must consult the whole-string
        // measure and never the per-character one. Here the per-character measure is ten times the
        // whole-string one, so a wrap that reached for it could not possibly still fit on one line.
        static double Inflated(string s) => 10 * s.Length;
        var unspacedWrap = TextLetterSpacing.Wrap(para, 8, s => TextLetterSpacing.Width(s, 0, Flat, Inflated));
        var spacedWrap = TextLetterSpacing.Wrap(para, 8, s => TextLetterSpacing.Width(s, 0.0001, Flat, Inflated));
        Check("spacing 0 wrapping never consults the per-character measure",
              unspacedWrap.Count == 1 && spacedWrap.Count > 1,
              $"unspaced={unspacedWrap.Count} line(s), spaced={spacedWrap.Count} line(s)");

        // ── the assumption the PDF burn-in rests on ────────────────────────────────────────
        // It draws a spaced line one character at a time and expects the glyphs to land where a
        // single whole-string DrawString would have put them, plus the spacing. That only holds
        // because PdfSharpCore's MeasureString neither kerns nor trims whitespace.
        {
            var doc = new PdfDocument();
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Helvetica", 24);
            double W(string s) => gfx.MeasureString(s, font).Width;

            const string line = "AV Wo 11";
            double whole = W(line);
            double summed = 0;
            foreach (var c in TextLetterSpacing.Clusters(line)) summed += W(c);
            Check("PdfSharpCore measures a string as the plain sum of its advances",
                  Math.Abs(whole - summed) < 1e-6, $"whole={whole:0.####} summed={summed:0.####}");

            Check("a space measures its real advance, not zero", W(" ") > 0, W(" ").ToString("0.####"));

            Check("burn-in width at spacing 0 == the untouched MeasureString",
                  Math.Abs(TextLetterSpacing.Width(line, 0, W, W) - whole) < 1e-9, "");

            int gaps = TextLetterSpacing.Clusters(line).Count - 1;
            Check("burn-in width at spacing 1.5 adds exactly (n-1) gaps",
                  Math.Abs(TextLetterSpacing.Width(line, 1.5, W, W) - (whole + 1.5 * gaps)) < 1e-6,
                  $"n-1={gaps}");
        }
    }
}
