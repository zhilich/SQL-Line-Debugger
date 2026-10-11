using System.Data;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal sealed class NativeDebugSession : IDisposable
{
    private static int activeSession;
    private readonly string connectionString;
    private readonly BreakpointStore breakpoints;
    private readonly object gate = new();
    private readonly Dictionary<(int Batch, int Worker, int Level), StackFrame> frames = new();
    private SqlConnection? control;
    private SqlCommand? executingCommand;
    private volatile bool stopped, breakNext = true, paused;
    private DebugAction action = DebugAction.Into;
    private int originLevel, spid;
    private Exception? callbackFailure;
    private Task? runningTask;
    public bool IsPaused => paused;
    public bool IsRunning => runningTask is { IsCompleted: false };
    public event Action<PauseSnapshot>? Paused;
    public event Action<string>? Output;
    public event Action<DataTable>? ResultSet;
    public event Action<SourceDocument>? SourceLoaded;
    public event Action? Running;

    public NativeDebugSession(string connectionString, BreakpointStore breakpoints)
    { this.connectionString = connectionString; this.breakpoints = breakpoints; }

    public Task RunAsync(string sql, bool demo = false)
    {
        lock (gate)
        {
            if (runningTask != null) throw new InvalidOperationException("Create a new session for each run.");
            runningTask = Task.Run(() => Execute(sql, demo));
            return runningTask;
        }
    }
    public void Resume(DebugAction next)
    {
        DebugTrace.Write("native Resume begin " + next);
        lock (gate)
        {
            if (!paused || stopped) return;
            action = next; breakNext = false; paused = false;
        }
        int hr = NativeMethods.ResumeDebugger();
        DebugTrace.Write($"native Resume returned 0x{hr:X8}");
        if (hr < 0) { Output?.Invoke($"Resume failed (0x{hr:X8})."); Stop(); }
        Running?.Invoke();
    }
    public void Break() => breakNext = true;
    public void Stop()
    {
        stopped = true;
        NativeMethods.RequestStop();
        // Cancel can wait for SQL Server's acknowledgement. It must run outside
        // the callback while the pause gate is released to consume TDS attention.
        var command = executingCommand;
        _ = Task.Run(() => { try { command?.Cancel(); } catch (Exception ex) { Output?.Invoke("Cancel: " + ex.Message); } });
        bool wasPaused = paused; paused = false;
        if (wasPaused) _ = Task.Run(async () => { await Task.Delay(100); NativeMethods.ResumeDebugger(); });
    }

    private void Execute(string sql, bool demo)
    {
        if (Interlocked.CompareExchange(ref activeSession, 1, 0) != 0)
            throw new InvalidOperationException("Only one debug session can run per application process.");
        bool registered = false;
        DemoFixture? fixture = null;
        Task? eventPump = null;
        try
        {
            control = new SqlConnection(connectionString);
            control.Open();
            using (var check = control.CreateCommand())
            {
                check.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin')";
                if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidOperationException("Native SQL debugging requires sysadmin on the test instance.");
            }
            Output?.Invoke($"Connected to {control.DataSource} / {control.Database}, SQL {control.ServerVersion}.");
            if (demo)
            {
                fixture = new DemoFixture(control);
                sql = fixture.Invocation;
                foreach (var source in fixture.Sources) SourceLoaded?.Invoke(source);
            }
            using var enable = control.CreateCommand();
            enable.CommandText = "DECLARE @b varbinary(8000); EXEC master.dbo.sp_enable_sql_debug @b OUTPUT; SELECT @b";
            byte[] blob = (byte[])(enable.ExecuteScalar() ?? throw new InvalidOperationException("SQL debugger returned no interface."));
            // COM callbacks copy events into a native queue. Managed parsing,
            // SQL source reads and publication run on our own event thread.
            NativeMethods.Start(blob);
            registered = true;
            eventPump = Task.Factory.StartNew(ProcessNativeEvents, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (stopped) return;
            using var worker = new SqlConnection(connectionString);
            worker.InfoMessage += (_, e) => Output?.Invoke(e.Message);
            worker.Open();
            using var command = worker.CreateCommand();
            executingCommand = command;
            // Deliberately no GO splitter: SQL batches retain exact text offsets.
            command.CommandText = sql;
            command.CommandTimeout = 0;
            if (stopped) return;
            using var reader = command.ExecuteReader();
            int resultNumber = 0;
            do
            {
                if (reader.FieldCount <= 0) continue;
                var table = new DataTable($"Result {++resultNumber}");
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string name = string.IsNullOrEmpty(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);
                    string unique = name;
                    for (int suffix = 2; table.Columns.Contains(unique); suffix++) unique = name + "_" + suffix;
                    table.Columns.Add(unique, reader.GetFieldType(i));
                }
                int total = 0;
                while (reader.Read())
                {
                    if (total++ < 10000) { object[] values = new object[reader.FieldCount]; reader.GetValues(values); table.Rows.Add(values); }
                }
                if (total > 10000) Output?.Invoke($"{table.TableName}: displaying the first 10,000 of {total:N0} rows.");
                ResultSet?.Invoke(table);
            } while (reader.NextResult());
            if (callbackFailure != null) throw new InvalidOperationException("Debugger callback failed.", callbackFailure);
            Output?.Invoke(stopped ? "Debugging stopped." : $"Execution completed; {resultNumber} result set(s).");
        }
        catch (SqlException) when (stopped && callbackFailure == null) { Output?.Invoke("Execution cancelled."); }
        finally
        {
            executingCommand = null; paused = false;
            if (registered)
            {
                int hr = NativeMethods.FinishDebugger();
                if (hr < 0) Output?.Invoke($"Debugger cleanup HRESULT 0x{hr:X8}.");
            }
            NativeMethods.CloseDebuggerEvents();
            eventPump?.GetAwaiter().GetResult();
            try { fixture?.Dispose(); }
            finally { control?.Dispose(); control = null; Interlocked.Exchange(ref activeSession, 0); }
        }
    }

    private void ProcessNativeEvents()
    {
        while (true)
        {
            int read = NativeMethods.ReadDebuggerEvent(out var pointer);
            if (read == 1) break;
            Marshal.ThrowExceptionForHR(read);
            string json;
            try { json = Marshal.PtrToStringUni(pointer)!; }
            finally { Marshal.FreeCoTaskMem(pointer); }
            int result = HandleEvent(json);
            if (json.StartsWith("{\"kind\":\"step\"", StringComparison.Ordinal) && result != 1)
            {
                int hr = NativeMethods.ResumeDebugger();
                if (hr < 0 && !stopped) { callbackFailure = new InvalidOperationException($"Continue failed (0x{hr:X8})."); Stop(); }
            }
        }
    }

    private int HandleEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var e = document.RootElement;
            string kind = e.GetProperty("kind").GetString()!;
            DebugTrace.Write("native callback " + kind);
            if (kind == "connection") { spid = e.GetProperty("spid").GetInt32(); Output?.Invoke($"Debug target SPID {spid}."); return 0; }
            if (kind is "error" or "trace")
            {
                string text = e.GetProperty("text").GetString()!;
                if (e.TryGetProperty("hr", out var hr)) text += $" (0x{hr.GetInt32():X8})";
                Output?.Invoke(text);
                if (kind == "error") throw new InvalidOperationException(text);
                return 0;
            }
            int batch = e.GetProperty("batch").GetInt32(), worker = e.GetProperty("worker").GetInt32(), level = e.GetProperty("level").GetInt32();
            var key = (batch, worker, level);
            if (kind == "pop") { frames.Remove(key); return 0; }
            if (kind is "push" or "dynamic")
            {
                var spans = e.GetProperty("spans").EnumerateArray().Select(ReadSpan).ToArray();
                SourceDocument source;
                if (kind == "dynamic")
                {
                    string text = e.GetProperty("text").GetString()!;
                    source = new(level == 0 ? "batch" : "dynamic:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes(text))),
                        level == 0 ? "Query" : "Dynamic SQL", control!.Database, text, spans);
                }
                else
                {
                    DebugTrace.Write("LoadSource begin " + e.GetProperty("name").GetString());
                    source = SqlCatalog.LoadSource(control!, e.GetProperty("db").GetString()!, e.GetProperty("oid").GetInt32(), e.GetProperty("name").GetString()!, spans);
                    DebugTrace.Write("LoadSource end");
                }
                frames[key] = new StackFrame(level, source);
                foreach (var span in spans) source.Range(span);
                SourceLoaded?.Invoke(source);
                return 0;
            }
            if (kind != "step") return 0;
            if (stopped) return 3;
            if (!frames.TryGetValue(key, out var frame)) throw new InvalidOperationException("No source frame for SQL debugger step.");
            var current = frame.Source;
            var activeSpan = ReadSpan(e.GetProperty("span"));
            current.Range(activeSpan);
            // Keep the caller's call site even when Continue/Step Over hides
            // its steps. Each invocation has its own position, including recursion.
            frames[key] = frame with { Span = activeSpan };
            var variables = e.GetProperty("variables").EnumerateArray().Select(v => new VariableValue(v.GetProperty("name").GetString()!,
                v.GetProperty("type").GetString()!, v.GetProperty("value").GetString()!, v.GetProperty("flags").GetUInt32())).ToArray();
            bool breakpoint = breakpoints.Contains(current, activeSpan);
            bool shouldPause;
            lock (gate) shouldPause = breakNext || breakpoint || action == DebugAction.Into || (action == DebugAction.Over && level <= originLevel) || (action == DebugAction.Out && level < originLevel);
            if (!shouldPause) return 0;
            var stack = frames.Where(f => f.Key.Batch == batch && f.Key.Worker == worker && f.Key.Level <= level)
                .OrderByDescending(f => f.Key.Level).Select(f => f.Value).ToArray();
            var snapshot = new PauseSnapshot(current, activeSpan, level, variables, stack, breakpoint ? "Breakpoint" : breakNext ? "Break" : "Step", spid);
            lock (gate)
            {
                if (stopped) return 3;
                originLevel = level; paused = true;
            }
            Paused?.Invoke(snapshot);
            DebugTrace.Write("native pause published " + current.DisplayName + ":" + snapshot.Line);
            return 1; // Return the COM callback; a later command resumes SQL.
        }
        catch (Exception ex)
        {
            callbackFailure = ex;
            stopped = true;
            try { Output?.Invoke("Debugger error: " + ex.Message); Stop(); } catch { }
            return 3;
        }
    }
    private static StatementSpan ReadSpan(JsonElement e) => new(e.GetProperty("index").GetInt32(), e.GetProperty("start").GetInt32(), e.GetProperty("end").GetInt32());
    public void Dispose()
    {
        if (IsRunning) throw new InvalidOperationException("Stop and await the session before disposing it.");
    }
}

