using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sdmm.Core.Cache;
using Sdmm.Core.Config;
using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;
using Sdmm.Remote;

namespace Sdmm.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly string _configPath;
    private readonly AppConfig _config;
    private DiscoveryResult? _discovery;
    private SmmState _smm;

    public ObservableCollection<InstallItem> Installs { get; } = [];
    public ObservableCollection<ModRow> Mods { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<SyncLine> SyncLines { get; } = [];

    /// <summary>
    /// The install the mod list shows and Install/Remove act on. It follows the mode: <see cref="SelectedLocal"/> when
    /// managing this PC's game, <see cref="SelectedServer"/> when managing the server. Set only through those.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(PlayCommand), nameof(OpenModsFolderCommand))]
    [NotifyPropertyChangedFor(nameof(TargetBannerText))]
    private InstallItem? _selectedInstall;

    /// <summary>The game install picked in "My game" mode; also what server sync installs into.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncFromServerCommand), nameof(ApplySyncCommand))]
    private InstallItem? _selectedLocal;

    /// <summary>The server picked in "Server" mode; also what server sync compares against.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncFromServerCommand), nameof(ApplySyncCommand))]
    private InstallItem? _selectedServer;

    [ObservableProperty] private InstallItem? _selectedInstallInList;

    /// <summary>False: manage this PC's game. True: manage the server's mods directly (install, update, remove there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClientMode), nameof(InstallPickerHeader), nameof(TargetBannerText))]
    private bool _isServerMode;

    public bool IsClientMode
    {
        get => !IsServerMode;
        set => IsServerMode = !value;
    }

    public string InstallPickerHeader => IsServerMode ? "Server" : "Game installation";

    /// <summary>Shown above the mod list in server mode, so it's obvious where installs go.</summary>
    public string TargetBannerText => !IsServerMode ? ""
        : SelectedInstall is { } s
            ? s.IsRemote
                ? $"Managing the server '{s.Name}' ({s.Location}). Installs and removals happen on the server. Stop it in your host's panel first (hosts lock the files while it runs) and start it again afterwards."
                : $"Managing the server '{s.Name}' ({s.Location}). Installs and removals happen on the server; restart it afterwards."
            : "Managing a server: add one or pick one in the sidebar.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(InstallCommand), nameof(RemoveCommand), nameof(SyncFromServerCommand), nameof(ApplySyncCommand), nameof(InstallFromZipCommand), nameof(TakeOverCommand), nameof(TakeOverAllCommand), nameof(RemoveFromSmmProfilesCommand),
        nameof(InstallRowFromSmrCommand), nameof(UpdateAllCommand), nameof(HandBackCommand), nameof(HandBackAllCommand), nameof(InstallDependenciesCommand))]
    private bool _isBusy;

    /// <summary>Bound to the install pickers so the selection can't change mid-operation.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>The install the current mod rows were built for. Install/Remove refuse if it differs from the selection.</summary>
    private InstallItem? _rowsFor;
    private bool _selectionChangedWhileBusy;

    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _rateLimitText = "";

    // Sources tab (one entry per line)
    [ObservableProperty] private string _usersText = "";
    [ObservableProperty] private string _orgsText = "";
    [ObservableProperty] private string _reposText = "";
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private bool _includeForks;
    [ObservableProperty] private bool _includeArchived;
    [ObservableProperty] private string _tokenStatus = "";

    [ObservableProperty] private bool _includePrereleases;
    [ObservableProperty] private string _cacheSizeText = "";

    /// <summary>Cached builds kept per mod file; older ones are deleted after each install (downloaded again if needed).</summary>
    private const int KeepBuildsPerAsset = 2;

    /// <summary>Local installs only (the "My game" picker and server sync's target). A live, filtered view of <see cref="Installs"/>.</summary>
    public System.ComponentModel.ICollectionView LocalInstalls { get; }
    /// <summary>Servers only (the "Server" picker and server sync).</summary>
    public System.ComponentModel.ICollectionView ServerInstalls { get; }
    public bool HasServers => Installs.Any(i => i.IsServer);
    public bool HasLocalInstalls => Installs.Any(i => !i.IsServer);

    public string ConfigPath => _configPath;
    public bool HasSources => _config.Sources.Users.Count + _config.Sources.Orgs.Count + _config.Sources.Repos.Count > 0;
    public string CachePath => _config.ResolveCacheDirectory();

    public MainViewModel(IDialogs dialogs, string? configPath = null)
    {
        _dialogs = dialogs;
        _configPath = configPath ?? AppConfig.DefaultPath;
        try
        {
            _config = AppConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            _dialogs.Error("Settings", $"Could not read {_configPath}: {ex.Message}\nStarting with default settings; the file will be overwritten when you save.");
            _config = new AppConfig();
        }
        _smm = SmmState.Load(_config.SmmDirectory);

        foreach (var i in _config.Installs) Installs.Add(new InstallItem(i));
        LocalInstalls = new System.Windows.Data.ListCollectionView(Installs) { Filter = o => o is InstallItem { IsServer: false } };
        ServerInstalls = new System.Windows.Data.ListCollectionView(Installs) { Filter = o => o is InstallItem { IsServer: true } };
        Installs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(HasLocalInstalls));
            UpdateEmptyState();
        };
        // Set the backing fields so no scan starts before the window is shown; StartAsync does the first load.
        // The app always starts in "My game" mode, so it never connects to a server unasked.
        // The game and server selected when SDMM last closed, else the first of each.
        _selectedLocal = Installs.FirstOrDefault(i => !i.IsServer && i.Config.Key == _config.LastGameInstall)
                         ?? Installs.FirstOrDefault(i => !i.IsServer);
        _selectedServer = Installs.FirstOrDefault(i => i.IsServer && i.Config.Key == _config.LastServerInstall)
                          ?? Installs.FirstOrDefault(i => i.IsServer);
        _selectedInstall = _selectedLocal;
        _includePrereleases = _config.IncludePrereleases;
        _useSmr = _config.UseSmr;
        _checkUpdatesOnStartup = _config.CheckForUpdatesOnStartup;
        Brand.Current.IsVariant = _config.Variant;
        LoadSourcesIntoForm();
        UpdateTokenStatus();
        UpdateCacheSize();
        Log($"Settings: {_configPath}");
        Log($"Download cache: {CachePath}");
        if (_smm.Found) Log($"SMM settings found ({_smm.Installations.Count} installs, {_smm.Profiles.Count} profiles); read-only.");
        foreach (var w in _smm.Warnings) Log("SMM: " + w);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    // ---- mod list view: search, filter, selection ---------------------------------------------------------

    public static IReadOnlyList<string> FilterOptions { get; } =
        ["All mods", "Installed here", "Updates available", "Not installed", "Managed elsewhere", "Has problems"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedFilter = "All mods";
    [ObservableProperty] private ModRow? _selectedMod;
    [ObservableProperty] private string _lastLogLine = "";
    [ObservableProperty] private bool _isLogOpen;

    private System.ComponentModel.ICollectionView? _modsView;

    /// <summary>The mod list as shown: filtered by the search box and the filter dropdown.</summary>
    public System.ComponentModel.ICollectionView ModsView
    {
        get
        {
            if (_modsView is null)
            {
                _modsView = System.Windows.Data.CollectionViewSource.GetDefaultView(Mods);
                _modsView.Filter = o => o is ModRow r && MatchesFilter(r);
            }
            return _modsView;
        }
    }

    public string ModCountText
    {
        get
        {
            int shown = Mods.Count(MatchesFilter);
            return shown == Mods.Count ? $"{Mods.Count} mods" : $"{shown} of {Mods.Count} mods";
        }
    }

    /// <summary>Shown when there are mods but the search or filter hides all of them; empty otherwise.</summary>
    public string NoMatchText
    {
        get
        {
            if (Mods.Count == 0 || Mods.Any(MatchesFilter)) return "";
            var why = new List<string>();
            if (SearchText.Trim().Length > 0) why.Add($"the search \"{SearchText.Trim()}\"");
            if (SelectedFilter != "All mods") why.Add($"the filter \"{SelectedFilter}\"");
            return $"No mods match {string.Join(" and ", why)}.";
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        SelectedFilter = "All mods";
    }

    private bool MatchesFilter(ModRow r)
    {
        var q = SearchText.Trim();
        if (q.Length > 0 &&
            !r.ModReference.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !r.Source.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !r.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            return false;
        return SelectedFilter switch
        {
            "Installed here" => r.Installed is not null,
            "Updates available" => r.State == ModState.UpdateAvailable,
            "Not installed" => r.Installed is null,
            "Managed elsewhere" => r.IsManagedElsewhere,
            "Has problems" => r.HasProblems,
            _ => true,
        };
    }

    partial void OnSearchTextChanged(string value)
    {
        ModsView.Refresh();
        OnPropertyChanged(nameof(ModCountText)); OnPropertyChanged(nameof(NoMatchText));
    }

    partial void OnSelectedFilterChanged(string value)
    {
        ModsView.Refresh();
        OnPropertyChanged(nameof(ModCountText)); OnPropertyChanged(nameof(NoMatchText));
    }

    [RelayCommand]
    private void CloseDetails() => SelectedMod = null;

    private readonly Dictionary<string, IReadOnlyList<Sdmm.Core.Smr.SmrVersion>> _smrVersionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The details panel's SMR version picker is filled when a mod on SMR is selected (when SMR installs are available).</summary>
    partial void OnSelectedModChanged(ModRow? value)
    {
        if (value is { Smr: not null } row && Brand.Current.IsVariant && row.SmrVersions.Count == 0) _ = LoadSmrVersionsAsync(row);
    }

    private async Task LoadSmrVersionsAsync(ModRow row)
    {
        try
        {
            if (!_smrVersionCache.TryGetValue(row.ModReference, out var versions))
            {
                using var smr = CreateSmr();
                var got = await smr.GetVersionsAsync([row.ModReference]);
                versions = got.TryGetValue(row.ModReference, out var v) ? v : [];
                _smrVersionCache[row.ModReference] = versions;
            }
            if (row.SmrVersions.Count > 0) return;
            var target = _scan?.Item == SelectedInstall ? _scan?.Read : null;
            row.SmrVersions.Add(new SmrVersionOption(null, "Newest that fits (recommended)", true));
            foreach (var v in versions.Where(v => v.Approved))
            {
                bool fits = target is null || Sdmm.Core.Smr.SmrResolver.Fits(v, target.Target, target.Changelist, out _);
                row.SmrVersions.Add(new SmrVersionOption(v,
                    $"{v.Version}{(v.IsRelease ? "" : $" ({v.Stability})")}" + (v.CreatedAt is { } d ? $"  ·  {d.LocalDateTime:yyyy-MM-dd}" : "") + (fits ? "" : "  ·  doesn't fit"),
                    fits));
            }
            row.SelectedSmrVersion = row.SmrVersions[0];
        }
        catch (Sdmm.Core.Smr.SmrException ex)
        {
            Log($"SMR versions for {row.ModReference} couldn't be read: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    // ---- sidebar tools ------------------------------------------------------------------------------------

    private bool CanUseLocalInstall() => SelectedInstall is { IsRemote: false };

    /// <summary>Opens FactoryGame\Mods of the selected local install in Explorer.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLocalInstall))]
    private void OpenModsFolder()
    {
        var mods = Path.Combine(SelectedInstall!.Config.Path, "FactoryGame", "Mods");
        if (!Directory.Exists(mods))
        {
            _dialogs.Error("Open Mods folder", $"{mods} does not exist.");
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{mods}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Starts the selected install's own game exe, so a test copy runs the copy rather than the Steam install
    /// (launching through Steam always starts the Steam install).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseLocalInstall))]
    private void Play()
    {
        var root = SelectedInstall!.Config.Path;
        var exe = new[] { "FactoryGameSteam.exe", "FactoryGameEGS.exe", "FactoryGame.exe" }
            .Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists);
        if (exe is null)
        {
            _dialogs.Error("Play", $"No game exe (FactoryGameSteam.exe, FactoryGameEGS.exe or FactoryGame.exe) found in {root}.");
            return;
        }
        Log($"Starting {exe}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = root });
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        // Only open web links; nothing else should be launched from a URL that came from GitHub data.
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Log(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss}  {line}";
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Append(text);
        else d.BeginInvoke(() => Append(text));

        void Append(string t)
        {
            LogLines.Add(t);
            while (LogLines.Count > 2000) LogLines.RemoveAt(0);
            LastLogLine = t;
        }
    }

    private void SaveConfig()
    {
        _config.Installs = Installs.Select(i => i.Config).ToList();
        _config.IncludePrereleases = IncludePrereleases;
        _config.Save(_configPath);
    }

    private GitHubClient CreateGitHub() =>
        new(SecretProtector.Unprotect(_config.ProtectedReadToken),
            new FileEtagCache(Path.Combine(CachePath, "http")));

    private PackageCache CreateCache() => new(Path.Combine(CachePath, "packages"));

    /// <summary>Zips added with "Install from zip…"; kept next to the download cache, but never pruned or cleared with it.</summary>
    private ImportedZips CreateImported() => new(Path.Combine(CachePath, "imported"));

    /// <summary>The GitHub mods from the last check plus imported zips, merged by mod reference.</summary>
    private IReadOnlyList<DiscoveredMod> AllMods()
    {
        IReadOnlyList<ModBuild> imported;
        try
        {
            imported = CreateImported().List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidModPackageException)
        {
            Log("Imported zips couldn't be read: " + ex.Message);
            imported = [];
        }
        return ImportedZips.Merge(_discovery?.Mods ?? [], imported);
    }

    private OpenedInstall Open(InstallItem item) =>
        InstallFactory.Open(item.Config, SecretProtector.Unprotect,
            fingerprint => _dialogs.Confirm("New server key",
                $"First connection to {item.Name}.\n\nServer fingerprint:\n{fingerprint}\n\n" +
                "Accept it? If you can, compare it with the one your host shows. It is saved, and a different key later is refused."),
            fingerprint =>
            {
                item.Config.TrustedFingerprint = fingerprint;
                Application.Current.Dispatcher.Invoke(SaveConfig);
                Log($"{item.Name}: saved server fingerprint {fingerprint}");
            });

    private async Task RunBusy(string status, Func<Task> work)
    {
        IsBusy = true;
        Status = status;
        try
        {
            while (true)
            {
                try
                {
                    await work();
                    Status = "Ready";
                    break;
                }
                // The server refused to change (or read) files: on hosted servers that almost always means it's running.
                catch (Exception ex) when (Sdmm.Remote.RemoteFailure.FindRefusal(ex) is { } refusal)
                {
                    Log("ERROR: " + ex.Message);
                    await RefreshTouchedAsync();
                    if (!AskServerStopped(SelectedInstall, refusal.Message))
                    {
                        Status = "The server refused the change; it's probably running.";
                        break;
                    }
                    Status = status;
                }
            }
        }
        catch (Exception ex)
        {
            Status = "Failed: " + ex.Message;
            Log("ERROR: " + ex.Message);
            _dialogs.Error(status.TrimEnd('.', '…'), Explain(ex));
            await RefreshTouchedAsync();
        }
        finally
        {
            IsBusy = false;
        }
        // Settings changed during the operation: check GitHub again once (no loop, the flags are cleared first).
        if (_refreshWhenIdle)
        {
            _refreshWhenIdle = false;
            _selectionChangedWhileBusy = false;
            await RefreshAsync();
        }
        // The selection changed during the operation: rebuild once.
        else if (_selectionChangedWhileBusy)
        {
            _selectionChangedWhileBusy = false;
            await RebuildRowsAsync();
        }
    }

    /// <summary>A failed install rolls back, but the list should show what's really there for the mods it touched.</summary>
    private async Task RefreshTouchedAsync()
    {
        bool touched;
        lock (_changed) touched = _changed.Count > 0;
        if (!touched) return;
        try { await RefreshAfterChangeAsync(); }
        catch (Exception refreshError) { Log("Couldn't re-read the changed mods: " + refreshError.Message); }
    }

    /// <summary>
    /// The server's files can't be changed right now. Hosted servers (Indifferent Broccoli, and presumably most hosts)
    /// lock them while the game server runs, so that's what this says, with a way to try again once it's stopped.
    /// Returns true to try again.
    /// </summary>
    private bool AskServerStopped(InstallItem? item, string serverSaid)
    {
        var name = item is null ? "the server" : $"'{item.Name}'";
        Log($"{(item?.Name ?? "Server")}: refused a change, probably because it's running ({serverSaid}).");
        return _dialogs.Choose("Server is probably running",
            $"SDMM can't change the mod files on {name} right now. That almost always means the server is running: hosted servers lock their files while the game server runs.\n\n" +
            "Stop the server in your host's panel and wait until it shows as stopped, then try again. Start it again when you're done in SDMM.\n\n" +
            $"(The server said: {serverSaid})",
            [("Try again", "The server is stopped now. Check again and carry on.")]) == 0;
    }

    /// <summary>The error text shown to the user, with a hint for the causes people can fix themselves.</summary>
    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ex.Message + "\n\nWindows refused access. Close the game and anything that might have the mod folder open (Explorer, an editor, a virus scan), then try again. If the game is in a protected folder, check that your Windows account can write to it.",
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) =>
            ex.Message + "\n\nThe drive is full. Free some space, or clear the download cache under Settings → Storage.",
        IOException when ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) =>
            ex.Message + "\n\nSomething has a file open: close the game, then try again.",
        _ => ex.Message,
    };

    /// <summary>Set when sources or the token changed while busy; RunBusy checks GitHub again when it finishes.</summary>
    private bool _refreshWhenIdle;

    private void EnsureRowsMatchSelection()
    {
        if (_rowsFor is null || _rowsFor != SelectedInstall)
            throw new InvalidOperationException("The mod list is out of date for the selected install. Click 'Check GitHub' and try again.");
    }

    partial void OnIncludePrereleasesChanged(bool value)
    {
        SaveConfig();
        if (IsBusy) _selectionChangedWhileBusy = true;
        else _ = RebuildRowsAsync(reuseScan: true);
    }

    partial void OnSelectedInstallChanged(InstallItem? value)
    {
        ServerLockText = "";
        if (IsBusy) _selectionChangedWhileBusy = true;
        else _ = RebuildRowsAsync();
    }

    partial void OnSelectedLocalChanged(InstallItem? value)
    {
        if (!IsServerMode) SelectedInstall = value;
        RememberSelection();
    }

    partial void OnSelectedServerChanged(InstallItem? value)
    {
        if (IsServerMode) SelectedInstall = value;
        RememberSelection();
    }

    /// <summary>Saves the picked game and server, so the next launch starts with them selected.</summary>
    private void RememberSelection()
    {
        var game = SelectedLocal?.Config.Key;
        var server = SelectedServer?.Config.Key;
        if (game == _config.LastGameInstall && server == _config.LastServerInstall) return;
        _config.LastGameInstall = game;
        _config.LastServerInstall = server;
        SaveConfig();
    }

    partial void OnIsServerModeChanged(bool value)
    {
        SelectedMod = null;
        SelectedInstall = value ? SelectedServer : SelectedLocal;
        UpdateEmptyState();
        Log(value ? $"Now managing the server{(SelectedServer is { } s ? $" '{s.Name}'" : "")}." : "Now managing this PC's game.");
    }

    // ---- discovery and the mod list ---------------------------------------------------------------------

    /// <summary>First load after the window appears: check GitHub if sources exist, else just read the install.</summary>
    public Task StartAsync() => HasSources ? RefreshAsync() : RebuildRowsAsync();

    // ---- first launch -------------------------------------------------------------------------------------

    /// <summary>
    /// Finds game installs on the first launch (and after settings were reset): every Steam library, Epic's manifests
    /// and SMM's install list. Runs once; installs removed later aren't added back.
    /// </summary>
    public void DetectInstallsOnFirstLaunch()
    {
        if (_config.InstallsAutoDetected) return;
        int added = AutoDetect();
        _config.InstallsAutoDetected = true;
        SaveConfig();
        if (added == 0 && !HasLocalInstalls)
            _dialogs.Info("No game found",
                "SDMM couldn't find Satisfactory in your Steam libraries, Epic Games or SMM's install list.\n\n" +
                "Add it under Settings → Games & servers → Add game folder… (the folder that contains FactoryGame).");
    }

    /// <summary>
    /// Says that mods can't appear until sources are added, and offers to open Sources. Used at startup and by
    /// "Check for new builds" when there are none.
    /// </summary>
    public void PromptForSources()
    {
        if (HasSources) return;
        Log("No GitHub sources configured yet.");
        if (_dialogs.Confirm("Add mod sources",
                "SDMM doesn't know where to look for mods yet, so the list stays empty.\n\n" +
                "It finds dev builds in the GitHub releases of the users, organizations or repos you add under " +
                "Sources (for example your own GitHub name and your friends').\n\nOpen Sources now?"))
            _dialogs.ShowSources();
    }

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunBusy("Checking GitHub…", async () =>
    {
        var sources = _config.Sources;
        if (sources.Users.Count + sources.Orgs.Count + sources.Repos.Count == 0)
        {
            PromptForSources();
            return;
        }
        using var gh = CreateGitHub();
        _discovery = await new ModDiscovery(gh).DiscoverAsync(sources);
        foreach (var e in _discovery.Errors) Log("GitHub: " + e);
        Log($"Scanned {_discovery.ScannedRepos.Count} repos, found {_discovery.Mods.Count} mods with releases.");
        if (gh.LastRateLimit is { } rl)
            RateLimitText = $"GitHub API: {rl.Remaining}/{rl.Limit} left, resets {rl.Reset.ToLocalTime():t}";
        _smm = SmmState.Load(_config.SmmDirectory);
        await BuildRowsAsync();
        await LoadSmrAsync(refreshAll: true);
        ReportDiscoveryProblems(_discovery);
    });

    /// <summary>
    /// Warns when the mod list is incomplete or out of date: the GitHub request limit ran out, GitHub was down, or
    /// sources failed. Without this, a used-up limit only showed as "0/60" in the sidebar and a partial list.
    /// </summary>
    private void ReportDiscoveryProblems(DiscoveryResult d)
    {
        if (!d.IsIncomplete) return;
        bool hasToken = SecretProtector.Unprotect(_config.ProtectedReadToken) is not null;
        var limited = d.Problems.Where(p => p.Kind == ProblemKind.RateLimited).ToList();
        var stale = d.Problems.Where(p => p.Kind == ProblemKind.Stale).ToList();
        var failed = d.Problems.Where(p => p.Kind == ProblemKind.Failed).ToList();
        bool rateLimited = d.RateLimitResetsAt is not null || limited.Count > 0;

        static string List(IEnumerable<string> items)
        {
            var all = items.Distinct().ToList();
            return string.Join(", ", all.Take(8)) + (all.Count > 8 ? $" and {all.Count - 8} more" : "");
        }

        var text = new System.Text.StringBuilder();
        string title;
        if (rateLimited)
        {
            title = "GitHub request limit reached";
            text.Append(hasToken
                ? "GitHub's request limit for your token ran out during this check."
                : "GitHub allows 60 requests per hour without a token, and that ran out during this check.");
            text.Append(d.Mods.Count == 0 && stale.Count == 0 ? " No mods could be loaded." : " The mod list is incomplete.");
        }
        else
        {
            title = "Some sources couldn't be read";
            text.Append("The mod list may be incomplete or out of date.");
        }
        text.AppendLine().AppendLine();
        if (limited.Count > 0)
            text.AppendLine($"Not read because of the limit ({limited.Count}): {List(limited.Select(p => p.Source))}.");
        if (stale.Count > 0)
            text.AppendLine($"Shown from the last successful check, may be out of date ({stale.Count}): {List(stale.Select(p => FriendlySource(p.Source)))}.");
        if (failed.Count > 0)
            text.AppendLine($"Failed ({failed.Count}):\n• " + string.Join("\n• ", failed.Take(5).Select(p => p.Message)) + (failed.Count > 5 ? $"\n• …and {failed.Count - 5} more (see the activity log)" : ""));
        if (d.RateLimitResetsAt is { } reset)
            text.AppendLine().Append($"The limit resets at {reset.ToLocalTime():t}. Check again after that.");

        Log($"{title}: {d.Problems.Count} source(s) affected.");
        if (rateLimited && !hasToken)
        {
            text.AppendLine().AppendLine().Append("A free read-only GitHub token raises the limit to 5,000 requests per hour. Open the token settings now?");
            if (_dialogs.Confirm(title, text.ToString())) _dialogs.ShowSources(expandTokenHelp: true);
        }
        else
        {
            _dialogs.Error(title, text.ToString());
        }
    }

    /// <summary>"/repos/bob/ModA/releases?per_page=100" → "bob/ModA"; "/users/bob/repos?..." → "user bob".</summary>
    private static string FriendlySource(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^/repos/([^/]+/[^/?]+)");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(path, @"^/(users|orgs)/([^/?]+)");
        if (m.Success) return (m.Groups[1].Value == "users" ? "user " : "org ") + m.Groups[2].Value;
        return path;
    }

    private async Task RebuildRowsAsync(bool reuseScan = false)
    {
        if (IsBusy) return;
        await RunBusy(reuseScan ? "Updating the list…" : SelectedInstall is { IsServer: true } ? "Reading the server…" : "Reading install…", async () =>
        {
            await BuildRowsAsync(reuseScan);
            // Rows for an install picked after the last GitHub check (or the first one, added on first launch)
            // haven't been looked up on SMR yet; without this they showed no icons until the next check.
            await LoadSmrAsync(refreshAll: false);
        });
    }

    /// <param name="reuseScan">Use the last read of this install instead of reading it again (nothing on disk changed).</param>
    private async Task BuildRowsAsync(bool reuseScan = false)
    {
        var keepSelected = SelectedMod?.ModReference;
        Mods.Clear();
        _rowsFor = null;
        var item = SelectedInstall;
        if (item is null)
        {
            OnPropertyChanged(nameof(ModCountText)); OnPropertyChanged(nameof(NoMatchText));
            UpdateEmptyState();
            return;
        }

        InstallRead read;
        bool fresh = !(reuseScan && _scan is { } cached && cached.Item == item);
        try
        {
            read = fresh ? await Task.Run(() => ReadInstall(item)) : _scan!.Value.Read;
        }
        catch (InvalidOperationException) when (item.IsRemote)
        {
            if (!await TryFixServerFolderAsync(item)) throw;
            read = await Task.Run(() => ReadInstall(item));
        }
        // A hosted server whose Mods folder is locked is almost certainly running: say so now, not only in the banner.
        while (fresh && read.LockError is { } locked && AskServerStopped(item, locked))
            read = await Task.Run(() => ReadInstall(item));
        _scan = (item, read);
        var (target, installed, lockError, problems) = (read.Target, read.Mods, read.LockError, read.Problems);
        item.Status = $"{target}, {installed.Count} mod{(installed.Count == 1 ? "" : "s")}" + (lockError is null ? "" : " · Mods folder locked");
        ServerLockText = lockError is null ? "" : LockedMessage(lockError);
        if (lockError is not null) Log($"{item.Name}: the Mods folder can't be changed right now ({lockError}).");

        var byRef = installed.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<ModRow>();
        foreach (var mod in AllMods())
        {
            var builds = mod.Builds
                .Where(b => b.Assets.ContainsKey(target) && (IncludePrereleases || !b.Prerelease))
                .Select(b => new BuildOption(b)).ToList();
            byRef.TryGetValue(mod.ModReference, out var inst);
            rows.Add(new ModRow(mod.ModReference, mod, inst, builds, item.IsServer, ProblemsFor(mod.ModReference)));
        }
        // Dev builds installed here that are no longer in the sources, so they can still be removed.
        foreach (var inst in installed.Where(m => m.Owner == ModOwner.Sdmm && rows.All(r => !r.ModReference.Equals(m.ModReference, StringComparison.OrdinalIgnoreCase))))
            rows.Add(new ModRow(inst.ModReference, null, inst, [], item.IsServer, ProblemsFor(inst.ModReference)));
        // Usually only your sources' builds and SDMM's own installs are listed, never other tools' mods outside your
        // sources. With listAll every installed mod is listed, so any of them can be taken over.
        bool listAll = Brand.Current.IsVariant;
        foreach (var inst in installed.Where(m => listAll && m.Owner != ModOwner.Sdmm &&
                                                  rows.All(r => !r.ModReference.Equals(m.ModReference, StringComparison.OrdinalIgnoreCase))))
            rows.Add(new ModRow(inst.ModReference, null, inst, [], item.IsServer, ProblemsFor(inst.ModReference)));
        int problemCount = rows.Count(r => r.HasProblems);
        if (problemCount > 0) Log($"{item.Name}: {problemCount} installed mod(s) have problems; filter by 'Has problems' to see them.");

        IReadOnlyList<ModProblem> ProblemsFor(string modRef) => problems.TryGetValue(modRef, out var list) ? list : [];

        foreach (var r in rows.OrderBy(r => r.ModReference, StringComparer.OrdinalIgnoreCase))
        {
            ApplySmr(r);
            // Picking another source or version changes whether Install is possible.
            r.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModRow.CanInstall)) InstallCommand.NotifyCanExecuteChanged();
            };
            Mods.Add(r);
        }
        _rowsFor = item;
        SelectedMod = keepSelected is null ? null : Mods.FirstOrDefault(r => r.ModReference == keepSelected);
        OnPropertyChanged(nameof(ModCountText)); OnPropertyChanged(nameof(NoMatchText));
        ModsView.Refresh();
        UpdateEmptyState();
        InstallCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        TakeOverCommand.NotifyCanExecuteChanged();
        TakeOverAllCommand.NotifyCanExecuteChanged();
        RemoveFromSmmProfilesCommand.NotifyCanExecuteChanged();
        HandBackCommand.NotifyCanExecuteChanged();
        HandBackAllCommand.NotifyCanExecuteChanged();
        InstallRowFromSmrCommand.NotifyCanExecuteChanged();
        RefreshUpdateCount();
        BrowseSmrCommand.NotifyCanExecuteChanged();
        // Only the listed mods' dependencies: the normal mode doesn't list (or fix up) other tools' mods.
        _unmet = DependencyFixer.FindUnmet(installed, Mods.Select(r => r.ModReference));
        UpdateDependencyBanner();
    }

    private sealed record InstallRead(ModTarget Target, IReadOnlyList<InstalledMod> Mods, string? LockError,
        IReadOnlyDictionary<string, IReadOnlyList<ModProblem>> Problems, long Changelist = 0);

    /// <summary>The last full read of an install, reused when nothing on disk changed (toggles) or patched after a change.</summary>
    private (InstallItem Item, InstallRead Read)? _scan;

    /// <summary>Mods changed by the running operation (installed, removed, taken over); RefreshAfterChangeAsync re-reads only these.</summary>
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    private void MarkChanged(IEnumerable<string> modRefs)
    {
        lock (_changed) foreach (var r in modRefs) _changed.Add(r);
    }

    /// <summary>
    /// After installing, removing or taking over mods: re-reads only the changed mod folders and rebuilds the list from
    /// the last full read. Over SFTP/FTP a full read costs a round trip per file; this needs a few per changed mod.
    /// Falls back to a full read when there's no earlier read of this install.
    /// </summary>
    private async Task RefreshAfterChangeAsync(InstallItem? item = null)
    {
        item ??= SelectedInstall;
        List<string> refs;
        lock (_changed) { refs = _changed.ToList(); _changed.Clear(); }
        if (item is null) return;
        if (item != SelectedInstall)
        {
            if (_scan?.Item == item) _scan = null; // read it fresh when it's picked again
            return;
        }
        if (_scan is not { } cached || cached.Item != item)
        {
            await BuildRowsAsync();
            return;
        }
        var prev = cached.Read;
        var smm = item.IsRemote ? null : _smm;
        var read = await Task.Run(() =>
        {
            using var o = Open(item);
            var fs = o.Install.FileSystem;
            var mods = prev.Mods.Where(m => !refs.Contains(m.ModReference, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var r in refs)
                foreach (var (root, inGf) in new[] { (InstallPaths.ModsDir, false), (InstallPaths.GameFeaturesDir, true) })
                {
                    var folder = InstallPaths.Combine(root, r);
                    if (fs.DirectoryExists(folder)) mods.Add(ModScanner.Inspect(fs, folder, r, inGf));
                }
            var problems = ModHealth.Check(mods, prev.Changelist, smm, item.IsRemote ? null : item.Config.Path, fs);
            // A change just went through, so the Mods folder was writable.
            return prev with { Mods = mods, Problems = problems, LockError = null };
        });
        _scan = (item, read);
        Log($"Re-read {refs.Count} changed mod folder{(refs.Count == 1 ? "" : "s")} on {item.Name}.");
        await BuildRowsAsync(reuseScan: true);
    }

    /// <summary>
    /// Reads an install's platform and mods, checks the installed mods for problems (ModHealth), and for a hosted
    /// server checks the Mods folder can be changed.
    /// </summary>
    private InstallRead ReadInstall(InstallItem item)
    {
        using var opened = Open(item);
        var info = opened.Install.Detect();
        var fs = opened.Install.FileSystem;
        var mods = ModScanner.Scan(fs);
        var problems = ModHealth.Check(mods, info.Changelist, item.IsRemote ? null : _smm, item.IsRemote ? null : item.Config.Path, fs);
        return new InstallRead(info.Target, mods, item.IsRemote ? ModsFolderAccess.Check(fs) : null, problems, info.Changelist);
    }

    /// <summary>Shown under the server banner when the hosted server's Mods folder is locked; empty otherwise.</summary>
    [ObservableProperty] private string _serverLockText = "";

    /// <summary>
    /// Explains a locked Mods folder. Hosts such as Indifferent Broccoli lock it while the server runs, so the fix is
    /// to stop the server in the host's panel, change mods, then start it again.
    /// </summary>
    private static string LockedMessage(string serverError) =>
        "The server's Mods folder is locked, so mods can't be changed right now; the server is probably running. Stop it in your host's panel, then check again.";

    /// <summary>What to do with the server around a change: hosted ones lock their files while running.</summary>
    private static string ServerNote(InstallItem item) => item.IsRemote
        ? "If the server is running, stop it in your host's panel first; start it again afterwards."
        : "Restart the server afterwards.";

    /// <summary>
    /// Before changing a hosted server's mods: if its Mods folder is locked, says the server is probably running and
    /// offers to check again once it's stopped. False if it stays locked (nothing is changed then).
    /// </summary>
    private bool EnsureServerWritable(InstallItem item, GameInstall install)
    {
        if (!item.IsRemote) return true;
        if (ModsFolderAccess.Check(install.FileSystem) is not { } error)
        {
            ServerLockText = "";
            return true;
        }
        while (true)
        {
            ServerLockText = LockedMessage(error);
            // Called from worker threads; the prompt marshals itself to the UI thread.
            if (!AskServerStopped(item, error))
            {
                Log($"{item.Name}: the Mods folder is locked ({error}); nothing was changed.");
                return false;
            }
            if (ModsFolderAccess.Check(install.FileSystem) is not { } again)
            {
                ServerLockText = "";
                return true;
            }
            error = again;
        }
    }

    /// <summary>
    /// A hosted server's "Game folder" didn't hold the game. Hosts' logins often start a level or more above it, so
    /// look for FactoryGame below it: one match is used automatically (and logged); several are offered in turn.
    /// Returns true if the path was changed (read again).
    /// </summary>
    private async Task<bool> TryFixServerFolderAsync(InstallItem item)
    {
        Status = $"Looking for the game folder on '{item.Name}'…";
        var found = await Task.Run(() =>
        {
            using var opened = Open(item);
            return GameRootFinder.FindAll(opened.Install.FileSystem);
        });
        string Full(string rel) => "/" + string.Join('/', new[] { item.Config.Path.Trim('/'), rel }.Where(s => s.Length > 0));
        var candidates = found.Where(f => f.Length > 0).Select(Full).ToList();
        if (candidates.Count == 0) return false;

        string? path = candidates.Count == 1 ? candidates[0]
            : candidates.FirstOrDefault(c => _dialogs.Confirm("Game folder found",
                $"'{item.Name}' has the game in more than one folder:\n{string.Join("\n", candidates)}\n\nUse {c} as the server's Game folder? (No shows the next one.)"));
        if (path is null) return false;
        Log($"{item.Name}: the game wasn't in {item.Config.Path}; found it in {path} and switched the server's Game folder there (change it under Settings → Games & servers).");
        item.Config.Path = path;
        item.Refresh();
        SaveConfig();
        RememberSelection();
        return true;
    }

    // ---- empty list -----------------------------------------------------------------------------------------

    public enum EmptyStateKind { None, NoServer, NoInstall, NoSources, NoMods }

    [ObservableProperty] private EmptyStateKind _emptyState;
    [ObservableProperty] private string _emptyStateText = "";
    [ObservableProperty] private string _emptyStateButtonText = "";

    /// <summary>Explains an empty mod list and offers the one step that fills it.</summary>
    private void UpdateEmptyState()
    {
        if (Mods.Count > 0 && SelectedInstall is not null)
        {
            EmptyState = EmptyStateKind.None;
            return;
        }
        (EmptyState, EmptyStateText, EmptyStateButtonText) =
            IsServerMode && !HasServers ? (EmptyStateKind.NoServer,
                "No server is set up yet. Add a hosted server (such as Indifferent Broccoli) with its SFTP or FTP login, or a dedicated server on this PC, to install and update its mods directly.", "Add server…")
            : IsServerMode && SelectedInstall is null ? (EmptyStateKind.NoServer, "Pick a server in the sidebar.", "")
            : !IsServerMode && !HasLocalInstalls ? (EmptyStateKind.NoInstall,
                "No game installation is set up yet. Auto-detect finds Steam and Epic installs, or add the folder yourself.", "Games & servers…")
            : !HasSources ? (EmptyStateKind.NoSources,
                "No mod sources yet. SDMM lists the dev builds it finds in the GitHub releases of the users, organizations or repos you add under Sources.", "Add sources…")
            : (EmptyStateKind.NoMods,
                "No mods found. Check for new builds, or check that your sources publish releases with Alpakit zips (<Mod>-Windows.zip and so on).", "");
    }

    [RelayCommand]
    private void EmptyStateAction()
    {
        switch (EmptyState)
        {
            case EmptyStateKind.NoServer: AddServerInstall(); break;
            case EmptyStateKind.NoInstall: _dialogs.ShowInstalls(); break;
            case EmptyStateKind.NoSources: _dialogs.ShowSources(); break;
        }
    }

    // ---- SMR (ficsit.app) listings ---------------------------------------------------------------------------

    private readonly Dictionary<string, Sdmm.Core.Smr.SmrMod> _smrMods = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Mod references already looked up on SMR (found or not), so a rebuild only asks about new ones.</summary>
    private readonly HashSet<string> _smrLookedUp = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Decoded logos by SMR mod id. Failed downloads aren't stored, so the next check tries again.</summary>
    private readonly Dictionary<string, System.Windows.Media.ImageSource> _logos = new(StringComparer.Ordinal);
    private static readonly TimeSpan LogoMaxAge = TimeSpan.FromDays(7);

    [ObservableProperty] private bool _useSmr;

    partial void OnUseSmrChanged(bool value)
    {
        _config.UseSmr = value;
        SaveConfig();
        if (!value)
        {
            _smrMods.Clear();
            _smrLookedUp.Clear();
            foreach (var r in Mods) { r.Smr = null; r.Logo = null; r.ShowSmrTab = false; }
            ModsView.Refresh();
            Log("SMR lookups turned off.");
        }
        else if (!IsBusy)
        {
            _ = RunBusy("Looking up mods on SMR…", () => LoadSmrAsync(refreshAll: true));
        }
    }

    private void ApplySmr(ModRow row)
    {
        if (!UseSmr || !_smrMods.TryGetValue(row.ModReference, out var smr)) return;
        row.Smr = smr;
        if (_logos.TryGetValue(smr.Id, out var logo)) row.Logo = logo;
    }

    /// <summary>
    /// Looks the listed mods up on SMR (batched into few requests), then fetches logos (cached on disk for a week).
    /// <paramref name="refreshAll"/> re-asks about every mod (after a GitHub check, so stats stay current); otherwise
    /// only mods not looked up yet are asked about. SMR is optional extra information, so failures are logged only.
    /// </summary>
    private async Task LoadSmrAsync(bool refreshAll)
    {
        if (!UseSmr || Mods.Count == 0) return;
        using var smr = new Sdmm.Core.Smr.SmrClient();
        var refs = Mods.Select(m => m.ModReference).Where(r => refreshAll || !_smrLookedUp.Contains(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Count > 0)
        {
            try
            {
                var found = await smr.GetModsAsync(refs);
                foreach (var r in refs)
                {
                    _smrLookedUp.Add(r);
                    _smrMods.Remove(r);
                }
                foreach (var (r, mod) in found) _smrMods[r] = mod;
                Log($"SMR: {found.Count} of {refs.Count} mods are published on ficsit.app.");
            }
            catch (Sdmm.Core.Smr.SmrException ex)
            {
                Log("SMR lookup skipped: " + ex.Message);
                return;
            }
        }
        foreach (var r in Mods) ApplySmr(r);
        ModsView.Refresh();
        RefreshUpdateCount();
        InstallRowFromSmrCommand.NotifyCanExecuteChanged();

        var logoDir = Path.Combine(CachePath, "smr-logos");
        var wanted = Mods.Select(r => r.Smr).OfType<Sdmm.Core.Smr.SmrMod>()
            .Where(m => m.LogoUrl is not null && !_logos.ContainsKey(m.Id)).DistinctBy(m => m.Id).ToList();
        foreach (var mod in wanted)
        {
            var image = await LoadLogoAsync(smr, mod, logoDir);
            if (image is null)
            {
                Log($"SMR: couldn't load the logo for {mod.ModReference}.");
                continue;
            }
            _logos[mod.Id] = image;
            foreach (var r in Mods.Where(r => r.Smr?.Id == mod.Id)) r.Logo = image;
        }
    }

    /// <summary>Loads an SMR logo from the disk cache (a week old at most) or downloads it. Null if unavailable.</summary>
    internal static async Task<System.Windows.Media.ImageSource?> LoadLogoAsync(Sdmm.Core.Smr.SmrClient smr, Sdmm.Core.Smr.SmrMod mod, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, mod.Id + ".img");
            byte[]? bytes = null;
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LogoMaxAge)
                bytes = await File.ReadAllBytesAsync(file);
            if (bytes is null)
            {
                bytes = await smr.DownloadLogoAsync(mod.LogoUrl!);
                if (bytes is null) return null;
                await File.WriteAllBytesAsync(file, bytes);
            }
            return DecodeImage(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes PNG/JPEG/WebP through Windows' codecs. WebP needs the WebP codec that ships with most Windows 10/11
    /// installs; without it this returns null and the row keeps its initials tile.
    /// </summary>
    private static System.Windows.Media.ImageSource? DecodeImage(byte[] bytes)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 168; // the largest tile is 84 px; 2x for high-DPI screens
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    // ---- SDMM updates -------------------------------------------------------------------------------------

    [ObservableProperty] private bool _checkUpdatesOnStartup;

    partial void OnCheckUpdatesOnStartupChanged(bool value)
    {
        _config.CheckForUpdatesOnStartup = value;
        SaveConfig();
    }

    public string AppVersionText => $"v{AppInfo.Version}";

    /// <summary>Set just before the self-updater closes the app, so closing doesn't ask about the running operation.</summary>
    public bool ClosingForUpdate { get; private set; }

    /// <summary>Called after startup; respects the startup toggle and a skipped version.</summary>
    public Task CheckForUpdatesOnStartupAsync() =>
        _config.CheckForUpdatesOnStartup ? CheckForAppUpdateAsync(manual: false) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task CheckForUpdates() => CheckForAppUpdateAsync(manual: true);

    private async Task CheckForAppUpdateAsync(bool manual)
    {
        Sdmm.Core.Updates.UpdateCheckResult result;
        try
        {
            using var gh = CreateGitHub();
            result = await Sdmm.Core.Updates.UpdateChecker.CheckAsync(gh, AppInfo.UpdateSource, AppInfo.Version);
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Log("Update check failed: " + ex.Message);
            if (manual) _dialogs.Error("Check for updates", "Couldn't check for updates: " + ex.Message);
            return;
        }

        Log("Update check: " + result.Message);
        if (result.Status != Sdmm.Core.Updates.UpdateCheckStatus.UpdateAvailable)
        {
            if (manual)
            {
                var msg = result.Status == Sdmm.Core.Updates.UpdateCheckStatus.SourceNotFound
                    ? result.Message + "\n\nSDMM's release page isn't published yet, so there's nothing to check against."
                    : result.Message;
                _dialogs.Info("Check for updates", msg);
            }
            return;
        }

        var update = result.Update!;
        if (!manual && string.Equals(_config.SkippedUpdateTag, update.Tag, StringComparison.Ordinal))
        {
            Log($"Update {update.Tag} was skipped earlier; not asking again.");
            return;
        }

        bool canSelfUpdate = AppUpdater.CanSelfUpdate(out var reason);
        switch (_dialogs.AskUpdate(update, AppInfo.Version.ToString(), canSelfUpdate, reason))
        {
            case UpdateChoice.SkipVersion:
                _config.SkippedUpdateTag = update.Tag;
                SaveConfig();
                Log($"Skipping SDMM {update.Tag}. A newer release will still be offered.");
                break;
            case UpdateChoice.UpdateNow when !canSelfUpdate:
                OpenUrl(update.HtmlUrl ?? AppInfo.UpdateSource.ReleasesUrl);
                break;
            case UpdateChoice.UpdateNow:
                await RunBusy($"Downloading SDMM {update.Tag}…", async () =>
                {
                    using var gh = CreateGitHub();
                    await AppUpdater.ApplyAsync(gh, AppInfo.UpdateSource, update);
                    Log($"Updated to {update.Tag}; restarting.");
                    ClosingForUpdate = true; // the new copy is already starting; don't ask whether to close
                    Application.Current.Shutdown();
                });
                break;
        }
    }

    // ---- missing dependencies ---------------------------------------------------------------------------

    /// <summary>Dependencies of the listed mods that are missing or the wrong version, from the last read of the install.</summary>
    private IReadOnlyList<DependencyNeed> _unmet = [];

    /// <summary>Above the list: which dependencies are missing, with a button to install them. Empty when none are.</summary>
    [ObservableProperty] private string _dependencyBannerText = "";

    private void UpdateDependencyBanner()
    {
        DependencyBannerText = _unmet.Count == 0 ? ""
            : _unmet.Count == 1
                ? $"{_unmet[0].ModReference} is {(_unmet[0].Installed is null ? "missing" : "the wrong version")}: {string.Join(", ", _unmet[0].Requirements.Select(r => r.NeededBy).Distinct())} need{(_unmet[0].Requirements.Select(r => r.NeededBy).Distinct().Count() == 1 ? "s" : "")} {_unmet[0].Requirements[0].RangeText}. The game won't start with it like this."
                : $"{_unmet.Count} dependencies are missing or the wrong version: {string.Join(", ", _unmet.Select(n => n.ModReference))}. The game won't start with them like this.";
        InstallDependenciesCommand.NotifyCanExecuteChanged();
    }

    private bool CanInstallDependencies(ModRow? row) =>
        !IsBusy && SelectedInstall is not null && (row is null ? _unmet.Count > 0 : row.HasUnmetDependencies);

    /// <summary>
    /// Installs the dependencies that are missing or too old, for one mod (from the details panel) or for every listed
    /// mod (the banner). Each comes from your GitHub sources when it's there, else from SMR when SMR installs are
    /// available; otherwise it says which ones to get with SMM instead. Shows the whole plan first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallDependencies))]
    private Task InstallDependenciesAsync(ModRow? row) => RunBusy("Installing missing dependencies…", async () =>
    {
        EnsureRowsMatchSelection();
        var item = SelectedInstall!;
        if (_scan is not { } scan || scan.Item != item) return;
        var needs = row is null ? _unmet
            : DependencyFixer.FindUnmet(scan.Read.Mods, [row.ModReference]);
        if (needs.Count == 0)
        {
            _dialogs.Info("Install missing dependencies", "Nothing is missing any more.");
            return;
        }
        bool smrAllowed = Brand.Current.IsVariant;
        var fixes = DependencyFixer.Plan(needs, AllMods(), scan.Read.Target, IncludePrereleases, smrAllowed);
        var github = fixes.Where(f => f.Build is not null).ToList();
        var smr = fixes.Where(f => f.FromSmr).ToList();
        var cant = fixes.Where(f => !f.CanFix).ToList();

        var text = new System.Text.StringBuilder();
        if (github.Count + smr.Count > 0)
        {
            text.Append($"Install into '{item.Name}':\n");
            foreach (var f in github)
                text.Append($"\n• {f.Need.ModReference} {f.Build!.Tag} from {(f.Build.IsImported ? "an imported zip" : f.Build.RepoFullName)}");
            foreach (var f in smr)
                text.Append($"\n• {f.Need.ModReference} from SMR (the newest that fits {string.Join(" and ", f.Need.Requirements.Select(r => r.RangeText).Distinct())})");
        }
        if (cant.Count > 0)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(github.Count + smr.Count > 0 ? "These can't be installed from here:\n" : "SDMM can't install these:\n");
            foreach (var f in cant) text.Append($"\n• {f.Need.Describe()}: {f.WhyNot}");
        }
        if (github.Count + smr.Count == 0)
        {
            _dialogs.Error("Install missing dependencies", text.ToString());
            return;
        }
        if (smr.Count > 0) text.Append("\n\nSMR's own dependencies for these are added as needed, and shown before anything is installed.");
        if (item.IsServer) text.Append("\n\n" + ServerNote(item));
        if (!_dialogs.Confirm("Install missing dependencies", text.ToString())) return;

        // SMR first: libraries such as SML usually come from there, and dev builds may need them.
        if (smr.Count > 0)
            await InstallFromSmrCoreAsync(smr.SelectMany(f => f.Need.Requirements.Select(r => new Sdmm.Core.Smr.SmrRequest(f.Need.ModReference, null, r.Range, r.NeededBy))).ToList(),
                item, askFirst: false);
        if (github.Count > 0 && await InstallBuildsAsync(item, github.Select(f => f.Build!).ToList()))
            await RefreshAfterChangeAsync(item);
        if (_unmet.Count > 0)
            Log($"Still unmet after installing: {string.Join(", ", _unmet.Select(n => n.Describe()))}. A dependency's own dependencies show up here once it's installed.");
    });

    // ---- install / remove -------------------------------------------------------------------------------

    private bool CanInstall(ModRow? row) => !IsBusy && row?.CanInstall == true && SelectedInstall is not null &&
                                            (!row.UseSmrSource || Brand.Current.IsVariant);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync(ModRow? row) => RunBusy($"Installing {row?.ModReference}…", async () =>
    {
        EnsureRowsMatchSelection();
        var item = SelectedInstall!;
        if (row!.UseSmrSource)
        {
            await InstallFromSmrCoreAsync([new Sdmm.Core.Smr.SmrRequest(row.ModReference, row.SelectedSmrVersion?.Version?.Version)]);
            return;
        }
        var build = row.SelectedBuild!.Build;
        if (item.IsServer && !_dialogs.Confirm("Install to server",
                $"Install {build.ModReference} {build.Tag} on the server '{item.Name}'?\n\n{ServerNote(item)} Everyone joining must then have exactly this version."))
            return;
        await InstallBuildsAsync(item, [build]);
        await RefreshAfterChangeAsync(item);
    });

    /// <summary>
    /// Downloads, plans (with confirmations) and installs a batch of builds into one install. Returns false if the
    /// plan was refused or the user cancelled; failures throw.
    /// </summary>
    private async Task<bool> InstallBuildsAsync(InstallItem item, IReadOnlyList<ModBuild> builds)
    {
        using var opened = await Task.Run(() => Open(item));
        var info = await Task.Run(() => opened.Install.Detect());
        if (!await Task.Run(() => EnsureServerWritable(item, opened.Install))) return false;

        long bytes = builds.Sum(b => b.Assets.TryGetValue(info.Target, out var a) && b.LocalZips is null ? a.Size : 0);
        if (DiskSpaceWarning(item, bytes) is { } space && !_dialogs.Confirm("Low disk space", space + "\n\nInstall anyway?"))
            return false;

        using var gh = CreateGitHub();
        var cache = CreateCache();
        var requests = new List<InstallRequest>();
        foreach (var b in builds)
        {
            if (!b.Assets.TryGetValue(info.Target, out var asset))
                throw new InvalidOperationException($"{b.ModReference} {b.Tag} has no {info.Target} build.");
            if (b.LocalZips?.TryGetValue(info.Target, out var localZip) == true)
            {
                // Imported by hand: no download. The marker records no repo, which means "local file".
                Log($"{b.ModReference}: using imported {asset.Name} {b.Tag}");
                var local = await Task.Run(() => ModPackage.Open(localZip));
                requests.Add(new InstallRequest(local, new PackageSource(null, b.Tag, asset.Name)));
                continue;
            }
            Log($"{b.ModReference}: getting {asset.Name} ({asset.Size / 1048576.0:0.0} MB) from {b.RepoFullName} {b.Tag}");
            var pkg = await cache.GetAsync(gh, b, info.Target);
            requests.Add(new InstallRequest(pkg, new PackageSource(b.RepoFullName, b.Tag, asset.Name)));
        }
        return await InstallRequestsAsync(item, opened, requests, cache);
    }

    /// <summary>Plans (with confirmations) and installs downloaded packages into an open install. False if refused or cancelled.</summary>
    /// <param name="installed">What's installed, when the caller just read it; saves the planner reading it again.</param>
    private async Task<bool> InstallRequestsAsync(InstallItem item, OpenedInstall opened, IReadOnlyList<InstallRequest> requests, PackageCache cache,
        IReadOnlyList<InstalledMod>? installed = null)
    {
        MarkChanged(requests.Select(r => r.Package.ModReference));
        var plan = await Task.Run(() => new InstallPlanner().Plan(opened.Install, requests, item.IsRemote ? null : _smm, installed));
        foreach (var w in plan.Issues.Where(i => i.Level == IssueLevel.Warning)) Log($"Note: {w.Message}");

        if (plan.IsBlocked)
        {
            var reasons = plan.Issues.Where(i => i.Level == IssueLevel.Blocked).Select(i => "• " + i.Message).Distinct();
            _dialogs.Error("Can't install", string.Join("\n\n", reasons));
            Log("Install refused: " + string.Join(" | ", plan.Issues.Where(i => i.Level == IssueLevel.Blocked).Select(i => i.Code).Distinct()));
            return false;
        }

        var confirmed = new HashSet<string>();
        var needs = plan.NeedingConfirmation;
        if (needs.Count > 0)
        {
            var text = string.Join("\n\n", needs.Select(i => "• " + i.Message).Distinct()) + "\n\nInstall anyway?";
            if (!_dialogs.Confirm("Problems found", text))
            {
                Log("Install cancelled.");
                return false;
            }
            foreach (var i in needs) confirmed.Add(i.Code);
        }

        InstallResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var installer = new Installer(Log);
                // Leftovers from an install that was interrupted (crash, power loss); otherwise they'd stay forever.
                installer.CleanupStaging(opened.Install);
                return installer.Execute(plan, confirmed);
            });
        }
        catch (Exception ex) when (item.IsRemote && ModsFolderAccess.Check(opened.Install.FileSystem) is { } lockError)
        {
            // The server locked the folder mid-install (it was started, say); the installer has rolled back.
            ServerLockText = LockedMessage(lockError);
            throw new Sdmm.Remote.RemoteRefusedException(lockError, ex);
        }
        Log($"Done: installed {result.Installed.Count}, already up to date {result.Skipped.Count}.");
        var freed = await Task.Run(() => cache.Prune(KeepBuildsPerAsset));
        if (freed > 0) Log($"Download cache: removed older builds ({freed / 1048576.0:0.0} MB freed; they can be downloaded again).");
        UpdateCacheSize();
        if (item.IsServer && result.Installed.Count > 0)
            _dialogs.Info("Restart the server", item.IsRemote
                ? "Installed on the server. Start (or restart) it from your host's panel so it loads the new version."
                : "Installed on the server. Restart it so it loads the new version.");
        return true;
    }

    private bool CanRemove(ModRow? row) => !IsBusy && row?.CanRemove == true && SelectedInstall is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync(ModRow? row) => RunBusy($"Removing {row?.ModReference}…", async () =>
    {
        EnsureRowsMatchSelection();
        var item = SelectedInstall!;
        // Every installed mod counts, not just the listed ones (SMM's mods can depend on a dev build too).
        var installedHere = _scan is { } last && last.Item == item ? last.Read.Mods
            : await Task.Run(() => { using var o = Open(item); return ModScanner.Scan(o.Install.FileSystem); });
        var deps = DependentsWarning(installedHere, [row!.ModReference]);
        if (!_dialogs.Confirm("Remove dev build", $"Remove {row.ModReference} from {item.Name}?\n\nThe build stays in the download cache, so you can reinstall it without downloading again." +
                (deps is null ? "" : "\n\n" + deps)))
            return;
        bool removed = await Task.Run(() =>
        {
            using var opened = Open(item);
            if (!EnsureServerWritable(item, opened.Install)) return false;
            MarkChanged([row.ModReference]);
            new Installer(Log).Uninstall(opened.Install, row.ModReference);
            return true;
        });
        if (!removed) return;
        await RefreshAfterChangeAsync(item);
    });

    // ---- server sync ------------------------------------------------------------------------------------

    private bool CanSync() => !IsBusy && SelectedLocal is not null && SelectedServer is not null;

    /// <summary>The pair and direction the sync lines were compared for; Match refuses if any changed since.</summary>
    private (InstallItem Server, InstallItem Local, SyncDirection Direction)? _syncFor;

    [ObservableProperty] private string _syncSummary = "";

    /// <summary>Sync window: change the game to match the server (default), or the server to match the game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncGameMatchesServer), nameof(SyncTargetText))]
    [NotifyCanExecuteChangedFor(nameof(ApplySyncCommand))]
    private bool _syncServerMatchesGame;

    public bool SyncGameMatchesServer
    {
        get => !SyncServerMatchesGame;
        set => SyncServerMatchesGame = !value;
    }

    public string SyncTargetText => SyncServerMatchesGame
        ? $"Changes the server '{SelectedServer?.Name}' to match your game '{SelectedLocal?.Name}'."
        : $"Changes your game '{SelectedLocal?.Name}' to match the server '{SelectedServer?.Name}'.";

    partial void OnSyncServerMatchesGameChanged(bool value)
    {
        SyncLines.Clear();
        SyncSummary = "";
        _syncFor = null;
    }

    private SyncDirection CurrentSyncDirection => SyncServerMatchesGame ? SyncDirection.ServerMatchesGame : SyncDirection.GameMatchesServer;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncFromServerAsync() => RunBusy("Comparing with the server…", CompareWithServerAsync);

    private async Task CompareWithServerAsync()
    {
        var server = SelectedServer!;
        var local = SelectedLocal!;
        var direction = CurrentSyncDirection;
        if (_discovery is null) await RefreshFromGitHubForSync();

        var (serverMods, localMods, serverTarget, localTarget) = await Task.Run(() =>
        {
            using var s = Open(server);
            var st = s.Install.Detect().Target;
            using var l = Open(local);
            var lt = l.Install.Detect().Target;
            return (ModScanner.Scan(s.Install.FileSystem), ModScanner.Scan(l.Install.FileSystem), st, lt);
        });
        var items = ServerSync.Compare(serverMods, localMods, new DiscoveryResult(AllMods(), [], []),
            direction == SyncDirection.GameMatchesServer ? localTarget : serverTarget, direction);
        bool variant = Brand.Current.IsVariant;
        SyncLines.Clear();
        // Matching mods are only listed when they're dev builds; matching SMM mods would just be noise.
        var changed = direction == SyncDirection.GameMatchesServer ? serverMods : localMods;
        foreach (var i in items.Where(i => i.Status != SyncStatus.InSync || changed.Any(m => m.ModReference.Equals(i.ModReference, StringComparison.OrdinalIgnoreCase) && m.Owner == ModOwner.Sdmm)))
            SyncLines.Add(new SyncLine(i, variant));
        _syncFor = (server, local, direction);

        int toInstall = items.Count(i => i.Status == SyncStatus.Install || (variant && i.Status == SyncStatus.FromSmr));
        int toRemove = items.Count(i => i.Status == SyncStatus.Remove);
        int blocking = items.Count(i => i.Status is SyncStatus.BuildNotFound or SyncStatus.ManagedElsewhere || (!variant && i.Status == SyncStatus.FromSmr));
        int oneSided = items.Count(i => i.Status is SyncStatus.ServerOnly or SyncStatus.ClientOnly);
        var (changing, other) = direction == SyncDirection.GameMatchesServer ? (local.Name, server.Name) : (server.Name, local.Name);
        SyncSummary = toInstall + toRemove + blocking == 0
            ? $"{local.Name} and {server.Name} match; SML should let you join." + (oneSided > 0 ? $" ({oneSided} mod{(oneSided == 1 ? " is" : "s are")} only needed on one side.)" : "")
            : $"To make {changing} match {other}: install {toInstall}, remove {toRemove}." + (blocking > 0 ? $" {blocking} need{(blocking == 1 ? "s" : "")} fixing by hand (see Details)." : "");
        Log($"Compared {local.Name} with {server.Name} ({direction}): {toInstall} to install, {toRemove} to remove, {blocking} to fix by hand, {oneSided} one-sided.");
        ApplySyncCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshFromGitHubForSync()
    {
        using var gh = CreateGitHub();
        _discovery = await new ModDiscovery(gh).DiscoverAsync(_config.Sources);
        foreach (var e in _discovery.Errors) Log("GitHub: " + e);
        ReportDiscoveryProblems(_discovery);
    }

    private bool CanApplySync() => !IsBusy && SyncLines.Any(l => l.IsActionable) &&
                                   _syncFor is { } p && p.Server == SelectedServer && p.Local == SelectedLocal && p.Direction == CurrentSyncDirection;

    /// <summary>Installs and removes mods on the side being changed, for the ticked lines only.</summary>
    [RelayCommand(CanExecute = nameof(CanApplySync))]
    private Task ApplySyncAsync() => RunBusy("Matching…", async () =>
    {
        var direction = CurrentSyncDirection;
        var target = direction == SyncDirection.GameMatchesServer ? SelectedLocal! : SelectedServer!;
        var other = direction == SyncDirection.GameMatchesServer ? SelectedServer! : SelectedLocal!;
        var picked = SyncLines.Where(l => l.IsActionable && l.IsSelected).Select(l => l.Item).ToList();
        var installs = picked.Where(i => i.Status == SyncStatus.Install).Select(i => i.Build!).ToList();
        var fromSmr = picked.Where(i => i.Status == SyncStatus.FromSmr && i.SmrVersion is not null).ToList();
        var removes = picked.Where(i => i.Status == SyncStatus.Remove).Select(i => i.ModReference).ToList();
        if (installs.Count + fromSmr.Count + removes.Count == 0)
        {
            _dialogs.Info("Match", "Nothing is ticked.");
            return;
        }
        var targetMods = await Task.Run(() => { using var o = Open(target); return ModScanner.Scan(o.Install.FileSystem); });
        var text = $"Change {(target.IsServer ? "the server" : "your game")} '{target.Name}' to match '{other.Name}'?\n";
        if (installs.Count > 0) text += $"\nInstall: {string.Join(", ", installs.Select(b => $"{b.ModReference} {b.Tag}"))}";
        if (fromSmr.Count > 0) text += $"\nInstall from SMR: {string.Join(", ", fromSmr.Select(i => $"{i.ModReference} {i.SmrVersion}"))}";
        if (removes.Count > 0) text += $"\nRemove: {string.Join(", ", removes)} (kept in the download cache)";
        if (DependentsWarning(targetMods, removes) is { } deps) text += "\n\n" + deps;
        if (target.IsServer) text += "\n\n" + ServerNote(target);
        if (!_dialogs.Confirm("Match", text)) return;

        if (installs.Count > 0 && !await InstallBuildsAsync(target, installs)) return;
        if (fromSmr.Count > 0)
            await InstallFromSmrCoreAsync(fromSmr.Select(i => new Sdmm.Core.Smr.SmrRequest(i.ModReference,
                Sdmm.Core.Versioning.SemVersion.TryParse(i.SmrVersion, out var v) ? v : null)).ToList(), target, askFirst: false);
        if (removes.Count > 0)
            await Task.Run(() =>
            {
                using var opened = Open(target);
                if (!EnsureServerWritable(target, opened.Install)) return;
                var installer = new Installer(Log);
                foreach (var r in removes) { installer.Uninstall(opened.Install, r); MarkChanged([r]); }
            });
        // Show the result: a successful match leaves only in-sync, one-sided and hand-fix lines.
        await CompareWithServerAsync();
        await RefreshAfterChangeAsync(target);
    });

    /// <summary>
    /// For a removal: the installed mods (that aren't being removed too) that require one of <paramref name="removing"/>,
    /// so the game won't load them afterwards. Null when nothing depends on them.
    /// </summary>
    private static string? DependentsWarning(IReadOnlyList<InstalledMod> installed, IReadOnlyCollection<string> removing)
    {
        if (removing.Count == 0) return null;
        var lines = installed
            .Where(m => m.Descriptor is not null && !removing.Contains(m.ModReference, StringComparer.OrdinalIgnoreCase))
            .SelectMany(m => m.Descriptor!.Plugins
                .Where(d => !d.Optional && d.Enabled && removing.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                .Select(d => $"{m.ModReference} needs {d.Name}"))
            .Distinct().ToList();
        return lines.Count == 0 ? null
            : $"Warning: {string.Join("; ", lines)}. The game won't load {(lines.Count == 1 ? "that mod" : "those mods")} without it.";
    }

    // ---- installs tab -----------------------------------------------------------------------------------

    [RelayCommand]
    private void AddLocalInstall()
    {
        var folder = _dialogs.PickFolder("Pick the Satisfactory folder (the one containing FactoryGame and Engine)");
        if (folder is null) return;
        AddLocal(folder, null);
    }

    /// <param name="quiet">Log instead of showing an error when the folder isn't a game install (auto-detect).</param>
    private bool AddLocal(string folder, string? name, bool quiet = false)
    {
        if (Installs.Any(i => !i.IsRemote && string.Equals(Path.GetFullPath(i.Config.Path).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            if (!quiet) Log($"{folder} is already in the list.");
            return false;
        }
        GameInfo info;
        try
        {
            info = GameInstall.Local("", folder).Detect();
        }
        catch (Exception ex)
        {
            if (quiet) Log($"Skipped {folder}: {ex.Message}");
            else _dialogs.Error("Not a game folder", ex.Message);
            return false;
        }
        name ??= $"{Path.GetFileName(folder.TrimEnd('\\'))} ({folder[..2]})";
        // Two installs from one store (two Steam libraries, say) would otherwise get the same name.
        if (Installs.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{name} - {Path.GetFileName(folder.TrimEnd('\\'))}";
        var cfg = new InstallConfig { Name = name, Kind = InstallKind.Local, Path = folder };
        Installs.Add(new InstallItem(cfg) { Status = $"{info.Target}, build {info.Changelist}" });
        SaveConfig();
        if (Installs[^1].IsServer) SelectedServer ??= Installs[^1];
        else SelectedLocal ??= Installs[^1];
        Log($"Added install {cfg.Name}: {folder} ({info.Target}, build {info.Changelist}).");
        return true;
    }

    [RelayCommand]
    private void AutoDetectInstalls()
    {
        int added = AutoDetect();
        if (added == 0) _dialogs.Info("Auto-detect", "No new installs found. Use Add folder… for an install in another place.");
    }

    /// <summary>Adds every Satisfactory install Steam, Epic and SMM know about that isn't listed yet. Returns how many were added.</summary>
    private int AutoDetect()
    {
        _smm = SmmState.Load(_config.SmmDirectory);
        var found = new List<(string Path, string? Name)>();
        try
        {
            string? steamRoot = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            steamRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            foreach (var g in GameLocator.FindSteam(steamRoot)) found.Add((g.Path, $"Steam ({g.Path[..2].ToUpperInvariant()})"));
            // A dedicated server installed on this PC through Steam; it's listed under Server mode.
            foreach (var g in GameLocator.FindSteamServers(steamRoot)) found.Add((g.Path, $"Local dedicated server ({g.Path[..2].ToUpperInvariant()})"));
            var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            foreach (var g in GameLocator.FindEpic(epic))
                found.Add((g.Path, $"Epic{(g.Label is { } l ? " " + l : "")} ({g.Path[..2].ToUpperInvariant()})"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log("Auto-detect: couldn't read the Steam or Epic records: " + ex.Message);
        }
        // SMM's own list (it can list one folder twice with different case; AddLocal skips duplicates).
        found.AddRange(_smm.Installations.Where(i => !i.Path.Contains("://") && Directory.Exists(i.Path)).Select(i => (i.Path, (string?)null)));

        int added = found.Count(f => AddLocal(f.Path, f.Name, quiet: true));
        Log(added == 0 ? "Auto-detect: no new installs found." : $"Auto-detect: added {added} install(s).");
        return added;
    }

    /// <summary>Asks whether the server is hosted or on this PC, then adds it and selects it.</summary>
    [RelayCommand]
    private void AddServerInstall()
    {
        switch (_dialogs.ChooseServerKind())
        {
            case ServerKind.Hosted: AddHostedServer(); break;
            case ServerKind.ThisPc: AddServerOnThisPc(); break;
        }
    }

    private void AddHostedServer()
    {
        var cfg = new InstallConfig { Name = "Server", Kind = InstallKind.Sftp, Path = "/" };
        if (!_dialogs.EditServer(cfg, TestServerDraftAsync, out var password)) return;
        if (password is not null) cfg.ProtectedPassword = SecretProtector.Protect(password);
        var item = new InstallItem(cfg);
        Installs.Add(item);
        SaveConfig();
        SelectedServer = item;
        UpdateEmptyState();
        Log($"Added server {cfg.Name} ({item.Location}).");
    }

    /// <summary>Adds Steam's dedicated server if it's installed and not listed yet; otherwise asks for the folder.</summary>
    private void AddServerOnThisPc()
    {
        string? steamRoot = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        IReadOnlyList<FoundGame> steam;
        try
        {
            steam = GameLocator.FindSteamServers(steamRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            steam = [];
        }
        var fresh = steam.Where(g => !Installs.Any(i => !i.IsRemote && SamePath(i.Config.Path, g.Path))).ToList();
        if (fresh.Count > 0 && _dialogs.Confirm("Dedicated server on this PC",
                $"Found Steam's dedicated server at\n{fresh[0].Path}\n\nAdd it? (Choose No to pick another folder.)"))
        {
            AddServerFolder(fresh[0].Path, $"Local dedicated server ({fresh[0].Path[..2].ToUpperInvariant()})");
            return;
        }
        var folder = _dialogs.PickFolder("Pick the dedicated server's folder (the one containing FactoryGame and FactoryServer.exe)");
        if (folder is not null) AddServerFolder(folder, null);
    }

    private void AddServerFolder(string folder, string? name)
    {
        try
        {
            if (GameInstall.Local("", folder).Detect().Target == ModTarget.Windows)
            {
                _dialogs.Error("Not a dedicated server",
                    "That folder holds the game, not a dedicated server. Add it under My game instead, or pick the server's folder (the one containing FactoryServer.exe).");
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Not a server folder", ex.Message);
            return;
        }
        if (Installs.FirstOrDefault(i => !i.IsRemote && SamePath(i.Config.Path, folder)) is { } existing)
        {
            SelectedServer = existing;
            return;
        }
        if (AddLocal(folder, name)) SelectedServer = Installs[^1];
        UpdateEmptyState();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// "Test connection" in the hosted-server dialog: connects with the typed settings (nothing is saved), checks the
    /// game folder, and if FactoryGame isn't there, looks up to two levels down for it.
    /// </summary>
    private Task<ServerTestResult> TestServerDraftAsync(InstallConfig draft, string? typedPassword) => Task.Run(() =>
    {
        string? accepted = null;
        try
        {
            using var opened = InstallFactory.Open(draft, p => typedPassword ?? SecretProtector.Unprotect(p),
                fingerprint => _dialogs.Confirm("New server key",
                    $"First connection to {draft.Host}.\n\nServer fingerprint:\n{fingerprint}\n\n" +
                    "Accept it? If you can, compare it with the one your host shows. It is saved with the server, and a different key later is refused."),
                fingerprint => accepted = fingerprint);
            InstallFactory.TestConnection(opened);
            try
            {
                var info = opened.Install.Detect();
                int mods = ModScanner.Scan(opened.Install.FileSystem).Count;
                Log($"Test connection to {draft.Host}: {info.Target}, {mods} mods.");
                return new ServerTestResult(true,
                    $"Connected. Found a {info.Target} server" + (info.Changelist > 0 ? $" (build {info.Changelist})" : "") + $" with {mods} mod folder{(mods == 1 ? "" : "s")}.",
                    null, accepted);
            }
            catch (InvalidOperationException ex)
            {
                var found = GameRootFinder.Find(opened.Install.FileSystem);
                if (found is { Length: > 0 })
                {
                    var suggested = "/" + string.Join('/', new[] { draft.Path.Trim('/'), found }.Where(s => s.Length > 0));
                    return new ServerTestResult(false, $"Connected, but FactoryGame isn't in {draft.Path}. It's in {suggested}.", suggested, accepted);
                }
                if (found == "")
                    return new ServerTestResult(false,
                        "Connected and found FactoryGame, but couldn't tell the server's platform (no Engine folder or server program visible). Set Platform (LinuxServer or WindowsServer, as your host's panel says).",
                        null, accepted);
                // Detect's message lists what the folder does contain, which shows where to look.
                return new ServerTestResult(false,
                    $"Connected, but no FactoryGame folder was found in {draft.Path} or three levels below it. {ex.Message} Check the Game folder in your host's file manager.",
                    null, accepted);
            }
        }
        catch (Exception ex)
        {
            return new ServerTestResult(false, "Couldn't connect: " + ex.Message, null, accepted);
        }
    });

    [RelayCommand]
    private void EditInstall(InstallItem? item)
    {
        item ??= SelectedInstallInList;
        if (item is null) return;
        if (!item.IsRemote)
        {
            _dialogs.Info("Local install", "Folders on this PC have no settings to edit. Remove it from the list and add it again to change the folder.");
            return;
        }
        if (!_dialogs.EditServer(item.Config, TestServerDraftAsync, out var password)) return;
        if (password is not null) item.Config.ProtectedPassword = SecretProtector.Protect(password);
        item.Refresh();
        SaveConfig();
        RememberSelection(); // a new host or folder changes the server's key
    }

    [RelayCommand]
    private void RemoveInstall(InstallItem? item)
    {
        item ??= SelectedInstallInList;
        if (item is null) return;
        if (!_dialogs.Confirm("Remove from list", $"Remove '{item.Name}' from this list?\n\nNothing is deleted from the game folder or the server."))
            return;
        Installs.Remove(item);
        if (SelectedLocal == item) SelectedLocal = Installs.FirstOrDefault(i => !i.IsServer);
        if (SelectedServer == item) SelectedServer = Installs.FirstOrDefault(i => i.IsServer);
        if (_syncFor is { } p && (p.Server == item || p.Local == item))
        {
            _syncFor = null;
            SyncLines.Clear();
            SyncSummary = "";
        }
        SaveConfig();
    }

    [RelayCommand]
    private Task TestInstallAsync(InstallItem? item)
    {
        item ??= SelectedInstallInList;
        if (item is null) return Task.CompletedTask;
        return RunBusy($"Testing {item.Name}…", async () =>
        {
            var (info, mods) = await Task.Run(() =>
            {
                using var opened = Open(item);
                InstallFactory.TestConnection(opened);
                var i = opened.Install.Detect();
                return (i, ModScanner.Scan(opened.Install.FileSystem));
            });
            item.Status = $"OK: {info.Target}, build {(info.Changelist == 0 ? "unknown" : info.Changelist)}, {mods.Count} mods";
            Log($"{item.Name}: {item.Status} (" + string.Join(", ", mods.GroupBy(m => m.Owner).Select(g => $"{g.Key} {g.Count()}")) + ")");
        });
    }

    // ---- sources tab ------------------------------------------------------------------------------------

    /// <summary>The SMR checkbox in Settings; applied to <see cref="UseSmr"/> on Save.</summary>
    [ObservableProperty] private bool _useSmrDraft;

    /// <summary>
    /// Fills the Settings window's source, token and SMR fields from the saved settings. Also how Cancel (or closing the window) throws
    /// away unsaved edits, since the fields are bound straight to these properties.
    /// </summary>
    public void LoadSourcesIntoForm()
    {
        var s = _config.Sources;
        UsersText = string.Join(Environment.NewLine, s.Users);
        OrgsText = string.Join(Environment.NewLine, s.Orgs);
        ReposText = string.Join(Environment.NewLine, s.Repos);
        Topic = s.Topic ?? "";
        IncludeForks = s.IncludeForks;
        IncludeArchived = s.IncludeArchived;
        UseSmrDraft = UseSmr;
    }

    /// <summary>Set when sources or the token changed; checked when Settings closes.</summary>
    private bool _sourcesChanged;

    /// <summary>
    /// Called after Settings closes: checks GitHub again if sources or the token changed, so the
    /// list reflects them without another click. Waits if something else is running.
    /// </summary>
    public void OnSourcesWindowClosed(bool refresh = true)
    {
        OnPropertyChanged(nameof(HasSources));
        UpdateEmptyState();
        if (!_sourcesChanged) return;
        _sourcesChanged = false;
        if (!refresh || !HasSources) return;
        if (IsBusy) _refreshWhenIdle = true;
        else _ = RefreshAsync();
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SourceConfig.NormalizeEntry).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Saves the Settings window's source and SMR fields. Returns false (and says why) if they're invalid, so the window stays open.</summary>
    public bool TrySaveSources()
    {
        var repos = Lines(ReposText);
        var bad = repos.Where(r => r.Split('/').Length != 2).ToList();
        if (bad.Count > 0)
        {
            _dialogs.Error("Repos", "These are not in owner/repo form:\n" + string.Join("\n", bad));
            return false;
        }
        var updated = new SourceConfig
        {
            Users = Lines(UsersText).Select(u => u.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Orgs = Lines(OrgsText).Select(o => o.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Repos = repos,
            Topic = string.IsNullOrWhiteSpace(Topic) ? null : Topic.Trim(),
            IncludeForks = IncludeForks,
            IncludeArchived = IncludeArchived,
        };
        var old = _config.Sources;
        bool changed = !updated.Users.SequenceEqual(old.Users, StringComparer.OrdinalIgnoreCase) ||
                       !updated.Orgs.SequenceEqual(old.Orgs, StringComparer.OrdinalIgnoreCase) ||
                       !updated.Repos.SequenceEqual(old.Repos, StringComparer.OrdinalIgnoreCase) ||
                       updated.Topic != old.Topic || updated.IncludeForks != old.IncludeForks || updated.IncludeArchived != old.IncludeArchived;
        _config.Sources = updated;
        SaveConfig();
        if (changed)
        {
            _sourcesChanged = true;
            Log($"Sources saved: {updated.Users.Count} users, {updated.Orgs.Count} orgs, {updated.Repos.Count} repos.");
        }
        UseSmr = UseSmrDraft;
        LoadSourcesIntoForm();
        return true;
    }

    private void UpdateCacheSize()
    {
        try
        {
            CacheSizeText = $"{CreateCache().GetSizeBytes() / 1048576.0:0.0} MB (keeps the newest {KeepBuildsPerAsset} builds of each mod file)";
            ImportedSizeText = $"{CreateImported().GetSizeBytes() / 1048576.0:0.0} MB";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CacheSizeText = "unknown: " + ex.Message;
        }
    }

    [ObservableProperty] private string _importedSizeText = "";

    /// <summary>
    /// Adds mod zips by hand (builds that aren't on GitHub, or your own Alpakit output). They're validated, copied
    /// into SDMM, listed with the GitHub builds, and offered for install into the selected game or server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task InstallFromZipAsync()
    {
        var files = _dialogs.PickZips("Pick mod zips (Alpakit output, for example ModName-Windows.zip)");
        if (files is null || files.Count == 0) return Task.CompletedTask;
        return RunBusy("Importing zips…", async () =>
        {
            var store = CreateImported();
            var imported = new List<ModPackage>();
            var failed = new List<string>();
            foreach (var f in files)
            {
                try
                {
                    var pkg = await Task.Run(() => store.Import(f));
                    imported.Add(pkg);
                    Log($"Imported {Path.GetFileName(f)}: {pkg.ModReference} {pkg.Descriptor.EffectiveVersion} ({pkg.Target}).");
                }
                catch (Exception ex) when (ex is InvalidModPackageException or IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
                {
                    failed.Add($"• {Path.GetFileName(f)}: {ex.Message}");
                }
            }
            UpdateCacheSize();
            if (failed.Count > 0) _dialogs.Error("Some zips weren't imported", string.Join("\n", failed));
            await BuildRowsAsync(reuseScan: true);
            if (imported.Count == 0) return;

            // Offer to install the ones that fit the selected install's platform right away.
            var item = SelectedInstall;
            if (item is null) return;
            var target = await Task.Run(() => { using var o = Open(item); return o.Install.Detect().Target; });
            var fitting = imported.Where(p => p.Target == target).ToList();
            var other = imported.Where(p => p.Target != target).Select(p => $"{p.ModReference} ({p.Target})").ToList();
            var note = other.Count > 0 ? $"\n\nKept for later (not a {target} build): {string.Join(", ", other)}." : "";
            if (fitting.Count == 0)
            {
                _dialogs.Info("Zips imported", $"Imported. They're listed with the other builds of each mod.{note}");
                return;
            }
            var list = string.Join(", ", fitting.Select(p => $"{p.ModReference} {p.Descriptor.EffectiveVersion}"));
            if (!_dialogs.Confirm("Install imported zips", $"Install {list} into {(item.IsServer ? "the server " : "")}'{item.Name}' now?{note}\n\nOtherwise they stay listed, ready to install later."))
                return;
            var builds = AllMods().SelectMany(m => m.Builds)
                .Where(b => b.IsImported && fitting.Any(p => p.ModReference.Equals(b.ModReference, StringComparison.OrdinalIgnoreCase) && b.Version == p.Descriptor.EffectiveVersion))
                .ToList();
            if (await InstallBuildsAsync(item, builds)) await RefreshAfterChangeAsync(item);
        });
    }

    [RelayCommand]
    private void ClearImported()
    {
        var store = CreateImported();
        if (!_dialogs.Confirm("Delete imported zips", $"Delete every zip added with Install from zip, in\n{store.Root}?\n\nInstalled mods are not affected, but builds that only existed as imported zips can't be reinstalled afterwards."))
            return;
        try
        {
            store.Clear();
            Log("Imported zips deleted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Delete imported zips", ex.Message);
        }
        UpdateCacheSize();
        if (!IsBusy) _ = RebuildRowsAsync(reuseScan: true);
    }

    /// <summary>Raised after the download cache was actually cleared (not when the confirmation is declined).</summary>
    public event Action? CacheCleared;

    /// <summary>Flips the branding variant and remembers it. Returns the new state.</summary>
    public bool ToggleVariant()
    {
        _config.Variant = !_config.Variant;
        Brand.Current.IsVariant = _config.Variant;
        SaveConfig();
        Log(_config.Variant ? "Welcome to the Satisfactory Black Market." : "Back to the Satisfactory Dev Mod Manager.");
        TakeOverCommand.NotifyCanExecuteChanged();
        TakeOverAllCommand.NotifyCanExecuteChanged();
        RemoveFromSmmProfilesCommand.NotifyCanExecuteChanged();
        HandBackCommand.NotifyCanExecuteChanged();
        HandBackAllCommand.NotifyCanExecuteChanged();
        InstallRowFromSmrCommand.NotifyCanExecuteChanged();
        RefreshUpdateCount();
        BrowseSmrCommand.NotifyCanExecuteChanged();
        // Which mods are listed depends on it; rebuild so the list matches.
        if (IsBusy) _selectionChangedWhileBusy = true;
        else _ = RebuildRowsAsync(reuseScan: true);
        return _config.Variant;
    }

    // ---- modpacks ---------------------------------------------------------------------------------------------
    //
    // A modpack is a named list of mods and versions, not files: applying it picks each mod's zip for the target
    // install's platform, and each mod's side (both, client only, server only) decides whether it goes on a game or a
    // server. SMR entries are only installed when SMR installs are available.

    /// <summary>Opens the Modpacks window (implemented by the main window).</summary>
    public event Action? ModpacksRequested;

    [RelayCommand]
    private void OpenModpacks() => ModpacksRequested?.Invoke();

    public IList<Sdmm.Core.Modpacks.Modpack> Modpacks => _config.Modpacks;
    public void SaveModpacks() => SaveConfig();
    public SmmState SmmSettings => _smm = SmmState.Load(_config.SmmDirectory);

    /// <summary>Captures the selected install as a modpack (every readable mod, with its version and side).</summary>
    public async Task<Sdmm.Core.Modpacks.Modpack?> CaptureModpackAsync(string name)
    {
        var item = SelectedInstall;
        if (item is null) return null;
        var installed = await Task.Run(() => { using var o = Open(item); return ModScanner.Scan(o.Install.FileSystem); });
        return Sdmm.Core.Modpacks.ModpackIO.FromInstall(name, installed, item.IsServer, SmrSourcePrefix, DateTimeOffset.UtcNow);
    }

    /// <summary>The selected install's platform and game build, for exported files' "exported from" note.</summary>
    public Task<(ModTarget Target, long Changelist)?> GetExportOriginAsync() => GetSelectedTargetAsync();

    public Task ApplyModpackAsync(Sdmm.Core.Modpacks.Modpack pack, bool removeOthers, bool useNewest) =>
        RunBusy($"Applying modpack '{pack.Name}'…", () => ApplyModpackCoreAsync(pack, removeOthers, useNewest));

    private async Task ApplyModpackCoreAsync(Sdmm.Core.Modpacks.Modpack pack, bool removeOthers, bool useNewest)
    {
        var item = SelectedInstall ?? throw new InvalidOperationException("Pick a game or server first.");
        bool variant = Brand.Current.IsVariant;
        var (info, installed) = await Task.Run(() =>
        {
            using var o = Open(item);
            return (o.Install.Detect(), ModScanner.Scan(o.Install.FileSystem));
        });
        var changes = Sdmm.Core.Modpacks.ModpackPlanner.Plan(pack, installed, item.IsServer, removeOthers, useNewest);
        var skipped = changes.Skipped.Select(x => $"{x.Mod} ({x.Why})").ToList();
        var problems = new List<string>();

        // Dev builds and imported zips, from the sources and imported zips SDMM knows.
        var all = AllMods().ToDictionary(m => m.ModReference, StringComparer.OrdinalIgnoreCase);
        var builds = new List<ModBuild>();
        foreach (var m in changes.Install.Where(m => m.Source != Sdmm.Core.Modpacks.ModpackSource.Smr))
        {
            var candidates = (all.TryGetValue(m.ModReference, out var dm) ? dm.Builds : [])
                .Where(b => b.Version is not null && b.Assets.ContainsKey(info.Target) &&
                            (m.Source == Sdmm.Core.Modpacks.ModpackSource.Imported ? b.IsImported : !b.IsImported && b.RepoFullName.Equals(m.Repo, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ModBuild? build = !useNewest && m.Version is { } wanted && Sdmm.Core.Versioning.SemVersion.TryParse(wanted, out var wv)
                ? candidates.FirstOrDefault(b => b.Version == wv)
                : candidates.Where(b => m.Constraint is null || (Sdmm.Core.Versioning.VersionRange.TryParse(m.Constraint, out var r, out _) && r!.Matches(b.Version!)))
                    .OrderByDescending(b => b.Version).FirstOrDefault();
            if (build is null)
                problems.Add($"{m.ModReference} {m.Version ?? m.Constraint ?? ""}: no {info.Target} build " +
                             (m.Source == Sdmm.Core.Modpacks.ModpackSource.Imported ? "among your imported zips." : $"in {m.Repo}'s releases (check that it's in your sources)."));
            else builds.Add(build);
        }

        // SMR mods: resolved with their dependencies (when SMR installs are available).
        var smrMods = changes.Install.Where(m => m.Source == Sdmm.Core.Modpacks.ModpackSource.Smr).ToList();
        using var smr = CreateSmr();
        Sdmm.Core.Smr.SmrInstallPlan? smrPlan = null;
        if (!variant)
            skipped.AddRange(smrMods.Select(m => $"{m.ModReference} (comes from SMR; install it with SMM)"));
        else if (smrMods.Count > 0)
        {
            Status = "Resolving dependencies on SMR…";
            var requests = smrMods.Select(m => new Sdmm.Core.Smr.SmrRequest(m.ModReference,
                !useNewest && m.Version is { } v && Sdmm.Core.Versioning.SemVersion.TryParse(v, out var sv) ? sv : null,
                m.Constraint is { } c && Sdmm.Core.Versioning.VersionRange.TryParse(c, out var range, out _) ? range : null)).ToList();
            smrPlan = await Sdmm.Core.Smr.SmrResolver.ResolveAsync(requests, info.Target, info.Changelist, installed, async refs => await smr.GetVersionsAsync(refs));
            problems.AddRange(smrPlan.Problems);
        }
        if (problems.Count > 0)
        {
            _dialogs.Error("Can't apply the modpack", string.Join("\n\n", problems.Select(p => "• " + p)));
            return;
        }
        var smrInstall = smrPlan?.ToInstall ?? [];
        // A modpack's own dependencies may already be in the pack as GitHub builds; don't fetch those from SMR too.
        smrInstall = smrInstall.Where(v => !builds.Any(b => b.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase))).ToList();
        var takeOver = (smrPlan?.TakeOver ?? []).Where(r => smrInstall.Any(v => v.ModReference.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();
        var remove = changes.Remove.Where(r => !smrInstall.Any(v => v.ModReference.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();

        if (builds.Count + smrInstall.Count + remove.Count == 0)
        {
            _dialogs.Info("Apply modpack", $"'{item.Name}' already matches '{pack.Name}'." +
                          (skipped.Count > 0 ? $"\n\nSkipped: {string.Join(", ", skipped)}" : ""));
            return;
        }

        // One confirmation for everything.
        _smm = SmmState.Load(_config.SmmDirectory);
        var profiles = takeOver.SelectMany(r => SmmProfileEditor.ProfilesListing(_smm, r)).Distinct().ToList();
        if (profiles.Count > 0 && SmmProfileEditor.FindRunningSmm() is { Count: > 0 } running)
        {
            _dialogs.Error("Close SMM first", $"Applying this modpack replaces mods SMM installed, which also removes them from your SMM profiles, and SMM is running ({string.Join(", ", running)}). Close SMM, then try again. Nothing was changed.");
            return;
        }
        string Current(string modRef) => installed.FirstOrDefault(i => i.ModReference.Equals(modRef, StringComparison.OrdinalIgnoreCase))?.Descriptor?.EffectiveVersion is { } v ? $" (replaces {v})" : "";
        var text = new System.Text.StringBuilder($"Apply '{pack.Name}' to '{item.Name}' ({info.Target})?\n");
        foreach (var b in builds) text.Append($"\n• {b.ModReference} {b.Tag} from {(b.IsImported ? "an imported zip" : b.RepoFullName)}{Current(b.ModReference)}");
        foreach (var v in smrInstall) text.Append($"\n• {v.ModReference} {v.Version} from SMR{Current(v.ModReference)}");
        if (remove.Count > 0) text.Append($"\n\nRemove (not in the modpack): {string.Join(", ", remove)}");
        if (DependentsWarning(installed, remove) is { } depWarning) text.Append("\n\n" + depWarning);
        if (skipped.Count > 0) text.Append($"\n\nSkipped: {string.Join(", ", skipped)}");
        if (changes.KeptForeign.Count > 0) text.Append($"\n\nLeft alone (not in the modpack, but installed by SMM, the host or by hand): {string.Join(", ", changes.KeptForeign)}");
        if (takeOver.Count > 0) text.Append($"\n\nTaken over first: {string.Join(", ", takeOver)}" + (profiles.Count > 0 ? $" (also removed from SMM profiles {string.Join(", ", profiles)})" : ""));
        if (DiskSpaceWarning(item, smrInstall.Sum(v => v.Targets[info.Target].Size ?? 0) + builds.Sum(b => b.Assets[info.Target].Size)) is { } space)
            text.Append("\n\n" + space);
        if (item.IsServer) text.Append("\n\n" + ServerNote(item));
        if (!_dialogs.Confirm("Apply modpack", text.ToString())) return;

        if (takeOver.Count > 0)
        {
            var adopted = await AdoptAsync(item, takeOver);
            if (adopted.Count < takeOver.Count) return;
            ReportStillHeldBySmm(await RemoveFromSmmProfilesAsync(item, adopted, profiles));
        }

        using var opened = await Task.Run(() => Open(item));
        if (!await Task.Run(() => EnsureServerWritable(item, opened.Install))) return;
        var cache = CreateCache();
        var installRequests = await DownloadBuildsAsync(builds, info.Target, cache);
        foreach (var v in smrInstall)
        {
            Status = $"Downloading {v.ModReference} {v.Version}…";
            var pkg = await cache.GetSmrAsync(smr, v, info.Target);
            installRequests.Add(new InstallRequest(pkg, new PackageSource(SmrSourcePrefix + v.ModReference, "v" + v.Version, Path.GetFileName(pkg.ZipPath))));
        }
        _smm = SmmState.Load(_config.SmmDirectory);
        if (installRequests.Count > 0 && !await InstallRequestsAsync(item, opened, installRequests, cache, takeOver.Count == 0 ? installed : null)) return;
        if (remove.Count > 0)
            await Task.Run(() =>
            {
                var installer = new Installer(Log);
                foreach (var r in remove) { installer.Uninstall(opened.Install, r); MarkChanged([r]); }
            });
        Log($"Applied modpack '{pack.Name}' to {item.Name}: {installRequests.Count} installed or changed, {remove.Count} removed, {skipped.Count} skipped.");
        await RefreshAfterChangeAsync(item);
        await LoadSmrAsync(refreshAll: false);
    }

    /// <summary>Downloads (or reuses from the cache) GitHub builds and imported zips for a platform.</summary>
    private async Task<List<InstallRequest>> DownloadBuildsAsync(IReadOnlyList<ModBuild> builds, ModTarget target, PackageCache cache)
    {
        var requests = new List<InstallRequest>();
        if (builds.Count == 0) return requests;
        using var gh = CreateGitHub();
        foreach (var b in builds)
        {
            var asset = b.Assets[target];
            if (b.LocalZips?.TryGetValue(target, out var localZip) == true)
            {
                requests.Add(new InstallRequest(await Task.Run(() => ModPackage.Open(localZip)), new PackageSource(null, b.Tag, asset.Name)));
                continue;
            }
            Status = $"Downloading {b.ModReference} {b.Tag}…";
            Log($"{b.ModReference}: getting {asset.Name} ({asset.Size / 1048576.0:0.0} MB) from {b.RepoFullName} {b.Tag}");
            requests.Add(new InstallRequest(await cache.GetAsync(gh, b, target), new PackageSource(b.RepoFullName, b.Tag, asset.Name)));
        }
        return requests;
    }

    /// <summary>
    /// A warning when a local install's drive (or the download cache's) has less room than the downloads need, with
    /// margin for extracting them. Null when there's room, or for remote installs (their space can't be checked).
    /// </summary>
    private string? DiskSpaceWarning(InstallItem item, long downloadBytes)
    {
        long need = downloadBytes * 3 + 200L * 1024 * 1024; // the zip, its extracted copy while staging, and slack
        var low = new List<string>();
        foreach (var path in new[] { item.IsRemote ? null : item.Config.Path, CachePath }.OfType<string>())
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                if (drive.IsReady && drive.AvailableFreeSpace < need)
                    low.Add($"{drive.Name} has {drive.AvailableFreeSpace / 1048576.0:0} MB free");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return low.Count == 0 ? null : $"Low disk space: {string.Join(", ", low.Distinct())}; this needs about {need / 1048576.0:0} MB.";
    }

    // ---- installing from SMR ----------------------------------------------------------------------------------
    //
    // Search SMR, install a mod with its dependencies into
    // the selected game or server, and update installed mods. SMR's terms allow downloading for personal use; SDMM
    // only downloads on an explicit install, keeps the zips in its local cache and never shares them.

    /// <summary>Marker SourceRepo prefix for mods installed from SMR ("ficsit.app/AutoTrains").</summary>
    public const string SmrSourcePrefix = ServerSync.SmrSourcePrefix;

    /// <summary>Opens the SMR browser window (implemented by the main window).</summary>
    public event Action? BrowseSmrRequested;

    private bool CanBrowseSmr() => Brand.Current.IsVariant;

    [RelayCommand(CanExecute = nameof(CanBrowseSmr))]
    private void BrowseSmr() => BrowseSmrRequested?.Invoke();

    /// <summary>A new SMR client; the caller disposes it.</summary>
    public Sdmm.Core.Smr.SmrClient CreateSmr() => new();

    /// <summary>The selected install's platform and game build, for showing which SMR versions fit.</summary>
    public async Task<(ModTarget Target, long Changelist)?> GetSelectedTargetAsync()
    {
        var item = SelectedInstall;
        if (item is null) return null;
        try
        {
            var info = await Task.Run(() => { using var o = Open(item); return o.Install.Detect(); });
            return (info.Target, info.Changelist);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Mod references installed in the selected install, with versions (for the SMR browser's "Installed" tags).</summary>
    public IReadOnlyDictionary<string, string> InstalledVersions =>
        Mods.Where(r => r.Installed?.Descriptor is not null)
            .GroupBy(r => r.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Installed!.Descriptor!.EffectiveVersion.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Installs SMR mods (and what they need) into the selected install. Used by the SMR browser and details panel.</summary>
    public Task InstallFromSmrAsync(IReadOnlyList<Sdmm.Core.Smr.SmrRequest> requests) =>
        RunBusy(requests.Count == 1 ? $"Installing {requests[0].ModReference} from SMR…" : "Installing from SMR…", () => InstallFromSmrCoreAsync(requests));

    private bool CanInstallFromSmr(ModRow? row) => !IsBusy && Brand.Current.IsVariant && row?.Smr is not null && SelectedInstall is not null;

    /// <summary>Details panel: install or update this mod from SMR to the newest version that fits.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallFromSmr))]
    private Task InstallRowFromSmrAsync(ModRow? row) => InstallFromSmrAsync([new Sdmm.Core.Smr.SmrRequest(row!.ModReference)]);

    /// <summary>Dev builds with a newer build in your sources (GitHub releases or imported zips).</summary>
    private List<ModRow> DevUpdates() => Mods.Where(r => r is { State: ModState.UpdateAvailable, IsFromSmr: false } && r.Latest is not null).ToList();

    /// <summary>Mods SMR has a newer release of (when SMR installs are available).</summary>
    private List<ModRow> SmrUpdates() => Brand.Current.IsVariant ? Mods.Where(r => r.SmrUpdateVersion is not null).ToList() : [];

    /// <summary>How many listed mods have an update; the Update all button shows it and is only clickable above 0.</summary>
    public int UpdateCount => DevUpdates().Select(r => r.ModReference).Union(SmrUpdates().Select(r => r.ModReference), StringComparer.OrdinalIgnoreCase).Count();

    public string UpdateAllText => UpdateCount == 0 ? "Up to date" : $"Update all ({UpdateCount})";

    private void RefreshUpdateCount()
    {
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(UpdateAllText));
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanUpdateAll() => !IsBusy && SelectedInstall is not null && UpdateCount > 0;

    /// <summary>
    /// Updates every listed mod that has one: dev builds to the newest build in your sources, and (when SMR installs
    /// are available) mods to SMR's newest release that fits. Each part shows what it will change before doing it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private Task UpdateAllAsync() => RunBusy("Updating mods…", async () =>
    {
        EnsureRowsMatchSelection();
        var item = SelectedInstall!;
        var dev = DevUpdates();
        var smrRefs = SmrUpdates().Select(r => r.ModReference).Where(r => dev.All(d => !d.ModReference.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();
        if (dev.Count > 0)
        {
            var list = string.Join("\n", dev.Select(r => $"• {r.ModReference}: {r.Installed?.Descriptor?.EffectiveVersion} → {r.Latest!.Build.Tag}"));
            if (_dialogs.Confirm("Update all", $"Update these dev builds in '{item.Name}'?\n\n{list}") && await InstallBuildsAsync(item, dev.Select(r => r.Latest!.Build).ToList()))
                await RefreshAfterChangeAsync(item);
        }
        if (smrRefs.Count > 0)
            await InstallFromSmrCoreAsync(smrRefs.Select(r => new Sdmm.Core.Smr.SmrRequest(r)).ToList());
    });

    private async Task InstallFromSmrCoreAsync(IReadOnlyList<Sdmm.Core.Smr.SmrRequest> requests, InstallItem? target = null, bool askFirst = true)
    {
        var item = target ?? SelectedInstall ?? throw new InvalidOperationException("Pick a game or server first.");
        if (requests.Count == 0) return;
        using var smr = CreateSmr();

        // Read the install and work out what to install.
        var (info, installed) = await Task.Run(() =>
        {
            using var o = Open(item);
            return (o.Install.Detect(), ModScanner.Scan(o.Install.FileSystem));
        });
        Status = "Resolving dependencies on SMR…";
        var plan = await Sdmm.Core.Smr.SmrResolver.ResolveAsync(requests, info.Target, info.Changelist, installed,
            async refs => await smr.GetVersionsAsync(refs));
        if (plan.Problems.Count > 0)
        {
            _dialogs.Error("Can't install from SMR", string.Join("\n\n", plan.Problems.Select(p => "• " + p)));
            Log("SMR install refused: " + string.Join(" | ", plan.Problems));
            return;
        }
        if (plan.ToInstall.Count == 0)
        {
            _dialogs.Info("Install from SMR", "Everything is already up to date.");
            return;
        }

        // Confirm, including any takeovers (and the SMM profile changes they bring).
        _smm = SmmState.Load(_config.SmmDirectory);
        var profiles = plan.TakeOver.SelectMany(r => SmmProfileEditor.ProfilesListing(_smm, r)).Distinct().ToList();
        if (profiles.Count > 0 && SmmProfileEditor.FindRunningSmm() is { Count: > 0 } running)
        {
            _dialogs.Error("Close SMM first",
                $"This replaces mods SMM installed, which also removes them from your SMM profiles ({string.Join(", ", profiles)}), and SMM is running ({string.Join(", ", running)}). " +
                "SMM keeps its profiles in memory and would undo the change.\n\nClose SMM, then try again. Nothing was changed.");
            return;
        }
        long bytes = plan.ToInstall.Sum(v => v.Targets[info.Target].Size ?? 0);
        var text = new System.Text.StringBuilder($"Install into '{item.Name}' from SMR ({bytes / 1048576.0:0.0} MB):\n");
        foreach (var v in plan.ToInstall)
        {
            var current = installed.FirstOrDefault(m => m.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase))?.Descriptor?.EffectiveVersion;
            bool asked = requests.Any(r => r.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase));
            text.Append($"\n• {v.ModReference} {v.Version}{(v.IsRelease ? "" : $" ({v.Stability})")}" +
                        (current is null ? "" : $" (replaces {current})") + (asked ? "" : " — needed by another mod"));
        }
        if (plan.TakeOver.Count > 0)
            text.Append($"\n\n{string.Join(", ", plan.TakeOver)} {(plan.TakeOver.Count == 1 ? "is" : "are")} currently managed by SMM or installed by hand; " +
                        "SDMM takes " + (plan.TakeOver.Count == 1 ? "it" : "them") + " over first" +
                        (profiles.Count > 0 ? $" and removes {(plan.TakeOver.Count == 1 ? "it" : "them")} from your SMM profiles ({string.Join(", ", profiles)}; profiles.json is backed up)." : "."));
        if (item.IsServer) text.Append("\n\n" + ServerNote(item));
        if (DiskSpaceWarning(item, bytes) is { } space) text.Append("\n\n" + space);
        // A caller that already confirmed (server sync) still gets asked when dependencies or takeovers were added.
        bool extra = plan.ToInstall.Count > requests.Count || plan.TakeOver.Count > 0;
        if ((askFirst || extra) && !_dialogs.Confirm("Install from SMR", text.ToString())) return;

        if (plan.TakeOver.Count > 0)
        {
            var adopted = await AdoptAsync(item, plan.TakeOver);
            if (adopted.Count < plan.TakeOver.Count) return; // AdoptAsync explained why
            ReportStillHeldBySmm(await RemoveFromSmmProfilesAsync(item, adopted, profiles));
        }

        // Download (verified against SMR's hashes), then install with the usual checks.
        using var opened = await Task.Run(() => Open(item));
        if (!await Task.Run(() => EnsureServerWritable(item, opened.Install))) return;
        var cache = CreateCache();
        var installRequests = new List<InstallRequest>();
        foreach (var v in plan.ToInstall)
        {
            var t = v.Targets[info.Target];
            Status = $"Downloading {v.ModReference} {v.Version}…";
            Log($"{v.ModReference}: getting {v.Version} ({(t.Size ?? 0) / 1048576.0:0.0} MB) from SMR");
            var pkg = await cache.GetSmrAsync(smr, v, info.Target);
            installRequests.Add(new InstallRequest(pkg, new PackageSource(SmrSourcePrefix + v.ModReference, "v" + v.Version, Path.GetFileName(pkg.ZipPath))));
        }
        _smm = SmmState.Load(_config.SmmDirectory);
        if (await InstallRequestsAsync(item, opened, installRequests, cache, plan.TakeOver.Count == 0 ? installed : null))
            Log($"Installed from SMR into {item.Name}: {string.Join(", ", plan.ToInstall.Select(v => $"{v.ModReference} {v.Version}"))}.");
        await RefreshAfterChangeAsync(item);
        await LoadSmrAsync(refreshAll: false);
    }

    // ---- takeover -----------------------------------------------------------------------------------------------
    //
    // Takes mods installed by SMM, a host's tool or by hand under SDMM's management. Every use is confirmed with its
    // consequences spelled out.

    private bool CanTakeOver(ModRow? row) => !IsBusy && Brand.Current.IsVariant && row?.CanTakeOver == true;

    [RelayCommand(CanExecute = nameof(CanTakeOver))]
    private Task TakeOverAsync(ModRow? row) => RunBusy($"Taking over {row?.ModReference}…", () => TakeOverRowsAsync([row!]));

    private bool CanTakeOverAll() => !IsBusy && Brand.Current.IsVariant && Mods.Any(r => r.CanTakeOver);

    [RelayCommand(CanExecute = nameof(CanTakeOverAll))]
    private Task TakeOverAllAsync() => RunBusy("Taking over mods…", async () =>
    {
        var all = Mods.Where(r => r.CanTakeOver).ToList();
        var mine = all.Where(r => r.IsInYourSources).ToList();
        // Only a real choice is asked: when every candidate is in your sources (or none is), both answers are the same.
        if (mine.Count > 0 && mine.Count < all.Count)
        {
            var others = all.Except(mine).Select(r => r.ModReference).ToList();
            var choice = _dialogs.Choose("Take over all",
                $"{all.Count} mods here are managed by SMM, the host or a hand copy. Which should SDMM take over?",
                [
                    ($"Only mods in your sources ({mine.Count})",
                     "The mods you also get from your GitHub sources or imported zips: " + string.Join(", ", mine.Select(r => r.ModReference)) + ". Everything else stays with its manager."),
                    ($"Every mod here ({all.Count})",
                     "Also the mods only another manager installed: " + string.Join(", ", others) + ". SDMM then installs and updates them from SMR."),
                ]);
            if (choice is null) return;
            if (choice == 0) all = mine;
        }
        await TakeOverRowsAsync(all);
    });

    /// <summary>
    /// Takes the mods over, then removes them from every SMM profile that lists them, so SMM doesn't put them back.
    /// In that order: a mod dropped from a profile while it still carries SMM's marker would be deleted by SMM's next
    /// apply. Editing profiles needs SMM closed, which is checked before anything changes.
    /// </summary>
    private async Task TakeOverRowsAsync(IReadOnlyList<ModRow> rows)
    {
        EnsureRowsMatchSelection();
        if (rows.Count == 0) return;
        var item = SelectedInstall!;
        _smm = SmmState.Load(_config.SmmDirectory);
        var refs = rows.Select(r => r.ModReference).ToList();
        var profiles = refs.SelectMany(r => SmmProfileEditor.ProfilesListing(_smm, r)).Distinct().ToList();
        if (profiles.Count > 0 && SmmProfileEditor.FindRunningSmm() is { Count: > 0 } running)
        {
            _dialogs.Error("Close SMM first",
                $"Taking over also removes the mods from your SMM profiles ({string.Join(", ", profiles)}), and SMM is running ({string.Join(", ", running)}). " +
                "SMM keeps its profiles in memory and would undo the change.\n\nClose SMM, then take over again. Nothing was changed.");
            return;
        }
        var title = rows.Count == 1 ? "Take over " + rows[0].ModReference : $"Take over {rows.Count} mods";
        if (!_dialogs.Confirm(title, TakeOverWarning(item, rows, profiles, PredictHeldBySmm(item, refs)))) return;

        var adopted = await AdoptAsync(item, refs);
        // Always checked, even with no profile listing them: a mod can be in SMM's hands purely as a dependency.
        var held = adopted.Count > 0 ? await RemoveFromSmmProfilesAsync(item, adopted, profiles) : [];
        await RefreshAfterChangeAsync(item);
        ReportStillHeldBySmm(held);
    }

    private bool CanRemoveFromSmmProfiles(ModRow? row) =>
        !IsBusy && Brand.Current.IsVariant && row?.Problems.Any(p => p.Code == "SMM_WILL_REPLACE") == true;

    /// <summary>For a mod SDMM already manages but an SMM profile still lists: take it out of those profiles.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFromSmmProfiles))]
    private Task RemoveFromSmmProfilesAsync(ModRow? row) => RunBusy($"Removing {row?.ModReference} from SMM profiles…", async () =>
    {
        _smm = SmmState.Load(_config.SmmDirectory);
        var profiles = SmmProfileEditor.ProfilesListing(_smm, row!.ModReference);
        if (profiles.Count == 0)
        {
            // Only the lockfile has it: it's a dependency of something, or a leftover from before.
            var lockOnly = await RemoveFromSmmProfilesAsync(SelectedInstall!, [row.ModReference], []);
            await BuildRowsAsync(reuseScan: true); // only SMM's files changed; SMM-replace warnings are re-checked below
            ReportStillHeldBySmm(lockOnly);
            return;
        }
        if (!_dialogs.Confirm("Remove from SMM profiles",
                $"Remove {row.ModReference} from these SMM profiles: {string.Join(", ", profiles)}?\n\n" +
                "SMM then stops putting its version back over the dev build. profiles.json is backed up first. SMM must be closed."))
            return;
        var held = await RemoveFromSmmProfilesAsync(SelectedInstall!, [row.ModReference], profiles);
        MarkChanged([row.ModReference]); // its "SMM will replace it" warning depends on the profiles just edited
        await RefreshAfterChangeAsync();
        ReportStillHeldBySmm(held);
    });

    /// <summary>
    /// Removes the mods from SMM's profiles, then checks this install: a mod that another mod in one of its SMM profiles
    /// still requires comes back with that mod, and is reported. For the rest, the leftover entries in the profiles'
    /// lockfiles here are cleared, since SMM rebuilds those on its next apply anyway.
    /// Returns the mods still held by SMM, with the mods that need them.
    /// </summary>
    private async Task<IReadOnlyList<(string Mod, IReadOnlyList<string> NeededBy)>> RemoveFromSmmProfilesAsync(
        InstallItem item, IReadOnlyList<string> modRefs, IReadOnlyList<string> profiles)
    {
        if (profiles.Count > 0)
        {
            try
            {
                var edit = SmmProfileEditor.RemoveMods(_smm.Directory, modRefs);
                if (edit.Profiles.Count > 0)
                    Log($"Removed {string.Join(", ", modRefs)} from SMM profiles {string.Join(", ", edit.Profiles)} (backup: {edit.BackupPath}).");
                if (edit.Removed is { Count: > 0 } removedEntries)
                    await Task.Run(() => RecordRemovedProfiles(item, removedEntries));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                Log("SMM profiles not changed: " + ex.Message);
                _dialogs.Error("SMM profiles not changed",
                    $"{ex.Message}\n\nThe mods are managed by SDMM now, but SMM may put its version back. Use Remove from SMM profiles on the mod once that's fixed.");
                _smm = SmmState.Load(_config.SmmDirectory);
                return [];
            }
            _smm = SmmState.Load(_config.SmmDirectory);
        }
        if (item.IsRemote) return []; // SMM's link between installs and profiles is only read for local installs

        var smm = _smm;
        return await Task.Run(() =>
        {
            using var opened = Open(item);
            var fs = opened.Install.FileSystem;
            var installed = ModScanner.Scan(fs);
            var attached = smm.FindInstallations(item.Config.Path).Select(i => i.Profile).Distinct().ToList();
            var held = new List<(string, IReadOnlyList<string>)>();
            var free = new List<string>();
            foreach (var r in modRefs)
            {
                var neededBy = SmmProfileEditor.RequiredBy(smm, attached, installed, r);
                if (neededBy.Count > 0) held.Add((r, neededBy));
                else free.Add(r);
            }
            foreach (var path in SmmProfileEditor.RemoveFromLockfiles(fs, attached, free))
                Log($"Cleared {string.Join(", ", free)} from SMM's lockfile {path} (SMM rebuilds it on its next apply).");
            return (IReadOnlyList<(string, IReadOnlyList<string>)>)held;
        });
    }

    /// <summary>Saves the SMM profile entries a takeover removed into each mod's SDMM marker, for a later hand-back.</summary>
    private void RecordRemovedProfiles(InstallItem item, IReadOnlyDictionary<string, IReadOnlyList<SmmProfileEntry>> removed)
    {
        try
        {
            using var o = Open(item);
            var fs = o.Install.FileSystem;
            foreach (var m in removed.Keys.SelectMany(r => ModScanner.ScanMod(fs, r)).Where(m => m.Owner == ModOwner.Sdmm && m.Marker is not null))
            {
                var marker = m.Marker!;
                var entries = (marker.PreviousSmmProfiles ?? []).Concat(removed[m.ModReference])
                    .GroupBy(e => e.Profile).Select(g => g.Last()).ToList();
                marker.PreviousSmmProfiles = entries;
                fs.WriteAllText(InstallPaths.Combine(m.Folder, SdmmMarker.FileName), marker.ToJson());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Not fatal: a hand-back then uses SMM's default entry instead of the exact one.
            Log("Couldn't record the removed SMM profile entries: " + ex.Message);
        }
    }

    // ---- hand back ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the row can be handed back here: to the host or a hand copy anywhere (it only removes SDMM's marker), to
    /// SMM only on a game on this PC (SMM's settings for remote servers aren't read).
    /// </summary>
    private bool HandBackPossible(ModRow r) =>
        r.HandBackTo == HandBackKind.Release || (r.HandBackTo == HandBackKind.Smm && SelectedInstall is { IsRemote: false });

    private bool CanHandBack(ModRow? row) => !IsBusy && Brand.Current.IsVariant && row is not null && HandBackPossible(row);

    /// <summary>Gives a mod back to whoever managed it before (the reverse of a takeover). See <see cref="HandBackRowsAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanHandBack))]
    private Task HandBackAsync(ModRow? row) => RunBusy($"Handing {row?.ModReference} back…", () => HandBackRowsAsync([row!]));

    private bool CanHandBackAll() => !IsBusy && Brand.Current.IsVariant && Mods.Any(HandBackPossible);

    [RelayCommand(CanExecute = nameof(CanHandBackAll))]
    private Task HandBackAllAsync() => RunBusy("Handing mods back…", () => HandBackRowsAsync(Mods.Where(HandBackPossible).ToList()));

    /// <summary>
    /// Hands mods back: the ones taken over from the host or a hand copy by removing SDMM's marker, then the rest to SMM.
    /// </summary>
    private async Task HandBackRowsAsync(IReadOnlyList<ModRow> rows)
    {
        EnsureRowsMatchSelection();
        var item = SelectedInstall!;
        var release = rows.Where(r => r.HandBackTo == HandBackKind.Release).ToList();
        var toSmm = rows.Where(r => r.HandBackTo == HandBackKind.Smm).ToList();
        if (release.Count > 0) await ReleaseRowsAsync(item, release, moreToSmm: toSmm.Count);
        if (toSmm.Count > 0) await HandBackToSmmAsync(toSmm);
    }

    /// <summary>
    /// Gives mods taken over from a host's tool or a hand copy back by removing SDMM's marker; the folder is left as it
    /// was before the takeover. A host's own mod tool sees it as it did then (if the host keeps its own records, SDMM
    /// never changed them).
    /// </summary>
    private async Task ReleaseRowsAsync(InstallItem item, IReadOnlyList<ModRow> rows, int moreToSmm)
    {
        var names = string.Join(", ", rows.Select(r => r.ModReference));
        var updated = rows.Where(r => r.Installed?.Marker is { } m && m.Sha256 != SdmmMarker.AdoptedSha256).Select(r => r.ModReference).ToList();
        var text = new System.Text.StringBuilder(
            $"Hand {names} back to {(item.IsServer ? "the host" : "being hand-installed")}?\n\n" +
            "SDMM stops managing " + (rows.Count == 1 ? "it" : "them") + ": its marker is removed and the files stay as they are, so " +
            (item.IsServer ? "your host's panel (or whoever installed " + (rows.Count == 1 ? "it" : "them") + ") is in charge again." : "it's a hand-installed mod again."));
        if (updated.Count > 0)
            text.Append($"\n\nSDMM updated {string.Join(", ", updated)} since taking {(updated.Count == 1 ? "it" : "them")} over, so that's the version left behind, not the one the host installed.");
        if (moreToSmm > 0) text.Append($"\n\n{moreToSmm} other mod{(moreToSmm == 1 ? " goes" : "s go")} back to SMM next; that's asked separately.");
        if (!_dialogs.Confirm(item.IsServer ? "Hand back to the host" : "Stop managing", text.ToString())) return;

        var failed = new List<string>();
        await Task.Run(() =>
        {
            using var o = Open(item);
            if (!EnsureServerWritable(item, o.Install)) { failed.Add("• The Mods folder can't be changed right now."); return; }
            var installer = new Installer(Log);
            foreach (var r in rows)
            {
                try
                {
                    installer.Release(o.Install, r.ModReference);
                    MarkChanged([r.ModReference]);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    failed.Add($"• {r.ModReference}: {ex.Message}");
                }
            }
        });
        await RefreshAfterChangeAsync(item);
        if (failed.Count > 0) _dialogs.Error("Some mods weren't handed back", string.Join("\n", failed));
    }

    /// <summary>
    /// Hands mods back to SMM: first adds them to SMM's profiles (the exact entries a takeover removed, else SMM's own
    /// default for this game's profile), then swaps SDMM's folder marker for SMM's. Profiles first, because SMM deletes
    /// .smm-marked folders its profile doesn't list. Local games SMM manages only; SMM must be closed.
    /// </summary>
    private async Task HandBackToSmmAsync(IReadOnlyList<ModRow> rows)
    {
        var item = SelectedInstall!;
        if (rows.Count == 0) return;
        _smm = SmmState.Load(_config.SmmDirectory);
        var attached = _smm.FindInstallations(item.Config.Path).Where(i => !i.Vanilla).Select(i => i.Profile).Distinct().ToList();
        if (attached.Count == 0)
        {
            _dialogs.Error("Hand back to SMM",
                $"SMM doesn't manage '{item.Name}' (it isn't in SMM's list of installs, or it's set to vanilla). Add it in SMM and pick a profile for it, then hand the mods back.");
            return;
        }
        if (SmmProfileEditor.FindRunningSmm() is { Count: > 0 } running)
        {
            _dialogs.Error("Close SMM first", $"Handing mods back edits SMM's profiles, and SMM is running ({string.Join(", ", running)}). Close SMM, then try again. Nothing was changed.");
            return;
        }

        var (info, installed) = await Task.Run(() => { using var o = Open(item); return (o.Install.Detect(), ModScanner.Scan(o.Install.FileSystem)); });
        var refs = rows.Select(r => r.ModReference).ToList();
        var entries = new Dictionary<string, IReadOnlyList<SmmProfileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var recorded = r.Installed?.Marker?.PreviousSmmProfiles;
            entries[r.ModReference] = recorded is { Count: > 0 }
                ? recorded
                : attached.Select(p => new SmmProfileEntry { Profile = p, Version = SmmProfileEditor.DefaultConstraint, Enabled = true }).ToList();
        }

        // Update to SMM's newest first. The takeover took these out of SMM's lockfile, so SMM's next apply picks the
        // newest SMR release the profile allows and replaces anything else; doing it here means SMM finds it already
        // installed (same hash) and the game is up to date right away.
        var updates = await PlanHandBackUpdatesAsync(rows, entries, info, installed);
        var updateRefs = updates.Updates.Select(v => v.ModReference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devBuilds = rows.Where(r => r.Installed?.Marker is { } m && !r.IsFromSmr && !m.IsAdoptedCopy && !updateRefs.Contains(r.ModReference))
            .Select(r => r.ModReference).ToList();
        // SMM installs a mod's dependencies with it, replacing SDMM's copies of them.
        var deps = installed.Where(m => refs.Contains(m.ModReference, StringComparer.OrdinalIgnoreCase) && m.Descriptor is not null)
            .SelectMany(m => m.Descriptor!.Plugins.Where(d => !d.Optional && d.Enabled).Select(d => d.Name))
            .Where(d => !refs.Contains(d, StringComparer.OrdinalIgnoreCase) && installed.Any(i => i.ModReference.Equals(d, StringComparison.OrdinalIgnoreCase) && i.Owner == ModOwner.Sdmm))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var profilesUsed = entries.Values.SelectMany(e => e.Select(x => x.Profile)).Distinct().ToList();
        var text = new System.Text.StringBuilder($"Hand {(rows.Count == 1 ? rows[0].ModReference : $"{rows.Count} mods")} back to SMM?\n\n");
        text.Append($"SMM manages {(rows.Count == 1 ? "it" : "them")} from then on: {(rows.Count == 1 ? "it's" : "they're")} added to your SMM profile{(profilesUsed.Count == 1 ? "" : "s")} {string.Join(", ", profilesUsed)} " +
                    "(profiles.json is backed up first), and SDMM's folder marker is swapped for SMM's. The files aren't changed.");
        if (updates.Updates.Count > 0)
            text.Append("\n\nFirst, SDMM updates these to the newest SMR release your SMM profile allows (SMM would do the same on its next apply):\n" +
                        string.Join("\n", updates.Updates.Select(v =>
                        {
                            var current = installed.FirstOrDefault(m => m.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase));
                            bool dev = current?.Marker is { } m && !m.IsAdoptedCopy && m.SourceRepo?.StartsWith(SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) != true;
                            return $"• {v.ModReference}: {current?.Descriptor?.EffectiveVersion}{(dev ? " (dev build)" : "")} → {v.Version}";
                        })));
        foreach (var note in updates.Notes) text.Append("\n\n" + note);
        if (devBuilds.Count > 0)
            text.Append($"\n\nDev builds: {string.Join(", ", devBuilds)}. SMM replaces {(devBuilds.Count == 1 ? "it" : "them")} with the published SMR version the next time it applies the profile.");
        if (deps.Count > 0)
            text.Append($"\n\nSMM also installs what {(rows.Count == 1 ? "it needs" : "they need")}, so it will replace SDMM's copies of {string.Join(", ", deps)} too. Hand those back as well to keep things tidy.");
        if (!_dialogs.Confirm("Hand back to SMM", text.ToString())) return;

        if (updates.Updates.Count > 0)
        {
            Status = "Updating from SMR before handing back…";
            await InstallFromSmrCoreAsync(updates.Updates.Select(v => new Sdmm.Core.Smr.SmrRequest(v.ModReference, v.Version)).ToList(), item, askFirst: false);
            Status = "Handing mods back to SMM…";
        }

        var edit = SmmProfileEditor.AddMods(_smm.Directory, entries);
        if (edit.Profiles.Count > 0) Log($"Added {string.Join(", ", refs)} to SMM profiles {string.Join(", ", edit.Profiles)} (backup: {edit.BackupPath}).");
        var failed = new List<string>();
        await Task.Run(() =>
        {
            using var o = Open(item);
            var installer = new Installer(Log);
            foreach (var r in refs)
            {
                try
                {
                    installer.HandBack(o.Install, r);
                    MarkChanged([r]);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    failed.Add($"• {r}: {ex.Message}");
                }
            }
        });
        _smm = SmmState.Load(_config.SmmDirectory);
        await RefreshAfterChangeAsync(item);
        if (failed.Count > 0)
            _dialogs.Error("Some mods weren't handed back",
                string.Join("\n", failed) + "\n\nThey're still in your SMM profile, so SMM installs its own copy on its next apply.");
        else
            _dialogs.Info("Handed back to SMM", "Done. Open SMM; it takes over on its next apply of the profile (it may update some mods to the newest version the profile allows).");
    }

    /// <param name="Updates">SMR versions to install before handing back (only the mods being handed back).</param>
    /// <param name="Notes">Why some weren't updated here (SMM still updates them on its next apply).</param>
    private sealed record HandBackUpdates(IReadOnlyList<Sdmm.Core.Smr.SmrVersion> Updates, IReadOnlyList<string> Notes);

    /// <summary>
    /// The newest SMR release for each mod being handed back that its SMM profile entries allow and that fits this game.
    /// A mod whose new version needs other mods installed or updated is left to SMM (it installs dependencies itself),
    /// so handing back never takes over or installs anything else.
    /// </summary>
    private async Task<HandBackUpdates> PlanHandBackUpdatesAsync(IReadOnlyList<ModRow> rows,
        IReadOnlyDictionary<string, IReadOnlyList<SmmProfileEntry>> entries, GameInfo info, IReadOnlyList<InstalledMod> installed)
    {
        var notes = new List<string>();
        var updates = new List<Sdmm.Core.Smr.SmrVersion>();
        try
        {
            using var smr = CreateSmr();
            Status = "Checking SMR for the newest versions…";
            foreach (var r in rows)
            {
                var requests = entries[r.ModReference]
                    .Select(e => Sdmm.Core.Versioning.VersionRange.TryParse(e.Version, out var range, out _) ? range : null)
                    .Where(range => range is not null)
                    .Select(range => new Sdmm.Core.Smr.SmrRequest(r.ModReference, null, range, "your SMM profile")).ToList();
                if (requests.Count == 0) requests.Add(new Sdmm.Core.Smr.SmrRequest(r.ModReference));
                var plan = await Sdmm.Core.Smr.SmrResolver.ResolveAsync(requests, info.Target, info.Changelist, installed,
                    async refs => await smr.GetVersionsAsync(refs));
                var pick = plan.ToInstall.FirstOrDefault(v => v.ModReference.Equals(r.ModReference, StringComparison.OrdinalIgnoreCase));
                if (plan.Problems.Count > 0 || pick is null)
                {
                    if (plan.Problems.Count > 0) notes.Add($"{r.ModReference} isn't updated first: {plan.Problems[0]}");
                    continue;
                }
                var current = installed.FirstOrDefault(m => m.ModReference.Equals(r.ModReference, StringComparison.OrdinalIgnoreCase));
                bool sameAsSmr = current?.Marker is { } m && m.SourceRepo?.StartsWith(SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) == true &&
                                 current.Descriptor?.EffectiveVersion == pick.Version;
                if (sameAsSmr) continue; // already SMM's newest, installed from SMR
                if (plan.ToInstall.Count > 1 || plan.TakeOver.Count > 0)
                {
                    notes.Add($"{r.ModReference} {pick.Version} also needs {string.Join(", ", plan.ToInstall.Where(v => v != pick).Select(v => $"{v.ModReference} {v.Version}"))}; SMM installs those and updates it on its next apply.");
                    continue;
                }
                updates.Add(pick);
            }
        }
        catch (Exception ex) when (ex is Sdmm.Core.Smr.SmrException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            notes.Add($"SMR couldn't be checked for newer versions ({ex.Message}); SMM updates them on its next apply.");
            updates.Clear();
        }
        return new HandBackUpdates(updates, notes);
    }

    /// <summary>A mod another mod in the profile depends on comes back with that mod; that can't be fixed here, so say so.</summary>
    private void ReportStillHeldBySmm(IReadOnlyList<(string Mod, IReadOnlyList<string> NeededBy)> held)
    {
        if (held.Count == 0) return;
        _dialogs.Info("SMM still needs some of these",
            string.Join("\n", held.Select(h => $"• {h.Mod} is required by {string.Join(", ", h.NeededBy)}, which your SMM profile still has.")) +
            "\n\nSMM installs a mod's dependencies with it, so it will put these back on its next apply. Remove the mods that need them from the profile in SMM, or keep both.");
    }

    /// <summary>Returns the mods that were taken over.</summary>
    private async Task<IReadOnlyList<string>> AdoptAsync(InstallItem item, IReadOnlyList<string> modRefs)
    {
        var failed = new List<string>();
        var done = new List<string>();
        await Task.Run(() =>
        {
            using var opened = Open(item);
            if (!EnsureServerWritable(item, opened.Install)) return;
            var installer = new Installer(Log);
            foreach (var r in modRefs)
            {
                try
                {
                    installer.Adopt(opened.Install, r);
                    done.Add(r);
                    MarkChanged([r]);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    failed.Add($"• {r}: {ex.Message}");
                }
            }
        });
        Log($"Takeover on {item.Name}: {done.Count} taken over, {failed.Count} failed.");
        if (failed.Count > 0) _dialogs.Error("Some mods weren't taken over", string.Join("\n", failed));
        return done;
    }

    /// <summary>
    /// Before a takeover: which of the mods another mod in this install's SMM profiles requires, so SMM would bring
    /// them back. Mods taken over together don't count, since they leave the profiles too. Local installs only.
    /// </summary>
    private IReadOnlyList<(string Mod, IReadOnlyList<string> NeededBy)> PredictHeldBySmm(InstallItem item, IReadOnlyList<string> refs)
    {
        if (item.IsRemote) return [];
        var installed = Mods.Select(r => r.Installed).OfType<InstalledMod>().ToList();
        var attached = _smm.FindInstallations(item.Config.Path).Select(i => i.Profile).Distinct().ToList();
        return refs.Select(r => (r, (IReadOnlyList<string>)SmmProfileEditor.RequiredBy(_smm, attached, installed, r)
                .Where(n => !refs.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList()))
            .Where(x => x.Item2.Count > 0).ToList();
    }

    private static string TakeOverWarning(InstallItem item, IReadOnlyList<ModRow> rows, IReadOnlyList<string> smmProfiles,
        IReadOnlyList<(string Mod, IReadOnlyList<string> NeededBy)> held)
    {
        bool anySmm = rows.Any(r => r.Installed?.Owner == ModOwner.Smm);
        bool anyOther = rows.Any(r => r.Installed?.Owner == ModOwner.Unmanaged);
        var list = string.Join(", ", rows.Take(12).Select(r => r.ModReference)) + (rows.Count > 12 ? $" and {rows.Count - 12} more" : "");
        var text = new System.Text.StringBuilder();
        text.Append($"Take over {list} on '{item.Name}'?\n\n");
        bool one = rows.Count == 1;
        text.Append($"SDMM will manage {(one ? "it" : "them")} from now on: it can update {(one ? "it to a dev build" : "them to dev builds")} or remove {(one ? "it" : "them")}. " +
                    "The mod files aren't changed; SDMM adds its own marker" + (anySmm ? " and removes SMM's (SDMM keeps a copy of it)." : ".") + "\n\n");
        text.Append("This skips the protection SDMM normally keeps around other tools' mods:\n");
        if (smmProfiles.Count > 0)
            text.Append($"\n• {(one ? "It" : "They")} will also be removed from these SMM profiles, so SMM stops putting its version back: {string.Join(", ", smmProfiles)}. " +
                        "That affects every install using those profiles. profiles.json is backed up first.");
        else if (anySmm && held.Count == 0)
            text.Append($"\n• SMM stops treating {(one ? "it" : "them")} as its own. No SMM profile lists {(one ? "it" : "them")}, so SMM won't put {(one ? "it" : "them")} back.");
        foreach (var (mod, neededBy) in held)
            text.Append($"\n• SMM will still put {mod} back: {string.Join(", ", neededBy)} in your SMM profile requires it. Take over {(neededBy.Count == 1 ? "that mod" : "those mods")} too, or remove {(neededBy.Count == 1 ? "it" : "them")} from the profile in SMM.");
        if (anyOther)
            text.Append(item.IsServer
                ? $"\n• If your host's mod tool still lists {(one ? "it" : "one")}, the host may replace or remove it again (for example when the server restarts). Remove it from the host's mod list first."
                : $"\n• Whatever copied {(one ? "it" : "them")} in may do so again; SDMM can't tell.");
        text.Append("\n\nContinue?");
        return text.ToString();
    }

    [RelayCommand]
    private void ClearCache()
    {
        if (!_dialogs.Confirm("Clear download cache",
                $"Delete all downloaded builds in\n{Path.Combine(CachePath, "packages")}?\n\nInstalled mods are not affected; builds are downloaded again when needed."))
            return;
        try
        {
            CreateCache().Clear();
            Log("Download cache cleared.");
            CacheCleared?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Clear download cache", ex.Message);
        }
        UpdateCacheSize();
    }

    /// <summary>
    /// The startup token prompt shows while no token is saved, until the user picks "Don't ask again".
    /// "Ask me later" (or closing the window) shows it again next launch.
    /// </summary>
    public bool ShouldShowWelcome => !_config.TokenPromptDismissed && SecretProtector.Unprotect(_config.ProtectedReadToken) is null;

    public void DismissTokenPrompt()
    {
        _config.TokenPromptDismissed = true;
        SaveConfig();
        Log("Token prompt turned off. Add a token any time under Settings → GitHub token.");
    }

    public void SetToken(string token)
    {
        token = token.Trim();
        _config.ProtectedReadToken = token.Length == 0 ? null : SecretProtector.Protect(token);
        SaveConfig();
        _sourcesChanged = true; // a token can reveal private repos and lifts the request limit, so check again
        UpdateTokenStatus();
        Log(token.Length == 0 ? "GitHub token removed." : "GitHub token saved (encrypted for your Windows account).");
    }

    /// <summary>True when a readable token is saved; shows "Remove saved token" only then.</summary>
    [ObservableProperty] private bool _hasToken;

    private void UpdateTokenStatus()
    {
        HasToken = SecretProtector.Unprotect(_config.ProtectedReadToken) is not null;
        TokenStatus = HasToken
            ? "Token saved: private repos it can read are included, 5,000 requests per hour."
            : "No token: public repos only, 60 GitHub requests per hour.";
    }
}
