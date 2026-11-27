using TDPdf.Services;

internal static class SessionStateTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        string root = Path.Combine(Path.GetTempPath(), "tdpdf-tabs-" + Guid.NewGuid().ToString("N"));
        string P(string n) => Path.Combine(root, n);
        TabViewState S(string n, int page = 0, double zoom = 0, string fit = "None", string mode = "Single") =>
            new(P(n), page, zoom, fit, mode);

        // ── session view state JSON ──────────────────────────────────────────────────────────
        var states = new List<TabViewState> { S("one.pdf", 4, 1.5, "None", "Continuous"), S("two.pdf", 0, 0, "Width", "Single") };
        string json = SessionState.Serialize(states);
        var back = SessionState.Parse(json);
        Check("session: JSON round-trips", back.Count == 2 && back[0] == states[0] && back[1] == states[1], json);

        // #246: every tab keeps its own view, including the two-page strip, through save, merge and reopen.
        var perTab = new List<TabViewState>
        {
            S("a.pdf", 1, 0, "Width", "TwoPageContinuous"), S("b.pdf", 2, 0, "None", "Grid"), S("c.pdf", 3, 1.25, "None", "Continuous"),
        };
        var perTabBack = SessionState.Parse(SessionState.Serialize(perTab));
        Check("session: each tab keeps its own view mode", perTabBack.Count == 3
              && perTabBack[0].ViewMode == "TwoPageContinuous" && perTabBack[1].ViewMode == "Grid" && perTabBack[2].ViewMode == "Continuous",
              string.Join(",", perTabBack.Select(t => t.ViewMode)));
        string perTabMerged = SessionState.Merge(SessionState.Serialize(perTab), new[] { P("a.pdf"), P("b.pdf"), P("c.pdf") },
            new[] { S("b.pdf", 9, 0, "None", "Single") });
        var mergedModes = SessionState.Parse(perTabMerged).ToDictionary(t => Path.GetFileName(t.Path), t => t.ViewMode);
        Check("session: a window saving one tab leaves the others' views alone",
              mergedModes["a.pdf"] == "TwoPageContinuous" && mergedModes["b.pdf"] == "Single" && mergedModes["c.pdf"] == "Continuous",
              string.Join(",", mergedModes.Select(kv => kv.Key + "=" + kv.Value)));
        Check("session: a tab with no saved state has no view to apply", SessionState.For(perTabBack, P("none.pdf")).ViewMode == "", "");
        Check("session: empty serialises to empty", SessionState.Serialize(new List<TabViewState>()) == "", "");
        Check("session: garbage parses to empty", SessionState.Parse("{not json").Count == 0 && SessionState.Parse(null).Count == 0 && SessionState.Parse("[null]").Count == 0, "");
        var hostile = SessionState.Parse("[{\"Path\":\"" + P("x.pdf").Replace("\\", "\\\\") + "\",\"Page\":-7,\"Zoom\":-2,\"Fit\":null,\"ViewMode\":null}]");
        Check("session: negative page/zoom and nulls are clamped", hostile.Count == 1 && hostile[0].Page == 0 && hostile[0].Zoom == 0 && hostile[0].Fit == "None", "");
        Check("session: no password or content field exists",
              typeof(TabViewState).GetProperties().All(p => !p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)), "");

        // merge: this window replaces its own entries and leaves other windows' alone
        string saved = SessionState.Serialize(new[] { S("other.pdf", 9), S("one.pdf", 1) });
        string merged = SessionState.Merge(saved, new[] { P("other.pdf"), P("one.pdf"), P("two.pdf") },
                                           new[] { S("one.pdf", 4), S("two.pdf", 2) });
        var mp = SessionState.Parse(merged);
        Check("session: merge keeps other windows' state", mp.Any(s => s.Path == P("other.pdf") && s.Page == 9), merged);
        Check("session: merge prefers this window's state", mp.Any(s => s.Path == P("one.pdf") && s.Page == 4) && mp.Count == 3, merged);
        string pruned = SessionState.Merge(saved, new[] { P("one.pdf") }, Array.Empty<TabViewState>());
        Check("session: merge drops state for files no longer in the session", SessionState.Parse(pruned).Count == 1, pruned);

        Check("session: lookup finds the file", SessionState.For(states, P("ONE.pdf")).Page == 4 || OperatingSystem.IsLinux(), "");
        Check("session: lookup falls back to a plain open", SessionState.For(states, P("zzz.pdf")) is { Page: 0, Zoom: 0, Fit: "None" }, "");

        // ── restore filtering ────────────────────────────────────────────────────────────────
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(P("real.pdf"), "x");
            string tmp = Path.Combine(root, "tmp");
            Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "work.pdf"), "x");
            var (present, missing) = SessionState.SplitExisting(
                new[] { P("real.pdf"), P("gone.pdf"), Path.Combine(tmp, "work.pdf"), P("real.pdf"), null, "" },
                File.Exists, tmp);
            Check("restore: existing files are kept in order", present.Count == 1 && present[0] == P("real.pdf"), string.Join(",", present));
            Check("restore: vanished files are reported", missing.Count == 1 && missing[0] == P("gone.pdf"), string.Join(",", missing));
            Check("restore: temp working copies are never reopened", !present.Concat(missing).Any(x => x.StartsWith(tmp)), "");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
