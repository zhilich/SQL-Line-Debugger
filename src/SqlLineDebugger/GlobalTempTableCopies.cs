using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal sealed record GlobalTableCopy(string Name, long RowCount);

// Lives for the UI's lifetime, across debug sessions. A separate creator
// connection per copy also keeps failures from closing earlier global tables.
internal sealed class GlobalTempTableCopies : IDisposable
{
    private readonly Dictionary<(string Connection, string Name), SqlConnection> creators = new();
    private readonly SemaphoreSlim copying = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;

    internal static string DefaultName(string sourceName)
    {
        string suffix = sourceName.TrimStart('@', '#');
        return "dbg_" + suffix[..Math.Min(suffix.Length, 122)];
    }

    internal static string NormalizeName(string entered)
    {
        string name = entered.Trim();
        if (name.StartsWith("##", StringComparison.Ordinal)) name = name[2..];
        else if (name.StartsWith('#')) name = name[1..];
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a global temporary table name.");
        name = "##" + name;
        if (name.Length > 128) throw new ArgumentException("The name must be at most 128 characters, including ##.");
        if (name.Any(char.IsControl)) throw new ArgumentException("The name cannot contain control characters.");
        return name;
    }

    internal async Task<GlobalTableCopy> CopyAsync(string connectionString, TempDbObject table, string enteredName, CancellationToken cancellation = default)
    {
        string name = NormalizeName(enteredName);
        CancellationToken shutdownToken;
        lock (creators) { ObjectDisposedException.ThrowIf(disposed, this); shutdownToken = shutdown.Token; }
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdownToken);
        await copying.WaitAsync(combined.Token);
        SqlConnection? connection = null;
        try
        {
            var settings = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "tempdb", Pooling = false };
            connection = new SqlConnection(settings.ConnectionString);
            await connection.OpenAsync(combined.Token);
            using var command = TempDbInspector.CreateReadCommand(connection, table, name);
            long count = Convert.ToInt64(await command.ExecuteScalarAsync(combined.Token));
            lock (creators)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var key = (settings.ConnectionString, name);
                if (creators.Remove(key, out var previous)) previous.Dispose();
                creators[key] = connection;
                connection = null; // Ownership transfers only after successful copy.
            }
            return new GlobalTableCopy(name, count);
        }
        finally
        {
            // A failed/cancelled connection closes its transaction and cannot
            // leave a partial replacement or affect other retained creators.
            connection?.Dispose();
            copying.Release();
        }
    }

    public void Dispose()
    {
        lock (creators)
        {
            if (disposed) return;
            disposed = true;
        }
        shutdown.Cancel();
        lock (creators) { foreach (var connection in creators.Values) connection.Dispose(); creators.Clear(); }
        shutdown.Dispose();
    }
}
