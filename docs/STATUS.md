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

## In flight

| Worktree | Owns | Notes |
|---|---|---|
| `spike-iso-build` | `docs/spikes/iso-build.md` | Can xorriso replace oscdimg? Vendoring a Windows binary, then a real ISO build from the `D:` tree. Boot test blocked on elevation. |
| `spike-dism-backend` | `docs/spikes/dism-backend.md` | `Microsoft.Dism` API surface, whether `DismRemoveProvisionedAppxPackage` is a usable export, vs `dism.exe`. |
| `catalog-authoring` | `catalog/*.json` | Expand 8 seed components to ~40. Pure data. |
| `unattend-generator` | `src/TinyWin.Unattend` | `autounattend.xml` generation + golden tests. Fully testable unelevated. |
| `registry-engine` | `src/TinyWin.Registry` | Hive session, privilege enabling, unload-retry. |
| `imaging-engine` | `src/TinyWin.Imaging` | `DismExeBackend` first — the guaranteed floor. |
| `ui-shell` | `src/TinyWin.App` | WinUI 3. Templates are not installed, so the csproj is hand-authored. |

## Next

- **M1 vertical slice** — the milestone that actually matters: ISO in, mount, remove 3 appx,
  unmount, rebuild, boots in Hyper-V. Needs `imaging-engine` + `iso-builder` + elevation.
- `iso-builder` worktree is deliberately **not** launched yet; it waits on the xorriso spike verdict.
