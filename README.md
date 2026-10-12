# SQL Line Debugger

A free, MIT-licensed Windows Forms debugger in C# / .NET 8 with real native SQL Server statement stepping. Tested on this workstation's `(localdb)\mssqllocaldb`, SQL Server 2019 15.0.4382.1.

## Build and run

Build the app from source using the requirements in **Build and test** below:

```powershell
.\build-debugger.ps1 -Publish
.\artifacts\SqlLineDebugger\SqlLineDebugger.exe
```

Keep all files in the generated `artifacts\SqlLineDebugger` folder together. The package requires the .NET 8 Windows Desktop x64 runtime. Windows authentication is used; native SQL debugging requires a sysadmin login and the SQL debugging COM proxy installed with SQL debugging/SSDT components. The app uses its own debug backend, not the SSDT debug engine.

Click **Demo** to debug disposable, uniquely named modules in tempdb. The demo calls a scalar function and a nested procedure, exposes parameters and locals, prints messages, and returns two result sets. The result is `Seed=7`, `Doubled=14`, `Result=17`. Demo modules are dropped on normal completion and cancellation.

For a larger fixture, [six nested sample procedures](samples/README.md) are installed in the `Test` database under `SqlDebugTest`. They contain about 16 KiB of SQL and provide five nested procedure frames, loops, branches, OUTPUT parameters, PRINT messages, four result sets, and an error scenario. Open `samples/Run-NestedProcedures.sql` with database `Test` selected.

For your own code:

1. Enter your server and database; defaults are `(localdb)\mssqllocaldb` and `tempdb`.
2. Click **Load Modules**. In **Modules**, double-click a procedure or function to view the database definition and set breakpoints.
3. Enter an invocation in the **Query** tab, for example `EXEC dbo.YourProcedure @id=42;`.
4. Press **F5** to start. Execution pauses before the first executable statement. **F11** enters procedures and functions.
5. Inspect **Locals**, **Call Stack**, **TempDB**, **Debug Output**, and **Result Sets**. Module source tabs open as you enter them.

Submit a single SQL batch; `GO` is a client batch separator and is not accepted. **Open SQL** loads a script and **Save SQL** saves the active source. Database module tabs are read-only.

Close source tabs with their **×** button or **Ctrl+W**. Closing a tab preserves its breakpoints and Query edits. Single-click a **Call Stack** frame to open its source again; the paused frame also restores its statement marker. **Show Query** reopens your batch with its edits; **Open SQL** reopens Query when loading a script.

The active frame has a yellow statement highlight and arrow. Caller frames show their suspended call site in green, including after reopening a closed tab. Selecting a recursive caller displays that invocation's own call site.

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

## TempDB inspection

While paused, open **TempDB** and click **Inspect** beside a table variable or temporary table. The list contains the resolved/internal name, Inspect, and Global, ordered by most recent lock acquisition. Inspect opens a modal, read-only snapshot dialog. It reads once; selecting a table or stepping never reads or refreshes its rows. Close the dialog to continue debugging, and click Inspect again for a new snapshot.

**Global** opens a name dialog prefilled with `dbg_<name>` (for example, `dbg_Rows` for `@Rows`). The `##` prefix is added automatically. Confirming Copy replaces an existing global table with a snapshot of the selected object's rows and supported base column types. For example, query `SELECT * FROM ##dbg_Rows` from another connection on the same instance. Empty tables keep their column schema. Copies retain duplicates, NULLs, binary data, date/time precision and large SQL decimals; indexes and constraints are not copied. A retained creator connection keeps each global table available across debug sessions until the app closes. [SQL Server global-table lifetime rules](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#temporary-tables) apply when those connections close.

Discovery uses a separate connection opened before native registration and a uniquely named XE session filtered to the target SPID. It captures TempDB `lock_acquired` events, module entry/return, and stored-procedure/ad-hoc statement boundaries, processes their event sequence, and correlates referenced table variables with physical objects. Variables allocated at procedure entry can appear empty with internal names before any INSERT or SELECT references them. Ambiguous multi-variable references retain internal names until later statements resolve them. Native frame lifetimes distinguish nested/repeated invocations and shadowed names and hide cached storage after return. The list updates asynchronously as XE buffers become available, keeping statement stepping fast; discovery can take roughly a second after a pause. No row inspection occurs during this collection.

Rows are read by executing an embedded, parameterized SQL batch directly against `tempdb` on the selected instance, from a connection in the host process outside the registered debug worker. The batch is stored at [SelectTempDbRows.sql](src/SqlLineDebugger/Sql/SelectTempDbRows.sql). The inspector does not install or call a stored procedure and does not require the `Test` database. Exact internal name, object ID, and creation date are checked before reading, so stale objects produce an error. XE sessions are removed on completion/cancellation; the host also attempts cleanup if its worker fails.

The raw-page reader supports a single uncompressed rowstore partition, ordinary heaps and unique/non-unique clustered indexes, integer/bit/real/float/decimal/numeric, GUID, character and binary columns, date/time/datetime2/datetimeoffset/datetime/smalldatetime, and supported off-row LOB layouts. Internal clustered uniquifiers are accounted for without appearing in the snapshot. Date/time precision 0–7 and stored time-zone offsets are preserved; the grid displays fractional seconds and offsets explicitly. Decimal values up to precision 38 are preserved using SQL decimal values where CLR decimal is insufficient. Each inspection has the reader's 100-page allocation limit and a 30-second command timeout; exceeding limits produces an error rather than a partial grid. Unsupported layouts/types, including XML, money, sql_variant, compressed/forwarded records and sparse/computed columns, produce an inspection error identifying the unsupported column where possible. Reading physical pages can expose uncommitted values and does not provide a transaction-consistent SELECT snapshot. These SQL Server internals remain version-dependent.

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

TempDB tests additionally cover ambiguous names and later resolution, empty schema, duplicates/NULL/Unicode, same-name objects in another session, snapshot immutability, updates/deletes/drop, transactions/rollback, numeric/GUID/binary/LOB values, nested/shadowed variables, cached objects across repeated calls, function variables, cancellation/SQL errors, and XE cleanup. Dialog tests exercise the actual Inspect event, verify the two-column list and read-only modal snapshot, check that the target remains paused and snapshots never auto-refresh, and verify inspection errors.

The test script generates `debugger-tests.log` and logs/screenshots under `artifacts`. Build outputs, published binaries, test results, and local IDE/tool caches are excluded from Git. Diagnostic scripts are preserved in [tests/probes](tests/probes/README.md). See [Architecture and support boundary](ARCHITECTURE.md) for implementation details.

## Current limits

This is an experimental working development debugger, not a compatibility-complete dbForge or SSDT replacement. Its native COM interface is private and can change across SQL Server builds. Validated scope: local SQL Server 2019, Windows authentication, serial T-SQL, scalar functions, procedures, scalar locals, output parameters, and the TempDB inspection scope described above. SQL CLR, parallel UDF debugging, encrypted modules, arbitrary expression evaluation, and modifying locals are not implemented. SQL Server may skip declaration statements that it does not expose as executable debug boundaries.

Continue currently steps statements internally so nested breakpoints remain effective, adding overhead to long loops. A running statement pauses at the next boundary. Results display at most 10,000 rows per result set; values are transferred to the UI as invariant text with NULLs preserved. Debugging executes SQL normally and Stop does not undo effects committed earlier. Avoid altering modules concurrently with a debug run.

Each session gets a fresh hidden native worker process because the LocalDB tests found that reusing a process after cancellation could lose debug callbacks. A native worker failure is reported to the UI. Debugged procedures execute intact. The TempDB inspector executes its embedded page-reader SQL directly and creates a temporary XE session; no procedure installation, extension installation, or firewall configuration changes are performed.
