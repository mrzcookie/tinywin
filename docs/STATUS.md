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
| **M4** | UI | In flight (`ui-shell`). |
| **M5** | Portable packaging | In flight (`packaging`). |
| **M6** | Hardening | In flight (`hardening`). |

`dotnet build` clean · **490 tests passing** · catalog validator clean.

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

- **Stage order deviates from the plan.** `BuildPipelineFactory` runs `StageFilesStage` before
  `InspectIsoStage`, so unsupported media is rejected only *after* a ~7 GB copy. §2.2 specifies
  inspect-then-stage. Cause: `InspectIsoStage` was written to read the staged tree rather than the
  source ISO. Assigned to the `hardening` worktree, which owns Core.
- **`PcaPatchDbTask` and `RetailDemo\CleanupOfflineContent`** are in the catalog but absent from
  real 26200 media. Both are `optional: true`, so they report `NoTarget` quietly — correct, but
  they are dead weight and could be dropped.

## The remaining gate

**Enable Hyper-V, then run `docs/spikes/iso-build.md` §9 step 5.**

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All   # elevated, reboots
```

That is the only unclosed question on the ISO path, and the UEFI load-size deviation above is
exactly what it would catch.
