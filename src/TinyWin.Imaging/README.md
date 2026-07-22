# TinyWin.Imaging

Owner worktree: `imaging-engine`. Implements `IImagingBackend` (see
`src/TinyWin.Core/Abstractions/IImagingBackend.cs`).

Ship `DismExeBackend` first — process invocation of `dism.exe` is the guaranteed floor and
unblocks the whole pipeline. Native backends go in behind the same interface afterwards as a
pure optimisation. See `docs/PLAN.md` §3.2 and the findings in `docs/spikes/dism-backend.md`.

**All DISM operations require elevation.** `dism.exe` returns error 740 otherwise.
