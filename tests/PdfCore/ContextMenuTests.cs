using TDPdf.Services;

/// <summary>
/// Pins the right-click menu model (#237): which commands each context offers, that nothing the old
/// menus offered went missing, the customised order, and the raise/lower movement. Pure, no WPF.
/// </summary>
internal static class ContextMenuTests
{
    private static ContextMenuContext C(ContextMenuKind kind, params MenuMarkKind[] marks) => new()
    {
        Kind = kind, MarkKinds = marks, CanPaste = true, CanRaise = true, CanLower = true, PagesSelected = 1, TabCount = 3,
        TabHasPath = true, TabHasDocument = true, BookmarkCanMoveUp = true, BookmarkCanMoveDown = true, BookmarksExist = true,
    };

    private static string[] Ids(MenuPlan p) => p.Ids.ToArray();

    private static readonly string[] Tools =
    {
        MenuId.ToolSelect, MenuId.ToolText, MenuId.ToolEditText, MenuId.ToolEditPicture,
        MenuId.ToolHighlight, MenuId.ToolDraw, MenuId.ToolCrop, MenuId.ToolRedact,
    };

    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("Right-click menus (ContextMenuModel, MenuLayouts, MarkOrder)");

        // ── Every command the old menus offered is still offered in the same context ────────────
        // The old canvas menu (MainWindow.BuildContextMenu) was one static list: cut, copy, paste, the
        // three picture rows (shown only with a picture selected), Edit Paragraph, OCR, Rasterize
        // Region, Print, Group, Ungroup, Arrange, the eight tool rows, Delete Selected, Undo Last,
        // Clear Page Annotations. Commands that act on a selection are listed only where a selection
        // exists; the tool rows moved to the empty-page menu by decision (#237).
        string[] pageLevel = { MenuId.EditParagraph, MenuId.Ocr, MenuId.Rasterize, MenuId.Print, MenuId.Undo, MenuId.ClearPage };
        void Offers(string what, ContextMenuContext ctx, params string[] expected)
        {
            var ids = Ids(ContextMenuModel.Plan(ctx));
            var missing = expected.Where(e => !ids.Contains(e)).ToArray();
            Check($"menu {what}: offers everything the old menu did", missing.Length == 0, string.Join(", ", missing));
        }

        Offers("empty page", C(ContextMenuKind.Page), pageLevel.Concat(Tools).Append(MenuId.Paste).ToArray());
        Offers("text selection", C(ContextMenuKind.Text), pageLevel.Append(MenuId.Copy).Append(MenuId.Paste).ToArray());
        foreach (var mk in Enum.GetValues<MenuMarkKind>().Where(k => k != MenuMarkKind.None))
            Offers($"one mark ({mk})", C(ContextMenuKind.Mark, mk),
                   pageLevel.Concat(new[] { MenuId.Cut, MenuId.Copy, MenuId.Paste, MenuId.Delete }).ToArray());
        Offers("grouped mark", new ContextMenuContext { Kind = ContextMenuKind.Mark, MarkKinds = new[] { MenuMarkKind.Shape }, HasGroup = true },
               MenuId.Ungroup);
        Offers("several marks", C(ContextMenuKind.Marks, MenuMarkKind.Shape, MenuMarkKind.Drawing),
               pageLevel.Concat(new[] { MenuId.Cut, MenuId.Copy, MenuId.Paste, MenuId.Delete, MenuId.Group, MenuId.Ungroup, MenuId.Arrange }).ToArray());
        Offers("picture", C(ContextMenuKind.Picture),
               pageLevel.Concat(new[] { MenuId.Cut, MenuId.Copy, MenuId.Paste, MenuId.Delete, MenuId.SavePicture, MenuId.ReplacePicture, MenuId.ExtractPicture }).ToArray());
        Offers("form field", C(ContextMenuKind.FormField), pageLevel.Append(MenuId.Paste).Append(MenuId.RenameField).Append(MenuId.DeleteField).ToArray());
        Offers("edited picture", C(ContextMenuKind.EditedPicture), MenuId.ReplaceImage, MenuId.DeleteImage, MenuId.ResetImageSize);
        Offers("thumbnail", C(ContextMenuKind.Thumbnail), MenuId.InsertBlank, MenuId.Duplicate, MenuId.RotateCw, MenuId.RotateCcw, MenuId.Rotate180,
               MenuId.Transform, MenuId.MoveUp, MenuId.MoveDown, MenuId.ExtractPages, MenuId.DeletePages, MenuId.TwoColumn);
        Offers("rotate button", C(ContextMenuKind.Toolbar), MenuId.RotateCw, MenuId.RotateCcw, MenuId.Rotate180, MenuId.Transform);
        Offers("tab", C(ContextMenuKind.Tab), MenuId.CloseTab, MenuId.CloseOthers, MenuId.MoveToNewWindow, MenuId.SplitMove, MenuId.SideBySide, MenuId.OpenFolder);
        Offers("bookmark", new ContextMenuContext { Kind = ContextMenuKind.Sidebar, Bookmark = BookmarkMenuState.Item },
               MenuId.BmRename, MenuId.BmAddChild, MenuId.BmSetDest, MenuId.BmMoveUp, MenuId.BmMoveDown, MenuId.BmDelete);
        Offers("several bookmarks", new ContextMenuContext { Kind = ContextMenuKind.Sidebar, Bookmark = BookmarkMenuState.Multiple, BookmarksSelected = 3 }, MenuId.BmDelete);
        Offers("empty outline", new ContextMenuContext { Kind = ContextMenuKind.Sidebar, Bookmark = BookmarkMenuState.Empty, BookmarksExist = true },
               MenuId.BmAdd, MenuId.BmDeleteAll);
        Offers("web link", new ContextMenuContext { Kind = ContextMenuKind.Link, Link = LinkMenuKind.Url }, MenuId.CopyUrl, MenuId.RemoveLink);
        Offers("mail link", new ContextMenuContext { Kind = ContextMenuKind.Link, Link = LinkMenuKind.Mail }, MenuId.CopyEmail, MenuId.RemoveLink);

        // ── Tool switching is on the empty-page menu only ───────────────────────────────────────
        foreach (var kind in Enum.GetValues<ContextMenuKind>())
        {
            var ids = Ids(ContextMenuModel.Plan(C(kind, MenuMarkKind.Shape)));
            bool hasTools = Tools.Any(ids.Contains);
            Check($"menu {kind}: tool rows {(kind == ContextMenuKind.Page ? "present" : "absent")}", hasTools == (kind == ContextMenuKind.Page), "");
        }

        // ── Quick-action rows and style controls ────────────────────────────────────────────────
        var one = ContextMenuModel.Plan(C(ContextMenuKind.Mark, MenuMarkKind.Drawing));
        Check("mark menu: quick row is copy, raise, lower, delete (no edit on a drawing)",
              one.Quick.Select(q => q.Id).SequenceEqual(new[] { MenuId.Copy, MenuId.Raise, MenuId.Lower, MenuId.Delete }), string.Join(",", one.Quick.Select(q => q.Id)));
        var text = ContextMenuModel.Plan(C(ContextMenuKind.Mark, MenuMarkKind.TextBox));
        Check("mark menu: a text box also gets Edit, first", text.Quick.Count > 0 && text.Quick[0].Id == MenuId.Edit, "");
        var many = ContextMenuModel.Plan(C(ContextMenuKind.Marks, MenuMarkKind.Shape, MenuMarkKind.Shape));
        Check("marks menu: quick row adds Group", many.Quick.Any(q => q.Id == MenuId.Group), "");
        Check("quick commands never repeat in the list", !one.Groups.SelectMany(g => g.Items).Any(i => one.Quick.Any(q => q.Id == i.Id)), "");
        var thumbs = ContextMenuModel.Plan(C(ContextMenuKind.Thumbnail));
        Check("thumbnail menu: the turn buttons come first, as the quick row",
              thumbs.Quick.Select(q => q.Id).SequenceEqual(new[] { MenuId.RotateCw, MenuId.RotateCcw, MenuId.Rotate180 }), "");
        Check("thumbnail menu: grouped by task",
              thumbs.Groups.Select(g => g.Name).SequenceEqual(new[] { "turn", "insert", "move", "extract", "view" }), string.Join(",", thumbs.Groups.Select(g => g.Name)));

        MenuStyleControls Controls(params MenuMarkKind[] k) => ContextMenuModel.StyleControlsFor(C(k.Length > 1 ? ContextMenuKind.Marks : ContextMenuKind.Mark, k));
        Check("style: a highlight gets swatches and opacity only", Controls(MenuMarkKind.Highlight) == (MenuStyleControls.Swatches | MenuStyleControls.Opacity), "");
        Check("style: text markup also gets the in-place switch",
              Controls(MenuMarkKind.Underline).HasFlag(MenuStyleControls.MarkupSwitch) && Controls(MenuMarkKind.MarkupHighlight).HasFlag(MenuStyleControls.MarkupSwitch), "");
        Check("style: drawings and shapes get a width slider", Controls(MenuMarkKind.Drawing).HasFlag(MenuStyleControls.Width) && Controls(MenuMarkKind.Shape).HasFlag(MenuStyleControls.Width), "");
        Check("style: text, signatures and stamps get none",
              Controls(MenuMarkKind.TextBox) == MenuStyleControls.None && Controls(MenuMarkKind.Signature) == MenuStyleControls.None && Controls(MenuMarkKind.Stamp) == MenuStyleControls.None, "");
        Check("style: several marks get only what all of them take",
              Controls(MenuMarkKind.Shape, MenuMarkKind.Highlight) == (MenuStyleControls.Swatches | MenuStyleControls.Opacity), "");
        Check("style: two text marks never get the switch", !Controls(MenuMarkKind.Underline, MenuMarkKind.Underline).HasFlag(MenuStyleControls.MarkupSwitch), "");
        Check("style: a mark and a text box share nothing", Controls(MenuMarkKind.Shape, MenuMarkKind.TextBox) == MenuStyleControls.None, "");
        Check("style: other menus carry no style rows", ContextMenuModel.StyleControlsFor(C(ContextMenuKind.Page)) == MenuStyleControls.None, "");

        // ── Enabled state ───────────────────────────────────────────────────────────────────────
        var nopaste = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Page, CanPaste = false });
        Check("paste is greyed with nothing to paste", !nopaste.AllEntries.First(e => e.Id == MenuId.Paste).Enabled, "");
        var raiseOff = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Mark, MarkKinds = new[] { MenuMarkKind.Shape }, CanRaise = false, CanLower = true });
        Check("raise is greyed for the top mark, lower is not",
              !raiseOff.Quick.First(q => q.Id == MenuId.Raise).Enabled && raiseOff.Quick.First(q => q.Id == MenuId.Lower).Enabled, "");
        var dupOff = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Thumbnail, PagesSelected = 0 });
        Check("duplicate is greyed with no page selected", !dupOff.AllEntries.First(e => e.Id == MenuId.Duplicate).Enabled, "");
        Check("duplicate header goes plural", ContextMenuModel.HeaderFor(MenuId.Duplicate, new ContextMenuContext { PagesSelected = 3 }) == "_Duplicate Pages", "");
        Check("rasterize header names the box when there is one", ContextMenuModel.HeaderFor(MenuId.Rasterize, new ContextMenuContext { HasBoxSelection = true }).Contains("Selected"), "");
        var twoCol = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Thumbnail, TwoColumnThumbnails = true });
        Check("two-column row shows its check mark", twoCol.AllEntries.First(e => e.Id == MenuId.TwoColumn).Checked, "");

        // Read-only windows (split view) keep only the commands that look.
        var ro = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Mark, MarkKinds = new[] { MenuMarkKind.Shape }, ReadOnly = true, CanPaste = true, CanRaise = true, CanLower = true });
        string[] mutating = { MenuId.Cut, MenuId.Paste, MenuId.Delete, MenuId.Raise, MenuId.Lower, MenuId.EditParagraph, MenuId.Rasterize, MenuId.Undo, MenuId.ClearPage };
        Check("read-only: every item that changes the document is greyed",
              mutating.All(m => !ro.AllEntries.First(e => e.Id == m).Enabled), string.Join(",", mutating.Where(m => ro.AllEntries.First(e => e.Id == m).Enabled)));
        Check("read-only: copy and print stay available",
              ro.AllEntries.First(e => e.Id == MenuId.Copy).Enabled && ro.AllEntries.First(e => e.Id == MenuId.Print).Enabled, "");
        var roTab = ContextMenuModel.Plan(new ContextMenuContext { Kind = ContextMenuKind.Tab, ReadOnly = true, TabCount = 2, TabHasDocument = true, TabHasPath = true });
        Check("read-only tab menu: no Open in Split View, the rest still works",
              !roTab.Ids.Contains(MenuId.SplitMove) && roTab.AllEntries.First(e => e.Id == MenuId.CloseTab).Enabled && roTab.AllEntries.First(e => e.Id == MenuId.SideBySide).Enabled, "");
        Check("read-only: Edit Menu stays usable", ReadOnlyGate.IsMenuCommandAllowed(ContextMenuModel.Info(MenuId.EditMenu).Header), "");

        // ── Catalogue sanity ────────────────────────────────────────────────────────────────────
        Check("every command id has a header and a help line", ContextMenuModel.AllIds.All(id =>
              !string.IsNullOrWhiteSpace(ContextMenuModel.Info(id).Header)), "");
        var allKinds = Enum.GetValues<ContextMenuKind>();
        Check("every kind's catalogue ids are known commands",
              allKinds.All(k => ContextMenuModel.ListIds(k).Concat(ContextMenuModel.QuickIds(k)).All(id => ContextMenuModel.TryInfo(id, out _))), "");
        Check("no kind lists the same id twice", allKinds.All(k => ContextMenuModel.ListIds(k).Distinct().Count() == ContextMenuModel.ListIds(k).Count), "");

        // Access keys: unique within one menu level, over many contexts.
        var contexts = new List<ContextMenuContext>();
        foreach (var kind in allKinds)
        {
            foreach (var ro2 in new[] { false, true })
            foreach (var group in new[] { false, true })
            foreach (var box in new[] { false, true })
            {
                var markKinds = Enum.GetValues<MenuMarkKind>().Where(m => m != MenuMarkKind.None).ToArray();
                contexts.Add(new ContextMenuContext
                {
                    Kind = kind, ReadOnly = ro2, HasGroup = group, HasBoxSelection = box, MarkKinds = new[] { MenuMarkKind.Shape },
                    CanPaste = true, CanRaise = true, CanLower = true, PagesSelected = 2, TabCount = 2, TabHasDocument = true, TabHasPath = true,
                    BookmarkCanMoveDown = true, BookmarkCanMoveUp = true, BookmarksExist = true, BookmarksSelected = 2,
                });
                foreach (var bm in Enum.GetValues<BookmarkMenuState>()) contexts.Add(new ContextMenuContext { Kind = kind, Bookmark = bm, BookmarksExist = true, BookmarksSelected = 2 });
                foreach (var lk in Enum.GetValues<LinkMenuKind>()) contexts.Add(new ContextMenuContext { Kind = kind, Link = lk });
                _ = markKinds;
            }
        }
        var clashes = new List<string>();
        foreach (var ctx in contexts)
        {
            var plan = ContextMenuModel.Plan(ctx);
            var keys = new Dictionary<char, string>();
            foreach (var e in plan.Groups.SelectMany(g => g.Items))
            {
                int u = e.Header.IndexOf('_');
                if (u < 0 || u + 1 >= e.Header.Length) continue;
                char k = char.ToLowerInvariant(e.Header[u + 1]);
                if (keys.TryGetValue(k, out var other)) clashes.Add($"{ctx.Kind}: {other} / {e.Id} on '{k}'");
                else keys[k] = e.Id;
            }
        }
        Check("access keys are unique within every menu", clashes.Count == 0, string.Join("; ", clashes.Distinct().Take(6)));

        // ── Customising the order ───────────────────────────────────────────────────────────────
        var markIds = ContextMenuModel.ListIds(ContextMenuKind.Page);
        var resolvedDefault = MenuLayouts.Resolve(markIds, null);
        Check("layout: no saved layout resolves to the default order", resolvedDefault.Select(r => r.Id).SequenceEqual(markIds) && resolvedDefault.All(r => !r.Hidden), "");

        var savedOrder = new List<string> { MenuId.Print, "gone.id", MenuId.Paste, MenuId.Print };
        savedOrder.AddRange(markIds.Where(id => id != MenuId.Print && id != MenuId.Paste));
        var saved = new KindLayout(savedOrder, new[] { MenuId.Ocr, "gone.id" });
        var res = MenuLayouts.Resolve(markIds, saved);
        Check("layout: saved items lead, in the saved order, once each", res[0].Id == MenuId.Print && res[1].Id == MenuId.Paste && res.Count(r => r.Id == MenuId.Print) == 1, string.Join(",", res.Select(r => r.Id)));
        Check("layout: removed ids are ignored", res.All(r => r.Id != "gone.id"), "");
        Check("layout: every default id survives", markIds.All(id => res.Any(r => r.Id == id)), "");
        Check("layout: hidden applies to known ids only", res.Single(r => r.Id == MenuId.Ocr).Hidden && res.Count(r => r.Hidden) == 1, "");

        // An item the saved layout never heard of lands after the item that precedes it by default.
        var future = new[] { "a", "b", "NEW", "c" };
        var fres = MenuLayouts.Resolve(future, new KindLayout(new[] { "c", "b", "a" }, Array.Empty<string>()));
        Check("layout: a new id goes in its default place", fres.Select(r => r.Id).SequenceEqual(new[] { "c", "b", "NEW", "a" }), string.Join(",", fres.Select(r => r.Id)));
        var first = MenuLayouts.Resolve(new[] { "NEW", "a" }, new KindLayout(new[] { "a" }, Array.Empty<string>()));
        Check("layout: a new first item stays first", first[0].Id == "NEW", "");

        // JSON
        var layouts = new Dictionary<string, KindLayout>
        {
            ["Mark"] = new(new[] { "cut", "paste" }, new[] { "print" }),
            ["Page"] = KindLayout.Empty,
        };
        string json = MenuLayouts.Serialize(layouts);
        var back = MenuLayouts.Parse(json);
        Check("json: round-trips order and hidden", back.TryGetValue("Mark", out var m) && m.Order.SequenceEqual(new[] { "cut", "paste" }) && m.Hidden.SequenceEqual(new[] { "print" }), json);
        Check("json: an empty kind is not stored", !back.ContainsKey("Page"), json);
        Check("json: unknown ids and kinds are carried, not lost, on read",
              MenuLayouts.Parse("{\"v\":9,\"menus\":{\"Zzz\":{\"order\":[\"x\"],\"hidden\":[],\"future\":1}},\"other\":true}").ContainsKey("Zzz"), "");
        Check("json: garbage reads as nothing, never throws",
              MenuLayouts.Parse("{not json").Count == 0 && MenuLayouts.Parse("[1,2]").Count == 0 && MenuLayouts.Parse("").Count == 0 && MenuLayouts.Parse(null).Count == 0
              && MenuLayouts.Parse("{\"menus\":{\"Mark\":5}}").Count == 0 && MenuLayouts.Parse("{\"menus\":{\"Mark\":{\"order\":[1,null,\"cut\"]}}}")["Mark"].Order.SequenceEqual(new[] { "cut" }), "");

        // Applying a layout to a plan
        var planPage = ContextMenuModel.Plan(C(ContextMenuKind.Page));
        var customOrder = new List<string> { MenuId.ClearPage, MenuId.Undo, MenuId.Print };
        customOrder.AddRange(ContextMenuModel.ListIds(ContextMenuKind.Page).Except(customOrder));
        var custom = MenuLayouts.Resolve(ContextMenuModel.ListIds(ContextMenuKind.Page),
            new KindLayout(customOrder, new[] { MenuId.ToolCrop, MenuId.Ocr }));
        var arranged = ContextMenuModel.Arrange(planPage, custom);
        var flat = arranged.Groups.SelectMany(g => g.Items).Select(i => i.Id).ToList();
        Check("arrange: hidden items are gone", !flat.Contains(MenuId.ToolCrop) && !flat.Contains(MenuId.Ocr), string.Join(",", flat));
        Check("arrange: the saved order leads", flat.Take(3).SequenceEqual(new[] { MenuId.ClearPage, MenuId.Undo, MenuId.Print }), string.Join(",", flat));
        Check("arrange: nothing but the hidden ones disappears", flat.Count == planPage.Groups.Sum(g => g.Items.Count) - 2, "");
        Check("arrange: separators only fall between different default groups",
              arranged.Groups.All(g => g.Items.Select(i => ContextMenuModel.GroupOf(ContextMenuKind.Page)[i.Id]).Distinct().Count() == 1), "");
        Check("arrange: the quick row and style controls are untouched", ContextMenuModel.Arrange(one, MenuLayouts.Resolve(ContextMenuModel.ListIds(ContextMenuKind.Mark), null)).Quick.Count == one.Quick.Count, "");
        // An id the context does not offer (Ungroup on an ungrouped mark) is skipped even if the layout lists it.
        var withUngroup = ContextMenuModel.Arrange(one, MenuLayouts.Resolve(ContextMenuModel.ListIds(ContextMenuKind.Mark), null));
        Check("arrange: ids the context never offers stay out", !withUngroup.Ids.Contains(MenuId.Ungroup), "");

        // The editor
        var ed = new MenuLayoutEditor(markIds, null);
        Check("editor: starts at the default and says so", ed.IsDefault && ed.Count == markIds.Count, "");
        int landed = ed.Move(0, 3);
        Check("editor: dragging a row moves it to the drop place", landed == 3 && ed.Rows[3].Id == markIds[0] && ed.Rows[0].Id == markIds[1], "");
        Check("editor: dropping past the end clamps", ed.Move(0, 999) == ed.Count - 1, "");
        Check("editor: Alt+Up on the first row stays put", ed.MoveBy(0, -1) == 0, "");
        int by = ed.MoveBy(2, +1);
        Check("editor: Alt+Down moves one row", by == 3, "");
        Check("editor: Space toggles hidden", ed.ToggleHidden(1) && ed.Rows[1].Hidden && !ed.ToggleHidden(1), "");
        ed.ToggleHidden(0);
        var layout = ed.ToLayout();
        var reopened = new MenuLayoutEditor(markIds, layout);
        Check("editor: a saved edit reopens exactly", reopened.Rows.SequenceEqual(ed.Rows), "");
        ed.Reset();
        Check("editor: Reset restores the default and saves as empty", ed.IsDefault && ed.ToLayout() == KindLayout.Empty && ed.Rows.Select(r => r.Id).SequenceEqual(markIds), "");
        Check("editor: out-of-range moves and toggles do nothing", ed.Move(-1, 2) == -1 && !ed.ToggleHidden(99), "");

        // ── Raise and lower ─────────────────────────────────────────────────────────────────────
        string[] abc() => new[] { "a", "b", "c", "d" };
        var list = abc().Select(s => new string(s.AsSpan())).ToList();
        var a = list[0]; var b = list[1]; var c = list[2]; var d = list[3];
        Check("raise: one step toward the top", MarkOrder.Move(list, new[] { b }, +1) && list.SequenceEqual(new[] { a, c, b, d }), string.Join("", list));
        Check("raise: the top mark cannot go higher", !MarkOrder.Move(list, new[] { d }, +1), "");
        Check("lower: one step toward the bottom", MarkOrder.Move(list, new[] { c }, -1) && list[0] == c && list[1] == a, string.Join("", list));
        Check("lower: the bottom mark cannot go lower", !MarkOrder.Move(list, new[] { list[0] }, -1), "");

        var l2 = new List<string> { new("a"), new("b"), new("c"), new("d") };
        var sel = new[] { l2[0], l2[1] };
        Check("raise: a block moves together and keeps its order", MarkOrder.Move(l2, sel, +1)
              && l2.IndexOf(sel[0]) == 1 && l2.IndexOf(sel[1]) == 2, string.Join("", l2));
        var l3 = new List<string> { new("a"), new("b"), new("c"), new("d") };
        var s3 = new[] { l3[1], l3[3] };
        MarkOrder.Move(l3, s3, +1);
        Check("raise: separated marks each step, the topmost one stays", l3.IndexOf(s3[0]) == 2 && l3.IndexOf(s3[1]) == 3, string.Join("", l3));
        var l4 = new List<string> { new("a"), new("b"), new("c"), new("d") };
        var s4 = l4.ToArray();
        Check("raise: everything selected moves nothing", !MarkOrder.Move(l4, s4, +1), "");
        var l5 = new List<string> { new("a"), new("b"), new("c"), new("d") };
        var mover = l5[0];
        var overlapsOnlyLast = l5[3];
        MarkOrder.Move(l5, new[] { mover }, +1, x => ReferenceEquals(x, overlapsOnlyLast));
        Check("raise: jumps past neighbours that do not overlap, to the one that does", l5.IndexOf(mover) == 3, string.Join("", l5));
        var l6 = new List<string> { new("a"), new("b") };
        MarkOrder.Move(l6, new[] { l6[0] }, +1, _ => false);
        Check("raise: with no overlap at all it still takes one step", l6.IndexOf("a") == 1 || l6[1] == "a", string.Join("", l6));
        Check("raise: a bad direction or an empty selection does nothing", !MarkOrder.Move(l6, new[] { l6[0] }, 0) && !MarkOrder.Move(l6, Array.Empty<string>(), 1), "");

        Check("can-move: a lone mark in the middle can go both ways", ContextMenuModel.CanMoveMarks(5, new[] { 2 }, +1) && ContextMenuModel.CanMoveMarks(5, new[] { 2 }, -1), "");
        Check("can-move: the top mark cannot go up, the bottom cannot go down",
              !ContextMenuModel.CanMoveMarks(5, new[] { 4 }, +1) && !ContextMenuModel.CanMoveMarks(5, new[] { 0 }, -1), "");
        Check("can-move: a block at the top cannot go up", !ContextMenuModel.CanMoveMarks(4, new[] { 2, 3 }, +1) && ContextMenuModel.CanMoveMarks(4, new[] { 2, 3 }, -1), "");
        Check("can-move: selecting everything moves nothing", !ContextMenuModel.CanMoveMarks(2, new[] { 0, 1 }, +1), "");
    }
}
