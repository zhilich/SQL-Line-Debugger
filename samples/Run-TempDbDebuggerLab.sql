-- Debugger connection: (localdb)\mssqllocaldb / Test.
-- Load Modules, set breakpoints at the labeled PRINT statements, then start.
-- Pauses occur BEFORE a statement, so each PRINT checkpoint follows the relevant DML.
-- Run ONE scenario at a time. The normal run includes all supported-layout labs.
EXEC SqlDebugLab.Run @RowCount=3, @Seed=7, @RecursionDepth=3;

-- Empty table-variable and temp-table schemas; other labs still populate their own data.
-- EXEC SqlDebugLab.Run @RowCount=0, @RecursionDepth=0;

-- Longer loops, repeated stepping, more locks, and changing object sort order.
-- EXEC SqlDebugLab.Run @RowCount=25, @RecursionDepth=5;

-- Intentional deepest error: caught by Run; partial result sets are returned.
-- EXEC SqlDebugLab.Run @RowCount=3, @Scenario=N'error';

-- Same error propagates to the client. Later labs and final result sets are skipped.
-- EXEC SqlDebugLab.Run @RowCount=3, @Scenario=N'error', @Rethrow=1;

-- Adds datetime2 and non-unique clustered layouts (now supported by the reader).
-- Inspect Y3 and Y4; the original module's PRINT labels still say unsupported.
-- EXEC SqlDebugLab.Run @IncludeUnsupported=1;
