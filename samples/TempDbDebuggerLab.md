# TempDB debugger lab

Server: `(localdb)\mssqllocaldb`. Database: `Test`. Schema: `SqlDebugLab`.
The fixture contains eleven procedures and two functions. Installation refuses
to overwrite an existing schema. All working data uses table variables or
session-local temp tables; there are no permanent data tables.

Open [Run-TempDbDebuggerLab.sql](Run-TempDbDebuggerLab.sql) in the debugger,
select `Test`, and click **Load Modules**. Set breakpoints on the labeled PRINT
statements in the modules, then start the normal run:

```sql
EXEC SqlDebugLab.Run @RowCount=3, @Seed=7, @RecursionDepth=3;
```

Breakpoints pause before the statement. Each labeled PRINT follows the setup
or modification whose rows you should inspect. Use **TempDB → Inspect** to
capture a snapshot, close the dialog, and continue stepping. Reinspect at a
later checkpoint to see subsequent changes. The snapshots do not refresh.

The main call chain is:

```text
Run → ProcessWork → EvaluateRow → ScoreRow → AdjustScore (scalar function)
                        └─────→ MakeTags (table-valued function)
```

`Run` also calls `BuildWork`, `ScopeChild` twice, `Recurse`, `Lifecycle`,
`TransactionLab`, `TypesLab`, and `DynamicLab`. Invoke `Run` as the entry point;
helper procedures rely on caller-created `#Work`, `#Trace`, or `#Snapshots`.

| Checkpoint | What to inspect / exercise |
| --- | --- |
| A1 in Run | First use joins two empty variables. `@Left` and `@Right` may initially have internal names. Inspect either empty object and its two columns. |
| A2 in Run | Single-variable use of `@Left` gives evidence to resolve the ambiguous pair. |
| A3 in Run | `@Left`: 3 rows including duplicate `(1, alpha)` and `(2, NULL)`; `@Right`: one row; root `@rows`: `(100, root caller)`. |
| B1 in BuildWork | `@numbers`: `@RowCount` rows. F10 over the following INSERT, then inspect the inherited `#Work`. |
| P1 in ProcessWork | `@queue` marks processed rows as Done. Repeated F11/F10/Shift+F11, breakpoints in loops, and changing lock-recency order. |
| E1/E2 in EvaluateRow | Step into MakeTags; afterward `@tags` has row label and parity tags. E1 is the INSERT statement, rather than a PRINT. |
| D1 in ScoreRow | Deep call stack, table variable `@components`, inherited temp tables, scalar locals and OUTPUT parameter. Click caller frames to inspect source markers. |
| F1 in AdjustScore | Scalar-function table variable `@components` before its aggregate SELECT. |
| F2 in MakeTags | Return table variable `@tags` has two rows before RETURN. |
| E3 in EvaluateRow | Scalar RETURN and OUTPUT values after ScoreRow returns; breakpoint on UPDATE. |
| S1 in ScopeChild | Two `@rows` objects with the same resolved name: root Id 100 versus child Id 201/202. `#ChildTemp` has one row; inherited `#Work` is also accessible. |
| S2/S3 in Run | Child variables and `#ChildTemp` disappear. Repeated calls exercise cached-object reuse while root `@rows` survives. |
| R1 in Recurse | Distinct `@rows` objects per recursive frame, containing Depth 3, 2, 1, 0 by the deepest call. Watch scopes disappear while unwinding. |
| L1 in Lifecycle | `#Lifecycle__`: 4 rows, duplicates and NULL. Friendly name should preserve trailing underscores. |
| L2 in Lifecycle | 3 rows remain; both Id 1 labels changed to `changed`, Id 3 deleted. |
| L3/L4 in Lifecycle | Truncate leaves empty schema; drop removes the object. |
| L5/L6 in dynamic SQL | Same friendly name `#Recreated` is dropped and replaced in a nested dynamic scope: first Id 10, then Id 20. Step into the dynamic batches to reach these PRINTs. |
| T1 in TransactionLab | Inspect uncommitted `#Uncommitted` rows and `#Work` amounts increased by 100. |
| T2/T3 in TransactionLab | Rollback removes `#Uncommitted` and restores #Work. Commit preserves `#Committed` until procedure exit. |
| Y1 in TypesLab | Unique clustered `#Types`: integer widths, bit, decimal, real/float, GUID, fixed/ANSI/Unicode text, binary, precision 38, NULLs and a 10,000-character off-row Unicode value. `@binary` includes fixed binary and Unicode characters. |
| Y2 in TypesLab | `#ManyRows`: 200 rows across multiple pages. |
| Y3/Y4 (optional) | Inspect datetime2(3) and duplicate rows in a non-unique clustered index with hidden uniquifier. Both layouts are now supported. The original procedure still labels them "unsupported" in PRINT output. |
| Q0/Q1 in DynamicLab | Step into parameterized sp_executesql. Inspect `@dynamicRows`, `#DynamicLocal`, and inherited `#DynamicShared`. Q0 is the EXEC statement. |
| Q2 in DynamicLab | Dynamic local objects disappear; `#DynamicShared` retains both rows until the procedure returns. |
| X1 in Run (error scenario) | TRY/CATCH, deepest error 51102, partial work and error locals; @Rethrow=1 propagates it. |
| Z1 in Run | Only root objects remain: #Work, #Trace, #Snapshots, @Left, @Right, @rows. Proceed to multiple result sets and session completion. |

The normal run emits three count result sets from the dynamic labs, followed by
seven final result sets: summary, work, trace, lab summaries, Left, Right, and
root rows. For the default inputs, the fixture arithmetic gives Processed=3,
ScoreTotal=38, RecursiveFrames=4, and final work amounts 14.25, 21.25, 28.25.
Work scores are 16, 8, 14. Final `#Snapshots` contains four lab summaries.
These are expected values for manual verification; the fixture has not been
executed as part of installation.

Additional scenarios are commented in the run script: empty work, 25 work
rows, deeper recursion, caught/propagated errors, and additional storage layouts.
The original `@IncludeUnsupported=1` flag now enables supported datetime2 and
non-unique clustered-index checks; its name is retained for existing run scripts.
An error at row 2 stops later labs. Recursion is limited to depth 8 and work
size to 1,000 rows so interactive runs remain bounded.

Also try closing a module tab while paused and clicking its call-stack row to
reopen it, changing selected frames during recursion, stepping out repeatedly,
and stopping mid-transaction. Run the fixture again to check a fresh session.
To check session filtering, execute the same entry point concurrently in two
debugger instances with different Seed values; each should show its own #Work.

Installation from the repository root:

```powershell
sqlcmd -S '(localdb)\mssqllocaldb' -E -b -i samples\Install-TempDbDebuggerLab.sql
```
