# TinyWin — Implementation Plan

A portable Windows app that takes a user-supplied Windows 11 ISO and produces a
debloated ISO, with per-component opt-out. Conceptually tiny11builder, rewritten in
C# with a WinUI 3 / Fluent UI and a **data-driven, modular component catalog**.

- **Repo:** `github.com/mrzcookie/tinywin` (Orca repo id `a296abdf-6189-482b-acf7-e15540c12a74`)
- **Prior art:** [ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder) — PowerShell, monolithic, two fixed presets
- **What we add:** granular selection, a versioned catalog, resumability, crash recovery, an unattend generator, and a real UI

---

## 1. Product definition

### Requirements
| | |
|---|---|
| Distribution | Single portable `.exe`, no installer, no MSIX. x64 + arm64. |
| Elevation | Always-elevated (`requireAdministrator`). DISM mount + hive load need it. |
| Input | A valid Windows 11 ISO the user already has. **The app never downloads Windows.** |
| Output | A bootable customized ISO next to the source, plus a build log and a saved preset. |
| Modularity | Every removal is an individually toggleable component; presets are just id lists. |
| License | **GPLv3** (`LICENSE` in repo root). Chosen deliberately — it lets us bundle xorriso, which removes the hard dependency on the non-redistributable Windows ADK. See §3.1. |

### Supported input media (v1)

| Version | Build | Status |
|---|---|---|
| **25H2** | 26200 | **Primary target.** Current GA release; serviced to 2027-10-12 (Home/Pro). |
| **24H2** | 26100 | **Supported, free.** Same servicing branch as 25H2 — 25H2 is an enablement package over 26100. Identical package names and registry paths, so one catalog covers both at zero extra cost. Note Home/Pro end-of-servicing is 2026-10-13. |
| 26H1 | 28000 | **Allowed but unverified.** A separate branch scoped to new-device OEM shipping, not offered as an in-place upgrade. Catalog entries are not validated against it. |
| 23H2 and older | 22631− | **Rejected at inspect time.** Past end of updates for Home/Pro. |

Rationale: 24H2 and 25H2 are one codebase from the catalog's perspective, so v1 gets two
supported versions for the price of one. 26H1 is a genuinely different build family and
would need its own catalog validation pass — that's the "major tweaks" we're explicitly
deferring. It is *not* hard-blocked: `appliesTo` degrades it to a warning banner
("catalog not verified for build 28000, actions may no-op") rather than a refusal, and
the per-action no-op reporting in §2.1 is what makes that safe.

### Explicit non-goals
- No ISO downloading, no activation/licensing manipulation, no KMS. The app operates
  on media the user already legitimately possesses. Say this in the README.
- No in-place modification of a running Windows install. Offline images only.
- No driver injection in v1 (good v2 feature — `/Add-Driver` is trivial once the
  pipeline exists).

### Scope warning worth stating up front
Removing servicing components (WinSxS, Windows Update, Defender, WinRE) produces an
image Microsoft will not service. That's tiny11's "core" mode and it's a legitimate
option, but it must be behind a distinct, clearly-labeled risk tier — not mixed in
with "uninstall the Xbox app." The catalog's `risk` field exists for this.

---

## 2. Architecture

Clean project seams are not cosmetic here — they're what makes the Orca
parallelization in §7 possible. Each project below is owned by one agent/worktree.

```
tinywin.sln
├─ src/TinyWin.App/          WinUI 3 UI. MVVM (CommunityToolkit.Mvvm). No DISM code.
├─ src/TinyWin.Core/         Build plan model, stage engine, progress, cancellation, recovery.
├─ src/TinyWin.Imaging/      IImagingBackend: mount/unmount/appx/capabilities/features/cleanup/export.
├─ src/TinyWin.Registry/     Offline hive load/unload + typed tweak application.
├─ src/TinyWin.IsoBuilder/   ISO extract, ISO rebuild, oscdimg/xorriso acquisition.
├─ src/TinyWin.Unattend/     autounattend.xml generation from UI toggles.
├─ src/TinyWin.Catalog/      Catalog loader, schema, dependency/conflict resolution.
├─ src/TinyWin.Cli/          Headless runner over the same Core. CI + integration testing.
├─ catalog/*.json            The component data. Versioned, build-aware.
└─ tests/                    Unit tests per project + a fake imaging backend.
```

**Dependency direction:** `App → Core → {Imaging, Registry, IsoBuilder, Unattend, Catalog}`.
Nothing below Core knows the UI exists. `Cli` is a second head on the same Core, which
is what makes the destructive pipeline testable without clicking through a GUI.

### 2.1 The component catalog — the core design idea

A component is declarative JSON. Adding a removal means adding data, not code.

```jsonc
{
  "id": "apps.xbox",
  "name": "Xbox & Gaming",
  "category": "Apps",
  "description": "Xbox app, Game Bar, Game Overlay, identity provider, TCUI.",
  "risk": "safe",                  // safe | caution | breaking | unserviceable
  "defaultIn": ["balanced", "aggressive", "core"],
  "estimatedSavingsMb": 340,
  "appliesTo": { "minBuild": 22000, "maxBuild": null },
  "requires": [],
  "conflicts": [],
  "breaks": ["Game Bar screen recording", "Xbox achievements & cloud saves"],
  "actions": [
    { "type": "removeProvisionedAppx", "packages": ["Microsoft.XboxApp", "Microsoft.XboxGameOverlay", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay", "Microsoft.Xbox.TCUI", "Microsoft.GamingApp"] },
    { "type": "setRegistry", "hive": "SOFTWARE", "key": "Policies\\Microsoft\\Windows\\GameDVR", "name": "AllowGameDVR", "kind": "dword", "data": 0 },
    { "type": "disableService", "name": "XblAuthManager" }
  ]
}
```

**Closed set of action types** (each has one executor, each is unit-testable against a
fake backend):

`removeProvisionedAppx` · `removeCapability` · `disableFeature` · `removePackage` ·
`setRegistry` · `deleteRegistryKey` · `deleteFile` · `deleteDirectory` ·
`removeScheduledTask` · `disableService` · `takeOwnership`

**`appliesTo` is not optional polish.** Package names and registry paths drift between
build families. A catalog entry that silently no-ops on a newer build is the #1 way this
class of tool rots. The build engine must record, per action, whether the target actually
existed, and surface "12 of 340 actions found no target" in the report.

For v1 the practical `appliesTo` for nearly every component is
`{ "minBuild": 26100, "maxBuild": 26299 }` — covering 24H2 + 25H2 as one branch (see §1).
The field is still mandatory on every entry, because the moment 26H2 or 26H1 validation
happens it becomes the only thing standing between us and a catalog that lies.

**Proposed catalog groupings**

| Category | Components |
|---|---|
| AI & Assistant | Copilot, Recall / Windows AI, AI Explorer components, online speech |
| Microsoft Apps | **Edge** and **WebView2** as two independent components (see below), OneDrive, Teams/Chat, Outlook for Windows, Mail & Calendar, Office Hub, Dev Home, Quick Assist, Family, Clipchamp |
| Media & Casual | Zune Music/Video, Camera, Sound Recorder, Solitaire, Paint, Sticky Notes, 3D Viewer, Maps, People |
| Gaming | Xbox bundle, GameDVR |
| Bing & Content | Bing News/Weather/Search, Widgets, Start menu suggestions, Lock screen ads, Consumer features |
| Telemetry & Privacy | DiagTrack, CEIP, Compatibility Appraiser, advertising ID, tailored experiences, input personalization, error reporting, dmwappushservice |
| Optional Features | WMP legacy, WordPad, IE mode, .NET 3.5, Fax/Scan, XPS, Print-to-PDF, Hyper-V, WSL, Remote Desktop, PowerShell ISE |
| Language & Input | Handwriting, OCR, TTS, speech recognition, non-selected language packs, retail demo |
| System (caution) | Windows Search index, System Restore, Reserved Storage, device encryption, Widgets service |
| Unserviceable (core tier) | **Windows Defender**, WinRE, WinSxS component store, Windows Update stack, servicing stack |

#### Edge vs WebView2 — two components, not one

tiny11 deletes `Program Files (x86)\Microsoft\Edge*` **and**
`System32\Microsoft-Edge-Webview` together. That's wrong for our purposes: WebView2 is a
shared runtime, and removing it silently breaks unrelated third-party apps (Teams, many
Electron-adjacent installers, several Office surfaces) in ways the user will never
connect back to this tool.

| id | Default | Notes |
|---|---|---|
| `apps.edge` | **remove** in balanced+ | Deletes Edge, EdgeUpdate, EdgeCore; removes the uninstall registry entries. `breaks`: no default browser until one is installed; some Settings pages show dead links. |
| `apps.edge.webview2` | **keep** — off in every shipped preset, including `core` | Opt-in only, `risk: "breaking"`. `breaks`: any third-party app embedding WebView2 fails to start. |

`apps.edge.webview2` declares `requires: ["apps.edge"]` — removing the runtime while
keeping the browser is incoherent, and the resolver should refuse it.

#### Windows Defender — included, gated

Included as an `unserviceable`-tier component (`system.defender`), not omitted. Reasoning:
people building test/lab VMs genuinely want it gone, and if we don't ship it they'll
apply a worse hand-rolled version anyway. The safety comes from the tier, not from
absence:

- `risk: "unserviceable"`, so it inherits the §4 Review-page gate that requires typing a
  confirmation string, not just a click.
- `defaultIn: ["core"]` only. Never in minimal/balanced/aggressive.
- `breaks` copy states plainly: no antivirus, no SmartScreen, no tamper protection, and
  it **cannot be reinstalled** on the resulting image.
- Removal is not reversible post-install, so the Review page says so explicitly.

**Separately: tweaks** (settings, not removals — different tab, different mental model):
TPM/SecureBoot/RAM/CPU install bypasses, `BypassNRO` for local accounts, disable
BitLocker auto-encryption, classic right-click context menu, taskbar alignment,
disable Start pins, show file extensions, disable web search in Start.

### 2.2 The build pipeline

Ordered, individually resumable stages. Each reports `(stage, percent, message)` and
appends to a structured log.

1. **Preflight** — verify admin, ≥25 GB free on scratch volume, no stale DISM mounts
   (`/Get-MountedImageInfo`), no leftover loaded hives from a crashed run.
2. **Inspect ISO** — mount read-only, confirm `sources\install.wim|.esd` + `boot.wim`,
   enumerate editions/indexes/build/arch/language, reject non-Win11 media.
3. **Stage** — copy ISO contents to scratch.
4. **Normalize** — `install.esd` → `install.wim` via export if needed.
5. **Mount** — mount the selected index.
6. **Apply plan** — resolve catalog → flat ordered action list → execute.
   Order matters: appx → capabilities → features → packages → files → tasks.
7. **Offline registry** — load the 5 hives, apply tweaks, unload (see §3.3).
8. **Unattend** — write generated `autounattend.xml` into the ISO root.
9. **Component cleanup** — `/Cleanup-Image /StartComponentCleanup /ResetBase`
   (skipped in core tier where WinSxS is already gone).
10. **Commit** — `Dismount-WindowsImage -Save`.
11. **Recompress** — `/Export-Image /Compress:recovery`.
12. **boot.wim** — apply the install-time bypass tweaks to boot.wim index 2.
13. **Build ISO** — oscdimg with the dual BIOS/UEFI El Torito boot data.
14. **Verify & report** — size before/after, per-action success/no-op counts, log path.

Every stage writes a `state.json` checkpoint so a failed run can resume from the last
good stage instead of re-copying 6 GB.

---

## 3. The four real technical risks

These are the parts that decide whether this project takes two weeks or two months.
Each gets a **timeboxed spike before** the milestone that depends on it.

### 3.1 ISO creation — **highest risk**, but the legal half is now solved

`oscdimg.exe` ships in the Windows ADK and is **not redistributable**; we cannot bundle
it. Confirmed: it is not present on this dev machine, and requiring every user to install
a ~100 MB SDK before a "portable app" runs would undercut the entire premise.

**GPLv3 resolves the licensing blocker**, so xorriso is now the intended primary path
rather than a fallback:

- **(a) Bundle xorriso** (GPLv3) next to / inside the exe. Zero setup, works on a
  machine with nothing installed. This is what makes TinyWin genuinely portable.
- **(b) Use a detected ADK** `oscdimg.exe` if one is present
  (`%ProgramFiles(x86)%\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\<arch>\Oscdimg\oscdimg.exe`)
  — selectable in settings, and the automatic fallback if (a) fails on some edge-case
  media.
- **(c)** Offer a consented `adksetup.exe /features OptionId.DeploymentTools` install
  only as a last resort, if both above fail.

GPLv3 removes the *legal* blocker, not the *technical* one — the spike gated this.

> **Spike complete: conditional GO.** See `docs/spikes/iso-build.md`. xorriso is adopted as
> primary with `oscdimg` as runtime-selectable fallback. One gate remains: an actual boot
> test, which needs elevation for Hyper-V.

**The UDF premise above was wrong, and the correction matters.** This section previously
asserted that a >4 GB `install.wim` "needs UDF, not ISO9660". Both halves are false:

- **xorriso cannot write UDF at all** — not a missing flag, the filesystem is unimplemented
  in libisofs. Had UDF genuinely been required, this would have been a hard NO-GO.
- **ISO 9660 level 3 permits multi-extent files**, lifting the 4 GiB single-extent ceiling.
  Verified end to end: a 4.7 GiB member file written with `-iso-level 3`, mounted by
  Windows' own `cdfs.sys`, and read back with a matching SHA-256 across the 4 GiB boundary.

So the requirement is **ISO 9660 level 3 + Joliet**, not UDF. The resulting media differs
from Microsoft's own (which is UDF-only) in filesystem type while staying readable by
Windows — and validating that deviation is exactly what the boot test is for.

Three further findings that `TinyWin.IsoBuilder` must act on:

1. **`-J -joliet-long` is mandatory.** With UDF gone, Joliet carries exact filenames. The
   two plausible alternatives are traps: `-full-iso9660-filenames` uppercases and truncates
   at 31 chars, and `-untranslated-filenames` *silently* truncates at 37 — it looks correct
   on inspection while corrupting long names. Joliet caps basenames at 103 chars, so the
   builder must preflight the staged tree and **fail loudly** rather than emit truncated
   media.
2. **Read boot geometry from the source ISO, don't hardcode it.**
   `xorriso -report_el_torito as_mkisofs` emits the exact options reproducing an existing
   image's boot setup. xorriso silently accepts a wrong `-boot-load-size`, so guessing is
   unsafe.
3. **The MSYS2 build takes POSIX paths only.** Every path argument needs converting to
   `/cygdrive/<drive>/...`; `C:\...` and `C:/...` both fail. Small, testable, and a golden
   test belongs on it.

**Escape hatch if the boot test fails on the >4 GB read:** split with
`DISM /Split-Image /FileSize:4000` into `install.swm`, which Windows Setup supports
natively and which removes the multi-extent requirement entirely. Ship it behind a setting.

**Remaining GPLv3 cost.** The vendored MSYS2 bundle is 8 files / 6.6 MB, but it drags in
`msys-2.0.dll` (LGPLv3), readline, ncurses, iconv, zlib and bzip2 — corresponding source
for *each* must be mirrored into our own release assets, not merely linked upstream. M5
should switch to a native mingw-w64 build from pinned upstream source in CI, which both
shrinks that obligation (`--disable-libreadline`, `--disable-libbz2`, static link) and
removes the `/cygdrive` tax. That build is **unproven** — no toolchain was installed to try
it — so it is a tracked M5 task, not a settled fact. arm64 xorriso is an open question.

Because the whole toolchain is now GPLv3, also record third-party attributions and ship
xorriso's corresponding source offer alongside releases — a GPLv3 obligation, not a
nicety. Fold this into M5 packaging.

Note: `DiscUtils` is **not** a viable option — its El Torito support is partial and its
UDF write support is inadequate for this.

### 3.2 Talking to DISM from managed code — **resolved by spike**

> **Spike complete.** See `docs/spikes/dism-backend.md`. Two findings changed this section;
> the original text is corrected below rather than preserved.

**Finding 1 — appx is already solved.** `Microsoft.Dism` 6.0.0 (MIT, with a first-class
`net10.0` target) already wraps the undocumented `DismGetProvisionedAppxPackages` and
`DismRemoveProvisionedAppxPackage` exports, and wraps them *correctly*. A hand-rolled
`PInvokeAppxBackend` is therefore **struck from this plan**. It would have re-derived
marshalling that a maintained package already does, while inviting a genuinely nasty bug:
`dismapi.h` declares its structs under `#pragma pack(push, 1)`, so the obvious
`LayoutKind.Sequential` P/Invoke gets a 72-byte stride where the truth is 68. That does not
crash — it yields a plausible-looking package list whose later fields are garbage, drifting
further each record. "TinyWin removed the wrong package" is exactly the failure our no-op
reporting cannot catch.

**Finding 2 — `dism.exe` is permanently mandatory, not a fallback.** This is the
load-bearing result. Neither `/Cleanup-Image /StartComponentCleanup /ResetBase` (stage 9)
nor `/Export-Image` (stage 11) exists as *any* export in `dismapi.dll`. The full export
table was checked. Whatever else TinyWin does, those two stages shell out to `dism.exe`, so
the process-invocation plumbing — argument building, output parsing, exit-code mapping,
cancellation — has to exist and be well-tested regardless of what else we build.

That makes the original "ship `DismExeBackend` first" decision correct for a stronger reason
than originally stated, and reduces three backends to two:

| Backend | Role | Covers |
|---|---|---|
| `DismExeBackend` | **Ships first. Never removed.** | Everything, including cleanup + export |
| `ManagedDismBackend` | M2 optimisation | Everything *except* cleanup + export, which delegate to `DismExeBackend` |

`ManagedDismBackend` is a **partial override composing over** the exe backend, not a peer
implementation. Model it that way explicitly — a `ManagedDismBackend` that throws
`NotSupportedException` from `CleanupImageAsync` is a trap for the stage engine.

Two consequences for the `IImagingBackend` contract, both already reflected in the shipped
interface:

- Cleanup and export are first-class members, not incidental helpers.
- The appx methods in `Microsoft.Dism` have **no progress and no cancellation overloads**.
  Per-package progress therefore belongs to Core's stage engine, not the backend, and
  cancellation can only be honoured *between* packages. Appx removal is the longest part of a
  debloat run, so a UI showing nothing between packages will look hung.

Also surfaced for §3.3: `DismGetRegistryMountPoint(session, DismRegistryHive)` exists, and
may be a cleaner route than hand-rolled `RegLoadKey`.

<details>
<summary>Original three-option framing, superseded</summary>

The documented DISM C API does not cover provisioned appx enumeration/removal, but
`DismRemoveProvisionedAppxPackage` and friends **do exist** as exports in `dismapi.dll`
(undocumented). Three options, and we ship with an abstraction so the choice is
reversible:

```csharp
interface IImagingBackend {
    Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(string mountPath, CancellationToken ct);
    Task RemoveProvisionedAppxAsync(string mountPath, string packageName, IProgress<double> p, CancellationToken ct);
    // + mount/unmount/capabilities/features/packages/cleanup/export
}
```

- `ManagedDismBackend` — `Microsoft.Dism` NuGet (MIT) for the documented surface: mount,
  unmount, features, capabilities, packages, cleanup, with real progress callbacks.
- `PInvokeAppxBackend` — direct P/Invoke to the undocumented exports for appx.
- `DismExeBackend` — `dism.exe` process invocation with output parsing. Slower, uglier,
  **always works**. This is the guaranteed-shippable floor.

**Decision rule:** implement `DismExeBackend` first so the pipeline is unblocked on day
one. Swap in the native backends behind the same interface as a pure optimization, and
keep `DismExeBackend` as a runtime-selectable fallback in settings.

</details>

**Still unverified**, pending one elevated run of `docs/spikes/harness/run-elevated.ps1`:
that the native call returns a sane package list against a live image, the 68-byte record
stride, and whether `dism.exe` renders its progress bar with backspaces under redirection
(which decides whether `DismExeBackend` can report real percentages or only stage
transitions). None of these change the decision above.

### 3.3 Offline registry hives that refuse to unload

The classic failure of every tool in this space: `RegUnLoadKey` returns
`ERROR_ACCESS_DENIED`, the hive stays mounted, the WIM won't dismount, and the user
needs a reboot. Requirements:

- Explicitly enable `SeBackupPrivilege` and `SeRestorePrivilege` on the process token.
  They are present-but-disabled by default even when elevated.
- A `HiveSession : IDisposable` that owns the lifetime and guarantees unload.
- Before unload: dispose every `RegistryKey`, then `GC.Collect()` +
  `WaitForPendingFinalizers()` + retry with backoff. .NET's `RegistryKey` finalizers are
  the usual culprit.
- Never hand a raw `RegistryKey` out of the Registry project — expose only
  `Apply(RegistryAction)` so no handle can escape the session scope.
- Crash recovery at startup: enumerate `HKLM` for our `z*` prefixes and unload strays,
  plus `dism /Cleanup-Mountpoints`.

Hives: `COMPONENTS`, `default`, `Users\Default\ntuser.dat`, `SOFTWARE`, `SYSTEM`.

### 3.4 Portable single-exe WinUI 3

Confirmed viable since Windows App SDK 1.5, but only under a specific combination:

```xml
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

Consequences to accept up front: ~150–250 MB exe, first-launch extraction to temp, and
no manifest-based features (no file associations, no background tasks) — none of which
we need. `.NET 10 SDK 10.0.302` is already installed on this machine.

---

## 4. UI design (WinUI 3 / Fluent)

`NavigationView` shell, Mica backdrop, follows system light/dark, with a bottom-pinned
persistent "Build" action.

| Page | Contents |
|---|---|
| **Source** | Drag-and-drop ISO target + browse. On drop: inspect, then show build number, architecture, language, and the edition/index list as selectable cards. |
| **Customize** | The main event. `TreeView` of categories → components with tri-state checkboxes. Search box. Risk badge per row (`safe`/`caution`/`breaking`/`unserviceable`, color-coded). Hovering a row shows a flyout listing exactly what breaks. A live "estimated size: 5.4 GB → 3.1 GB" readout in the header. Preset dropdown (Minimal / Balanced / Aggressive / Core / Custom) + import/export preset JSON. |
| **Tweaks** | Toggle list for install bypasses, local-account/OOBE, UI defaults. Grouped, each with a one-line explanation. |
| **Review** | Read-only expandable summary of every action that will run, grouped by stage. `InfoBar` warnings escalating with risk tier — the unserviceable tier requires typing a confirmation. |
| **Build** | Stage list with per-stage state (pending/running/done/failed), overall progress ring, elapsed + rough ETA, and a collapsible live log pane with a "copy log" button. Cancel button that unwinds safely (dismount /discard) rather than leaving a mounted image. |
| **Done** | Before/after size, action success/no-op counts, "reveal ISO in Explorer", "save these choices as a preset", "view full log". |

UI principles worth enforcing in review: nothing destructive is one click from the
start; the app tells you *what breaks*, not just what it removes; and every no-op action
is reported rather than silently swallowed.

---

## 5. Testing

The pipeline is slow and destructive, so the test strategy has to carry more weight than
usual.

- **Unit (xunit)** — Core/Catalog/Unattend/Registry against `FakeImagingBackend`, which
  records the action sequence. Assert on the resolved plan, not on DISM.
- **Catalog schema test** — validates every JSON file: known action types, no dangling
  `requires`/`conflicts`, unique ids, presets reference real ids. This runs in CI and is
  what keeps the catalog from rotting as contributors add entries.
- **Golden tests** — generated `autounattend.xml` and generated oscdimg command line
  compared against checked-in expected output.
- **Integration** — `TinyWin.Cli --iso X --preset balanced --out Y` against a real ISO.
  Not in cloud CI (needs admin + 25 GB + hours). Run locally / on a self-hosted runner.
- **Boot smoke test (stretch, high value)** — script Hyper-V: create a Gen2 VM, attach
  the output ISO, boot, screenshot at 60 s, assert Setup reached the language screen.
  This is the only test that actually proves the ISO works, and it's ~150 lines of
  PowerShell. Worth doing early.

---

## 6. Milestones

| | Milestone | Contents | Gate |
|---|---|---|---|
| **M0** | Skeleton & contracts | Solution, all projects, DI wiring, `IImagingBackend`/`ICatalog`/`IBuildStage` interfaces, `FakeImagingBackend`, catalog JSON schema, CI (build + unit tests). | Everything downstream depends on these interfaces. **Must land on `main` before fan-out.** |
| **M0.5** | Two spikes, in parallel throwaway worktrees | (a) xorriso vs ADK ISO build; (b) native DISM P/Invoke vs `dism.exe`. | Both are go/no-go, not deliverables. Timebox 2 days each. |
| **M1** | Vertical slice, headless | `TinyWin.Cli` end-to-end: ISO in → mount → remove 3 appx → unmount → rebuild ISO → boots in Hyper-V. Ugly is fine. | **The single most important milestone.** Proves the whole risky path before any UI exists. |
| **M2** | Engines | Full `Imaging` action executors, `Registry` hive session, `Unattend` generator, `IsoBuilder` with ADK detection + acquisition. Stage engine with checkpointing and crash recovery. | Four independent worktrees. |
| **M3** | Catalog | The full component set from §2.1, with `breaks`, size estimates, `appliesTo` ranges, and the four presets. Verified against a real 25H2 (26200) ISO and spot-checked against 24H2 (26100). | Independent worktree; pure data + tests. |
| **M4** | UI | All seven pages, MVVM over Core, live progress, drag-drop, presets import/export. | Starts once M0 contracts freeze; can overlap M2/M3. |
| **M5** | Portable packaging | Single-exe publish x64 + arm64, elevation manifest, icon, versioning, GitHub Release workflow. GPLv3 obligations: bundled-xorriso attribution + corresponding-source offer in each release. | |
| **M6** | Hardening | Crash recovery UX, cancellation unwinding, disk-space guards, boot smoke test in CI, README with the legal/scope statement. | |

---

## 7. Building this with Orca

The project decomposes unusually well: after M0, four of the five workstreams touch
disjoint project directories. That's a genuine parallel fan-out, not a forced one.

### Phase 1 — sequential, single worktree
M0 must be authored once and land on `main`. Fanning out before the interfaces exist
means four agents inventing four incompatible `IImagingBackend`s.

```
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 \
  --name m0-skeleton --agent claude \
  --prompt "Read docs/PLAN.md. Implement M0 only: solution, all projects per §2, the interfaces in §2.2/§3.2, FakeImagingBackend, catalog JSON schema, CI. No feature code." --json
```

### Phase 2 — spikes, independent and disposable
Run alongside M0. These may be dead ends, which is exactly why they get their own
worktrees rather than polluting `main`.

```
orca worktree create --name spike-iso-build --no-parent --agent claude \
  --prompt "Read docs/PLAN.md §3.1. Timebox 2 days. Project is GPLv3 so bundling xorriso is permitted - determine whether xorrisofs can produce a UEFI+BIOS bootable Windows 11 ISO with a >4GB install.wim, using a dual El Torito catalog (etfsboot.com + efisys.bin via -eltorito-alt-boot) and UDF. Test in Hyper-V Gen1 and Gen2. Also recommend vendoring vs building the Windows xorriso binary. Write findings to docs/spikes/iso-build.md. Do not build app features." --json

orca worktree create --name spike-dism-pinvoke --no-parent --agent claude \
  --prompt "Read docs/PLAN.md §3.2. Determine whether DismRemoveProvisionedAppxPackage / DismGetProvisionedAppxPackages can be P/Invoked reliably from .NET 10, vs shelling to dism.exe. Write findings to docs/spikes/dism-backend.md." --json
```

### Phase 3 — parallel fan-out, one worktree per project
Once M0 is on `main`. File overlap between these is near zero.

```
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 --name imaging-engine  --agent claude --prompt "Read docs/PLAN.md. Own src/TinyWin.Imaging + its tests only." --json
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 --name registry-engine --agent claude --prompt "Read docs/PLAN.md §3.3. Own src/TinyWin.Registry + its tests only." --json
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 --name iso-builder     --agent claude --prompt "Read docs/PLAN.md §3.1. Own src/TinyWin.IsoBuilder + its tests only. Wait for docs/spikes/iso-build.md." --json
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 --name catalog         --agent claude --prompt "Read docs/PLAN.md §1 and §2.1. Own catalog/*.json + schema tests only. No C# outside TinyWin.Catalog. Target builds 26100-26299 (24H2+25H2) as one branch. Edge and WebView2 are separate components with WebView2 defaulting to keep; Defender is unserviceable-tier, core preset only." --json
orca worktree create --repo id:a296abdf-6189-482b-acf7-e15540c12a74 --name ui-shell        --agent claude --prompt "Read docs/PLAN.md §4. Own src/TinyWin.App only. Bind to Core interfaces; never call DISM directly." --json
```

The catalog worktree is the best parallelization target of all — it's ~40 JSON files of
independent research (which packages, which registry keys, what breaks), it's pure data,
and it has zero merge risk with the engine work.

### Conventions

Repo-wide working agreements live in `CLAUDE.md` at the root, so every agent spawned into
a worktree inherits them automatically. In short: **Conventional Commits**
(`feat:`/`fix:`/`chore:`/`docs:`/`ci:`/`refactor:`/`test:`/`build:`), scoped where useful,
in small feature-grouped increments — and **no `Co-Authored-By` trailer**.

- Each worker updates its card as it goes:
  `orca worktree set --worktree active --comment "hive unload retry working" --json`
  and `--workspace-status in-review` when ready.
- **Ownership boundaries are the merge strategy.** The prompts above assign one project
  per worktree deliberately; keeping to them is what makes five parallel branches merge
  cleanly.
- Integrate at milestone boundaries, not continuously. Merge M2's four branches together
  and run the CLI integration test before starting M4 UI polish.
- For supervised coordination (task DAGs, blocking on spike results before the
  iso-builder worker proceeds), use the `orchestration` skill rather than the fire-and-
  forget `worktree create --agent` handoff above.

---

## 8. Decisions (resolved 2026-07-22)

| # | Question | Decision | Consequence |
|---|---|---|---|
| 1 | License | **GPLv3**, `LICENSE` committed | xorriso becomes the *primary* ISO builder, not a fallback — the app is portable with no ADK. Adds a source-offer/attribution obligation to M5. §3.1 rewritten. |
| 2 | Build coverage | **24H2 (26100) + 25H2 (26200)**; 26H1 allowed-but-unverified; 23H2− rejected | Two supported versions for one catalog's effort, since 25H2 is an enablement package over 26100. No per-build catalog forking in v1. §1 + §2.1. |
| 3 | Edge / WebView2 | **Two independent components**; `apps.edge.webview2` defaults to *keep* in every preset including `core` | Diverges from tiny11, which removes both together and silently breaks third-party WebView2 apps. §2.1. |
| 4 | Defender | **Included**, as `unserviceable` tier, `defaultIn: ["core"]` only | Gated behind the typed-confirmation Review gate. §2.1. |

### Follow-on questions these created

- **xorriso build provenance** — vendor a prebuilt Windows xorriso binary, or build it as
  a release step? Vendoring is simpler; building is cleaner for the GPL source offer.
  Decide during the §3.1 spike.
- **26H1 in the UI** — warning banner only, or also a settings toggle to suppress it for
  users who've verified their own catalog? Defer to M4.

## Sources
- [ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder) — reference behavior, package list, registry keys
- [Distribute an unpackaged WinUI 3 app](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app) / [self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [jeffkl/ManagedDism](https://github.com/jeffkl/ManagedDism) — managed DISM API wrapper
- [DISM app package servicing options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-app-package--appx-or-appxbundle--servicing-command-line-options)
- [Create an ISO image for UEFI platforms](https://learn.microsoft.com/en-us/troubleshoot/windows-server/setup-upgrade-and-drivers/create-iso-image-for-uefi-platforms)
- [DiscUtils El Torito limitations](https://github.com/DiscUtils/DiscUtils/issues/247)
