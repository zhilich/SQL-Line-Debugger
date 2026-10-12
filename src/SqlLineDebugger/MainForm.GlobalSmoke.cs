using System.Reflection;
using Microsoft.Data.SqlClient;

namespace SqlLineDebugger;

internal sealed partial class MainForm
{
    internal async Task<int> GlobalTempSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        database.Text = "Test";
        string globalName = "##SqlLineGlobalUi_" + Guid.NewGuid().ToString("N"), tempName = globalName + "_Temp";
        using var other = new SqlConnection(SqlCatalog.ConnectionString(server.Text, "Test")); await other.OpenAsync();
        async Task<int> Scalar(string sql) { using var cmd = other.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(await cmd.ExecuteScalarAsync()); }
        query.LoadSource(new("batch", "Query", "Test", """
            DECLARE @Rows table(Id int, Label nvarchar(40));
            INSERT @Rows VALUES(1,N'original');
            CREATE TABLE #GlobalUiTemp(Id int);
            INSERT #GlobalUiTemp VALUES(3);
            PRINT N'global-ready';
            UPDATE @Rows SET Label=N'changed';
            INSERT @Rows VALUES(2,N'new');
            PRINT N'global-changed';
            """, Array.Empty<StatementSpan>()));
        int opened = 0, copied = 0, cancelled = 0;
        Exception? failure = null;
        globalCopyOpenedTest = dialog =>
        {
            int attempt = ++opened;
            dialog.Opacity = 0; dialog.ShowInTaskbar = false;
            var timeout = new System.Windows.Forms.Timer { Interval = 5000 };
            timeout.Tick += (_, _) => { failure ??= dialog.CopyError ?? new Exception("Global dialog timed out."); dialog.Close(); };
            dialog.FormClosed += (_, _) =>
            {
                timeout.Stop(); timeout.Dispose();
                try
                {
                    Require(session!.IsPaused, "Global dialog resumed target");
                    if (attempt is 1 or 5) { Require(dialog.Copy == null && dialog.DialogResult == DialogResult.Cancel, "cancel created a copy"); cancelled++; }
                    else { if (dialog.CopyError != null) throw dialog.CopyError; Require(dialog.Copy != null && dialog.DialogResult == DialogResult.OK, "confirmed copy failed"); copied++; }
                }
                catch (Exception ex) { failure = ex; }
            };
            dialog.Shown += (_, _) =>
            {
                try
                {
                    Require(dialog.NameInput.Text == (attempt == 3 ? "dbg_GlobalUiTemp" : "dbg_Rows"), "dialog default name");
                    if (attempt == 1)
                    {
                        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                        bitmap.Save(Path.Combine(directory, "global-copy-dialog.png"), System.Drawing.Imaging.ImageFormat.Png);
                        dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                    }
                    else if (attempt == 5)
                    {
                        dialog.NameInput.Text = "##"; dialog.CopyButton.PerformClick();
                        Require(dialog.CopyError is ArgumentException && dialog.DialogResult == DialogResult.None, "invalid name should stay in dialog");
                        dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                    }
                    else
                    {
                        dialog.NameInput.Text = attempt == 3 ? tempName : globalName;
                        timeout.Start(); dialog.CopyButton.PerformClick();
                    }
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            };
        };
        tempDbPauseTest = async snapshot =>
        {
            try
            {
                string sql = snapshot.Source.Statement(snapshot.Span).TrimStart();
                if (sql.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase))
                {
                    await TempDbTests.WaitTables(session!, t => t.Any(x => x.Name == "@Rows") && t.Any(x => x.Name == "#GlobalUiTemp"));
                    Require(tempTables.Columns.Count == 3 && tempTables.Columns[2].Name == "Global", "Global button must be right of Inspect");
                    if (sql.Contains("global-ready"))
                    {
                        await ClickGlobal("@Rows"); // Cancel.
                        Require(await Scalar($"SELECT COUNT(*) FROM tempdb.sys.tables WHERE name=N'{globalName}'") == 0, "cancel created a global table");
                        await ClickGlobal("@Rows"); // Confirm copy.
                        await ClickGlobal("#GlobalUiTemp");
                        Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(globalName)} WHERE Label=N'original'") == 1, "UI copy not queryable from separate connection");
                        Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(tempName)} WHERE Id=3") == 1, "UI temp table copy");
                    }
                    else if (sql.Contains("global-changed"))
                    {
                        await ClickGlobal("@Rows"); // Replace copy.
                        await ClickGlobal("@Rows"); // Invalid name, cancel.
                        Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(globalName)}") == 2, "UI replacement row count");
                    }
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
            Require(opened == 5 && copied == 3 && cancelled == 2, "all dialog flows exercised");
            Require(await Scalar($"SELECT COUNT(*) FROM {SqlCatalog.Quote(globalName)}") == 2, "global table survives debug session end");
            File.WriteAllText(Path.Combine(directory, "tempdb-global-ui-tests.log"), "PASS: actual Global button right of Inspect; variable/temp name defaults; cancel and validation; confirmed copy and replacement queryable externally; target stays paused; copies survive debug session end.\n" + output.Text);
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "tempdb-global-ui-tests.log"), ex.ToString()); return 1; }
        finally
        {
            tempDbPauseTest = null; globalCopyOpenedTest = null;
            using var cleanup = other.CreateCommand(); cleanup.CommandText = $"DROP TABLE IF EXISTS {SqlCatalog.Quote(globalName)}; DROP TABLE IF EXISTS {SqlCatalog.Quote(tempName)};"; await cleanup.ExecuteNonQueryAsync();
        }
        async Task ClickGlobal(string name)
        {
            var tabs = (TabControl)tempTables.Parent!.Parent!; tabs.SelectedTab = (TabPage)tempTables.Parent;
            int row = -1;
            for (int retry = 0; retry < 100 && row < 0; retry++)
            {
                row = tempTables.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => r.DataBoundItem is TempDbObject t && t.Name == name);
                if (row < 0) await Task.Delay(50);
            }
            Require(row >= 0, "source bound in TempDB grid");
            typeof(DataGridView).GetMethod("OnCellContentClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tempTables, new object[] { new DataGridViewCellEventArgs(2, row) });
            if (failure != null) throw failure;
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
    }
}
