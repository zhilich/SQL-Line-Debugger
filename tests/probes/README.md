# Diagnostic scripts

These optional development scripts investigate SQL Server internals used by the
TempDB inspector. They are source files, not generated test results, and are not
part of the main test suite.

- `Probe-TemporalLayout.ps1` inspects physical temporal-column and clustered-index
  layouts using a disposable temporary table. It connects to the local user's
  `(localdb)\mssqllocaldb` instance and requires a `Test` database.
- `tempdb-xe-probe.sql` exercises temporary-object lock and statement events using
  disposable table variables, a temporary table, and an Extended Events session.
  Run it on an isolated development instance with the required XE permissions.

Keep captured output under the ignored `artifacts` directory.
