using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static partial class TempDbTests
{
    private static async Task TestEarlyProcedureVariables(TextWriter log)
    {
        using (var setup = new SqlConnection(Connection))
        {
            await setup.OpenAsync();
            using var check = setup.CreateCommand(); check.CommandText = "SELECT OBJECT_ID(N'SqlDebugLab.Run')";
            if (await check.ExecuteScalarAsync() is not int) { log.WriteLine("SKIP Run entry test (sample absent)"); return; }
        }
        bool entered = false, resolved = false;
        await RunScenario("EXEC SqlDebugLab.Run @RowCount=0;", async (session, snapshot) =>
        {
            string module = snapshot.Source.Name.Replace("[", "").Replace("]", "");
            if (!(module == "Run" || module.EndsWith(".Run", StringComparison.Ordinal))) return;
            if (entered)
            {
                string statement = snapshot.Source.Statement(snapshot.Span).TrimStart();
                if (statement.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase) && statement.Contains("A3 Run"))
                {
                    await WaitTables(session, t => new[] { "@Left", "@Right", "@rows" }.All(n => t.Any(x => x.Name == n)));
                    resolved = true; session.Stop();
                }
                return;
            }
            entered = true;
            log.WriteLine("FIRST Run STATEMENT: " + snapshot.Source.Statement(snapshot.Span));
            var tables = await WaitTables(session, t => t.Count >= 3);
            log.WriteLine("ENTRY OBJECTS: " + string.Join(", ", tables.Select(t => t.Name)));
            int variables = 0;
            foreach (var table in tables)
            {
                var rows = await session.InspectAsync(table);
                if (!rows.Columns.Contains("Id") || rows.Columns.Count != 2 || !(rows.Columns.Contains("Label") || rows.Columns.Contains("ScopeName"))) continue;
                Require(rows.Rows.Count == 0 && table.Name == table.InternalName, "entry variables are empty and unresolved before any statement uses them");
                variables++;
            }
            Require(variables == 3, "three initialized table variables visible at first Run statement");
        }, log);
        Require(entered, "Run entry pause visited");
        Require(resolved, "entry variables resolve after their references execute");
        log.WriteLine("PASS three unresolved/inspectable table variables at procedure entry before use");
    }
}
