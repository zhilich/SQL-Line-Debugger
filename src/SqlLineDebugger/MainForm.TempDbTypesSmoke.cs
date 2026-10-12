using System.Data;
using System.Reflection;

namespace SqlLineDebugger;

internal sealed partial class MainForm
{
    internal async Task<int> TempDbTypesSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        database.Text = "Test";
        query.LoadSource(new("batch", "Query", "Test", """
            CREATE TABLE #MoneyUi(Id int, Amount money, SmallAmount smallmoney);
            INSERT #MoneyUi VALUES(1,-922337203685477.5808,-214748.3648),
                (2,922337203685477.5807,214748.3647),(3,NULL,NULL);
            PRINT N'money-ui-ready';
            CREATE TABLE #Snapshots(Sequence int IDENTITY PRIMARY KEY, ModuleName nvarchar(40),
                [Checkpoint] nvarchar(80), [RowCount] int, Note nvarchar(160));
            EXEC SqlDebugLab.TypesLab @IncludeUnsupported=1;
            """, Array.Empty<StatementSpan>()));
        Exception? failure = null;
        int dialogs = 0;
        var errors = new List<string>();
        snapshotOpenedTest = dialog =>
        {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false;
            // Record formatting errors instead of allowing the default error
            // dialog to block this unattended regression. Any error fails it.
            dialog.RowsGrid.DataError += (_, e) =>
            {
                errors.Add($"row={e.RowIndex}, column={e.ColumnIndex}: {e.Exception}");
                e.ThrowException = false; e.Cancel = true;
            };
            dialog.SnapshotFinished += () =>
            {
                try
                {
                    if (dialog.CaptureError != null) throw dialog.CaptureError;
                    bool money = dialog.Snapshot!.Columns.Contains("SmallAmount");
                    int rowCount = money ? 3 : 2;
                    Require(dialog.Snapshot.Rows.Count == rowCount && dialog.RowsGrid.Rows.Count == rowCount, "snapshot contains all rows");
                    foreach (DataGridViewRow row in dialog.RowsGrid.Rows)
                        foreach (DataGridViewCell cell in row.Cells)
                        {
                            _ = cell.FormattedValue;
                            dialog.RowsGrid.CurrentCell = cell;
                            dialog.RowsGrid.Refresh();
                        }
                    using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                    bitmap.Save(Path.Combine(directory, money ? "tempdb-money-snapshot.png" : "tempdb-types-snapshot.png"), System.Drawing.Imaging.ImageFormat.Png);
                    Require(errors.Count == 0, "snapshot DataError: " + string.Join(Environment.NewLine, errors));
                    Require(dialog.RowsGrid.Columns.Cast<DataGridViewColumn>().All(c => c is DataGridViewTextBoxColumn), "snapshot columns display text for arbitrary SQL types");
                    if (money)
                    {
                        foreach (DataGridViewRow row in dialog.RowsGrid.Rows)
                            foreach (string name in new[] { "Amount", "SmallAmount" })
                            {
                                var cell = row.Cells[name];
                                string expected = cell.Value == DBNull.Value ? "NULL" : ((decimal)cell.Value).ToString(System.Globalization.CultureInfo.CurrentCulture);
                                Require(cell.FormattedValue as string == expected, "money display preserves four places/full range/NULL");
                            }
                        Require(dialog.Snapshot.Columns["Amount"]!.DataType == typeof(decimal) && dialog.Snapshot.Columns["SmallAmount"]!.DataType == typeof(decimal), "money snapshot retains native decimal types");
                        dialogs++;
                        return;
                    }
                    var valueRow = dialog.RowsGrid.Rows.Cast<DataGridViewRow>().Single(r => (int)r.Cells["Id"].Value == 1);
                    var nullRow = dialog.RowsGrid.Rows.Cast<DataGridViewRow>().Single(r => (int)r.Cells["Id"].Value == 2);
                    Require(valueRow.Cells["Flag"].FormattedValue as string == "True", "bit value displays without checkbox type mismatch");
                    Require(valueRow.Cells["Bytes"].FormattedValue as string == "0x001122FF", "binary value displays as hex instead of an image");
                    Require(valueRow.Cells["WideValue"].FormattedValue as string == new string('9', 38), "decimal(38) display preserves all digits");
                    foreach (DataGridViewCell cell in nullRow.Cells.Cast<DataGridViewCell>().Skip(1))
                        Require(cell.FormattedValue as string == "NULL", "NULL display for " + cell.OwningColumn.Name);
                    Require(dialog.Snapshot!.Columns["Flag"]!.DataType == typeof(bool) && dialog.Snapshot.Columns["Bytes"]!.DataType == typeof(byte[]), "snapshot retains native data types");
                    dialogs++;
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            };
        };
        tempDbPauseTest = async snapshot =>
        {
            try
            {
                string sql = snapshot.Source.Statement(snapshot.Span).TrimStart();
                if (sql.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase) && (sql.Contains("Y1 TypesLab") || sql.Contains("money-ui-ready")))
                {
                    string name = sql.Contains("money-ui-ready") ? "#MoneyUi" : "#Types";
                    await TempDbTests.WaitTables(session!, t => t.Any(x => x.Name == name));
                    var inspection = (TabControl)tempTables.Parent!.Parent!;
                    inspection.SelectedTab = (TabPage)tempTables.Parent;
                    int row = -1;
                    for (int retry = 0; retry < 100 && row < 0; retry++)
                    {
                        row = tempTables.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => r.DataBoundItem is TempDbObject t && t.Name == name);
                        if (row < 0) await Task.Delay(50);
                    }
                    Require(row >= 0, name + " bound to TempDB list");
                    typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(tempTables, new object[] { new DataGridViewCellEventArgs(1, row) });
                    if (name == "#MoneyUi") session!.Resume(DebugAction.Into); else session!.Stop();
                }
                else session!.Resume(DebugAction.Into);
            }
            catch (Exception ex) { failure = ex; session!.Stop(); }
        };
        try
        {
            await Start(false);
            if (failure != null) throw failure;
            if (lastError != null) throw lastError;
            Require(dialogs == 2, "actual #MoneyUi and #Types Inspect dialogs exercised");
            File.WriteAllText(Path.Combine(directory, "tempdb-types-ui-tests.log"), "PASS: actual #MoneyUi and #Types Inspect dialogs; every cell formatted and scrolled into view; no DataError; money/smallmoney full ranges/four-place precision, bit/binary/decimal(38)/NULL display correct; snapshots retain native types.\n" + output.Text);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "tempdb-types-ui-tests.log"), ex + "\n" + string.Join(Environment.NewLine, errors));
            return 1;
        }
        finally { tempDbPauseTest = null; snapshotOpenedTest = null; }
        static void Require(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
    }
}
