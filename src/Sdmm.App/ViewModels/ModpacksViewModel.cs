using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sdmm.Core.Modpacks;

namespace Sdmm.App.ViewModels;

/// <summary>One mod in the Modpacks window; edits write straight through to the <see cref="ModpackMod"/>.</summary>
public sealed partial class ModpackModRow(ModpackMod mod) : ObservableObject
{
    public ModpackMod Mod { get; } = mod;
    public string ModReference => Mod.ModReference;
    public string VersionText => Mod.Version ?? Mod.Constraint ?? "newest";
    public string SourceText => Mod.Source switch
    {
        ModpackSource.GitHub => Mod.Repo ?? "GitHub",
        ModpackSource.Imported => "imported zip",
        _ => "SMR",
    };

    public bool Enabled
    {
        get => Mod.Enabled;
        set { if (Mod.Enabled != value) { Mod.Enabled = value; OnPropertyChanged(); } }
    }

    public static IReadOnlyList<string> SideOptions { get; } = ["Both", "Players only", "Server only"];

    public string SideText
    {
        get => Mod.Side switch { ModSide.ClientOnly => "Players only", ModSide.ServerOnly => "Server only", _ => "Both" };
        set
        {
            var side = value switch { "Players only" => ModSide.ClientOnly, "Server only" => ModSide.ServerOnly, _ => ModSide.Both };
            if (Mod.Side != side) { Mod.Side = side; OnPropertyChanged(); }
        }
    }
}

/// <summary>One modpack in the list.</summary>
public sealed partial class ModpackItem : ObservableObject
{
    public Modpack Pack { get; }
    public ObservableCollection<ModpackModRow> Rows { get; }

    public ModpackItem(Modpack pack)
    {
        Pack = pack;
        Rows = new ObservableCollection<ModpackModRow>(pack.Mods.Select(m => new ModpackModRow(m)));
    }

    public string Name
    {
        get => Pack.Name;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? Pack.Name : value.Trim();
            if (Pack.Name != v) { Pack.Name = v; OnPropertyChanged(); }
        }
    }

    public string Summary => $"{Rows.Count} mods";

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
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(DeleteCommand), nameof(ApplyCommand))]
    private ModpackItem? _selected;

    [ObservableProperty] private ModpackModRow? _selectedRow;
    [ObservableProperty] private bool _removeOthers = true;
    [ObservableProperty] private bool _useNewest;
    [ObservableProperty] private string? _selectedSmmProfile;

    public string TargetText => _main.SelectedInstall is { } i
        ? $"Applies to {(i.IsServer ? "the server " : "")}'{i.Name}'. The right files for its platform are picked automatically, and mods marked for the other side are skipped."
        : "Pick a game or server in the main window to apply modpacks to.";

    public bool IsVariant => Brand.Current.IsVariant;

    public ModpacksViewModel(MainViewModel main, IDialogs dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Packs = new ObservableCollection<ModpackItem>(main.Modpacks.Select(p => new ModpackItem(p)));
        Selected = Packs.FirstOrDefault();
        SmmProfiles = IsVariant ? main.SmmSettings.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        SelectedSmmProfile = SmmProfiles.FirstOrDefault();
        _main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsBusy)) ApplyCommand.NotifyCanExecuteChanged(); };
    }

    /// <summary>Writes the modpacks back to the settings file (edits are made in place).</summary>
    public void Save() => _main.SaveModpacks();

    private void Add(Modpack pack)
    {
        // Keep names unique, so exported files and the list stay unambiguous.
        var baseName = pack.Name;
        for (int n = 2; _main.Modpacks.Any(p => p.Name.Equals(pack.Name, StringComparison.OrdinalIgnoreCase)); n++)
            pack.Name = $"{baseName} ({n})";
        _main.Modpacks.Add(pack);
        var item = new ModpackItem(pack);
        Packs.Add(item);
        Selected = item;
        Save();
    }

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
            Add(ModpackIO.Import(File.ReadAllText(file), DateTimeOffset.UtcNow));
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
    private Task ApplyAsync()
    {
        Save();
        return _main.ApplyModpackAsync(Selected!.Pack, RemoveOthers, UseNewest);
    }
}
