using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sdmm.Core.Modpacks;

namespace Sdmm.App.ViewModels;

/// <summary>
/// A note on a modpack mod for the Modpacks and Compare windows: SMR reports it broken or damaged, or its GitHub repo
/// isn't in the sources.
/// </summary>
public sealed record ModpackModStatus(string Text, string Tip, string BrushKey)
{
    public System.Windows.Media.Brush Brush => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[BrushKey];

    /// <summary>For picking the most serious of several notes.</summary>
    public int Severity => BrushKey switch { "BadBrush" => 3, "WarnBrush" => 2, _ => 1 };
}

/// <summary>One mod in the Modpacks window; edits write straight through to the <see cref="ModpackMod"/>.</summary>
public sealed partial class ModpackModRow : ObservableObject
{
    public ModpackModRow(ModpackMod mod)
    {
        Mod = mod;
        // Packs from before the automatic side was stored: their side was worked out automatically too.
        if (!mod.SideSetByUser && mod.Side != mod.AutoSide) mod.AutoSide = mod.Side;
    }

    public ModpackMod Mod { get; }
    public string ModReference => Mod.ModReference;
    public string VersionText => Mod.Version ?? Mod.Constraint ?? "newest";

    /// <summary>A note on this mod (SMR report, repo not in the sources), or null.</summary>
    [ObservableProperty] private ModpackModStatus? _status;

    /// <summary>After the pack changed elsewhere (applied with newer versions, or followed an install).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(IsAutoSide));
        OnPropertyChanged(nameof(IsSideOverridden));
    }
    public string SourceText => SourceLabel(Mod);

    /// <summary>Where a pack mod comes from, as shown in the Modpacks and Compare windows.</summary>
    public static string SourceLabel(ModpackMod mod) => mod.Source switch
    {
        ModpackSource.GitHub => mod.Repo ?? "GitHub",
        ModpackSource.Imported => "imported zip",
        _ => "SMR",
    };

    public bool Enabled
    {
        get => Mod.Enabled;
        set { if (Mod.Enabled != value) { Mod.Enabled = value; OnPropertyChanged(); } }
    }

    public static IReadOnlyList<string> SideOptions { get; } = ["Games and servers", "Players' games only", "Servers only"];

    public static string Label(ModSide side) => side switch
    {
        ModSide.ClientOnly => "Players' games only",
        ModSide.ServerOnly => "Servers only",
        _ => "Games and servers",
    };

    /// <summary>Where the mod is installed. Picking the automatic value again counts as automatic.</summary>
    public string SideText
    {
        get => Label(Mod.Side);
        set
        {
            var side = value switch { "Players' games only" => ModSide.ClientOnly, "Servers only" => ModSide.ServerOnly, _ => ModSide.Both };
            if (Mod.Side == side) return;
            Mod.Side = side;
            Mod.SideSetByUser = side != Mod.AutoSide;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutoSide));
            OnPropertyChanged(nameof(IsSideOverridden));
            OnPropertyChanged(nameof(SideTip));
        }
    }

    public bool IsAutoSide => !Mod.SideSetByUser;
    public bool IsSideOverridden => Mod.SideSetByUser;

    public string SideTip => IsAutoSide
        ? "Automatic: worked out from the mod's .uplugin when it was added (mods that set RequiredOnRemote to false go only on the side they were found on). Pick another value to override it."
        : $"Set by you. Automatic would be: {Label(Mod.AutoSide)}.";

    [RelayCommand]
    private void ResetSide()
    {
        Mod.Side = Mod.AutoSide;
        Mod.SideSetByUser = false;
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(IsAutoSide));
        OnPropertyChanged(nameof(IsSideOverridden));
        OnPropertyChanged(nameof(SideTip));
    }
}

/// <summary>One modpack in the list.</summary>
public sealed partial class ModpackItem : ObservableObject
{
    public Modpack Pack { get; }
    public ObservableCollection<ModpackModRow> Rows { get; }

    private readonly Func<ModpackItem, string, string> _uniqueName;
    private readonly Action<string, string> _renamed;

    /// <param name="uniqueName">Makes a typed name unique among the other packs.</param>
    /// <param name="renamed">Called with the old and new name after a rename.</param>
    public ModpackItem(Modpack pack, Func<ModpackItem, string, string> uniqueName, Action<string, string> renamed)
    {
        Pack = pack;
        _uniqueName = uniqueName;
        _renamed = renamed;
        Rows = new ObservableCollection<ModpackModRow>(pack.Mods.Select(m => new ModpackModRow(m)));
    }

    /// <summary>Committed when the name box loses focus or Enter is pressed. Blank keeps the old name; a taken name gets " (2)".</summary>
    public string Name
    {
        get => Pack.Name;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? Pack.Name : _uniqueName(this, value.Trim());
            var old = Pack.Name;
            if (old != v)
            {
                Pack.Name = v;
                _renamed(old, v);
            }
            // Always: the box shows the name actually used (trimmed, or made unique).
            OnPropertyChanged();
        }
    }

    public string Summary => $"{Rows.Count} mods";

    /// <summary>Brings the rows up to date with the pack, which may have changed while the window was open.</summary>
    public void Resync()
    {
        if (!Rows.Select(r => r.Mod).SequenceEqual(Pack.Mods))
        {
            Rows.Clear();
            foreach (var m in Pack.Mods) Rows.Add(new ModpackModRow(m));
            OnPropertyChanged(nameof(Summary));
        }
        else foreach (var r in Rows) r.Refresh();
    }

    public void RemoveRow(ModpackModRow row)
    {
        Rows.Remove(row);
        Pack.Mods.Remove(row.Mod);
        OnPropertyChanged(nameof(Summary));
    }

    public override string ToString() => Name;
}

/// <summary>
/// The Modpacks window: save the selected game or server as a modpack, import and export modpack files, edit which
/// mods a pack has and where they go, and apply a pack to the selected install. SMR entries install only when SMR
/// installs are available, as does importing SMM profiles.
/// </summary>
public sealed partial class ModpacksViewModel : ObservableObject
{
    private const string FileFilter = "Modpacks (*.modpack.json)|*.modpack.json|JSON files (*.json)|*.json";
    private readonly MainViewModel _main;
    private readonly IDialogs _dialogs;

    public MainViewModel Main => _main;
    public ObservableCollection<ModpackItem> Packs { get; }
    public IReadOnlyList<string> SmmProfiles { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(DeleteCommand), nameof(ApplyCommand), nameof(DuplicateCommand))]
    private ModpackItem? _selected;

    [ObservableProperty] private ModpackModRow? _selectedRow;
    [ObservableProperty] private bool _removeOthers = true;
    [ObservableProperty] private string? _selectedSmmProfile;

    public string TargetText => _main.SelectedInstall is { } i
        ? $"Applies to {(i.IsServer ? "the server " : "")}'{i.Name}'. The right files for its platform are picked automatically, and mods marked for the other side are skipped."
        : "Pick a game or server in the main window to apply modpacks to.";

    public bool IsVariant => Brand.Current.IsVariant;

    public ModpacksViewModel(MainViewModel main, IDialogs dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Packs = new ObservableCollection<ModpackItem>(main.Modpacks.Select(Item));
        Selected = Packs.FirstOrDefault();
        SmmProfiles = IsVariant ? main.SmmSettings.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        SelectedSmmProfile = SmmProfiles.FirstOrDefault();
        _main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy)) ApplyCommand.NotifyCanExecuteChanged(); };
        Packs.CollectionChanged += (_, _) => CompareCommand.NotifyCanExecuteChanged();
        _ = LoadStatusesAsync();
    }

    private IReadOnlyDictionary<string, Sdmm.Core.Smr.SmrMod> _smrInfo = new Dictionary<string, Sdmm.Core.Smr.SmrMod>();

    /// <summary>Looks the packs' SMR mods up on SMR (for broken/damaged reports), then updates every row's note.</summary>
    private async Task LoadStatusesAsync()
    {
        UpdateStatuses();
        var refs = _main.Modpacks.SelectMany(p => p.Mods).Where(m => m.Source == ModpackSource.Smr).Select(m => m.ModReference).ToList();
        if (refs.Count == 0) return;
        try
        {
            _smrInfo = await _main.GetSmrInfoAsync(refs);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            _main.Log("SMR lookup for modpacks skipped: " + ex.Message);
        }
        UpdateStatuses();
    }

    private void UpdateStatuses()
    {
        foreach (var row in Packs.SelectMany(p => p.Rows)) row.Status = StatusFor(row.Mod);
    }

    /// <summary>
    /// The note for a pack mod: SMR's broken/damaged report (for the selected game or server's branch) for mods
    /// installed from SMR, or that a GitHub mod's repo isn't in the sources. Dev builds have no SMR report.
    /// </summary>
    public ModpackModStatus? StatusFor(ModpackMod m)
    {
        if (m.Source == ModpackSource.Smr && _smrInfo.TryGetValue(m.ModReference, out var smr) &&
            Sdmm.Core.Smr.SmrReport.For(smr, _main.BranchOf(_main.SelectedInstall)) is { } r)
            return new(r.Short, r.Message, r.Level == Sdmm.Core.Smr.SmrReportLevel.Broken ? "BadBrush" : "WarnBrush");
        if (m.Source == ModpackSource.GitHub && m.Repo is { Length: > 0 } repo && !_main.RepoInSources(repo))
            return new("Not in your sources",
                $"{repo} isn't in your mod sources, so SDMM can't install this mod yet. Applying the modpack offers to add it, or add it under Settings → Mod sources.",
                "InfoBrush");
        return null;
    }

    /// <summary>Writes the modpacks back to the settings file (edits are made in place).</summary>
    public void Save() => _main.SaveModpacks();

    private ModpackItem Item(Modpack pack) => new(pack, UniqueName, (old, now) =>
    {
        _main.ModpackRenamed(old, now);
        Save();
    });

    /// <summary>Keeps names unique, so exported files, the list and the main window's picker stay unambiguous.</summary>
    private string UniqueName(ModpackItem? self, string name)
    {
        var result = name;
        for (int n = 2; _main.Modpacks.Any(p => p != self?.Pack && p.Name.Equals(result, StringComparison.OrdinalIgnoreCase)); n++)
            result = $"{name} ({n})";
        return result;
    }

    private void Add(Modpack pack)
    {
        pack.Name = UniqueName(null, pack.Name);
        _main.Modpacks.Add(pack);
        var item = Item(pack);
        Packs.Add(item);
        Selected = item;
        Save();
    }

    /// <summary>Asks the window to open the Compare window, starting from the selected pack.</summary>
    public event Action<Modpack?>? CompareRequested;

    private bool CanCompare() => Packs.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare()
    {
        Save();
        CompareRequested?.Invoke(Selected?.Pack);
    }

    /// <summary>A separate copy of the selected pack, to change without touching the original.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate() => Add(Selected!.Pack.Copy(Selected.Name + " (copy)", DateTimeOffset.UtcNow));

    [RelayCommand]
    private async Task NewFromInstallAsync()
    {
        var install = _main.SelectedInstall;
        if (install is null)
        {
            _dialogs.Info("New modpack", "Pick a game or server in the main window first.");
            return;
        }
        var pack = await _main.CaptureModpackAsync($"{install.Name} {DateTime.Now:yyyy-MM-dd}");
        if (pack is null) return;
        if (pack.Mods.Count == 0)
        {
            _dialogs.Info("New modpack", $"'{install.Name}' has no mods installed.");
            return;
        }
        Add(pack);
    }

    [RelayCommand]
    private void Import()
    {
        var file = _dialogs.PickOpenFile("Import a modpack", FileFilter);
        if (file is null) return;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 5 * 1024 * 1024) throw new FormatException("The file is too big to be a modpack.");
            var pack = ModpackIO.Import(File.ReadAllText(file), DateTimeOffset.UtcNow);
            Add(pack);
            // Someone else's pack may use GitHub repos this user doesn't follow.
            _main.OfferToAddSources(_main.ReposMissingFromSources(pack.Mods), "Not now",
                "Leave the sources as they are. Applying the modpack offers this again.");
            _ = LoadStatusesAsync();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Import modpack", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportSmmProfile()
    {
        if (!IsVariant || SelectedSmmProfile is null) return;
        if (!_main.SmmSettings.Profiles.TryGetValue(SelectedSmmProfile, out var profile)) return;
        Add(ModpackIO.FromSmmProfile(profile, DateTimeOffset.UtcNow));
        _ = LoadStatusesAsync();
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportAsync()
    {
        var pack = Selected!.Pack;
        var safe = string.Concat(pack.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = _dialogs.PickSaveFile("Export modpack", FileFilter, safe + ModpackFile.Extension);
        if (file is null) return;
        var origin = await _main.GetExportOriginAsync();
        try
        {
            Sdmm.Core.Diagnostics.FileLog.Write("modpack", $"exporting to {file}");
            File.WriteAllText(file, ModpackIO.Export(pack, origin?.Target ?? Sdmm.Core.Packaging.ModTarget.Windows, origin?.Changelist ?? 0));
            _main.Log($"Exported modpack '{pack.Name}' to {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Export modpack", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        var item = Selected!;
        if (!_dialogs.Confirm("Delete modpack", $"Delete the modpack '{item.Name}'? Installed mods aren't affected.")) return;
        _main.Modpacks.Remove(item.Pack);
        Packs.Remove(item);
        Selected = Packs.FirstOrDefault();
        Save();
    }

    [RelayCommand]
    private void RemoveRow(ModpackModRow? row)
    {
        if (row is null || Selected is null) return;
        Selected.RemoveRow(row);
        Save();
    }

    private bool CanApply() => Selected is not null && !_main.IsBusy && _main.SelectedInstall is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        Save();
        await _main.ApplyModpackAsync(Selected!.Pack, RemoveOthers);
        // Applying may have updated the pack (newer versions), and added sources change the notes.
        foreach (var p in Packs) p.Resync();
        UpdateStatuses();
    }
}
