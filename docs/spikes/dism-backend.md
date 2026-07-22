# Spike — DISM backend selection (PLAN §3.2)

**Status:** complete, with one class of result unverified (see §7).
**Date:** 2026-07-22 · **Branch:** `spike-dism-backend` · **Timebox:** 2 days, used well under.

## Environment

| | |
|---|---|
| OS | Windows 11 25H2, build 26200 (in the 26100/26200 servicing branch TinyWin targets) |
| `dismapi.dll` | 10.0.26100.8457, x64, image base `0x180000000` |
| .NET SDK | 10.0.302 |
| `Microsoft.Dism` | **6.0.0** (MIT) — ships a native `net10.0` target, plus net8.0/net472/netstandard2.0 |
| Tooling | `dumpbin.exe` 14.51.36248 (VS 18 Community) |
| **Elevation** | **Not available in this session.** See §7. |

Reproduce with `docs/spikes/harness/` — see §8.

---

## 1. Recommendation

**Confirm the plan: ship `DismExeBackend` first.** The plan called it "the guaranteed-shippable
floor." The evidence upgrades it beyond that:

> `dism.exe` is not merely a fallback. It is **permanently mandatory**, because two stages of
> the §2.2 pipeline have no native DISM API at all.

Neither `/Cleanup-Image /StartComponentCleanup /ResetBase` (stage 9) nor `/Export-Image`
(stage 11) exists as a supported export in `dismapi.dll` (§3). Whatever else TinyWin does, it
will be shelling out to `dism.exe` for those two stages in v1. So the process-invocation
plumbing — argument building, output parsing, exit-code mapping, cancellation — has to exist
and be well-tested regardless. Building it first is right.

**But challenge one premise.** PLAN §3.2 says:

> The documented DISM C API does not cover provisioned appx enumeration/removal ... three
> options ... `PInvokeAppxBackend` — direct P/Invoke to the undocumented exports for appx.

The three-way split is now a two-way split. `Microsoft.Dism` 6.0.0 **already wraps the
undocumented appx exports** (§2), correctly (§4). `PInvokeAppxBackend` should be **struck from
the plan** — there is no reason to hand-roll marshalling that a maintained MIT package already
does, and the hand-rolled version carries a silent-corruption failure mode (§4) that the
package has already navigated.

Revised backend set behind `IImagingBackend`:

| Backend | Role | Covers |
|---|---|---|
| `DismExeBackend` | **Ships first. Never removed.** | Everything, including cleanup + export |
| `ManagedDismBackend` | Optimization, added in M2 | Everything *except* cleanup + export, which delegate to `DismExeBackend` |

`ManagedDismBackend` is therefore not a standalone backend — it is a partial override that
composes over the exe backend. Model that explicitly rather than pretending both implement the
same interface fully; a `ManagedDismBackend` that throws `NotSupportedException` on
`CleanupImageAsync` is a trap for the Core stage engine.

**Sequencing.** M1's vertical slice (mount → remove 3 appx → unmount → rebuild ISO) should use
`DismExeBackend` end to end. Introduce `ManagedDismBackend` in M2 once the pipeline is proven,
behind the runtime-selectable settings toggle the plan already calls for.

---

## 2. Does `Microsoft.Dism` expose provisioned Appx? — **Yes**

Reflected over the real assembly (`harness/DismSpike` mode `dump`):

```
DismAppxPackageCollection GetProvisionedAppxPackages(DismSession session)
void                      RemoveProvisionedAppxPackage(DismSession session, string packageName)
void                      AddProvisionedAppxPackage(...)   // 3 overloads
```

`DismAppxPackage` exposes `PackageName`, `DisplayName`, `PublisherId`, `Architecture`
(`DismProcessorArchitecture`), `ResourceId`, `InstallLocation`, `Version` (`System.Version`).

The package's internal struct carries a `Regions` field that is **not** surfaced as a public
property. Irrelevant for TinyWin — removal keys off `PackageName`.

### Coverage of the surface the spike was asked to check

| Capability | `Microsoft.Dism` | Notes |
|---|---|---|
| Mount | ✅ `MountImage` ×14 overloads, `MountImageAsync` ×8 | index or name, `readOnly`, `DismMountImageOptions` |
| Unmount | ✅ `UnmountImage` / `UnmountImageAsync` | `commitChanges` bool — this is the `/discard` path for cancellation unwinding |
| Commit | ✅ `CommitImage` / `CommitImageAsync` | `discardChanges` bool |
| Features | ✅ `GetFeatures`, `GetFeatureInfo`, `EnableFeature`, `DisableFeature` (+ByPackageName/Path) | `DisableFeature` takes `removePayload` |
| Capabilities | ✅ `GetCapabilities`, `GetCapabilityInfo`, `AddCapability`, `RemoveCapability` | |
| Packages | ✅ `GetPackages`, `GetPackageInfoByName/Path`, `AddPackage`, `RemovePackageByName/Path` | |
| **Provisioned Appx** | ✅ **`GetProvisionedAppxPackages`, `RemoveProvisionedAppxPackage`** | the headline finding |
| **Cleanup-Image / StartComponentCleanup / ResetBase** | ❌ **absent** | only `CheckImageHealth` / `RestoreImageHealth`. Not the same thing. |
| **Export-Image** | ❌ **absent** | only `ApplyFfuImage` / `SplitFfuImage`, which are FFU, not WIM export |
| Progress callbacks | ⚠️ **partial** | see below |

Two bonus finds relevant to other worktrees:

- `GetMountedImages()` and `CleanupMountpoints()` — exactly what §2.2 stage 1 preflight and
  the §3.3 crash-recovery path need. No need to parse `/Get-MountedImageInfo`.
- `GetRegistryMountPoint(session, DismRegistryHive)` — **flag this to the `TinyWin.Registry`
  worktree.** DISM exposes its own hive mount point; it may be a cleaner route than manual
  `RegLoadKey`, or at minimum a cross-check for §3.3's unload problem.

### Progress callbacks — read this carefully

`DismProgressCallback` / `IProgress<DismProgress>` overloads exist for `MountImage`,
`UnmountImage`, `CommitImage`, `EnableFeature`, `DisableFeature`, `AddCapability`,
`RemoveCapability`, `AddPackage`, `RemovePackageBy*`, `CheckImageHealth`, `RestoreImageHealth`.

**They do not exist for any appx method.** `GetProvisionedAppxPackages`,
`RemoveProvisionedAppxPackage` and `AddProvisionedAppxPackage` have no progress overload and no
`*Async` overload — they are synchronous and silent.

Consequence for the `IImagingBackend` signature in PLAN §3.2:

```csharp
Task RemoveProvisionedAppxAsync(string mountPath, string packageName, IProgress<double> p, CancellationToken ct);
```

The `IProgress<double>` here can only ever report 0 → 1 per package. Real granularity is
"package N of M", which belongs to Core's stage engine, not the backend. Either drop the
parameter from this one method or document that it is coarse. Appx removal is also the longest
part of a debloat run, so a UI that shows nothing between packages will look hung — the fix is
per-package progress emitted by the caller, which the plan's action-list model supports.

Cancellation has the same shape: no `CancellationToken` overload on appx methods, so
cancellation can only be honoured *between* packages. That is acceptable (the plan's
requirement is that cancellation unwinds with `/discard`, which happens at dismount), but it
must be a conscious decision rather than a surprise.

---

## 3. Are the undocumented exports real? — **Yes, and the signatures are now pinned**

### Export table

`dumpbin /exports C:\Windows\System32\dismapi.dll` → 136 exports (full dump in
`harness/dismapi-exports.txt`): 50 unprefixed, 86 underscore-prefixed.

```
   28   1B 0003AAE0 DismGetProvisionedAppxPackages
   42   29 0003AB60 DismRemoveProvisionedAppxPackage
    5    4 0003A8F0 DismAddProvisionedAppxPackage
   92   5B 0003AAE0 _DismGetProvisionedAppxPackages     <- same RVA
  114   71 0003AB60 _DismRemoveProvisionedAppxPackage   <- same RVA
  115   72 0003DB10 _DismRemoveProvisionedAppxPackageAllUsers
```

Both names are aliases onto one implementation. The underscore prefix is the SDK header's
marker for "declared but unsupported", not a different function.

**Binding proven at runtime, unelevated:** `Marshal.Prelink` resolves all seven needed entry
points (`DismInitialize`, `DismOpenSession`, `DismCloseSession`, `DismShutdown`, `DismDelete`,
`DismGetProvisionedAppxPackages`, `DismRemoveProvisionedAppxPackage`). A deliberately bogus
control export fails with `EntryPointNotFoundException`, so the positives mean something.

### Signatures, recovered from disassembly

Not guessed — read off the x64 prologue of each export.

`DismGetProvisionedAppxPackages` @ `0x18003AAE0` takes exactly three arguments and tail-calls
an internal at `0x18004394C`:

```
18003AAFC: mov  ebx,ecx        ; arg1 session, used as 32-bit  -> DismSession is UINT
18003AAF9: mov  rsi,rdx        ; arg2 pointer
18003AAF6: mov  rdi,r8         ; arg3 pointer
18003AB1E: call 18004394C
```

and inside that internal, the results are written out as a qword and a dword:

```
180043C7B: mov  rax,qword ptr [rdi+48h]   ; the array
180043C7F: mov  qword ptr [r15],rax       ; -> *arg2
180043C82: mov  eax,dword ptr [rdi+50h]   ; the count
180043C85: mov  dword ptr [r14],eax       ; -> *arg3   (32-bit!)
180043D43: mov  esi,8007000Eh             ; E_OUTOFMEMORY on allocation failure
```

`DismRemoveProvisionedAppxPackage` @ `0x18003AB60` takes exactly two, and wraps a four-argument
internal, hardcoding `flags = 0` and supplying a scratch out-DWORD:

```
18003AB85: mov  edi,ecx                   ; arg1 session (32-bit)
18003AB82: mov  rbx,rdx                   ; arg2 PCWSTR packageName
18003ABAD: and  dword ptr [rsp+50h],0
18003ABB2: lea  r9,[rsp+50h]              ; internal arg4 = &scratch
18003ABB7: xor  r8d,r8d                   ; internal arg3 = 0
18003ABBF: call 180044B74
```

So:

```c
HRESULT DismGetProvisionedAppxPackages(DismSession session, DismAppxPackage** ppPackages, UINT* pCount);
HRESULT DismRemoveProvisionedAppxPackage(DismSession session, PCWSTR packageName);
typedef UINT DismSession;
```

Corroborated independently: the `dismapi.h` mirror at
[Chuyu-Team/DISMSDK](https://github.com/Chuyu-Team/DISMSDK/blob/main/dismapi.h) declares both
functions (underscore-prefixed) and `typedef UINT DismSession`.

### What is *not* there

Checked the whole export table for the two missing pipeline stages:

- **No `StartComponentCleanup` / `ResetBase` of any spelling.** The nearest is `_DismCleanImage`
  (ordinal 68, underscore-prefixed, signature unknown, uncorroborated). `DismCheckImageHealth`
  and `DismRestoreImageHealth` are `/ScanHealth` and `/RestoreHealth` — different operations
  that do not compact WinSxS.
- **No WIM `Export-Image`.** `_DismExportOsImage` (ordinal 71) is the FFU/OS-image family
  alongside `_DismApplyFfuImage` / `_DismCaptureOsImage`, not `/Export-Image /Compress:recovery`.

This is the load-bearing finding behind §1.

**Alternative worth noting for later:** `C:\Windows\System32\wimgapi.dll` exports
`WIMExportImage`, `WIMCaptureImage`, `WIMSetTemporaryPath` and `WIMRegisterMessageCallback`
(progress). So stage 11 *could* eventually go native via WIMGAPI rather than DISM. That is a
separate API family with its own marshalling work — a v2 optimization, explicitly out of scope
here, but it means "export must be `dism.exe` forever" is not strictly true.

---

## 4. Struct layout, and the trap in it

This was the most valuable result of the spike, and it inverted mid-investigation.

`Microsoft.Dism` declares every one of its 14 native structs with `Pack = 4`. On x64 that is
**not** the natural layout. Emitting a field-identical clone with default packing and diffing
offsets (`harness` mode `layout`) shows 8 of 14 structs move:

```
MISMATCH DismAppxPackage_      declared 68, natural 72
    ResourceId       declared +0x2C  natural +0x30
    InstallLocation  declared +0x34  natural +0x38
    Regions          declared +0x3C  natural +0x40
MISMATCH DismImageInfo_        declared 140, natural 144   (17 fields shift)
MISMATCH DismCapability_       declared 12,  natural 16
MISMATCH DismFeature_          declared 12,  natural 16
MISMATCH DismFeatureInfo_      declared 44,  natural 56
MISMATCH DismCapabilityInfo_   declared 36,  natural 40
MISMATCH DismDriver_           declared 52,  natural 56
MISMATCH DismMountedImageInfo_ declared 28,  natural 32
```

My first read was that this is a latent x64 bug in the package. **It is not**, and the reason
matters. `dismapi.h` wraps its struct block in `#pragma pack(push, 1)`. Every field in these
structs is either 4 or 8 bytes wide, so cumulative offsets are always multiples of 4 — which
makes `pack(1)` and `pack(4)` produce **byte-identical** layouts. `Microsoft.Dism` is correct.
What it is *not* is natural alignment, and that is the trap.

The header's own definition matches the package field-for-field:

```c
typedef struct _DismAppxPackage {
    PCWSTR PackageName; PCWSTR DisplayName; PCWSTR PublisherId;
    UINT MajorVersion; UINT MinorVersion; UINT Build; UINT RevisionNumber; UINT Architecture;
    PCWSTR ResourceId; PCWSTR InstallLocation; _Maybenull_ PCWSTR Region;
} DismAppxPackage;
```

(Header calls the last field `Region`; the package calls it `Regions`. Cosmetic.)

**The trap, stated plainly.** Five consecutive `UINT`s sit between two runs of pointers. Under
natural x64 alignment the compiler inserts 4 bytes of padding after `Architecture`; under the
real `pack(1)` it does not. A hand-rolled P/Invoke written the obvious way — `[StructLayout(
LayoutKind.Sequential)]`, no `Pack` — gets a **68 vs 72 byte stride** and a 4-byte shift on the
last three pointers. It would not crash cleanly. Record 0's first eight fields would read
correctly, `ResourceId` onward would be garbage pointers, and every subsequent record would
drift further. Against a *mounted offline image* that means a plausible-looking package list
that is subtly wrong — precisely the failure mode CLAUDE.md's "report no-ops" rule exists to
catch, arriving in a form it cannot catch.

**This is the strongest argument against `PInvokeAppxBackend`.** The bug it invites is silent,
data-dependent, and would most likely surface as "TinyWin removed the wrong package."

Reference implementation with the correct attribute is in
`harness/DismSpike/NativeAppx.cs` — it compiles and its entry points bind, but see §7.

---

## 5. Benchmark — **not measured**

Requested: `dism.exe` process invocation vs native API for an enumeration operation.

**I could not run this.** `dism.exe` performs its elevation check before doing any work and
refuses *every* servicing command unelevated, including read-only ones:

```
> dism /English /Get-ImageInfo /ImageFile:C:\nonexistent.wim
Error: 740
Elevated permissions are required to run DISM.
```

The only number I can honestly report is the process-spawn floor on that refusal path —
**14–15 ms** over 7 runs. That is `CreateProcess` plus the elevation check and nothing else. It
excludes the provider-store load and the `DismHost.exe` child, which dominate real DISM
invocations. **Do not quote it as the cost of a DISM command.**

The native side is equally unmeasured: `DismInitialize` returns `0x800702E4`
(`ERROR_ELEVATION_REQUIRED`) before a session can be opened.

### What the argument actually rests on (inference, not measurement)

The recommendation in §1 does **not** depend on these numbers, which is why the spike still
concludes. It rests on §3's capability gap. The performance argument is secondary and runs:
`dism.exe` pays full process + provider-store initialization **per invocation**, whereas the
API pays it once per `DismSession` and amortizes across every operation on that session. A
balanced-preset run is on the order of hundreds of actions across ~40 components, so the
amortization is the entire case for `ManagedDismBackend`. Whether that is worth 30 seconds or
10 minutes on a real run is **unknown until someone runs §8.** Treat it as an M2 optimization
to be justified by measurement at that time, not as a settled fact.

### `dism.exe` output parseability — **inference, unverified**

`/Get-ProvisionedAppxPackages` emits repeating `Key : Value` blocks (`DisplayName`, `Version`,
`Architecture`, `ResourceId`, `PackageName`, `Regions`) separated by blank lines. Two practical
notes that hold regardless:

- **Always pass `/English`.** Without it DISM localizes both keys and status text, and any
  parser keyed on English strings silently returns nothing on a German or Japanese host — a
  no-op bug of exactly the class CLAUDE.md forbids.
- **Parse exit codes, not prose.** `0` success, `3010` reboot required, `740` elevation.

For progress, DISM renders `[==========            20.0%                          ]` by
rewriting one line. The open question — which I could not answer — is whether, when stdout is a
**pipe** rather than a console, it uses `\b` backspaces, bare `\r`, or suppresses the bar
entirely. This decides whether `DismExeBackend` can report real percentages or only stage
transitions, and it is the single most useful thing to learn from §8. The harness measures it
directly by counting `0x08` / `0x0D` / `0x0A` bytes in a redirected capture.

---

## 6. Corrections to PLAN.md §3.2

Recommended edits, in priority order:

1. **Delete `PInvokeAppxBackend`.** `Microsoft.Dism` 6.0.0 covers appx correctly. §4 is the
   justification.
2. **Amend the opening claim.** "The documented DISM C API does not cover provisioned appx
   enumeration/removal" is true of the *documented* API but misleading as written — the
   exports exist, are aliased unprefixed, and are already wrapped by the MIT package the plan
   intends to use.
3. **Add the capability gap.** State that stages 9 (`StartComponentCleanup`/`ResetBase`) and 11
   (`Export-Image`) have no DISM API and are `dism.exe`-only. This is new information and it is
   what makes the decision rule correct.
4. **Revise the `IImagingBackend` sketch** so cleanup and export are first-class members, and
   note that appx removal has no native progress or cancellation granularity (§2).
5. **Note for `TinyWin.Registry` (§3.3):** `DismGetRegistryMountPoint` exists. Worth a look
   before hand-rolling `RegLoadKey`.

---

## 7. What is proven vs what is not

Stated explicitly, per the spike's terms.

### Proven — executed on this machine

- `Microsoft.Dism` 6.0.0 exposes `GetProvisionedAppxPackages` / `RemoveProvisionedAppxPackage`,
  and has a first-class `net10.0` target. *(reflection over the real assembly)*
- The full managed API surface in §2, including the **absence** of cleanup and export.
  *(reflection)*
- `DismGetProvisionedAppxPackages` and `DismRemoveProvisionedAppxPackage` are real exports.
  *(dumpbin)*
- Those entry points **bind** from .NET 10 x64. *(`Marshal.Prelink`, with a negative control)*
- Their signatures — argument count, 32-bit `DismSession`, qword-out array, dword-out count,
  HRESULT return. *(disassembly of the export prologues and the internal's epilogue)*
- `Microsoft.Dism`'s `Pack = 4` differs from natural x64 alignment on 8 of 14 structs, with
  exact offsets. *(reflection + emitted comparison struct)*
- No cleanup/ResetBase or WIM-export export exists in `dismapi.dll`. *(full export table)*
- `wimgapi.dll` exports `WIMExportImage` + a progress callback. *(dumpbin)*
- `DismInitialize` → `0x800702E4` and `dism.exe` → `Error: 740` when unelevated. *(executed)*
- `dism.exe` spawn floor 14–15 ms on the refusal path. *(executed; near-useless, see §5)*
- The harness compiles clean against .NET 10. *(built)*

### Inference from documentation or binary inspection alone

- **That `Pack = 4` is correct rather than a bug.** Rests on the `dismapi.h`
  `#pragma pack(push, 1)` mirror plus the arithmetic that pack(1) ≡ pack(4) for these fields.
  The mirror is third-party, not Microsoft-published. High confidence, not proof. *The
  offsets it implies are what §8 checks first.*
- The `DismAppxPackage` field list and the `Region`/`Regions` naming. *(same mirror)*
- `_DismCleanImage` being unusable — I only know it is underscore-prefixed with an unknown
  signature. I did not disassemble it. It is possible, though unattractive, that it is a
  viable `StartComponentCleanup`. If someone wants to reopen that, it is the one stone left
  unturned.
- `dism.exe` output shape and progress encoding (§5).
- Everything performance-related (§5).

### Unverified — needs one elevated run

- That `DismGetProvisionedAppxPackages` returns a sane package list against a live image.
- That the runtime record stride is 68 bytes.
- That the managed and raw-P/Invoke paths agree package-for-package.
- Any benchmark number at all.
- The progress-bar encoding under redirection.

**None of these change the §1 recommendation**, which rests on the capability gap in §3. They
change how confidently `ManagedDismBackend` can be scheduled into M2.

---

## 8. Reproducing / finishing this

Everything lives in `docs/spikes/harness/`. It is throwaway spike code, not project code — no
`src/` project was created or touched.

Unelevated (works now):

```powershell
dotnet build docs/spikes/harness/DismSpike -c Release
$exe = "docs/spikes/harness/DismSpike/bin/Release/net10.0/DismSpike.exe"
& $exe dump      # Microsoft.Dism API surface + public types
& $exe layout    # Marshal.Prelink binding test + Pack=4 vs natural offset diff
```

Elevated — **read-only; it enumerates and never calls `RemoveProvisionedAppxPackage`**:

```powershell
# from an elevated PowerShell
powershell -ExecutionPolicy Bypass -File docs/spikes/harness/run-elevated.ps1
```

Writes to `docs/spikes/harness/results/`. Read off, in order:

| File | Answers |
|---|---|
| `03-pinvoke.txt` | "detected record stride" — **must say 68 bytes / MATCH**. If it says 72, §4 is wrong and `Microsoft.Dism` really is broken on x64. |
| `02` vs `03` | managed and raw package lists must be identical |
| `04-bench.txt` | the §5 numbers that are currently missing |
| `08-progress-encoding.txt` | backspace count > 0 ⇒ the progress bar is `\b`-driven and parseable |

The P/Invoke probe deliberately does not trust the struct: it walks the returned buffer as
qwords, classifies each slot as string-pointer / int / zero via `VirtualQuery`, auto-detects
the repeating stride, and only then cross-checks against the header-derived layout. If the
header mirror is wrong, that dump will show it rather than hide it.

## Sources

- [Chuyu-Team/DISMSDK — `dismapi.h`](https://github.com/Chuyu-Team/DISMSDK/blob/main/dismapi.h) — struct packing, `DismAppxPackage`, `DismSession`
- [jeffkl/ManagedDism](https://github.com/jeffkl/ManagedDism) — the `Microsoft.Dism` package
- [DismPackage structure](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism/dismpackage-structure?view=windows-11) — documented struct family
- [DISM app package (.appx) servicing options](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-app-package--appx-or-appxbundle--servicing-command-line-options) — `dism.exe` appx surface
