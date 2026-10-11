# SQL Server native debugger feasibility probe

This experiment tests native SQL Server debugging without rewriting or instrumenting an existing procedure. It uses a new, connection-local temporary procedure as a disposable fixture. It does not install a Visual Studio extension.

**Updated result: a hybrid approach successfully read rows from both the table variable and temporary table while the procedure was paused.** Native COM controls execution; a separate SQL connection reads `tempdb` data pages. See [STORAGE-FEASIBILITY.md](STORAGE-FEASIBILITY.md) and [storage-probe-result.log](storage-probe-result.log). The earlier [symbol-only experiment](FEASIBILITY.md) remains valid for that narrower interface.

## Run

Prerequisites match this workstation: Windows x64, .NET Framework 4.x, Visual Studio 2022 Community C++ build tools, and Windows-authenticated SQL Server with debugger permissions. Build paths in the scripts are explicit and may need adjustment on another machine.

```powershell
.\build.ps1
.\bin\NativeProbe.exe '(localdb)\mssqllocaldb' | Tee-Object -FilePath .\storage-probe-result.log
$LASTEXITCODE
```

Run as the Windows user who owns the LocalDB instance. A restricted sandbox may not be able to connect to that user's LocalDB or COM services. No password is used. The initial database is `tempdb`, connection pooling is disabled, and the only procedure created is `#CodexDebugProbe`, which disappears when its connection closes. No firewall or server configuration is changed.

Exit 0 means the experiment completed, native stepping controls passed, all four expected labels were recovered from data pages while paused, and the fixture's final control result matched expectations. This is a limited fixture test, not a general table-inspection guarantee. Exit 2 means a stepping or storage control failed. Exit 1 means a managed setup/execution error; a native failure may terminate the process separately.

## Files and behavior

- `Runner.cs`: establishes SQL connections, obtains and unmarshals the debugger interface, reads candidate objects' pages through the preexisting control connection while paused, validates final row counts, and requests cleanup.
- `NativeHost.cpp`: implements COM callbacks in native C++, requests an initial break, steps into each statement, enumerates local symbols, and attempts named global-symbol lookups at the scalar marker.
- `build.ps1` and `build-native.cmd`: compile the .NET Framework runner and native x64 callback DLL without package downloads.
- `probe-result.log`: successful complete run on the user's LocalDB instance.
- `FEASIBILITY.md`: interpretation, evidence, limitations, and sources.

The fixture creates and populates `@probeTable` and `#ProbeTemp` with two rows each, then sets `@probeScalar` to 314159. At the next paused statement the debugger reads the scalar marker and enumerates symbols. Execution then increments the scalar and returns the final scalar and both row counts as ordinary procedure results. Those final counts are a control that proves the fixture populated both tables; they are **not** debugger-based row inspection.

The debugger uses `GetSyms` for locals and `GetGlobalSym` for `@@SPID` as a successful global control. It also records the rejection of `#ProbeTemp` and `@probeTable` through the global lookup method. These rejections do not claim that a global-only method ought to support local tables.

## Experimental limitations

The COM ABI is based on a historical published interface description, not a supported current SDK. Only the paths recorded in the log were exercised. The compound-symbol child-enumeration branch was not exercised because SQL Server returned a leaf for the table variable. This is a single-worker feasibility probe, not a general debugger: parallel workers, multiple matched connections, user cancellation, remote COM, long-lived sessions, and arbitrary data types are not covered. Query timeouts are configured, but a stuck native COM callback is not protected by a process watchdog.

Do not infer that another SQL Server build behaves identically. The storage reader is restricted to this fixture's columns and small in-row heap data. Candidate discovery uses object creation and schema matching, not a proven session/frame/variable-to-object mapping. Do not use this heuristic to inspect a shared instance containing unrelated workloads. No general query evaluator, process-memory reader, or production debugger is implemented.
