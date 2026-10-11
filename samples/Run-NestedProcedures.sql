-- Open this file in the debugger. Server: (localdb)\mssqllocaldb; Database: Test.
-- F11 enters Run, then BuildOrders. At the ProcessOrder EXEC, F11 follows:
-- Run -> ProcessOrder -> PriceLine -> CalculateDiscount -> AssessRisk.
-- F10 skips a call; Shift+F11 returns to the caller; F9 sets a line breakpoint.
-- Try a breakpoint on SET @RiskScore or the final SELECT in Run.

EXEC SqlDebugTest.Run
    @Seed = 7,
    @OrderCount = 3,
    @Scenario = N'normal',
    @Verbose = 1;

-- Other scenarios (run one at a time):
-- EXEC SqlDebugTest.Run @Seed=7, @OrderCount=2, @Scenario=N'edge';
-- EXEC SqlDebugTest.Run @Seed=7, @OrderCount=2, @Scenario=N'error';
-- EXEC SqlDebugTest.Run @Scenario=N'error', @Rethrow=1; -- Unhandled SQL error.
