using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

public static class SqlCatalog
{
    public static string ConnectionString(string server, string database) => new SqlConnectionStringBuilder
    {
        DataSource = server, InitialCatalog = database, IntegratedSecurity = true, Pooling = false,
        Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = true, ConnectTimeout = 10,
        ApplicationName = "SQL Line Debugger"
    }.ConnectionString;
    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
    public static IReadOnlyList<SourceDocument> LoadModules(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT o.object_id, QUOTENAME(s.name)+'.'+QUOTENAME(o.name), m.definition FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id WHERE o.is_ms_shipped=0 AND o.type IN ('P','FN','IF','TF','TR') ORDER BY s.name,o.name";
        using var reader = command.ExecuteReader();
        var sources = new List<SourceDocument>();
        while (reader.Read()) if (!reader.IsDBNull(2)) sources.Add(new($"{connection.Database}:{reader.GetInt32(0)}", reader.GetString(1), connection.Database, reader.GetString(2), Array.Empty<StatementSpan>()));
        return sources;
    }
    internal static SourceDocument LoadSource(SqlConnection control, string database, int objectId, string name, IReadOnlyList<StatementSpan> spans)
    {
        using var command = control.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = $"SET LOCK_TIMEOUT 2000; SELECT definition FROM {Quote(database)}.sys.sql_modules WHERE object_id=@id";
        command.Parameters.AddWithValue("@id", objectId);
        var definition = command.ExecuteScalar() as string;
        if (definition == null) throw new InvalidOperationException($"Cannot read {database}.{name}; it may be encrypted or require VIEW DEFINITION.");
        return new($"{database}:{objectId}", name, database, definition, spans);
    }
}
