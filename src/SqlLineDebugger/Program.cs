namespace SqlLineDebugger;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        NativeMethods.InitializeSecurity();
        if (args.Length == 2 && args[0] == "--debug-worker") return DebugWorker.Run(args[1]);
        if (args.Contains("--tempdb-test") || args.Contains("--tempdb-stepover-test") || args.Contains("--tempdb-types-test") || args.Contains("--tempdb-global-test") || args.Contains("--tempdb-entry-test"))
        {
            string path = args.SkipWhile(a => a != "--log").Skip(1).FirstOrDefault() ?? "artifacts/tempdb-tests.log";
            return TempDbTests.Run(path, args.Contains("--tempdb-stepover-test"), args.Contains("--tempdb-types-test"), args.Contains("--tempdb-global-test"), args.Contains("--tempdb-entry-test"));
        }
        if (args.Contains("--self-test"))
        {
            string path = args.SkipWhile(a => a != "--log").Skip(1).FirstOrDefault() ?? "debugger-tests.log";
            return IntegrationTests.Run(path);
        }
        // Native workers use the MTA; Windows Forms gets its own STA thread
        // with a message pump.
        int uiExitCode = 0;
        var uiThread = new Thread(() => uiExitCode = RunUi(args));
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start(); uiThread.Join();
        return uiExitCode;
    }
    private static int RunUi(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--editor-smoke"))
        {
            string directory = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts";
            return EditorScrollTests.Run(directory);
        }
        using var form = new MainForm();
        int exitCode = 0;
        if (args.Contains("--ui-smoke") || args.Contains("--tempdb-ui-smoke") || args.Contains("--tempdb-types-ui-smoke") || args.Contains("--tempdb-global-ui-smoke"))
        {
            bool types = args.Contains("--tempdb-types-ui-smoke");
            bool global = args.Contains("--tempdb-global-ui-smoke");
            bool tempDb = global || types || args.Contains("--tempdb-ui-smoke");
            string directory = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts";
            form.Opacity = 0; form.ShowInTaskbar = false;
            Exception? callbackError = null;
            ThreadExceptionEventHandler errorHandler = (_, e) => { callbackError = e.Exception; form.Close(); };
            Application.ThreadException += errorHandler;
            form.Shown += async (_, _) =>
            {
                try { exitCode = global ? await form.GlobalTempSmokeAsync(directory) : types ? await form.TempDbTypesSmokeAsync(directory) : tempDb ? await form.TempDbSmokeAsync(directory) : await form.SmokeAsync(directory); }
                catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, tempDb ? "tempdb-ui-tests.log" : "ui-tests.log"), (callbackError ?? ex).ToString()); exitCode = 1; }
                finally { Application.ThreadException -= errorHandler; form.Close(); }
            };
        }
        Application.Run(form);
        return exitCode;
    }
}
