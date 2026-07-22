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

**Capabilities the catalog removes.** All declare an explicit version, so the DISM form
`Name~~~~0.0.<major>.<minor>` — or `Name~~~en-US~0.0.1.0` for the `Language.*` family —
is derivable:

`App.StepsRecorder` 1.0 · `Browser.InternetExplorer` 11.0 · `Language.Handwriting` 1.0 ·
`Language.OCR` 1.0 · `Language.Speech` 1.0 · `Language.TextToSpeech` 1.0 ·
`MathRecognizer` 1.0 · `Media.WindowsMediaPlayer` 12.0 ·
`Microsoft.Windows.PowerShell.ISE` 1.0 · `OpenSSH.Client` 1.0 ·
`Print.Management.Console` 1.0

**Capabilities present but not removed by the catalog**, either because they carry no
version (see §4.4) or because nothing should remove them:

`Edge.Webview2.Platform` · `Windows.WinOcr` · `Windows.TerminalServices.AppServerClient` ·
`Windows.Telnet.Client` · `Windows.TFTP.Client` · `Windows.SimpleTCP.Content` ·
`Windows.SmbDirect` · `Windows.WorkFolders.Client` · `NetFX3` · `WMIC` · `VBSCRIPT` ·
`Client.WOW64` · `Language.Basic` · `Language.UI.Client` · `Hello.Face.20134` ·
`DirectX.Configuration.Database` · `Microsoft.Windows.Notepad.System` ·
`OneCoreUAP.OneSync` · `Windows.Kernel.LA57` · the 22 `Microsoft.Windows.Wifi.Client.*`
and `Microsoft.Windows.Ethernet.Client.*` driver capabilities

Note that `Windows.Telnet.Client`, `Windows.TFTP.Client`, `Windows.SimpleTCP.Content`,
`Windows.SmbDirect` and `Windows.WorkFolders.Client` exist as *both* a capability and an
optional feature. `features.legacynet` targets the feature, which is the form that
actually toggles them.

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

1. **Scheduled task paths.** ~~Verified against the running dev machine, not the image.~~
   **Now verified against the image** — `docs/reference/09-taskcache-tree.txt` is an elevated
   dump of the real `TaskCache\Tree`. **18 of the catalog's 20 task paths are confirmed
   present.** The two that are not are `\Microsoft\Windows\RetailDemo\CleanupOfflineContent`
   (no `RetailDemo` subtree exists at all) and
   `\Microsoft\Windows\Application Experience\PcaPatchDbTask` (no `Pca*` entry exists); both
   were already `optional: true`, so they report `NoTarget` quietly, which is correct.

   The original text and its two consequences are kept below for the reasoning, with the first
   corrected:
   - ~~`\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser` and
     `\ProgramDataUpdater` do **not** exist on 26200 — Microsoft removed them in 24H2.~~
     **Corrected 2026-07-22.** This was inferred from the *running dev machine*, and it was
     wrong. The elevated capture in `docs/reference/09-taskcache-tree.txt` shows both
     registered in the real offline image's `TaskCache\Tree`, along with a third the catalog
     had missed: `Microsoft Compatibility Appraiser Exp`. All three are now targeted and
     `optional` has been cleared, so a future disappearance is reported rather than shrugged off.

     The general lesson is worth keeping: a *serviced install* is not the same artefact as
     *pristine media*, and inferring image contents from the host is unsound in both directions.
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

4. **Three capabilities have no derivable DISM name.** `Edge.Webview2.Platform`,
   `Windows.WinOcr` and `Windows.TerminalServices.AppServerClient` are declared in the
   manifests **without a `version` attribute**, unlike the other 72. DISM identifies a
   capability as `Name~PublicKeyToken~Arch~Language~Version`, and with no version to read
   there is no way to construct that string without running `/Get-Capabilities` against a
   mounted image. Rather than guess a version and ship an action that silently matches
   nothing, **the three `RemoveCapability` actions were dropped**:
   - `apps.edge.webview2` still deletes both WebView2 directories, which is the removal
     that actually matters.
   - `language.ocr` still removes `Language.OCR~~~en-US~0.0.1.0`, which is verified.
   - `features.remotedesktop` still stops `TermService` and sets `fDenyTSConnections`.

   One elevated `dism /Get-Capabilities` run would resolve all three.

5. **Language components are en-US only.** The `Language.*` capabilities on this media are
   declared `language="en-us"` and the catalog hard-codes `~~~en-US~`. On media in another
   language every entry in `language.json` no-ops. The schema has no way to say "whatever
   language this image is", and the resolver has no access to the inspected image language
   at catalog-load time, so this is left as a known limitation rather than worked around.
   **Flagged for TinyWin.Catalog / TinyWin.Core** — the plan resolver already takes a
   target build, and taking a target language alongside it would fix this.

6. **`RemovePackage` names are version-less prefixes.** Entries use
   `UserExperience-Recall-Package~31bf3856ad364e35~amd64~~` with no trailing version,
   following the seed catalog's convention. Package versions differ by patch level — this
   media carries *two* installed versions of `UserExperience-Recall-Package`, at
   `10.0.26100.1591` and `10.0.26100.8036` — so pinning one would break on any other
   media. **The executor must prefix-match and remove every matching version**, not expect
   an exact identity. Flagged for TinyWin.Imaging.

7. **Offline SYSTEM hive uses `ControlSet001`, not `CurrentControlSet`.**
   `system.deviceencryption` and `features.remotedesktop` write to
   `ControlSet001\Control\...` because `CurrentControlSet` is a runtime symlink that does
   not exist in an offline hive. This is correct for every image observed, but an image
   whose last boot selected a different control set would need `ControlSet002`. Not
   expressible in the schema; the value is read from `Select\Current` at hive-load time,
   which is a TinyWin.Registry concern.

8. **`estimatedSavingsMb` for capability and feature components** is approximate.
   Payloads for those live in WinSxS under abbreviated directory names
   (`amd64_microsoft-windows-i..ttings.resources_...`), so per-feature attribution by
   name is unreliable. App and directory components use exact measured sizes; feature
   and capability components use conservative round estimates.

---

## 4. Schema gaps and unverifiable identities

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
