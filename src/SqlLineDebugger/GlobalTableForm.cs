namespace SqlLineDebugger;

internal sealed class GlobalTableForm : Form
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Label message = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "The ## prefix is added automatically." };
    private bool busy, closeRequested;
    internal TextBox NameInput { get; } = new() { Dock = DockStyle.Top };
    internal Button CopyButton { get; } = new() { Text = "Copy", Width = 90 };
    internal GlobalTableCopy? Copy { get; private set; }
    internal Exception? CopyError { get; private set; }

    internal GlobalTableForm(string sourceName, Func<string, CancellationToken, Task<GlobalTableCopy>> copy)
    {
        Text = "Copy to global temporary table";
        ClientSize = new Size(480, 170); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false;
        Font = new Font("Segoe UI", 10); Padding = new Padding(12);
        NameInput.Text = GlobalTempTableCopies.DefaultName(sourceName);
        var label = new Label { Text = "Global temp table name", Dock = DockStyle.Top, Height = 28 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(CopyButton);
        var hint = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) }; hint.Controls.Add(message);
        Controls.Add(hint); Controls.Add(buttons); Controls.Add(NameInput); Controls.Add(label);
        AcceptButton = CopyButton; CancelButton = cancel;
        Shown += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
        CopyButton.Click += async (_, _) =>
        {
            if (busy) return;
            try
            {
                string name = GlobalTempTableCopies.NormalizeName(NameInput.Text);
                busy = true; NameInput.Enabled = CopyButton.Enabled = false;
                CopyError = null; message.ForeColor = SystemColors.ControlText; message.Text = "Copying snapshot…";
                Copy = await copy(name, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { CopyError = ex; message.ForeColor = Color.Firebrick; message.Text = ex.Message; }
            finally
            {
                busy = false;
                if (!IsDisposed)
                {
                    NameInput.Enabled = CopyButton.Enabled = true;
                    if (closeRequested) DialogResult = DialogResult.Cancel;
                    else if (Copy != null) DialogResult = DialogResult.OK;
                    if (Copy != null || closeRequested) Close();
                }
            }
        };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            closeRequested = true; cancellation.Cancel(); e.Cancel = true; DialogResult = DialogResult.None;
        };
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { cancellation.Cancel(); cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
