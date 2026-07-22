# Spike: xorriso as TinyWin's primary ISO builder

**Question (PLAN.md §3.1):** can `xorrisofs` replace the non-redistributable ADK
`oscdimg.exe` as TinyWin's primary ISO builder?

**Verdict: GO — conditional.** Adopt xorriso as primary, keep ADK `oscdimg` as the
runtime-selectable fallback (PLAN.md §3.1 path (b)). The condition is a single
outstanding gate: **an actual boot test on real Windows 11 media**, which could not be
run in this environment. §8 is a ready-to-run checklist for that gate.

The spike also **falsifies one of the premises in PLAN.md §3.1** and that changes the
design. See §2.

- **Date:** 2026-07-22
- **Branch:** `spike-iso-build` (throwaway worktree, no `src/` touched)
- **xorriso tested:** GNU xorriso 1.5.6, MSYS2 build, x86_64

---

## 1. Environment and its limits

Everything below was established on this machine. What could not be established is
listed in §9 rather than glossed over.

| | |
|---|---|
| Windows ADK / `oscdimg.exe` | **Not installed.** Confirms PLAN.md §3.1. Not installed by this spike. |
| Windows 11 ISO | **None present, and none downloaded** — project non-negotiable. All media-dependent work is deferred to §8. |
| Elevation | **Session is not elevated.** Hyper-V VM creation and DISM both require it, so no boot test and no real-media staging. |
| WSL | Not installed. |
| C/C++ toolchain | None (`gcc`, `cl`, MSYS2, Docker all absent). A from-source build of xorriso was therefore **not** performed — see §3. |
| Package managers | `winget` and `choco` present; **neither ships an xorriso package** (both searched, no match). |

`Mount-DiskImage` turned out to work **without** elevation, which is what made the
filesystem-level verification in §5–§6 possible.

---

## 2. The headline finding: xorriso cannot write UDF — and does not need to

PLAN.md §3.1 and the spike brief both assume UDF output is required, "because
install.wim exceeds 4GB and ISO9660 cannot hold it". **Both halves of that need
correcting.**

### 2a. xorriso has no UDF support whatsoever

Not a missing flag — the filesystem is not implemented in libisofs.

```
$ xorrisofs -udf -o out.iso src/
xorriso : FAILURE : -as genisofs: Unsupported option '-udf'
xorriso : NOTE : -return_with SORRY 32 triggered by problem severity FAILURE
(exit 32)
```

Corroborating evidence, all from the shipped artifacts rather than third-party docs:

- `xorrisofs -help` — zero occurrences of `udf`, case-insensitive.
- `xorriso -help` — zero occurrences. It offers `-joliet`, `-rockridge`, `-hfsplus`; no UDF.
- The four man pages and five info files in the package mention `udf` **nowhere**.
- GNU's own project page states plainly that xorriso "does not produce UDF filesystems".

This is structural. No build option, version bump, or flag recovers it. **If UDF were
genuinely required, this spike would be a NO-GO.**

### 2b. But ISO 9660 *can* hold a >4 GiB file, and Windows reads it correctly

ISO 9660 **level 3** permits multi-extent files, lifting the 4 GiB single-extent ceiling.
That is a real capability of the format, not a workaround, and xorriso implements it.

Verified with a 4.7 GiB (5,046,586,572-byte) placeholder `sources/install.wim` carrying
ASCII markers at 0, 2 GiB, and on both sides of the 4 GiB boundary:

```
# control — level 1 correctly refuses
$ xorrisofs -iso-level 1 -o lvl1.iso src/
xorriso : FAILURE : File exceeds size limit of 4294967295 bytes: '.../sources/install.wim'

# level 3 — succeeds
$ xorrisofs -iso-level 3 ... -o big.iso src/
ISO image produced: 2465054 sectors        (14.3 s, 5,048,430,592 bytes)
```

xorriso reads its own output back at full length:

```
$ xorriso -indev big.iso -lsl /sources
-rw-r--r--  1 ...  5046586572 Jul 22 02:14 'install.wim'
```

**And so does Windows.** Mounted via `Mount-DiskImage` — i.e. read by Windows' own
`cdfs.sys`, not by xorriso:

```
reported length: 5046586572
  @4294967232 -> MARKER@4294967232      <- last 64 bytes below 4 GiB
  @4294967296 -> MARKER@4294967296      <- first 64 bytes above 4 GiB
  @5046586508 -> MARKER@5046586508      <- tail
sha256 read from ISO : AFABD66AB3BC9409F78BA2F00715975D72893D1215E32E59D5A42FDF033A22A7
sha256 of source file: AFABD66AB3BC9409F78BA2F00715975D72893D1215E32E59D5A42FDF033A22A7
```

Byte-for-byte identical across the boundary. **Windows' CDFS driver handles multi-extent
ISO 9660 correctly.**

Why this is strong evidence for the real pipeline: `install.wim` is read by `setup.exe`
running under WinPE, and WinPE uses the same `cdfs.sys` family that was exercised above.
`bootmgr`'s own pre-Windows ISO 9660 reader only has to reach `\sources\boot.wim`, which
is ~500 MB — comfortably single-extent, so multi-extent is never on the boot-critical
path. It is not proof (§9), but the residual risk is narrow and well-identified.

### 2c. Consequence for PLAN.md

PLAN.md §3.1 should be amended: the requirement is **ISO 9660 level 3 + Joliet**, not UDF.
The resulting media deviates from Microsoft's own (which is UDF-only, `oscdimg -u2
-udfver102`) in filesystem type while remaining readable by Windows. That deviation is
the main thing the §8 boot test exists to validate.

---

## 3. Acquiring the binary — vendor vs. build

### What was done

No prebuilt xorriso exists in `winget` or `choco`, and no mingw-native package exists in
MSYS2 (only the `msys` variant). Rather than take a third-party repack
(`dEajL3kA/xorriso-win32`, `PeyTy/xorriso-exe-for-windows` — both are redistributions of
MSYS2 output, the latter stuck on 1.5.2), the binary was pulled **directly from the
official MSYS2 repository**, which publishes detached signatures alongside each package:

```
https://repo.msys2.org/msys/x86_64/xorriso-1.5.6-1-x86_64.pkg.tar.zst        (+ .sig)
```

Extracted with Windows' bundled `bsdtar` (libarchive 3.8.4 has libzstd), plus its runtime
dependency chain. The minimal set that runs standalone is **8 files, 6.58 MB**:

| File | KB |
|---|---:|
| `xorriso.exe` | 1585 |
| `msys-2.0.dll` | 3288 |
| `msys-iconv-2.dll` | 1078 |
| `msys-ncursesw6.dll` | 345 |
| `msys-readline8.dll` | 281 |
| `msys-z.dll` | 90 |
| `msys-bz2-1.dll` | 66 |
| `msys-charset-1.dll` | 9 |

Verified running from a directory containing nothing else:

```
xorriso version   :  1.5.6
Version timestamp :  2023.06.07.180001
libisofs   in use :  1.5.6      libjte in use : 2.0.0
libburn    in use :  1.5.6      libisoburn    : 1.5.6
libburn OS adapter:  internal X/Open adapter sg-dummy
License GPLv3+: GNU GPL version 3 or later
```

`sg-dummy` means no optical-drive support was compiled in. Irrelevant to TinyWin — we
only ever write image files.

### The two real costs of the MSYS2 build

**(a) POSIX-only path arguments.** xorriso prepends its own working directory to any path
not starting with `/`, so a drive-letter path is treated as *relative*:

```
$ xorrisofs -o "C:/out/x.iso" "C:/scratch/tree"
xorriso : FAILURE : Cannot determine attributes of source file
  '/cygdrive/c/Users/Zachary/orca/workspaces/tinywin/spike-iso-build/C:/scratch/tree'

$ xorrisofs -o "/cygdrive/c/out/x.iso" "/cygdrive/c/scratch/tree"
(exit 0)
```

Both `C:\...` and `C:/...` fail. `TinyWin.IsoBuilder` must convert every path argument to
`/cygdrive/<drive>/...` with forward slashes. This is a small, testable helper — but it is
a genuine integration tax and an easy source of bugs, so it belongs in a golden test.

**(b) A wider GPL corresponding-source obligation than expected.** Shipping this bundle
means conveying, alongside xorriso (GPLv3+): `msys-2.0.dll` (LGPLv3, a Cygwin fork),
readline (GPLv3+), ncurses, iconv, zlib and bzip2. Every copyleft component needs its
corresponding source made available *from the same place we publish the binary*
(GPLv3 §6d). MSYS2 publishes PKGBUILDs and source packages, but the obligation is ours,
so those artifacts must be mirrored into our own release assets.

### Recommendation

**Vendor now, build later — and treat them as two different milestones.**

1. **M1/M2 (now): vendor the official MSYS2 binary.** It works today, it is signed at
   source, and it unblocks the vertical slice immediately. Ship the 8-file set above plus
   a `THIRD-PARTY.md` and mirrored source tarballs.

2. **M5 (packaging): switch to a native mingw-w64 build produced by our own CI.** This is
   the better answer for the GPLv3 source offer *and* it removes cost (a):

   - **Provenance.** GPLv3 §1 requires "the scripts used to control compilation and
     installation". A CI job whose inputs are a pinned upstream tarball
     (`xorriso-1.5.6.tar.gz`, SHA-256 `d4b6b66bd04c49c6b358ee66475d806d6f6d7486e801106a47d331df1f2f8feb`,
     from `ftp.gnu.org`, GPG-signed) and a checked-in build script *is* the corresponding
     source. Nothing has to be reconstructed after the fact.
   - **A smaller obligation.** Upstream `configure` exposes `--disable-libreadline`,
     `--disable-libbz2`, `--disable-libacl`, `--disable-libcdio` and `--enable-static
     --disable-shared` (all confirmed present in the 1.5.6 `configure`). Dropping readline
     and iconv removes the LGPL/GPL runtime deps entirely.
   - **Native paths.** A mingw build links against MSVCRT, not the MSYS runtime, so
     drive-letter paths work and helper (a) disappears.
   - **Upstream supports the host.** `configure.ac` explicitly branches on
     `cygwin*|mingw*` for the system adapter, so `mingw` is a recognised build target,
     not an unsupported hack.
   - **Currency.** MSYS2 ships 1.5.6 (timestamped 2023-06-07); upstream is already at
     **1.5.8**. Building ourselves decouples us from that lag.

   **Honest caveat: the mingw build was not performed or verified in this spike** — no
   toolchain is installed here and installing one was out of scope. Treat "native
   mingw-w64 xorriso builds and passes the §8 checklist" as an unproven assumption and a
   tracked M5 task, not a settled fact. If it turns out not to build cleanly, staying on
   the vendored MSYS2 binary is a perfectly serviceable fallback — it is what §5–§7
   actually validated.

---

## 4. Capability matrix — evidence from the binary, not the manual

| Requirement | Verdict | Evidence |
|---|---|---|
| **UDF output** (`-udf`) | ❌ **Unsupported** | `FAILURE : -as genisofs: Unsupported option '-udf'`, exit 32. Absent from `-help`, man pages, info files. |
| **>4 GiB member file** | ✅ Supported | Level 1 refuses at 4294967295 bytes; `-iso-level 3` writes a 4.7 GiB file, read back byte-identical by both xorriso and Windows CDFS. §2b. |
| **Dual El Torito catalog** | ✅ Supported | §5 — BIOS + UEFI entries in one catalog, verified by `-report_el_torito`. |
| **BIOS entry** (`-b`, `-no-emul-boot`) | ✅ Supported | Catalog entry 1: `Pltf BIOS`, `Emul none`. |
| **UEFI entry** (`-eltorito-alt-boot -e`) | ✅ Supported | Catalog entry 2: `Pltf UEFI`, `Emul none`. Platform ID 0xEF set automatically by `-e`. |
| **Filename fidelity** | ⚠️ **Only with `-J -joliet-long`** | §6 — the obvious flag choices silently corrupt names. |
| **`-isohybrid-*`** | ⚠️ Present but needs an external file | §7. |
| **GPT/ESP marking** | ✅ Supported, self-contained | §7 — `-append_partition` + `-appended_part_as_gpt`. |
| **Reproduce a source ISO's boot setup** | ✅ Supported | §7 — `-report_el_torito as_mkisofs`. |

---

## 5. Synthetic test: dual El Torito boot catalog

**Placeholder boot files prove structure, not bootability.** Nothing in §5–§7 says the
media boots; it says the on-disc structures are shaped correctly. Bootability is §8.

Synthetic tree mirroring Windows media layout:

| Path | Size | Content |
|---|---:|---|
| `boot/etfsboot.com` | 2,048 | `PLACEHOLDER-BIOS` + `55 AA` at 510 |
| `efi/microsoft/boot/efisys.bin` | 1,458,176 | `PLACEHOLDER-UEFI` + `55 AA` at 510 |
| `efi/boot/bootx64.efi`, `setup.exe`, `sources/boot.wim`, `autounattend.xml` | small | text stubs |

Built with:

```
xorrisofs -iso-level 3 -full-iso9660-filenames -volid TINYWIN_SYNTH \
  -b boot/etfsboot.com -no-emul-boot -boot-load-size 8 \
  -eltorito-alt-boot -e efi/microsoft/boot/efisys.bin -no-emul-boot \
  -o synth.iso src/
```

Inspected with `xorriso -indev synth.iso -report_el_torito plain`:

```
El Torito catalog  : 33  1
El Torito cat path : /boot.catalog
El Torito images   :   N  Pltf  B   Emul  Ld_seg  Hdpt  Ldsiz         LBA
El Torito boot img :   1  BIOS  y   none  0x0000  0x00      8          34
El Torito boot img :   2  UEFI  y   none  0x0000  0x00   2848          35
El Torito img path :   1  /boot/etfsboot.com
El Torito img path :   2  /efi/microsoft/boot/efisys.bin
```

Two entries, one catalog, correct platform IDs, both no-emulation. **This is exactly the
structure `oscdimg -bootdata:2#p0,e,b...#pEF,e,b...` produces.**

Note entry 2's `Ldsiz 2848` = 2848 × 512 = 1,458,176 — xorriso derived the full image
size itself. Entry 1 shows `Ldsiz 8` because we *told* it 8, even though the placeholder
is only 4 sectors long. **xorriso accepted a load size larger than the file without
complaint** — so do not guess this value on real media; read it from the source ISO (§7).

The same structure was re-verified on the 4.7 GiB `big.iso` and on the Joliet build
`bigj.iso`: identical catalog, so large-file support and dual-boot structure compose.

---

## 6. Filename fidelity — the trap that would have shipped broken media

Windows ISO 9660 media with no UDF tree exposes whatever name tree exists. Four option
sets were built from one tree and each was **mounted in Windows** to see what Windows
actually resolves:

| Source name (41 chars) | Windows sees |
|---|---|
| — with `-full-iso9660-filenames` | `SOURCES\API_MS_WIN_CORE_PROCESSTHRE.DLL` |
| — with `-untranslated-filenames` | `sources\api-ms-win-core-processthreads-l1.dll` |
| — with `-iso-level 4 -untranslated-filenames` | `sources\api-ms-win-core-processthreads-l1.dll` |
| — with **`-J -joliet-long`** | `sources\api-ms-win-core-processthreads-l1-1-2.dll` ✅ |

`api-ms-win-core-processthreads-l1-1-2.dll` is the real name.

- `-full-iso9660-filenames` uppercases, maps `-` → `_`, truncates at 31. **Catastrophic.**
- `-untranslated-filenames` preserves case and punctuation but still **silently truncates
  at 37 characters**. This is the dangerous one: it looks correct on casual inspection and
  quietly corrupts the long names. `-iso-level 4` does not help.
- **`-J -joliet-long` is the only option set that reproduced every name exactly.**

Windows prefers the Joliet tree, so Joliet is not optional for TinyWin — it is what
replaces the naming role UDF plays on Microsoft's own media.

**Residual limit:** `-joliet-long` caps basenames at 103 characters (64 without it).
`TinyWin.IsoBuilder` should preflight the staged tree and **fail loudly** on any basename
over 103 chars rather than emit a silently truncated ISO. This is exactly the
"report no-ops, never silently swallow" rule from CLAUDE.md applied to the ISO stage.

### Joliet composes with >4 GiB

The production combination was built and mounted:

```
sources/install.wim   reported length: 5046586572
sha256: AFABD66AB3BC9409F78BA2F00715975D72893D1215E32E59D5A42FDF033A22A7   (matches source)
```

Correct lowercase names *and* a byte-perfect 4.7 GiB file in the same image.

### boot.catalog visibility

xorriso materialises `/boot.catalog` in the tree; Microsoft media has no such file.
`-hide boot.catalog -hide-joliet boot.catalog` removes it from both trees while leaving
the El Torito catalog fully functional (verified: root listing clean, catalog still
reported at LBA 40 with both entries). Recommended, for parity.

---

## 7. isohybrid, GPT, and reproducing the source ISO's boot setup

**`-isohybrid-gpt-basdat` alone is a no-op.** It was passed to the §5 build; the resulting
image has **zero non-zero bytes in the first 512** and no `55 AA` signature, and
`-report_system_area` says `No System Area was loaded`. It only takes effect together
with `-isohybrid-mbr FILE`, which requires a SYSLINUX `isohdpfx.bin` — a separate
GPLv2+ file we would have to vendor.

**The self-contained alternative works.** `-append_partition 2 0xef <efisys.bin>
-appended_part_as_gpt` produces a protective MBR + GPT with a genuine EFI System
Partition, with no external files:

```
Boot record  : El Torito , MBR protective-msdos-label cyl-align-off GPT
MBR partition      :   1   0x00  0xee            1         6511
GPT type GUID      :   2  28732ac11ff8d211ba4b00a0c93ec93b   -> C12A7328-F81F-11D2-BA4B-00A0C93EC93B (ESP)
GPT partname local :   2  Appended2
```

**Recommendation: don't ship it by default.** `oscdimg` produces no system area at all, so
plain output is the parity-preserving choice, and Rufus rewrites the layout when writing
USB media anyway. Keep the appended-GPT form as an opt-in "dd-able image" setting.

### `-report_el_torito as_mkisofs` — use this instead of hardcoding boot flags

xorriso can emit the exact option list that reproduces an existing ISO's boot setup:

```
$ xorriso -indev bigj.iso -report_el_torito as_mkisofs
-V 'TINYWIN_BIGJ'
-c '/boot.catalog'
-b '/boot/etfsboot.com'
-no-emul-boot
-boot-load-size 8
-eltorito-alt-boot
-e '/efi/microsoft/boot/efisys.bin'
-no-emul-boot
-boot-load-size 2848
```

**`TinyWin.IsoBuilder` should run this against the user's source ISO during Inspect (§2.2
stage 2) and reuse the reported values** — volume id, catalog path, both boot image paths,
and both load sizes. That eliminates the guessing flagged in §5, and automatically adapts
to media whose layout differs from the 24H2/25H2 norm. Cache it in `state.json`.

---

## 8. Proposed command line, and how it maps to tiny11's oscdimg call

tiny11builder (PLAN.md §2.2 step 13) runs:

```
oscdimg.exe -m -o -u2 -udfver102 \
  -bootdata:2#p0,e,b"<scratch>\boot\etfsboot.com"#pEF,e,b"<scratch>\efi\microsoft\boot\efisys.bin" \
  "<scratch>" "<out>\tiny11.iso"
```

Proposed replacement:

```
xorrisofs.exe \
  -iso-level 3 \
  -J -joliet-long \
  -volid "<VOLID_FROM_SOURCE_ISO>" \
  -hide boot.catalog -hide-joliet boot.catalog \
  -b boot/etfsboot.com \
  -no-emul-boot \
  -boot-load-size <FROM_SOURCE_ISO> \
  -eltorito-alt-boot \
  -e efi/microsoft/boot/efisys.bin \
  -no-emul-boot \
  -boot-load-size <FROM_SOURCE_ISO> \
  -o /cygdrive/c/<out>/tinywin.iso \
     /cygdrive/c/<scratch>
```

Argument by argument:

| oscdimg | xorrisofs | What it does / why |
|---|---|---|
| `-u2 -udfver102` | `-iso-level 3` | **The substitution that makes this whole thing work.** oscdimg uses UDF 1.02 to escape the 4 GiB single-extent ceiling; xorriso cannot write UDF, so we use ISO 9660 level 3 multi-extent instead. Verified equivalent for our purpose in §2b. |
| *(UDF supplies real names)* | `-J -joliet-long` | With UDF gone, Joliet is what carries exact filenames to Windows. Non-optional — §6. |
| `-m` (ignore size limit) | *(not needed)* | xorriso has no default image-size cap. |
| `-o` (dedupe identical files) | *(no equivalent)* | Content-dedup is not implemented. Costs some output size; no correctness impact. |
| *(oscdimg takes volid from `-l`)* | `-volid` | Set from the source ISO. Windows Setup does not require a specific id, but keeping it stable avoids surprising the user's scripts. |
| *(no visible catalog)* | `-hide boot.catalog -hide-joliet boot.catalog` | Parity: keeps the stray `/boot.catalog` out of both name trees. §6. |
| `#p0,e,b<etfsboot.com>` | `-b boot/etfsboot.com -no-emul-boot` | `p0` = platform 0 (x86 BIOS) — xorriso's default for `-b`. `e` = no emulation = `-no-emul-boot`. `b` = the boot image. |
| *(oscdimg derives it)* | `-boot-load-size <n>` | Sectors the firmware loads. **Read from the source ISO, do not hardcode** — xorriso silently accepts a wrong value (§5). |
| `#pEF,e,b<efisys.bin>` | `-eltorito-alt-boot -e efi/microsoft/boot/efisys.bin -no-emul-boot` | `-eltorito-alt-boot` opens the second catalog entry; `-e` sets the EFI image and marks platform 0xEF (`pEF`) automatically — confirmed in §5's catalog dump. |
| `"<scratch>" "<out>\x.iso"` | `-o <out> <scratch>` | Note the **reversed order** and that **both paths must be `/cygdrive/...`** (§3). |

Deliberately **not** included: `-isohybrid-*` (no-op without a SYSLINUX file, and not
oscdimg parity — §7), `-boot-info-table` (a SYSLINUX patching feature; it *rewrites bytes
inside the boot image* and must never be applied to Microsoft's `etfsboot.com`), and
`-r`/`-rock` (Rock Ridge is ignored by Windows; untested here, so left out).

Per PLAN.md §5, this belongs in a golden test comparing the generated command line
against checked-in expected output.

---

## 9. Validation checklist — run once real media is available

**Prerequisites:** a Windows 11 24H2 (26100) or 25H2 (26200) x64 ISO the user already
owns; **an elevated shell**; Hyper-V enabled; ~30 GB free. Replace `<SRC>` with the source
ISO path and `<W>` with a scratch directory. `$X` is the vendored `xorriso` folder.
Remember every xorriso path argument must be `/cygdrive/c/...`.

### Step 1 — Record the source ISO's boot geometry (do this first)

```powershell
& "$X\xorriso.exe" -indev /cygdrive/c/<SRC> -report_el_torito plain
& "$X\xorriso.exe" -indev /cygdrive/c/<SRC> -report_el_torito as_mkisofs
& "$X\xorriso.exe" -indev /cygdrive/c/<SRC> -report_system_area plain
```

Capture the volume id, both boot image paths, and **both `-boot-load-size` values**. These
feed §8. Expect the source to report UDF-based media; xorriso reads the ISO 9660 tree.

### Step 2 — Stage and confirm the large-file case is actually exercised

```powershell
Mount-DiskImage -ImagePath <SRC>
robocopy <mounted>:\ <W>\iso /E /COPY:DAT /R:1 /W:1
Dismount-DiskImage -ImagePath <SRC>
(Get-Item <W>\iso\sources\install.wim).Length     # >4294967295 makes this the real test
(Get-ChildItem <W>\iso -Recurse -File | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum   # must be <= 103
```

If `install.wim` is under 4 GiB, note it — the multi-extent path is then untested by this
run and should be forced with a deliberately oversized image.

### Step 3 — Build with the §8 command line

Substitute the load sizes from step 1. Then verify structure before booting anything:

```powershell
& "$X\xorriso.exe" -indev /cygdrive/c/<W>/tinywin.iso -report_el_torito plain
& "$X\xorriso.exe" -indev /cygdrive/c/<W>/tinywin.iso -lsl /sources
```

Expect two catalog entries (BIOS + UEFI) and `install.wim` at full length.

### Step 4 — Filesystem-level readback (catches name and size corruption before boot)

```powershell
$img = Mount-DiskImage -ImagePath <W>\tinywin.iso -PassThru
$d = ($img | Get-Volume).DriveLetter
# every source file present, same name, same length?
$src = Get-ChildItem <W>\iso -Recurse -File
$out = Get-ChildItem "${d}:\" -Recurse -File
Compare-Object $src $out -Property Name,Length | Format-Table -AutoSize     # expect EMPTY
(Get-FileHash "${d}:\sources\install.wim" -Algorithm SHA256).Hash
(Get-FileHash "<W>\iso\sources\install.wim" -Algorithm SHA256).Hash          # must match
Dismount-DiskImage -ImagePath <W>\tinywin.iso
```

An empty `Compare-Object` result is the pass condition. **Any output here means a name was
truncated or a file was dropped — stop and fix before boot-testing.**

### Step 5 — Hyper-V boot matrix (the actual gate)

| # | VM | Firmware | Secure Boot | Pass condition |
|---|---|---|---|---|
| 1 | Gen 2 | UEFI | **Off** | Reaches Setup language screen. Proves the `efisys.bin` catalog entry. |
| 2 | Gen 2 | UEFI | **On** (MS UEFI CA) | Reaches language screen. Proves we did not disturb signed boot files. |
| 3 | Gen 1 | BIOS | n/a | Reaches language screen. Proves the `etfsboot.com` entry. |
| 4 | Gen 2 | UEFI | Off | **Run Setup to completion** — the only test that proves `install.wim` is readable end-to-end past 4 GiB. |
| 5 | — | — | — | Rufus to a real USB stick, boot bare metal. Catches firmware quirks Hyper-V will not. |

```powershell
$vhd = "<W>\tw-gen2.vhdx"
New-VM -Name TinyWin-Gen2 -Generation 2 -MemoryStartupBytes 4GB -NewVHDPath $vhd -NewVHDSizeBytes 64GB
Add-VMDvdDrive -VMName TinyWin-Gen2 -Path <W>\tinywin.iso
Set-VMFirmware -VMName TinyWin-Gen2 -EnableSecureBoot Off `
  -FirstBootDevice (Get-VMDvdDrive -VMName TinyWin-Gen2)
Start-VM TinyWin-Gen2
# case 2: Set-VMFirmware -VMName TinyWin-Gen2 -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority

New-VM -Name TinyWin-Gen1 -Generation 1 -MemoryStartupBytes 4GB -NewVHDPath "<W>\tw-gen1.vhdx" -NewVHDSizeBytes 64GB
Set-VMDvdDrive -VMName TinyWin-Gen1 -Path <W>\tinywin.iso
Start-VM TinyWin-Gen1
```

**Control run:** build the same staged tree with ADK `oscdimg` and run the same matrix. If
a case fails for both builders, the fault is in the staged tree (stages 1–12), not in the
ISO builder. Without this control, a stage-6 mistake looks like an xorriso failure.

### Step 6 — Decide

All of 1–4 pass → xorriso confirmed primary, close this spike.
Case 1 or 3 fails → boot catalog problem; re-check load sizes against step 1.
Case 4 alone fails → the multi-extent read hypothesis in §2b is wrong for WinPE; fall back
to `oscdimg`, or split `install.wim` into `install.swm` (`DISM /Split-Image
/FileSize:4000`), which Windows Setup supports natively and which removes the >4 GiB
requirement entirely. **That split is the designed escape hatch if §2b does not hold.**

---

## 10. What is proven, and what is not

**Proven on this machine:**

- xorriso 1.5.6 runs standalone on Windows from an 8-file, 6.58 MB vendored set.
- xorriso cannot write UDF, in any configuration.
- `-iso-level 3` writes a 4.7 GiB member file; Windows' own CDFS driver reads it back
  byte-for-byte across the 4 GiB boundary (SHA-256 match).
- A dual BIOS+UEFI El Torito catalog is produced with correct platform IDs and
  no-emulation flags, and survives alongside a >4 GiB file.
- `-J -joliet-long` is the only tested option set that preserves Windows filenames
  exactly; two plausible alternatives silently truncate at 37 characters.
- `-isohybrid-gpt-basdat` alone produces no system area; `-append_partition` +
  `-appended_part_as_gpt` produces a real ESP-typed GPT.
- `-report_el_torito as_mkisofs` reproduces a source image's boot options.
- Path arguments must be `/cygdrive/...`; drive-letter forms fail.

**Not proven — and why:**

| Unverified | Why | Risk |
|---|---|---|
| **The ISO boots.** No case in §9 step 5 was run. | No Windows 11 media (must not be downloaded); no elevation for Hyper-V. | **This is the gate.** Everything else is structural. |
| Real `etfsboot.com` / `efisys.bin` behave as the placeholders did. | Placeholders prove catalog structure only, never bootability. | Low — the catalog is byte-shaped identically to oscdimg's. |
| WinPE/`bootmgr` read multi-extent ISO 9660 as `cdfs.sys` does. | Boot-time drivers were never exercised. | **Highest technical risk.** Narrowed by §2b: `boot.wim` is single-extent, and `install.wim` is read by `setup.exe` under WinPE's `cdfs.sys`. Escape hatch: SWM split. |
| Correct `-boot-load-size` for real media. | No media to read it from. | Low — §9 step 1 reads it from the source. |
| Windows Setup tolerates ISO 9660+Joliet where it expects UDF. | Needs a real Setup run. | Moderate; §9 step 5 case 4 is the test. |
| Secure Boot unaffected. | Not run. | Low — no signed binary is modified. |
| Native mingw-w64 xorriso builds, is static, and behaves identically. | No toolchain installed; out of scope. | Moderate, but fully de-risked by staying on the vendored MSYS2 binary. |
| Names >103 chars on real media. | No media to scan. | Low, mitigated by the §9 step 2 preflight. |
| arm64 xorriso. | Only x86_64 obtained. MSYS2 has no arm64 msys repo. | Open question for M5 (PLAN.md ships x64 + arm64). x64 xorriso runs under emulation on arm64 Windows, but that is untested. |

---

## 11. Actions for the plan

1. **Amend PLAN.md §3.1.** Replace "UDF, not ISO9660" with "ISO 9660 level 3 multi-extent
   plus Joliet". The stated technical rationale for needing UDF is incorrect.
2. **`TinyWin.IsoBuilder`** (PLAN.md §7 `iso-builder` worktree) should implement:
   a Windows→`/cygdrive/` path converter; boot-geometry capture via
   `-report_el_torito as_mkisofs` at Inspect time; a >103-char basename preflight that
   fails loudly; the §8 command line; and post-build verification via `-report_el_torito`
   + `-lsl`.
3. **Keep `oscdimg` detection** (PLAN.md §3.1 path (b)) as a runtime-selectable fallback,
   and use it as the control build in §9 step 5.
4. **Add the SWM split** (`DISM /Split-Image /FileSize:4000`) as a contingency behind a
   setting — it is the answer if the WinPE multi-extent assumption fails.
5. **M5 packaging:** native mingw-w64 build in CI; `THIRD-PARTY.md`; mirrored
   corresponding source for xorriso *and* every copyleft runtime DLL if the MSYS2 bundle
   ships; resolve the arm64 question.

## Sources

- [GNU xorriso](https://www.gnu.org/software/xorriso/) — states xorriso does not produce UDF
- [MSYS2 xorriso package](https://packages.msys2.org/packages/xorriso?variant=x86_64) — 1.5.6-1, `msys` repo, GPL-3.0-or-later
- [repo.msys2.org](https://repo.msys2.org/msys/x86_64/) — signed upstream packages used here
- [ftp.gnu.org/gnu/xorriso](https://ftp.gnu.org/gnu/xorriso/) — source tarballs (1.5.8 current)
- [dEajL3kA/xorriso-win32](https://github.com/dEajL3kA/xorriso-win32) / [PeyTy/xorriso-exe-for-windows](https://github.com/PeyTy/xorriso-exe-for-windows) — third-party repacks, evaluated and not used
- [Create an ISO image for UEFI platforms](https://learn.microsoft.com/en-us/troubleshoot/windows-server/setup-upgrade-and-drivers/create-iso-image-for-uefi-platforms) — oscdimg `-bootdata` semantics
- [ntdevlabs/tiny11builder](https://github.com/ntdevlabs/tiny11builder) — the oscdimg invocation being replaced
