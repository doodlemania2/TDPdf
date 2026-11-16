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
| `--flatten <in.pdf> <out.pdf> [--dpi <n>]` | Turn every page into a picture so the file can't be edited. The default is 150 dpi. |
| `--print <in.pdf> [--printer <name>] [--pages <range>] [--copies <n>]` | Print without showing a window. Uses the default printer unless you name one. |
| `--ocr <in.pdf> <out.pdf> [--lang <code>]` | Add hidden, searchable text to scanned pages. English is the default; other languages download the first time you use them. |
| `--batch-resave <in> <out> [--log <f.csv>] [--quiet]` | Open and save a file, or every PDF in a folder, through TDPdf's normal open and save steps. Useful for checking a batch of files. |
| `--version`, `-v` | Show the version. |
| `--help`, `-h`, `/?` | Show this list. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | It worked. |
| 1 | The job failed. |
| 2 | The command was written wrong. |

The install and uninstall switches (`/install`, `/uninstall`, `/silent`) are covered in
[Deploying TDPdf in an organisation](intune-distribution.md).
