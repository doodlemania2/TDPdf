using TDPdf.Services;
using static TDPdf.Services.WindowPlacementMath;

/// <summary>Pins split view (#232): the half layout, the launch switches and the read-only command table.</summary>
internal static class SplitViewTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Split view (SplitLayout, WindowLaunch, ReadOnlyGate)");

        var one = new Box(0, 0, 1920, 1040);
        var win = new Box(100, 100, 1000, 700);

        // ── SplitLayout ────────────────────────────────────────────────────────────────────────
        var p = SplitLayout.Compute(win, new[] { one }, 640, 420);
        Check("split: one monitor gives left and right halves",
              p is { } a && a.Axis == SplitAxis.SideBySide && a.This == new Box(0, 0, 960, 1040) && a.Partner == new Box(960, 0, 960, 1040), $"{p}");

        var right = SplitLayout.Compute(win, new[] { one }, 640, 420, SplitSide.Right);
        Check("split: keeping the right half swaps the two rectangles",
              right is { } r && r.This == new Box(960, 0, 960, 1040) && r.Partner == new Box(0, 0, 960, 1040), $"{right}");

        var odd = new Box(0, 0, 1367, 728);
        var o = SplitLayout.Compute(new Box(10, 10, 800, 600), new[] { odd }, 640, 420);
        Check("split: an odd width tiles the work area with no gap or overlap",
              o is { } ob && ob.This.X + ob.This.Width == ob.Partner.X && ob.Partner.X + ob.Partner.Width == odd.X + odd.Width
              && ob.This.Width + ob.Partner.Width == odd.Width, $"{o}");

        var second = new Box(1920, 0, 2560, 1400);
        var onSecond = SplitLayout.Compute(new Box(2000, 100, 1000, 700), new[] { one, second }, 640, 420);
        Check("split: uses the monitor the window is on, not the first",
              onSecond is { } s && s.This == new Box(1920, 0, 1280, 1400) && s.Partner == new Box(3200, 0, 1280, 1400), $"{onSecond}");

        var straddle = SplitLayout.Compute(new Box(1700, 100, 800, 600), new[] { one, second }, 640, 420);
        Check("split: a window across two monitors takes the one it covers most",
              straddle is { } t && t.This.X == 1920, $"{straddle}");

        var offScreen = SplitLayout.Compute(new Box(9000, 9000, 800, 600), new[] { one, second }, 640, 420);
        Check("split: a window off every monitor falls back to the first", offScreen is { } f && f.This.X == 0, $"{offScreen}");

        var narrow = new Box(0, 0, 1000, 900);
        var stacked = SplitLayout.Compute(new Box(0, 0, 900, 800), new[] { narrow }, 640, 420);
        Check("split: too narrow for two windows stacks them top and bottom",
              stacked is { } st && st.Axis == SplitAxis.Stacked && st.This == new Box(0, 0, 1000, 450) && st.Partner == new Box(0, 450, 1000, 450), $"{stacked}");

        var tiny = SplitLayout.Compute(new Box(0, 0, 900, 700), new[] { new Box(0, 0, 1000, 700) }, 640, 420);
        Check("split: no room either way is refused (null)", tiny is null, $"{tiny}");

        var bigScale = SplitLayout.Compute(win, new[] { one }, 1000, 400);
        Check("split: a larger app scale (bigger minimum) falls back to stacking", bigScale is { } bs && bs.Axis == SplitAxis.Stacked, $"{bigScale}");

        Check("split: no monitors is refused", SplitLayout.Compute(win, Array.Empty<Box>(), 640, 420) is null, "");
        Check("split: NaN minimum is refused", SplitLayout.Compute(win, new[] { one }, double.NaN, 420) is null, "");

        // ── WindowLaunch ───────────────────────────────────────────────────────────────────────
        var l = WindowLaunch.Parse(new[] { "--new-window", @"C:\a b\x.pdf", "--window-bounds=10,20,800,600" });
        Check("launch: the tear-off form (as before) parses", l.NewWindow && !l.ReadOnly && l.Path == @"C:\a b\x.pdf" && l.Bounds == new Box(10, 20, 800, 600), $"{l}");

        l = WindowLaunch.Parse(new[] { "--new-window", "--read-only", "f.pdf", "--window-bounds=960,0,960,1040" });
        Check("launch: read-only through --new-window", l.NewWindow && l.ReadOnly && l.Path == "f.pdf" && l.Bounds == new Box(960, 0, 960, 1040), $"{l}");

        l = WindowLaunch.Parse(new[] { "--read-only", "f.pdf" });
        Check("launch: --read-only on its own is a separate window with the flag", l.ReadOnly && !l.NewWindow && l.SeparateWindow && l.Path == "f.pdf", $"{l}");

        l = WindowLaunch.Parse(new[] { "f.pdf", "--READ-ONLY" });
        Check("launch: switches are case-insensitive and may follow the path", l.ReadOnly && l.Path == "f.pdf", $"{l}");

        l = WindowLaunch.Parse(new[] { "f.pdf" });
        Check("launch: a plain path is not a separate window", !l.SeparateWindow && !l.ReadOnly && l.Path == "f.pdf" && l.Bounds is null, $"{l}");

        l = WindowLaunch.Parse(Array.Empty<string>());
        Check("launch: no arguments", !l.SeparateWindow && l.Path is null, $"{l}");

        l = WindowLaunch.Parse(new[] { "--read-only=false", "--readonly", "f.pdf" });
        Check("launch: near-miss spellings do not set read-only", !l.ReadOnly && l.Path == "f.pdf", $"{l}");

        l = WindowLaunch.Parse(new[] { "--read-only", "--window-bounds=1,2,3", "f.pdf" });
        Check("launch: malformed bounds are dropped, the rest still applies", l.ReadOnly && l.Bounds is null && l.Path == "f.pdf", $"{l}");

        l = WindowLaunch.Parse(new[] { "--window-bounds=0,0,0,0", "--read-only", "f.pdf" });
        Check("launch: implausible (zero-size) bounds are dropped", l.Bounds is null, $"{l}");

        l = WindowLaunch.Parse(new[] { "--read-only", "--frobnicate", "f.pdf" });
        Check("launch: an unknown switch is ignored, not taken for the path", l.Path == "f.pdf", $"{l}");

        l = WindowLaunch.Parse(new[] { "--read-only" });
        Check("launch: read-only with no document is still a read-only window", l.ReadOnly && l.Path is null, $"{l}");

        var args = WindowLaunch.BuildArgs(@"C:\d\y.pdf", true, new Box(960, 0, 960, 1040));
        var back = WindowLaunch.Parse(args);
        Check("launch: BuildArgs round-trips through Parse",
              args[0] == "--new-window" && back.NewWindow && back.ReadOnly && back.Path == @"C:\d\y.pdf" && back.Bounds == new Box(960, 0, 960, 1040), string.Join(" ", args));
        var plain = WindowLaunch.Parse(WindowLaunch.BuildArgs("z.pdf", false, null));
        Check("launch: BuildArgs without read-only or bounds", plain.NewWindow && !plain.ReadOnly && plain.Bounds is null && plain.Path == "z.pdf", $"{plain}");

        // ── ReadOnlyGate ───────────────────────────────────────────────────────────────────────
        Check("gate: Select and Pan are allowed", ReadOnlyGate.IsToolAllowed("Select") && ReadOnlyGate.IsToolAllowed("Pan"), "");
        bool anyEditTool = new[] { "Text", "EditText", "EditImage", "Highlight", "Underline", "Strikethrough", "Draw", "Signature", "Image",
                                   "Crop", "Redact", "Form", "Measure", "Erase", "Shape", "Stamp", "SomethingNew" }
                           .Any(ReadOnlyGate.IsToolAllowed);
        Check("gate: every other tool, and any tool added later, is refused", !anyEditTool, "");

        string[] allowed = { "_Open…", "_Close", "_Print…", "Document _Info…", "E_xit", "_Find…", "_Copy", "Pan / _Hand", "_Select", "Compare _PDFs…",
                             "Reopen Closed _Tab", "Export Pages as _Images…", "Close Tab", "Move to New Window", "View Side by Side", "End Split View" };
        Check("gate: looking and navigating commands are allowed", allowed.All(h => ReadOnlyGate.IsMenuCommandAllowed(h)),
              string.Join(", ", allowed.Where(h => !ReadOnlyGate.IsMenuCommandAllowed(h))));
        string[] refused = { "_Save", "Save _As…", "Save _Flattened…", "_New", "_Undo", "_Redo", "_Delete Selected Pages", "_Clear All Annotations", "_Merge…",
                             "_Watermark…", "Trans_form Page…", "Rasteri_ze Region…", "_OCR", "Import Fo_rm Data…", "Open in Split View", "Insert Blank Page After", "Brand New Command" };
        Check("gate: saving, editing and unknown commands are refused", refused.All(h => !ReadOnlyGate.IsMenuCommandAllowed(h)),
              string.Join(", ", refused.Where(h => ReadOnlyGate.IsMenuCommandAllowed(h))));
        Check("gate: View and Help are view-only menus, File and Tools are not",
              ReadOnlyGate.IsViewOnlyMenu("_View") && ReadOnlyGate.IsViewOnlyMenu("_Help") && !ReadOnlyGate.IsViewOnlyMenu("_File") && !ReadOnlyGate.IsViewOnlyMenu("_Tools"), "");
        Check("gate: header normalising ignores access keys, ellipsis and case",
              ReadOnlyGate.Normalize("Save _As…") == "save as" && ReadOnlyGate.Normalize(null) == "" && ReadOnlyGate.Normalize("  X... ") == "x", "");

        Check("gate: Delete and Backspace are blocked, Ctrl+Delete is not a chord we know",
              ReadOnlyGate.IsChordBlocked("Delete", false, false, false) && ReadOnlyGate.IsChordBlocked("Back", false, false, false), "");
        Check("gate: Ctrl+S, X, V, Z, Y, N, R are blocked",
              new[] { "S", "X", "V", "Z", "Y", "N", "R" }.All(k => ReadOnlyGate.IsChordBlocked(k, true, false, false)), "");
        Check("gate: Ctrl+Shift+S, Z, R, U, O are blocked",
              new[] { "S", "Z", "R", "U", "O" }.All(k => ReadOnlyGate.IsChordBlocked(k, true, true, false)), "");
        Check("gate: Ctrl+C, Ctrl+F, Ctrl+P, Ctrl+W, arrows, PageDown are not blocked",
              !ReadOnlyGate.IsChordBlocked("C", true, false, false) && !ReadOnlyGate.IsChordBlocked("F", true, false, false)
              && !ReadOnlyGate.IsChordBlocked("P", true, false, false) && !ReadOnlyGate.IsChordBlocked("W", true, false, false)
              && !ReadOnlyGate.IsChordBlocked("Down", false, false, false) && !ReadOnlyGate.IsChordBlocked("Next", false, false, false), "");
        Check("gate: Alt chords (Alt+Shift+V compare, Alt+Left history) are never blocked",
              !ReadOnlyGate.IsChordBlocked("V", false, true, true) && !ReadOnlyGate.IsChordBlocked("Left", false, false, true), "");
        Check("gate: reload waits for a save to settle", ReadOnlyGate.ReloadDebounceMs >= 250 && ReadOnlyGate.ReloadMaxRetries >= 5, "");
    }
}
