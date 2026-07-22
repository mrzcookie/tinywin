# M5 — portable packaging: implementation findings

Covers the single-file publish, the elevation manifest, the GPLv3 source obligation, the
release workflow and version stamping (`docs/PLAN.md` §6, M5 row).

Everything with a number attached below was measured on this machine (.NET SDK 10.0.302,
Windows 11 26200, **unelevated**). What was not executed is listed in §6 and is not dressed
up as verified.

---

## 0. Result

| | |
|---|---|
| `win-x64` single-file exe | **215.4 MB** (220,570 KB), 1 file |
| `win-arm64` single-file exe | **229.2 MB**, 1 file |
| Release archive, x64 | 83.9 MB zipped (exe + xorriso bundle + licences) |
| Release archive, arm64 | 81.5 MB zipped |
| Corresponding source archive | **139.1 MB** — of which msys2-runtime is 122.5 MB |
| TinyWin source archive | 0.4 MB |

Both architectures publish clean, and the PE headers confirm they are what they claim:
`0x8664` for x64, `0xAA64` for arm64. `docs/PLAN.md` §3.4 predicted 150–250 MB; that holds.

---

## 1. The single-file publish

`src/TinyWin.App/TinyWin.App.csproj` — owned by the `ui-shell` worktree — already carries the
exact §3.4 property combination, and it works unmodified:

```
WindowsPackageType=None  WindowsAppSDKSelfContained=true  SelfContained=true  PublishSingleFile=true
```

**Those properties were deliberately not repeated in `tools/publish.ps1`.** Passing them on
the command line would let the csproj and the release script drift, and would mean a plain
`dotnet publish` produced something different from a release. The script passes only the
runtime identifier, the output path and the version.

The publish output is **four files: the exe and three PDBs**. Nothing else — no loose
runtime DLLs, no `resources.pri` beside the exe. `EnableMsixTooling=true` is what makes the
last part true, and the comment in the csproj saying so is correct: without it the XAML
resource index does not get embedded. `publish.ps1` asserts the four-file shape and fails the
release if anything else appears, because `PublishSingleFile` degrades to a folder of ~400
DLLs rather than erroring when the combination breaks, and that is not something to discover
from a user's bug report. PDBs are dropped from the archive.

### arm64 is produced by cross-compilation, not verified by running

`dotnet publish -r win-arm64` from an x64 host succeeds and emits a correct ARM64 PE — the
native bits come from NuGet, not from the host toolchain. Nobody has launched it. This is
listed in §6, not claimed here.

---

## 2. Elevation — already correct, now verified in the shipped binary

`ui-shell` had done this, and done it well: `app.manifest` requests
`requestedExecutionLevel level="requireAdministrator"`, with `app.asinvoker.manifest` behind
`-p:TinyWinElevation=false` as a development escape hatch. Nothing here changed either file.

What was missing was proof that it survives to the artifact. A WinUI 3 self-contained publish
**merges** the application manifest with a generated one — the shipped `RT_MANIFEST` is
131 KB of Windows App SDK activatable-class registrations — so "the manifest is in the csproj"
and "the exe asks for elevation" are two different claims.

Extracting `RT_MANIFEST` from the published single-file exe via `FindResource`:

```
requestedExecutionLevel level="requireAdministrator" uiAccess="false"
dpiAwareness ...>permonitorv2,permonitor
activeCodePage ...>UTF-8
```

All three survive the merge. Without this the app would hit DISM error 740 at the first
mount, which is the failure `docs/STATUS.md` records for unelevated agent shells.

---

## 3. GPLv3 — the part that is an obligation, not paperwork

### 3.1 What is actually being conveyed

The vendored bundle is 8 files / 6.58 MB, and four of its seven upstream components are
copyleft: **xorriso** (GPL-3.0-or-later), **msys2-runtime** (LGPL-3.0-or-later), **libiconv**
(LGPL-2.1-or-later) and **readline** (GPL-3.0-or-later). GPLv3 §6 requires the Corresponding
Source to be conveyed **from the same place as the binary**.

`docs/findings/iso-builder.md` §3 already settled *where* that place is — the GitHub Release,
not the git tree — and that decision is not revisited. M5's job was to build the machinery
that makes it true, which is now three pieces:

| Piece | What it does |
|---|---|
| `THIRD-PARTY.md` | Every bundled component, its version, its licence and its upstream project. Plus the source offer itself. |
| `tools/pack-source.ps1` | Builds `tinywin-<v>-source.zip` (via `git archive HEAD`) and `xorriso-bundle-<v>-corresponding-source.zip` (the seven MSYS2 `.src.tar.zst` packages, `PACKAGES.txt`, `LICENSE`, `BUILDING.txt`). |
| `.github/workflows/release.yml` | Attaches binaries and both source archives to **one** release, in one `gh release create` call. |

The MSYS2 source packages are the right artifact rather than a convenience: each contains the
upstream tarball *and* the PKGBUILD that configures, compiles and installs it, which is
precisely GPLv3 §1's "scripts used to control compilation and installation".

### 3.2 The attribution check is a build step, not a checklist

`tools/check-third-party.ps1` parses the pinned `$Packages` table out of
`tools/fetch-xorriso.ps1` and fails if any component's name, version or licence identifier is
missing from `THIRD-PARTY.md`, or if the source-offer section has been removed. It runs in
`ci.yml` on every PR and again first in `release.yml`. No network, ~1 second.

The reason it exists: adding a package to the bundle without recording it produces a release
that looks complete and is a licence violation. Nothing else in the build would notice —
exactly the silent-no-op failure mode CLAUDE.md says to refuse.

It earned its place on the first run, catching that the bzip2 licence was written as prose
where the pin uses the SPDX identifier `bzip2-1.0.6`.

### 3.3 The corresponding source costs 139 MB per release, and 122 MB of it is msys2-runtime

Measured, not estimated:

| Source package | Size |
|---|---:|
| `msys2-runtime-3.6.9-2` | **122.5 MB** |
| `libiconv-1.19-1` | 4.9 MB |
| `ncurses-6.6-2` | 3.6 MB |
| `readline-8.3.003-1` | 3.2 MB |
| `xorriso-1.5.6-1` | 2.7 MB |
| `zlib-1.3.2-1` | 1.3 MB |
| `bzip2-1.0.8-4` | 0.8 MB |

`docs/PLAN.md` §3.1 proposes replacing the MSYS2 bundle with a native mingw-w64 build to
shrink this obligation. **That number is the argument.** A statically linked mingw-w64 xorriso
built `--disable-libreadline --disable-libbz2` needs no msys2-runtime and no readline, which
would remove the LGPLv3 mirroring entirely and drop ~126 MB of the 139 — and would also
retire the `/cygdrive` path tax in `CygwinPath.cs`.

**Not attempted here.** No mingw-w64 toolchain is installed on this machine, and building
xorriso from source is a different task from packaging it. It remains tracked, now with a
concrete payoff attached.

### 3.4 An unresolved licensing question, raised rather than decided

TinyWin is GPLv3 by its author's choice — xorriso runs as a separate process, so nothing
forces it. But the app now redistributes the Windows App SDK **inside** its own executable
(that is what `WindowsAppSDKSelfContained` means), and those binaries carry Microsoft's terms.
GPLv3 §1's "System Libraries" carve-out is a poor fit precisely when the library is shipped
rather than assumed present on the user's system.

The ordinary remedy is an additional permission under **GPLv3 §7**, which only the copyright
holder can grant. A ready-to-adopt clause is written out in `THIRD-PARTY.md` §2. It has
**not** been applied — changing licence terms is not a packaging decision.

### 3.5 arm64 and xorriso

MSYS2 publishes no `aarch64` msys repository, so there is no native arm64 xorriso to vendor.
The arm64 archive ships the **same x86-64 bundle**, relying on Windows on Arm's x64 emulation;
`BackendLocator` will also find a native `arm64\Oscdimg\oscdimg.exe` if an ADK is installed.
Recorded in `THIRD-PARTY.md`. Unverified — see §6.

---

## 4. Versioning

The git tag is the source of truth.

| Build | Stamped as |
|---|---|
| Tag `v1.2.3` | `1.2.3` — `release.yml` strips the `v` and passes `-p:Version=` |
| Tag `v1.2.3-rc.1` | `1.2.3-rc.1`, and the release is marked prerelease |
| Anything else | `0.1.0-dev`, from `VersionPrefix` in `Directory.Build.props` |
| CI, untagged | `0.1.0-dev+<GITHUB_SHA>` — the SHA arrives via `SourceRevisionId` |

Verified: `dotnet msbuild -getProperty:Version` gives `0.1.0-dev` by default and `1.2.3`
under `-p:Version=1.2.3`; a published exe stamped `1.0.0-test` reports `FileVersion 1.0.0.0`
and `ProductVersion 1.0.0-test`.

The `-dev` suffix on every untagged build is deliberate: an artifact that escapes a
developer's machine should not be mistakable for a release.

`release.yml` **refuses to release** if the tag's `x.y.z` disagrees with `<VersionPrefix>`.
Bump `Directory.Build.props` in the commit you tag.

---

## 5. The release workflow

`.github/workflows/release.yml`, on `push: tags: ['v*']`, plus `workflow_dispatch` for a
rehearsal that builds every asset and uploads them as workflow artifacts **without**
publishing a release.

Order matters and is deliberate:

1. resolve and validate the version against `VersionPrefix`;
2. **attribution check** — offline and instant, so a licence gap fails in seconds rather than
   after a 145 MB download and two 200 MB publishes;
3. restore, build, **test** — a release must not ship untested code, so the CI gate runs again
   on the tagged commit rather than being assumed;
4. `fetch-xorriso.ps1 -WithSource`;
5. `publish.ps1` (both RIDs) and `pack-source.ps1`;
6. **asset completeness check** — every expected file, each with a size floor, so a truncated
   download or a stubbed publish cannot pass;
7. checksums, artifact upload, then `gh release create` with binaries *and* source in one call.

`gh` rather than a third-party release action: it is preinstalled on the runner and adds no
supply-chain dependency. Style follows `ci.yml`; `ci.yml` gained one step and is otherwise
untouched.

### Two things worth knowing if this is edited

- **A PowerShell here-string cannot live in an indented YAML block.** `@"` requires its
  closing `"@` at column 0, which no block scalar can provide. This is why the release notes
  are generated by `tools/release-notes.ps1 -OutFile` and passed with `--notes-file`, rather
  than being inline. It is a parse error, not a style preference.
- **`pwsh` is not installed on this development machine** — only Windows PowerShell 5.1. The
  GitHub runner has both, and the workflow uses the runner default (`pwsh`), but every script
  under `tools/` was written and tested against 5.1 so a local rehearsal works. That is also
  why they avoid redirecting native-command stderr: in 5.1 that turns a clean exit into a
  terminating `NativeCommandError`, which is the same trap `fetch-xorriso.ps1` documents.

---

## 6. Not proven

| Unverified | Why |
|---|---|
| **The published exe runs.** | It requests `requireAdministrator`, and this session is unelevated. The manifest is verified; the launch is not. |
| **arm64 runs at all.** | Cross-compiled from x64. No arm64 hardware here. The PE header is correct and that is the entire claim. |
| **The x64 xorriso bundle works under Windows on Arm emulation.** | Same reason. It is the arm64 ISO path's only vendored option, so it belongs in any arm64 test pass. |
| **`release.yml` has never run.** | Deliberate — the brief forbids firing it at the real repository. The YAML parses, every embedded PowerShell block parses, and every script it calls has been executed locally. The workflow as a whole has not. |
| **`publish.ps1` against the real `src/TinyWin.App`.** | The UI project is uncommitted in the `ui-shell` worktree and does not exist on this branch. See §7. |
| An icon. | `ApplicationIcon` needs an `.ico` asset and a csproj line in a project this worktree does not own. Listed in the M5 row of `docs/PLAN.md`; not done. |

---

## 7. The dependency on `ui-shell`

`src/TinyWin.App` is uncommitted in the `ui-shell` worktree, so it is not on this branch and
not in `tinywin.slnx`. Everything above was therefore measured against an **isolated copy** of
that worktree's `TinyWin.App`, `TinyWin.Core` and `TinyWin.Catalog`, taken to a scratch
directory so nothing in `ui-shell` was touched. One transient compile error in that in-flight
source (`CS0104`, an ambiguous `UnhandledExceptionEventArgs` between `Microsoft.UI.Xaml` and
`System`) was fixed **in the scratch copy only** — `ui-shell` owns the real fix.

Consequences to expect at merge:

- `tools/publish.ps1` fails with a named, deliberate error until `TinyWin.App` lands. The
  release workflow fails at that step. This is correct: a release without the app is not a
  release.
- The publish properties live in `TinyWin.App.csproj`, which `ui-shell` owns. This worktree
  did not touch that file, or `app.manifest`, or `app.asinvoker.manifest`.
- `TinyWin.App` must be added to `tinywin.slnx` by whoever merges `ui-shell` — the release
  workflow's `dotnet build tinywin.slnx` will not otherwise cover it.

The measured sizes in §0 depend only on the runtime and the Windows App SDK, so they will not
move much as the UI grows.
