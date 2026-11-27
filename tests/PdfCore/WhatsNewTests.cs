using TDPdf.Services;

/// <summary>
/// Pins the release-notes Markdown subset parser and the "show after an update" decision
/// (#250). The parser is fed the real v2.3.0.0 notes so a change to how the notes are written
/// that the window could not render fails here.
/// </summary>
internal static class WhatsNewTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        // The real file.
        string? dir = AppContext.BaseDirectory;
        string? path = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, ".github", "release-notes", "v2.3.0.0.md");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = Path.GetDirectoryName(dir);
        }
        Check("whatsnew: real v2.3.0.0 notes found", path is not null, "");
        if (path is not null)
        {
            var blocks = ReleaseNotesMarkdown.Parse(File.ReadAllText(path));
            Check("whatsnew: first block is the title",
                  blocks.Count > 0 && blocks[0].Kind == NotesBlockKind.Title && blocks[0].PlainText == "TDPdf v2.3.0.0",
                  blocks.Count > 0 ? blocks[0].PlainText : "none");
            Check("whatsnew: lead line is a bold paragraph",
                  blocks.Count > 1 && blocks[1].Kind == NotesBlockKind.Paragraph && blocks[1].Runs.All(r => r.Bold),
                  blocks.Count > 1 ? blocks[1].PlainText : "none");
            var headings = blocks.Where(b => b.Kind == NotesBlockKind.Heading).Select(b => b.PlainText).ToList();
            Check("whatsnew: all section headings found",
                  headings.SequenceEqual(new[] { "Pages and viewing", "Page tools", "Printing", "Editing", "Forms", "Security and privacy", "Fixes" }),
                  string.Join("|", headings));
            Check("whatsnew: bullets found", blocks.Count(b => b.Kind == NotesBlockKind.Bullet) > 30, "");
            Check("whatsnew: no raw markers leak into the text",
                  blocks.All(b => !b.PlainText.Contains("**") && !b.PlainText.Contains('`') && !b.PlainText.StartsWith("- ")), "");
            var rotate = blocks.First(b => b.PlainText.StartsWith("Rotate 180"));
            Check("whatsnew: bold inside a bullet", rotate.Runs[0].Bold && rotate.Runs[0].Text == "Rotate 180°" && !rotate.Runs[1].Bold, rotate.PlainText);
        }

        var code = ReleaseNotesMarkdown.Parse("- Type `report.v2` and **save**")[0];
        Check("whatsnew: inline code and bold in a bullet",
              code.Kind == NotesBlockKind.Bullet && code.Runs.Count == 4 && code.Runs[1].Code && code.Runs[1].Text == "report.v2" && code.Runs[3].Bold && code.Runs[3].Text == "save",
              code.PlainText);

        // Edge cases.
        Check("whatsnew: empty input gives no blocks", ReleaseNotesMarkdown.Parse("  \n ").Count == 0 && ReleaseNotesMarkdown.Parse(null).Count == 0, "");
        var p = ReleaseNotesMarkdown.Parse("one\ntwo\r\n\r\nthree");
        Check("whatsnew: wrapped lines join into one paragraph", p.Count == 2 && p[0].PlainText == "one two" && p[1].PlainText == "three", $"{p.Count}");
        var stray = ReleaseNotesMarkdown.ParseInline("a ** b ` c");
        Check("whatsnew: unbalanced markers stay literal", string.Concat(stray.Select(r => r.Text)) == "a ** b ` c" && stray.All(r => !r.Bold && !r.Code), "");
        var link = ReleaseNotesMarkdown.ParseInline("see [the guide](https://x.example/a) now");
        Check("whatsnew: link keeps its label only", string.Concat(link.Select(r => r.Text)) == "see the guide now", string.Concat(link.Select(r => r.Text)));
        var quote = ReleaseNotesMarkdown.Parse("> **Note** text");
        Check("whatsnew: quote marker dropped", quote.Count == 1 && quote[0].PlainText == "Note text" && quote[0].Runs[0].Bold, "");

        // Decision.
        var v3 = new Version(3, 0, 0, 0);
        Check("whatsnew: fresh install shows nothing", !WhatsNewPolicy.ShouldShow(null, false, v3, true, true), "");
        Check("whatsnew: first launch of a build with the feature after an update shows", WhatsNewPolicy.ShouldShow(null, true, v3, true, true), "");
        Check("whatsnew: older stored version shows", WhatsNewPolicy.ShouldShow("2.3.0.0", false, v3, true, true), "");
        Check("whatsnew: same version does not show", !WhatsNewPolicy.ShouldShow("3.0.0.0", true, v3, true, true), "");
        Check("whatsnew: newer stored version (downgrade) does not show", !WhatsNewPolicy.ShouldShow("3.1.0.0", true, v3, true, true), "");
        Check("whatsnew: user turned it off", !WhatsNewPolicy.ShouldShow("2.3.0.0", true, v3, false, true), "");
        Check("whatsnew: policy turned it off", !WhatsNewPolicy.ShouldShow("2.3.0.0", true, v3, true, false), "");
        Check("whatsnew: unreadable stored value does not show", !WhatsNewPolicy.ShouldShow("garbage", true, v3, true, true), "");
        Check("whatsnew: version compare is numeric, not text", WhatsNewPolicy.ShouldShow("2.9.0.0", true, new Version(2, 10, 0, 0), true, true), "");
        Check("whatsnew: stored value is the running version", WhatsNewPolicy.VersionToStore(v3) == "3.0.0.0", "");
        // Every version missed since the last one seen, newest first.
        var avail = new[] { "1.30.0.0", "2.0.0.0", "2.0.1.0", "2.2.1.0", "2.3.0.0", "2.4.0.0", "3.0.0.0", "3.1.0.0" }
            .Select(Version.Parse).ToList();
        string Seq(IReadOnlyList<Version> l) => string.Join(",", l);
        var skipped = WhatsNewPolicy.VersionsToShow(avail, "2.2.1.0", v3);
        Check("whatsnew: skipped updates show each missed version, newest first", Seq(skipped) == "3.0.0.0,2.4.0.0,2.3.0.0", Seq(skipped));
        Check("whatsnew: one-step update shows just the running version", Seq(WhatsNewPolicy.VersionsToShow(avail, "2.4.0.0", v3)) == "3.0.0.0", "");
        Check("whatsnew: nothing stored shows only the running version", Seq(WhatsNewPolicy.VersionsToShow(avail, null, v3)) == "3.0.0.0", "");
        Check("whatsnew: unreadable stored value shows only the running version", Seq(WhatsNewPolicy.VersionsToShow(avail, "garbage", v3)) == "3.0.0.0", "");
        Check("whatsnew: versions newer than the running one are never shown", !WhatsNewPolicy.VersionsToShow(avail, "2.0.0.0", v3).Contains(new Version(3, 1, 0, 0)), "");
        var old = WhatsNewPolicy.VersionsToShow(avail, "1.0.0.0", v3);
        Check("whatsnew: notes older than 2.0.1.0 are never offered", old.All(x => x >= WhatsNewPolicy.OldestNotes) && old.Contains(new Version(2, 0, 1, 0)), Seq(old));
        var many = Enumerable.Range(0, 30).Select(i => new Version(2, 5, i, 0)).ToList();
        Check("whatsnew: a long gap is capped", WhatsNewPolicy.VersionsToShow(many, "2.0.1.0", new Version(2, 5, 29, 0)).Count == WhatsNewPolicy.MaxVersionsShown, "");
        Check("whatsnew: resource name gives its version", WhatsNewPolicy.VersionFromResourceName("TDPdf.ReleaseNotes.v2.3.0.0.md") == new Version(2, 3, 0, 0), "");
        Check("whatsnew: other resource names give no version",
            WhatsNewPolicy.VersionFromResourceName("TDPdf.ReleaseNotes.md") is null
            && WhatsNewPolicy.VersionFromResourceName("TDPdf.THIRD-PARTY-NOTICES.md") is null
            && WhatsNewPolicy.VersionFromResourceName("TDPdf.ReleaseNotes.v2.3.md") is null, "");
    }
}
