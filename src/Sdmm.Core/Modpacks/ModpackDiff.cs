using Sdmm.Core.Versioning;

namespace Sdmm.Core.Modpacks;

/// <summary>How a mod compares between two modpacks.</summary>
public enum ModpackDiffKind
{
    /// <summary>Only the left pack has it.</summary>
    OnlyLeft,
    /// <summary>Only the right pack has it.</summary>
    OnlyRight,
    /// <summary>Both packs have it, with a different version, source, side or on/off switch.</summary>
    Different,
    /// <summary>Both packs have it with the same settings.</summary>
    Same,
}

/// <summary>What differs for a mod that is in both packs.</summary>
[Flags]
public enum ModpackDiffParts
{
    None = 0,
    /// <summary>The saved version, or the version range when there's no saved version.</summary>
    Version = 1,
    /// <summary>Where it's installed from (SMR, a GitHub repo, an imported zip).</summary>
    Source = 2,
    /// <summary>Which side it goes on (games and servers, players' games only, servers only).</summary>
    Side = 4,
    /// <summary>Switched on in one pack and off in the other.</summary>
    Enabled = 8,
}

/// <summary>One mod in a comparison: its entry in each pack (null when missing) and what differs.</summary>
public sealed record ModpackDiffLine(string ModReference, ModpackMod? Left, ModpackMod? Right, ModpackDiffKind Kind, ModpackDiffParts Differences);

/// <summary>Compares two modpacks mod by mod (the Compare window in the Modpacks window).</summary>
public static class ModpackDiff
{
    /// <summary>
    /// Every mod in either pack, matched by mod reference (ignoring case), ordered by mod reference. Switched-off mods
    /// count as in the pack, as they're kept in it; a different switch shows as <see cref="ModpackDiffParts.Enabled"/>.
    /// </summary>
    public static IReadOnlyList<ModpackDiffLine> Compare(Modpack left, Modpack right)
    {
        var l = Index(left);
        var r = Index(right);
        return l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                l.TryGetValue(key, out var a);
                r.TryGetValue(key, out var b);
                if (a is null) return new ModpackDiffLine(b!.ModReference, null, b, ModpackDiffKind.OnlyRight, ModpackDiffParts.None);
                if (b is null) return new ModpackDiffLine(a.ModReference, a, null, ModpackDiffKind.OnlyLeft, ModpackDiffParts.None);
                var parts = Differences(a, b);
                return new ModpackDiffLine(a.ModReference, a, b, parts == ModpackDiffParts.None ? ModpackDiffKind.Same : ModpackDiffKind.Different, parts);
            })
            .OrderBy(x => x.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // A pack shouldn't list a mod twice; if a hand-edited file does, the first entry is the one that counts.
    private static Dictionary<string, ModpackMod> Index(Modpack pack)
    {
        var map = new Dictionary<string, ModpackMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in pack.Mods) map.TryAdd(m.ModReference, m);
        return map;
    }

    private static ModpackDiffParts Differences(ModpackMod a, ModpackMod b)
    {
        var parts = ModpackDiffParts.None;
        if (!SameVersion(a.Version, b.Version) || a.Version is null && !SameText(a.Constraint, b.Constraint)) parts |= ModpackDiffParts.Version;
        if (a.Source != b.Source || a.Source == ModpackSource.GitHub && !SameText(a.Repo, b.Repo)) parts |= ModpackDiffParts.Source;
        if (a.Side != b.Side) parts |= ModpackDiffParts.Side;
        if (a.Enabled != b.Enabled) parts |= ModpackDiffParts.Enabled;
        return parts;
    }

    // "v1.2.0" and "1.2.0" are the same; build metadata still counts ("+dev.1" vs "+dev.2" are different dev builds).
    private static bool SameVersion(string? a, string? b) =>
        SemVersion.TryParse(a, out var va) && SemVersion.TryParse(b, out var vb)
            ? va!.CompareTo(vb) == 0 && string.Equals(va.Build, vb!.Build, StringComparison.OrdinalIgnoreCase)
            : SameText(a, b);

    private static bool SameText(string? a, string? b) =>
        string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}
