# Catalog gaps and verification notes

What the catalog could and could not verify against real media, and where the shipped
schema forced a workaround. Written during M3 (docs/PLAN.md §6).

Everything below was checked against **`D:\sources\install.wim`, Windows 11 25H2,
build 26200.8037**, all 11 editions. Component versions inside the image are stamped
`10.0.26100.x`, confirming §1's claim that 25H2 is an enablement package over 24H2 and
that one catalog covers both.

## How verification was done without elevation

`dism` needs admin and fails with error 740 here, so the image was never mounted.
Instead:

| Evidence | Method |
|---|---|
| Edition list, build number | `[1].xml` extracted from the WIM, parsed as UTF-16 |
| Provisioned appx names | `Program Files\WindowsApps\*` directory names per edition |
| Capability ids | `<capabilityIdentity>` in all 3460 `Windows\servicing\Packages\*.mum` manifests |
| Optional feature names | `<update name="...">` in the same manifests |
| Removable package names | `.mum` filenames under `Windows\servicing\Packages` |
| File and directory paths | Full recursive listing of every image |
| Component sizes | Sum of the size column per subtree, per edition |
| Service names, task paths | The dev machine, which is itself build 26200 (25H2) |

7-Zip reads WIM files unelevated, which is what made all of the above possible. The
listing and the extracted manifests are reproducible with:

```sh
7z l D:\sources\install.wim                                  # full recursive listing
7z e -omum D:\sources\install.wim "6\Windows\servicing\Packages\*.mum"
```

Index 6 is Windows 11 Pro; index 1 is Home. Both were used, because they differ.

---

## 1. Things tiny11builder removes that no longer exist on 26100/26200

These are the highest-value findings: every one of them is an action that would have
silently found no target, which docs/PLAN.md §2.1 calls the #1 way this class of tool
rots. **None of them are in the catalog.**

### Provisioned appx that are gone

Absent from all 11 editions of 26200:

| Package | Note |
|---|---|
| `Microsoft.XboxApp` | Xbox Console Companion, retired. Only `Microsoft.GamingApp` remains. |
| `Microsoft.XboxGameOverlay` | Distinct from `Microsoft.XboxGamingOverlay`, which *does* exist. Easy to conflate. |
| `Microsoft.Windows.Copilot` | Never shipped under this name; the app is `Microsoft.Copilot`. |
| `Microsoft.WindowsMaps` | Maps app retired. |
| `Microsoft.People` | Retired; only the `PeopleExperienceHost` system app remains. |
| `microsoft.windowscommunicationsapps` | Mail & Calendar retired, replaced by `Microsoft.OutlookForWindows`. |
| `Microsoft.ZuneVideo` | Movies & TV retired. `Microsoft.ZuneMusic` (now Media Player) survives. |
| `Microsoft.MixedReality.Portal`, `Microsoft.Microsoft3DViewer`, `Microsoft.Print3D` | 3D stack retired. |
| `Microsoft.549981C3F5F10` | Cortana retired. |
| `Microsoft.Getstarted` | Tips retired. |
| `Microsoft.MicrosoftJournal`, `Microsoft.Whiteboard` | Never in-box on this media. |
| `Microsoft.SkypeApp`, `MicrosoftTeams` | The consumer Teams/Chat stub is gone; `MSTeams` is the current package. |

docs/PLAN.md §2.1 proposes catalog entries for Maps, People, Mail & Calendar and
3D Viewer. **Those components cannot be written for this build family** — there is
nothing to remove. They are omitted rather than shipped as guaranteed no-ops.

### Capabilities that are gone

The image declares exactly 75 capability identities. These well-known ones are **not**
among them:

- `Microsoft.Windows.WordPad` — WordPad was removed from Windows in 24H2.
- `Print.Fax.Scan` — Windows Fax and Scan is no longer a capability here.
- `XPS.Viewer` — the *viewer* capability is gone; the `Printing-XPSServices-Features`
  *feature* still exists and is what the catalog targets.
- `App.Support.QuickAssist` — Quick Assist is now the `MicrosoftCorporationII.QuickAssist`
  appx, so the catalog removes it that way.
- `Microsoft.Windows.MSPaint` — Paint is now the `Microsoft.Paint` appx.
- `App.WirelessDisplay.Connect`, `Microsoft.Windows.StorageManagement`,
  `Windows.Client.ShellComponents`, `Hello.Face.Migration.*`.

docs/PLAN.md §2.1 proposes WordPad, Fax/Scan and XPS Viewer components under Optional
Features. WordPad and Fax/Scan are omitted for the same reason as above.

### Packages that are gone

- **`Windows-Defender-Client-Package`** does not exist on 26200. This matters because
  the seed `system.defender` entry used it. The only Defender packages present are
  `Windows-Defender-AM-Default-Definitions-Package`,
  `Windows-Defender-ApplicationGuard-Inbox-Package` and
  `Windows-Defender-Group-Policy-Package`. Defender's payload lives in
  `Program Files\Windows Defender` (47 MB), `Program Files\Windows Defender Advanced
  Threat Protection` (250 MB) and ~583 MB of WinSxS, and is **not removable as a single
  CBS package** on this build family. The catalog now removes the directories, disables
  the services and applies policy instead, which is the only path that actually works.

---

## 2. Verified-present, used by the catalog

Recorded so a future build-family bump has something to diff against.

**Capabilities** (DISM form is `Name~~~~0.0.<major>.<minor>`, or
`Name~~~<lang>~0.0.1.0` for the `Language.*` family):

`App.StepsRecorder` 1.0 · `Browser.InternetExplorer` 11.0 · `Language.Handwriting` 1.0 ·
`Language.OCR` 1.0 · `Language.Speech` 1.0 · `Language.TextToSpeech` 1.0 ·
`MathRecognizer` 1.0 · `Media.WindowsMediaPlayer` 12.0 · `Microsoft.Windows.PowerShell.ISE` 1.0 ·
`OpenSSH.Client` 1.0 · `Print.Management.Console` 1.0 · `Edge.Webview2.Platform` ·
`Windows.WinOcr` · `Windows.TerminalServices.AppServerClient` · `Windows.Telnet.Client` ·
`Windows.TFTP.Client` · `Windows.SimpleTCP.Content` · `Windows.SmbDirect` ·
`Windows.WorkFolders.Client` · `NetFX3` · `WMIC` · `VBSCRIPT`

**Optional features**, each confirmed declared by a specific manifest:

`WindowsMediaPlayer` · `MediaPlayback` · `Internet-Explorer-Optional-amd64` ·
`LegacyComponents` · `DirectPlay` · `NetFx3` · `NetFx4-AdvSrvs` · `WCF-Services45` ·
`Printing-XPSServices-Features` · `Printing-PrintToPDFServices-Features` ·
`Printing-Foundation-Features` · `MSRDC-Infrastructure` · `MicrosoftWindowsPowerShellV2` ·
`MicrosoftWindowsPowerShellV2Root` · `Microsoft-Hyper-V-All` · `HypervisorPlatform` ·
`VirtualMachinePlatform` · `Microsoft-Windows-Subsystem-Linux` · `WorkFolders-Client` ·
`TelnetClient` · `TFTP` · `SimpleTCP` · `SmbDirect` · `Client-DeviceLockdown` ·
`SearchEngine-Client-Package`

**Packages**: `UserExperience-Recall-Package` and `UserExperience-AIX-Package` both exist,
so Recall and the AI experience host are genuinely removable as packages.

**Edition-dependent appx.** Actions for these carry `optional: true`, because absence is
normal rather than noteworthy:

- Home only (indexes 1–3): `Microsoft.Copilot`, `MicrosoftCorporationII.MicrosoftFamily`
- Pro/Education only (4–11): `Microsoft.MicrosoftOfficeHub`
- Absent from every N edition (2, 5, 7, 9, 11): `Microsoft.ZuneMusic`,
  `Microsoft.WindowsSoundRecorder`, `Microsoft.GamingApp`, `Microsoft.Xbox.TCUI`,
  `Microsoft.XboxGamingOverlay`, `Microsoft.Windows.DevHome`, and the whole codec set

---

## 3. Could not verify

Stated plainly rather than guessed at.

1. **Scheduled task paths.** The offline image ships only nine task definitions under
   `Windows\System32\Tasks`; everything else is materialized during setup from the
   `TaskCache` registration in the SOFTWARE hive. Task paths in the catalog were
   therefore verified against the *running* 26200 dev machine, not the image. Two
   consequences:
   - `\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser` and
     `\ProgramDataUpdater` do **not** exist on 26200 — Microsoft removed them in 24H2.
     They are kept in the catalog with `optional: true` and reduced weight, because a
     pristine image may still register them at OOBE and the cost of trying is nil.
   - **`RemoveScheduledTask` against an offline image almost certainly cannot work by
     deleting a file**, since the file is not there yet. The executor will need to edit
     `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\{Tree,Tasks}`.
     This is an engine concern, not a catalog one, but the catalog's no-op counts will
     look alarming until it is handled. **Flagged for TinyWin.Imaging / TinyWin.Registry.**

2. **Service names** were verified against the running dev machine, which is the same
   build (26200) but a serviced install rather than pristine media. Every service the
   catalog touches was found present. `Fax`, `TabletInputService`, `AJRouter` and
   `ClickToRunSvc` were probed and found absent, so nothing references them.

3. **Registry policy values** could not be verified at all — the SOFTWARE and NTUSER
   hives cannot be loaded without `reg load`, which needs elevation. Every registry
   action is sourced from Microsoft's published Group Policy / CSP documentation and
   from tiny11builder, not from the image. Registry actions are the least-verified part
   of this catalog. A single elevated pass with the image mounted would settle them.

4. **`estimatedSavingsMb` for capability and feature components** is approximate.
   Payloads for those live in WinSxS under abbreviated directory names
   (`amd64_microsoft-windows-i..ttings.resources_...`), so per-feature attribution by
   name is unreliable. App and directory components use exact measured sizes; feature
   and capability components use conservative round estimates.

---

## 4. Schema gaps

Per the M3 brief the C# model was not modified. These are the places where that cost
something. None are blocking.

1. **No provenance field on a component.** There is nowhere to record *how* an entry was
   verified or when, so this file has to carry it out-of-band. A
   `verifiedAgainst: "26200.8037"` string would let the validator warn when the catalog
   claims a build range it was never checked against — which is precisely the rot
   `appliesTo` exists to prevent, only one level up. Worked around by this document.

2. **`estimatedSavingsMb` has no defined compression semantics.** The catalog stores the
   uncompressed on-disk footprint, because that is what is measurable and reproducible.
   The UI readout in §4 is an *ISO* size estimate, and install.wim is LZX-compressed at
   roughly 3–4:1. Consuming the field directly will overstate the saving by that factor.
   The compression factor belongs in Core or the UI, not in per-component data, so
   nothing was changed here — but `system.winsxs` claims ~10.7 GB and a naive subtraction
   will produce a negative ISO size. **Flagged for TinyWin.Core / TinyWin.App.**

3. **`optional` is per-action, not per-package.** `RemoveProvisionedAppx` takes a
   `packages` array with one shared `optional` flag, so a component mixing
   always-present and edition-specific packages must either split into two actions or
   mark the whole set optional and lose no-op signal on the packages that should always
   be there. The catalog splits into two actions where it matters — see `apps.xbox` and
   `media.codecs` — which is a workaround, not a fix.

4. **No way to express "N editions only" or "Home only"** beyond `optional: true`.
   `appliesTo` covers build numbers, not SKUs. Edition-specific components therefore
   report a legitimate no-op as an ordinary optional miss.
