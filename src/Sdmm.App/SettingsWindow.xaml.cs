using System.Windows;
using System.Windows.Controls;
using Sdmm.App.ViewModels;
using Sdmm.Core.Config;

namespace Sdmm.App;

/// <summary>The tabs of the Settings window.</summary>
public enum SettingsPage { Games, Sources, Token, Smr, Security, Storage, Updates }

/// <summary>
/// One window for every setting, with tabs down the side. Games &amp; servers, storage and update settings take effect
/// as they're changed (they always did); sources, the token and the SMR toggle are drafts saved with Save.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsSequence _sequence = new();
    private readonly Dictionary<RadioButton, (SettingsPage Page, FrameworkElement Content)> _tabs;
    private bool _saved;

    /// <summary>The tab last looked at, so reopening Settings from the sidebar lands there again (this session only).</summary>
    private static SettingsPage _lastPage = SettingsPage.Games;

    /// <param name="page">The tab to open on; null for the one used last.</param>
    /// <param name="expandTokenHelp">Open the token tab with its steps expanded and the box focused (used by the welcome).</param>
    public SettingsWindow(MainViewModel vm, SettingsPage? page = null, bool expandTokenHelp = false)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _vm = vm;
        _tabs = new()
        {
            [GamesTab] = (SettingsPage.Games, GamesPage),
            [SourcesTab] = (SettingsPage.Sources, SourcesPage),
            [TokenTab] = (SettingsPage.Token, TokenPage),
            [SmrTab] = (SettingsPage.Smr, SmrPage),
            [SecurityTab] = (SettingsPage.Security, SecurityPage),
            [StorageTab] = (SettingsPage.Storage, StoragePage),
            [UpdatesTab] = (SettingsPage.Updates, UpdatesPage),
        };
        if (expandTokenHelp) page = SettingsPage.Token;
        var start = page ?? _lastPage;
        _tabs.First(t => t.Value.Page == start).Key.IsChecked = true;
        ShowPage(start);

        if (expandTokenHelp)
        {
            TokenHelp.IsExpanded = true;
            Loaded += (_, _) =>
            {
                // Down to the status line, so the box and the lines under it are all on screen.
                TokenStatusText.BringIntoView();
                TokenBox.Focus();
            };
        }

        // Hooked after the optional pre-expansion above, so opening from the welcome doesn't count as a step.
        TokenHelp.Expanded += (_, _) => _sequence.TokenHelpExpanded();
        _vm.CacheCleared += _sequence.CacheCleared;
        Closed += (_, _) =>
        {
            _vm.CacheCleared -= _sequence.CacheCleared;
            // Cancel, Esc or the X: throw away unsaved edits (the fields are bound straight to the view model).
            if (!_saved) _vm.LoadSourcesIntoForm();
        };
        _sequence.Completed += OnSequenceCompleted;
        // Fields show the saved settings, not edits left over from a window that was cancelled some other way.
        _vm.LoadSourcesIntoForm();
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent for the XAML default, before _tabs exists.
        if (_tabs is null || sender is not RadioButton rb || !_tabs.TryGetValue(rb, out var tab)) return;
        ShowPage(tab.Page);
    }

    private void ShowPage(SettingsPage page)
    {
        foreach (var (_, (p, content)) in _tabs)
            content.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        _lastPage = page;
    }

    /// <summary>Saves the sources, the SMR setting and a pasted token, then closes. Invalid sources keep it open on that tab.</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_vm.TrySaveSources())
        {
            SourcesTab.IsChecked = true;
            return;
        }
        if (TokenBox.Password.Trim().Length > 0) _vm.SetToken(TokenBox.Password);
        TokenBox.Clear();
        if (VtKeyBox.Password.Trim().Length > 0) _vm.SetVirusTotalKey(VtKeyBox.Password);
        VtKeyBox.Clear();
        _saved = true;
        Close();
    }

    // Checked/Unchecked rather than Click: they fire for mouse, keyboard and accessibility tools alike.
    // They don't fire during InitializeComponent's first binding here because the handler checks IsLoaded.
    private void OnForksToggled(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) _sequence.ForksToggled();
    }

    /// <summary>Switching on says plainly what changes and only goes ahead when confirmed; switching off just does it.</summary>
    private void OnSequenceCompleted()
    {
        const string on = "Satisfactory Black Market", off = "Satisfactory Dev Mod Manager";
        if (!Brand.Current.IsVariant)
        {
            if (MessageBox.Show(this,
                    "Psst… you found the Satisfactory Black Market. Before you go in, here's what's different. " +
                    "None of it is documented or supported, so only switch it on if you understand it:\n\n" +
                    "• Take over: SDMM can take over mods that SMM, your host or a hand copy installed, and manage them itself. " +
                    "That deliberately sets aside SDMM's usual rule of never touching other tools' mods.\n\n" +
                    "• SMM's settings get edited: taking a mod over removes it from your SMM profiles, and handing it back " +
                    "adds it again (profiles.json is backed up first, and SMM must be closed).\n\n" +
                    "• Installs from SMR (ficsit.app): search, install and update published mods with their dependencies, as SMM " +
                    "does, including in modpacks, server sync and missing dependencies. SDMM's dependency resolver is simpler than " +
                    "SMM's and can refuse where SMM would find a way.\n\n" +
                    "• Every mod installed in a game or server is listed, not only the ones from your sources.\n\n" +
                    "Each of these still asks before it changes anything. To leave, do the same thing again.\n\nGo in?",
                    on, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _vm.ToggleVariant();
            return;
        }
        _vm.ToggleVariant();
        MessageBox.Show(this, "Back to the Satisfactory Dev Mod Manager. Act natural.", off, MessageBoxButton.OK, MessageBoxImage.None);
    }

    /// <summary>Hides the placeholder once something is typed, and points at Save (the token has no button of its own).</summary>
    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        bool typed = TokenBox.Password.Length > 0;
        TokenPlaceholder.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        TokenSaveHint.Visibility = typed ? Visibility.Visible : Visibility.Collapsed;
        // After layout, so the newly shown hint has a position to scroll to.
        if (typed) Dispatcher.BeginInvoke(() => TokenStatusText.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnVtKeyChanged(object sender, RoutedEventArgs e) =>
        VtKeyPlaceholder.Visibility = VtKeyBox.Password.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    private void OnClearVtKey(object sender, RoutedEventArgs e)
    {
        VtKeyBox.Clear();
        _vm.SetVirusTotalKey("");
    }

    private void OnOpenVirusTotal(object sender, RoutedEventArgs e)
    {
        Sdmm.Core.Diagnostics.FileLog.Write("process", "opening virustotal.com in the browser");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.virustotal.com/") { UseShellExecute = true });
    }

    private void OnClearToken(object sender, RoutedEventArgs e)
    {
        TokenBox.Clear();
        _vm.SetToken("");
    }

    /// <summary>Opens GitHub's "new fine-grained token" page in the default browser.</summary>
    private void OnOpenTokenPage(object sender, RoutedEventArgs e)
    {
        Sdmm.Core.Diagnostics.FileLog.Write("process", "opening GitHub's token page in the browser");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/settings/personal-access-tokens/new") { UseShellExecute = true });
    }
}
