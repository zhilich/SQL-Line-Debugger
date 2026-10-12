using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static partial class TempDbTests
{
    private static async Task TestMoneyLayouts(TextWriter log)
    {
        using var owner = new SqlConnection(Connection); await owner.OpenAsync();
        const string columns = "Id int, Label nvarchar(40), Amount money, SmallAmount smallmoney, Flag bit, Bytes varbinary(10)";
        const string values = """
            (1,N'zero',0,0,1,0x),
            (2,N'positive',123456.7890,123456.7890,0,0x00FF),
            (3,N'negative',-123456.7890,-123456.7890,1,0x1122),
            (4,N'minimum',-922337203685477.5808,-214748.3648,NULL,NULL),
            (5,N'maximum',922337203685477.5807,214748.3647,1,0xFF),
            (6,N'fraction',0.0001,-0.0001,0,0x01),
            (7,N'fraction',-0.0001,0.0001,0,0x01),
            (8,NULL,NULL,NULL,NULL,NULL),
            (9,N'rounding',123456.78905,-123456.78905,1,0x00),
            (10,N'low word maximum',429496.7295,1.2345,0,0xFF),
            (11,N'word boundary',429496.7296,-1.2345,0,0xFF),
            (12,N'negative word boundary',-429496.7296,-1.2345,1,0xFF),
            (13,N'fraction',-0.0001,0.0001,0,0x01)
            """;
        async Task Execute(string sql) { using var cmd = owner.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        async Task<DataTable> Select(string name)
        {
            using var cmd = owner.CreateCommand(); cmd.CommandText = "SELECT * FROM " + SqlCatalog.Quote(name);
            using var reader = await cmd.ExecuteReaderAsync(); var rows = new DataTable(); rows.Load(reader); return rows;
        }
        async Task<TempDbObject> Metadata(string name)
        {
            using var cmd = owner.CreateCommand();
            cmd.CommandText = "SELECT object_id,name,create_date FROM tempdb.sys.tables WHERE object_id=OBJECT_ID(@name)";
            cmd.Parameters.AddWithValue("@name", "tempdb.." + name);
            using var reader = await cmd.ExecuteReaderAsync(); Require(await reader.ReadAsync(), "money fixture metadata");
            return new(reader.GetInt32(0), reader.GetString(1), name, reader.GetDateTime(2), 0);
        }
        static void Compare(DataTable expected, DataTable actual, string context)
        {
            Require(actual.Rows.Count == expected.Rows.Count && actual.Columns.Count == expected.Columns.Count, context + " exact counts");
            for (int c = 0; c < expected.Columns.Count; c++)
                Require(actual.Columns[c].DataType == expected.Columns[c].DataType && actual.Columns[c].ColumnName == expected.Columns[c].ColumnName, context + " native column types/names");
            var remaining = actual.Rows.Cast<DataRow>().ToList();
            foreach (DataRow row in expected.Rows)
            {
                int match = remaining.FindIndex(r => Enumerable.Range(0, expected.Columns.Count).All(c => row[c] is byte[] a && r[c] is byte[] b ? a.SequenceEqual(b) : Equals(row[c], r[c])));
                Require(match >= 0, context + " exact tuple: " + string.Join(",", row.ItemArray));
                remaining.RemoveAt(match);
            }
        }
        async Task Verify(string name)
        {
            var actual = await TempDbInspector.ReadAsync(Connection, await Metadata(name));
            Compare(await Select(name), actual, name);
            Require(actual.Columns["Amount"]!.ExtendedProperties["SqlType"] as string == "money" && actual.Columns["SmallAmount"]!.ExtendedProperties["SqlType"] as string == "smallmoney", "native SQL monetary types");
            log.WriteLine($"PASS {name}: {actual.Rows.Count} exact tuples match owner SELECT, native money/smallmoney metadata");
        }

        await Execute($"CREATE TABLE #MoneyHeap({columns}); INSERT #MoneyHeap VALUES {values};");
        await Verify("#MoneyHeap");
        await Execute("SELECT * INTO #MoneyClustered FROM #MoneyHeap; CREATE CLUSTERED INDEX IX_Money ON #MoneyClustered(Amount DESC,SmallAmount);");
        await Verify("#MoneyClustered");
        await Execute($"CREATE TABLE #MoneyEmpty({columns});");
        await Verify("#MoneyEmpty");
        await Execute("BEGIN TRANSACTION; UPDATE #MoneyHeap SET Amount=-922337203685477.5808,SmallAmount=214748.3647 WHERE Id=2;");
        try { await Verify("#MoneyHeap"); } finally { await Execute("ROLLBACK TRANSACTION;"); }
        await Verify("#MoneyHeap");

        using var copies = new GlobalTempTableCopies();
        string global = "##MoneyTest_" + Guid.NewGuid().ToString("N");
        await copies.CopyAsync(Connection, await Metadata("#MoneyClustered"), global);
        Compare(await Select("#MoneyClustered"), await Select(global), "global money snapshot");
        using (var schema = owner.CreateCommand())
        {
            schema.CommandText = "SELECT COUNT(*) FROM tempdb.sys.columns WHERE object_id=OBJECT_ID(@name) AND ((name='Amount' AND system_type_id=60) OR (name='SmallAmount' AND system_type_id=122))";
            schema.Parameters.AddWithValue("@name", "tempdb.." + global);
            Require(Convert.ToInt32(await schema.ExecuteScalarAsync()) == 2, "Global preserves money/smallmoney SQL column types");
        }

        var expectedRows = await Select("#MoneyHeap");
        bool inspected = false;
        await RunScenario($"DECLARE @MoneyRows table({columns}); INSERT @MoneyRows VALUES {values}; PRINT N'money-ready';", async (session, snapshot) =>
        {
            string sql = snapshot.Source.Statement(snapshot.Span).TrimStart();
            if (!sql.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase) || !sql.Contains("money-ready")) return;
            var tables = await WaitTables(session, t => t.Any(x => x.Name == "@MoneyRows"));
            var table = tables.Single(t => t.Name == "@MoneyRows");
            Compare(expectedRows, await session.InspectAsync(table), "paused money table variable");
            await session.CopyToGlobalAsync(table, global, copies);
            Compare(expectedRows, await Select(global), "global money table variable");
            Require(session.IsPaused, "money inspection/copy keeps target paused");
            inspected = true;
        }, log);
        Require(inspected, "money table-variable checkpoint inspected");
        log.WriteLine("PASS money/smallmoney heaps, non-unique monetary clustered keys, positive/negative/zero/NULL values, signed limits, four-place fractions, rounding, word boundaries, duplicate values, empty schema, uncommitted changes/rollback, paused table variables and Global copies");
    }
}
