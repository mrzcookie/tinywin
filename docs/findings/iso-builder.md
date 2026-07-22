# TinyWin.IsoBuilder — implementation findings

Implements `IIsoBuilder` (`src/TinyWin.Core/Abstractions/IIsoBuilder.cs`, unchanged) over the
vendored xorriso as primary and ADK `oscdimg` as runtime-selectable fallback, per
`docs/spikes/iso-build.md` §11 item 2 and `docs/PLAN.md` §3.1.

Everything below was established against **real Windows 11 25H2 (26200) media**
(`CCCOMA_X64FRE_EN-US_DV9`, 8,471,603,200 bytes, `install.wim` 7,578,075,168 bytes) in an
**unelevated** session. The spike could not do this — it had no media — so three of its
conclusions needed correcting and one needed extending.

---

## 0. End-to-end result on real media

Extract → preflight → build → verify, run against the 25H2 ISO:

```
ExtractAsync   976 files, 94 directories, 8,465,957,864 bytes            24 s
IsoPreflight   canBuild=True, requiresMultiExtent=True
               largest = sources\install.wim (7,578,075,168)
BuildAsync     8,467,810,304-byte ISO                                    28 s
VerifyAsync    976 files in the image, 976 in the staged tree, passed=True
```

Then read back through Windows' own CDFS driver by mounting the output — the pass condition in
`docs/spikes/iso-build.md` §9 step 4:

```
label 'CCCOMA_X64FRE_EN'  fs CDFS  size 8467810304
source files=976  image files=976
Compare-Object -Property Name,Length : EMPTY          <- pass condition
sha256 install.wim from image  : 93EE38F5...BD9809D0
sha256 install.wim from source : 93EE38F5...BD9809D0  <- match
```

**A 7.06 GB `install.wim` written as ISO 9660 level 3 multi-extent reads back byte-identical
through Windows' own reader.** The spike proved this with a 4.7 GiB synthetic file; it now holds
for the real one. Every filename survived Joliet intact across 976 files.

This closes §9 steps 1–4. Step 5, the Hyper-V boot matrix, still needs elevation and remains open.

---

## 1. What real media changed

### 1.1 `-report_el_torito as_mkisofs` returns nothing usable on Microsoft media

The spike recommends reading boot geometry with `as_mkisofs` and reusing the reported values.
On genuine Microsoft media that command emits **no boot options at all**:

```
xorriso : SORRY : Cannot enable EL Torito boot image #1 because it is not a data file in the ISO filesystem
xorriso : SORRY : Cannot enable EL Torito boot image #2 because it is not a data file in the ISO filesystem
-V 'CCCOMA_X64FRE_EN-US_DV9'
--modification-date='2026030800000000'
--boot-catalog-hide
-eltorito-alt-boot
```

Microsoft's boot images live outside the ISO 9660 tree, so xorriso cannot name them and refuses to
describe them. `-report_el_torito plain` still reports both catalog entries:

```
El Torito boot img :   1  BIOS  y   none  0x0000  0x00      8         534
El Torito boot img :   2  UEFI  y   none  0x0000  0x00      1         536
```

**Consequence:** `plain` is authoritative for geometry; `as_mkisofs` contributes image paths only on
media that has them (our own rebuilt output does). `ElToritoReportParser` parses both and
`IsoBuilderService.ReadBootGeometryAsync` merges them, falling back to the well-known Microsoft
paths and recording that assumption in `IsoInspection.Notes`. Both fixtures are checked in under
`tests/TinyWin.IsoBuilder.Tests/Fixtures/`.

### 1.2 The real UEFI load size is 1, and xorriso will not reproduce it

Real 25H2 media declares **`-boot-load-size 8` for BIOS and `1` for UEFI** — one 512-byte sector for
a 1.4 MB `efisys.bin`. UEFI firmware reads the FAT image itself, so the field is nominal there.

xorriso **ignores `-boot-load-size` for the `-e` (UEFI) entry** and always writes the full image
size. Four option orderings were tried against a tree holding the real `efisys.bin`; all four
produced `Ldsiz 2880` (= 1,474,560 / 512) when asked for 1:

| Variant | Requested | Written |
|---|---:|---:|
| `-e … -no-emul-boot -boot-load-size 1` | 1 | 2880 |
| `-boot-load-size 1 -e … -no-emul-boot` | 1 | 2880 |
| `-e … -boot-load-size 1 -no-emul-boot` | 1 | 2880 |
| `-eltorito-platform 0xEF -b … -boot-load-size 1` | 1 | 2880 |

The BIOS entry (`-b`) does honour the value — a request for 4 against the real 4096-byte
`etfsboot.com` wrote 4. So the spike's "xorriso silently accepts a wrong load size" warning holds
for BIOS and is the reason that value is read from the source and verified after the build.

**Consequence:** the UEFI load size cannot be reproduced with xorriso. `IsoVerification` treats a
BIOS mismatch as an **error** and the UEFI difference as a **reported warning** — not silence, per
CLAUDE.md. Full-size is what every Linux ISO ships, so the risk is judged low, but it is a genuine
deviation from Microsoft's media and belongs in the boot-test gate below.

### 1.3 xorriso cannot read a Windows 11 ISO at all — extraction had to change

The spike assumed extraction was in scope for xorriso. It is not:

```
$ xorriso -indev Win11_25H2_English_x64_v2.iso -lsl /
total 1
-r-xr-xr-x    1 0        0             135 Mar  7 18:00 'README.TXT'
```

Microsoft ships a **decoy ISO 9660 tree containing one file**. Everything real is in the UDF tree,
which libisofs does not implement. `xorriso -osirrox` would extract `README.TXT` and nothing else.

**Consequence:** `ExtractAsync` mounts the image through Windows' own UDF reader and copies the
tree. The mount is `virtdisk.dll` `OpenVirtualDisk` + `AttachVirtualDisk` with
`ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY`, which **works unelevated** (verified). The attach is not made
permanent, so the image detaches when the handle closes even on a hard process exit — cancellation
cannot strand a mounted image.

Drive-letter resolution matches on `IOCTL_STORAGE_GET_DEVICE_NUMBER`, not on volume label. That is
not defensive over-engineering: the development machine had two mounts of the same ISO presenting
identical labels at `D:` and `F:` simultaneously.

The copier also clears the read-only attribute on every file. Optical media is read-only and a
plain copy carries the attribute across, which would fail the later DISM and registry stages far
from the cause.

### 1.4 Windows truncates the volume label to 16 characters

Microsoft's own label is 23 characters. Joliet's volume descriptor holds 16, so a rebuilt image
mounts as `CCCOMA_X64FRE_EN`. Verified by mounting the output.

Refusing to build would make stock media unbuildable, so `IsoPreflight` reports this as a
**warning** carrying the exact truncated value, while an over-long *basename* remains a hard
failure.

---

## 2. What was built

| Piece | File | Notes |
|---|---|---|
| Windows → `/cygdrive` converter | `CygwinPath.cs` | Golden table in `Golden/cygwin-paths.txt`. Rejects relative and drive-relative paths outright — xorriso would resolve them against its own cwd. |
| Boot geometry capture | `ElToritoReport.cs`, `IsoBuilderService.ReadBootGeometryAsync` | Parses both report flavours. Never hardcodes load sizes. |
| Joliet preflight | `IsoPreflight.cs` | Fails loudly over 103 characters; also reports file counts, total bytes and whether the tree needs multi-extent. |
| The §8 command line | `XorrisoCommandLine.cs` | Pure function; golden test against `Golden/xorriso-build.args`. Tests assert `-boot-info-table`, `-isohybrid-*`, `-udf`, `-full-iso9660-filenames` and `-untranslated-filenames` are all absent. |
| Post-build verification | `IsoVerification.cs`, `IsoContentListing.cs` | `-report_el_torito plain` for the catalog plus `-find / -type f -exec lsdl` for a file-for-file size comparison against the staged tree. |
| Backend probing | `BackendLocator.cs` | Vendored xorriso, then ADK `oscdimg` across `amd64`/`arm64`/`x86`. Every unavailable backend states a reason. |
| Extraction | `IsoImageMount.cs`, `TreeCopier.cs`, `Interop/NativeMethods.cs` | See §1.3. |
| SWM split escape hatch | `WimSplitter.cs` | `dism /Split-Image /FileSize:4000`, deletes `install.wim` afterwards, reports the no-op cases. |
| oscdimg fallback | `OscdimgCommandLine.cs` | tiny11builder's invocation; golden test. |

Two deviations from the brief worth flagging:

- **`-find / -type f -exec lsdl` replaced a plain `-lsl`.** Same formatter, whole tree, so the
  comparison covers every file rather than just `/sources`. `ListDirectoryArguments` still exposes
  `-lsl` for targeted checks.
- **Every invocation leads with `-return_with FAILURE 32`.** Without it, reading a perfectly good
  25H2 ISO exits 32 because of the two SORRY events in §1.1.

### 2.1 `BuildAsync` and null `BootGeometry`

`BootGeometry` is honoured when set. When null, the build falls back to `IsoDefaults` — the values
read off real 25H2 media (BIOS 8, UEFI 1, standard paths) — and emits a diagnostic naming the
fallback and pointing at `ReadBootGeometryAsync`. The warning is not decoration: a wrong BIOS load
size is accepted silently by xorriso and produces media that fails only at boot, so an assumed value
must never pass unmentioned.

The oscdimg backend needs no geometry at all; it derives load sizes from the boot images.

### 2.2 A gap in the Core contract

`IIsoBuilder` has no member for Inspect-time geometry capture, and `TinyWin.IsoBuilder` cannot add
one (dependency direction is `IsoBuilder → Core`). `ReadBootGeometryAsync` and `VerifyAsync`
therefore live on the concrete `IsoBuilderService`, so the composition root must register the
concrete type or Core must grow the member. **Recommend Core add:**

```csharp
Task<IsoBootGeometry> ReadBootGeometryAsync(string isoPath, CancellationToken cancellationToken = default);
```

Not done here — `IIsoBuilder.cs` belongs to the Core worktree.

`IsoBuilderService` also raises a `Diagnostic` event for every non-fatal observation, because
`BuildAsync` returns `Task` with nowhere to put a report. Core's build report should subscribe.

---

## 3. Vendoring: fetched by script, not committed

**Decision: `tools/fetch-xorriso.ps1` downloads the bundle; `tools/xorriso/` stays gitignored.**

The script pins all seven MSYS2 packages by version *and* SHA-256, verifies each download, extracts
with Windows' bundled `bsdtar`, and copies out exactly the 8 files the spike identified — reproduced
here at **6.58 MB**, matching the spike exactly. It writes `PACKAGES.txt` recording every package,
hash, source package and license, and `-WithSource` fetches the matching source packages.

Reasoning, given the GPLv3 corresponding-source obligation:

- GPLv3 §6 requires corresponding source to be conveyed **from the same place as the binary**. The
  place TinyWin conveys binaries is the GitHub Release, not the git tree. A release job running
  `fetch-xorriso.ps1 -WithSource` and attaching both discharges §6d precisely; committing the binary
  alone would not discharge anything while putting 6.6 MB in every clone forever.
- Committing the binary *and* its corresponding source would mean committing the source of
  msys2-runtime, libiconv, ncurses, readline, zlib and bzip2 — far more than 6.6 MB, permanently.
- Pinned hashes give the reproducibility that committing the blob would otherwise be buying.

Cost: a fresh clone must run the script before the ISO stage works. `ProbeBackendsAsync` says so by
name rather than failing mysteriously, and the integration tests skip rather than fail.

The copyleft components requiring mirrored source are xorriso (GPL-3.0-or-later), msys2-runtime
(LGPL-3.0-or-later), libiconv (LGPL-2.1-or-later) and readline (GPL-3.0-or-later). M5 still owns
`THIRD-PARTY.md` and the native mingw-w64 build.

---

## 4. Still not proven

The spike's gate is unchanged and **not closed by this work**:

| Unverified | Why |
|---|---|
| **The output ISO boots.** | Hyper-V needs elevation; this session had none. `docs/spikes/iso-build.md` §9 step 5 is still the gate. |
| WinPE reads multi-extent ISO 9660 past 4 GiB. | Boot-time only — §0 exercised `cdfs.sys`, not `bootmgr`. Narrower than before: it now holds for the real 7.06 GB `install.wim`, not just a synthetic one. Escape hatch implemented: `SplitOversizeImage`. |
| A UEFI entry with the full-size load count boots on Microsoft media. | New, from §1.2. Covered by the same Gen 2 boot cases. |
| `dism /Split-Image` end to end. | Needs elevation. The command line and no-op reporting are implemented and unit-tested; the invocation is not. |
| The oscdimg fallback against a real ADK. | No ADK installed. Command line is golden-tested only. |
| arm64. | Open M5 question, unchanged. |

Content verification is skipped for oscdimg output and says so: it writes UDF, and xorriso — which
performs the read-back — cannot read UDF.
