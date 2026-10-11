namespace SqlLineDebugger;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        NativeMethods.InitializeSecurity();
        if (args.Length == 2 && args[0] == "--debug-worker") return DebugWorker.Run(args[1]);
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
        if (args.Contains("--ui-smoke"))
        {
            string directory = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts";
            form.Opacity = 0; form.ShowInTaskbar = false;
            Exception? callbackError = null;
            ThreadExceptionEventHandler errorHandler = (_, e) => { callbackError = e.Exception; form.Close(); };
            Application.ThreadException += errorHandler;
            form.Shown += async (_, _) =>
            {
                try { exitCode = await form.SmokeAsync(directory); }
                catch (Exception ex) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "ui-tests.log"), (callbackError ?? ex).ToString()); exitCode = 1; }
                finally { Application.ThreadException -= errorHandler; form.Close(); }
            };
        }
        Application.Run(form);
        return exitCode;
    }
}
