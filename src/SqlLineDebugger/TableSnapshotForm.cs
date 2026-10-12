using System.Data;
using System.Globalization;

namespace SqlLineDebugger;

internal sealed class TableSnapshotForm : Form
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Label message = new() { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8), Text = "Reading snapshot…" };
    internal DataGridView RowsGrid { get; } = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, AutoGenerateColumns = false
    };
    internal Task<DataTable>? CaptureTask { get; private set; }
    internal DataTable? Snapshot { get; private set; }
    internal Exception? CaptureError { get; private set; }
    internal event Action? SnapshotFinished;

    internal TableSnapshotForm(string name, Func<CancellationToken, Task<DataTable>> inspect)
    {
        Text = name + " — Snapshot";
        Size = new Size(1000, 620); MinimumSize = new Size(600, 350);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var close = new Button { Text = "Close", Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(5) }; footer.Controls.Add(close);
        Controls.Add(RowsGrid); Controls.Add(message); Controls.Add(footer); CancelButton = close;
        RowsGrid.CellFormatting += (_, e) =>
        {
            if (e.Value == DBNull.Value || e.Value is System.Data.SqlTypes.INullable { IsNull: true }) { e.Value = "NULL"; e.FormattingApplied = true; }
            else if (e.Value is byte[] bytes) { e.Value = "0x" + Convert.ToHexString(bytes); e.FormattingApplied = true; }
            else if (e.Value is System.Data.SqlTypes.SqlDecimal number) { e.Value = number.ToString(); e.FormattingApplied = true; }
            else if (e.Value is DateTime date)
            {
                var column = Snapshot?.Columns[RowsGrid.Columns[e.ColumnIndex].DataPropertyName];
                e.Value = date.ToString(column?.ExtendedProperties["SqlType"] as string == "date" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                e.FormattingApplied = true;
            }
            else if (e.Value is DateTimeOffset offset) { e.Value = offset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture); e.FormattingApplied = true; }
            else if (e.Value is TimeSpan time) { e.Value = time.ToString("c", CultureInfo.InvariantCulture); e.FormattingApplied = true; }
        };
        Shown += async (_, _) =>
        {
            try
            {
                CaptureTask = inspect(cancellation.Token);
                Snapshot = await CaptureTask;
                if (IsDisposed) return;
                // Auto-generation treats byte[] as images and bool as checkboxes.
                // Every SQL value is rendered as text (hex, NULL, dates, etc.),
                // so use text cells while retaining the bound column's native type.
                foreach (DataColumn sourceColumn in Snapshot.Columns)
                    RowsGrid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        Name = sourceColumn.ColumnName, HeaderText = sourceColumn.ColumnName,
                        DataPropertyName = sourceColumn.ColumnName, ValueType = sourceColumn.DataType,
                        DefaultCellStyle = new DataGridViewCellStyle { NullValue = "NULL" }
                    });
                RowsGrid.DataSource = Snapshot;
                foreach (DataGridViewColumn column in RowsGrid.Columns) { column.Width = Math.Min(320, Math.Max(80, column.Width)); column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None; }
                message.Text = $"{Snapshot.Rows.Count:N0} row(s) — captured once";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { CaptureError = ex; if (!IsDisposed) message.Text = ex.Message; }
            finally { if (!IsDisposed) SnapshotFinished?.Invoke(); }
        };
        FormClosed += (_, _) => cancellation.Cancel();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation.Cancel();
        base.Dispose(disposing);
    }
}
