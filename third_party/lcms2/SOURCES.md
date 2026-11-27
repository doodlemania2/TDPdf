# Little CMS (lcms2) native library

`win-x64/liblcms2-2.dll` is the MSYS2 UCRT64 build of Little CMS, bundled so colour management
works with printer and CMYK ICC profiles. It is embedded in TDPdf.exe and loaded only when its
SHA-256 equals `LcmsBootstrap.PinnedSha256` (Services/LcmsNative.cs).

| Item | Value |
|---|---|
| Package | `mingw-w64-ucrt-x86_64-lcms2` 2.20-1 (`...-2.20-1-any.pkg.tar.zst`), from https://repo.msys2.org/mingw/ucrt64/ |
| Package SHA-256 | `4e1d12047e72c63832f2b50c2fc87c7ec9d2e5a8bd40ce6e820ccbeedf2e7b8f` (matches the `ucrt64.db` entry) |
| Signatures | `ucrt64.db.sig` and the package `.sig` verified with gpg against the MSYS2 keyring (https://github.com/msys2/MSYS2-keyring); signed by the MSYS2 development key 5F944B027F7FE2091985AA2EFA11531AA0AA7F57 |
| Shipped file | `bin/liblcms2-2.dll`, 451,042 bytes, SHA-256 `54ce5a43219a39a52eafd10f40180f75b659dda59c40204795d9be13350055a2` |
| Build recipe (PKGBUILD) | https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-lcms2 |
| Upstream source | https://github.com/mm2/Little-CMS/releases/download/lcms2.20/lcms2-2.20.tar.gz (SHA-256 `18ff5504fa9cc8a2b1f3591180d90ead16c8a2ea78e530523b46eab70e1ca6b9`, from the PKGBUILD) |
| Licence | MIT, `licenses/LICENSE-lcms2-MIT` |

## Dependencies

The DLL imports only `KERNEL32.dll` and the Universal CRT forwarders (`api-ms-win-crt-*`), all part of
Windows 10 and later. No other MSYS2 package (no libwinpthread, no libgcc) is needed, so nothing else is
shipped.

## Not shipped

The package also holds `liblcms2_fast_float-2.dll` (a plugin under GPL-3.0-or-later) and command-line
tools; TDPdf bundles none of them.

## Updating

Replace the DLL, `SHA256SUMS` and `LcmsBootstrap.PinnedSha256` together, re-read the PE imports for new
dependencies, and update this file and `build/generate-third-party-notices.ps1`.
