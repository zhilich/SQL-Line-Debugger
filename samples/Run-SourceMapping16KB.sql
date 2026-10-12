-- Server: (localdb)\mssqllocaldb; database: Test.
-- Load Modules and select dbo.DebuggerSourceMapping16KB to set breakpoints.
EXEC dbo.DebuggerSourceMapping16KB
    @Seed = 7,
    @Iterations = 3,
    @TakeErrorPath = 0,
    @Verbose = 1;

-- To exercise the caught THROW and CATCH source spans, change TakeErrorPath to 1.
