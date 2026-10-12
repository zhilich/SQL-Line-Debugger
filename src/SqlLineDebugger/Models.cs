using System.Security.Cryptography;
using System.Text;

namespace SqlLineDebugger;

public sealed record StatementSpan(int Index, int Start, int End);
public sealed record VariableValue(string Name, string Type, string Value, uint Flags);
public sealed record SourceDocument(string Key, string Name, string Database, string Text, IReadOnlyList<StatementSpan> Spans)
{
    public string Hash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(Text)));
    public string DisplayName => Name.StartsWith("SqlLineDemo_", StringComparison.Ordinal) ? "Demo." + Name[(Name.LastIndexOf('_') + 1)..] : Name;
    public int LineAt(int offset) => 1 + Text.Take(Math.Clamp(offset, 0, Text.Length)).Count(c => c == '\n');
    public (int Start, int Length) Range(StatementSpan span)
    {
        // SQL Server debugger offsets are bytes into the original UTF-16 text.
        if ((span.Start & 1) != 0 || (span.End & 1) != 0 || span.Start < 0 || span.End < span.Start || span.End / 2 > Text.Length)
            throw new InvalidOperationException($"SQL source span {span.Start}:{span.End} does not fit the database text. Source highlighting disabled.");
        // offEnd identifies the final UTF-16 character (inclusive), frequently
        // excluding a trailing semicolon. Some spans use text length as sentinel.
        int end = Math.Min(Text.Length, span.End / 2 + 1);
        return (span.Start / 2, end - span.Start / 2);
    }
    public string Statement(StatementSpan span) { var r = Range(span); return Text.Substring(r.Start, r.Length); }
    public int StatementLine(StatementSpan span) => LineAt(Range(span).Start);
}
public sealed record StackFrame(int Level, SourceDocument Source, StatementSpan? Span = null);
public sealed record PauseSnapshot(SourceDocument Source, StatementSpan Span, int Level, IReadOnlyList<VariableValue> Variables,
    IReadOnlyList<StackFrame> Stack, string Reason, int Spid, IReadOnlyList<TempDbObject>? Tables = null, long PauseId = 0)
{
    public int Line => Source.StatementLine(Span);
}
public sealed record TempDbObject(int ObjectId, string InternalName, string Name, DateTime Created, long LastLockSequence);
public sealed record TableDiscovery(long PauseId, IReadOnlyList<TempDbObject> Tables);
public sealed record Breakpoint(string SourceKey, string SourceHash, int Line, string Name);
public enum DebugAction { Into, Over, Out, Continue }

public sealed class BreakpointStore
{
    private readonly object sync = new();
    private readonly List<Breakpoint> items = new();
    public event Action? Changed;
    public IReadOnlyList<Breakpoint> All { get { lock (sync) return items.ToArray(); } }
    public void Clear() { lock (sync) items.Clear(); Changed?.Invoke(); }
    public void Replace(IEnumerable<Breakpoint> replacements) { lock (sync) { items.Clear(); items.AddRange(replacements); } Changed?.Invoke(); }
    public void Toggle(SourceDocument source, int line)
    {
        lock (sync)
        {
            var existing = items.Find(b => b.SourceKey == source.Key && b.Line == line);
            if (existing != null) items.Remove(existing);
            else items.Add(new(source.Key, source.Hash, line, source.DisplayName));
        }
        Changed?.Invoke();
    }
    public bool Contains(SourceDocument source, StatementSpan span)
    {
        var range = source.Range(span);
        var first = source.LineAt(range.Start);
        // Server spans can include indentation before the next statement.
        // Binding on that whitespace would stop one statement too early.
        int meaningfulLength = source.Text.Substring(range.Start, range.Length).TrimEnd().Length;
        var last = source.LineAt(range.Start + Math.Max(0, meaningfulLength - 1));
        lock (sync) return items.Any(b => b.SourceKey == source.Key && b.SourceHash == source.Hash && b.Line >= first && b.Line <= last);
    }
}
