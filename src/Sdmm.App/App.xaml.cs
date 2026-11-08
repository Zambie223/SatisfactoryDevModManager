using System.Windows;
using System.Windows.Threading;

namespace Sdmm.App;

public partial class App : Application
{
    /// <summary>Passed by the self-updater to the new copy it starts.</summary>
    public const string AfterUpdateArg = "--after-update";

    // One instance per settings file: two windows on the same settings would overwrite each other's changes.
    private Mutex? _instance;
    private EventWaitHandle? _wakeUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var key = InstanceKey(e.Args);
        _instance = new Mutex(initiallyOwned: true, @"Local\SDMM-" + key, out bool first);
        _wakeUp = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SDMM-show-" + key);
        // Started by the self-updater while the old copy is still closing: wait for it rather than give way.
        if (!first && e.Args.Contains(AfterUpdateArg))
        {
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; } // the old copy exited without releasing it
        }
        if (!first)
        {
            // Already running with these settings: bring that window forward instead.
            _wakeUp.Set();
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        var wake = _wakeUp;
        new Thread(() =>
        {
            while (wake.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
        }) { IsBackground = true, Name = "SDMM single instance" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        _wakeUp?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A short hash of the settings file in use (--config, or the default), so test instances with their own settings can run alongside.</summary>
    private static string InstanceKey(string[] args)
    {
        int i = Array.IndexOf(args, "--config");
        var path = i >= 0 && i + 1 < args.Length ? args[i + 1] : Sdmm.Core.Config.AppConfig.DefaultPath;
        string full;
        try { full = System.IO.Path.GetFullPath(path).ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { full = path; }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full));
        return Convert.ToHexString(hash, 0, 8);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, $"{Brand.Current.Name}: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
