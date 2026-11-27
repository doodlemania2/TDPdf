namespace TDPdf.Services
{
    /// <summary>What is under the pointer when a right-click menu opens.</summary>
    public enum ContextMenuKind
    {
        /// <summary>Empty page (nothing selected under the pointer).</summary>
        Page,
        /// <summary>A text selection.</summary>
        Text,
        /// <summary>One mark.</summary>
        Mark,
        /// <summary>Several marks.</summary>
        Marks,
        /// <summary>A picture the PDF carries, selected with the picture tool.</summary>
        Picture,
        /// <summary>A form field being authored.</summary>
        FormField,
        /// <summary>A picture edit (replace / delete / reset size).</summary>
        EditedPicture,
        /// <summary>A page thumbnail.</summary>
        Thumbnail,
        /// <summary>A tab.</summary>
        Tab,
        /// <summary>The bookmarks (outline) sidebar.</summary>
        Sidebar,
        /// <summary>The Rotate toolbar button.</summary>
        Toolbar,
        /// <summary>A link on the page.</summary>
        Link,
    }

    /// <summary>The kind of one selected mark, as far as a menu cares.</summary>
    public enum MenuMarkKind
    {
        None, Highlight, MarkupHighlight, Underline, Strikethrough, Drawing, Shape, TextBox, EditedText, Signature, Picture, Stamp,
    }

    public enum BookmarkMenuState { Item, Multiple, Empty }

    public enum LinkMenuKind { Url, Mail, Other }

    /// <summary>The controls that sit on a mark menu as rows of their own, not as commands.</summary>
    [Flags]
    public enum MenuStyleControls
    {
        None = 0,
        Swatches = 1,
        Opacity = 2,
        Width = 4,
        MarkupSwitch = 8,
    }

    /// <summary>Everything the model needs to know about the moment the menu opens. Plain data, no WPF.</summary>
    public sealed class ContextMenuContext
    {
        public ContextMenuKind Kind { get; init; }
        /// <summary>The kinds of the selected marks (one for <see cref="ContextMenuKind.Mark"/>, several for Marks).</summary>
        public IReadOnlyList<MenuMarkKind> MarkKinds { get; init; } = Array.Empty<MenuMarkKind>();
        public bool ReadOnly { get; init; }
        public bool CanPaste { get; init; }
        public bool HasGroup { get; init; }
        public bool CanRaise { get; init; }
        public bool CanLower { get; init; }
        public bool HasBoxSelection { get; init; }
        public bool HasPageSelection { get; init; }
        public int PagesSelected { get; init; }
        public bool PagesMoveAsBlock { get; init; }
        public bool TwoColumnThumbnails { get; init; }
        public int TabCount { get; init; }
        public bool TabHasPath { get; init; }
        public bool TabHasDocument { get; init; }
        public BookmarkMenuState Bookmark { get; init; }
        public int BookmarksSelected { get; init; }
        public bool BookmarkCanMoveUp { get; init; }
        public bool BookmarkCanMoveDown { get; init; }
        public bool BookmarksExist { get; init; }
        public LinkMenuKind Link { get; init; }

        public int MarkCount => MarkKinds.Count;
        public MenuMarkKind FirstMark => MarkKinds.Count > 0 ? MarkKinds[0] : MenuMarkKind.None;
    }

    /// <summary>Static facts about one command: how it reads, never what it does.</summary>
    public sealed record MenuItemInfo(string Id, string Header, string? Gesture, string? Help, string? Glyph,
                                      bool MirrorGlyph = false, string? Short = null);

    /// <summary>One row of a plan: a command id and whether it can run right now.</summary>
    public sealed record MenuEntry(string Id, string Header, bool Enabled, bool Checked = false);

    /// <summary>A run of rows with a separator before it. <paramref name="Name"/> is the default group the rows came from.</summary>
    public sealed record MenuGroup(string Name, IReadOnlyList<MenuEntry> Items);

    /// <summary>What a menu offers: the quick-action row, the style controls, then the grouped command list.</summary>
    public sealed record MenuPlan(ContextMenuKind Kind, IReadOnlyList<MenuEntry> Quick, MenuStyleControls Controls,
                                  IReadOnlyList<MenuGroup> Groups)
    {
        public IEnumerable<MenuEntry> AllEntries => Quick.Concat(Groups.SelectMany(g => g.Items));
        public IEnumerable<string> Ids => AllEntries.Select(e => e.Id);
    }

    /// <summary>Command ids. The window maps each to its action; the model orders and gates them.</summary>
    public static class MenuId
    {
        public const string Cut = "cut", Copy = "copy", Paste = "paste", Delete = "delete", Group = "group", Ungroup = "ungroup",
            Arrange = "arrange", Raise = "raise", Lower = "lower", Edit = "edit",
            SavePicture = "savePicture", ReplacePicture = "replacePicture", ExtractPicture = "extractPicture",
            EditParagraph = "editParagraph", Ocr = "ocr", Rasterize = "rasterize", Print = "print", Undo = "undo", ClearPage = "clearPage",
            ToolSelect = "tool.select", ToolText = "tool.text", ToolEditText = "tool.editText", ToolEditPicture = "tool.editPicture",
            ToolHighlight = "tool.highlight", ToolDraw = "tool.draw", ToolCrop = "tool.crop", ToolRedact = "tool.redact",
            RenameField = "renameField", DeleteField = "deleteField",
            ReplaceImage = "replaceImage", DeleteImage = "deleteImage", ResetImageSize = "resetImageSize",
            InsertBlank = "insertBlank", Duplicate = "duplicate", RotateCw = "rotateCw", RotateCcw = "rotateCcw", Rotate180 = "rotate180",
            Transform = "transform", MoveUp = "moveUp", MoveDown = "moveDown", ExtractPages = "extractPages", DeletePages = "deletePages",
            TwoColumn = "twoColumn",
            CloseTab = "closeTab", CloseOthers = "closeOthers", MoveToNewWindow = "moveToNewWindow", SplitMove = "splitMove",
            SideBySide = "sideBySide", OpenFolder = "openFolder",
            BmRename = "bmRename", BmAddChild = "bmAddChild", BmSetDest = "bmSetDest", BmMoveUp = "bmMoveUp", BmMoveDown = "bmMoveDown",
            BmDelete = "bmDelete", BmAdd = "bmAdd", BmDeleteAll = "bmDeleteAll",
            CopyUrl = "copyUrl", CopyEmail = "copyEmail", RemoveLink = "removeLink",
            EditMenu = "editMenu";
    }

    /// <summary>
    /// Which commands each right-click menu offers, in what order, for what is under the pointer.
    /// Pure: the window turns the ids into menu rows and attaches the actions, and a customised
    /// order (<see cref="MenuLayouts"/>) is applied on top with <see cref="Arrange"/>.
    /// </summary>
    public static class ContextMenuModel
    {
        // ── Command facts ──────────────────────────────────────────────────────────────

        private static MenuItemInfo I(string id, string header, string? gesture, string? help, string? glyph,
                                      bool mirror = false, string? shortName = null)
            => new(id, header, gesture, help, glyph, mirror, shortName);

        private const string RotateGlyph = "";

        private static readonly Dictionary<string, MenuItemInfo> Infos = new[]
        {
            I(MenuId.Cut, "Cu_t", "Ctrl+X", "Cut the selected marks to the clipboard", ""),
            I(MenuId.Copy, "_Copy", "Ctrl+C", "Copy the selected marks, or the selected text, to the clipboard", ""),
            I(MenuId.Paste, "_Paste", "Ctrl+V", "Paste marks, a picture or text from the clipboard onto this page", ""),
            I(MenuId.Delete, "De_lete Selected", "Delete", "Delete the selected annotation", "", false, "Delete"),
            I(MenuId.Group, "_Group", "Ctrl+G", "Group the selected marks so they move and delete together", null),
            I(MenuId.Ungroup, "_Ungroup", "Ctrl+Shift+G", "Split the selected group back into separate marks", null),
            I(MenuId.Arrange, "Arra_nge", null, "Align, space evenly or match the size of the selected marks", null),
            I(MenuId.Raise, "Rai_se", null, "Move the selected marks one step above the marks they overlap", ""),
            I(MenuId.Lower, "Lo_wer", null, "Move the selected marks one step below the marks they overlap", ""),
            I(MenuId.Edit, "_Edit", null, "Edit the text of the selected mark", ""),
            I(MenuId.SavePicture, "_Save Picture As...", null, "Save the selected picture: the original JPEG, or a PNG at its own resolution", null),
            I(MenuId.ReplacePicture, "Rep_lace Picture...", null, "Put another picture in this one's place, at the same width", null),
            I(MenuId.ExtractPicture, "E_xtract Image...", null, "Cut a box, or one colour area, out of the picture as a transparent picture mark", null),
            I(MenuId.EditParagraph, "_Edit Paragraph", "Ctrl+click", "Edit the whole paragraph under the pointer and reflow it in its own font", ""),
            I(MenuId.Ocr, "OCR Page to Clip_board", "Ctrl+Shift+O", "Recognize the current page's text with OCR and copy it to the clipboard", ""),
            I(MenuId.Rasterize, "Rasterize Re_gion…", null, "Turn a boxed area into a picture and take its text and graphics off the page (not redaction)", null),
            I(MenuId.Print, "Pr_int", "Ctrl+P", "Print the current PDF", ""),
            I(MenuId.Undo, "Und_o Last", "Ctrl+Z", "Undo the last annotation change", ""),
            I(MenuId.ClearPage, "Clear Page _Annotations", null, "Clear all annotations on this page", ""),
            I(MenuId.ToolSelect, "_Select Tool", "V", "Switch to the select tool", ""),
            I(MenuId.ToolText, "_Text Tool", "1", "Switch to the text tool", ""),
            I(MenuId.ToolEditText, "Edit Existing Text", "2", "Switch to the existing text edit tool", ""),
            I(MenuId.ToolEditPicture, "Select Picture", "3", "Switch to the picture tool: select, move, resize, replace or extract a picture the PDF carries", ""),
            I(MenuId.ToolHighlight, "_Highlight Tool", "5", "Switch to the highlight tool", ""),
            I(MenuId.ToolDraw, "_Draw Tool", "9", "Switch to the draw tool", ""),
            I(MenuId.ToolCrop, "_Crop Tool", "C", "Switch to the crop tool", ""),
            I(MenuId.ToolRedact, "_Redact", "R", "Mark areas whose content is permanently removed from the file", ""),
            I(MenuId.RenameField, "_Rename Field…", null, "Rename the selected form field", ""),
            I(MenuId.DeleteField, "_Delete Field", "Delete", "Delete the selected form field", ""),
            I(MenuId.ReplaceImage, "_Replace Image...", null, "Replace this picture with another file", ""),
            I(MenuId.DeleteImage, "_Delete Image", null, "Remove this picture from the page", ""),
            I(MenuId.ResetImageSize, "Reset _Size", null, "Put this picture back at its original size", ""),
            I(MenuId.InsertBlank, "Insert _Blank Page After", null, "Insert a blank page after the selected page", ""),
            I(MenuId.Duplicate, "_Duplicate Page", null, "Insert a copy of each selected page after the last of them", ""),
            I(MenuId.RotateCw, "Rotate CW", null, "Turn the selected pages a quarter turn clockwise", RotateGlyph),
            I(MenuId.RotateCcw, "Rotate CCW", null, "Turn the selected pages a quarter turn counter-clockwise", RotateGlyph, true),
            I(MenuId.Rotate180, "Rotate 180°", "Ctrl+Shift+U", "Turn the selected pages upside down", RotateGlyph),
            I(MenuId.Transform, "_Transform…", null, "Rotate by a fine angle, scale, flip, or straighten the page (rasterizes it to an image)", ""),
            I(MenuId.MoveUp, "Move Page _Up", null, "Move the selected page up the list", ""),
            I(MenuId.MoveDown, "Move Page Dow_n", null, "Move the selected page down the list", ""),
            I(MenuId.ExtractPages, "_Extract Page(s)", null, "Save the selected pages as a new file", ""),
            I(MenuId.DeletePages, "De_lete Page(s)", null, "Delete the selected pages", ""),
            I(MenuId.TwoColumn, "Two-_Column Thumbnails", null, "Show two page thumbnails per row", ""),
            I(MenuId.CloseTab, "_Close Tab", "Ctrl+W", "Close this document", ""),
            I(MenuId.CloseOthers, "Close _Other Tabs", "Ctrl+Shift+W", "Close every open document except this one", ""),
            I(MenuId.MoveToNewWindow, "_Move to New Window", null, "Open this document alone in a new TDPdf window", ""),
            I(MenuId.SplitMove, "Open in _Split View", null, "Move this tab to a new window beside this one", null),
            I(MenuId.SideBySide, "_View Side by Side", null, "Show this document in a read-only window beside this one. It updates when you save.", null),
            I(MenuId.OpenFolder, "Open Containing _Folder", null, "Show this document in File Explorer", ""),
            I(MenuId.BmRename, "_Rename", "F2", null, ""),
            I(MenuId.BmAddChild, "Add _child bookmark", null, null, ""),
            I(MenuId.BmSetDest, "Se_t destination to current page", null, null, ""),
            I(MenuId.BmMoveUp, "Move _up", null, null, ""),
            I(MenuId.BmMoveDown, "Move do_wn", null, null, ""),
            I(MenuId.BmDelete, "_Delete", "Delete", null, ""),
            I(MenuId.BmAdd, "_Add bookmark", null, null, ""),
            I(MenuId.BmDeleteAll, "Delete all _bookmarks", null, null, ""),
            I(MenuId.CopyUrl, "Copy _URL", null, null, ""),
            I(MenuId.CopyEmail, "Copy _Email Address", null, null, ""),
            I(MenuId.RemoveLink, "_Remove Link from PDF", null, null, ""),
            I(MenuId.EditMenu, "Edit Menu…", null, "Drag the items of this menu into the order you want, or hide the ones you never use", ""),
        }.ToDictionary(i => i.Id, StringComparer.Ordinal);

        /// <summary>Every command id the menus know.</summary>
        public static IReadOnlyCollection<string> AllIds => Infos.Keys;

        public static MenuItemInfo Info(string id) => Infos[id];

        public static bool TryInfo(string id, out MenuItemInfo info) => Infos.TryGetValue(id, out info!);

        /// <summary>The header the row shows in this context (a few change with the selection).</summary>
        public static string HeaderFor(string id, ContextMenuContext ctx)
        {
            switch (id)
            {
                case MenuId.Ocr:
                    return ctx.PagesSelected > 1 ? "OCR Selected Pages to Clip_board" : "OCR Page to Clip_board";
                case MenuId.Rasterize:
                    return ctx.HasBoxSelection ? "Rasterize Selected Re_gion…" : "Rasterize Re_gion…";
                case MenuId.Duplicate:
                    return ctx.PagesSelected > 1 ? "_Duplicate Pages" : "_Duplicate Page";
                case MenuId.MoveUp:
                    return ctx.PagesMoveAsBlock ? "Move Pages _Up" : "Move Page _Up";
                case MenuId.MoveDown:
                    return ctx.PagesMoveAsBlock ? "Move Pages Dow_n" : "Move Page Dow_n";
                case MenuId.BmDelete:
                    return ctx.Bookmark == BookmarkMenuState.Multiple ? $"_Delete ({ctx.BookmarksSelected})" : "_Delete";
                default:
                    return Infos[id].Header;
            }
        }

        // ── Catalogue ──────────────────────────────────────────────────────────────────

        private sealed record Slot(string Id, string Group, Func<ContextMenuContext, bool>? Applies, Func<ContextMenuContext, bool>? Enabled, bool Quick);

        private static Slot S(string id, string group, Func<ContextMenuContext, bool>? applies = null,
                               Func<ContextMenuContext, bool>? enabled = null, bool quick = false)
            => new(id, group, applies, enabled, quick);

        private static bool Selected(ContextMenuContext c) => c.MarkCount > 0 || c.Kind == ContextMenuKind.Picture;

        // The page-level commands every canvas menu carries (the old single menu offered them everywhere).
        private static IEnumerable<Slot> CanvasTail(bool tools)
        {
            yield return S(MenuId.EditParagraph, "text");
            yield return S(MenuId.Ocr, "page");
            yield return S(MenuId.Rasterize, "page");
            yield return S(MenuId.Print, "page");
            if (tools)
            {
                yield return S(MenuId.ToolSelect, "tools");
                yield return S(MenuId.ToolText, "tools");
                yield return S(MenuId.ToolEditText, "tools");
                yield return S(MenuId.ToolEditPicture, "tools");
                yield return S(MenuId.ToolHighlight, "tools");
                yield return S(MenuId.ToolDraw, "tools");
                yield return S(MenuId.ToolCrop, "tools");
                yield return S(MenuId.ToolRedact, "tools");
            }
            yield return S(MenuId.Undo, "edit");
            yield return S(MenuId.ClearPage, "edit");
        }

        private static readonly IReadOnlyDictionary<ContextMenuKind, Slot[]> Catalogues = new Dictionary<ContextMenuKind, Slot[]>
        {
            [ContextMenuKind.Page] = new[] { S(MenuId.Paste, "clip", null, c => c.CanPaste) }
                .Concat(CanvasTail(tools: true)).ToArray(),

            [ContextMenuKind.Text] = new[] { S(MenuId.Copy, "clip"), S(MenuId.Paste, "clip", null, c => c.CanPaste) }
                .Concat(CanvasTail(tools: false)).ToArray(),

            [ContextMenuKind.Mark] = new[]
            {
                S(MenuId.Edit, "quick", c => c.FirstMark is MenuMarkKind.TextBox or MenuMarkKind.EditedText, null, quick: true),
                S(MenuId.Copy, "quick", null, c => c.FirstMark != MenuMarkKind.EditedText, quick: true),
                S(MenuId.Raise, "quick", null, c => c.CanRaise, quick: true),
                S(MenuId.Lower, "quick", null, c => c.CanLower, quick: true),
                S(MenuId.Delete, "quick", null, null, quick: true),
                S(MenuId.Cut, "clip", null, c => c.FirstMark != MenuMarkKind.EditedText),
                S(MenuId.Paste, "clip", null, c => c.CanPaste),
                S(MenuId.Ungroup, "mark", c => c.HasGroup),
            }.Concat(CanvasTail(tools: false)).ToArray(),

            [ContextMenuKind.Marks] = new[]
            {
                S(MenuId.Copy, "quick", null, null, quick: true),
                S(MenuId.Raise, "quick", null, c => c.CanRaise, quick: true),
                S(MenuId.Lower, "quick", null, c => c.CanLower, quick: true),
                S(MenuId.Group, "quick", null, null, quick: true),
                S(MenuId.Delete, "quick", null, null, quick: true),
                S(MenuId.Cut, "clip"),
                S(MenuId.Paste, "clip", null, c => c.CanPaste),
                S(MenuId.Ungroup, "mark", null, c => c.HasGroup),
                S(MenuId.Arrange, "mark"),
            }.Concat(CanvasTail(tools: false)).ToArray(),

            [ContextMenuKind.Picture] = new[]
            {
                S(MenuId.Copy, "quick", null, null, quick: true),
                S(MenuId.Delete, "quick", null, null, quick: true),
                S(MenuId.Cut, "clip"),
                S(MenuId.Paste, "clip", null, c => c.CanPaste),
                S(MenuId.SavePicture, "picture"),
                S(MenuId.ReplacePicture, "picture"),
                S(MenuId.ExtractPicture, "picture"),
            }.Concat(CanvasTail(tools: false)).ToArray(),

            [ContextMenuKind.FormField] = new[]
            {
                S(MenuId.RenameField, "field"),
                S(MenuId.DeleteField, "field"),
                S(MenuId.Paste, "clip", null, c => c.CanPaste),
            }.Concat(CanvasTail(tools: false)).ToArray(),

            [ContextMenuKind.EditedPicture] = new[]
            {
                S(MenuId.ReplaceImage, "image"),
                S(MenuId.DeleteImage, "image"),
                S(MenuId.ResetImageSize, "image"),
            },

            [ContextMenuKind.Thumbnail] = new[]
            {
                S(MenuId.RotateCw, "quick", null, null, quick: true),
                S(MenuId.RotateCcw, "quick", null, null, quick: true),
                S(MenuId.Rotate180, "quick", null, null, quick: true),
                S(MenuId.Transform, "turn"),
                S(MenuId.InsertBlank, "insert"),
                S(MenuId.Duplicate, "insert", null, c => c.PagesSelected > 0),
                S(MenuId.MoveUp, "move"),
                S(MenuId.MoveDown, "move"),
                S(MenuId.ExtractPages, "extract"),
                S(MenuId.DeletePages, "extract"),
                S(MenuId.TwoColumn, "view"),
            },

            [ContextMenuKind.Toolbar] = new[]
            {
                S(MenuId.RotateCw, "quick", null, null, quick: true),
                S(MenuId.RotateCcw, "quick", null, null, quick: true),
                S(MenuId.Rotate180, "quick", null, null, quick: true),
                S(MenuId.Transform, "turn"),
            },

            [ContextMenuKind.Tab] = new[]
            {
                S(MenuId.CloseTab, "close"),
                S(MenuId.CloseOthers, "close", null, c => c.TabCount > 1),
                S(MenuId.MoveToNewWindow, "window"),
                S(MenuId.SplitMove, "window", c => !c.ReadOnly, c => c.TabCount > 1),
                S(MenuId.SideBySide, "window", null, c => c.TabHasDocument && c.TabHasPath),
                S(MenuId.OpenFolder, "file", null, c => c.TabHasPath),
            },

            [ContextMenuKind.Sidebar] = new[]
            {
                S(MenuId.BmRename, "edit", c => c.Bookmark == BookmarkMenuState.Item),
                S(MenuId.BmAddChild, "edit", c => c.Bookmark == BookmarkMenuState.Item),
                S(MenuId.BmSetDest, "edit", c => c.Bookmark == BookmarkMenuState.Item),
                S(MenuId.BmMoveUp, "move", c => c.Bookmark == BookmarkMenuState.Item, c => c.BookmarkCanMoveUp),
                S(MenuId.BmMoveDown, "move", c => c.Bookmark == BookmarkMenuState.Item, c => c.BookmarkCanMoveDown),
                S(MenuId.BmDelete, "delete", c => c.Bookmark != BookmarkMenuState.Empty),
                S(MenuId.BmAdd, "add", c => c.Bookmark == BookmarkMenuState.Empty),
                S(MenuId.BmDeleteAll, "bulk", c => c.Bookmark == BookmarkMenuState.Empty && c.BookmarksExist),
            },

            [ContextMenuKind.Link] = new[]
            {
                S(MenuId.CopyEmail, "copy", c => c.Link == LinkMenuKind.Mail),
                S(MenuId.CopyUrl, "copy", c => c.Link == LinkMenuKind.Url),
                S(MenuId.RemoveLink, "remove"),
            },
        };

        /// <summary>
        /// The ids a menu kind can ever list in its reorderable part, in default order: what the
        /// "Edit menu" window shows. The quick-action row is not part of it.
        /// </summary>
        public static IReadOnlyList<string> ListIds(ContextMenuKind kind)
            => Catalogues[kind].Where(s => !s.Quick).Select(s => s.Id).Distinct().ToList();

        /// <summary>The quick-action ids a menu kind can ever show.</summary>
        public static IReadOnlyList<string> QuickIds(ContextMenuKind kind)
            => Catalogues[kind].Where(s => s.Quick).Select(s => s.Id).Distinct().ToList();

        /// <summary>The default group of each reorderable id for a kind (separators fall between different groups).</summary>
        public static IReadOnlyDictionary<string, string> GroupOf(ContextMenuKind kind)
            => Catalogues[kind].Where(s => !s.Quick).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Group);

        /// <summary>Kinds whose menus end with an "Edit menu" row.</summary>
        public static bool IsCustomisable(ContextMenuKind kind) => Catalogues.ContainsKey(kind);

        // ── Plans ──────────────────────────────────────────────────────────────────────

        private static bool Enabled(string id, string header, ContextMenuContext ctx, Func<ContextMenuContext, bool>? gate)
        {
            if (ctx.ReadOnly && !ReadOnlyGate.IsMenuCommandAllowed(header)) return false;
            return gate is null || gate(ctx);
        }

        /// <summary>The default plan for a context: what the menu offers, before any customisation.</summary>
        public static MenuPlan Plan(ContextMenuContext ctx)
        {
            var slots = Catalogues[ctx.Kind].Where(s => s.Applies is null || s.Applies(ctx)).ToList();
            MenuEntry Entry(Slot s)
            {
                string header = HeaderFor(s.Id, ctx);
                bool on = Enabled(s.Id, header, ctx, s.Enabled);
                return new MenuEntry(s.Id, header, on, s.Id == MenuId.TwoColumn && ctx.TwoColumnThumbnails);
            }

            var quick = slots.Where(s => s.Quick).Select(Entry).ToList();
            var groups = new List<MenuGroup>();
            foreach (var g in slots.Where(s => !s.Quick).GroupBy(s => s.Group))
                groups.Add(new MenuGroup(g.Key, g.Select(Entry).ToList()));
            return new MenuPlan(ctx.Kind, quick, StyleControlsFor(ctx), groups);
        }

        /// <summary>
        /// Applies a customised order and hidden set to a plan. Rows keep their enabled state; a
        /// separator falls wherever two neighbours came from different default groups. Ids the plan
        /// does not offer in this context are skipped, and the quick row never changes.
        /// </summary>
        public static MenuPlan Arrange(MenuPlan plan, IReadOnlyList<(string Id, bool Hidden)> resolved)
        {
            var offered = plan.Groups.SelectMany(g => g.Items).ToDictionary(e => e.Id, StringComparer.Ordinal);
            var groupOf = GroupOf(plan.Kind);
            var result = new List<MenuGroup>();
            string? lastGroup = null;
            var current = new List<MenuEntry>();
            foreach (var (id, hidden) in resolved)
            {
                if (hidden || !offered.TryGetValue(id, out var entry)) continue;
                string g = groupOf.TryGetValue(id, out var gn) ? gn : "";
                if (lastGroup is not null && g != lastGroup && current.Count > 0)
                {
                    result.Add(new MenuGroup(lastGroup, current));
                    current = new List<MenuEntry>();
                }
                lastGroup = g;
                current.Add(entry);
            }
            if (current.Count > 0 && lastGroup is not null) result.Add(new MenuGroup(lastGroup, current));
            return plan with { Groups = result };
        }

        // ── Style controls ─────────────────────────────────────────────────────────────

        private static MenuStyleControls CapabilitiesOf(MenuMarkKind kind) => kind switch
        {
            MenuMarkKind.Highlight => MenuStyleControls.Swatches | MenuStyleControls.Opacity,
            MenuMarkKind.MarkupHighlight or MenuMarkKind.Underline or MenuMarkKind.Strikethrough
                => MenuStyleControls.Swatches | MenuStyleControls.Opacity | MenuStyleControls.MarkupSwitch,
            MenuMarkKind.Drawing or MenuMarkKind.Shape
                => MenuStyleControls.Swatches | MenuStyleControls.Opacity | MenuStyleControls.Width,
            _ => MenuStyleControls.None,
        };

        /// <summary>
        /// The colour, opacity, width and markup-switch rows a mark menu carries: what every selected
        /// mark can take. The switch needs exactly one markup mark; width needs drawings or shapes only.
        /// </summary>
        public static MenuStyleControls StyleControlsFor(ContextMenuContext ctx)
        {
            if (ctx.Kind is not (ContextMenuKind.Mark or ContextMenuKind.Marks) || ctx.MarkKinds.Count == 0)
                return MenuStyleControls.None;
            var all = CapabilitiesOf(ctx.MarkKinds[0]);
            foreach (var k in ctx.MarkKinds.Skip(1)) all &= CapabilitiesOf(k);
            if (ctx.MarkKinds.Count > 1) all &= ~MenuStyleControls.MarkupSwitch;
            return all;
        }

        /// <summary>
        /// True when raising (direction +1) or lowering (-1) would move something: at least one
        /// selected mark has an unselected neighbour on that side of the page list.
        /// </summary>
        public static bool CanMoveMarks(int listCount, IReadOnlyCollection<int> selectedIndices, int direction)
        {
            if (selectedIndices.Count == 0 || selectedIndices.Count >= listCount) return false;
            var set = new HashSet<int>(selectedIndices);
            foreach (int i in selectedIndices)
            {
                int n = i + direction;
                if (n >= 0 && n < listCount && !set.Contains(n)) return true;
            }
            return false;
        }
    }
}
