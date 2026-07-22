# Build status

Living scratchpad for the parallel build. Updated as worktrees land.

Last updated: 2026-07-22.

## Landed on `main`

**M0 — skeleton & contracts.** Complete. `dotnet build` clean, `dotnet test` 19/19 green.

- Solution `tinywin.slnx` (.NET 10 defaults to the `.slnx` format), 8 projects.
- `TinyWin.Catalog` — component/preset model, `ActionValidator`, `CatalogValidator`,
  `PlanResolver` (requires-closure, conflict detection, build-range warnings, action ordering).
- `TinyWin.Core` — `IImagingBackend`, `IOfflineRegistry`/`IHiveSession`, `IIsoBuilder`,
  `IUnattendGenerator`, the 14-stage `BuildStageId`, `BuildPipeline` with reverse-order rollback.
- `catalog/` — 8 seed components + 4 presets, encoding the §8 decisions.
- `tests/` — `FakeImagingBackend` plus catalog, resolver and pipeline-rollback suites.
- `TinyWin.Cli` — `catalog`, `presets`, `doctor`.
- CI — Windows build + unit tests on push/PR.

## Environment facts

| | |
|---|---|
| .NET SDK | 10.0.302 |
| Host OS | Windows 11 build 26200 (25H2) — matches the primary target |
| Test media | `C:\Users\Zachary\ISOs\Win11_25H2_English_x64_v2.iso`, mounted read-only at `D:` |
| `install.wim` | **7.06 GB** — confirms UDF is mandatory for the rebuilt ISO, not theoretical |
| Boot files | `D:\boot\etfsboot.com` and `D:\efi\microsoft\boot\efisys.bin` both present |
| Windows ADK | **Not installed** — so the xorriso path matters |
| Elevation | **Agent shells are NOT elevated.** `dism` fails with error 740. |
| Disk | ~795 GB free on `C:` |

### The elevation constraint

This is the single biggest limit on autonomous progress. DISM, Hyper-V VM creation and offline
hive loading all require elevation, and agents cannot self-elevate without a UAC prompt. Every
worktree has therefore been briefed to:

1. put command construction, output parsing and marshalling behind a testable seam, and unit test
   those thoroughly;
2. write a ready-to-run elevated verification script under `scripts/`;
3. state plainly in its findings what is proven versus what is unverified.

**When you are back at the machine, running those scripts in an elevated shell is the highest-value
thing you can do** — it converts a pile of inference into verified behaviour.

## Spikes — both complete and merged

### `docs/spikes/dism-backend.md` — confirmed the plan, for a stronger reason

- **`Microsoft.Dism` 6.0.0 already wraps the undocumented provisioned-appx exports**, and
  wraps them correctly. `PInvokeAppxBackend` is struck from the plan. Hand-rolling it invited a
  silent bug: `dismapi.h` uses `#pragma pack(push, 1)`, so the obvious `LayoutKind.Sequential`
  struct gets a 72-byte stride against a real 68 — yielding a plausible-but-wrong package list
  rather than a clean crash.
- **`dism.exe` is permanently mandatory, not a fallback.** Neither `StartComponentCleanup`/
  `ResetBase` nor `Export-Image` exists as *any* export in `dismapi.dll`. The whole table was
  checked. `ManagedDismBackend` therefore becomes a partial override composing over
  `DismExeBackend`, not a peer.
- Appx methods have no progress or cancellation overloads, so per-package progress belongs to
  Core's stage engine.
- `DismGetRegistryMountPoint` exists — flagged to `registry-engine` as a possible alternative to
  hand-rolled `RegLoadKey`.

### `docs/spikes/iso-build.md` — conditional GO, and it falsified a plan premise

- **xorriso cannot write UDF in any configuration** — the filesystem is unimplemented in
  libisofs, not a missing flag. Had UDF truly been required this would have been a hard no.
- **It isn't required.** ISO 9660 level 3 multi-extent holds a 4.7 GiB member file, and
  Windows' own `cdfs.sys` reads it back with a matching SHA-256 across the 4 GiB boundary.
  The real requirement is **ISO 9660 level 3 + Joliet**.
- `-J -joliet-long` is mandatory and the alternatives are traps: `-untranslated-filenames`
  *silently* truncates at 37 characters, which inspects clean and ships broken.
- Boot geometry must be read from the source ISO (`-report_el_torito as_mkisofs`); xorriso
  silently accepts a wrong `-boot-load-size`.
- The MSYS2 build takes `/cygdrive/...` paths only, and drags a wider GPL corresponding-source
  obligation than expected (`msys-2.0.dll`, readline, ncurses, iconv, zlib, bzip2).
- Escape hatch if the boot test fails: `DISM /Split-Image /FileSize:4000` into `install.swm`.

**One gate remains for the ISO path: an actual Hyper-V boot test**, which needs elevation.
The checklist is §9 of the spike doc.

## In flight

| Worktree | Owns | Notes |
|---|---|---|
| `catalog-authoring` | `catalog/*.json` | Expand 8 seed components to ~40. Pure data. |
| `unattend-generator` | `src/TinyWin.Unattend` | `autounattend.xml` + golden tests. Fully testable unelevated. |
| `registry-engine` | `src/TinyWin.Registry` | Hive session, privilege enabling, unload-retry. Evaluating `DismGetRegistryMountPoint` first. |
| `imaging-engine` | `src/TinyWin.Imaging` | `DismExeBackend` — now known to be permanent, not a stopgap. |
| `iso-builder` | `src/TinyWin.IsoBuilder` | Launched after the spike verdict, implementing its §11 work list. |
| `ui-shell` | `src/TinyWin.App` | WinUI 3. Templates are not installed, so the csproj is hand-authored. |

## Next

- **M1 vertical slice** — the milestone that actually matters: ISO in, mount, remove 3 appx,
  unmount, rebuild, boot in Hyper-V. Needs `imaging-engine` + `iso-builder` + **elevation**.
- Both spike branches are merged; their worktrees can be archived.
