namespace ADM.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, "Local\\ADM.DownloadManager.v1", out var first);
        if (!first)
        {
            var index = Array.IndexOf(e.Args, "--add");
            try { LinkReceiver.SendAsync(index >= 0 && e.Args.Length > index + 1 ? e.Args[index + 1] : "activate").GetAwaiter().GetResult(); }
            catch { MessageBox.Show("ADM is already running. Open it from the system tray.", "ADM", MessageBoxButton.OK, MessageBoxImage.Information); }
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            var captureIndex = Array.IndexOf(e.Args, "--screenshot");
            if (captureIndex >= 0 && e.Args.Length > captureIndex + 1)
            {
                File.AppendAllText(e.Args[captureIndex + 1] + ".error.txt", args.Exception + Environment.NewLine);
                args.Handled = true; Shutdown(1); return;
            }
            MessageBox.Show(args.Exception.Message, "ADM · Something needs attention", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        try { var window = new MainWindow(e.Args); MainWindow = window; window.Show(); }
        catch (Exception ex) { MessageBox.Show("ADM could not start: " + ex.Message, "ADM"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}
