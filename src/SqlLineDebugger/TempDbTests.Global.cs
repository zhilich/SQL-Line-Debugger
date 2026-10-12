using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static partial class TempDbTests
{
    private static async Task TestGlobalCopies(TextWriter log)
    {
        Require(GlobalTempTableCopies.DefaultName("@Rows") == "dbg_Rows" && GlobalTempTableCopies.DefaultName("#Work__") == "dbg_Work__", "default name strips SQL sigils and preserves underscores");
        Require(GlobalTempTableCopies.NormalizeName(" dbg_Rows ") == "##dbg_Rows" && GlobalTempTableCopies.NormalizeName("##dbg_Rows") == "##dbg_Rows", "global prefix and trim");
        foreach (string invalid in new[] { "", "##", new string('a', 127), "a\nb" })
        {
            bool rejected = false; try { GlobalTempTableCopies.NormalizeName(invalid); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid global name rejected");
        }
        using var copies = new GlobalTempTableCopies();
        using var other = new SqlConnection(Connection); await other.OpenAsync();
        string prefix = "##SqlLineGlobalTest_" + Guid.NewGuid().ToString("N");
        string rowsName = prefix + "_Rows", tempName = prefix + "_Temp", emptyName = prefix + "_Empty", quotedName = prefix + "_]'quoted";
        async Task Execute(string sql) { using var cmd = other.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        async Task<int> Scalar(string sql) { using var cmd = other.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(await cmd.ExecuteScalarAsync()); }
        await Execute($"CREATE TABLE {SqlCatalog.Quote(rowsName)}(OldColumn int); INSERT {SqlCatalog.Quote(rowsName)} VALUES(999);");
        bool ready = false, changed = false;
        try
        {
            await RunScenario("""
                DECLARE @Rows table(Id int, Label nvarchar(80), Flag bit, Wide decimal(38,0), Bytes varbinary(max), Stamp datetime2(7), Zone datetimeoffset(7), LongText nvarchar(max));
                INSERT @Rows VALUES(1,N'original',1,99999999999999999999999999999999999999,0x001122FF,'2026-01-02T03:04:05.1234567','2026-01-02T03:04:05.1234567+05:30',REPLICATE(CONVERT(nvarchar(max),NCHAR(937)),10000)),
                    (1,N'original',1,99999999999999999999999999999999999999,0x001122FF,'2026-01-02T03:04:05.1234567','2026-01-02T03:04:05.1234567+05:30',REPLICATE(CONVERT(nvarchar(max),NCHAR(937)),10000)),(2,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
                DECLARE @Empty table(Id int, Label nvarchar(30));
                SELECT COUNT(*) FROM @Empty;
                CREATE TABLE #GlobalSource(Id int, Amount decimal(18,4));
                INSERT #GlobalSource VALUES(3,12.3456);
                CREATE TABLE #GlobalUnsupported(Value xml);
                INSERT #GlobalUnsupported VALUES(N'<test />');
                BEGIN TRANSACTION;
                UPDATE #GlobalSource SET Amount=17.5000;
                PRINT N'copy-ready';
                ROLLBACK TRANSACTION;
                UPDATE @Rows SET Label=N'changed' WHERE Id=1;
                PRINT N'copy-changed';
                """, async (session, snapshot) =>
            {
                string sql = snapshot.Source.Statement(snapshot.Span).TrimStart();
                if (!sql.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) return;
                var tables = await WaitTables(session, t => t.Any(x => x.Name == "@Rows") && t.Any(x => x.Name == "@Empty") && t.Any(x => x.Name == "#GlobalSource") && t.Any(x => x.Name == "#GlobalUnsupported"));
                if (sql.Contains("copy-ready"))
                {
                    var rows = tables.Single(t => t.Name == "@Rows");
                    var result = await session.CopyToGlobalAsync(rows, rowsName, copies);
                    Require(result.Name == rowsName && result.RowCount == 3 && session.IsPaused, "copy row count and target remains paused");
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)} WHERE Id=1 AND Label=N'original' AND Flag=1 AND Wide=99999999999999999999999999999999999999 AND Bytes=0x001122FF AND Stamp=CONVERT(datetime2(7),'2026-01-02T03:04:05.1234567') AND DATEPART(TZOFFSET,Zone)=330") == 2, "duplicates, native decimal(38), bit/binary and date/time offsets copied exactly");
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)} WHERE Id=2 AND Label IS NULL AND Flag IS NULL AND Wide IS NULL AND Bytes IS NULL AND Stamp IS NULL AND Zone IS NULL") == 1, "NULL tuple copied");
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)} WHERE LongText=REPLICATE(CONVERT(nvarchar(max),NCHAR(937)),10000) AND DATALENGTH(LongText)=20000") == 2, "off-row Unicode MAX copied in full");
                    Require(await Scalar($"SELECT COUNT(*) FROM tempdb.sys.columns WHERE object_id=OBJECT_ID(N'tempdb..{rowsName}') AND name=N'OldColumn'") == 0, "existing global table dropped and schema replaced");
                    Require((await session.CopyToGlobalAsync(tables.Single(t => t.Name == "#GlobalSource"), tempName, copies)).RowCount == 1, "temp table copy");
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(tempName)} WHERE Amount=17.5000") == 1, "uncommitted source captured from another connection");
                    Require((await session.CopyToGlobalAsync(tables.Single(t => t.Name == "@Empty"), emptyName, copies)).RowCount == 0 && await Scalar($"SELECT COUNT(*) FROM tempdb.sys.columns WHERE object_id=OBJECT_ID(N'tempdb..{emptyName}')") == 2, "empty table keeps schema");
                    Require((await session.CopyToGlobalAsync(rows, quotedName, copies)).RowCount == 3 && await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(quotedName)}") == 3, "identifier safely quoted");
                    bool unsupported = false;
                    try { await session.CopyToGlobalAsync(tables.Single(t => t.Name == "#GlobalUnsupported"), rowsName, copies); } catch (SqlException ex) { unsupported = ex.Message.Contains("Unsupported"); }
                    Require(unsupported && await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)}") == 3, "failed reader preserves previous copy");
                    bool stale = false;
                    try { await session.CopyToGlobalAsync(rows with { Created = rows.Created.AddDays(-1) }, rowsName, copies); } catch (SqlException) { stale = true; }
                    Require(stale && await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)}") == 3, "stale object rejects copy before dropping destination");
                    ready = true;
                }
                else if (sql.Contains("copy-changed"))
                {
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)} WHERE Label=N'original'") == 2, "global snapshot never auto-refreshes");
                    await session.CopyToGlobalAsync(tables.Single(t => t.Name == "@Rows"), rowsName, copies);
                    Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)} WHERE Label=N'changed'") == 2, "repeated copy replaces data");
                    changed = true;
                }
            }, log);
            Require(ready && changed, "all global copy checkpoints visited");
            Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(rowsName)}") == 3 && await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(tempName)} WHERE Amount=17.5000") == 1, "copies survive source rollback and debug session end");
            copies.Dispose();
            Require(await Scalar($"SELECT COUNT(*) FROM tempdb.sys.tables WHERE name LIKE N'{prefix}%'") == 0, "retained creator connections close and globals disappear");
            log.WriteLine("PASS global naming, real paused table-variable/temp-table copying, replacement/schema, duplicates, NULL, precision 38, binary, temporal offsets, uncommitted snapshots, empty schema, quoted identifiers, failure/stale protection, immutable copies and app-lifetime cleanup");
        }
        finally { foreach (string name in new[] { rowsName, tempName, emptyName, quotedName }) await Execute("DROP TABLE IF EXISTS " + SqlCatalog.Quote(name)); }
    }
}
