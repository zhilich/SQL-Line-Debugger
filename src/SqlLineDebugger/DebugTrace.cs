using System.Diagnostics;
using System.Collections.Concurrent;

namespace SqlLineDebugger;

internal static class DebugTrace
{
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly ConcurrentQueue<string> entries = new();
    private static readonly string? directory = Environment.GetEnvironmentVariable("SQL_LINE_DEBUG_TRACE");
    static DebugTrace() { if (!string.IsNullOrEmpty(directory)) AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(); }
    internal static void Write(string stage)
    {
        if (string.IsNullOrEmpty(directory)) return;
        entries.Enqueue($"{DateTime.UtcNow:O} {clock.Elapsed.TotalMilliseconds:F1}ms thread={Environment.CurrentManagedThreadId} {stage}");
    }
    private static void Flush() { Directory.CreateDirectory(directory!); File.WriteAllLines(Path.Combine(directory!, $"trace-{Environment.ProcessId}.log"), entries); }
}
