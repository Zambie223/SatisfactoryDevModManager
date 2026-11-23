using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sdmm.Core.Modpacks;

namespace Sdmm.App.ViewModels;

/// <summary>One mod in the Compare window: what each pack has, and how they differ.</summary>
public sealed class ModpackCompareRow
{
    public ModpackCompareRow(ModpackDiffLine line, string leftName, string rightName, Func<ModpackMod, ModpackModStatus?>? status = null)
    {
        Line = line;
        // The most serious note from either side (an SMR report, or a repo that isn't in the sources).
        Note = new[] { line.Left, line.Right }.OfType<ModpackMod>().Select(m => status?.Invoke(m)).OfType<ModpackModStatus>()
            .OrderByDescending(s => s.Severity).FirstOrDefault();
        LeftText = Describe(line.Left);
        RightText = Describe(line.Right);
        StatusText = line.Kind switch
        {
            ModpackDiffKind.OnlyLeft => $"Only in {leftName}",
            ModpackDiffKind.OnlyRight => $"Only in {rightName}",
            ModpackDiffKind.Same => "In both",
            _ => "In both, different " + string.Join(", ", Parts(line.Differences)),
        };
    }

    public ModpackDiffLine Line { get; }
    public ModpackDiffKind Kind => Line.Kind;
    public string ModReference => Line.ModReference;
    public string LeftText { get; }
    public string RightText { get; }
    public string StatusText { get; }
    public ModpackModStatus? Note { get; }

    /// <summary>"1.2.0 · SMR", plus the side when it isn't both and "(off)" when switched off; a dash when missing.</summary>
    private static string Describe(ModpackMod? m)
    {
        if (m is null) return "—";
        var text = $"{m.Version ?? m.Constraint ?? "newest"} · {ModpackModRow.SourceLabel(m)}";
        if (m.Side != ModSide.Both) text += " · " + ModpackModRow.Label(m.Side).ToLowerInvariant();
        return m.Enabled ? text : text + " (off)";
    }

    private static IEnumerable<string> Parts(ModpackDiffParts p)
    {
        if (p.HasFlag(ModpackDiffParts.Version)) yield return "version";
        if (p.HasFlag(ModpackDiffParts.Source)) yield return "source";
        if (p.HasFlag(ModpackDiffParts.Side)) yield return "side";
        if (p.HasFlag(ModpackDiffParts.Enabled)) yield return "on/off";
    }
}

/// <summary>Which rows the Compare window shows.</summary>
public enum ModpackCompareFilter { All, OnlyLeft, OnlyRight, Both }

/// <summary>
/// The Compare window (from the Modpacks window): two modpacks side by side, with the mods both have and the ones
/// each is missing. Read-only; it never changes a pack.
/// </summary>
public sealed partial class ModpackCompareViewModel : ObservableObject
{
    private IReadOnlyList<ModpackCompareRow> _all = [];
    private readonly Func<ModpackMod, ModpackModStatus?>? _status;

    public ModpackCompareViewModel(IReadOnlyList<Modpack> packs, Modpack? left, Func<ModpackMod, ModpackModStatus?>? status = null)
    {
        _status = status;
        Packs = packs;
        Left = left ?? packs.FirstOrDefault();
        Right = packs.FirstOrDefault(p => p != Left) ?? Left;
    }

    public IReadOnlyList<Modpack> Packs { get; }

    [ObservableProperty] private Modpack? _left;
    [ObservableProperty] private Modpack? _right;
    [ObservableProperty] private ModpackCompareFilter _filter;
    [ObservableProperty] private IReadOnlyList<ModpackCompareRow> _rows = [];
    [ObservableProperty] private string _summary = "";

    public bool IsEmpty => Rows.Count == 0;

    partial void OnRowsChanged(IReadOnlyList<ModpackCompareRow> value) => OnPropertyChanged(nameof(IsEmpty));

    public string LeftName => Left?.Name ?? "";
    public string RightName => Right?.Name ?? "";
    public bool SamePack => Left is not null && Left == Right;

    public string AllLabel => $"All ({_all.Count})";
    public string OnlyLeftLabel => $"Only in {LeftName} ({Count(ModpackDiffKind.OnlyLeft)})";
    public string OnlyRightLabel => $"Only in {RightName} ({Count(ModpackDiffKind.OnlyRight)})";
    public string BothLabel => $"In both ({Count(ModpackDiffKind.Same) + Count(ModpackDiffKind.Different)})";

    // The filter tabs are radio buttons, one bool each.
    public bool ShowAll { get => Filter == ModpackCompareFilter.All; set { if (value) Filter = ModpackCompareFilter.All; } }
    public bool ShowOnlyLeft { get => Filter == ModpackCompareFilter.OnlyLeft; set { if (value) Filter = ModpackCompareFilter.OnlyLeft; } }
    public bool ShowOnlyRight { get => Filter == ModpackCompareFilter.OnlyRight; set { if (value) Filter = ModpackCompareFilter.OnlyRight; } }
    public bool ShowBoth { get => Filter == ModpackCompareFilter.Both; set { if (value) Filter = ModpackCompareFilter.Both; } }

    partial void OnLeftChanged(Modpack? value) => Refresh();
    partial void OnRightChanged(Modpack? value) => Refresh();

    partial void OnFilterChanged(ModpackCompareFilter value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(ShowAll));
        OnPropertyChanged(nameof(ShowOnlyLeft));
        OnPropertyChanged(nameof(ShowOnlyRight));
        OnPropertyChanged(nameof(ShowBoth));
    }

    [RelayCommand]
    private void Swap() => (Left, Right) = (Right, Left);

    private int Count(ModpackDiffKind kind) => _all.Count(r => r.Kind == kind);

    private void Refresh()
    {
        _all = Left is null || Right is null
            ? []
            : ModpackDiff.Compare(Left, Right).Select(l => new ModpackCompareRow(l, LeftName, RightName, _status)).ToList();
        int differ = Count(ModpackDiffKind.Different);
        int both = differ + Count(ModpackDiffKind.Same);
        Summary = Left is null || Right is null ? ""
            : SamePack ? "That's the same modpack on both sides; pick another one to compare with."
            : $"{both} in both ({(differ == 0 ? "all the same" : $"{differ} set differently")}) · {Count(ModpackDiffKind.OnlyLeft)} only in {LeftName} · {Count(ModpackDiffKind.OnlyRight)} only in {RightName}";
        foreach (var name in new[] { nameof(LeftName), nameof(RightName), nameof(SamePack), nameof(AllLabel), nameof(OnlyLeftLabel), nameof(OnlyRightLabel), nameof(BothLabel) })
            OnPropertyChanged(name);
        ApplyFilter();
    }

    private void ApplyFilter() => Rows = Filter switch
    {
        ModpackCompareFilter.OnlyLeft => _all.Where(r => r.Kind == ModpackDiffKind.OnlyLeft).ToList(),
        ModpackCompareFilter.OnlyRight => _all.Where(r => r.Kind == ModpackDiffKind.OnlyRight).ToList(),
        ModpackCompareFilter.Both => _all.Where(r => r.Kind is ModpackDiffKind.Same or ModpackDiffKind.Different).ToList(),
        _ => _all,
    };
}
