using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SqlLineDebugger;

internal sealed class SqlEditor : UserControl
{
    private readonly Panel margin = new() { Dock = DockStyle.Left, Width = 65, BackColor = Color.FromArgb(242, 245, 248) };
    public RichTextBox TextBox { get; } = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 11), BorderStyle = BorderStyle.None,
        WordWrap = false, DetectUrls = false, HideSelection = false, AcceptsTab = true, BackColor = Color.White };
    private readonly BreakpointStore breakpoints;
    public SourceDocument Source { get; private set; }
    private int currentLine = -1;
    private int highlightStart, highlightLength;
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref Point point);
    public event Action? BreakpointsChanged;
    public SqlEditor(SourceDocument source, BreakpointStore breakpoints, bool editable = false)
    {
        Dock = DockStyle.Fill;
        Source = source; this.breakpoints = breakpoints;
        Controls.Add(TextBox); Controls.Add(margin);
        TextBox.ReadOnly = !editable;
        TextBox.VScroll += (_, _) => margin.Invalidate();
        TextBox.Resize += (_, _) => margin.Invalidate();
        TextBox.TextChanged += (_, _) => margin.Invalidate();
        margin.Paint += PaintMargin;
        margin.MouseDown += (_, e) =>
        {
            int index = TextBox.GetCharIndexFromPosition(new Point(0, e.Y));
            Toggle(TextBox.GetLineFromCharIndex(index) + 1);
        };
        LoadSource(source);
    }
    public void LoadSource(SourceDocument source)
    {
        string display = Display(source.Text);
        // Snapshots carry updated spans even when the module text is unchanged.
        // Reassigning Text and recoloring it resets the native editor's viewport.
        if (Source.Hash == source.Hash && TextBox.Text == display)
        { Source = source; return; }
        Source = source;
        currentLine = -1; highlightStart = highlightLength = 0;
        TextBox.Text = display;
        TextBox.SelectAll(); TextBox.SelectionColor = Color.FromArgb(38, 44, 52);
        TextBox.SelectionBackColor = Color.White;
        foreach (Match match in Regex.Matches(TextBox.Text, @"\b(SELECT|FROM|WHERE|EXEC|EXECUTE|DECLARE|SET|BEGIN|END|RETURN|AS|CREATE|PROCEDURE|FUNCTION|IF|ELSE|WHILE|PRINT|OUTPUT|RETURNS|INSERT|INTO|VALUES|UPDATE|DELETE|NOCOUNT|ON|USE)\b", RegexOptions.IgnoreCase))
        { TextBox.Select(match.Index, match.Length); TextBox.SelectionColor = Color.FromArgb(30, 81, 180); }
        foreach (Match match in Regex.Matches(TextBox.Text, @"N?'(?:''|[^'])*'|--[^\n]*"))
        { TextBox.Select(match.Index, match.Length); TextBox.SelectionColor = match.Value.StartsWith("--") ? Color.FromArgb(74, 129, 76) : Color.FromArgb(164, 62, 48); }
        TextBox.Select(0, 0); margin.Invalidate();
    }
    public static string Display(string raw) => raw.Replace("\r\n", "\n").Replace('\r', '\n');
    public void RefreshQuery()
    {
        if (!TextBox.ReadOnly) Source = new("batch", "Query", Source.Database, TextBox.Text, Array.Empty<StatementSpan>());
    }
    public void ToggleAtCaret() => Toggle(TextBox.GetLineFromCharIndex(TextBox.SelectionStart) + 1);
    private void Toggle(int line)
    {
        RefreshQuery();
        if (line > 0 && line <= TextBox.Lines.Length) { breakpoints.Toggle(Source, line); margin.Invalidate(); BreakpointsChanged?.Invoke(); }
    }
    public void Highlight(StatementSpan span)
    {
        var range = Source.Range(span);
        int start = Display(Source.Text[..range.Start]).Length;
        // SQL spans may end after the next line's indentation. Trim only the
        // display highlight; retain original server offsets for source mapping.
        int length = Display(Source.Text.Substring(range.Start, range.Length).TrimEnd()).Length;
        UpdateFormatting(() =>
        {
            ClearPreviousHighlight();
            TextBox.Select(start, length); TextBox.SelectionBackColor = Color.FromArgb(255, 239, 156);
            highlightStart = start; highlightLength = length;
            TextBox.Select(start, 0); currentLine = Source.StatementLine(span);
        }, revealCaret: true);
    }
    public void ClearHighlight()
    {
        if (highlightLength == 0) return;
        int caret = TextBox.SelectionStart, length = TextBox.SelectionLength;
        UpdateFormatting(() =>
        {
            ClearPreviousHighlight();
            TextBox.Select(caret, length); currentLine = -1;
        });
    }
    private void ClearPreviousHighlight()
    {
        if (highlightLength == 0) return;
        TextBox.Select(highlightStart, highlightLength); TextBox.SelectionBackColor = Color.White;
        highlightStart = highlightLength = 0;
    }
    private void UpdateFormatting(Action update, bool revealCaret = false)
    {
        Point scroll = default;
        SendMessage(TextBox.Handle, 0x4DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS
        // WM_SETREDRAW(true) can show hidden controls, so only suspend visible ones.
        bool suspendRedraw = TextBox.Visible;
        if (suspendRedraw) SendMessage(TextBox.Handle, 0xB, IntPtr.Zero, IntPtr.Zero);
        try
        {
            update();
            SendMessage(TextBox.Handle, 0x4DE, IntPtr.Zero, ref scroll); // EM_SETSCROLLPOS
            if (revealCaret)
            {
                var position = TextBox.GetPositionFromCharIndex(TextBox.SelectionStart);
                if (position.Y < 0 || position.Y + TextBox.Font.Height > TextBox.ClientSize.Height)
                    TextBox.ScrollToCaret();
            }
        }
        finally
        {
            if (suspendRedraw) SendMessage(TextBox.Handle, 0xB, new IntPtr(1), IntPtr.Zero);
            TextBox.Invalidate(); margin.Invalidate();
        }
    }
    public void RefreshMargin() => margin.Invalidate();
    private void PaintMargin(object? sender, PaintEventArgs e)
    {
        var points = breakpoints.All.Where(b => b.SourceKey == Source.Key && b.SourceHash == Source.Hash).Select(b => b.Line).ToHashSet();
        using var brush = new SolidBrush(Color.FromArgb(115, 127, 142));
        for (int line = 0; line < TextBox.Lines.Length; line++)
        {
            int index = TextBox.GetFirstCharIndexFromLine(line);
            if (index < 0) continue;
            var position = TextBox.GetPositionFromCharIndex(index);
            if (position.Y < -TextBox.Font.Height || position.Y > Height) continue;
            e.Graphics.DrawString((line + 1).ToString(), TextBox.Font, brush, 27, position.Y);
            if (points.Contains(line + 1)) e.Graphics.FillEllipse(Brushes.Firebrick, 7, position.Y + 3, 12, 12);
            if (line + 1 == currentLine) e.Graphics.FillPolygon(Brushes.DarkGoldenrod, new[] { new Point(3, position.Y + 1), new Point(22, position.Y + 9), new Point(3, position.Y + 17) });
        }
    }
}
