# TinyWin.Registry

Owner worktree: `registry-engine`. Implements `IOfflineRegistry` / `IHiveSession` (see
`src/TinyWin.Core/Abstractions/IOfflineRegistry.cs`).

Read `docs/PLAN.md` §3.3 before writing anything. The two rules that matter:

1. **No raw `RegistryKey` may escape the session.** A stray managed finalizer pins a hive open
   and the image can then never be dismounted.
2. **`SeBackupPrivilege` and `SeRestorePrivilege` must be explicitly enabled** on the process
   token. Both are present-but-disabled by default even in an elevated process.

Unload must retry after `GC.Collect()` + `WaitForPendingFinalizers()`, and must throw if it
ultimately fails rather than reporting success.

---

## Layout

| File | Role |
|---|---|
| `OfflineRegistry.cs` | `IOfflineRegistry`. Opens sessions, unloads stranded hives. |
| `HiveSession.cs` | `IHiveSession`. Action semantics and teardown. |
| `HiveUnloader.cs` | The retry loop: forced finalization, backoff, give up honestly. |
| `HiveLayout.cs` | Hive → file path in the image, hive → `HKLM` mount name. |
| `RegistryKeyPath.cs` | Normalising and validating catalog `key` fields. |
| `RegistryValueConverter.cs` | Catalog `kind`/`data` → CLR object + `RegistryValueKind`. |
| `TaskCache.cs` | Scheduled task registration layout in the SOFTWARE hive. |
| `Interop/INativeRegistry.cs` | **The seam.** Everything above it is unit tested. |
| `Interop/Win32NativeRegistry.cs` | The only file that calls Win32. |

Everything above `INativeRegistry` is covered by `tests/TinyWin.Registry.Tests` against
`FakeNativeRegistry`. Everything below it needs elevation and is covered by
`scripts/verify-offline-registry.ps1`.

## Verifying by hand

```powershell
# Elevated. Needs no ISO and no mounted image — it saves a real hive out of HKCU.
.\scripts\verify-offline-registry.ps1

# Also run the all-five-hives test against a genuinely mounted image.
.\scripts\verify-offline-registry.ps1 -MountPath C:\scratch\mount

# Check every catalog registry action against real media (docs/catalog-gaps.md §3.3).
.\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -VerifyCatalog -OutFile audit.csv

# Dump the scheduled task registrations (docs/catalog-gaps.md §3.1).
.\scripts\dump-offline-registry.ps1 -MountPath C:\scratch\mount -TaskCache
```

---

## Design notes

### Why `RegLoadKey` and not `RegLoadAppKey`

**What `RegLoadAppKey` actually does.** It does *not* hand back a mount point. There is no name
under `HKLM`, nothing visible in regedit, and no `RegUnLoadKey` step — it returns an `HKEY` for the
hive root directly, and the hive is released by the kernel when the last handle into it is closed.
It is documented as not requiring `SeBackupPrivilege`/`SeRestorePrivilege`, using file-level access
to the hive file instead.

So on the face of it, it deletes both halves of the problem: no privilege dance, and no unload call
that can fail. That is why it deserved a real look rather than a dismissal.

**Why it does not actually remove the problem.** It relocates it somewhere worse:

- The failure mode is a *leaked handle*, not a failed unload call. `RegUnLoadKey` returning
  `ERROR_ACCESS_DENIED` is a symptom; the disease is that something still has a key open. App hives
  do not cure that — they just remove the diagnostic. With a named mount the user sees
  `HKLM\zTW-SOFTWARE` sitting in regedit and fixes it with `reg unload`. With an app hive there is
  nothing to look at and nothing to unload: a leaked handle in a still-running process blocks the
  WIM dismount with no way to see why, and the only remedy is killing the process.
- **It inverts the handle discipline.** The app hive root handle *is* the hive, so it must be
  long-lived and stored in a field for the life of the session. Today no `RegistryKey` in this
  project outlives the method that opened it, which is what makes "no raw `RegistryKey` may escape"
  a structural property rather than a rule people have to remember. `RegLoadAppKey` would require
  exactly one deliberately long-lived handle, and every subkey would be derived from it.
- **Crash recovery is in the contract.** `UnloadStrandedHivesAsync` exists because Core's preflight
  calls it. There is nothing for it to do in an app-hive world — which sounds like a win until you
  remember it also cleans up after tiny11-style scripts, which strand `z*` mounts constantly.
- **The privilege work does not go away.** `SeBackupPrivilege`/`SeRestorePrivilege` are needed a
  second time, independently of loading, to open ACL-protected keys *inside* the hive with
  `REG_OPTION_BACKUP_RESTORE` — `TaskCache` is exactly that class of key. Choosing `RegLoadAppKey`
  would remove one caller of `EnableHivePrivileges`, not the method.
- It would break `scripts/dump-offline-registry.ps1`, since `reg.exe` cannot see an app hive.

**The honest counterpoint.** `RegLoadKey`'s failure is worse *when it happens*: a stranded named
hive survives process exit and can require a reboot, where an app hive dies with the process. The
call made here is to prefer a visible, repairable failure over an invisible, self-healing one, and
to spend the effort on making unload reliable instead — forced finalization, backoff, and throwing
rather than lying.

**What would make me revisit it.** Any of these:

1. The elevated spike shows `RegUnLoadKey` failing *even after* the forced-finalization retry. That
   would mean the pin is coming from outside our process — an antivirus scanner or Explorer
   grabbing the mount point the moment it appears — and a private namespace nothing else can see
   is the only real fix.
2. We ever need two builds mounted at once. Named mount points collide; app hives do not.
3. `REG_OPTION_BACKUP_RESTORE` opens turn out to be unnecessary, which would remove the
   independent reason to keep the privileges.

The choice sits behind `INativeRegistry`, so it is reversible by writing a second implementation;
`HiveSession` and the action layer would not change.

**Not verified.** DISM refuses to run unelevated here (error 740), so no hive has been loaded by
either mechanism on this machine.

### Where `SetRegistry` does *not* report `NoTarget`

`DeleteRegistryKey` reports `NoTarget` when the key (or the named value) was not there — that is
the docs/PLAN.md §2.1 contract, and it is what catches a catalog entry whose key a Windows update
renamed.

`SetRegistry` deliberately does not. Most tweaks are policy values under keys Windows only
materialises once something sets them — `Policies\Microsoft\Windows\Explorer` does not exist on
stock media — so treating a missing key as a missing target would make every working tweak report a
no-op and bury the signal. The honest no-op question for a write is "did this change anything",
which needs a before/after comparison the catalog does not currently ask for.
`scripts/dump-offline-registry.ps1 -VerifyCatalog` answers it out-of-band today.

### Scheduled task removal lives here

See `TaskCache.cs`. Rationale and the contract change Core needs are in
[`docs/registry-findings.md`](../../docs/registry-findings.md).

### Known gap: deleting ACL-protected keys

Keys are opened with `REG_OPTION_BACKUP_RESTORE`, so reads and writes bypass the ACL using the
backup/restore privileges. **Deletion has no equivalent** — `RegDeleteKeyEx` takes no options and
checks `DELETE` on the key itself, so a key owned by TrustedInstaller or SYSTEM may refuse to be
deleted even elevated. `TaskCache` is very likely in that category: it denies read access to a
non-elevated process on this dev machine.

If the elevated pass hits `ERROR_ACCESS_DENIED` on delete, the fix is to take ownership
(`SeTakeOwnershipPrivilege` → `WRITE_OWNER` → `WRITE_DAC` → grant Administrators full control) and
retry. That is not implemented: it is ~60 lines of P/Invoke that cannot be tested here and might
turn out to be unnecessary, and the error message already names the cause precisely. One elevated
run settles whether it is needed.
