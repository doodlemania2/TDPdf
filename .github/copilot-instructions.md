# TDPdf - Copilot instructions

`CLAUDE.md` at the repo root is the source of truth for architecture, build, release and CI. Read it first; this file only repeats the rules that matter most for an assistant.

TDPdf is a Windows-only WPF PDF editor (`net10.0-windows`, `win-x64`), a GPLv3 fork of KillerPDF. Cross-compiling from macOS/Linux needs `-p:EnableWindowsTargeting=true`.

## Rules

1. **Namespace is `TDPdf`.** No KillerPDF strings in shipping code; new dialog titles and product strings say "TDPdf".
2. **Dialogs go through `TdpDialog`.** Never `MessageBox.Show` or default file-dialog chrome for new UI.
3. **Use theme brushes** from `MainWindow.xaml` resources and `Themes/*.xaml`; never hard-code hex colours.
4. **Call `MarkDirty()` on any change that mutates the document**, and keep per-document state on `DocumentContext`.
5. **Nullable-clean.** Do not add warnings (check with `dotnet build --no-incremental`; baseline is in `CLAUDE.md`) and do not silence them with `!` unless the invariant is guaranteed.
6. **Tests:** `dotnet run --project tests/PdfCore/PdfCore.Tests.csproj -c Release` must print `ALL PASS`. Add tests for pure logic you add.
7. **Release PRs touch three files together:** `TDPdf.csproj` (Version, AssemblyVersion, FileVersion), `CHANGELOG.md`, and `.github/release-notes/v<x.y.z.w>.md`. The release notes file is required before the tag exists. Signed releases are produced only by pushing a tag (`release.yml`).
8. **Never break the printer Properties-dialog path.** In `Services/PrintPreviewWindow.cs` the DEVMODE <-> PrintTicket conversions must stay `PrintTicketScope.JobScope` (pinned by `tests/PdfCore/PrintPlan.cs`).
