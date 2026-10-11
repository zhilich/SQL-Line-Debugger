using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

// Unique, disposable modules in tempdb, with no changes to the user's modules.
internal sealed class DemoFixture : IDisposable
{
    private readonly SqlConnection connection;
    private readonly string prefix = "SqlLineDemo_" + Guid.NewGuid().ToString("N");
    public string Invocation => $"-- F11 enters the procedure, then the scalar function.\r\nEXEC tempdb.dbo.[{prefix}_Outer] @seed = 7;";
    public IReadOnlyList<SourceDocument> Sources { get; }
    public DemoFixture(SqlConnection connection)
    {
        this.connection = connection;
        try
        {
            Run($"USE tempdb; EXEC(N'CREATE FUNCTION dbo.[{prefix}_Double](@value int) RETURNS int AS\r\nBEGIN\r\n    DECLARE @answer int;\r\n    SET @answer = @value * 2;\r\n    RETURN @answer;\r\nEND');");
            Run($"USE tempdb; EXEC(N'CREATE PROCEDURE dbo.[{prefix}_Inner] @value int, @result int OUTPUT AS\r\nBEGIN\r\n    SET @result = @value + 3;\r\n    PRINT N''Inner procedure complete'';\r\nEND');");
            Run($"USE tempdb; EXEC(N'CREATE PROCEDURE dbo.[{prefix}_Outer] @seed int AS\r\nBEGIN\r\n    SET NOCOUNT ON;\r\n    DECLARE @number int = @seed, @result int;\r\n    SET @number = dbo.[{prefix}_Double](@number);\r\n    EXEC dbo.[{prefix}_Inner] @number, @result OUTPUT;\r\n    PRINT N''Demo complete — expected result: 17'';\r\n    SELECT @seed AS Seed, @number AS Doubled, @result AS Result;\r\n    SELECT N''Second result set'' AS Message;\r\nEND');");
            var sources = new List<SourceDocument>();
            foreach (var suffix in new[] { "Double", "Inner", "Outer" })
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT o.object_id, m.definition FROM tempdb.sys.objects o JOIN tempdb.sys.sql_modules m ON o.object_id=m.object_id WHERE o.name=@name";
                cmd.Parameters.AddWithValue("@name", prefix + "_" + suffix);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) throw new InvalidOperationException("Demo source unavailable.");
                sources.Add(new($"tempdb:{reader.GetInt32(0)}", prefix + "_" + suffix, "tempdb", reader.GetString(1), Array.Empty<StatementSpan>()));
            }
            Sources = sources;
        }
        catch { Dispose(); throw; }
    }
    private void Run(string sql) { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 10; cmd.ExecuteNonQuery(); }
    public void Dispose() => Run($"USE tempdb; DROP PROCEDURE IF EXISTS dbo.[{prefix}_Outer]; DROP PROCEDURE IF EXISTS dbo.[{prefix}_Inner]; DROP FUNCTION IF EXISTS dbo.[{prefix}_Double];");
}
