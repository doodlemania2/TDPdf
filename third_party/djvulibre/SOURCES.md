# DjVuLibre library: where the bundled DLLs come from

TDPdf reads DjVu books with the DjVuLibre **library**. Five 64-bit Windows DLLs are embedded in
`TDPdf.exe` (see `TDPdf.csproj`) and loaded by the DjVu worker process (`TDPdf.exe --djvu-worker`,
`Services/DjvuWorker.cs`). No DjVuLibre program (`ddjvu.exe`, `djvused.exe` and the rest) is shipped.

Every file below was taken, unchanged, from a package on <https://repo.msys2.org/mingw/ucrt64/>
(MSYS2, UCRT64 environment). The package list was read from `ucrt64.db`, whose detached signature was
checked against the MSYS2 keyring, and each package was checked against the SHA-256 in that database
and against its own `.sig`. The five files are pinned by `SHA256SUMS`; the build stops if any file in
`win-x64/` does not match, or if a `.exe` is there.

The set is the import closure of `libdjvulibre-21.dll`, found by reading the PE import tables: its
non-system imports are `libgcc_s_seh-1.dll`, `libjpeg-8.dll` and `libstdc++-6.dll`; the last of those
imports `libgcc_s_seh-1.dll` and `libwinpthread-1.dll`. Everything else any of the five imports is a
Windows system library (`KERNEL32`, `ADVAPI32` and the `api-ms-win-crt-*` Universal C Runtime
forwarders, which ship with Windows 10 and later).

| File in `win-x64/` | Size (bytes) | SHA-256 |
|---|---|---|
| `libdjvulibre-21.dll` | 1,755,813 | `5bfbc3e8076a4b695fc3f719619886ce908bbd7dbb3060a6a6cc29eb2e2aae20` |
| `libjpeg-8.dll` | 963,471 | `22286acc18b907d03eec1177c99f54a802a6f8437888d1abb79e65a7ecd14605` |
| `libstdc++-6.dll` | 2,659,194 | `098e4964e3c8179847f7a1c313d2e8020ca12df3f1d18878a417a8f54d41505a` |
| `libgcc_s_seh-1.dll` | 150,025 | `44f6318f96eb2aaf6e90400bb1178ab6d55b01ae2cf24846d6b30d96e87e89dd` |
| `libwinpthread-1.dll` | 73,308 | `94a9a4e2fe27b4bc827a4b209e9deb4cc3dc34bc025df06b849144f9d4c588f8` |

## Packages

Each recipe link is the MSYS2 `PKGBUILD` that built that package. The corresponding source is also
mirrored by MSYS2 at the `repo.msys2.org/mingw/sources/` link, which holds the exact recipe and patches
used for the version shipped.

### mingw-w64-ucrt-x86_64-djvulibre 3.5.30-2

- File used: `ucrt64/bin/libdjvulibre-21.dll`
- Package: `mingw-w64-ucrt-x86_64-djvulibre-3.5.30-2-any.pkg.tar.zst`,
  SHA-256 `ed51d57f1af30d963a1651ed71faae7ddf8824a3c685acd19203cea73335a641`
- Recipe: <https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-djvulibre>
- Upstream source: <https://downloads.sourceforge.net/djvu/djvulibre-3.5.30.tar.gz>
  (SHA-256 `ee5e457d4cfebe566f94b99e5e3d3cc7f5c79ddb741c2ac2ba2e456f00329644`), project page <https://djvu.sourceforge.net/>
- MSYS2 source package: <https://repo.msys2.org/mingw/sources/mingw-w64-djvulibre-3.5.30-2.src.tar.zst>
- Licence: GPL-2.0-or-later. Text in `licenses/djvulibre/COPYING`; notices in `licenses/djvulibre/COPYRIGHT`.

### mingw-w64-ucrt-x86_64-libjpeg-turbo 3.2.1-1

- File used: `ucrt64/bin/libjpeg-8.dll`
- Package: `mingw-w64-ucrt-x86_64-libjpeg-turbo-3.2.1-1-any.pkg.tar.zst`,
  SHA-256 `8eed77c1e0d6de7f8822687f7b1dfa32cda09394806ef67b23399e63de1a92a6`
- Recipe: <https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-libjpeg-turbo>
- Upstream source: <https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.2.1/libjpeg-turbo-3.2.1.tar.gz>
  (SHA-256 `c27ad72a68da151177929aa969ae773a2da5ac2af9ce946079314fdac8cece7f`)
- MSYS2 source package: <https://repo.msys2.org/mingw/sources/mingw-w64-libjpeg-turbo-3.2.1-1.src.tar.zst>
- Licences: IJG licence and BSD-3-Clause (and zlib for the SIMD code, as the package's `LICENSE.md` lays out).
  Text in `licenses/libjpeg-turbo/LICENSE.md` and `README.ijg`.

### mingw-w64-ucrt-x86_64-libstdc++ 16.2.0-7 and mingw-w64-ucrt-x86_64-libgcc 16.2.0-7

Both come from the one MSYS2 recipe `mingw-w64-gcc` (GCC 16.2.0).

- Files used: `ucrt64/bin/libstdc++-6.dll` (package `libstdc++`) and `ucrt64/bin/libgcc_s_seh-1.dll` (package `libgcc`)
- Packages: `mingw-w64-ucrt-x86_64-libstdc++-16.2.0-7-any.pkg.tar.zst`,
  SHA-256 `4dd4b216b0e92ffa160127dd50620372609e98cb219ff1c5dcb9b3fbfe153253`;
  `mingw-w64-ucrt-x86_64-libgcc-16.2.0-7-any.pkg.tar.zst`,
  SHA-256 `bc0ae31c582e0847964b4c8c4b55bb2404a9b7ddce7ebf0b176b6693b76e0c62`
- Recipe: <https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-gcc>
- Upstream source: <https://ftp.gnu.org/gnu/gcc/gcc-16.2.0/gcc-16.2.0.tar.xz>
  (SHA-256 `e6738e29597f733270731aa90600f37ffdc045079dfc27ec7e8192cc81085c3e`)
- MSYS2 source package: <https://repo.msys2.org/mingw/sources/mingw-w64-gcc-16.2.0-7.src.tar.zst>
- Licence: GPL-3.0-or-later WITH the GCC Runtime Library Exception 3.1. Text in
  `licenses/gcc-runtime/COPYING3` and `COPYING.RUNTIME`. The exception lets the runtime libraries be
  combined with programs under any licence.

### mingw-w64-ucrt-x86_64-libwinpthread 14.0.0.r598.ge3d18511f-1

- File used: `ucrt64/bin/libwinpthread-1.dll`
- Package: `mingw-w64-ucrt-x86_64-libwinpthread-14.0.0.r598.ge3d18511f-1-any.pkg.tar.zst`,
  SHA-256 `c35d6be83d141c8cfbb61f1637dd4b53aac8f5562d766aa26d0741c40bad4e4d`
- Recipe: <https://github.com/msys2/MINGW-packages/tree/master/mingw-w64-winpthreads>
- Upstream source: <https://sourceforge.net/p/mingw-w64/mingw-w64/> at commit
  `e3d18511f9759d455cae20c34703eea4a0723cf3` (`mingw-w64-libraries/winpthreads`)
- MSYS2 source package: <https://repo.msys2.org/mingw/sources/mingw-w64-winpthreads-14.0.0.r598.ge3d18511f-1.src.tar.zst>
- Licences: MIT and BSD-3-Clause. Text in `licenses/winpthreads/COPYING`.

## Refreshing

1. Download `ucrt64.db` and `ucrt64.db.sig` from <https://repo.msys2.org/mingw/ucrt64/> and check the
   signature against the `msys2.gpg` keyring (the `msys2-keyring` package on `repo.msys2.org/msys/x86_64/`,
   or <https://github.com/msys2/MSYS2-keyring>).
2. Take the new `mingw-w64-ucrt-x86_64-djvulibre` package, check its SHA-256 against the database and its
   `.sig`, and list the DLLs it imports. Resolve every import that is not a Windows system library to its
   package, check that package the same way, and repeat until only system libraries remain.
3. Replace the files in `win-x64/`, regenerate `SHA256SUMS`, update this file, the names in
   `Services/DjvuNativeBootstrap.cs` (`LibraryNames`) if a file name changed (the `21` in
   `libdjvulibre-21.dll` is the library's major version), `licenses/`, and
   `build/generate-third-party-notices.ps1`; then regenerate `THIRD-PARTY-NOTICES.md`.
