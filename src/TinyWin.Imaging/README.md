# TinyWin.Imaging

Owner worktree: `imaging-engine`. Implements `IImagingBackend` (see
`src/TinyWin.Core/Abstractions/IImagingBackend.cs`).

**All DISM operations require elevation.** `dism.exe` performs its elevation check before parsing
arguments, so even `/Get-WimInfo` returns error 740 unelevated. Every method here throws
`DismElevationRequiredException` in that case.

## `DismExeBackend` is permanent, not a stopgap

`docs/spikes/dism-backend.md` checked the entire export table of `dismapi.dll`. Neither
`/Cleanup-Image /StartComponentCleanup /ResetBase` (pipeline stage 9) nor `/Export-Image` (stage 11)
exists as any export. A future `ManagedDismBackend` over the `Microsoft.Dism` package can take over
mount, appx, capabilities, features and packages, but **cleanup and export come back here, forever**
— or at least until someone does the separate `wimgapi.dll` work the spike flagged as a v2 option.

So this is not the shabby fallback the plan originally described. Treat `CleanupImageAsync` and
`ExportImageAsync` as first-class members with no alternative implementation.

Do **not** hand-roll P/Invoke for provisioned appx. `Microsoft.Dism` 6.0.0 already wraps the
undocumented exports correctly, and the naive `LayoutKind.Sequential` struct produces a 72-byte
stride where the truth is 68 — a *plausible but wrong* package list rather than a clean crash. See
the spike, §4.

## Layout

| File | Role |
|---|---|
| `DismExeBackend.cs` | The `IImagingBackend` implementation. Orchestration only. |
| `Dism/DismCommandLine.cs` | Pure argument construction. Always emits `/English`. |
| `Dism/DismOutputParser.cs` | Pure parsing of DISM's `Key : Value` blocks. |
| `Dism/DismOutputReader.cs` | Raw character stream → lines + progress percentages. |
| `Dism/DismStageProgress.cs` | Progress fallback when DISM reports no percentages. |
| `Dism/DismExitCode.cs` | Exit code → `DismErrorKind` → an actionable message. |
| `ImageInventory.cs` | Per-image enumeration cache. The `NoTarget` machinery. |
| `Execution/IProcessRunner.cs` | The seam. `ChildProcessRunner` is the only untestable part. |

## Why it is shaped this way

**Command-line construction and output parsing are pure functions behind `IProcessRunner`.** That is
the only structure that can be tested on an unelevated machine, and this whole project was written
on one. `tests/TinyWin.Imaging.Tests` covers both halves against captured DISM output; see
`Samples/README.md` for what is real capture and what is authored.

**`/English` is on every command, with no code path that omits it.** Without it DISM localises both
the keys and the status prose, and the parser silently returns nothing on a German or Japanese host
— reporting every action as a no-op. That is the failure mode CLAUDE.md's "report no-ops" rule
exists to catch, arriving in a form the rule cannot catch.

**Absence is established by enumeration, not by error code.** Removal methods list the image first
and return `ActionStatus.NoTarget` when the target is not there, rather than firing a removal and
interpreting whatever comes back. Error codes vary by component type and DISM version; an
enumeration does not. Codes are still mapped as a backstop. The enumerations are cached per mounted
image, so this costs four extra invocations per image rather than one per action.

This path is exercised heavily and correctly: roughly 13 of the packages tiny11builder removes no
longer exist on 26100/26200 at all.

**Catalog names resolve to DISM names.** The catalog says `Microsoft.BingNews`; DISM demands
`Microsoft.BingNews_4.55.62231.0_neutral_~_8wekyb3d8bbwe`. Capability and package identities embed
versions that move between builds. The backend resolves the short form against what DISM listed, so
the catalog carries no version strings that would rot on every servicing update.

**Progress handles all three encodings, and the absence of any.** DISM renders its bar by rewriting
one line, and the spike could not determine whether that survives stdout redirection as backspaces,
bare carriage returns, or not at all. `DismOutputReader` decodes all three; when none arrive,
`SawPercentage` stays false and `DismStageProgress` falls back to bounded stage-transition movement
so a silent 40-minute `/ResetBase` does not look identical to a hang. `scripts/Verify-DismBackend.ps1`
records which encoding a real DISM actually uses.

**Cancellation kills the child process tree** — `dism.exe` spawns `DismHost.exe`, and killing only
the parent leaves the mount held — and leaves the image mounted so the caller can unwind with
`UnmountAsync(commit: false)`. `UnmountAsync` is the deliberate exception: it observes no
cancellation at all, because an interrupted commit is the corrupt image everything else here works
to prevent, and because the unwind path *is* a call to `UnmountAsync`.

## Verifying against real media

`dotnet test` covers the pure halves. The rest needs elevation and a Windows ISO:

```powershell
# From an ELEVATED PowerShell, at the repo root
powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1

# ...and, for real mount/remove/unmount against an exported scratch WIM (~10 GB, tens of minutes)
powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1 -Full
```

It captures real output for every enumeration command, probes the progress encoding on a real
mount, and runs `ElevatedBackendTests` against the live backend. Those tests skip rather than fail
when unelevated, so CI stays green.

## Known gaps

- `GetEditionsAsync` costs one `dism.exe` invocation per index. The WIM's own XML header carries the
  same data and can be read unelevated in milliseconds — a worthwhile optimisation for the Source
  page, but it is not DISM and so does not belong in this class.
- The `DismExitCode` table only contains codes there is evidence for. Unknown codes report their raw
  value in hex and decimal rather than being guessed at; the verification script is how the table
  grows.
