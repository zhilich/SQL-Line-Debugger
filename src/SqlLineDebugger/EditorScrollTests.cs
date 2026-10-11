using System.Runtime.InteropServices;

namespace SqlLineDebugger;

internal static class EditorScrollTests
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static int FirstLine(RichTextBox text) => (int)SendMessage(text.Handle, 0xCE, IntPtr.Zero, IntPtr.Zero);
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "editor-scroll-tests.log");
        try
        {
            string raw = string.Join("\r\n", Enumerable.Range(1, 240).Select(i => $"SET @value = {i}; -- statement {i} / \u03A9"));
            var source = new SourceDocument("scroll-test", "Long procedure", "Test", raw, Array.Empty<StatementSpan>());
            using var window = new Form { Size = new Size(850, 500), Opacity = 0, ShowInTaskbar = false };
            using var editor = new SqlEditor(source, new BreakpointStore());
            window.Controls.Add(editor); window.Show(); Application.DoEvents();
            var text = editor.TextBox;
            text.Select(text.GetFirstCharIndexFromLine(154), 0); text.ScrollToCaret();
            StatementSpan Span(int line)
            {
                int start = raw.IndexOf($"SET @value = {line};", StringComparison.Ordinal);
                int length = $"SET @value = {line};".Length;
                return new(line, start * 2, (start + length - 1) * 2);
            }
            editor.Highlight(Span(140));
            int baseline = FirstLine(text), textChanges = 0;
            Require(baseline > 100, "Long source scrolled beyond line 100");
            text.TextChanged += (_, _) => textChanges++;
            for (int line = 141; line <= 145; line++)
            {
                // A new snapshot has identical source text but updated spans.
                var snapshotSource = new SourceDocument(source.Key, source.Name, source.Database, raw, new[] { Span(line) });
                int beforeRefresh = textChanges;
                editor.LoadSource(snapshotSource);
                Require(textChanges == beforeRefresh, "Unchanged source was rewritten on refresh");
                Require(FirstLine(text) == baseline, "Source refresh reset the viewport");
                Require(editor.Source.Spans.Count == 1, "Source metadata refreshed");
                editor.Highlight(Span(line)); Application.DoEvents();
                Require(FirstLine(text) == baseline, "Visible next statement changed the viewport");
            }
            Require(text.Text == SqlEditor.Display(raw), "Highlighting changed SQL text");
            editor.ClearHighlight();
            Require(FirstLine(text) == baseline, "Clearing the highlight changed the viewport");
            editor.Highlight(Span(220)); Application.DoEvents();
            var caret = text.GetPositionFromCharIndex(text.SelectionStart);
            Require(FirstLine(text) > baseline && caret.Y >= 0 && caret.Y + text.Font.Height <= text.ClientSize.Height,
                "Off-screen statement was not revealed");
            text.Select(text.GetFirstCharIndexFromLine(144), 1);
            Require(text.SelectionBackColor == Color.White, "Previous statement highlight remained");
            text.Select(text.GetFirstCharIndexFromLine(219), 1);
            Require(text.SelectionBackColor == Color.FromArgb(255, 239, 156), "Current statement highlight missing");
            File.WriteAllText(path, "PASS: 240-line CRLF/Unicode source; unchanged source refresh preserves viewport and updates spans; five visible steps stay still; off-screen step scrolls into view; old highlight clears.\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(path, ex.ToString()); return 1; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
