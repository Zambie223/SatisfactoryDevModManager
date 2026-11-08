using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smr;

namespace Sdmm.App.ViewModels;

/// <summary>One mod in the SMR browser's results.</summary>
public sealed partial class SmrResultRow(SmrMod mod, string? installedVersion) : ObservableObject
{
    public SmrMod Mod { get; } = mod;
    public string ModReference => Mod.ModReference;
    public string Name => Mod.Name;
    public string Authors => string.Join(", ", Mod.Authors.Select(a => a.Username));
    public string ShortDescription => Mod.ShortDescription ?? "";
    public string Stats => $"{Mod.Downloads:N0} downloads" + (Mod.LatestRelease is { } r ? $"  ·  {r}" : Mod.LatestBeta is { } b ? $"  ·  {b} (beta)" : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(IsInstalled))]
    private string? _installedVersion = installedVersion;

    public bool IsInstalled => InstalledVersion is not null;
    public string InstalledText => InstalledVersion is { } v ? $"Installed {v}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    private System.Windows.Media.ImageSource? _logo;

    public bool HasLogo => Logo is not null;
    public string Initials => new ModRow(ModReference, null, null, []).Initials;
    public System.Windows.Media.Brush TileBrush => new ModRow(ModReference, null, null, []).TileBrush;
    public override string ToString() => ModReference;
}

/// <summary>One choice in the SMR browser's version picker.</summary>
/// <param name="Version">Null for "the newest version that fits".</param>
public sealed record SmrVersionOption(SmrVersion? Version, string Display, bool Fits);

/// <summary>
/// The SMR browser: search SMR's catalog, see which versions fit the selected game or server, and
/// install a mod with its dependencies. Searches are made when the user types (after a pause) or changes the sort,
/// one page at a time, in keeping with SMR's fair-use policy.
/// </summary>
public sealed partial class SmrBrowserViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 30;
    private readonly MainViewModel _main;
    private readonly SmrClient _smr;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;
    private (ModTarget Target, long Changelist)? _target;
    private int _total;

    public MainViewModel Main => _main;
    public ObservableCollection<SmrResultRow> Results { get; } = [];
    public ObservableCollection<SmrVersionOption> Versions { get; } = [];
    public static IReadOnlyList<string> SortOptions { get; } = ["Popular", "Recently updated", "Most downloaded", "Name"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedSort = "Popular";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private SmrResultRow? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private SmrVersionOption? _selectedVersion;

    [ObservableProperty] private string _versionNote = "";
    [ObservableProperty] private string _changelog = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _canLoadMore;

    public SmrBrowserViewModel(MainViewModel main)
    {
        _main = main;
        _smr = main.CreateSmr();
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = SearchAsync(append: false);
        };
        _main.PropertyChanged += OnMainChanged;
    }

    public void Dispose()
    {
        _main.PropertyChanged -= OnMainChanged;
        _debounce.Stop();
        _searchCts?.Cancel();
        _smr.Dispose();
    }

    private void OnMainChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy)) InstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>First load: read the selected install's platform, then list popular mods.</summary>
    public async Task StartAsync()
    {
        _target = await _main.GetSelectedTargetAsync();
        TargetText = _main.SelectedInstall is { } i && _target is { } t
            ? $"Installs into {(i.IsServer ? "the server " : "")}'{i.Name}' ({t.Target}{(t.Changelist != 0 ? $", game build {t.Changelist}" : "")})"
            : "Pick a game or server in the main window to install into.";
        await SearchAsync(append: false);
    }

    partial void OnSearchTextChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnSelectedSortChanged(string value) => _ = SearchAsync(append: false);

    partial void OnSelectedChanged(SmrResultRow? value) => _ = LoadVersionsAsync(value);

    partial void OnSelectedVersionChanged(SmrVersionOption? value)
    {
        var v = value?.Version ?? Versions.Skip(1).FirstOrDefault(o => o.Fits)?.Version;
        Changelog = v?.Changelog is { Length: > 0 } c ? TextFormat.MarkdownToPlain(c) : "";
        VersionNote = value is { Version: { } pinned, Fits: false } && _target is { } t && !SmrResolver.Fits(pinned, t.Target, t.Changelist, out var why)
            ? $"This version {why}, so it can't be installed here."
            : "";
    }

    private bool CanLoadMoreNow() => CanLoadMore && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadMoreNow))]
    private Task LoadMore() => SearchAsync(append: true);

    private async Task SearchAsync(bool append)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsLoading = true;
        StatusText = "Searching SMR…";
        try
        {
            var sort = SelectedSort switch
            {
                "Recently updated" => SmrSort.RecentlyUpdated,
                "Most downloaded" => SmrSort.MostDownloaded,
                "Name" => SmrSort.Name,
                _ => SmrSort.Popular,
            };
            var result = await _smr.SearchAsync(SearchText, sort, append ? Results.Count : 0, PageSize, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!append) Results.Clear();
            var installed = _main.InstalledVersions;
            var fresh = result.Mods.Select(m => new SmrResultRow(m, installed.TryGetValue(m.ModReference, out var v) ? v : null)).ToList();
            foreach (var r in fresh) Results.Add(r);
            _total = result.Total;
            CanLoadMore = Results.Count < _total;
            StatusText = _total == 0 ? "No mods found." : $"Showing {Results.Count} of {_total:N0} mods on SMR.";
            _ = LoadLogosAsync(fresh, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SmrException ex)
        {
            StatusText = "SMR couldn't be searched: " + ex.Message;
        }
        finally
        {
            if (_searchCts == cts) IsLoading = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadLogosAsync(IReadOnlyList<SmrResultRow> rows, CancellationToken ct)
    {
        var dir = Path.Combine(_main.CachePath, "smr-logos");
        foreach (var r in rows.Where(r => r.Mod.LogoUrl is not null))
        {
            if (ct.IsCancellationRequested) return;
            r.Logo = await MainViewModel.LoadLogoAsync(_smr, r.Mod, dir);
        }
    }

    private async Task LoadVersionsAsync(SmrResultRow? row)
    {
        Versions.Clear();
        SelectedVersion = null;
        Changelog = "";
        VersionNote = "";
        if (row is null) return;
        try
        {
            var all = await _smr.GetVersionsAsync([row.ModReference]);
            if (Selected != row) return;
            if (!all.TryGetValue(row.ModReference, out var versions) || versions.Count == 0)
            {
                VersionNote = "SMR lists no versions for this mod.";
                return;
            }
            Versions.Add(new SmrVersionOption(null, "Newest that fits (recommended)", true));
            foreach (var v in versions.Where(v => v.Approved))
            {
                bool fits = _target is { } t && SmrResolver.Fits(v, t.Target, t.Changelist, out _);
                Versions.Add(new SmrVersionOption(v,
                    $"{v.Version}{(v.IsRelease ? "" : $" ({v.Stability})")}" + (v.CreatedAt is { } d ? $"  ·  {d.LocalDateTime:yyyy-MM-dd}" : "") + (fits ? "" : "  ·  doesn't fit"),
                    fits));
            }
            SelectedVersion = Versions[0];
            if (!Versions.Skip(1).Any(o => o.Fits))
                VersionNote = "No version of this mod fits the selected install (platform or game version).";
        }
        catch (SmrException ex)
        {
            VersionNote = "Versions couldn't be read: " + ex.Message;
        }
    }

    private bool CanInstall() => Selected is not null && SelectedVersion is not null && (SelectedVersion.Version is null || SelectedVersion.Fits)
                                 && !_main.IsBusy && _main.SelectedInstall is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var row = Selected!;
        await _main.InstallFromSmrAsync([new SmrRequest(row.ModReference, SelectedVersion?.Version?.Version)]);
        // Refresh the "Installed" tags (dependencies may have been installed too).
        var installed = _main.InstalledVersions;
        foreach (var r in Results) r.InstalledVersion = installed.TryGetValue(r.ModReference, out var v) ? v : null;
    }

    [RelayCommand]
    private void OpenPage() => _main.OpenUrlCommand.Execute(Selected?.Mod.PageUrl);
}
