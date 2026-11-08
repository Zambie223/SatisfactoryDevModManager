using System.Collections.Specialized;
using System.Windows;
using Sdmm.App.ViewModels;
using Sdmm.Core.Config;
using Microsoft.Win32;

namespace Sdmm.App;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        AppUpdater.CleanupPreviousVersion();
        // Optional: SatisfactoryDevModManager.exe --config <path>
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--config");
        string? configPath = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        _vm = new MainViewModel(this, configPath);
        DataContext = _vm;
        _vm.BrowseSmrRequested += () => new SmrBrowserWindow(_vm) { Owner = this }.ShowDialog();
        _vm.ModpacksRequested += () => new ModpacksWindow(_vm, this) { Owner = this }.ShowDialog();
        Loaded += async (_, _) =>
        {
            // First launch: find the game before anything else, so the first check has an install to show.
            _vm.DetectInstallsOnFirstLaunch();

            // The welcome and the sources prompt run before the first GitHub check, so what's added there is used by it
            // (that's also why Settings opened from here doesn't start its own check when it closes).
            bool sourcesShown = false;
            if (_vm.ShouldShowWelcome)
            {
                var welcome = new WelcomeWindow { Owner = this };
                welcome.ShowDialog();
                switch (welcome.Choice)
                {
                    case WelcomeChoice.AddTokenNow:
                        // Not marked as dismissed: if no token ends up saved, the prompt comes back next launch.
                        ShowSettings(SettingsPage.Token, expandTokenHelp: true, refreshAfter: false);
                        sourcesShown = true;
                        break;
                    case WelcomeChoice.DontAskAgain:
                        _vm.DismissTokenPrompt();
                        break;
                }
            }
            if (!_vm.HasSources && !sourcesShown)
            {
                _suppressRefreshAfterSources = true;
                try { _vm.PromptForSources(); }
                finally { _suppressRefreshAfterSources = false; }
            }
            await _vm.StartAsync();
            await _vm.CheckForUpdatesOnStartupAsync();
        };
        // Closing mid-install could leave a mod half swapped in (the next install cleans up, but say so).
        Closing += (_, e) =>
        {
            if (_vm.IsBusy && !_vm.ClosingForUpdate && MessageBox.Show(this,
                    $"{_vm.Status}{Environment.NewLine}{Environment.NewLine}Something is still running. Closing now can leave a mod half-installed; SDMM tidies that up the next time it installs. Close anyway?",
                    Brand.Current.Name, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
        };
        // Keep the log scrolled to the newest line.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    // ---- sidebar popups ---------------------------------------------------------------------------------

    private void ShowPopup(Window w)
    {
        w.Owner = this;
        w.DataContext = _vm;
        w.ShowDialog();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowSettings(null, expandTokenHelp: false, refreshAfter: true);
    private void OnOpenServerSync(object sender, RoutedEventArgs e) => ShowPopup(new ServerSyncWindow());

    /// <summary>True during the startup sources prompt, whose window must not start a check (StartAsync does one).</summary>
    private bool _suppressRefreshAfterSources;

    /// <param name="page">The tab to open on; null for the one used last.</param>
    /// <param name="refreshAfter">Check GitHub again when it closes, if sources or the token changed.</param>
    private void ShowSettings(SettingsPage? page, bool expandTokenHelp, bool refreshAfter)
    {
        ShowPopup(new SettingsWindow(_vm, page, expandTokenHelp));
        _vm.OnSettingsClosed(refreshAfter && !_suppressRefreshAfterSources);
    }

    // ---- IDialogs (marshalled to the UI thread, since remote connections call back from worker threads) ----

    private T OnUi<T>(Func<T> f) => Dispatcher.CheckAccess() ? f() : Dispatcher.Invoke(f);

    /// <summary>
    /// The window prompts should sit on top of: the active window (which may be a popup opened from another popup, such
    /// as server settings over Settings), else the newest visible popup, else this window.
    /// </summary>
    private Window ActiveOwner()
    {
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
        return windows.FirstOrDefault(w => w.IsActive && w != this)
               ?? windows.LastOrDefault(w => w != this && w.Owner is not null)
               ?? this;
    }

    public void Info(string title, string message) =>
        OnUi(() => MessageBox.Show(ActiveOwner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information));

    public void Error(string title, string message) =>
        OnUi(() => MessageBox.Show(ActiveOwner(), message, title, MessageBoxButton.OK, MessageBoxImage.Warning));

    public bool Confirm(string title, string message) =>
        OnUi(() => MessageBox.Show(ActiveOwner(), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public string? PickFolder(string title) => OnUi(() =>
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog(ActiveOwner()) == true ? d.FolderName : null;
    });

    public string? PickOpenFile(string title, string filter) => OnUi(() =>
    {
        var d = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return d.ShowDialog(ActiveOwner()) == true ? d.FileName : null;
    });

    public string? PickSaveFile(string title, string filter, string defaultName) => OnUi(() =>
    {
        var d = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return d.ShowDialog(ActiveOwner()) == true ? d.FileName : null;
    });

    public IReadOnlyList<string>? PickZips(string title) => OnUi<IReadOnlyList<string>?>(() =>
    {
        var d = new OpenFileDialog { Title = title, Filter = "Mod zips (*.zip)|*.zip", Multiselect = true, CheckFileExists = true };
        return d.ShowDialog(ActiveOwner()) == true ? d.FileNames : null;
    });

    public void ShowSources(bool expandTokenHelp = false) => OnUi(() =>
    {
        ShowSettings(expandTokenHelp ? SettingsPage.Token : SettingsPage.Sources, expandTokenHelp, refreshAfter: true);
        return true;
    });

    public void ShowInstalls() => OnUi(() =>
    {
        ShowSettings(SettingsPage.Games, expandTokenHelp: false, refreshAfter: true);
        return true;
    });

    public UpdateChoice AskUpdate(Sdmm.Core.Updates.AvailableUpdate update, string currentVersion, bool canSelfUpdate, string cannotSelfUpdateReason) =>
        OnUi(() =>
        {
            var w = new UpdateWindow(update, currentVersion, canSelfUpdate, cannotSelfUpdateReason) { Owner = ActiveOwner() };
            w.ShowDialog();
            return w.Choice;
        });

    public int? Choose(string title, string message, IReadOnlyList<(string Title, string Detail)> options) => OnUi(() =>
    {
        var w = new ChoiceWindow(title, message, options) { Owner = ActiveOwner() };
        return w.ShowDialog() == true ? w.Choice : null;
    });

    public ServerKind? ChooseServerKind() => OnUi(() =>
    {
        var w = new AddServerWindow { Owner = ActiveOwner() };
        return w.ShowDialog() == true ? w.Choice : null;
    });

    public bool EditServer(InstallConfig config, Func<InstallConfig, string?, Task<ServerTestResult>> test, out string? newPassword)
    {
        string? pw = null;
        bool ok = OnUi(() =>
        {
            var w = new ServerWindow(config, test) { Owner = ActiveOwner() };
            if (w.ShowDialog() != true) return false;
            pw = w.NewPassword;
            return true;
        });
        newPassword = pw;
        return ok;
    }
}
