# Services

UI-independent logic extracted from `MainWindow`: PDF reading and writing helpers (for example `PdfItems`, `PdfNamedDestinations`, `PdfSaveNormalizer`, `PdfFormText`, `PageRaster`), page and geometry math, OCR model handling, print planning, the Documenso e-signature client and its windows, and install/update policy.

Most files here are pure logic with no WPF dependency and are compiled into the `tests/PdfCore` harness via `<Compile Include>`, so changes to them should come with a test there. Files that do use WPF (the `*Window.cs` classes, `ThemeManager`, `PrintPreviewWindow`) are not covered by the harness. `MainWindow` itself stays split across `MainWindow.*.cs` partials at the repository root.
