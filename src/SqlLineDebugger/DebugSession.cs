using System.Data;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace SqlLineDebugger;

// One native worker process per session. SQL Server's debugger registration can
// become unusable for a PID after TDS cancellation on the tested LocalDB build.
// Isolating registration also prevents an ABI failure from taking down the UI.
public sealed class DebugSession : IDisposable
{
    private readonly string connectionString;
    private readonly BreakpointStore breakpoints;
    private readonly Channel<string> commands = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = false });
    private Process? worker;
    private Task? runningTask;
    private volatile bool paused, stopped, breakRequested;
    private long pauseId;
    private IReadOnlyList<TempDbObject> tempTables = Array.Empty<TempDbObject>();
    public IReadOnlyList<TempDbObject> TempTables => Volatile.Read(ref tempTables);
    public event Action<TableDiscovery>? TablesChanged;
    public bool IsPaused => paused;
    public bool IsRunning => runningTask is { IsCompleted: false };
    public event Action<PauseSnapshot>? Paused;
    public event Action<string>? Output;
    public event Action<DataTable>? ResultSet;
    public event Action<SourceDocument>? SourceLoaded;
    public event Action? Running;
    public DebugSession(string connectionString, BreakpointStore breakpoints)
    { this.connectionString = connectionString; this.breakpoints = breakpoints; breakpoints.Changed += SyncBreakpoints; }
    private void SyncBreakpoints() => Send(new { kind = "breakpoints", data = breakpoints.All });
    public Task RunAsync(string sql, bool demo = false)
    {
        if (runningTask != null) throw new InvalidOperationException("Create a new session for each run.");
        runningTask = Task.Run(() => RunWorker(sql, demo));
        return runningTask;
    }
    public void Resume(DebugAction next)
    {
        if (!paused || stopped) return;
        // Sync current breakpoints before resuming so gutter changes at a pause
        // take effect immediately in the isolated worker's pause policy.
        Send(new { kind = "breakpoints", data = breakpoints.All });
        paused = false;
        Send(new { kind = "resume", data = next });
        Running?.Invoke();
    }
    public void Break() { breakRequested = true; Send(new { kind = "break" }); }
    public Task<DataTable> InspectAsync(TempDbObject table, CancellationToken cancellation = default)
    {
        if (!IsRunning || !IsPaused) throw new InvalidOperationException("Pause the debugger before inspecting a table.");
        return TempDbInspector.ReadAsync(connectionString, table, cancellation);
    }
    internal Task<GlobalTableCopy> CopyToGlobalAsync(TempDbObject table, string name, GlobalTempTableCopies copies, CancellationToken cancellation = default)
    {
        if (!IsRunning || !IsPaused) throw new InvalidOperationException("Pause the debugger before copying a table.");
        return copies.CopyAsync(connectionString, table, name, cancellation);
    }
    public void Stop() { stopped = true; paused = false; Send(new { kind = "stop" }); }
    private void Send(object message)
    {
        // UI actions enqueue commands; no pipe write or flush runs on the UI.
        DebugTrace.Write("host enqueue command");
        commands.Writer.TryWrite(JsonSerializer.Serialize(message));
    }
    private async Task RunWorker(string sql, bool demo)
    {
        string pipeName = "SqlLineDebugger_" + Guid.NewGuid().ToString("N");
        string tempDbSessionName = "SqlLineDebugger_TempDB_" + Guid.NewGuid().ToString("N");
        // Separate handles for each direction avoid read/write contention on a
        // duplex PipeStream. Buffer commands so a callback cannot block the UI.
        await using var events = new NamedPipeServerStream(pipeName + "_events", PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        await using var commandPipe = new NamedPipeServerStream(pipeName + "_commands", PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(DebugSession).Assembly.Location);
        start.ArgumentList.Add("--debug-worker"); start.ArgumentList.Add(pipeName);
        worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native debugger worker.");
        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(events.WaitForConnectionAsync(connectTimeout.Token), commandPipe.WaitForConnectionAsync(connectTimeout.Token));
            using var reader = new StreamReader(events);
            using var connectedWriter = new StreamWriter(commandPipe) { AutoFlush = true };
            // Startup precedes any commands enqueued while the worker connects.
            await connectedWriter.WriteLineAsync(JsonSerializer.Serialize(new { kind = "start", data = new WorkerRequest(connectionString, sql, demo, breakpoints.All, tempDbSessionName) }));
            using var sendCancellation = new CancellationTokenSource();
            var sendTask = SendCommands(connectedWriter, sendCancellation.Token);
            try
            {
            if (stopped) Send(new { kind = "stop" });
            if (breakRequested) Send(new { kind = "break" });
            bool completed = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                string kind = e.GetProperty("kind").GetString()!;
                DebugTrace.Write("host receive " + kind);
                switch (kind)
                {
                    case "output": Output?.Invoke(e.GetProperty("data").GetString()!); break;
                    case "source": SourceLoaded?.Invoke(e.GetProperty("data").Deserialize<SourceDocument>()!); break;
                    case "paused":
                        paused = true;
                        if (stopped) { Send(new { kind = "stop" }); break; }
                        var snapshot = e.GetProperty("data").Deserialize<PauseSnapshot>()!;
                        Interlocked.Exchange(ref pauseId, snapshot.PauseId);
                        Volatile.Write(ref tempTables, snapshot.Tables ?? Array.Empty<TempDbObject>());
                        Paused?.Invoke(snapshot);
                        break;
                    case "tables":
                        var discovery = e.GetProperty("data").Deserialize<TableDiscovery>()!;
                        if (paused && discovery.PauseId == Interlocked.Read(ref pauseId))
                        {
                            Volatile.Write(ref tempTables, discovery.Tables);
                            TablesChanged?.Invoke(discovery);
                        }
                        break;
                    case "result": ResultSet?.Invoke(ReadTable(e.GetProperty("data"))); break;
                    case "error": throw new InvalidOperationException(e.GetProperty("data").GetString());
                    case "complete": completed = true; break;
                }
                if (completed) break;
            }
            if (!completed) throw new InvalidOperationException("Native debugger worker disconnected unexpectedly. See Debug Output.");
            }
            finally
            {
                commands.Writer.TryComplete(); sendCancellation.Cancel();
                try { await sendTask; } catch (OperationCanceledException) { } catch (IOException) when (stopped || worker.HasExited) { }
            }
        }
        finally
        {
            commands.Writer.TryComplete();
            paused = false;
            if (!worker.HasExited && !worker.WaitForExit(5000)) worker.Kill(entireProcessTree: true);
            worker.Dispose(); worker = null;
            try { await TempDbInspector.CleanupSessionAsync(connectionString, tempDbSessionName); }
            catch (Exception ex) { Output?.Invoke("TempDB XE cleanup: " + ex.Message); }
        }
    }
    private async Task SendCommands(StreamWriter destination, CancellationToken cancellation)
    {
        await foreach (string command in commands.Reader.ReadAllAsync(cancellation))
        {
            DebugTrace.Write("host pipe write begin");
            await destination.WriteLineAsync(command.AsMemory(), cancellation);
            DebugTrace.Write("host pipe write end");
        }
    }
    private static DataTable ReadTable(JsonElement data)
    {
        var table = new DataTable(data.GetProperty("name").GetString());
        foreach (var column in data.GetProperty("columns").EnumerateArray()) table.Columns.Add(column.GetString()!, typeof(string));
        foreach (var row in data.GetProperty("rows").EnumerateArray())
            table.Rows.Add(row.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? DBNull.Value : (object)v.GetString()!).ToArray());
        return table;
    }
    public void Dispose()
    {
        if (IsRunning) throw new InvalidOperationException("Stop and await the session before disposing it.");
        breakpoints.Changed -= SyncBreakpoints;
    }
}

internal sealed record WorkerRequest(string ConnectionString, string Sql, bool Demo, IReadOnlyList<Breakpoint> Breakpoints, string? TempDbSessionName = null);
