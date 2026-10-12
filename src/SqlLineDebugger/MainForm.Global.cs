namespace SqlLineDebugger;

internal sealed partial class MainForm
{
    private void CopyTableToGlobal(TempDbObject table)
    {
        if (session is not { IsPaused: true } target) return;
        using var dialog = new GlobalTableForm(table.Name,
            (name, cancellation) => target.CopyToGlobalAsync(table, name, globalCopies, cancellation));
        globalCopyOpenedTest?.Invoke(dialog);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Copy is { } copy)
        {
            Log($"Copied {copy.RowCount:N0} row(s) from {table.Name} to {copy.Name}.");
            status.Text = $"Copied {copy.RowCount:N0} row(s) to {copy.Name}";
        }
    }

}
