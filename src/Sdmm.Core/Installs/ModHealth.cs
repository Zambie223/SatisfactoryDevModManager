using Sdmm.Core.Smm;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Installs;

/// <param name="Code">Stable identifier, for example "DEPENDENCY_MISSING".</param>
/// <param name="Short">A few words for the mod list.</param>
/// <param name="Message">The full explanation for the details panel.</param>
public sealed record ModProblem(string Code, string Short, string Message);

/// <summary>
/// Problems with mods that are already installed, found from what's on disk: things that stop the game loading a
/// mod, or that will undo a dev build. The install planner checks the same rules for what is about to be installed;
/// this shows them for what is there now, so they're visible before anyone launches the game.
/// </summary>
public static class ModHealth
{
    /// <param name="changelist">The install's game build, 0 if unknown (game version checks are then skipped, as SML does).</param>
    /// <param name="smm">SMM's settings, to warn when SMM will replace a dev build. Null for servers or when SMM isn't found.</param>
    /// <param name="localRoot">The local install folder, for matching SMM's install list; null for remote installs.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<ModProblem>> Check(IReadOnlyList<InstalledMod> installed, long changelist,
        SmmState? smm = null, string? localRoot = null, IInstallFileSystem? fs = null)
    {
        var result = new Dictionary<string, List<ModProblem>>(StringComparer.OrdinalIgnoreCase);
        void Add(string modRef, ModProblem p)
        {
            if (!result.TryGetValue(modRef, out var list)) result[modRef] = list = [];
            if (list.All(x => x.Code != p.Code || x.Message != p.Message)) list.Add(p);
        }

        var byRef = installed.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToList();
        var descriptors = installed.Where(m => m.Descriptor is not null)
            .GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Descriptor!, StringComparer.OrdinalIgnoreCase);

        foreach (var g in byRef)
        {
            var copies = g.ToList();
            var modRef = g.Key;

            if (copies.Count > 1)
                Add(modRef, new("DUPLICATE", "Installed twice",
                    $"{modRef} is installed more than once ({string.Join(" and ", copies.Select(c => c.Folder))}). " +
                    "The game can only load one copy; remove the extra one where it was installed."));

            foreach (var m in copies.Where(m => m.Descriptor is null))
                Add(modRef, new("BROKEN", "Broken copy",
                    $"{m.Folder}: {m.DescriptorError ?? "the .uplugin can't be read"}. The game won't load it; reinstall it."));

            var d = copies.Select(c => c.Descriptor).FirstOrDefault(x => x is not null);
            if (d is null) continue;

            if (d.GameVersionText is { } gv && changelist != 0 && VersionRange.TryParse(gv, out var gameRange, out _) &&
                !gameRange!.Matches(new SemVersion(changelist, 0, 0)))
                Add(modRef, new("GAME_VERSION", "Wrong game version",
                    $"{modRef} requires game version {gv}, but this install is build {changelist}. SML will stop the game at startup."));

            foreach (var dep in d.Plugins)
            {
                // Entries without a SemVersion are usually engine or game plugins, which SML doesn't version-check.
                if (dep.SemVersionRange is null || !dep.Enabled) continue;
                if (!VersionRange.TryParse(dep.SemVersionRange, out var range, out _)) continue;
                if (!descriptors.TryGetValue(dep.Name, out var depD))
                {
                    if (!dep.Optional)
                        Add(modRef, new("DEPENDENCY_MISSING", $"Needs {dep.Name}",
                            $"{modRef} requires {dep.Name} {dep.SemVersionRange}, which isn't installed. The game won't load {modRef} without it."));
                    continue;
                }
                if (!range!.Matches(depD.EffectiveVersion))
                    Add(modRef, new("DEPENDENCY_VERSION", $"Needs {dep.Name} {dep.SemVersionRange}",
                        $"{modRef} requires {dep.Name} {dep.SemVersionRange}, but {depD.EffectiveVersion} is installed. SML will stop the game at startup."));
            }

            // A dev build whose mod is also in the SMM profile for this install gets replaced on SMM's next apply.
            if (smm is not null && localRoot is not null && fs is not null && copies.Any(c => c.Owner == ModOwner.Sdmm))
                foreach (var c in smm.FindConflicts(localRoot, fs, modRef))
                    Add(modRef, new("SMM_WILL_REPLACE", "SMM will replace it", c.Reason));
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ModProblem>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
