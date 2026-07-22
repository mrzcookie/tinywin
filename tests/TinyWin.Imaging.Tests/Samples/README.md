# DISM output samples

Fixtures for `DismOutputParserTests`. **Provenance matters here** — a parser tested only against
output invented by the person who wrote the parser proves nothing. Each file is labelled below.

| File | Provenance |
|---|---|
| `error-740.txt` | **Captured.** Verbatim stdout of `dism.exe /English /Get-WimInfo /WimFile:D:\sources\install.wim` run unelevated on the dev machine. |
| `get-wiminfo-list.txt` | **Derived from real data.** Layout is DISM's; every index, name, description and byte count is read out of the XML header of the real Windows 11 25H2 `D:\sources\install.wim` (11 editions, build 26200.8037). |
| `get-wiminfo-index1.txt`, `get-wiminfo-index6.txt` | **Derived from real data.** Same source — architecture, version, ServicePack build, edition id, file/directory counts and language are the image's real values. |
| `get-mountedwiminfo.txt` | **Authored.** DISM's documented block layout, plausible TinyWin paths. |
| `get-provisionedappxpackages.txt` | **Authored.** Real Windows 11 package family names; versions are representative, not measured. |
| `get-capabilities.txt` | **Authored.** Real 24H2/25H2 capability identities. |
| `get-features.txt` | **Authored.** Real optional-feature names, covering all four states including `Disabled with Payload Removed`. |
| `get-packages.txt` | **Authored.** Real package identity shape, covering `Installed` / `Superseded` / `Staged`. |

The authored files exist because **no DISM command runs unelevated** — `dism.exe` performs its
elevation check before parsing arguments, so even `/Get-WimInfo` returns 740 (that refusal is what
`error-740.txt` is). They encode the documented `Key : Value` block format, which is the thing the
parser actually depends on.

## Replacing the authored files with captured ones

`scripts/Verify-DismBackend.ps1` writes real output for every one of these commands to
`Samples/captured/`. From an elevated prompt:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1 -Wim D:\sources\install.wim
```

Then diff `captured/` against this directory and replace anything that differs. A difference is a
finding, not a nuisance — it means the parser was tested against a shape DISM does not produce.
