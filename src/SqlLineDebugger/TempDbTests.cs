using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static partial class TempDbTests
{
    private static readonly string Connection = SqlCatalog.ConnectionString(@"(localdb)\mssqllocaldb", "Test");
    internal static int Run(string path, bool stepOverOnly = false, bool typesOnly = false, bool globalOnly = false)
    {
        using var log = new StreamWriter(path) { AutoFlush = true };
        try
        {
            if (globalOnly)
            {
                TestGlobalCopies(log).GetAwaiter().GetResult();
                log.WriteLine("ALL GLOBAL COPY TESTS PASSED");
                return 0;
            }
            if (typesOnly)
            {
                TestTypesLab(log).GetAwaiter().GetResult();
                log.WriteLine("ALL TEMPDB TYPE TESTS PASSED");
                return 0;
            }
            if (stepOverOnly)
            {
                TestStepOverNames(log).GetAwaiter().GetResult();
                log.WriteLine("ALL TEMPDB STEP OVER TESTS PASSED");
                return 0;
            }
            Require(SqlTableReferences.Variables("SELECT '@fake' AS [@fake] FROM @actual /* JOIN @ignored */ WHERE x=@scalar").SetEquals(new[] { "@actual" }), "SQL tokens exclude literals, quoted identifiers, comments and scalars");
            Require(TempDbTracker.DisplayTempName("#some__name" + new string('_', 60) + "000000000123") == "#some__name", "suffix removal preserves name underscores");
            Require(TempDbTracker.DisplayTempName("##global__123456ABCDEF") == "##global__123456ABCDEF", "global names have no generated suffix");
            TestMappingAndSnapshots(log).GetAwaiter().GetResult();
            TestTransactionAndTypes(log).GetAwaiter().GetResult();
            TestTypesLab(log).GetAwaiter().GetResult();
            TestGlobalCopies(log).GetAwaiter().GetResult();
            TestNestedScopes(log).GetAwaiter().GetResult();
            TestStepOverNames(log).GetAwaiter().GetResult();
            TestFunctionAndSample(log).GetAwaiter().GetResult();
            TestCancellation(log).GetAwaiter().GetResult();
            using var cleanup = new SqlConnection(Connection); cleanup.Open();
            using var command = cleanup.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.server_event_sessions WHERE name LIKE 'SqlLineDebugger[_]TempDB[_]%'";
            Require(Convert.ToInt32(command.ExecuteScalar()) == 0, "XE sessions removed after completion/cancellation");
            log.WriteLine("ALL TEMPDB TESTS PASSED"); return 0;
        }
        catch (Exception ex) { log.WriteLine(ex); return 1; }
    }

    private static async Task TestStepOverNames(TextWriter log)
    {
        using var setup = new SqlConnection(Connection); await setup.OpenAsync();
        using var check = setup.CreateCommand(); check.CommandText = "SELECT OBJECT_ID(N'SqlDebugLab.Run')";
        if (await check.ExecuteScalarAsync() is int)
            await Verify("EXEC SqlDebugLab.Run @RowCount=3;", "EXEC SqlDebugLab.ProcessWork");
        else log.WriteLine("SKIP SqlDebugLab.ProcessWork reproduction (sample absent)");

        async Task Verify(string sql, string call)
        {
            using var session = new DebugSession(Connection, new());
            var expected = new[] { "@Left", "@Right", "@rows" };
            Dictionary<int, string>? before = null;
            Exception? failure = null;
            bool steppedOver = false, checkedNames = false;
            int loss = 0;
            var callbacks = new List<Task>();
            session.Output += text =>
            {
                log.WriteLine(text);
                if (text.Contains("XE buffer lost events", StringComparison.Ordinal)) Interlocked.Increment(ref loss);
            };
            session.Paused += snapshot => callbacks.Add(Task.Run(async () =>
            {
                try
                {
                    string statement = snapshot.Source.Statement(snapshot.Span);
                    if (!steppedOver && statement.Contains(call, StringComparison.Ordinal))
                    {
                        var tables = await WaitTables(session, t => expected.All(n => t.Any(x => x.Name == n)));
                        before = tables.Where(t => expected.Contains(t.Name)).ToDictionary(t => t.ObjectId, t => t.Name);
                        Require(before.Count == 3, "three caller variables resolved before Step Over");
                        steppedOver = true;
                        session.Resume(DebugAction.Over);
                    }
                    else if (steppedOver)
                    {
                        // Allow delayed XE dispatch to finish while the caller is paused.
                        // Check every update so a transient loss is not masked by re-resolution.
                        for (int i = 0; i < 40; i++)
                        {
                            await Task.Delay(50);
                            var tables = session.TempTables;
                            Require(before!.All(pair => tables.Any(t => t.ObjectId == pair.Key && t.Name == pair.Value)),
                                "caller variable names remain resolved after Step Over: " + string.Join(", ", tables.Select(t => t.Name)));
                        }
                        foreach (var pair in before!)
                        {
                            var table = session.TempTables.Single(t => t.ObjectId == pair.Key);
                            Require((await session.InspectAsync(table)).Rows.Count > 0, "preserved mapping still inspects the live caller object");
                        }
                        checkedNames = true;
                        session.Stop();
                    }
                    else session.Resume(DebugAction.Into);
                }
                catch (Exception ex) { failure = ex; session.Stop(); }
            }));
            var run = session.RunAsync(sql);
            if (await Task.WhenAny(run, Task.Delay(60000)) != run) { session.Stop(); await run; throw new Exception("Step Over name test timed out."); }
            await run;
            await Task.WhenAll(callbacks);
            if (failure != null) throw failure;
            Require(steppedOver && checkedNames, "Step Over checkpoint visited");
            log.WriteLine($"PASS {call}: caller names/object IDs preserved, snapshots inspectable; XE loss notifications={loss}");
        }
    }

    private static async Task TestMappingAndSnapshots(TextWriter log)
    {
        using var other = new SqlConnection(Connection); await other.OpenAsync();
        using (var command = other.CreateCommand()) { command.CommandText = "CREATE TABLE #InspectTemp(Id int,Label nvarchar(80)); INSERT #InspectTemp VALUES(999,N'other session');"; await command.ExecuteNonQueryAsync(); }
        DataTable? first = null;
        TempDbObject? dropped = null;
        bool ambiguous = false, resolved = false, changed = false, removed = false;
        await RunScenario("""
            DECLARE @Left table(Id int,Label nvarchar(80));
            DECLARE @Right table(Id int,Label nvarchar(80));
            SELECT COUNT(*) FROM @Left a CROSS JOIN @Right b;
            PRINT N'ambiguous';
            SELECT COUNT(*) FROM @Left;
            PRINT N'resolved';
            INSERT @Left VALUES(1,N'alpha Ω'),(1,N'alpha Ω'),(2,NULL);
            INSERT @Right VALUES(3,N'right');
            CREATE TABLE #InspectTemp(Id int,Label nvarchar(80));
            INSERT #InspectTemp VALUES(4,N'temp'),(5,N'delete me');
            PRINT N'ready';
            UPDATE @Left SET Label=N'changed' WHERE Id=1;
            DELETE #InspectTemp WHERE Id=5;
            PRINT N'changed';
            DROP TABLE #InspectTemp;
            PRINT N'dropped';
            SELECT COUNT(*) AS ActualCount FROM @Left;
            """, async (session, snapshot) =>
        {
            string statement = snapshot.Source.Statement(snapshot.Span);
            if (!statement.TrimStart().StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) return;
            if (statement.Contains("N'ambiguous'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Count == 2);
                Require(tables.All(t => t.Name == t.InternalName), "ambiguous first use retains both internal names");
                ambiguous = true;
            }
            else if (statement.Contains("N'resolved'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Any(x => x.Name == "@Left") && t.Any(x => x.Name == "@Right"));
                var empty = await session.InspectAsync(tables.Single(t => t.Name == "@Right"));
                Require(empty.Rows.Count == 0 && empty.Columns.Count == 2, "empty table retains column schema");
                resolved = true;
            }
            else if (statement.Contains("N'ready'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Count == 3 && t.Any(x => x.Name == "#InspectTemp"));
                Require(tables.Select(t => t.LastLockSequence).SequenceEqual(tables.Select(t => t.LastLockSequence).OrderByDescending(s => s)), "objects sorted by most recent lock");
                first = await session.InspectAsync(tables.Single(t => t.Name == "@Left"));
                Require(first.Rows.Count == 3 && first.Rows.Cast<DataRow>().Count(r => r["Label"] as string == "alpha Ω") == 2 && first.Rows.Cast<DataRow>().Count(r => r["Label"] == DBNull.Value) == 1,
                    "snapshot preserves tuples, duplicates, Unicode and NULL");
                dropped = tables.Single(t => t.Name == "#InspectTemp");
                var temp = await session.InspectAsync(dropped);
                Require(temp.Rows.Count == 2 && !temp.Rows.Cast<DataRow>().Any(r => Convert.ToInt32(r["Id"]) == 999), "SPID filtering excludes identically named table in another connection");
            }
            else if (statement.Contains("N'changed'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Count == 3 && t.First().Name == "#InspectTemp");
                var second = await session.InspectAsync(tables.Single(t => t.Name == "@Left"));
                Require(second.Rows.Cast<DataRow>().Count(r => r["Label"] as string == "changed") == 2 && first!.Rows.Cast<DataRow>().Count(r => r["Label"] as string == "alpha Ω") == 2,
                    "new inspection sees updates while previous snapshot stays unchanged");
                Require((await session.InspectAsync(tables.Single(t => t.Name == "#InspectTemp"))).Rows.Count == 1, "snapshot excludes deleted records");
                changed = true;
            }
            else if (statement.Contains("N'dropped'", StringComparison.Ordinal))
            {
                await WaitTables(session, t => t.All(x => x.Name != "#InspectTemp"));
                bool rejected = false;
                try { await session.InspectAsync(dropped!); } catch (SqlException) { rejected = true; }
                Require(rejected, "dropped object rejects stale inspection instead of reading another object");
                removed = true;
            }
        }, log);
        Require(ambiguous && resolved && first != null && changed && removed, "all mapping/snapshot checkpoints visited");
        log.WriteLine("PASS ambiguous mapping -> resolution, internal names, empty schema, duplicates, Unicode/NULL, cross-session isolation, updates/deletes, immutable snapshots, sort order and stale objects");
    }

    private static async Task TestTransactionAndTypes(TextWriter log)
    {
        bool inspected = false, rolledBack = false;
        await RunScenario("""
            BEGIN TRAN;
            CREATE TABLE #TypeFixture__(Id int PRIMARY KEY,Small smallint,Tiny tinyint,Big bigint,Flag bit,
                Amount decimal(18,4),R real,F float,Guid uniqueidentifier,TextValue nvarchar(max),Ansi varchar(50),Bytes varbinary(max),Wide decimal(38,0));
            INSERT #TypeFixture__ VALUES(1,-123,255,9223372036854775806,1,-123456.7890,1.25,-2.5,
                '00112233-4455-6677-8899-AABBCCDDEEFF',REPLICATE(CONVERT(nvarchar(max),N'Ω'),10000),'abc',0x001122FF,99999999999999999999999999999999999999);
            PRINT N'types-ready';
            ROLLBACK;
            PRINT N'rollback-done';
            CREATE TABLE #UnsupportedDate(Value xml);
            INSERT #UnsupportedDate VALUES(N'<test />');
            PRINT N'unsupported-ready';
            """, async (session, snapshot) =>
        {
            string statement = snapshot.Source.Statement(snapshot.Span);
            if (!statement.TrimStart().StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) return;
            if (statement.Contains("N'types-ready'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Any(x => x.Name == "#TypeFixture__"));
                var rows = await session.InspectAsync(tables.Single(t => t.Name == "#TypeFixture__"));
                var row = rows.Rows[0];
                Require(rows.Rows.Count == 1 && (int)row["Id"] == 1 && (short)row["Small"] == -123 && (byte)row["Tiny"] == 255 && (long)row["Big"] == 9223372036854775806,
                    "clustered rowstore and integer widths decode correctly during uncommitted transaction");
                Require((bool)row["Flag"] && (decimal)row["Amount"] == -123456.7890m && (float)row["R"] == 1.25f && (double)row["F"] == -2.5,
                    "bit, decimal, real and float decode correctly");
                Require((Guid)row["Guid"] == Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF") && (string)row["TextValue"] == new string('Ω', 10000) && (string)row["Ansi"] == "abc" && ((byte[])row["Bytes"]).SequenceEqual(new byte[] { 0, 17, 34, 255 }),
                    "GUID, ANSI, off-row Unicode LOB and binary decode correctly");
                Require(row["Wide"].ToString() == "99999999999999999999999999999999999999", "SQL decimal(38) avoids CLR decimal overflow");
                inspected = true;
            }
            if (statement.Contains("N'rollback-done'", StringComparison.Ordinal))
            { await WaitTables(session, t => t.All(x => x.Name != "#TypeFixture__")); rolledBack = true; }
            if (statement.Contains("N'unsupported-ready'", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Any(x => x.Name == "#UnsupportedDate"));
                bool rejected = false;
                try { await session.InspectAsync(tables.Single(t => t.Name == "#UnsupportedDate")); }
                catch (SqlException ex) { rejected = ex.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase); }
                Require(rejected, "unsupported decoder formats produce a clear error");
            }
        }, log);
        Require(inspected && rolledBack, "transaction/type checkpoints visited");
        log.WriteLine("PASS uncommitted transaction, rollback, clustered index, supported numeric/text/binary/GUID types, off-row LOB and explicit unsupported-layout error");
    }

    private static async Task TestNestedScopes(TextWriter log)
    {
        string name = "SqlLineTempTest_" + Guid.NewGuid().ToString("N");
        using var setup = new SqlConnection(Connection); await setup.OpenAsync();
        async Task Execute(string sql) { using var cmd = setup.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        await Execute($"CREATE PROCEDURE dbo.[{name}_Child] AS BEGIN DECLARE @rows table(Id int); INSERT @rows VALUES(20); CREATE TABLE #ChildTemp(Id int); INSERT #ChildTemp VALUES(30); SELECT COUNT(*) FROM #ParentTemp; PRINT N'nested-ready'; END");
        await Execute($"CREATE PROCEDURE dbo.[{name}_Parent] AS BEGIN DECLARE @rows table(Id int); INSERT @rows VALUES(10); CREATE TABLE #ParentTemp(Id int); INSERT #ParentTemp VALUES(40); EXEC dbo.[{name}_Child]; PRINT N'child-returned'; EXEC dbo.[{name}_Child]; PRINT N'child-returned'; END");
        int nested = 0, returned = 0;
        try
        {
            await RunScenario($"EXEC dbo.[{name}_Parent];", async (session, snapshot) =>
            {
                string statement = snapshot.Source.Statement(snapshot.Span);
                if (!statement.TrimStart().StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) return;
                if (statement.Contains("N'nested-ready'", StringComparison.Ordinal))
                {
                    var tables = await WaitTables(session, t => t.Count == 4 && t.Count(x => x.Name == "@rows") == 2);
                    var values = new List<int>();
                    foreach (var table in tables.Where(t => t.Name == "@rows")) values.Add((int)(await session.InspectAsync(table)).Rows[0][0]);
                    Require(values.OrderBy(v => v).SequenceEqual(new[] { 10, 20 }), "shadowed table variable names retain separate object identities");
                    Require((await session.InspectAsync(tables.Single(t => t.Name == "#ParentTemp"))).Rows.Count == 1, "child can inspect inherited parent temp table");
                    nested++;
                }
                if (statement.Contains("N'child-returned'", StringComparison.Ordinal))
                {
                    var tables = await WaitTables(session, t => t.Count == 2 && t.All(x => x.Name != "#ChildTemp"));
                    Require((int)(await session.InspectAsync(tables.Single(t => t.Name == "@rows"))).Rows[0][0] == 10, "return removes child scope and restores parent variable");
                    returned++;
                }
            }, log);
            Require(nested == 2 && returned == 2, "repeated nested calls/cache reuse tested");
        }
        finally { await Execute($"DROP PROCEDURE dbo.[{name}_Parent]; DROP PROCEDURE dbo.[{name}_Child];"); }
        log.WriteLine("PASS nested/shadowed variables, inherited temp table, scope exit and repeated calls/object caching");
    }

    private static async Task TestCancellation(TextWriter log)
    {
        using var session = new DebugSession(Connection, new());
        session.Output += text => log.WriteLine(text);
        session.Paused += _ => session.Stop();
        await session.RunAsync("CREATE TABLE #CancelTemp(Id int); INSERT #CancelTemp VALUES(1);");
        bool rejected = false;
        try { await session.InspectAsync(new(0, "#invalid", "#invalid", DateTime.Now, 0)); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "inspection is rejected when debugger is not paused");
        using var failed = new DebugSession(Connection, new());
        failed.Paused += _ => failed.Resume(DebugAction.Into);
        bool sqlFailed = false;
        try { await failed.RunAsync("CREATE TABLE #FailedTemp(Id int); THROW 51001, 'test SQL failure', 1;"); }
        catch (InvalidOperationException ex) { sqlFailed = ex.Message.Contains("test SQL failure", StringComparison.Ordinal); }
        Require(sqlFailed, "SQL execution failure propagated after XE cleanup");
        log.WriteLine("PASS cancel and pause-only inspection");
    }

    private static async Task TestFunctionAndSample(TextWriter log)
    {
        string name = "SqlLineTempTest_" + Guid.NewGuid().ToString("N") + "_Function";
        using var setup = new SqlConnection(Connection); await setup.OpenAsync();
        async Task Execute(string sql) { using var cmd = setup.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        await Execute($"CREATE FUNCTION dbo.[{name}](@id int) RETURNS int AS BEGIN DECLARE @data table(Id int,Label nvarchar(50)); INSERT @data VALUES(@id,N'function'); DECLARE @answer int; SELECT @answer=SUM(Id) FROM @data; RETURN @answer; END");
        bool inspected = false;
        try
        {
            await RunScenario($"SELECT dbo.[{name}](7) AS Value;", async (session, snapshot) =>
            {
                if (snapshot.Source.Name.EndsWith("_Function", StringComparison.Ordinal) && snapshot.Source.Statement(snapshot.Span).StartsWith("RETURN", StringComparison.Ordinal))
                {
                    var tables = await WaitTables(session, t => t.Count == 1);
                    var rows = await session.InspectAsync(tables[0]);
                    Require(rows.Rows.Count == 1 && (int)rows.Rows[0]["Id"] == 7 && (string)rows.Rows[0]["Label"] == "function", "table variable inside scalar function inspected");
                    inspected = true;
                }
            }, log);
        }
        finally { await Execute($"DROP FUNCTION dbo.[{name}];"); }
        Require(inspected, "function table inspection exercised");
        log.WriteLine("PASS table variable inside native-stepped scalar function");

        using var check = setup.CreateCommand(); check.CommandText = "SELECT OBJECT_ID(N'SqlDebugTest.Run')";
        if (await check.ExecuteScalarAsync() is not int) { log.WriteLine("SKIP installed 16 KiB sample (SqlDebugTest.Run absent)"); return; }
        bool sampleInspected = false;
        var results = new List<DataTable>();
        await RunScenario("EXEC SqlDebugTest.Run @Seed=7,@OrderCount=3;", async (session, snapshot) =>
        {
            if (!sampleInspected && snapshot.Source.Name.EndsWith("AssessRisk", StringComparison.Ordinal) && snapshot.Source.Statement(snapshot.Span).StartsWith("RETURN", StringComparison.Ordinal))
            {
                var tables = await WaitTables(session, t => t.Any(x => x.Name == "#Orders") && t.Any(x => x.Name == "#Lines") && t.Any(x => x.Name == "#Trace"));
                Require((await session.InspectAsync(tables.Single(t => t.Name == "#Orders"))).Rows.Count == 3, "large sample orders inspected inside nested leaf");
                Require((await session.InspectAsync(tables.Single(t => t.Name == "#Lines"))).Rows.Count == 9, "large sample lines inspected inside nested leaf");
                Require((await session.InspectAsync(tables.Single(t => t.Name == "#Trace"))).Rows.Count > 0, "large sample trace inspected inside nested leaf");
                sampleInspected = true;
            }
        }, log, table => results.Add(table));
        Require(sampleInspected && results.Count == 4 && Convert.ToDecimal(results[0].Rows[0]["GrandTotal"]) == 748.12m, "inspection leaves large sample execution/results unchanged");
        log.WriteLine("PASS installed 16 KiB nested sample: Orders/Lines/Trace snapshots at leaf pause and unchanged four result sets/grand total");
    }

    internal static async Task<IReadOnlyList<TempDbObject>> WaitTables(DebugSession session, Func<IReadOnlyList<TempDbObject>, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate(session.TempTables))
        {
            try { await Task.Delay(50, deadline.Token); }
            catch (OperationCanceledException) { throw new Exception("TempDB discovery timeout: " + string.Join(", ", session.TempTables.Select(t => $"{t.Name}/{t.ObjectId}"))); }
        }
        return session.TempTables;
    }

    private static async Task RunScenario(string sql, Func<DebugSession, PauseSnapshot, Task> inspect, TextWriter log, Action<DataTable>? result = null)
    {
        using var session = new DebugSession(Connection, new());
        Exception? failure = null;
        var callbacks = new List<Task>();
        session.Output += text => log.WriteLine(text);
        if (result != null) session.ResultSet += result;
        session.Paused += snapshot =>
        {
            callbacks.Add(Task.Run(async () =>
            {
                try
                {
                    await inspect(session, snapshot);
                    session.Resume(DebugAction.Into);
                }
                catch (Exception ex) { failure = ex; session.Stop(); }
            }));
        };
        var run = session.RunAsync(sql);
        if (await Task.WhenAny(run, Task.Delay(90000)) != run) { session.Stop(); throw new Exception("TempDB scenario exceeded 90 seconds."); }
        await run;
        await Task.WhenAll(callbacks);
        if (failure != null) throw failure;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
}
