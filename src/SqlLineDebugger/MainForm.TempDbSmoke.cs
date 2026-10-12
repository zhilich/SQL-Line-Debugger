using System.Data;
using System.Reflection;

namespace SqlLineDebugger;

internal sealed partial class MainForm
{
    internal async Task<int> TempDbSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        database.Text = "Test";
        query.LoadSource(new("batch", "Query", "Test", """
            DECLARE @Rows table(Id int,Label nvarchar(50),Day date,Stamp datetime2(7),Clock time(7),Zone datetimeoffset(7));
            INSERT @Rows VALUES(1,N'before Ω','20260102','2026-01-02T03:04:05.1234567','03:04:05.1234567','2026-01-02T03:04:05.1234567+05:30');
            CREATE TABLE #UiTemp(Id int,Label nvarchar(50));
            INSERT #UiTemp VALUES(2,N'temp');
            PRINT N'ui-ready';
            UPDATE @Rows SET Label=N'after';
            PRINT N'ui-changed';
            CREATE TABLE #UiUnsupported(Value xml);
            INSERT #UiUnsupported VALUES(N'<test />');
            PRINT N'ui-error';
            """, Array.Empty<StatementSpan>()));
        Exception? failure = null;
        DataTable? first = null;
        int dialogs = 0, completed = 0;
        snapshotOpenedTest = dialog =>
        {
            int attempt = ++dialogs;
            dialog.Opacity = 0; dialog.ShowInTaskbar = false;
            dialog.SnapshotFinished += () =>
            {
                try
                {
                    Require(session!.IsPaused, "inspection resumed the debuggee");
                    Require(dialog.RowsGrid.ReadOnly, "snapshot grid is editable");
                    if (attempt == 3)
                    {
                        Require(dialog.CaptureError?.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) == true && dialog.Snapshot == null,
                            "unsupported table did not show an inspection error");
                        SaveDialog(dialog, Path.Combine(directory, "tempdb-inspection-error.png"));
                    }
                    else
                    {
                        Require(dialog.CaptureError == null && dialog.Snapshot?.Rows.Count == 1 && dialog.RowsGrid.Rows.Count == 1,
                            "Inspect did not populate a snapshot dialog");
                        if (attempt == 1) { first = dialog.Snapshot; Require((string)first!.Rows[0]["Label"] == "before Ω", "initial snapshot value incorrect"); }
                        else Require((string)dialog.Snapshot!.Rows[0]["Label"] == "after", "new inspection missed mutation");
                        Require(dialog.RowsGrid.Rows[0].Cells["Day"].FormattedValue as string == "2026-01-02", "date display incorrect");
                        Require(dialog.RowsGrid.Rows[0].Cells["Stamp"].FormattedValue as string == "2026-01-02 03:04:05.1234567", "datetime2 display lost fractional seconds");
                        Require(dialog.RowsGrid.Rows[0].Cells["Clock"].FormattedValue as string == "03:04:05.1234567", "time display lost fractional seconds");
                        Require(dialog.RowsGrid.Rows[0].Cells["Zone"].FormattedValue as string == "2026-01-02 03:04:05.1234567 +05:30", "datetimeoffset display lost time zone");
                        SaveDialog(dialog, Path.Combine(directory, $"tempdb-snapshot-{attempt}.png"));
                    }
                    completed++;
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            };
        };
        tempDbPauseTest = async snapshot =>
        {
            try
            {
                string statement = snapshot.Source.Statement(snapshot.Span);
                if (statement.TrimStart().StartsWith("PRINT", StringComparison.OrdinalIgnoreCase))
                {
                    bool error = statement.Contains("N'ui-error'", StringComparison.Ordinal);
                    bool changed = statement.Contains("N'ui-changed'", StringComparison.Ordinal);
                    var tables = await TempDbTests.WaitTables(session!, t => t.Any(x => x.Name == "@Rows") && t.Any(x => x.Name == "#UiTemp") && (!error || t.Any(x => x.Name == "#UiUnsupported")));
                    Require(tempTables.Columns.Count == 3 && tempTables.Columns[0].Name == "Name" && tempTables.Columns[1].Name == "Inspect" && tempTables.Columns[2].Name == "Global",
                        "TempDB list should contain name, Inspect and Global in order");
                    if (changed)
                    {
                        Require(dialogs == 1 && completed == 1 && (string)first!.Rows[0]["Label"] == "before Ω", "snapshot refreshed automatically after stepping");
                    }
                    var inspection = (TabControl)tempTables.Parent!.Parent!;
                    inspection.SelectedTab = (TabPage)tempTables.Parent;
                    // The host's table notification is queued to the UI; wait for
                    // binding before exercising the button's actual event handler.
                    string name = error ? "#UiUnsupported" : "@Rows";
                    int row = -1;
                    for (int retry = 0; retry < 100 && row < 0; retry++)
                    {
                        row = tempTables.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => r.DataBoundItem is TempDbObject t && t.Name == name);
                        if (row < 0) await Task.Delay(50);
                    }
                    Require(row >= 0, "TempDB list did not bind discovered objects");
                    if (!changed && !error) SavePreview(Path.Combine(directory, "debugger-tempdb.png"));
                    typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(tempTables, new object[] { new DataGridViewCellEventArgs(1, row) });
                    if (failure != null) throw failure;
                }
                session!.Resume(DebugAction.Into);
            }
            catch (Exception ex) { failure = ex; session!.Stop(); }
        };
        try
        {
            await Start(false);
            if (failure != null) throw failure;
            if (lastError != null) throw lastError;
            Require(dialogs == 3 && completed == 3, "all three Inspect dialogs were not exercised");
            Require(tempTables.DataSource == null && !tempTables.Enabled, "TempDB list stayed inspectable after session ended");
            File.WriteAllText(Path.Combine(directory, "tempdb-ui-tests.log"), "PASS: TempDB tab contains name, Inspect and Global; actual Inspect event opens modal read-only snapshot; target stays paused; snapshots never auto-refresh; subsequent Inspect captures mutation; unsupported format displays an error; list clears at session end.\n" + output.Text);
            return 0;
        }
        finally { tempDbPauseTest = null; snapshotOpenedTest = null; }

        static void Require(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
        static void SaveDialog(Form dialog, string path)
        {
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
