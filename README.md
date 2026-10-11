# SQL Line Debugger

A free, MIT-licensed Windows Forms debugger in C# / .NET 8 with real native SQL Server statement stepping. Tested on this workstation's `(localdb)\mssqllocaldb`, SQL Server 2019 15.0.4382.1.

## Run the packaged app

Open `artifacts\SqlLineDebugger\SqlLineDebugger.exe`, or run:

```powershell
.\artifacts\SqlLineDebugger\SqlLineDebugger.exe
```

Keep all files in that folder together. The package requires the .NET 8 Windows Desktop x64 runtime, which is installed on this workstation. Windows authentication is used; native SQL debugging requires a sysadmin login and the SQL debugging COM proxy installed with SQL debugging/SSDT components. The app uses its own debug backend, not the SSDT debug engine.

Click **Demo** to debug disposable, uniquely named modules in tempdb. The demo calls a scalar function and a nested procedure, exposes parameters and locals, prints messages, and returns two result sets. The result is `Seed=7`, `Doubled=14`, `Result=17`. Demo modules are dropped on normal completion and cancellation.

For a larger fixture, [six nested sample procedures](samples/README.md) are installed in the `Test` database under `SqlDebugTest`. They contain about 16 KiB of SQL and provide five nested procedure frames, loops, branches, OUTPUT parameters, PRINT messages, four result sets, and an error scenario. Open `samples/Run-NestedProcedures.sql` with database `Test` selected.

For your own code:

1. Enter your server and database; defaults are `(localdb)\mssqllocaldb` and `tempdb`.
2. Click **Load Modules**. In **Modules**, double-click a procedure or function to view the database definition and set breakpoints.
3. Enter an invocation in the **Query** tab, for example `EXEC dbo.YourProcedure @id=42;`.
4. Press **F5** to start. Execution pauses before the first executable statement. **F11** enters procedures and functions.
5. Inspect **Locals**, **Call Stack**, **Debug Output**, and **Result Sets**. Module source tabs open as you enter them.

Submit a single SQL batch; `GO` is a client batch separator and is not accepted. **Open SQL** loads a script and **Save SQL** saves the active source. Database module tabs are read-only.

Close source tabs with their **×** button or **Ctrl+W**. Closing a tab preserves its breakpoints and Query edits. Single-click a **Call Stack** frame to open its source again; the paused frame also restores its statement marker. **Show Query** reopens your batch with its edits; **Open SQL** reopens Query when loading a script.

| Action | Shortcut |
| --- | --- |
| Start / Continue | F5 |
| Step Into | F11 |
| Step Over | F10 |
| Step Out | Shift+F11 |
| Toggle breakpoint on current line | F9, or click the left gutter |
| Stop / cancel execution | Shift+F5 |
| Close active source tab | Ctrl+W |
| Break at next statement boundary | Pause toolbar button |

## Source mapping

Source comes from SQL Server's actual module definition or the dynamic batch text supplied by its debug callbacks. Native UTF-16 byte spans map to that exact text, with an inclusive end offset and explicit conversion for the editor's line endings. Source hashes prevent old breakpoints from binding after a definition changes. Invalid spans produce a diagnostic instead of guessed highlighting.

The tests cover live CRLF and LF batches, Unicode, blank lines, multiline SELECT statements, final-token highlighting, and a breakpoint that must not bind to the previous statement's trailing indentation. A 30-second native pause also verifies that stepping and variable inspection still work after a human-length pause. This addresses the source-copy and offset problems exercised by the tests; it does not establish that every possible SSDT drift case is solved.

## Build and test

Requirements: Windows x64, .NET 8 or newer SDK, and Visual Studio C++ x64 tools. `native\build.cmd` discovers the C++ installation using `vswhere`; NuGet restores Microsoft.Data.SqlClient and its dependencies.

```powershell
.\build-debugger.ps1 -Publish
.\test-debugger.ps1
```

Open `SqlLineDebugger.sln` to edit the C# application. Run the build script before building in Visual Studio so the native bridge exists. Build outputs are in `src\SqlLineDebugger\bin\Release\net8.0-windows`; publish outputs are in `artifacts\SqlLineDebugger`.

Run tests as the Windows user who owns LocalDB. Restricted sandboxes may be unable to access that user's LocalDB or COM services. The integration suite creates only unique demo procedures/functions in tempdb, cleans them up, and verifies native stepping, scalar values, OUTPUT parameters, nested breakpoints, cancellation, restart after cancellation, source mapping, and result sets. The invisible UI smoke test checks the actual form, captures previews, records step latency, and fails if any demo step takes more than 2 seconds. A 240-line editor regression verifies that source refresh and visible steps preserve scroll position while off-screen steps remain visible. The native match interface includes the installed SQL Debugging proxy's Ping callback; this fixes the original 20–23 second crash-report stall described in ARCHITECTURE.md.

- [Native test log](debugger-tests.log)
- [UI test log](artifacts/ui-tests.log)
- [Editor scroll test log](artifacts/editor-scroll-tests.log)
- [Paused debugger preview](artifacts/debugger-paused.png)
- [Result-set preview](artifacts/debugger-results.png)
- [Architecture and support boundary](ARCHITECTURE.md)

## Current limits

This is an experimental working development debugger, not a compatibility-complete dbForge or SSDT replacement. Its native COM interface is private and can change across SQL Server builds. Validated scope: local SQL Server 2019, Windows authentication, serial T-SQL, scalar functions, procedures, scalar locals, and output parameters. SQL CLR, parallel UDF debugging, encrypted modules, arbitrary expression evaluation, modifying locals, and table-variable row inspection are not implemented. SQL Server may skip declaration statements that it does not expose as executable debug boundaries.

Continue currently steps statements internally so nested breakpoints remain effective, adding overhead to long loops. A running statement pauses at the next boundary. Results display at most 10,000 rows per result set; values are transferred to the UI as invariant text with NULLs preserved. Debugging executes SQL normally and Stop does not undo effects committed earlier. Avoid altering modules concurrently with a debug run.

Each session gets a fresh hidden native worker process because the LocalDB tests found that reusing a process after cancellation could lose debug callbacks. A native worker failure is reported to the UI. No stored procedure rewriting, extension installation, or server/firewall configuration changes are performed by the app.

The original native and storage feasibility experiments are preserved: [probe README](PROBE-README.md), [scalar-symbol findings](FEASIBILITY.md), and [storage findings](STORAGE-FEASIBILITY.md). The storage-page experiment is not integrated into this app.
