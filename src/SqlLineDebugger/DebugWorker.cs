using System.Data;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;

namespace SqlLineDebugger;

internal static class DebugWorker
{
    internal static int Run(string pipeName)
    {
        using var events = new NamedPipeClientStream(".", pipeName + "_events", PipeDirection.Out);
        using var commandsPipe = new NamedPipeClientStream(".", pipeName + "_commands", PipeDirection.In, PipeOptions.Asynchronous);
        events.Connect(15000); commandsPipe.Connect(15000);
        using var reader = new StreamReader(commandsPipe);
        using var writer = new StreamWriter(events) { AutoFlush = true };
        object writeLock = new();
        void Send(string kind, object? data = null) { lock (writeLock) writer.WriteLine(JsonSerializer.Serialize(new { kind, data })); }
        var line = reader.ReadLine() ?? throw new IOException("Debugger host disconnected before startup.");
        using var start = JsonDocument.Parse(line);
        var request = start.RootElement.GetProperty("data").Deserialize<WorkerRequest>()!;
        var points = new BreakpointStore(); points.Replace(request.Breakpoints);
        using var session = new NativeDebugSession(request.ConnectionString, points, request.TempDbSessionName);
        session.Output += text => Send("output", text);
        session.SourceLoaded += source => Send("source", source);
        session.Paused += snapshot => Send("paused", snapshot);
        session.TablesChanged += discovery => Send("tables", discovery);
        session.ResultSet += table => Send("result", new
        {
            name = table.TableName,
            columns = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray(),
            rows = table.Rows.Cast<DataRow>().Select(r => r.ItemArray.Select(v => v == null || v == DBNull.Value ? null : Format(v)).ToArray()).ToArray()
        });
        // Native Continue and synchronous SQL can block threads. Keep the
        // command pump on its own thread rather than ThreadPool continuations.
        var commands = Task.Factory.StartNew(() =>
        {
            try
            {
                while (true)
                {
                    DebugTrace.Write("worker read begin");
                    var command = reader.ReadLineAsync().GetAwaiter().GetResult();
                    DebugTrace.Write("worker read end");
                    if (command == null) break;
                    using var doc = JsonDocument.Parse(command);
                    var e = doc.RootElement;
                    DebugTrace.Write("worker command " + e.GetProperty("kind").GetString());
                    switch (e.GetProperty("kind").GetString())
                    {
                        case "resume": session.Resume(e.GetProperty("data").Deserialize<DebugAction>()); break;
                        case "break": session.Break(); break;
                        case "stop": session.Stop(); break;
                        case "breakpoints": points.Replace(e.GetProperty("data").Deserialize<Breakpoint[]>()!); break;
                    }
                }
                if (session.IsRunning) session.Stop(); // Parent closed/crashed: do not leave SQL paused.
            }
            catch (IOException) { if (session.IsRunning) session.Stop(); }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            session.RunAsync(request.Sql, request.Demo).GetAwaiter().GetResult();
            Send("complete");
            return 0;
        }
        catch (Exception ex) { Send("error", ex.ToString()); return 1; }
    }
    private static string Format(object value) => value switch
    {
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };
}
