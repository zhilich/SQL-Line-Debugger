using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal static class TempDbInspector
{
    private static readonly Lazy<string> InspectionSql = new(() =>
    {
        using var resource = typeof(TempDbInspector).Assembly.GetManifestResourceStream("SqlLineDebugger.Sql.SelectTempDbRows.sql")!;
        using var text = new StreamReader(resource);
        return text.ReadToEnd();
    });
    internal static async Task CleanupSessionAsync(string connectionString, string name)
    {
        var settings = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 3 };
        using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 3;
        command.CommandText = $"IF EXISTS(SELECT 1 FROM sys.server_event_sessions WHERE name=@name) DROP EVENT SESSION {SqlCatalog.Quote(name)} ON SERVER;";
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync();
    }
    internal static async Task<DataTable> ReadAsync(string connectionString, TempDbObject table, CancellationToken cancellation = default)
    {
        var settings = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "tempdb", Pooling = false };
        // This is a connection in the UI/host process, not the registered native
        // worker. Its queries neither resume nor become part of the debuggee.
        using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellation);
        using var command = CreateReadCommand(connection, table);
        using var reader = await command.ExecuteReaderAsync(cancellation);
        var snapshot = new DataTable(table.Name);
        var schema = reader.GetColumnSchema();
        var sqlDecimals = Enumerable.Range(0, reader.FieldCount).Select(i =>
            reader.GetFieldType(i) == typeof(decimal) && schema[i].NumericPrecision > 28).ToArray();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var column = snapshot.Columns.Add(reader.GetName(i), sqlDecimals[i] ? typeof(System.Data.SqlTypes.SqlDecimal) : reader.GetFieldType(i));
            column.ExtendedProperties["SqlType"] = reader.GetDataTypeName(i);
        }
        while (await reader.ReadAsync(cancellation))
        {
            var values = new object[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                values[i] = reader.IsDBNull(i) ? DBNull.Value : sqlDecimals[i] ? reader.GetSqlDecimal(i) : reader.GetValue(i);
            snapshot.Rows.Add(values);
        }
        // Preserve errors emitted after the result set as errors, not partial snapshots.
        while (await reader.NextResultAsync(cancellation)) { while (await reader.ReadAsync(cancellation)) { } }
        return snapshot;
    }

    internal static SqlCommand CreateReadCommand(SqlConnection connection, TempDbObject table, string? globalName = null)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            SET IMPLICIT_TRANSACTIONS OFF;
            SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
            SET LOCK_TIMEOUT 1000;
            IF NOT EXISTS (SELECT 1 FROM tempdb.sys.tables WITH(NOLOCK)
                WHERE object_id=@expected_object_id AND name=@table_name AND create_date=@created)
                THROW 51000, 'This temporary object no longer exists. Pause again to update the TempDB list.', 1;
            """ + Environment.NewLine + InspectionSql.Value;
        command.Parameters.Add("@expected_object_id", SqlDbType.Int).Value = table.ObjectId;
        command.Parameters.Add("@table_name", SqlDbType.NVarChar, 776).Value = table.InternalName;
        command.Parameters.Add("@max_pages", SqlDbType.Int).Value = 100;
        command.Parameters.Add("@created", SqlDbType.DateTime).Value = table.Created;
        command.Parameters.Add("@global_name", SqlDbType.NVarChar, 128).Value = (object?)globalName ?? DBNull.Value;
        return command;
    }
}
