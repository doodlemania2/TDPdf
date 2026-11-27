using TDPdf.Services;

internal static class ClosedTabTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        string root = Path.Combine(Path.GetTempPath(), "tdpdf-tabs-" + Guid.NewGuid().ToString("N"));
        string P(string n) => Path.Combine(root, n);
        TabViewState S(string n, int page = 0, double zoom = 0, string fit = "None", string mode = "Single") =>
            new(P(n), page, zoom, fit, mode);

        // ── closed-tab stack ─────────────────────────────────────────────────────────────────
        var st = new ClosedTabStack();
        var none = new List<string>();
        Check("closed tabs: empty stack reopens nothing", st.TakeLatestExisting(_ => true, none) is null && none.Count == 0, "");

        st.Push(S("a.pdf", 1)); st.Push(S("b.pdf", 2)); st.Push(S("c.pdf", 3));
        var miss = new List<string>();
        var first = st.TakeLatestExisting(_ => true, miss);
        var second = st.TakeLatestExisting(_ => true, miss);
        Check("closed tabs: latest first, then the one before", first?.Path == P("c.pdf") && second?.Path == P("b.pdf"), $"{first?.Path} {second?.Path}");
        Check("closed tabs: page and view survive", first is { Page: 3, Fit: "None", ViewMode: "Single" }, "");

        var st2 = new ClosedTabStack();
        st2.Push(S("keep.pdf")); st2.Push(S("gone1.pdf")); st2.Push(S("gone2.pdf"));
        var skipped = new List<string>();
        var got = st2.TakeLatestExisting(p => !Path.GetFileName(p).StartsWith("gone"), skipped);
        Check("closed tabs: missing files are skipped and reported", got?.Path == P("keep.pdf") && skipped.Count == 2
              && skipped[0] == P("gone2.pdf") && skipped[1] == P("gone1.pdf"), string.Join(",", skipped));
        Check("closed tabs: skipped entries are consumed", st2.Count == 0, $"{st2.Count}");

        var st3 = new ClosedTabStack();
        st3.Push(S("only.pdf"));
        var m3 = new List<string>();
        Check("closed tabs: all missing yields null and lists them", st3.TakeLatestExisting(_ => false, m3) is null && m3.Count == 1, "");

        var cap = new ClosedTabStack();
        for (int i = 0; i < 35; i++) cap.Push(S($"f{i}.pdf", i));
        Check("closed tabs: capped at 20", cap.Count == ClosedTabStack.Capacity, $"{cap.Count}");
        var newest = cap.TakeLatestExisting(_ => true, new List<string>());
        Check("closed tabs: cap keeps the newest", newest?.Page == 34, $"{newest?.Page}");
        int n = 1; while (cap.TakeLatestExisting(_ => true, new List<string>()) is { } last) { n++; if (last.Page < 15) break; }
        Check("closed tabs: cap dropped the oldest", cap.Count == 0 && n == 20, $"{n}");

        var blank = new ClosedTabStack();
        blank.Push(null); blank.Push(new TabViewState("  ", 0, 0, "None", ""));
        Check("closed tabs: blank paths are ignored", blank.Count == 0, "");
    }
}
