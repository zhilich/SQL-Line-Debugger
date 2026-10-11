using System.Data;
using System.Diagnostics;

namespace SqlLineDebugger;

internal sealed class MainForm : Form
{
    private readonly TextBox server = new() { Text = @"(localdb)\mssqllocaldb", Width = 220 };
    private readonly TextBox database = new() { Text = "tempdb", Width = 140 };
    private readonly TabControl sourceTabs = new() { Dock = DockStyle.Fill };
    private readonly TabControl bottomTabs = new() { Dock = DockStyle.Fill };
    private readonly TabControl resultTabs = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 10), BackColor = Color.FromArgb(247, 249, 252), BorderStyle = BorderStyle.None };
    private readonly DataGridView locals = Grid();
    private readonly ListBox stack = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10), DisplayMember = "Name" };
    private readonly ListBox modules = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DisplayMember = "DisplayName" };
    private readonly ListBox breakpointList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly ToolStripStatusLabel status = new("Ready — load demo or enter a single SQL batch.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStrip toolstrip = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6), ImageScalingSize = new Size(20, 20) };
    private readonly BreakpointStore breakpoints = new();
    private readonly Dictionary<string, SqlEditor> editors = new();
    private readonly SqlEditor query;
    private DebugSession? session;
    private bool closing;
    private bool loadingModules;
    private string? smokeDirectory;
    private bool smokeCaptured;
    private Exception? lastError;
    private readonly List<double> stepTimes = new();
    private long pendingStep;
    private ToolStripButton startButton = null!, demoButton = null!, intoButton = null!, overButton = null!, outButton = null!, pauseButton = null!, stopButton = null!, loadButton = null!, openButton = null!;
    public MainForm()
    {
        locals.ShowCellToolTips = false;
        Text = "SQL Line Debugger"; MinimumSize = new Size(1050, 700); Size = new Size(1440, 920); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Color.White;
        var connections = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 7, 5, 4), WrapContents = false };
        connections.Controls.AddRange(new Control[] { new Label { Text = "Server", AutoSize = true, Padding = new Padding(0, 4, 4, 0) }, server,
            new Label { Text = "Database", AutoSize = true, Padding = new Padding(12, 4, 4, 0) }, database,
            new Label { Text = "Windows authentication  •  Native SQL debugging", AutoSize = true, ForeColor = Color.SlateGray, Padding = new Padding(16, 4, 0, 0) } });
        AddButton(ref startButton, "▶ Start / Continue (F5)", async () => { if (session?.IsRunning == true) session.Resume(DebugAction.Continue); else await Start(false); });
        AddButton(ref demoButton, "Demo", async () => await Start(true));
        toolstrip.Items.Add(new ToolStripSeparator());
        AddButton(ref intoButton, "Step Into (F11)", () => session?.Resume(DebugAction.Into));
        AddButton(ref overButton, "Step Over (F10)", () => session?.Resume(DebugAction.Over));
        AddButton(ref outButton, "Step Out (⇧F11)", () => session?.Resume(DebugAction.Out));
        AddButton(ref pauseButton, "Pause", () => session?.Break());
        AddButton(ref stopButton, "■ Stop (⇧F5)", Stop);
        toolstrip.Items.Add(new ToolStripSeparator());
        var toggle = new ToolStripButton("● Breakpoint (F9)"); toggle.Click += (_, _) => ActiveEditor?.ToggleAtCaret(); toolstrip.Items.Add(toggle);
        AddButton(ref loadButton, "Load Modules", async () => await LoadModules());
        AddButton(ref openButton, "Open SQL", OpenSql);
        var save = new ToolStripButton("Save SQL"); save.Click += (_, _) => SaveSql(); toolstrip.Items.Add(save);

        var vertical = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Height = 710, SplitterDistance = 430, Panel1MinSize = 180, Panel2MinSize = 160 };
        var horizontal = new SplitContainer { Dock = DockStyle.Fill, Width = 1260, SplitterDistance = 850, Panel1MinSize = 500, Panel2MinSize = 260 };
        horizontal.Panel1.Controls.Add(sourceTabs);
        var inspection = new TabControl { Dock = DockStyle.Fill };
        inspection.TabPages.Add(Page("Locals", locals)); inspection.TabPages.Add(Page("Call Stack", stack)); inspection.TabPages.Add(Page("Modules", modules)); inspection.TabPages.Add(Page("Breakpoints", breakpointList));
        horizontal.Panel2.Controls.Add(inspection); vertical.Panel1.Controls.Add(horizontal);
        Shown += (_, _) => { vertical.SplitterDistance = (int)(vertical.Height * 0.65); horizontal.SplitterDistance = (int)(horizontal.Width * 0.70); };
        bottomTabs.TabPages.Add(Page("Debug Output", output)); bottomTabs.TabPages.Add(Page("Result Sets", resultTabs)); vertical.Panel2.Controls.Add(bottomTabs);
        var strip = new StatusStrip(); strip.Items.Add(status);
        Controls.Add(vertical); Controls.Add(toolstrip); Controls.Add(connections); Controls.Add(strip);
        toolstrip.Dock = DockStyle.Top;
        query = new SqlEditor(new("batch", "Query", "tempdb", "-- Enter a single batch, or click Demo.\nDECLARE @value int = 7;\nSET @value = @value * 2;\nPRINT N'Hello from SQL Server';\nSELECT @value AS Value;", Array.Empty<StatementSpan>()), breakpoints, true);
        AddEditor(query);
        modules.DoubleClick += (_, _) => { if (modules.SelectedItem is SourceDocument source) ShowSource(source); };
        stack.DoubleClick += (_, _) => { if (stack.SelectedItem is FrameItem item) ShowSource(item.Frame.Source); };
        KeyPreview = true;
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F9) ActiveEditor?.ToggleAtCaret();
            else if (e.KeyCode == Keys.F5 && e.Shift) Stop();
            else if (e.KeyCode == Keys.F5) { if (session?.IsRunning == true) session.Resume(DebugAction.Continue); else await Start(false); }
            else if (e.KeyCode == Keys.F10) session?.Resume(DebugAction.Over);
            else if (e.KeyCode == Keys.F11) session?.Resume(e.Shift ? DebugAction.Out : DebugAction.Into);
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        };
        FormClosing += (_, e) => { if (session?.IsRunning == true) { e.Cancel = true; closing = true; Stop(); } };
        UpdateState();
    }
    private static TabPage Page(string title, Control control) { var page = new TabPage(title); page.Controls.Add(control); return page; }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private void AddButton(ref ToolStripButton field, string text, Action action)
    { field = new ToolStripButton(text); field.Click += (_, _) => action(); toolstrip.Items.Add(field); }
    private SqlEditor? ActiveEditor => sourceTabs.SelectedTab?.Controls.OfType<SqlEditor>().FirstOrDefault();
    private void Dispatch(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    private void Log(string message) { output.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}"); output.SelectionStart = output.TextLength; output.ScrollToCaret(); }
    private void AddEditor(SqlEditor editor)
    {
        editors[editor.Source.Key] = editor;
        editor.BreakpointsChanged += UpdateBreakpoints;
        sourceTabs.TabPages.Add(Page(editor.Source.DisplayName, editor));
    }
    private SqlEditor ShowSource(SourceDocument source)
    {
        if (!editors.TryGetValue(source.Key, out var editor)) { editor = new SqlEditor(source, breakpoints); AddEditor(editor); }
        else if (editor.Source.Hash != source.Hash)
        {
            if (breakpoints.All.Any(b => b.SourceKey == source.Key)) Log($"Source changed: {source.Name}. Old breakpoints are unbound; reset them against the new source.");
            editor.LoadSource(source);
        }
        else if (editor != query) editor.LoadSource(source);
        sourceTabs.SelectedTab = sourceTabs.TabPages.Cast<TabPage>().First(p => p.Controls.Contains(editor));
        return editor;
    }
    private void UpdateBreakpoints()
    {
        breakpointList.Items.Clear();
        foreach (var point in breakpoints.All) breakpointList.Items.Add($"{point.Name}: line {point.Line}");
        foreach (var editor in editors.Values) editor.RefreshMargin();
    }
    private async Task LoadModules()
    {
        loadingModules = true; UpdateState();
        try
        {
            string connection = SqlCatalog.ConnectionString(server.Text, database.Text);
            var sources = await Task.Run(() => SqlCatalog.LoadModules(connection));
            modules.Items.Clear(); foreach (var source in sources) modules.Items.Add(source);
            Log($"Loaded {sources.Count} module(s). Double-click a module to set breakpoints before starting.");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { loadingModules = false; UpdateState(); }
    }
    private async Task Start(bool demo)
    {
        if (session?.IsRunning == true || loadingModules) return;
        lastError = null;
        resultTabs.TabPages.Clear(); output.Clear(); locals.DataSource = null; stack.Items.Clear();
        query.RefreshQuery(); query.TextBox.ReadOnly = true;
        session?.Dispose();
        session = new DebugSession(SqlCatalog.ConnectionString(server.Text, database.Text), breakpoints);
        session.Output += text => Dispatch(() => Log(text));
        session.SourceLoaded += source => Dispatch(() =>
        {
            if (source.Key == "batch" && demo) query.LoadSource(source);
            if (demo && source.Database == "tempdb" && source.Key != "batch" && !modules.Items.Cast<SourceDocument>().Any(s => s.Key == source.Key)) modules.Items.Add(source);
        });
        session.ResultSet += table => Dispatch(() => { var grid = Grid(); grid.DataSource = table; resultTabs.TabPages.Add(Page(table.TableName, grid)); });
        session.Running += () => Dispatch(() => { status.Text = "Running…"; UpdateState(); });
        session.Paused += snapshot => Dispatch(() =>
        {
            if (pendingStep != 0) { stepTimes.Add(Stopwatch.GetElapsedTime(pendingStep).TotalMilliseconds); pendingStep = 0; }
            DebugTrace.Write("UI pause begin " + snapshot.Source.DisplayName + ":" + snapshot.Line);
            var active = ShowSource(snapshot.Source);
            foreach (var editor in editors.Values) if (editor != active) editor.ClearHighlight();
            DebugTrace.Write("UI clear highlights end");
            active.Highlight(snapshot.Span);
            DebugTrace.Write("UI show/highlight source end");
            locals.DataSource = snapshot.Variables.Select(v => new { v.Name, v.Type, v.Value }).ToList();
            stack.Items.Clear(); foreach (var frame in snapshot.Stack) stack.Items.Add(new FrameItem(frame));
            status.Text = $"{snapshot.Reason} • {snapshot.Source.Database}.{snapshot.Source.DisplayName} • Line {snapshot.Line} • SPID {snapshot.Spid}";
            UpdateState();
            DebugTrace.Write("UI pause display complete");
            if (smokeDirectory != null)
            {
                if (!smokeCaptured && snapshot.Variables.Any(v => v.Name == "@answer" && v.Value == "14"))
                {
                    active.ToggleAtCaret(); UpdateBreakpoints();
                    DebugTrace.Write("UI preview begin");
                    SavePreview(Path.Combine(smokeDirectory, "debugger-paused.png")); smokeCaptured = true;
                    DebugTrace.Write("UI preview end");
                }
                pendingStep = Stopwatch.GetTimestamp();
                session.Resume(DebugAction.Into);
            }
        });
        try
        {
            var task = session.RunAsync(query.Source.Text, demo); UpdateState(); status.Text = "Connecting…";
            await task;
            status.Text = "Session ended.";
            if (resultTabs.TabPages.Count > 0) bottomTabs.SelectedIndex = 1;
        }
        catch (Exception ex) { lastError = ex; Log(ex.ToString()); status.Text = "Debugging failed — see Debug Output."; bottomTabs.SelectedIndex = 0; }
        finally
        {
            query.TextBox.ReadOnly = false;
            foreach (var editor in editors.Values) editor.ClearHighlight();
            locals.DataSource = null; stack.Items.Clear(); UpdateState();
            if (closing) Close();
        }
    }
    private void Stop()
    {
        if (session?.IsRunning != true) return;
        status.Text = "Cancelling…"; stopButton.Enabled = false;
        // Never block the UI on a SQL attention request.
        _ = Task.Run(session.Stop);
    }
    private void UpdateState()
    {
        bool running = session?.IsRunning == true, paused = running && session!.IsPaused;
        startButton.Enabled = (!running || paused) && !loadingModules; demoButton.Enabled = !running && !loadingModules;
        intoButton.Enabled = overButton.Enabled = outButton.Enabled = paused;
        pauseButton.Enabled = running && !paused; stopButton.Enabled = running;
        loadButton.Enabled = openButton.Enabled = !running && !loadingModules; server.Enabled = database.Enabled = !running && !loadingModules;
    }
    private void OpenSql()
    {
        using var dialog = new OpenFileDialog { Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { query.LoadSource(new("batch", "Query", database.Text, File.ReadAllText(dialog.FileName), Array.Empty<StatementSpan>())); sourceTabs.SelectedIndex = 0; }
    }
    private void SaveSql()
    {
        if (ActiveEditor is not { } editor) return;
        using var dialog = new SaveFileDialog { Filter = "SQL scripts (*.sql)|*.sql", FileName = "Query.sql" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { editor.RefreshQuery(); File.WriteAllText(dialog.FileName, editor.Source.Text); }
    }
    private sealed record FrameItem(StackFrame Frame) { public string Name => $"[{Frame.Level}] {Frame.Source.Database}.{Frame.Source.DisplayName}"; }
    internal async Task<int> SmokeAsync(string directory)
    {
        smokeDirectory = directory;
        Directory.CreateDirectory(directory);
        await Start(true);
        if (lastError != null) throw lastError;
        if (!smokeCaptured || resultTabs.TabPages.Count != 2) throw new InvalidOperationException("UI smoke test failed to display the function pause or both result sets.");
        if (stepTimes.Count < 10 || stepTimes.Any(ms => ms > 2000))
            throw new InvalidOperationException("UI step latency regression (limit 2000 ms): " + string.Join(", ", stepTimes.Select(ms => ms.ToString("F1"))));
        SavePreview(Path.Combine(directory, "debugger-results.png"));
        File.WriteAllText(Path.Combine(directory, "ui-tests.log"), "PASS: Windows Forms created, nested function source highlighted, @answer=14 displayed, gutter breakpoint toggled, two result-set grids displayed.\n" +
            "Step latencies (ms): " + string.Join(", ", stepTimes.Select(ms => ms.ToString("F1"))) + "\n" + output.Text);
        return 0;
    }
    private void SavePreview(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
