# TDPdf

A PDF editor for Windows that you install without an administrator, that keeps quiet
on the network, and that will not let you ship a redaction it cannot prove worked.

It is a single self-contained `TDPdf.exe`. There is no installer to run as admin, no
runtime to install first, no subscription, and no account.

[Download TDPdf {{VERSION}}](/download/)

---

## Redaction that checks its own work

Most tools treat redaction as a drawing operation. You place a black box, the box is
saved into the file, and the text underneath is still there — selectable, searchable,
recoverable by anyone who opens the file in something other than the viewer you used.
A redaction that leaves the bytes in the file is worse than no redaction at all,
because it looks finished.

TDPdf removes the content objects, then **re-opens the file it just produced and reads
it back** to check the marked areas are actually empty. The check is positional, not a
string search: it asks whether any glyph still has its centre inside a rectangle you
redacted. A word that legitimately appears elsewhere in the document cannot trigger a
false alarm, and a letter merely clipping the edge of your box cannot either.

**If anything survived, the output file is never written.** You get an error naming
what was found, not a document you would reasonably believe was safe. The pipeline is
fail-closed on purpose — the one failure mode that must not exist here is a silent one.

Two other things fall out of doing it this way:

- **Metadata goes too.** The Info dictionary *and* the XMP packet are cleared, because
  they are two independent copies of much the same thing and clearing one leaves the
  other to be found. The file is rewritten in full rather than saved incrementally, so
  the pre-redaction revision is not left sitting earlier in the file.
- **Scanned pages are reported, not guessed at.** If your mark falls inside an image —
  the signature of a scanned document, where the whole page is one picture — removing
  objects cannot clear it, and deleting the image would blank the page. TDPdf tells you
  which pages those are and offers to rasterise them, because that costs the page its
  text layer and that is your decision to make, not the program's.

## One file, no administrator

`TDPdf.exe` is the whole application: WPF, the PDF engines, the native `pdfium.dll`,
the OCR engine, all inside one signed executable. Nothing is unpacked into
`Program Files`, and nothing asks for elevation.

Run it from a USB stick and it works. Run it from your Downloads folder and it offers
to install itself, per user, into `%LOCALAPPDATA%\Programs\TDPdf\` — Start Menu entry,
`.pdf` file association, a single Add/Remove Programs entry, and a clean uninstall. No
UAC prompt at any point, because nothing outside your own profile is touched.

For scripted deployment there is `TDPdf.exe /install /silent`, and when it runs as
SYSTEM it installs per machine instead, which is how it deploys through Intune.

## Nothing phones home by default

TDPdf reports to nobody unless somebody deliberately gives it somewhere to report to.

There is **no destination compiled into the binary**. The last one — an Azure
Application Insights key embedded at build time — was removed in 1.24.0.0 and is not
coming back. Reporting needs two independent things: your consent, *and* a destination
supplied at runtime by an environment variable or by administrator-pushed policy. A
build with no destination is inert whichever way the consent setting is set.

If your organisation deploys TDPdf and points it at their collector, reporting is on
until you turn it off, and the [privacy page](/privacy/) enumerates every event name
and field that can be sent. If you want a destination of your own, the
[telemetry page](/telemetry/) explains how. The maintainers of this project receive
nothing either way.

The one thing that reaches the network regardless is a twice-daily check of the public
GitHub releases listing. It carries no identifier — not even which version you are
running, because the comparison happens on your machine.

## What else it does

- **Rendering** through PDFium, the engine in Chrome's PDF viewer
- **Inline text editing** with font matching against the fonts already in the document
- **Annotations** — text boxes, freehand ink, highlights, images, with adjustable
  colour, size and opacity
- **Signatures** — draw one and reuse it, or import a PNG/JPG/BMP, then click to place
- **Forms** — fill interactive AcroForm fields, and author new ones
- **Merge and split**, with drag-and-drop page reordering and a right-click page
  sidebar (insert, rotate, move, extract, delete, across multi-page selections)
- **OCR** for scanned documents
- **Search** across the whole document, drag-select to copy text
- **Tabs** — several PDFs open in one window, with per-tab dirty tracking
- **Password-protected PDFs** open with a prompt instead of an error
- **Print** with annotations flattened, or **Save Flattened** to rasterise every page
  at 150 DPI into something genuinely uneditable
- **Bookmarks**, measurement tools, image export, and a keyboard map overlay

![TDPdf image annotation](https://raw.githubusercontent.com/doodlemania2/TDPdf/main/screenshots/add_image.png)

![TDPdf right-click page sidebar](https://raw.githubusercontent.com/doodlemania2/TDPdf/main/screenshots/right_click_sidebar.png)

## Free software, and the source to prove it

TDPdf is **GPLv3**. It is a fork of [KillerPDF](https://github.com/SteveTheKiller/KillerPDF)
by Steve, who wrote the original, and it is maintained by The Doodle Project, LLC.

Every release ships the corresponding source for that exact binary as a zip alongside
it, because GPLv3 §6 requires it and because "trust us, it's the same code" is not a
claim anyone should accept. The [third-party licences](/third-party-licenses/) page
lists every component inside the executable, down to the native ones no licence scanner
can see.

Requires Windows 10 or 11, 64-bit. There is no macOS or Linux build.

[Download TDPdf {{VERSION}}](/download/) · [Source on GitHub]({{REPO_URL}})
