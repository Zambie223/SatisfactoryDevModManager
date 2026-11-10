using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sdmm.Core.Installs;
using Sdmm.Core.Json;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Modpacks;

/// <summary>Which side of a multiplayer game a mod belongs on.</summary>
public enum ModSide
{
    /// <summary>Client and server (the default; SML requires most mods on both).</summary>
    Both,
    /// <summary>Only players' games (its .uplugin sets RequiredOnRemote to false and it was on a client).</summary>
    ClientOnly,
    /// <summary>Only the server (RequiredOnRemote false and it was on a server, like DepotNetworkFix).</summary>
    ServerOnly,
}

/// <summary>Where a modpack mod is installed from.</summary>
public enum ModpackSource
{
    /// <summary>SMR (ficsit.app).</summary>
    Smr,
    /// <summary>A GitHub release from your sources (<see cref="ModpackMod.Repo"/>).</summary>
    GitHub,
    /// <summary>A zip added with Install from zip.</summary>
    Imported,
}

/// <summary>One mod in a modpack. Platform-neutral: the right zip is chosen when the pack is applied.</summary>
public sealed class ModpackMod
{
    public required string ModReference { get; set; }
    /// <summary>The exact version to install, or null to use <see cref="Constraint"/> (or the newest that fits).</summary>
    public string? Version { get; set; }
    /// <summary>An SML version range ("&gt;=1.2.0"), used when <see cref="Version"/> is null (for example from an SMM profile).</summary>
    public string? Constraint { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<ModpackSource>))]
    public ModpackSource Source { get; set; } = ModpackSource.Smr;
    /// <summary>"owner/repo" for <see cref="ModpackSource.GitHub"/>.</summary>
    public string? Repo { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<ModSide>))]
    public ModSide Side { get; set; } = ModSide.Both;

    /// <summary>
    /// The side worked out automatically when the mod was added (from its .uplugin; "both" when that isn't known).
    /// <see cref="Side"/> equals it unless <see cref="SideSetByUser"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ModSide>))]
    public ModSide AutoSide { get; set; } = ModSide.Both;

    /// <summary>The user picked <see cref="Side"/> in the modpack, overriding <see cref="AutoSide"/>.</summary>
    public bool SideSetByUser { get; set; }

    public bool Enabled { get; set; } = true;

    public ModpackMod Clone() => new()
    {
        ModReference = ModReference, Version = Version, Constraint = Constraint, Source = Source, Repo = Repo,
        Side = Side, AutoSide = AutoSide, SideSetByUser = SideSetByUser, Enabled = Enabled,
    };

    /// <summary>Whether this mod belongs on a server (true) or a player's game (false).</summary>
    public bool AppliesTo(bool server) => Side switch
    {
        ModSide.ClientOnly => !server,
        ModSide.ServerOnly => server,
        _ => true,
    };
}

/// <summary>A named set of mods that can be applied to a game or a server (SMM calls these profiles).</summary>
public sealed class Modpack
{
    public string Name { get; set; } = "Modpack";
    public List<ModpackMod> Mods { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>A separate copy with every mod and setting (Duplicate in the Modpacks window).</summary>
    public Modpack Copy(string name, DateTimeOffset now) => new() { Name = name, Mods = Mods.Select(m => m.Clone()).ToList(), CreatedAtUtc = now };
}

/// <summary>Where an exported modpack came from; informational, applying works on any platform.</summary>
public sealed class ModpackOrigin
{
    [JsonConverter(typeof(JsonStringEnumConverter<ModTarget>))]
    public ModTarget Platform { get; set; }
    public long GameBuild { get; set; }
}

/// <summary>The exported file (".modpack.json").</summary>
public sealed class ModpackFile
{
    public const string FormatName = "sdmm-modpack";
    public const int CurrentFormatVersion = 1;
    public const string Extension = ".modpack.json";

    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "";
    public ModpackOrigin? ExportedFrom { get; set; }
    public List<ModpackMod> Mods { get; set; } = [];
}

public static partial class ModpackIO
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ModReferencePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoPattern();

    /// <summary>
    /// Captures what's installed as a modpack. Mods whose .uplugin sets RequiredOnRemote to false are marked for the
    /// side they were found on (client-only on a game, server-only on a server). SMR is assumed for mods SMM or a hand
    /// copy installed. Broken copies (no readable .uplugin) are left out.
    /// </summary>
    public static Modpack FromInstall(string name, IReadOnlyList<InstalledMod> installed, bool isServer, string smrSourcePrefix, DateTimeOffset now)
    {
        var mods = new List<ModpackMod>();
        foreach (var m in installed.Where(m => m.Descriptor is not null).GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var repo = m.Marker?.SourceRepo;
            var (source, ghRepo) = m.Owner == ModOwner.Sdmm && m.Marker is { IsAdoptedCopy: false }
                ? repo is null ? (ModpackSource.Imported, (string?)null)
                : repo.StartsWith(smrSourcePrefix, StringComparison.OrdinalIgnoreCase) ? (ModpackSource.Smr, null)
                : (ModpackSource.GitHub, repo)
                : (ModpackSource.Smr, null);
            var side = m.Descriptor!.IsRequiredOnRemote ? ModSide.Both : isServer ? ModSide.ServerOnly : ModSide.ClientOnly;
            mods.Add(new ModpackMod
            {
                ModReference = m.ModReference,
                Version = m.Descriptor.EffectiveVersion.ToString(),
                Source = source,
                Repo = ghRepo,
                Side = side,
                AutoSide = side,
            });
        }
        return new Modpack { Name = name, Mods = mods.OrderBy(x => x.ModReference, StringComparer.OrdinalIgnoreCase).ToList(), CreatedAtUtc = now };
    }

    /// <summary>
    /// Brings a modpack in line with what's installed, for the given mods (or for every mod when <paramref name="mods"/>
    /// is null). An installed mod is added, or its entry updated to the installed version and source (switched on; a
    /// side the user picked is kept). A mod that's gone is dropped from the pack only if it's in
    /// <paramref name="removed"/> (or when every mod is followed), and only when the pack has it for this kind of
    /// install; entries for the other side, and switched-off ones, stay. Returns whether the pack changed.
    /// </summary>
    public static bool Follow(Modpack pack, IReadOnlyList<InstalledMod> installed, bool isServer, string smrSourcePrefix,
        IReadOnlyCollection<string>? mods, IReadOnlyCollection<string>? removed, DateTimeOffset now)
    {
        var captured = FromInstall("", installed, isServer, smrSourcePrefix, now).Mods.ToDictionary(m => m.ModReference, StringComparer.OrdinalIgnoreCase);
        var present = installed.Select(m => m.ModReference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scope = mods is not null ? mods.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : pack.Mods.Select(m => m.ModReference).Concat(captured.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var r in scope)
        {
            var entry = pack.Mods.FirstOrDefault(m => m.ModReference.Equals(r, StringComparison.OrdinalIgnoreCase));
            if (captured.TryGetValue(r, out var cur))
            {
                if (entry is null)
                {
                    pack.Mods.Add(cur);
                    changed = true;
                    continue;
                }
                var installedVersion = SemVersion.TryParse(cur.Version!, out var iv) ? iv : null;
                // An entry with only a range (from an SMM profile) keeps it while the installed version fits.
                bool fits = entry.Version is null && entry.Constraint is not null && installedVersion is not null &&
                            VersionRange.TryParse(entry.Constraint, out var range, out _) && range!.Matches(installedVersion);
                var before = (entry.Version, entry.Constraint, entry.Source, entry.Repo, entry.Enabled, entry.Side, entry.AutoSide);
                if (!fits) { entry.Version = cur.Version; entry.Constraint = null; }
                entry.Source = cur.Source;
                entry.Repo = cur.Repo;
                entry.Enabled = true;
                entry.AutoSide = cur.AutoSide;
                if (!entry.SideSetByUser || !entry.AppliesTo(isServer)) { entry.Side = cur.Side; entry.SideSetByUser = false; }
                changed |= before != (entry.Version, entry.Constraint, entry.Source, entry.Repo, entry.Enabled, entry.Side, entry.AutoSide);
            }
            else if (entry is not null && !present.Contains(r) && entry.Enabled && entry.AppliesTo(isServer) &&
                     (mods is null || removed?.Contains(r) == true))
            {
                pack.Mods.Remove(entry);
                changed = true;
            }
        }
        if (changed) pack.Mods.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.ModReference, b.ModReference));
        return changed;
    }

    /// <summary>
    /// How the install differs from the pack: pack mods to install or change here, and installed mods the pack doesn't
    /// have. Both empty means it matches.
    /// </summary>
    public static (IReadOnlyList<string> Missing, IReadOnlyList<string> Extra) Compare(Modpack pack, IReadOnlyList<InstalledMod> installed, bool isServer)
    {
        var changes = ModpackPlanner.Plan(pack, installed, isServer, removeOthers: true, useNewest: false);
        return (changes.Install.Select(m => m.ModReference).ToList(), changes.Remove.Concat(changes.KeptForeign).ToList());
    }

    /// <summary>
    /// Converts an SMM profile: its enabled mods, with SMM's version constraint, installed from SMR. Sides aren't known
    /// from a profile, so every mod is "both"; adjust them in the modpack if needed.
    /// </summary>
    public static Modpack FromSmmProfile(SmmProfile profile, DateTimeOffset now) => new()
    {
        Name = profile.Name,
        CreatedAtUtc = now,
        Mods = profile.Mods.Values
            .Select(m => new ModpackMod
            {
                ModReference = m.ModReference,
                Constraint = string.IsNullOrWhiteSpace(m.VersionConstraint) ? null : m.VersionConstraint,
                Source = ModpackSource.Smr,
                Enabled = m.Enabled,
            })
            .OrderBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    public static string Export(Modpack pack, ModTarget platform, long gameBuild) =>
        JsonSerializer.Serialize(new ModpackFile
        {
            Name = pack.Name,
            ExportedFrom = new ModpackOrigin { Platform = platform, GameBuild = gameBuild },
            Mods = pack.Mods,
        }, CoreJsonContext.Default.ModpackFile);

    /// <summary>Reads an exported modpack. Throws <see cref="FormatException"/> with a readable reason if it isn't one.</summary>
    public static Modpack Import(string json, DateTimeOffset now)
    {
        ModpackFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ModpackFile);
        }
        catch (JsonException ex)
        {
            throw new FormatException("This isn't a modpack file (it isn't valid JSON): " + ex.Message, ex);
        }
        if (file is null || file.Format != ModpackFile.FormatName)
            throw new FormatException("This isn't an SDMM modpack file.");
        if (file.FormatVersion > ModpackFile.CurrentFormatVersion)
            throw new FormatException($"This modpack was made by a newer SDMM (format {file.FormatVersion}); update SDMM to import it.");
        var bad = new List<string>();
        foreach (var m in file.Mods)
        {
            if (m.ModReference is null || !ModReferencePattern().IsMatch(m.ModReference)) bad.Add($"'{m.ModReference}' isn't a valid mod reference");
            else if (m.Version is not null && !SemVersion.TryParse(m.Version, out _)) bad.Add($"{m.ModReference}: '{m.Version}' isn't a version");
            else if (m.Constraint is not null && !VersionRange.TryParse(m.Constraint, out _, out _)) bad.Add($"{m.ModReference}: '{m.Constraint}' isn't a version range");
            else if (m.Source == ModpackSource.GitHub && (m.Repo is null || !RepoPattern().IsMatch(m.Repo))) bad.Add($"{m.ModReference}: GitHub source without a valid owner/repo");
        }
        if (bad.Count > 0) throw new FormatException("The modpack has invalid entries:\n" + string.Join("\n", bad.Take(10)));
        var dupes = file.Mods.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0) throw new FormatException("The modpack lists some mods twice: " + string.Join(", ", dupes));
        return new Modpack { Name = string.IsNullOrWhiteSpace(file.Name) ? "Imported modpack" : file.Name.Trim(), Mods = file.Mods, CreatedAtUtc = now };
    }
}

/// <summary>What applying a modpack to an install changes.</summary>
/// <param name="Install">Mods to install or change (missing, or at another version than the pack says).</param>
/// <param name="Remove">SDMM-managed mods installed here that the pack doesn't have (when removal was asked for).</param>
/// <param name="Unchanged">Pack mods already installed at the right version.</param>
/// <param name="Skipped">Pack mods not for this kind of install (side) or switched off, with why.</param>
/// <param name="KeptForeign">Mods installed by SMM, a host or by hand that the pack doesn't have; never removed.</param>
public sealed record ModpackChanges(
    IReadOnlyList<ModpackMod> Install,
    IReadOnlyList<string> Remove,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<(string Mod, string Why)> Skipped,
    IReadOnlyList<string> KeptForeign);

public static class ModpackPlanner
{
    /// <param name="useNewest">Ignore the pack's versions and use the newest that fit (or match its constraints).</param>
    public static ModpackChanges Plan(Modpack pack, IReadOnlyList<InstalledMod> installed, bool isServer, bool removeOthers, bool useNewest)
    {
        var byRef = installed.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var install = new List<ModpackMod>();
        var unchanged = new List<string>();
        var skipped = new List<(string, string)>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in pack.Mods)
        {
            if (!m.Enabled) { skipped.Add((m.ModReference, "switched off in the modpack")); continue; }
            if (!m.AppliesTo(isServer)) { skipped.Add((m.ModReference, isServer ? "only for players' games" : "only for servers")); continue; }
            wanted.Add(m.ModReference);
            var current = byRef.TryGetValue(m.ModReference, out var i) ? i.Descriptor?.EffectiveVersion : null;
            bool matches = current is not null && !useNewest && (
                m.Version is not null ? SemVersion.TryParse(m.Version, out var v) && v == current
                : m.Constraint is null || (VersionRange.TryParse(m.Constraint, out var r, out _) && r!.Matches(current)));
            if (matches) unchanged.Add(m.ModReference);
            else install.Add(m);
        }
        // Mods the pack doesn't want here (absent, switched off, or for the other side). Only SDMM's own are removed;
        // other tools' mods are reported and left alone.
        var others = installed.Where(m => !wanted.Contains(m.ModReference)).ToList();
        var remove = removeOthers ? others.Where(m => m.Owner == ModOwner.Sdmm).Select(m => m.ModReference).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        var kept = others.Where(m => m.Owner != ModOwner.Sdmm).Select(m => m.ModReference).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ModpackChanges(install, remove, unchanged, skipped, kept);
    }
}
