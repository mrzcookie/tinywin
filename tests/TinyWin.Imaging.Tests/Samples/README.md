# DISM output samples

Fixtures for `DismOutputParserTests` and `DismExeBackendTests`. **Provenance matters here** — a
parser tested only against output invented by the person who wrote the parser proves nothing.

Every sample below except `get-mountedwiminfo.txt` is now **real captured output**, taken elevated
from real Windows 11 25H2 media (build 26200.8037, DISM 10.0.26100.8737) by
`scripts/capture-reference.ps1` and imported from `docs/reference/`.

| File | Provenance | Source |
|---|---|---|
| `get-wiminfo-list.txt` | **Captured** | `docs/reference/01-wiminfo.txt` |
| `get-provisionedappxpackages.txt` | **Captured** — 48 packages | `docs/reference/03-provisioned-appx.txt` |
| `get-capabilities.txt` | **Captured** — 425 capabilities, 46 installed | `docs/reference/04-capabilities.txt` |
| `get-features.txt` | **Captured** — 98 features across all three states | `docs/reference/05-features.txt` |
| `get-packages.txt` | **Captured** — 144 packages, 78 installed / 66 staged | `docs/reference/06-packages.txt` |
| `mount-progress.txt` | **Captured** — a real `/Mount-Wim`, all 98 progress repaints | `docs/reference/02-progress-encoding.txt` |
| `error-740.txt` | **Captured** — verbatim stdout of an unelevated `/Get-WimInfo` | this dev machine |
| `get-wiminfo-index1.txt`, `get-wiminfo-index6.txt` | **Derived from real data** — DISM's layout, but every value read from the real `install.wim` XML header | `D:\sources\install.wim` |
| `get-mountedwiminfo.txt` | **Authored** — the only remaining one | DISM's documented block layout |

The captures were UTF-16LE (an artefact of PowerShell's `>` redirection, not of DISM) and are stored
here as UTF-8.

## Two things the real captures corrected

Worth recording, because both were plausible guesses that were simply wrong:

1. **`Browser.InternetExplorer~~~~0.0.11.0` is `Installed` on 26200**, not `Not Present`. The
   authored sample had it the other way round, which would have made a real removal look like a
   no-op in tests.
2. **`/Get-Packages` lists the same package base name twice** — `Staged` at one version and
   `Installed` at another, staged first. Resolving a short catalog name by taking the first prefix
   match therefore picked the wrong identity. `DismExeBackend.TryResolveIdentity` now ranks
   installed states above staged ones. The same hazard applies to `Language.Basic~~~<locale>`, where
   ~100 identities are listed and exactly one is installed.

Neither would have been caught without real output.

## `get-mountedwiminfo.txt` is still authored

`/Get-MountedWimInfo` only prints anything when an image is mounted, and the capture run unmounted
before reaching it. It is the one fixture still written by hand. Replacing it needs a capture taken
*while* an image is mounted — `scripts/Verify-DismBackend.ps1` does exactly that and writes
`get-mountedwiminfo-while-mounted.txt`.

## Refreshing

`scripts/Verify-DismBackend.ps1` writes real output for every one of these commands to
`Samples/captured/` (untracked). From an elevated prompt:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Verify-DismBackend.ps1
```

Then diff `captured/` against this directory. A difference is a finding, not a nuisance — it means
the parser is being tested against a shape DISM no longer produces.
