# Command line

TDPdf can do common jobs from a script or a command prompt, with no window. Every command works
while the TDPdf window is open. Run `TDPdf.exe --help` to see this list on your own machine.

## Open a file

```
TDPdf.exe <file.pdf>
```

## Commands

| Command | What it does |
|---|---|
| `--merge <out.pdf> <in1> <in2> ...` | Merge PDFs, and images, into one PDF. |
| `--extract-pages <in.pdf> <pages> <out.pdf>` | Copy pages into a new PDF. Pages are written like `1-3,5,9-12`. |
| `--split <in.pdf> <outDir>` | Write one PDF per page. |
| `--decrypt <in.pdf> <out.pdf> [--password <p>]` | Remove the password protection, keeping the file's quality where possible. |
| `--to-image <in.pdf> <outDir> [--dpi <n>] [--format png\|jpg] [--pages <range>] [--transparent]` | Save pages as pictures. The default is PNG at 150 dpi on a white background. `--transparent` keeps the background clear and needs `--format png`. |
| `--export-epub <in.pdf> <out.epub> [--pages <range>] [--chapters bookmarks\|headings\|none] [--no-pictures] [--keep-headers] [--no-fonts] [--overwrite]` | Turn a PDF into an EPUB book for phones and e-readers. Bookmarks become the table of contents and the chapters, and pictures are kept. Pages with no text are added as pictures, so run `--ocr` first for readable text. Fonts from the PDF are included only when their licence allows it. It will not replace an existing book unless you add `--overwrite`. |
| `--flatten <in.pdf> <out.pdf> [--dpi <n>]` | Turn every page into a picture so the file can't be edited. The default is 150 dpi. |
| `--print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]` | Print without showing a window. Uses the default printer unless you name one. |
| `--ocr <in.pdf> <out.pdf> [--lang <code>]` | Add hidden, searchable text to scanned pages. English is the default; other languages download the first time you use them. |
| `--pdfua <file.pdf\|folder> [more ...] [--json]` | Check accessibility (PDF/UA) and list what is wrong. A folder is checked file by file. `--json` names the standard each file was checked against, PDF/UA-1 or PDF/UA-2. A pass means TDPdf's machine checks found nothing, not that the file meets the standard. |
| `--convert-book <book.epub\|book.djvu\|book.djv> <out.pdf> [--paper Letter\|A4\|A5\|Book\|Pocket] [--margin <mm>] [--font <pt>]` | Turn an EPUB or DjVu book into a PDF. `--paper`, `--margin` and `--font` set the page size, margin (5 to 40 mm) and text size (7 to 24 pt) of an EPUB; DjVu pages keep their own size. DjVu books need DjVuLibre. |
| `--batch-resave <in> <out> [--log <f.csv>] [--quiet]` | Open and save a file, or every PDF in a folder, through TDPdf's normal open and save steps. Useful for checking a batch of files. |
| `--version`, `-v` | Show the version. |
| `--help`, `-h`, `/?` | Show this list. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | It worked. |
| 1 | The job failed. |
| 2 | The command was written wrong. |
| 3 | `--pdfua` only: a file failed the checks or was only partly checked. |

The install and uninstall switches (`/install`, `/uninstall`, `/silent`) are covered in
[Deploying TDPdf in an organisation](intune-distribution.md).
