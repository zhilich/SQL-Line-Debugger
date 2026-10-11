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
            TestHighlightBoundaries(window);
            File.WriteAllText(path, "PASS: 240-line CRLF/Unicode source; unchanged source refresh preserves viewport and updates spans; five visible steps stay still; off-screen step scrolls into view; old highlight clears.\nPASS: LF/CRLF statement markers exclude following whitespace and indentation while preserving multiline content and final tokens.\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(path, ex.ToString()); return 1; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void TestHighlightBoundaries(Form window)
    {
        foreach (string newline in new[] { "\n", "\r\n" })
        {
            string raw = $"-- Unicode \u03A9 \U0001F6F0{newline}DECLARE @number int = 7;{newline}    {newline}\t  SET @number = 14;{newline}SELECT{newline}    @number AS Value;{newline}    ";
            var source = new SourceDocument("marker-test", "Marker", "Test", raw, Array.Empty<StatementSpan>());
            using var editor = new SqlEditor(source, new BreakpointStore());
            window.Controls.Add(editor); editor.BringToFront(); Application.DoEvents();
            var text = editor.TextBox;
            void RequireColor(int index, Color color, string reason)
            { text.Select(index, 1); Require(text.SelectionBackColor == color, reason); }
            Color marker = Color.FromArgb(255, 239, 156);
            int declaration = raw.IndexOf("DECLARE", StringComparison.Ordinal);
            int nextStatement = raw.IndexOf("SET", StringComparison.Ordinal);
            // SQL can include all whitespace preceding the next statement.
            editor.Highlight(new(0, declaration * 2, (nextStatement - 1) * 2));
            string display = SqlEditor.Display(raw);
            int declarationEnd = display.IndexOf(';') + 1;
            RequireColor(declarationEnd - 1, marker, "DECLARE final semicolon lost its highlight");
            int nextLineStart = display.IndexOf('\n', declarationEnd) + 1;
            RequireColor(nextLineStart, Color.White, "Marker includes the following blank line");
            int setStart = display.IndexOf("SET", StringComparison.Ordinal);
            for (int index = display.LastIndexOf('\n', setStart) + 1; index <= setStart; index++)
                RequireColor(index, Color.White, "Marker includes the next statement's indentation or token");

            int selectStart = raw.IndexOf("SELECT", StringComparison.Ordinal);
            editor.Highlight(new(1, selectStart * 2, (raw.Length - 1) * 2));
            int continuation = display.LastIndexOf("@number", StringComparison.Ordinal);
            RequireColor(continuation - 1, marker, "Multiline statement indentation lost its highlight");
            int finalToken = display.LastIndexOf(';');
            RequireColor(finalToken, marker, "Multiline statement final token lost its highlight");
            for (int index = finalToken + 2; index < display.Length; index++)
                RequireColor(index, Color.White, "Marker includes whitespace after the final statement");
        }
    }
}
