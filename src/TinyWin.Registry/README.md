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
