# Nested procedure debugger fixture

For the expanded table-variable and temp-table fixture, see
[TempDB debugger lab](TempDbDebuggerLab.md) and open
[Run-TempDbDebuggerLab.sql](Run-TempDbDebuggerLab.sql). It uses a separate
`SqlDebugLab` schema and includes recursion, functions, dynamic SQL, nested
scopes, snapshots, transactions, and deliberate error scenarios.

Installed in `Test` on `(localdb)\mssqllocaldb`, in the `SqlDebugTest` schema.
Six procedure definitions contain 16,438 bytes of ASCII/UTF-8 SQL (16.05 KiB).
SQL Server stores those definitions as Unicode internally. The installation
script also includes schema setup and transaction boilerplate.

| Procedure | SQL source bytes | Purpose |
| --- | ---: | --- |
| Run | 4,587 | Entry point, temporary tables, loop, TRY/CATCH, four result sets |
| BuildOrders | 2,182 | Deterministic order/line generation and edge/error inputs |
| ProcessOrder | 2,669 | Line loop, OUTPUT totals, shipping, review status |
| PriceLine | 2,384 | Discount call, tax, line totals, temporary table updates |
| CalculateDiscount | 2,413 | Risk call, loyalty/volume rates, cap and risk adjustment |
| AssessRisk | 2,203 | Deepest frame, component scores, decisions, intentional THROW |

The deepest call chain is:

```text
Run -> ProcessOrder -> PriceLine -> CalculateDiscount -> AssessRisk
```

`Run` also calls `BuildOrders`. Run the entry point rather than invoking helpers
directly: helpers share the temporary tables created by their caller. Execution
creates no permanent rows or tables. Installation creates six procedures and
one schema and refuses to overwrite an existing schema.

In the debugger, select server `(localdb)\mssqllocaldb`, database `Test`, and
open [Run-NestedProcedures.sql](Run-NestedProcedures.sql). Click **Load Modules**
to view these procedures and set breakpoints, then **Start**. F11 enters a call,
F10 steps over it, Shift+F11 returns to the caller, and F9 toggles a breakpoint.
Useful breakpoints include the line loop in `ProcessOrder`, discount adjustment
in `CalculateDiscount`, risk component assignments in `AssessRisk`, and the
final SELECT statements in `Run`.

```sql
EXEC SqlDebugTest.Run @Seed=7, @OrderCount=3, @Scenario=N'normal';
```

The default run completes 3 orders / 9 lines with GrandTotal **748.12**. It emits
PRINT messages and four result sets: summary, orders, lines, and execution trace.
OUTPUT parameters and RETURN codes are available in caller variables after
returning from nested procedures. Inspect scalar locals while stepping; table
rows are supplied through the result windows when execution reaches the SELECTs.

Other runs:

```sql
-- Zero quantity, zero price, and high quantity.
EXEC SqlDebugTest.Run @Seed=7, @OrderCount=2, @Scenario=N'edge';
-- Exercise Accept, Limited, and Review decisions.
EXEC SqlDebugTest.Run @Seed=0, @OrderCount=12, @Verbose=0;
-- Error 51002 in AssessRisk reaches Run's CATCH; four partial result sets remain.
EXEC SqlDebugTest.Run @Seed=7, @OrderCount=2, @Scenario=N'error';
-- Propagate the same error to the SQL client.
EXEC SqlDebugTest.Run @Scenario=N'error', @Rethrow=1;
```

To install in a fresh `Test` database or validate the fixture:

```powershell
sqlcmd -S '(localdb)\mssqllocaldb' -E -b -i samples\Install-NestedProcedures.sql
.\samples\Test-NestedProcedures.ps1
```

Validation checks totals, OUTPUT-derived results, row counts, all risk branches,
zero-value lines, error propagation, and all four result sets. Logs:
[installation sizes](../artifacts/nested-sample-install.log) and
[scenario tests](../artifacts/nested-sample-tests.log).
