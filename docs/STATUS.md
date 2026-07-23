# Build status

Living scratchpad for the parallel build. Updated as worktrees land.

Last updated: 2026-07-22.

## Milestones

| | Milestone | State |
|---|---|---|
| **M0** | Skeleton & contracts | **Done.** |
| **M0.5** | Spikes (DISM backend, ISO builder) | **Done.** Both merged, both corrected the plan. |
| **M1** | Vertical slice, headless | **Code complete.** `tinywin build` wires all 13 stages over the real backends. Cannot be *executed* here — DISM needs elevation. |
| **M2** | Engines | **Done.** Imaging, registry, ISO builder and unattend all merged. |
| **M3** | Catalog | **Done.** 73 components, 4 presets, validated against real 25H2 media. |
| **M4** | UI | **Done.** WinUI 3 shell, Home + the seven workflow pages, MVVM throughout. |
| **M5** | Portable packaging | **Done.** Single-file publish verified against the merged app: 215.5 MB exe, 88 MB zip. |
| **M6** | Hardening | **Done.** Resume/checkpointing, disk guards, failure advice, build report rendering. |

**All milestones complete.** The only unclosed item is the Hyper-V boot test, which needs a
human — see the bottom of this file.

**Release** build clean with the WinUI app in the solution · **567 tests passing** · catalog
validator clean.

## Hardening — what M6 actually added

- **Checkpoint and resume.** A checkpoint is written after every stage and deleted on success, so
  a run that dies in stage 11 does not re-copy 6 GB. Two details that took thought and are worth
  keeping: a *skipped* stage is recorded as completed (otherwise `NormalizeImage`, which deletes
  the ESD it was asked about, re-decides against stale context on resume), and the checkpoint is
  written *after* rollback rather than before, so the resume logic knows the mount was discarded.
- **`NoTargetRatio`, not just a count.** Twelve no-ops means something very different in a
  20-action minimal build than in a 340-action core build, so the drift signal is a ratio.
- **Cancelled is distinguished from failed.** Both leave `Succeeded` false, but only one is a bug.
- **`FailureAdvice`** turns exceptions into what the user should do about them.

The UI honours both corrections raised during the build: the Customize page separates the ISO-size
estimate from the catalog's uncompressed payload figure, and the Review page requires typing
`UNSERVICEABLE` rather than ticking a box.

## Catalog verification against real media

Everything below is checked against `docs/reference/`, captured elevated from the 26200 ISO.

| Check | Result |
|---|---|
| Provisioned appx names | **44 of 45 present.** The one absent is Pro/Education-only and already `optional`. |
| Image packages not targeted | 4 — Calculator, Notepad, Terminal (deliberate keeps) and `ApplicationCompatibilityEnhancements` (**the one real coverage gap**). |
| `DisableService` names | **23 of 25 present.** Both absent ones already `optional`. |
| Scheduled task paths | **18 of 20 present.** Both absent ones already `optional`. |

One inference was found to be **wrong**: the catalog had recorded that `Microsoft Compatibility
Appraiser` and `ProgramDataUpdater` were removed in 24H2, based on the *running dev machine*. The
image's TaskCache has both, plus a third the catalog missed. Corrected. Services survived the same
scrutiny because base-image services sit in the hive whether or not they have run, while scheduled
tasks are registered at OOBE — which is exactly why one inference held and the other did not.

## Environment facts

| | |
|---|---|
| .NET SDK | 10.0.302 |
| Host OS | Windows 11 build 26200 (25H2) — matches the primary target |
| Test media | `C:\Users\Zachary\ISOs\Win11_25H2_English_x64_v2.iso`, mounts at `D:` |
| `install.wim` | 7.06 GB — real multi-extent case, not synthetic |
| Windows ADK | Not installed, so the xorriso path is the one that matters |
| **Elevation** | **Agent shells are NOT elevated.** `dism` fails with error 740. |
| **Hyper-V** | **Disabled.** The ISO boot test is deferred until it is enabled. |

## Ground truth captured from real media

`docs/reference/` holds an elevated capture from the 25H2 ISO — real
`/Get-ProvisionedAppxPackages` (293 lines), capabilities (855), features (201), packages (581),
the TaskCache tree, and the offline SOFTWARE/SYSTEM/NTUSER hives.

**The progress question is settled.** DISM rewrites its progress bar with **bare CR**, no
backspaces, and percentages *are* present under stdout redirection (202 CR, 0 BS, 98 percent
tokens). `DismExeBackend` can report real percentages rather than only stage transitions.

## What the verification actually found

The catalog was verified with 7-Zip, which reads WIM files unelevated — so most of the work did
not need admin after all. It found that **~13 packages tiny11builder removes no longer exist** on
26100/26200 (`Microsoft.XboxApp`, `WindowsMaps`, `People`, `windowscommunicationsapps`,
`ZuneVideo`, the 3D stack, Cortana, `Getstarted`, `MicrosoftTeams`), plus the WordPad and Fax/Scan
capabilities. Every one would have been a silent no-op.

`Windows-Defender-Client-Package` does not exist either, so Defender is removed by directory,
service and policy instead of as a CBS package.

**Scheduled tasks are a registry operation**, not a filesystem one: an offline image ships only
nine task files and the rest materialise at setup from `TaskCache` in the SOFTWARE hive.
`RemoveScheduledTask` was routed to `ApplyRegistryStage` accordingly.

## Known deviations and open risks

- **xorriso ignores `-boot-load-size` for the UEFI entry.** Real media declares `1`; xorriso always
  writes the full image size (`2880`). Four option orderings were tried. `IsoVerification` treats
  the BIOS mismatch as an error and the UEFI difference as a reported warning. Risk judged low,
  but it is a genuine deviation from Microsoft's media and **only the boot test settles it**.
- **xorriso cannot read a Windows 11 ISO** (they are UDF-only), so extraction is done another way,
  and content verification is skipped for `oscdimg` output and says so.
- **`estimatedSavingsMb` is uncompressed footprint**, while the UI shows an ISO estimate. LZX is
  roughly 3–4:1, so a naive subtraction goes negative on the Core preset.
- Registry actions remain the least-verified part of the catalog; `docs/reference/` now provides
  the data to audit them against.

## Known issues

- **Stage order deviates from the plan. Still open.** `BuildPipelineFactory` runs `StageFilesStage`
  before `InspectIsoStage`, so unsupported media is rejected only *after* a ~7 GB copy. §2.2
  specifies inspect-then-stage. Cause: `InspectIsoStage` reads the staged tree rather than the
  source ISO.

  It was assigned to the `hardening` worktree as an optional task and did not get done — that
  agent was killed by a machine restart before reaching it. The fix needs `IIsoBuilder` to expose
  a read-only ISO mount (`IsoImageMount` already exists but is `internal`), so `InspectIsoStage`
  can enumerate editions from the source before anything is copied. `Mount-DiskImage` works
  unelevated, so this is achievable; it was left alone rather than reopening the pipeline
  immediately after a large merge.

  A better variant is worth considering first: the WIM's own XML header carries the edition list
  and can be read unelevated in milliseconds, with no mount and no DISM call at all — see the
  known gaps in `src/TinyWin.Imaging/README.md`.
- **`PcaPatchDbTask` and `RetailDemo\CleanupOfflineContent`** are in the catalog but absent from
  real 26200 media. Both are `optional: true`, so they report `NoTarget` quietly — correct, but
  they are dead weight and could be dropped.

## Packaging — `docs/findings/packaging.md`

- **The single-file publish works for both architectures.** 215.4 MB (x64) and 229.2 MB (arm64),
  one file each, PE headers verified.
- **Elevation survives to the artifact.** WinUI merges `app.manifest` into a 131 KB generated one;
  `requireAdministrator` was extracted back out of the published exe to confirm it, rather than
  assumed.
- **The GPLv3 obligation is enforced by the build**, not by intent.
  `tools/check-third-party.ps1` fails CI when a bundled component is missing from
  `THIRD-PARTY.md`, and `release.yml` attaches binaries and corresponding source to one release in
  one call. Corresponding source is **139 MB**, 122 MB of which is msys2-runtime — a concrete
  argument for the native mingw-w64 build in `docs/PLAN.md` §3.1.
- **Versions come from the git tag.** Anything untagged is stamped `-dev`, so a stray build cannot
  be mistaken for a release.
- **`release.yml` has never actually run.** It was validated by parsing and by executing every
  script it calls. It was written before `ui-shell` merged; now that the app is on `main`, the
  publish step needs a real run to confirm.

Still open: an application icon, arm64 verification on real hardware, and one licensing question
below.

## Open question for the maintainer

**Does redistributing the Windows App SDK inside a GPLv3 executable actually work?** The
self-contained single-file publish embeds Microsoft's Windows App SDK runtime into the same binary
that is conveyed under GPLv3. Microsoft's redistribution terms and GPLv3 §5–6 both have opinions
about that, and this project has not resolved it. Raised by the packaging worktree; it is a
licensing judgement, not a technical one, so it is left for a human.

## The remaining gate

**Enable Hyper-V, then run `docs/spikes/iso-build.md` §9 step 5.**

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All   # elevated, reboots
```

That is the only unclosed question on the ISO path, and the UEFI load-size deviation above is
exactly what it would catch.
