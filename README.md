# TinyWin

A portable Windows app that turns a Windows 11 ISO you already have into a lighter one,
with per-component control over what gets removed.

Conceptually a descendant of [tiny11builder](https://github.com/ntdevlabs/tiny11builder),
rebuilt in C# with a WinUI 3 interface and a modular, data-driven component catalog instead
of two fixed presets.

> **Status: in development.** The contracts, catalog engine and CLI are in place; the
> imaging, registry, ISO and UI layers are being built. Nothing here is ready to use yet.
> See [`docs/STATUS.md`](docs/STATUS.md).

## What it does

- **Takes an ISO you supply**, inspects it, and lets you pick an edition.
- **Removes what you choose** — around 40 individually toggleable components across apps,
  telemetry, optional features and system services, grouped into four presets
  (Minimal / Balanced / Aggressive / Core).
- **Tells you what breaks.** Every component carries a plain-language list of the
  consequences, and a risk tier that decides how hard the UI makes it to select.
- **Reports what it actually did**, including actions whose target was not found — a
  catalog entry that silently no-ops is a bug, not a success.
- **Writes a bootable ISO** back out.

## What it does not do

- **It never downloads Windows.** You supply media you already legitimately possess.
- **It never touches activation or licensing.** No KMS, no key generation, no bypasses.
- **It does not modify a running installation.** Offline images only.

TinyWin is a customisation tool for your own installation media, in the same spirit as an
unattended answer file or an MDT task sequence. Removing components does not change the
terms under which you licensed Windows, and none of the operations here are novel — they
are the documented DISM and offline-registry mechanisms Microsoft ships for exactly this
purpose.

## Requirements

- Windows 10/11 x64 or arm64
- **Administrator rights** — DISM refuses to service an image without them
- ~25 GB of free scratch space
- A Windows 11 **24H2 (build 26100)** or **25H2 (26200)** ISO

26H1 (build 28000) media is accepted with a warning: the catalog has not been validated
against that branch, so some removals may find no target. 23H2 and older are refused —
they are past end of updates.

## A word on the Core preset

The `core` preset removes servicing itself: WinSxS, the Windows Update stack, WinRE and
Defender. The result **cannot be updated, repaired, or have features added, ever**. It is
genuinely useful for disposable test and lab VMs, and genuinely unsuitable for a machine
you care about. The UI requires typing a confirmation before it will build one.

## Building

```
dotnet build tinywin.slnx
dotnet test tinywin.slnx
```

Useful during development:

```
dotnet run --project src/TinyWin.Cli -- doctor              # can this machine run a build?
dotnet run --project src/TinyWin.Cli -- catalog --validate  # is the catalog well-formed?
dotnet run --project src/TinyWin.Cli -- presets             # what does each preset resolve to?
```

### Building a release locally

```
powershell -File tools/fetch-xorriso.ps1 -WithSource   # vendored xorriso + its source
powershell -File tools/publish.ps1                     # portable exe, win-x64 and win-arm64
powershell -File tools/pack-source.ps1                 # the GPLv3 corresponding-source archives
```

Everything lands in `artifacts/`. This is the same path CI takes — see
[`.github/workflows/release.yml`](.github/workflows/release.yml), which runs on a `v*` tag.

## Documentation

| | |
|---|---|
| [`docs/PLAN.md`](docs/PLAN.md) | Architecture, catalog design, technical risks, milestones |
| [`docs/STATUS.md`](docs/STATUS.md) | What has landed and what is in flight |
| [`docs/spikes/`](docs/spikes/) | Investigations into the DISM backend and ISO builder |
| [`CLAUDE.md`](CLAUDE.md) | Working agreements and project boundaries |

## Licence

GPLv3 — see [`LICENSE`](LICENSE).

TinyWin bundles [GNU xorriso](https://www.gnu.org/software/xorriso/) (GPLv3+) to build ISOs
without requiring the Windows ADK. Corresponding source for xorriso and its runtime
dependencies is attached to every release, as GPLv3 §6 requires — from the same place as the
binaries, not merely linked. [`THIRD-PARTY.md`](THIRD-PARTY.md) lists every bundled
component, its licence and where its source comes from.

## Acknowledgements

[ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder) established the
approach this project builds on. Its published package list and registry keys were the
starting point for the catalog.
