# SQL Line Debugger architecture

The Windows Forms application targets .NET 8 on Windows x64. Each debug session starts a separate instance of the executable as a hidden MTA native worker; Forms runs on an STA thread with a message pump. Two named pipes restricted to the current Windows user carry commands and snapshots separately. UI commands enter a channel and are written asynchronously. A fresh worker process avoids the LocalDB registration problem observed when restarting after cancellation under the same process ID, and isolates native ABI failures from the UI. The small C++ DLL hosts native COM callback vtables and queues copied events for a dedicated managed reader. The SQL procedures and functions execute intact inside SQL Server: this application does not interpret SQL, rewrite modules, inject instrumentation, or replay prior statements.

## Execution

1. The worker process opens a control connection with pooling disabled, before registration. It requests the marshaled server interface using `master.dbo.sp_enable_sql_debug`.
2. `CoUnmarshalInterface` obtains `IHostDebugServerInstance`; the native bridge registers this application's machine and process ID.
3. A new target connection matches that registration. Its batch generates `PushDynamic`; an initial asynchronous break starts statement events.
4. `Push` identifies stored modules by database and object ID. The control connection retrieves their actual `sys.sql_modules.definition`. `PushDynamic` supplies dynamic batch text directly.
5. `Step` supplies statement spans and scope. The bridge obtains scalar locals with `GetSyms`, copies values to JSON, frees COM allocations, enqueues the event, and returns the RPC. The managed event pump reads the queue and applies the pause policy outside the callback.
6. A pause raises an immutable snapshot to the UI and returns the COM callback immediately, leaving SQL Server waiting for its next command. Resume calls the native Continue export from the worker's command reader in the MTA COM apartment. This avoids holding a callback RPC open during a long human pause. The UI stays responsive.
7. The target's ordinary `SqlDataReader` collects result sets and `InfoMessage` collects PRINT/SQL informational output. Results are available as the server produces them. A grid displays up to 10,000 rows per result set; remaining rows are drained.
8. Stop sends SQL client cancellation on a separate thread and resumes a paused worker so SQL Server can consume the attention. Calling `StopDebugging` inside the callback means detach rather than cancellation. LocalDB cancellation can affect subsequent registrations for the same process, so each run uses a fresh worker process. Registration is removed after target execution closes; demo fixtures are dropped afterward. The worker exits when the run ends. Host disconnection requests cancellation.

## Source mapping

The `m_offStart` and `m_offEnd` values are UTF-16 byte offsets. On the tested engine, the end identifies the final character inclusively, often omitting a semicolon, and spans can include following whitespace. `SourceDocument` preserves the original database or callback text. Offset validation rejects odd, inverted, or out-of-range spans instead of guessing a line. The RichTextBox displays normalized line endings; prefix conversion maps original coordinates to display coordinates without replacing the underlying source.

Breakpoints bind to a database/object key, a SHA-256 hash of exact source, and a line. Dynamic SQL keys include its text hash. Query breakpoints bind to the actual submitted batch. A breakpoint on a continuation line binds to that statement; trailing indentation on the next line is excluded. If a module's definition changes between loading and execution, an old hash cannot bind to the new definition. Modules should not be concurrently altered during a debug run: the private API does not provide a verified execution-version source snapshot.

Continue, Step Over, and Step Out are managed policies over native Step Into events. Continue stops at matching breakpoints; Over also stops when the call depth returns to the original depth; Out stops at a shallower depth. The initial event and explicit Pause stop at the next statement boundary. This keeps breakpoints effective in nested functions/procedures at the cost of server round trips during Continue. A long executing SQL statement cannot be paused in its interior.

## Compatibility boundary

This is a real, experimental native debugger built on private SQL Server COM interfaces, with Windows authentication, one active target connection, and serial T-SQL execution. SQL CLR and parallel UDF execution are not supported. Workers and stack frames carry batch/worker/level identifiers, but concurrent-worker behavior is not validated. Scalar table-variable symbols can appear as `(table)`; this application does not claim their rows are inspectable. The earlier physical-page experiment is kept separately and is not integrated into the product.

The SQL debugging COM proxy must already be installed and registered on the workstation (on the test machine it came with the SQL debugging/SSDT components). The app does not call the SSDT debugger engine. It requires sysadmin access for native registration. It does not modify firewall or server configuration. Command cancellation does not undo SQL effects already committed before Stop.

The installed SQL Debugging 130 COM proxy expects a `Ping` method after `OnMatch` on the match interface. That slot is missing from the historical published definition. The original bridge omitted it: RPC dispatched through adjacent RTTI data, raised an execute access violation, and Windows Error Reporting suspended the worker for roughly 20–23 seconds while collecting a report. RPC later recovered, so the previous correctness test passed despite the delay. Adding the slot fixes the wait and the subsequent unregister `0x80070057` diagnostic. The UI regression now requires every demo step to arrive within 2 seconds; measured times after the fix are milliseconds. The native suite also holds a pause for 30 seconds to exercise heartbeat calls. Private interfaces still require validation on other installed proxy/server versions.

## Sources

- [Microsoft SQL Server debugger requirements](https://learn.microsoft.com/en-us/sql/ssdt/debugger/run-transact-sql-debugger?view=sql-server-ver17)
- [Microsoft's published historical COM interface definitions](https://patents.google.com/patent/US7500225B2/en), appendix. These describe the ABI; they do not guarantee compatibility on current engines.
- [Original local native probe](PROBE-README.md), [symbol feasibility results](FEASIBILITY.md), and [separate storage experiment](STORAGE-FEASIBILITY.md).

Observed span interpretation, callback behavior, and compatibility claims are grounded in the application's own LocalDB test logs.
