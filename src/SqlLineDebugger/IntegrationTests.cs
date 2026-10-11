using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static class IntegrationTests
{
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
    public static int Run(string logPath)
    {
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        try
        {
            var connection = SqlCatalog.ConnectionString(@"(localdb)\mssqllocaldb", "tempdb");
            foreach (string mode in new[] { "into", "over", "out", "breakpoint", "line-breakpoint", "cancel", "repeat", "long-pause" })
            {
                log.WriteLine("TEST " + mode);
                var points = new BreakpointStore();
                using var session = new DebugSession(connection, points);
                var snapshots = new List<PauseSnapshot>();
                var sources = new List<SourceDocument>();
                var results = new List<DataTable>();
                bool nestedBreakpoint = false, requestedOut = false, returnedOut = false;
                int outLevel = 0;
                bool longPauseRequested = false;
                session.Output += log.WriteLine;
                session.ResultSet += results.Add;
                session.SourceLoaded += source =>
                {
                    sources.Add(source);
                    if (mode == "breakpoint" && source.Name.EndsWith("_Double") && source.Spans.Count == 0)
                        points.Toggle(source, 4); // SET @answer inside the nested function.
                    if (mode == "line-breakpoint" && source.Name.EndsWith("_Outer") && source.Spans.Count == 0)
                        points.Toggle(source, 4); // DECLARE, avoiding prior span's trailing indentation.
                };
                session.Paused += snapshot =>
                {
                    snapshots.Add(snapshot);
                    log.WriteLine($"PAUSE level={snapshot.Level} {snapshot.Reason} {snapshot.Source.Name}:{snapshot.Line} bytes={snapshot.Span.Start}-{snapshot.Span.End} sql={snapshot.Source.Statement(snapshot.Span).Replace("\r", "").Replace("\n", " ")}");
                    Require(snapshot.Line >= 1, "source line is valid");
                    Require(!string.IsNullOrWhiteSpace(snapshot.Source.Statement(snapshot.Span)), "mapped statement is nonempty");
                    if (snapshot.Level == 0) Require(snapshot.Source.Statement(snapshot.Span).TrimEnd(';').EndsWith("= 7", StringComparison.Ordinal), "inclusive end maps the final query token");
                    if (snapshot.Source.Statement(snapshot.Span).StartsWith("RETURN")) Require(snapshot.Source.Statement(snapshot.Span).TrimEnd(';').EndsWith("@answer", StringComparison.Ordinal), "inclusive end maps final RETURN token");
                    Require(snapshots.Count < 100, "bounded stepping");
                    Require(snapshot.Stack[0].Span == snapshot.Span, "top frame carries current statement");
                    foreach (var caller in snapshot.Stack.Skip(1))
                    {
                        Require(caller.Span != null, "caller carries suspended statement");
                        string callSite = caller.Source.Statement(caller.Span!).Trim();
                        Require(caller.Level == 0 ? callSite.StartsWith("EXEC") :
                            caller.Source.StatementLine(caller.Span!) == (snapshot.Source.Name.EndsWith("_Double") ? 5 : 6),
                            "caller position stays on the function/procedure call site, including hidden Continue steps");
                    }
                    if (mode == "cancel") { session.Stop(); return; }
                    if (mode == "long-pause" && snapshot.Source.Name.EndsWith("_Double") && !longPauseRequested)
                    {
                        longPauseRequested = true;
                        log.WriteLine("Holding native pause for 30 seconds...");
                        _ = Task.Run(async () => { await Task.Delay(30000); session.Resume(DebugAction.Into); });
                        return;
                    }
                    if (mode == "over") { session.Resume(DebugAction.Over); return; }
                    if (mode is "breakpoint" or "line-breakpoint")
                    {
                        if (snapshot.Source.Name.EndsWith("_Double") && snapshot.Reason == "Breakpoint") { nestedBreakpoint = true; Require(snapshot.Line == 4, "breakpoint source maps to SET on line 4"); }
                        if (mode == "line-breakpoint" && snapshot.Reason == "Breakpoint") { nestedBreakpoint = true; Require(snapshot.Line == 4 && snapshot.Source.Statement(snapshot.Span).StartsWith("DECLARE"), "breakpoint binds DECLARE, not prior statement's indentation"); }
                        session.Resume(DebugAction.Continue); return;
                    }
                    if (mode == "out" && snapshot.Source.Name.EndsWith("_Double") && !requestedOut)
                    { requestedOut = true; outLevel = snapshot.Level; session.Resume(DebugAction.Out); return; }
                    if (requestedOut && !returnedOut) { Require(snapshot.Level < outLevel, "step out returns to caller"); returnedOut = true; }
                    session.Resume(DebugAction.Into);
                };
                Task task = session.RunAsync("", true);
                if (!task.Wait(TimeSpan.FromSeconds(40))) { session.Stop(); throw new Exception("Debugger test exceeded 40 seconds."); }
                task.GetAwaiter().GetResult();
                Require(snapshots.Count > 0, "native pause received");
                if (mode == "cancel") Require(results.Count == 0, "cancel does not execute remaining result statements");
                else
                {
                    Require(results.Count == 2, "both result sets collected");
                    Require(Convert.ToInt32(results[0].Rows[0]["Result"]) == 17, "real SQL result is 17");
                    if (mode is "into" or "repeat" or "long-pause")
                    {
                        Require(snapshots.Any(s => s.Source.Name.EndsWith("_Double")), "step into scalar function");
                        Require(snapshots.Any(s => s.Source.Name.EndsWith("_Inner")), "step into nested procedure");
                        Require(snapshots.Any(s => s.Variables.Any(v => v.Name == "@answer" && v.Value == "14")), "inspect function local value");
                        Require(snapshots.Any(s => s.Variables.Any(v => v.Name == "@result" && v.Value == "17")), "inspect OUTPUT parameter");
                    }
                    if (mode == "long-pause") Require(longPauseRequested, "sustained human pause exercised");
                    if (mode == "over") Require(snapshots.All(s => s.Level == 0), "step over hides callee statements");
                    if (mode == "out") Require(requestedOut && returnedOut, "step out exercised");
                    if (mode is "breakpoint" or "line-breakpoint") Require(nestedBreakpoint, "continue hits correct breakpoint");
                }
                log.WriteLine("PASS " + mode);
            }
            TestLiveMapping(connection, log);
            TestSourceMapping(log);
            using var cleanup = new SqlConnection(connection); cleanup.Open();
            using var cmd = cleanup.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tempdb.sys.objects WHERE name LIKE 'SqlLineDemo[_]%'";
            Require(Convert.ToInt32(cmd.ExecuteScalar()) == 0, "demo modules cleaned up");
            log.WriteLine("ALL TESTS PASSED"); return 0;
        }
        catch (Exception ex) { log.WriteLine(ex); return 1; }
    }
    private static void TestSourceMapping(TextWriter log)
    {
        const string text = "-- Unicode 🛰 and CRLF\r\n\r\nDECLARE @x int = 1;\r\nSELECT\r\n @x AS Value;";
        int start = text.IndexOf("SELECT", StringComparison.Ordinal);
        var source = new SourceDocument("mapping", "Mapping", "tempdb", text, Array.Empty<StatementSpan>());
        var span = new StatementSpan(1, start * 2, (text.Length - 1) * 2);
        Require(source.StatementLine(span) == 4, "UTF-16 offsets with Unicode and blank CRLF lines");
        Require(source.Statement(span) == "SELECT\r\n @x AS Value;", "exact multiline source text");
        var points = new BreakpointStore(); points.Toggle(source, 5);
        Require(points.Contains(source, span), "breakpoint on continuation line binds to statement");
        var changed = new SourceDocument("mapping", "Mapping", "tempdb", text + " ", source.Spans);
        Require(!points.Contains(changed, span), "source hash prevents drifting breakpoint binding");
        bool rejected = false;
        try { source.Range(new(1, 3, 100000)); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "invalid offsets fail instead of drifting");
        log.WriteLine("PASS UTF-16 / CRLF / multiline / source hash / invalid span mapping");
    }
    private static void TestLiveMapping(string connectionString, TextWriter log)
    {
        foreach (string newline in new[] { "\r\n", "\n" })
        {
            string sql = string.Join(newline, "-- Unicode 🛰 source offsets", "", "DECLARE @value int = 3;", "SET @value = @value + 4;", "SELECT", "    @value AS Value;");
            using var session = new DebugSession(connectionString, new());
            var seen = new List<PauseSnapshot>();
            var tables = new List<DataTable>();
            session.ResultSet += tables.Add;
            session.Paused += snapshot =>
            {
                seen.Add(snapshot);
                Require(snapshot.Source.Text == sql, "callback preserves exact submitted Unicode batch");
                string statement = snapshot.Source.Statement(snapshot.Span).Trim();
                Require(snapshot.Line switch { 3 => statement.StartsWith("DECLARE"), 4 => statement.StartsWith("SET"), 5 => statement.StartsWith("SELECT"), _ => false }, "live source maps to correct statement line despite Unicode, blank lines and line endings");
                session.Resume(DebugAction.Into);
            };
            var task = session.RunAsync(sql);
            Require(task.Wait(TimeSpan.FromSeconds(20)), "live mapping completes"); task.GetAwaiter().GetResult();
            Require(seen.Any(s => s.Line == 4) && seen.Any(s => s.Line == 5), "live SET and multiline SELECT stepped");
            Require(tables.Count == 1 && Convert.ToInt32(tables[0].Rows[0]["Value"]) == 7, "live mapped batch executes exactly once");
            log.WriteLine("PASS live Unicode / blank lines / multiline SQL / " + (newline == "\n" ? "LF" : "CRLF"));
        }
    }
}
