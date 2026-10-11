# Hybrid debugger: successful storage inspection experiment

Tested October 8, 2026, America/Chicago, against `(localdb)\mssqllocaldb` (SQL Server 2019, 15.0.4382.1).

## Result

The probe recovered the actual rows of both `@probeTable` and `#ProbeTemp` while the test procedure was paused in a native `IHostDebug::Step` callback. No extra SQL was inserted into the procedure. Its body was identical to the symbol-only experiment.

| Storage object | Id | Label |
|---|---:|---|
| Table-variable candidate `#B0A390EC` | 11 | table variable alpha |
| Table-variable candidate `#B0A390EC` | 22 | table variable beta |
| Local temporary-table candidate `#ProbeTemp...` | 33 | temp alpha |
| Local temporary-table candidate `#ProbeTemp...` | 44 | temp beta |

The [captured log](storage-probe-result.log) shows the row reads between step 8 (scalar 314159) and its `Continue`, before the final ordinary SELECT. `PAUSED STORAGE VERIFICATION=True` confirms recovery of all four expected labels. The log also contains the expected IDs. The current automated storage assertion checks label-set equality, not full tuple/duplicate equality; it is a smoke test for the access path.

## Architecture demonstrated

1. Open a control SQL connection and collect a baseline of temporary object IDs.
2. Register the native debugger and open a separate target connection.
3. Execute the intact disposable procedure and step natively.
4. At a paused statement, use the preexisting control connection to find new candidate temporary objects with the fixture's columns.
5. Enumerate allocated data pages using `sys.dm_db_database_page_allocations`.
6. Read those pages using `DBCC PAGE ... WITH TABLERESULTS`, extract the fixture's column values, and log them before resuming execution.

The control connection predates debugger registration, so its inspection queries are not themselves debugged. Inspection is synchronous within the native pause callback. It does not depend on SSDT's Watch evaluator or on the server returning a structured table symbol.

## What this proves, and what it does not

**Proven:** native statement stepping and read-only observation of these two ordinary tempdb-backed tables can be combined without procedure instrumentation on the tested instance. The earlier failure of the debugger symbol API does not preclude this hybrid design.

**Not proven:** a general-purpose row reader, reliable automatic variable-to-object identity, transaction-consistent snapshots, or compatibility across SQL Server versions and table formats.

The fixture has two small heaps with int and short nvarchar columns, no concurrent modifications, no explicit transaction, and distinctive data. Current candidate discovery is a heuristic: newly observed object IDs plus column names. It does not establish ownership by SPID or mapping to a named variable. Object caching/reuse, nested calls, recursion, multiple identically shaped variables, and concurrent sessions can defeat it. The known fixture values establish the source of the rows in this controlled run; a product cannot rely on that knowledge.

## Implementable next design

- Keep the proven native stepping backend for execution control, stack context, and scalar watches.
- Build a separate table-inspection backend that returns rows with provenance: object ID, file/page/slot, collection time, paused frame, and mapping confidence.
- Develop object identity tracking at successive statement boundaries. Combine parsed declarations and current stack scope with object lifecycle observations and session ownership evidence where available. Treat this as work to validate, not an already proven mapping method. Refuse ambiguous automatic mappings; show candidates or require an isolated debug instance.
- Start with isolated development instances and ordinary tempdb heaps with a defined type subset. Explicitly reject unsupported layouts.
- Validate updates, deletes and ghost records, NULLs, variable-length data, multi-page tables, clustered indexes, off-row values, nested procedures, recursion, transactions, and object reuse. Page dumps are physical observations; they must not automatically be represented as a normal SELECT result.
- Read and page results on demand, cap scanning, and keep the target paused until collection finishes. A paused target alone does not freeze other sessions or engine background activity.
- Add a UI only after identity and row-decoding tests establish the supported scope.

This is a feasible experimental development debugger, with the data-inspection layer tied to SQL Server internals. It is not yet a drop-in general replacement for SSDT.

## Sources and support boundary

[Microsoft's page architecture guide](https://learn.microsoft.com/en-us/sql/relational-databases/pages-and-extents-architecture-guide?view=sql-server-ver17) describes page allocation discovery and explicitly says `sys.dm_db_database_page_allocations` is unsupported and subject to change. It also identifies `DBCC PAGE` as unsupported. The supported `sys.dm_db_page_info` alternative exposes page-header information, not a replacement rowset reader.

[Microsoft's example of reading index page contents](https://techcommunity.microsoft.com/blog/coreinfrastructureandsecurityblog/viewing-sql-server-non-clustered-index-page-contents/370420) illustrates `DBCC PAGE` with `TABLERESULTS`. The rows and pause timing reported here come from our local execution, not from assuming that example covers table variables.
