using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Installing;

/// <summary>
/// Works out what installing a set of packages would do and everything that could go wrong, without touching files.
/// The checks mirror what SML and ficsit-cli will do:
/// <list type="bullet">
/// <item>SMM replaces or deletes ".smm" folders, and overwrites any folder whose mod is in the profile it applies.</item>
/// <item>SML crashes at startup (Fatal log) on a dependency version mismatch or a GameVersion mismatch
/// (ModLoadingLibrary.cpp, VerifyPluginDependencies).</item>
/// <item>Unreal refuses to start a plugin whose required dependency is missing.</item>
/// </list>
/// </summary>
public sealed class InstallPlanner
{
    private readonly IGameProcessDetector _processes;

    public InstallPlanner(IGameProcessDetector? processes = null)
    {
        _processes = processes ?? new GameProcessDetector();
    }

    /// <param name="installed">What's installed, if the caller just read it (saves a full read over SFTP/FTP); else read here.
    /// The installer re-checks ownership of each folder right before changing it, so a slightly older list can't cause harm.</param>
    public InstallPlan Plan(GameInstall install, IReadOnlyList<InstallRequest> requests, SmmState? smm, IReadOnlyList<InstalledMod>? installed = null)
    {
        var issues = new List<InstallIssue>();
        var actions = new List<PlannedAction>();
        var game = install.Detect();
        var fs = install.FileSystem;
        installed ??= ModScanner.Scan(fs);

        if (install.LocalRoot is { } root)
        {
            foreach (var p in _processes.FindRunning(root))
                issues.Add(new(IssueLevel.Blocked, "GAME_RUNNING", null, $"Close the game first: {p} is running from this install."));
        }
        else
        {
            issues.Add(new(IssueLevel.Warning, "SERVER_RESTART", null, "Restart the server after installing; a running server keeps the old version loaded."));
        }

        foreach (var dup in requests.GroupBy(r => r.Package.ModReference, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(IssueLevel.Blocked, "DUPLICATE_REQUEST", dup.Key, $"{dup.Key} was selected more than once."));

        foreach (var req in requests)
        {
            var pkg = req.Package;
            var d = pkg.Descriptor;
            var modRef = pkg.ModReference;

            if (pkg.Target is null)
                issues.Add(new(IssueLevel.Warning, "UNKNOWN_TARGET", modRef,
                    $"{Path.GetFileName(pkg.ZipPath)} does not follow the <Mod>-<Target>.zip naming, so its platform can't be checked against this install ({game.Target})."));
            else if (pkg.Target != game.Target)
                issues.Add(new(IssueLevel.Blocked, "WRONG_TARGET", modRef,
                    $"{Path.GetFileName(pkg.ZipPath)} is a {pkg.Target} build, but this install is {game.Target}."));

            if (!d.HasValidSemVersion)
                issues.Add(new(IssueLevel.NeedsConfirmation, "NO_SEMVERSION", modRef,
                    $"{modRef}.uplugin has no valid SemVersion, so SML falls back to {d.EffectiveVersion} and cannot tell builds apart in multiplayer."));

            if (req.Source.Tag is { } tag)
            {
                if (!SemVersion.TryParse(tag, out var tagVersion))
                    issues.Add(new(IssueLevel.Blocked, "TAG_NOT_VERSION", modRef, $"Release tag '{tag}' is not a semantic version (expected v<major>.<minor>.<patch>)."));
                else if (d.HasValidSemVersion && tagVersion != d.EffectiveVersion)
                    issues.Add(new(IssueLevel.Blocked, "TAG_MISMATCH", modRef,
                        $"Release tag '{tag}' does not match the SemVersion {d.SemVersionText} inside the zip. Rebuild with the right version or fix the tag."));
            }

            var dest = InstallPaths.ModFolder(modRef, d.GameFeature);
            InstalledMod? existing = null, stale = null;
            bool blocked = false;
            foreach (var m in installed.Where(m => m.ModReference.Equals(modRef, StringComparison.OrdinalIgnoreCase)))
            {
                switch (m.Owner)
                {
                    case ModOwner.Smm:
                        issues.Add(new(IssueLevel.Blocked, "SMM_OWNED", modRef,
                            $"{m.Folder} was installed by SMM. Remove {modRef} from your SMM profile and apply it in SMM first, then install the dev build."));
                        blocked = true;
                        break;
                    case ModOwner.Unmanaged:
                        issues.Add(new(IssueLevel.Blocked, "UNMANAGED_FOLDER", modRef,
                            $"{m.Folder} exists but was not installed by this tool or SMM (no marker). Delete or move it yourself if it should be replaced."));
                        blocked = true;
                        break;
                    default:
                        if (m.Folder.Equals(dest, StringComparison.OrdinalIgnoreCase)) existing = m;
                        else stale = m;
                        break;
                }
            }

            if (install.LocalRoot is { } localRoot && smm is not null)
            {
                foreach (var c in smm.FindConflicts(localRoot, fs, modRef))
                    issues.Add(new(IssueLevel.Blocked, "SMM_PROFILE", modRef, c.Reason));
            }

            var kind = existing?.Marker?.Sha256 == pkg.Sha256 && stale is null ? ActionKind.AlreadyInstalled
                : existing is not null || stale is not null ? ActionKind.Replace
                : ActionKind.Install;
            if (!blocked) actions.Add(new PlannedAction(req, kind, dest, existing, stale));

            if (d.GameVersionText is { } gv && game.Changelist != 0)
            {
                if (VersionRange.TryParse(gv, out var range, out _))
                {
                    if (!range!.Matches(new SemVersion(game.Changelist, 0, 0)))
                        issues.Add(new(IssueLevel.NeedsConfirmation, "GAME_VERSION", modRef,
                            $"{modRef} requires game version {gv}, but this install is build {game.Changelist}. SML will stop the game at startup."));
                }
                else
                {
                    issues.Add(new(IssueLevel.Warning, "BAD_GAME_VERSION", modRef, $"{modRef} has an invalid GameVersion '{gv}'; SML ignores it."));
                }
            }
        }

        CheckDependencies(requests, installed, issues);

        return new InstallPlan { Install = install, Game = game, Actions = actions, Issues = issues };
    }

    /// <summary>Checks dependencies as they will be after the install: requested packages replace installed copies.</summary>
    private static void CheckDependencies(IReadOnlyList<InstallRequest> requests, IReadOnlyList<InstalledMod> installed, List<InstallIssue> issues)
    {
        var after = new Dictionary<string, UPluginDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in installed)
            if (m.Descriptor is not null) after[m.ModReference] = m.Descriptor;
        foreach (var r in requests)
            after[r.Package.ModReference] = r.Package.Descriptor;
        var requested = requests.Select(r => r.Package.ModReference).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Dependencies of what is being installed.
        foreach (var r in requests)
            CheckDependenciesOf(r.Package.Descriptor, after, issues, "MISSING_DEPENDENCY", "DEPENDENCY_VERSION");

        // Installed mods that depend on something being replaced.
        foreach (var m in installed)
        {
            if (m.Descriptor is null || requested.Contains(m.ModReference)) continue;
            foreach (var dep in m.Descriptor.Plugins)
            {
                if (!requested.Contains(dep.Name) || dep.SemVersionRange is null) continue;
                if (!VersionRange.TryParse(dep.SemVersionRange, out var range, out _)) continue;
                var newVersion = after[dep.Name].EffectiveVersion;
                if (!range!.Matches(newVersion))
                    issues.Add(new(IssueLevel.NeedsConfirmation, "BREAKS_DEPENDENT", dep.Name,
                        $"Installed mod {m.ModReference} requires {dep.Name} {dep.SemVersionRange}, but this would install {newVersion}. SML will stop the game at startup."));
            }
        }
    }

    private static void CheckDependenciesOf(UPluginDescriptor d, Dictionary<string, UPluginDescriptor> after, List<InstallIssue> issues,
        string missingCode, string versionCode)
    {
        foreach (var dep in d.Plugins)
        {
            // Entries without a SemVersion are usually engine or game plugins; SML does not version-check them.
            if (dep.SemVersionRange is null) continue;
            if (!VersionRange.TryParse(dep.SemVersionRange, out var range, out var err))
            {
                issues.Add(new(IssueLevel.Warning, "BAD_DEPENDENCY_RANGE", d.ModReference,
                    $"{d.ModReference} has an invalid version range '{dep.SemVersionRange}' for {dep.Name}: {err}"));
                continue;
            }
            if (!after.TryGetValue(dep.Name, out var depDescriptor))
            {
                if (dep.Optional) continue; // SML skips missing optional dependencies
                issues.Add(new(IssueLevel.NeedsConfirmation, missingCode, d.ModReference,
                    $"{d.ModReference} requires {dep.Name} {dep.SemVersionRange}, which is not installed. The game will not load {d.ModReference} without it; install it through SMM or this tool."));
                continue;
            }
            var v = depDescriptor.EffectiveVersion;
            if (!range!.Matches(v))
                issues.Add(new(IssueLevel.NeedsConfirmation, versionCode, d.ModReference,
                    $"{d.ModReference} requires {dep.Name} {dep.SemVersionRange}, but {v} is installed. SML will stop the game at startup."));
        }
    }
}
