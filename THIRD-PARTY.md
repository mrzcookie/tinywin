# Third-party components and the GPLv3 source offer

TinyWin is licensed under the **GNU General Public License, version 3 or later**
(see [`LICENSE`](LICENSE)). It redistributes the components listed below.

This file is not a courtesy. TinyWin bundles GPLv3 and LGPLv3 software, and
**GPLv3 §6 requires the corresponding source to be conveyed from the same place as the
binary**. The place TinyWin conveys binaries is the GitHub Release, so every release
carries the corresponding source as a release asset next to the binaries — see
[Where to get the source](#where-to-get-the-source) below.

`tools/check-third-party.ps1` fails the build if a package pinned in
`tools/fetch-xorriso.ps1` is missing from this file. Adding a bundled component without
recording it here is a build error, not a review nit.

---

## 1. Shipped alongside the executable — the xorriso bundle

TinyWin ships GNU xorriso so that writing a bootable ISO needs no Windows ADK install
(`docs/PLAN.md` §3.1). The binaries come from the official MSYS2 repository, pinned by
version **and** SHA-256 in `tools/fetch-xorriso.ps1`, and are laid out in `xorriso\` next
to `TinyWin.exe`.

xorriso is executed as a **separate process**; TinyWin does not link against it.

| File | Component | Version | Licence | Upstream project |
|---|---|---|---|---|
| `xorriso.exe` | GNU xorriso | 1.5.6 | **GPL-3.0-or-later** | <https://www.gnu.org/software/xorriso/> |
| `msys-2.0.dll` | msys2-runtime (a fork of the Cygwin DLL) | 3.6.9 | **LGPL-3.0-or-later** | <https://github.com/msys2/msys2-runtime> |
| `msys-iconv-2.dll`, `msys-charset-1.dll` | GNU libiconv | 1.19 | **LGPL-2.1-or-later** | <https://www.gnu.org/software/libiconv/> |
| `msys-ncursesw6.dll` | ncurses | 6.6 | MIT-like (X11-style) | <https://invisible-island.net/ncurses/> |
| `msys-readline8.dll` | GNU readline | 8.3.003 | **GPL-3.0-or-later** | <https://tiswww.case.edu/php/chet/readline/rltop.html> |
| `msys-z.dll` | zlib | 1.3.2 | Zlib | <https://zlib.net/> |
| `msys-bz2-1.dll` | bzip2 | 1.0.8 | `bzip2-1.0.6` (BSD-style) | <https://sourceware.org/bzip2/> |

The four components in **bold** are copyleft and are the reason the source-mirroring
obligation exists. The other three are permissive; their source is mirrored anyway, because
splitting the archive by licence would be more work than shipping all seven.

The exact package filenames, SHA-256 hashes and source-package names are reproduced in
`xorriso\PACKAGES.txt` inside every release archive, written by `tools/fetch-xorriso.ps1`.

### Why the binaries are not committed to git

Recorded here so it is not relitigated. GPLv3 §6 ties source to the *place of conveyance*,
which is the release, not the git tree. Committing a 6.6 MB binary would put it in every
clone forever while discharging nothing; committing its corresponding source too would mean
carrying the source of msys2-runtime, libiconv, ncurses, readline, zlib and bzip2
permanently. Pinned SHA-256 hashes buy the reproducibility that committing the blob would
otherwise be for. See `docs/findings/iso-builder.md` §3.

### arm64

MSYS2 publishes no `aarch64` build of the msys runtime, so **the arm64 release archive
ships the same x86-64 xorriso bundle**, which runs under Windows 11 on Arm's x64 emulation.
An arm64 machine with the Windows ADK installed will also find the native
`arm64\Oscdimg\oscdimg.exe` through `BackendLocator`. Neither path has been executed on
real arm64 hardware — see `docs/findings/packaging.md`.

---

## 2. Linked into `TinyWin.exe`

The app publishes self-contained, so the .NET and Windows App SDK runtimes are
redistributed inside the single-file executable rather than assumed present.

| Component | Version | Licence | Source |
|---|---|---|---|
| .NET runtime and libraries | 10.0.x | MIT | <https://github.com/dotnet/runtime> |
| Windows App SDK / WinUI 3 | 2.3.1 | Microsoft Software Licence Terms (redistribution permitted) | <https://github.com/microsoft/WindowsAppSDK> |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| Microsoft.Dism (ManagedDism) | 6.0.0 | MIT | <https://github.com/jeffkl/ManagedDism> |

Build-time only, not redistributed: `Microsoft.Windows.SDK.BuildTools`,
`Microsoft.NET.Test.Sdk`, `xunit.v3`, `xunit.runner.visualstudio`.

`dism.exe`, `oscdimg.exe` and the Windows ADK are **never redistributed** by TinyWin. DISM
is invoked from the host Windows installation; oscdimg is used only if the user already has
an ADK installed. `docs/PLAN.md` §3.1 records why: oscdimg is not redistributable.

### A note for the copyright holder — GPLv3 §7 and the Microsoft runtimes

TinyWin is GPLv3 by its author's choice; nothing forces that, because xorriso is invoked
out-of-process. But a GPLv3 program that links the Windows App SDK — whose binaries carry
Microsoft's own terms and are redistributed inside our executable rather than being present
on the system — sits awkwardly against GPLv3 §1's "System Libraries" carve-out, since
self-contained deployment is precisely the case where the library is *not* a major component
of the user's operating system.

The ordinary remedy is an **additional permission under GPLv3 §7**, granted by the copyright
holder, along these lines:

> Additional permission under GNU GPL version 3 section 7: You have permission to link or
> combine this program with the Microsoft Windows App SDK and the .NET runtime and
> libraries, and to convey the resulting work, notwithstanding the terms of the GPL. If you
> modify this program, you may extend this exception to your version, but you are not
> obliged to do so.

This is not applied. Adding a licence exception is the copyright holder's decision, not a
packaging change — raised here because M5 is where it would surface.

---

## 3. Not shipped — acknowledged

[ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder) (MIT) established the
approach. Its published package list and registry keys were the starting point for
`catalog/`. No tiny11builder code is included.

---

## Where to get the source

Every GitHub Release at <https://github.com/mrzcookie/tinywin/releases> carries, next to the
binary archives:

| Asset | Contents |
|---|---|
| `tinywin-<version>-source.zip` | TinyWin's complete source at the released tag, including the build scripts and workflows that produce the binaries. |
| `xorriso-bundle-<version>-corresponding-source.zip` | The MSYS2 source packages (`.src.tar.zst`) for all seven bundled components, each containing the upstream tarball and the PKGBUILD that compiles and installs it, plus `PACKAGES.txt` and `BUILDING.txt`. |
| `SHA256SUMS.txt` | SHA-256 of every asset in the release. |

To rebuild the xorriso bundle yourself, unpack a `.src.tar.zst` and run `makepkg` in an
MSYS2 shell; `BUILDING.txt` in the archive spells this out. To reproduce the exact bundle
TinyWin ships, run `tools/fetch-xorriso.ps1`, which verifies every download against its
pinned hash.

This offer is valid for as long as TinyWin distributes the corresponding binaries. If a
release asset is ever missing, that is a bug — please open an issue.
