# TinyWin — working agreements

TinyWin turns a user-supplied Windows 11 ISO into a debloated one, with per-component
opt-out. WinUI 3 / .NET 10, portable single-exe, GPLv3.

**Read `docs/PLAN.md` before writing code.** It holds the architecture, the component
catalog design, the four known technical risks, and the milestone breakdown.

## Conventions (IMPORTANT)

- **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`,
  `test:`, `build:`), scoped where useful. Commit in small, feature-grouped increments.
- **Do NOT** add a `Co-Authored-By: Claude` trailer to commits.

## Project boundaries

Work is fanned out across parallel worktrees (see `docs/PLAN.md` §7), one project per
worktree. **Staying inside your assigned project is the merge strategy** — it is what
lets five branches land cleanly.

| Project | Owns |
|---|---|
| `TinyWin.App` | WinUI 3 UI only. Binds to Core interfaces; never calls DISM directly. |
| `TinyWin.Core` | Build plan model, stage engine, progress, cancellation, recovery. |
| `TinyWin.Imaging` | `IImagingBackend` — mount/appx/capabilities/features/cleanup/export. |
| `TinyWin.Registry` | Offline hive load/unload and typed tweak application. |
| `TinyWin.IsoBuilder` | ISO extract/rebuild, xorriso and oscdimg backends. |
| `TinyWin.Unattend` | `autounattend.xml` generation. |
| `TinyWin.Catalog` | Catalog loader, schema, dependency/conflict resolution. |
| `catalog/*.json` | Component data. Data-only — no C# lives here. |
| `TinyWin.Cli` | Headless runner over Core. Used for integration testing. |

Dependency direction is `App → Core → {Imaging, Registry, IsoBuilder, Unattend, Catalog}`.
Nothing below Core knows the UI exists.

## Non-negotiables

- **The app never downloads Windows and never touches activation or licensing.** It
  operates only on media the user already has.
- **Report no-ops.** Every catalog action records whether its target actually existed. An
  action that silently finds nothing is a bug, and the build report must surface counts.
- **Never leak a `RegistryKey` out of a hive session.** Hives that fail to unload are the
  classic failure of this class of tool — see `docs/PLAN.md` §3.3.
- **Cancellation must unwind**, dismounting with `/discard` rather than leaving a mounted
  image behind.
- GPLv3: keep third-party attributions and the xorriso corresponding-source offer current.

## Target media

24H2 (build 26100) and 25H2 (26200) are one servicing branch and the supported target.
26H1 (28000) is allowed but unverified — warn, don't block. 23H2 and older are rejected.
