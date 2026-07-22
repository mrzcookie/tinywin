# Registry engine findings

From the `registry-engine` worktree. Written for the people who own `TinyWin.Core`,
`TinyWin.Catalog` and `catalog/*.json`, because the decisions below need something from them.

Companion to `src/TinyWin.Registry/README.md`, which holds the design notes that stay inside this
project (the `RegLoadAppKey` evaluation, the `SetRegistry`/`NoTarget` asymmetry, the ACL gap).

---

## 1. Scheduled task removal is implemented, in `IHiveSession.ApplyAsync`

`docs/catalog-gaps.md` §3.1 is right: on 26100/26200 a task is a `TaskCache` registration in the
SOFTWARE hive, not a file. `HiveSession.ApplyAsync` now handles `ActionType.RemoveScheduledTask`
directly. **It needs one routing change from Core to actually receive the action** — see §2.

### What it does

Given `name = "\Microsoft\Windows\Application Experience\ProgramDataUpdater"`:

1. Normalise the name to a `Tree`-relative path (drop the leading separator, `/` → `\`, reject
   `..`).
2. If `TaskCache\Tree\<name>` does not exist → **`NoTarget`**. Nothing is written. This is the
   Compatibility Appraiser case §3.1 predicts, and it now reports honestly instead of pretending.
3. Read the task's GUID from the `Id` value on the `Tree` node. If the node exists but has no `Id`,
   **throw** rather than delete: the `Id` is the only thing that can locate the other half, so
   deleting the `Tree` node would orphan the registration permanently. Core turns the throw into
   `ActionOutcome.Failed` with the message, which is the right outcome for genuinely broken media.
4. Delete `TaskCache\<sub>\<GUID>` for `sub` in `Tasks`, `Plain`, `Logon`, `Boot`, `Maintenance`.
   `Tasks` is the registration; the rest are trigger indexes a task appears in only if it has that
   kind of trigger, so a miss there is normal and is not reported.
5. Delete the `Tree` node **last**. Deliberate: it is the only name → GUID mapping, so a crash
   partway through leaves it pointing at whatever remains and a re-run finishes the job. Deleting
   it first would strand the rest unreachable.
6. `Applied`.

Your message named `{Tree, Tasks}`. The trigger-index subkeys are swept too — a task with a logon
trigger has an entry under `Logon` keyed by the same GUID, and leaving it is the "partial delete
leaves a broken registration" case one level down.

### Why here rather than as a primitive Core composes

Both were defensible; this is why this one won.

- **It needs a hive read.** Step 3 reads the GUID. Composing this in Core means exposing a read on
  `IHiveSession`, which today is "callers get `ApplyAsync` and nothing else" — the contract that
  makes it impossible for a caller to hold anything with a lifetime. Widening it to a general
  read/write surface to serve one action is a bad trade. The read exists on the *internal*
  `INativeRegistry` seam instead, where it cannot escape the project.
- **One catalog action must produce one outcome.** Split into Core-composed primitives, a task that
  exists in `Tree` but not `Tasks` — a genuinely broken registration — reports several
  contradictory statuses instead of one honest one, and the build report's no-op counts stop
  meaning anything.
- **The invariant is registry-shaped, not pipeline-shaped.** "`Tree` carries the `Id`, the index
  subkeys are keyed by it, delete `Tree` last" is `TaskCache` schema knowledge. It belongs next to
  the code that does the deletes, which is where someone will look when 26H1 changes it.

**The line I would draw**, so this does not become case-by-case: *if the translation is a
one-to-one rewrite into an action that already exists, Core does it; if it has to read the hive to
decide what to do, it belongs in the registry engine.* By that rule `ApplyRegistryStage`'s
`DisableService` → `SetRegistry` rewrite is correctly in Core (one key, one value, no read), and
`RemoveScheduledTask` is correctly here.

### Unverified

The `TaskCache` subkey list is from the documented Task Scheduler layout, **not from real media**.
It could not be checked: the offline hives need elevation, and `TaskCache` on this dev machine
(also 26200) denies read access to a non-elevated process — `Requested registry access is not
allowed`. `scripts/dump-offline-registry.ps1 -TaskCache` prints the actual subkey list and
cross-checks every catalog task path against the image in one elevated pass.

There is also a real chance deletion hits `ERROR_ACCESS_DENIED` on these keys. See "Known gap" in
`src/TinyWin.Registry/README.md`.

---

## 2. Contract change needed in Core / Catalog — **I did not make this change**

Right now `RemoveScheduledTask` never reaches the registry engine:

- `PlanResolver.ImageActionOrder` (`src/TinyWin.Catalog/Resolution/PlanResolver.cs`) puts
  `RemoveScheduledTask` in `ImageActions`, not `RegistryActions`.
- `ApplyComponentsStage` (`src/TinyWin.Core/Pipeline/Stages/ApplyComponentsStage.cs:118`) executes
  it as a file delete under `Windows\System32\Tasks` — which per §3.1 mostly deletes nothing.
- `ApplyRegistryStage` derives the hives to load from `action.Hive`, and a task action has none.

What is needed:

1. Route `RemoveScheduledTask` to `ApplyRegistryStage` instead of `ApplyComponentsStage` — move it
   from `ImageActionOrder` into `RegistryActionTypes`, or special-case it the way
   `DisableService` already is.
2. **`ApplyRegistryStage` must load the Software hive whenever a task action is present.** The
   action carries no `hive` field and `ActionValidator` does not require one, deliberately — the
   registration is always in SOFTWARE. Something like:

   ```csharp
   var hives = actions.Select(a => a.Action.Hive).OfType<RegistryHive>()
       .Concat(actions.Any(a => a.Action.Type == ActionType.RemoveScheduledTask)
           ? [RegistryHive.Software] : [])
       .Distinct().ToList();
   ```

3. `ApplyRegistryStage.Describe` gains a `RemoveScheduledTask` case (it currently falls through to
   `$"{action.Type} {action.Key}"`, and `Key` is null for a task).
4. `ShouldRun` must return true when only task actions are selected.

The engine side already tolerates both shapes: a task action with no `hive` is assumed to be
Software, and one that names a different hive is rejected with a clear message. Nothing in
`IOfflineRegistry` or `IHiveSession` changes — no interface edit is required, which is why I did
not make one.

**Until this lands**, the two `RemoveScheduledTask` actions in `catalog/system.json`
(`privacy.telemetry`) still go down the file-delete path and will report `NoTarget`.

---

## 3. Yes — there is now a way to dump offline hive contents

For `docs/catalog-gaps.md` §3.3, which records registry policy values as the least-verified part of
the catalog. `scripts/dump-offline-registry.ps1`, elevated, three modes:

```powershell
# Every SetRegistry / DeleteRegistryKey action in the catalog, checked against the image.
.\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -VerifyCatalog -OutFile audit.csv

# TaskCache registrations, plus every catalog task path marked REGISTERED / NOT PRESENT.
.\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -TaskCache

# An arbitrary subtree.
.\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -Hive Software -Key 'Policies\Microsoft'
```

`-VerifyCatalog` emits one row per action with a verdict:

| Verdict | Meaning |
|---|---|
| `KeyMissing` | Key absent on stock media. Normal for a policy key `SetRegistry` will create; a red flag for `DeleteRegistryKey`, which will report `NoTarget`. |
| `ValueMissing` | Key present, value absent. Normal for `SetRegistry`. |
| `AlreadyMatches` | The image already has this value — the action is a genuine no-op. |
| `ValueDiffers` | The image has something else. The action is doing real work. |
| `KeyPresent` | A `DeleteRegistryKey` action has something to delete. |

The rows worth acting on are `AlreadyMatches` (delete the action, it does nothing) and `KeyMissing`
on a `DeleteRegistryKey` (the target moved).

It loads hives under a `zTWDump-` prefix, deliberately distinct from the engine's `zTW-` so this
script and a concurrent build cannot unload each other's mounts, and it unloads in a `finally` with
the same forced-finalization retry the engine uses. Feel free to fold it into the elevated capture
script — or call it from there; it is standalone and takes only `-MountPath`.

---

## 4. What is tested and what is not

**Unit tested** (`tests/TinyWin.Registry.Tests`, 124 tests, runs unelevated in CI):
hive → file path mapping · mount naming and the recovery prefix check · key path normalisation and
rejection of pasted `HKLM\...` paths · every `RegistryValueKind` conversion including hex strings,
booleans, unsigned dwords, base64 and byte-array binary · `Applied`/`NoTarget` for key and value
deletes · `TaskCache` translation, sweep order and the missing-`Id` refusal · load order, dedup,
reverse-order unload · unload retry, attempt limits, and that a stuck hive **throws** · partial
load unwind · cancellation unwind · stranded-hive recovery, including that it ignores mount points
that are not ours.

**Not verified — needs one elevated run** (`scripts/verify-offline-registry.ps1`):
`AdjustTokenPrivileges` actually enabling the two privileges · `RegLoadKey`/`RegUnLoadKey` against
a real hive file · `REG_OPTION_BACKUP_RESTORE` opens working on ACL-protected keys · whether
`RegDeleteKeyEx` can delete TrustedInstaller-owned keys at all · the `TaskCache` subkey list ·
all five hives loading from a real mounted image.

The elevated tests need no ISO and no mounted image — they `reg save` a throwaway `HKCU` key into a
directory shaped like a mount point, which is a genuine hive file and exercises the same code path.
`-MountPath` adds the real-image test on top.
