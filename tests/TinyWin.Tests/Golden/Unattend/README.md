# Unattend golden files

One `autounattend.xml` per case in
[`../../Unattend/UnattendCases.cs`](../../Unattend/UnattendCases.cs). `UnattendGoldenTests`
renders each case and compares it with the file of the same name here.

`UnattendGenerator` is pure and deterministic, so this is the cheapest place in the codebase to
prove correctness: a malformed answer file does not fail at build time, it fails minutes into a
Windows install, long after the user made the ISO.

## Regenerating

```powershell
$env:TINYWIN_UPDATE_GOLDEN = '1'
dotnet test tests\TinyWin.Tests\TinyWin.Tests.csproj
Remove-Item Env:\TINYWIN_UPDATE_GOLDEN
```

The tests then rewrite these files in place and pass unconditionally. **Read the resulting
`git diff` line by line** — regenerating is not review, the diff is. Check in particular:

- `processorArchitecture` is `amd64` or `arm64`, never `x64` or `x86`.
- Every `component` keeps `publicKeyToken="31bf3856ad364e35"`, `language="neutral"`,
  `versionScope="nonSxS"`. Setup silently ignores a component whose identity does not match.
- Components sit in a pass that accepts them. `Microsoft-Windows-International-Core-WinPE` is
  windowsPE-only; `Microsoft-Windows-International-Core` is not valid there.
- `RunSynchronousCommand/Order` values run 1..n with no gaps inside each `RunSynchronous`.

## Adding a case

Add it to `UnattendCases.All`, regenerate, review the new file. A case with no golden file fails;
so does a golden file with no case.

## Note on line endings

The generator emits CRLF, `.gitattributes` stores `*.xml` as LF, and the comparison normalises
both. Line endings are asserted separately in `UnattendGeneratorTests`.
