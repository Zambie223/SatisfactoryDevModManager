using Sdmm.Core.Discovery;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Installing;

/// <summary>One installed mod's requirement on another: "<see cref="NeededBy"/> needs <see cref="RangeText"/>".</summary>
public sealed record DependencyRequirement(string NeededBy, VersionRange Range, string RangeText);

/// <summary>
/// A required dependency that's missing or the wrong version.
/// </summary>
/// <param name="Requirements">
/// Every installed mod's requirement on it, including ones that are already met, so a fix satisfies all of them
/// (installing the newest SML for one mod mustn't break another that needs an older one).
/// </param>
/// <param name="Installed">The copy that's there now (wrong version or unreadable), or null when it's missing.</param>
public sealed record DependencyNeed(string ModReference, IReadOnlyList<DependencyRequirement> Requirements, InstalledMod? Installed)
{
    public bool IsSatisfiedBy(SemVersion v) => Requirements.All(r => r.Range.Matches(v));

    /// <summary>"SML ^3.12.0 (for ExampleMod)", with every requirement.</summary>
    public string Describe() =>
        $"{ModReference} {string.Join(" and ", Requirements.Select(r => $"{r.RangeText} (for {r.NeededBy})"))}";
}

/// <summary>Where one missing dependency will come from. Exactly one of <see cref="Build"/>, <see cref="FromSmr"/> and <see cref="WhyNot"/> applies.</summary>
public sealed record DependencyFix(DependencyNeed Need, ModBuild? Build, bool FromSmr, string? WhyNot)
{
    public bool CanFix => Build is not null || FromSmr;
}

/// <summary>
/// Finds installed mods' required dependencies that are missing or don't fit, and picks where to install each from,
/// following the same rule as single installs: a mod in your GitHub sources (or imported) comes from there; anything
/// else from SMR, where that's allowed. The SMR side's own dependencies are resolved by
/// <see cref="Smr.SmrResolver"/> afterwards.
/// </summary>
public static class DependencyFixer
{
    /// <summary>The same rules as ModHealth's DEPENDENCY_MISSING / DEPENDENCY_VERSION: enabled, non-optional, versioned entries.</summary>
    /// <param name="neededBy">Only dependencies of these mods (for example, the mods the list shows); null for all.</param>
    public static IReadOnlyList<DependencyNeed> FindUnmet(IReadOnlyList<InstalledMod> installed, IEnumerable<string>? neededBy = null)
    {
        var only = neededBy is null ? null : new HashSet<string>(neededBy, StringComparer.OrdinalIgnoreCase);
        var descriptors = installed.Where(m => m.Descriptor is not null)
            .GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Descriptor!, StringComparer.OrdinalIgnoreCase);

        // Every requirement on every dependency, then keep the dependencies where at least one of the chosen mods' isn't met.
        var all = new Dictionary<string, List<DependencyRequirement>>(StringComparer.OrdinalIgnoreCase);
        var unmet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modRef, d) in descriptors)
            foreach (var dep in d.Plugins)
            {
                if (dep.SemVersionRange is null || !dep.Enabled || dep.Optional) continue;
                if (!VersionRange.TryParse(dep.SemVersionRange, out var range, out _)) continue;
                if (!all.TryGetValue(dep.Name, out var list)) all[dep.Name] = list = [];
                list.Add(new DependencyRequirement(modRef, range!, dep.SemVersionRange));
                bool met = descriptors.TryGetValue(dep.Name, out var depD) && range!.Matches(depD.EffectiveVersion);
                if (!met && (only is null || only.Contains(modRef))) unmet.Add(dep.Name);
            }

        return unmet.Order(StringComparer.OrdinalIgnoreCase)
            .Select(r => new DependencyNeed(r, all[r],
                installed.FirstOrDefault(m => m.ModReference.Equals(r, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <param name="sources">Mods in your GitHub sources and imported zips.</param>
    /// <param name="smrAllowed">Whether SMR may be used.</param>
    public static IReadOnlyList<DependencyFix> Plan(IReadOnlyList<DependencyNeed> needs, IReadOnlyList<DiscoveredMod> sources,
        ModTarget target, bool includePrereleases, bool smrAllowed)
    {
        var fixes = new List<DependencyFix>();
        foreach (var need in needs)
        {
            var wants = string.Join(" and ", need.Requirements.Select(r => r.RangeText).Distinct());
            // SDMM only replaces its own folders. Another tool's copy can only be replaced by taking it over, which the
            // SMR install does when allowed; otherwise it has to be updated where it was installed.
            if (need.Installed is { Owner: not ModOwner.Sdmm } other)
            {
                var who = other.Owner == ModOwner.Smm ? "SMM" : "hand (or by the host)";
                var have = other.Descriptor?.EffectiveVersion.ToString() ?? "an unreadable copy";
                fixes.Add(smrAllowed
                    ? new DependencyFix(need, null, true, null)
                    : new DependencyFix(need, null, false, $"{have} is installed by {who}; update it there to {wants}."));
                continue;
            }

            var discovered = sources.FirstOrDefault(m => m.ModReference.Equals(need.ModReference, StringComparison.OrdinalIgnoreCase));
            if (discovered is not null)
            {
                var fitting = discovered.Builds
                    .Where(b => b.Version is { } v && need.IsSatisfiedBy(v) && b.Assets.ContainsKey(target)).ToList();
                // Builds are newest first. Prereleases only when they're shown, or when no release fits.
                var build = fitting.FirstOrDefault(b => includePrereleases || !b.Prerelease) ?? fitting.FirstOrDefault();
                if (build is not null)
                {
                    fixes.Add(new DependencyFix(need, build, false, null));
                    continue;
                }
                fixes.Add(smrAllowed
                    ? new DependencyFix(need, null, true, null)
                    : new DependencyFix(need, null, false, $"no {target} build in your sources is {wants}."));
                continue;
            }

            fixes.Add(smrAllowed
                ? new DependencyFix(need, null, true, null)
                : new DependencyFix(need, null, false, "it isn't in your GitHub sources. Install it with SMM (from ficsit.app), or add the repo that publishes it."));
        }
        return fixes;
    }
}
