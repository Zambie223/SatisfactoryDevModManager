using Sdmm.Core.Discovery;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Modpacks;

/// <summary>A pack mod with a newer version available than the one the pack saved.</summary>
public sealed record ModpackNewerVersion(string ModReference, SemVersion Saved, SemVersion Newest);

/// <summary>Choosing GitHub and imported-zip builds for modpack mods (shared by the app and the command line tool).</summary>
public static class ModpackVersions
{
    /// <summary>The pack's saved version for a mod, or null when it only has a range (or nothing).</summary>
    public static SemVersion? Saved(ModpackMod m) => SemVersion.TryParse(m.Version, out var v) ? v : null;

    /// <summary>
    /// The builds a pack entry can use on this platform, newest first: from the entry's repo for a GitHub mod, from the
    /// imported zips for an imported one.
    /// </summary>
    public static IReadOnlyList<ModBuild> Candidates(DiscoveredMod? mod, ModpackMod m, ModTarget target) =>
        (mod?.Builds ?? [])
            .Where(b => b.Version is not null && b.Assets.ContainsKey(target) &&
                        (m.Source == ModpackSource.Imported ? b.IsImported
                            : !b.IsImported && b.RepoFullName.Equals(m.Repo, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(b => b.Version)
            .ToList();

    /// <summary>
    /// The newest build that fits the entry's version range, following the pre-release setting. With
    /// <paramref name="preReleaseIfNothingElse"/>, a pre-release is still used when no release fits (as when installing
    /// a mod's dependencies); without it, pre-releases are only considered when they're switched on.
    /// </summary>
    public static ModBuild? Newest(IReadOnlyList<ModBuild> candidates, ModpackMod m, bool includePrereleases, bool preReleaseIfNothingElse)
    {
        var fitting = candidates
            .Where(b => m.Constraint is null || (VersionRange.TryParse(m.Constraint, out var r, out _) && r!.Matches(b.Version!)))
            .ToList();
        return fitting.FirstOrDefault(b => includePrereleases || !b.Prerelease) ?? (preReleaseIfNothingElse ? fitting.FirstOrDefault() : null);
    }

    /// <summary>The build to install: exactly <paramref name="version"/> when given, else the newest that fits.</summary>
    public static ModBuild? Pick(IReadOnlyList<ModBuild> candidates, ModpackMod m, SemVersion? version, bool includePrereleases) =>
        version is not null
            ? candidates.FirstOrDefault(b => b.Version == version)
            : Newest(candidates, m, includePrereleases, preReleaseIfNothingElse: true);

    /// <summary>
    /// A newer build than the saved version, if there is one. Pre-releases only count when they're switched on, or when
    /// the saved version is itself a pre-release (someone testing dev builds wants the next one).
    /// </summary>
    public static ModpackNewerVersion? Newer(IReadOnlyList<ModBuild> candidates, ModpackMod m, bool includePrereleases)
    {
        if (Saved(m) is not { } saved) return null;
        var newest = Newest(candidates, m, includePrereleases || saved.IsPreRelease, preReleaseIfNothingElse: false);
        return newest?.Version is { } v && v > saved ? new ModpackNewerVersion(m.ModReference, saved, v) : null;
    }
}
