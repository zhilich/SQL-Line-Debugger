using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal sealed class TempDbTracker : IDisposable
{
    private readonly SqlConnection observer;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly string sessionName;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<int, Scope> scopes = new();
    private readonly Dictionary<long, Scope> scopeHistory = new();
    private readonly Dictionary<int, Observed> objects = new();
    private readonly Dictionary<int, Metadata> metadata = new();
    private readonly Dictionary<ulong, int> allocations = new();
    private readonly List<Statement> statements = new();
    private readonly List<Constraint> constraints = new();
    private readonly Dictionary<(long Scope, int Object), string> names = new();
    private readonly List<ModuleEntry> moduleEntries = new();
    private readonly HashSet<long> enteredScopes = new();
    private readonly Dictionary<int, DateTime> orphanEntryObjects = new();
    private Task? collector;
    private long generation, lastSequence, processedEvents, droppedEvents;
    private bool started, lossReported;
    private IReadOnlyList<TempDbObject> published = Array.Empty<TempDbObject>();
    internal event Action<IReadOnlyList<TempDbObject>>? Updated;

    private sealed record Scope(long Id, int Level, int ObjectId, SourceDocument Source, HashSet<string> Variables, DateTime Entered)
    { internal DateTime Exited = DateTime.MaxValue; }
    private sealed record Metadata(int Id, string InternalName, DateTime Created, bool Variable);
    private sealed class Observed(Metadata table, long sequence, long owner)
    {
        internal Metadata Table = table;
        internal long Sequence = sequence;
        internal long Owner = owner;
        internal string? FriendlyName;
    }
    private sealed class Statement(string kind, int objectId, int nest, int offset, long scope, HashSet<string> variables, string sql)
    {
        internal readonly string Kind = kind;
        internal readonly int ObjectId = objectId, Nest = nest, Offset = offset;
        internal readonly long Scope = scope;
        internal readonly HashSet<string> Variables = variables;
        internal readonly HashSet<int> Objects = new();
        internal readonly HashSet<int> EntryObjects = new();
        internal readonly string Sql = sql;
    }
    private sealed record ModuleEntry(int ObjectId, DateTime Started, long CallerScope, Metadata[] Tables)
    { internal long Scope; }
    private sealed record Constraint(long Scope, HashSet<string> Variables, HashSet<int> Objects);

    internal TempDbTracker(string connectionString, Action<string> log, string? name = null)
    {
        sessionName = name ?? "SqlLineDebugger_TempDB_" + Guid.NewGuid().ToString("N");
        this.log = log;
        observer = new SqlConnection(connectionString);
        try
        {
            observer.Open(); // Open before native registration: this connection is never debugged.
            using var command = observer.CreateCommand();
            command.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 1000;";
            command.ExecuteNonQuery();
        }
        catch { observer.Dispose(); throw; }
    }

    internal void Start(int spid)
    {
        using var command = observer.CreateCommand();
        command.CommandTimeout = 5;
        string predicate = $"[sqlserver].[session_id]=({spid})";
        string actions = "ACTION(package0.event_sequence,sqlserver.session_id)";
        command.CommandText = $"""
            CREATE EVENT SESSION {SqlCatalog.Quote(sessionName)} ON SERVER
            ADD EVENT sqlserver.lock_acquired({actions} WHERE([database_id]=(2) AND {predicate})),
            ADD EVENT sqlserver.module_start(SET collect_statement=(0) {actions} WHERE({predicate})),
            ADD EVENT sqlserver.module_end(SET collect_statement=(0) {actions} WHERE({predicate})),
            ADD EVENT sqlserver.sp_statement_starting(SET collect_statement=(1) {actions} WHERE({predicate})),
            ADD EVENT sqlserver.sp_statement_completed(SET collect_statement=(1) {actions} WHERE({predicate})),
            ADD EVENT sqlserver.sql_statement_starting(SET collect_statement=(1) {actions} WHERE({predicate})),
            ADD EVENT sqlserver.sql_statement_completed(SET collect_statement=(1) {actions} WHERE({predicate}))
            ADD TARGET package0.ring_buffer(SET max_memory=(1024),max_events_limit=(1000))
            WITH(MAX_MEMORY=2048 KB,EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS,
                 MAX_DISPATCH_LATENCY=1 SECONDS,STARTUP_STATE=OFF);
            ALTER EVENT SESSION {SqlCatalog.Quote(sessionName)} ON SERVER STATE=START;
            """;
        command.ExecuteNonQuery();
        started = true;
        collector = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellation.Token);
                    try { Drain(); Updated?.Invoke(Volatile.Read(ref published)); }
                    catch (SqlException ex) when (!cancellation.IsCancellationRequested) { log("TempDB collection will retry: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log("TempDB collection: " + ex.Message); }
        });
    }

    internal void Push(int level, SourceDocument source)
    {
        lock (gate)
        {
            int objectId = int.TryParse(source.Key[(source.Key.LastIndexOf(':') + 1)..], out int id) ? id : 0;
            var scope = new Scope(++generation, level, objectId, source, SqlTableReferences.Variables(source.Text), DateTime.UtcNow);
            scopes[level] = scope; scopeHistory[scope.Id] = scope;
            BindModuleEntries();
            Publish();
        }
    }

    internal void Pop(int level)
    {
        lock (gate)
        {
            if (scopes.Remove(level, out var scope))
            {
                scope.Exited = DateTime.UtcNow;
                Publish();
            }
        }
    }

    internal void ObserveVariables(int level, IReadOnlyList<VariableValue> variables)
    {
        lock (gate) if (scopes.TryGetValue(level, out var scope))
            foreach (var variable in variables.Where(v => v.Type.Equals("table", StringComparison.OrdinalIgnoreCase)))
                scope.Variables.Add(variable.Name);
    }

    internal IReadOnlyList<TempDbObject> Snapshot()
        => Volatile.Read(ref published);

    private void Publish()
    {
            var active = scopes.Values.Select(s => s.Id).ToHashSet();
            // Owner 0 denotes unknown scope for local objects, not session-wide
            // lifetime. Cached locals can retain metadata and acquire cleanup
            // locks after return, especially when XE loses statement context.
            // Only global tables may be shown without an active owning frame.
            Volatile.Write(ref published, objects.Values.Where(o => metadata.ContainsKey(o.Table.Id) &&
                    (o.Table.InternalName.StartsWith("##", StringComparison.Ordinal) || (o.Owner != 0 && active.Contains(o.Owner))))
                .OrderByDescending(o => o.Sequence).Select(o =>
                {
                    string name = o.Table.Variable
                        ? names.Where(n => n.Key.Object == o.Table.Id && active.Contains(n.Key.Scope))
                            .OrderByDescending(n => scopeHistory[n.Key.Scope].Level).Select(n => n.Value).FirstOrDefault() ?? o.Table.InternalName
                        : o.FriendlyName ?? DisplayTempName(o.Table.InternalName);
                    return new TempDbObject(o.Table.Id, o.Table.InternalName, name, o.Table.Created, o.Sequence);
                }).ToArray());
    }

    internal static string DisplayTempName(string name)
    {
        if (name.StartsWith("##", StringComparison.Ordinal)) return name;
        // Only remove the engine's padded twelve-hex-digit suffix, preserving
        // user underscores and global temporary-table names.
        return Regex.Replace(name, @"_{2,}[0-9A-Fa-f]{12}$", "");
    }

    private void Drain()
    {
        if (!started) return;
        using var command = observer.CreateCommand();
        command.CommandTimeout = 3;
        command.CommandText = "SELECT t.target_data FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address WHERE s.name=@name AND t.target_name='ring_buffer'";
        command.Parameters.AddWithValue("@name", sessionName);
        string? xml = command.ExecuteScalar() as string;
        if (xml == null) return;
        var root = XElement.Parse(xml);
        // SQL/target flushing must never hold the state lock needed by stepping.
        var liveMetadata = ReadMetadata();
        lock (gate)
        {
        var events = root.Elements("event").Select(e => (Event: e, Sequence: Number(Action(e, "event_sequence"))))
            .Where(e => e.Sequence > lastSequence).OrderBy(e => e.Sequence).ToArray();
        long total = Number((string?)root.Attribute("totalEventsProcessed"));
        long dropped = Number((string?)root.Attribute("droppedCount"));
        bool lost = (string?)root.Attribute("truncated") == "1" || dropped > droppedEvents ||
            total - processedEvents > events.Length;
        if (lost)
        {
            // A gap invalidates in-flight statement/lock correlation, not facts
            // already established for a live object in a particular invocation.
            // RemoveExpiredObjects validates ID/name/create_date below, and
            // PruneHistory retires mappings when their owning frame has exited.
            constraints.Clear(); statements.Clear(); moduleEntries.Clear(); orphanEntryObjects.Clear();
            if (!lossReported) { log("TempDB XE buffer lost events; confirmed names are preserved for live objects. Unresolved variables need new statement evidence."); lossReported = true; }
        }
        processedEvents = total;
        droppedEvents = dropped;
        metadata.Clear(); allocations.Clear();
        foreach (var pair in liveMetadata.Tables) metadata[pair.Key] = pair.Value;
        foreach (var pair in liveMetadata.Allocations) allocations[pair.Key] = pair.Value;
        RemoveExpiredObjects();
        foreach (var item in events)
        {
            Process(item.Event, item.Sequence);
            lastSequence = item.Sequence;
        }
        ResolveConstraints();
        if (events.Length > 0) PruneHistory(DateTime.Parse((string)events[^1].Event.Attribute("timestamp")!, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        Publish();
        }
    }

    private (Dictionary<int, Metadata> Tables, Dictionary<ulong, int> Allocations) ReadMetadata()
    {
        var tables = new Dictionary<int, Metadata>();
        var units = new Dictionary<ulong, int>();
        using var command = observer.CreateCommand();
        command.CommandTimeout = 3;
        command.CommandText = """
            SELECT t.object_id,t.name,t.create_date,p.hobt_id,p.partition_id,a.allocation_unit_id
            FROM tempdb.sys.tables t WITH(NOLOCK)
            LEFT JOIN tempdb.sys.partitions p WITH(NOLOCK) ON p.object_id=t.object_id
            LEFT JOIN tempdb.sys.allocation_units a WITH(NOLOCK) ON a.container_id=p.hobt_id OR a.container_id=p.partition_id
            WHERE t.name LIKE '#%';
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int id = reader.GetInt32(0); string name = reader.GetString(1);
            tables[id] = new Metadata(id, name, reader.GetDateTime(2), Regex.IsMatch(name, @"^#[0-9A-Fa-f]{8}$"));
            for (int column = 3; column <= 5; column++) if (!reader.IsDBNull(column)) units[unchecked((ulong)reader.GetInt64(column))] = id;
        }
        return (tables, units);
    }
    private void RemoveExpiredObjects()
    {
        foreach (var id in objects.Keys.ToArray())
        {
            var observed = objects[id];
            // Cached local temp storage changes between its internal hex name
            // and the user's padded name without changing physical identity.
            // Keep its invocation ownership across that rename, including a
            // retired owner so returning cached tables remain hidden.
            if (metadata.TryGetValue(id, out var renamed) && renamed.Created == observed.Table.Created &&
                renamed.InternalName != observed.Table.InternalName && renamed.Variable != observed.Table.Variable)
            {
                observed.Table = renamed; observed.FriendlyName = null;
                foreach (var key in names.Keys.Where(n => n.Object == id).ToArray()) names.Remove(key);
                constraints.RemoveAll(c => c.Objects.Contains(id));
                continue;
            }
            if (!metadata.TryGetValue(id, out var live) || live.InternalName != objects[id].Table.InternalName || live.Created != objects[id].Table.Created)
            {
                objects.Remove(id);
                foreach (var key in names.Keys.Where(n => n.Object == id).ToArray()) names.Remove(key);
                constraints.RemoveAll(c => c.Objects.Contains(id));
            }
        }
    }

    private void PruneHistory(DateTime watermark)
    {
        // Retain history until XE has passed the frame's exit, then keep only
        // the owners needed to recognize cached objects in later invocations.
        var retired = scopeHistory.Values.Where(s => s.Exited < watermark.AddMilliseconds(-20)).Select(s => s.Id).ToHashSet();
        foreach (var key in names.Keys.Where(k => retired.Contains(k.Scope)).ToArray()) names.Remove(key);
        constraints.RemoveAll(c => retired.Contains(c.Scope));
        statements.RemoveAll(s => retired.Contains(s.Scope));
        var owners = objects.Values.Select(o => o.Owner).ToHashSet();
        foreach (long id in retired.Where(id => !owners.Contains(id))) { scopeHistory.Remove(id); enteredScopes.Remove(id); }
    }

    private void BindModuleEntries()
    {
        foreach (var entry in moduleEntries.Where(e => e.Scope == 0))
        {
            // The native push follows allocation/module_start and may be delayed
            // by source loading. Match each invocation once, including recursion.
            int? level = scopeHistory.TryGetValue(entry.CallerScope, out var caller) ? caller.Level + 1 : null;
            var scope = scopeHistory.Values.Where(s => s.ObjectId == entry.ObjectId && !enteredScopes.Contains(s.Id) &&
                s.Entered >= entry.Started.AddMilliseconds(-20) && s.Exited >= entry.Started && (!level.HasValue || s.Level == level))
                .OrderBy(s => Math.Abs((s.Entered - entry.Started).Ticks)).FirstOrDefault();
            if (scope == null) continue;
            entry.Scope = scope.Id; enteredScopes.Add(scope.Id);
            foreach (var table in entry.Tables)
                if (objects.TryGetValue(table.Id, out var observed) && observed.Table == table)
                    observed.Owner = scope.Id;
        }
    }

    private void Process(XElement e, long sequence)
    {
        string kind = (string?)e.Attribute("name") ?? "";
        int objectId = (int)Number(Data(e, "object_id"));
        int nest = (int)Number(Data(e, "nest_level"));
        int offset = (int)Number(Data(e, "offset"));
        if (kind == "module_start")
        {
            var call = statements.LastOrDefault();
            var timestamp = DateTime.Parse((string)e.Attribute("timestamp")!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            // A ring-buffer gap can remove the enclosing EXEC even though the
            // initialization locks and module_start survived. Use only the
            // immediately preceding unowned locks in that case.
            var ids = (call?.EntryObjects ?? new HashSet<int>()).Concat(orphanEntryObjects.Where(p =>
                p.Value >= timestamp.AddMilliseconds(-20) && p.Value <= timestamp).Select(p => p.Key)).Distinct();
            var tables = ids.Where(objects.ContainsKey).Select(id => objects[id].Table).ToArray();
            call?.EntryObjects.Clear();
            orphanEntryObjects.Clear();
            moduleEntries.Add(new ModuleEntry(objectId, timestamp, call?.Scope ?? 0, tables));
            BindModuleEntries();
            return;
        }
        if (kind == "module_end")
        {
            int index = moduleEntries.FindLastIndex(m => m.ObjectId == objectId);
            if (index >= 0) moduleEntries.RemoveRange(index, moduleEntries.Count - index);
            // Objects locked during return/cache cleanup do not belong to the
            // next module's initialization group.
            statements.LastOrDefault()?.EntryObjects.Clear();
            orphanEntryObjects.Clear();
            return;
        }
        if (kind.EndsWith("statement_starting", StringComparison.Ordinal))
        {
            DateTime timestamp = DateTime.Parse((string?)e.Attribute("timestamp") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var history = scopeHistory.Values.Where(s => s.Entered <= timestamp.AddMilliseconds(20) && s.Exited >= timestamp);
            var scope = kind.StartsWith("sp_", StringComparison.Ordinal)
                ? history.Where(s => s.ObjectId == objectId).OrderBy(s => Math.Abs(s.Level - nest)).ThenByDescending(s => s.Id).FirstOrDefault()
                : history.Where(s => s.ObjectId == 0).OrderByDescending(s => s.Level).ThenByDescending(s => s.Id).FirstOrDefault();
            string sql = Data(e, "statement");
            statements.Add(new Statement(kind[..^8], objectId, nest, offset, scope?.Id ?? 0,
                SqlTableReferences.Variables(sql, scope?.Variables), sql));
            return;
        }
        if (kind.EndsWith("statement_completed", StringComparison.Ordinal))
        {
            int index = statements.FindLastIndex(s => s.Kind == kind[..^9] && s.ObjectId == objectId && s.Nest == nest && s.Offset == offset);
            if (index >= 0)
            {
                for (int i = index; i < statements.Count; i++) Complete(statements[i]);
                statements.RemoveRange(index, statements.Count - index);
            }
            return;
        }
        if (kind != "lock_acquired") return;
        if (objectId == 0 && ulong.TryParse(Data(e, "associated_object_id"), out ulong allocation)) allocations.TryGetValue(allocation, out objectId);
        if (!metadata.TryGetValue(objectId, out var table)) return;
        var current = statements.LastOrDefault();
        // Locks at procedure entry/exit can arrive while only the caller's EXEC
        // remains in XE context. They do not prove ownership by that caller.
        long owner = current != null && !IsExecute(current.Sql) ? current.Scope : 0;
        bool global = table.InternalName.StartsWith("##", StringComparison.Ordinal);
        if (!objects.TryGetValue(objectId, out var observed))
        {
            objects[objectId] = observed = new Observed(table, sequence, 0);
        }
        observed.Sequence = sequence;
        if (current == null && !global && observed.Owner == 0)
            orphanEntryObjects[objectId] = DateTime.Parse((string)e.Attribute("timestamp")!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        if (current != null)
        {
            if (!global && (table.Variable || IsExecute(current.Sql)) && (observed.Owner == 0 || !scopes.Values.Any(s => s.Id == observed.Owner)))
                current.EntryObjects.Add(objectId);
            if (table.Variable) current.Objects.Add(objectId);
            else
            {
                var references = SqlTableReferences.Tokens(current.Sql).Select(t => t.TrimStart('\0'))
                    .Where(t => t.StartsWith('#') && table.InternalName.StartsWith(t, StringComparison.OrdinalIgnoreCase) &&
                        (table.InternalName.Length == t.Length || Regex.IsMatch(table.InternalName[t.Length..], @"^_+[0-9A-Fa-f]{12}$")))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (references.Length == 1) observed.FriendlyName = table.InternalName[..references[0].Length];
            }
            // Ownership is set at creation/first observation, not by an inherited
            // temp table's later use inside a child procedure.
            bool retiredOwner = observed.Owner != 0 && !scopes.Values.Any(s => s.Id == observed.Owner);
            bool referenced = table.Variable ? current.Variables.Count > 0 : observed.FriendlyName != null;
            if (!global && owner != 0 && referenced && (observed.Owner == 0 || current.Sql.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
                (retiredOwner && scopeHistory.TryGetValue(observed.Owner, out var previous) && scopeHistory.TryGetValue(owner, out var next) && previous.Source.Key == next.Source.Key && previous.Level == next.Level)))
                observed.Owner = owner;
        }
    }

    private void Complete(Statement statement)
    {
        if (statement.Scope == 0 || IsExecute(statement.Sql) || statement.Variables.Count == 0 || statement.Objects.Count != statement.Variables.Count) return;
        if (!constraints.Any(c => c.Scope == statement.Scope && c.Variables.SetEquals(statement.Variables) && c.Objects.SetEquals(statement.Objects)))
            constraints.Add(new Constraint(statement.Scope, statement.Variables, statement.Objects));
        ResolveConstraints();
    }

    private static bool IsExecute(string sql)
    {
        string? first = SqlTableReferences.Tokens(sql).FirstOrDefault();
        return string.Equals(first, "EXEC", StringComparison.OrdinalIgnoreCase) || string.Equals(first, "EXECUTE", StringComparison.OrdinalIgnoreCase);
    }

    private void ResolveConstraints()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var constraint in constraints)
            {
                var remainingObjects = constraint.Objects.Where(id => objects.ContainsKey(id) && !names.ContainsKey((constraint.Scope, id))).ToArray();
                var used = constraint.Objects.Where(id => names.ContainsKey((constraint.Scope, id))).Select(id => names[(constraint.Scope, id)]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remainingNames = constraint.Variables.Where(v => !used.Contains(v)).ToArray();
                if (remainingObjects.Length == 1 && remainingNames.Length == 1)
                {
                    int id = remainingObjects[0]; string name = remainingNames[0];
                    names[(constraint.Scope, id)] = name;
                    if (scopeHistory.TryGetValue(constraint.Scope, out var scope))
                    {
                        var tokens = SqlTableReferences.Tokens(scope.Source.Text);
                        if (Enumerable.Range(1, tokens.Count - 1).Any(i => tokens[i].Equals(name, StringComparison.OrdinalIgnoreCase) && tokens[i - 1].Equals("DECLARE", StringComparison.OrdinalIgnoreCase)))
                            objects[id].Owner = constraint.Scope;
                    }
                    changed = true;
                }
            }
        } while (changed);
    }

    private static string Data(XElement e, string name) => e.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == name)?.Element("value")?.Value ?? "";
    private static string Action(XElement e, string name) => e.Elements("action").FirstOrDefault(d => (string?)d.Attribute("name") == name)?.Element("value")?.Value ?? "";
    private static long Number(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : 0;

    public void Dispose()
    {
        cancellation.Cancel();
        collector?.GetAwaiter().GetResult();
        try
        {
                using var command = observer.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = $"IF EXISTS(SELECT 1 FROM sys.server_event_sessions WHERE name=@name) DROP EVENT SESSION {SqlCatalog.Quote(sessionName)} ON SERVER;";
                command.Parameters.AddWithValue("@name", sessionName);
                command.ExecuteNonQuery();
        }
        finally { observer.Dispose(); cancellation.Dispose(); }
    }
}
