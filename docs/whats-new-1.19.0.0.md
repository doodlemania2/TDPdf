# TDPdf 1.19 — What's New

*Released 28 July 2026*

> **For end users.** This is the plain-language version, written to be handed
> straight to people who use TDPdf — pasted into an email or intranet post, or
> dropped into the Intune app description. It deliberately carries no version
> numbers beyond the release, no file paths, and no issue references.
>
> For the engineering record see [`CHANGELOG.md`](../CHANGELOG.md); for the
> GitHub Release body see [`.github/release-notes/v1.19.0.0.md`](../.github/release-notes/v1.19.0.0.md).

This update brings a much better text selection experience, a set of new markup tools, keyboard shortcuts for every tool, and several fixes — including an important one to how saving works.

---

## Please read: saving is fixed

**In earlier versions, pressing Ctrl+S didn't always save to your file.**

If you had opened a password-protected PDF, or if you had rotated, deleted, reordered, or cropped a page, TDPdf could write your changes to a temporary working file instead of your actual document. The save looked completely successful — no error, no warning — but the temporary file was deleted when you closed the app, and your original document was never updated.

**This is fixed.** Saving now always goes to your real document.

If you have edited documents this way in the past and the changes seem to have vanished, that is why. Sadly those edits cannot be recovered, as the temporary files are long gone.

---

## Working with text

### Selection that follows the text

Dragging with the Select tool now selects text the way your browser does — it follows the actual run of words in reading order, flowing across lines and paragraphs, instead of grabbing everything that happened to fall inside a rectangle. **Ctrl+A** now shows a proper selection too, rather than shading the whole page.

Clicking still selects annotations, and dragging from a blank part of the page still draws the classic selection box. Hold **Shift** while dragging if you want to force the box.

### Highlight that hugs the words — plus Strikethrough and Underline

Highlighting now traces each line of text instead of laying down one flat rectangle over everything. Two new tools join it: **Strikethrough** and **Underline**.

Each drag creates a single piece of markup, so it moves, restyles, deletes, and undoes as one item, even when it spans several lines.

> **Note:** this works in Single, Two-Page and Grid view. In Continuous (scrolling) view, text selection behaves as it did before.
>
> On a scanned page with no text layer, Highlight still draws a rectangle as it always has, and tells you why in the status bar.

---

## Keyboard shortcuts for tools

TDPdf never had tool shortcuts before. Now every tool has one, with the number keys following the toolbar from left to right:

| Key | Tool | | Key | Tool |
| --- | --- | --- | --- | --- |
| **V** | Select | | **6** or **K** | Strikethrough |
| **P** | Pan | | **7** or **U** | Underline |
| **1** or **T** | Text | | **8** or **S** | Shape |
| **2** or **X** | Edit Text | | **9** or **D** | Draw |
| **3** | Edit Image | | **0** or **E** | Eraser |
| **4** or **I** | Insert Image | | **G** | Signature |
| **5** or **H** | Highlight | | **C** | Crop |

**Esc** now steps back through whatever you're doing — cancelling a shape, leaving full screen, closing the find bar — and once there's nothing left to cancel, returns you to the Select tool. It still never closes the app.

Press **Ctrl+?** any time for the full shortcut map.

---

## New features

### Export pages as images
**File → Export Pages as Images** saves your pages as PNG or JPEG files at whatever resolution you need (24–1200 DPI). You can export the whole document or just a page range, and any markup you've added is included. Large exports show progress and can be cancelled with **Esc**.

### Freeform shapes
The Shape tool can now draw freeform polygons alongside rectangles, ellipses and lines. Click to place each corner, then double-click — or click your first point again — to close the shape. **Backspace** removes the last point, **Esc** cancels.

### Night reading
Press **Ctrl+I**, or click the moon at the bottom of the sidebar, to flip the document to light-on-dark for comfortable reading in a dim room. It's display-only: printing, saving, and exporting always use the document's real colours. TDPdf remembers your preference.

### Make the app bigger (or smaller)
Scale the toolbar, sidebar and tabs anywhere from 70% to 250% with **Ctrl+Shift+Plus** and **Ctrl+Shift+Minus** (**Ctrl+Shift+0** resets), or by scrolling your mouse wheel over the TDPdf logo or the size chip in the status bar. Handy on very high-resolution screens or if the controls are simply too small to hit comfortably.

The page itself is deliberately left alone — app size and page zoom stay independent, so making the buttons bigger never changes how your document looks.

### Print odd or even pages
The print dialog can now print **odd pages only** or **even pages only**, on top of any page range you've set. Print the odd pages, flip the stack over, print the even ones — two-sided printing on a printer that can't do it itself.

### Remove a password
**File → Remove Password** saves a protected document back over itself with the password protection removed. This has always been what happened when you saved a protected file — TDPdf can't edit an encrypted file in place, so it removes the protection on opening — but it was invisible. Now it's a deliberate choice, and ordinary saves of a protected file tell you in the status bar that the protection was dropped.

### Privacy
A new **Don't remember recently opened files** option in Settings. Turning it on also clears the list you already have, so nothing about your documents is left behind on a shared computer.

---

## Fixes

**Exported JPEGs came out completely black.** Most PDFs don't paint their own background, and that was being exported as black rather than white. Exported images now have a proper white background. This also affected flattened PDFs.

**Form fields were in the wrong place on US Letter documents.** Interactive form boxes could sit around half a centimetre out of position — worst near the top of the page — on any document that wasn't A4, even though the page itself looked correct. Clickable links had the same problem, and were also misplaced on rotated pages.

**Cropping renamed your document.** Applying a crop switched the tab over to a temporary file with a name like `report.crop-a1b2c3.pdf`, which then turned up in your recent files list.

**Delete and the arrow keys interfered with typing.** While typing in a form field, the find bar, or the page number box, pressing Delete could remove a page instead of a character, and Home/End/arrows moved through the document instead of your text.

**Some saved files were rejected by Adobe Acrobat** with a "page dimensions out of range" error. Files already affected by this repair themselves when you re-save them in this version.

Plus: night-reading colours could be accidentally baked into a saved document, and the **Password Required** prompt now matches the rest of the app instead of appearing as a plain Windows dialog.

---

## Smaller touches

- The page sidebar now slides open and closed smoothly instead of snapping, and hides itself when no document is open.
- Menus show icons next to each item, and items that can be switched on or off now show a proper tick.
- Page rotation in the right-click menu has matching clockwise and anti-clockwise icons.

---

## Requirements

Windows 10 (1809) or later, 64-bit. TDPdf is a single file with nothing to install alongside it.
