using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static partial class TempDbTests
{
    private static async Task TestTypesLab(TextWriter log)
    {
        await TestMoneyLayouts(log);
        using (var setup = new SqlConnection(Connection))
        {
            await setup.OpenAsync();
            using var check = setup.CreateCommand(); check.CommandText = "SELECT OBJECT_ID(N'SqlDebugLab.TypesLab')";
            if (await check.ExecuteScalarAsync() is not int)
            {
                log.WriteLine("SKIP installed TypesLab (sample absent)");
                await TestTemporalLayouts(log);
                return;
            }
        }
        var failures = new List<string>();
        var visited = new HashSet<string>();
        await RunScenario("""
            CREATE TABLE #Snapshots(Sequence int IDENTITY PRIMARY KEY, ModuleName nvarchar(40),
                [Checkpoint] nvarchar(80), [RowCount] int, Note nvarchar(160));
            EXEC SqlDebugLab.TypesLab @IncludeUnsupported=1;
            """, async (session, snapshot) =>
        {
            string sql = snapshot.Source.Statement(snapshot.Span).TrimStart();
            if (!sql.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase)) return;
            string[] expected = sql.Contains("Y1 TypesLab") ? ["#Types", "@binary"]
                : sql.Contains("Y2 TypesLab") ? ["#ManyRows"]
                : sql.Contains("Y3 TypesLab") ? ["#UnsupportedDate"]
                : sql.Contains("Y4 TypesLab") ? ["#NonUniqueClustered"] : [];
            if (expected.Length == 0) return;
            var tables = await WaitTables(session, t => expected.All(n => t.Any(x => x.Name == n)));
            foreach (string name in expected)
            {
                visited.Add(name);
                try
                {
                    var rows = await session.InspectAsync(tables.Single(t => t.Name == name));
                    if (name == "#Types")
                    {
                        Require(rows.Rows.Count == 2, "#Types row count");
                        var row = rows.Rows.Cast<DataRow>().Single(r => (int)r["Id"] == 1);
                        Require((short)row["SmallValue"] == -123 && (byte)row["TinyValue"] == 255 && (long)row["BigValue"] == 9223372036854775806 && (bool)row["Flag"], "#Types integer/bit values");
                        Require((decimal)row["Amount"] == -123456.7890m && (float)row["RealValue"] == 1.25f && (double)row["FloatValue"] == -2.5, "#Types numeric values");
                        Require((Guid)row["GuidValue"] == Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF") && (string)row["FixedText"] == "fixed   " && (string)row["AnsiText"] == "ASCII text", "#Types GUID and fixed/ANSI text");
                        Require((string)row["UnicodeText"] == new string('Ω', 10000) && ((byte[])row["Bytes"]).SequenceEqual(new byte[] { 0, 17, 34, 255 }) && row["WideValue"].ToString() == new string('9', 38), "#Types MAX Unicode, binary, decimal(38)");
                        var nullRow = rows.Rows.Cast<DataRow>().Single(r => (int)r["Id"] == 2);
                        Require(rows.Columns.Cast<DataColumn>().Skip(1).All(c => nullRow.IsNull(c)), "#Types NULL row (including SqlDecimal.Null)");
                    }
                    else if (name == "@binary")
                        Require(rows.Rows.Count == 1 && ((byte[])rows.Rows[0]["FixedBytes"]).SequenceEqual(new byte[] { 0, 255, 17, 34 }) && (string)rows.Rows[0]["ShortText"] == "Unicode: Ω 漢", "@binary fixed bytes and Unicode");
                    else if (name == "#ManyRows")
                        Require(rows.Rows.Count == 200 && rows.Rows.Cast<DataRow>().Select(r => (int)r["Id"]).Order().SequenceEqual(Enumerable.Range(1, 200)) && rows.Rows.Cast<DataRow>().All(r => (string)r["Payload"] == new string('x', 100)), "#ManyRows exact tuples across pages");
                    else if (name == "#UnsupportedDate")
                        Require(rows.Rows.Count == 1 && (DateTime)rows.Rows[0]["Value"] == new DateTime(2026, 1, 2, 3, 4, 5, 6), "datetime2(3) value and native type");
                    else if (name == "#NonUniqueClustered")
                        Require(rows.Rows.Count == 2 && rows.Rows.Cast<DataRow>().All(r => (int)r["Id"] == 1) && rows.Rows.Cast<DataRow>().Select(r => (string)r["Label"]).Order().SequenceEqual(new[] { "first", "second" }), "non-unique clustered duplicates");
                    log.WriteLine($"PASS TypesLab {name}: {rows.Rows.Count} rows; " + string.Join(", ", rows.Columns.Cast<DataColumn>().Select(c => c.ColumnName + ":" + c.DataType.Name)));
                }
                catch (Exception ex) { failures.Add(name + ": " + ex.Message); log.WriteLine($"FAIL TypesLab {name}: {ex}"); }
            }
        }, log);
        Require(visited.SetEquals(new[] { "#Types", "@binary", "#ManyRows", "#UnsupportedDate", "#NonUniqueClustered" }), "all TypesLab checkpoints visited");
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        await TestTemporalLayouts(log);
    }

    private static async Task TestTemporalLayouts(TextWriter log)
    {
        using var owner = new SqlConnection(Connection); await owner.OpenAsync();
        async Task Execute(string sql) { using var cmd = owner.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        string columns = string.Join(",", Enumerable.Range(0, 8).SelectMany(s => new[] { $"T{s} time({s})", $"D{s} datetime2({s})", $"O{s} datetimeoffset({s})" }));
        await Execute($"CREATE TABLE #TemporalLayouts(Id int PRIMARY KEY, Day date, Legacy datetime, SmallLegacy smalldatetime,{columns});");
        // Every scale, negative legacy day counts, nonzero offsets crossing UTC
        // midnight, NULLs, and endpoints that must not round into the next day.
        var cases = new[]
        {
            (Date: "2026-01-02", Time: "03:04:05", Fraction: "1234567", Zone: "+05:30", Legacy: "2026-01-02T03:04:05.997", Small: "2026-01-02T03:04:00"),
            (Date: "0001-01-01", Time: "00:00:00", Fraction: "0000000", Zone: "+00:00", Legacy: "1753-01-01T00:00:00.000", Small: "1900-01-01T00:00:00"),
            (Date: "9999-12-31", Time: "23:59:59", Fraction: "9999999", Zone: "+00:00", Legacy: "9999-12-31T23:59:59.997", Small: "2079-06-06T23:59:00"),
            (Date: "1899-12-31", Time: "23:55:11", Fraction: "7654321", Zone: "-03:30", Legacy: "1899-12-31T23:55:11.003", Small: "2020-02-29T12:30:00"),
            (Date: "2020-02-29", Time: "01:15:17", Fraction: "0000001", Zone: "+14:00", Legacy: "1900-01-01T00:00:00.007", Small: "1900-01-01T00:00:00"),
            (Date: "2020-02-29", Time: "21:15:17", Fraction: "1000000", Zone: "-14:00", Legacy: "1900-01-01T00:00:00.000", Small: "1900-01-01T00:00:00")
        };
        for (int i = 0; i < cases.Length; i++)
        {
            var test = cases[i];
            var values = new List<string> { (i + 1).ToString(), $"'{test.Date}'", $"'{test.Legacy}'", $"'{test.Small}'" };
            foreach (int scale in Enumerable.Range(0, 8))
            {
                string time = test.Time + (scale == 0 ? "" : "." + test.Fraction[..scale]);
                values.AddRange(new[] { $"'{time}'", $"'{test.Date}T{time}'", $"'{test.Date}T{time}{test.Zone}'" });
            }
            await Execute("INSERT #TemporalLayouts VALUES(" + string.Join(",", values) + ");");
        }
        await Execute("INSERT #TemporalLayouts(Id) VALUES(7);");
        await Compare("#TemporalLayouts");

        // Hidden uniquifier before the variable columns, composite descending
        // key, empty/NULL payloads, duplicate tuples, deleted rows, and bit packing.
        await Execute("""
            CREATE TABLE #ClusteredLayouts(Id int NULL, SubId smallint, Flag bit, Other bit,
                Label nvarchar(80), Bytes varbinary(20), Stamp datetime2(7));
            CREATE CLUSTERED INDEX IX_ClusteredLayouts ON #ClusteredLayouts(Id DESC, SubId);
            INSERT #ClusteredLayouts VALUES(1,1,1,0,N'first',0x00FF,'20260102'),
                (1,1,0,1,N'second',0x1122,'20260103'),(1,1,NULL,NULL,NULL,NULL,NULL),
                (2,1,1,1,N'',0x,'20200101'),(NULL,1,0,0,N'null key',0x00,'20200229'),
                (3,1,1,0,N'delete',0x01,'20200101'),(1,1,0,1,N'second',0x1122,'20260103');
            DELETE #ClusteredLayouts WHERE Id=3;
            """);
        await Compare("#ClusteredLayouts");
        log.WriteLine("PASS temporal precision 0..7, full date ranges, datetime/smalldatetime bounds and rounding, UTC/positive/negative/max offsets, NULLs; composite descending non-unique clustered keys, duplicates, deletes, empty/NULL text/binary and packed bits");

        async Task Compare(string name)
        {
            using var metadata = owner.CreateCommand();
            metadata.CommandText = "SELECT object_id,name,create_date FROM tempdb.sys.tables WHERE object_id=OBJECT_ID(@name)";
            metadata.Parameters.AddWithValue("@name", "tempdb.." + name);
            TempDbObject table;
            using (var reader = await metadata.ExecuteReaderAsync()) { Require(await reader.ReadAsync(), "fixture object metadata"); table = new(reader.GetInt32(0), reader.GetString(1), name, reader.GetDateTime(2), 0); }
            var actual = await TempDbInspector.ReadAsync(Connection, table);
            using var select = owner.CreateCommand(); select.CommandText = "SELECT * FROM " + name;
            using var expectedReader = await select.ExecuteReaderAsync();
            var expected = new DataTable(); expected.Load(expectedReader);
            Require(actual.Columns.Count == expected.Columns.Count && actual.Rows.Count == expected.Rows.Count, name + " exact row and column counts");
            for (int c = 0; c < expected.Columns.Count; c++) Require(actual.Columns[c].DataType == expected.Columns[c].DataType, name + " native CLR type: " + expected.Columns[c].ColumnName);
            // Physical leaf order can differ from the SELECT order; compare a
            // multiset of whole tuples, including offsets and duplicate counts.
            var remaining = actual.Rows.Cast<DataRow>().ToList();
            foreach (DataRow row in expected.Rows)
            {
                int index = remaining.FindIndex(candidate => Enumerable.Range(0, expected.Columns.Count).All(c => Equal(row[c], candidate[c])));
                if (index < 0)
                {
                    var candidate = remaining.FirstOrDefault(r => Equals(row[0], r[0]));
                    string details = candidate == null ? "missing key " + row[0] : string.Join("; ", Enumerable.Range(0, expected.Columns.Count).Where(c => !Equal(row[c], candidate[c])).Select(c => expected.Columns[c].ColumnName + ": expected " + Show(row[c]) + ", actual " + Show(candidate[c])));
                    throw new Exception(name + " tuple mismatch: " + details);
                }
                remaining.RemoveAt(index);
            }
            log.WriteLine($"PASS {name}: {actual.Rows.Count} exact tuples / {actual.Columns.Count} native column types match owner SELECT");
        }
        static bool Equal(object left, object right) => left is byte[] a && right is byte[] b ? a.SequenceEqual(b)
            : left is DateTimeOffset first && right is DateTimeOffset second ? first.EqualsExact(second) : Equals(left, right);
        static string Show(object value) => value is DateTimeOffset dto ? dto.ToString("O") : value is DateTime dt ? dt.ToString("O") : value.ToString() ?? "NULL";
    }
}
