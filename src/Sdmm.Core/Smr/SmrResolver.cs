using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Smr;

/// <summary>What installing from SMR involves.</summary>
/// <param name="ToInstall">SMR versions to install: the requested mods plus any dependencies that are missing or too old.</param>
/// <param name="TakeOver">Mods in <paramref name="ToInstall"/> whose installed copy SMM, a host or a hand copy put there; they must be taken over first.</param>
/// <param name="Problems">Reasons it can't be done. Nothing should be installed while there are any.</param>
public sealed record SmrInstallPlan(IReadOnlyList<SmrVersion> ToInstall, IReadOnlyList<string> TakeOver, IReadOnlyList<string> Problems)
{
    public bool CanInstall => Problems.Count == 0 && ToInstall.Count > 0;
}

/// <summary>
/// One mod to install from SMR: a specific version, or (when <paramref name="Version"/> is null) the newest that fits,
/// limited to <paramref name="Range"/> when given (for example an SMM profile's "&gt;=1.2.0"). The same mod can be requested
/// more than once with different ranges (several mods needing one dependency); the version picked meets all of them.
/// </summary>
/// <param name="For">Who wants <paramref name="Range"/>, for messages ("the modpack" when not given).</param>
public sealed record SmrRequest(string ModReference, SemVersion? Version = null, VersionRange? Range = null, string? For = null);

/// <summary>
/// Works out which SMR versions to install for a request, including dependencies. A simpler cousin of SMM's resolver:
/// <list type="bullet">
/// <item>Only approved versions with a build for the install's platform, and (when the game build is known) a
/// GameVersion that fits it, are used.</item>
/// <item>For mods not pinned to a version, the newest release that satisfies every requirement seen so far is
/// picked; betas and alphas only when no release fits.</item>
/// <item>An installed dependency that already satisfies its requirement is kept. One that doesn't is updated from
/// SMR; if SMM or something else installed it, it has to be taken over first.</item>
/// <item>Requirements that can't all be met are reported instead of guessed at. There's no backtracking: picking an
/// older version of one mod to satisfy another isn't attempted beyond the version ranges themselves.</item>
/// </list>
/// Optional dependencies are skipped, as SML does when they're missing.
/// </summary>
public static class SmrResolver
{
    /// <summary>
    /// Whether a version can be installed here: approved on SMR, built for <paramref name="target"/>, and (when the
    /// game build is known) made for this game version. <paramref name="why"/> says what's wrong otherwise.
    /// </summary>
    public static bool Fits(SmrVersion v, ModTarget target, long changelist, out string why)
    {
        if (!v.Approved) { why = "not approved on SMR yet"; return false; }
        if (!v.Targets.ContainsKey(target)) { why = $"has no {target} build"; return false; }
        if (changelist != 0 && v.GameVersion is { } gv && VersionRange.TryParse(gv, out var range, out _) &&
            !range!.Matches(new SemVersion(changelist, 0, 0)))
        {
            why = $"needs game version {gv} (this install is build {changelist})";
            return false;
        }
        why = "";
        return true;
    }

    public static async Task<SmrInstallPlan> ResolveAsync(
        IReadOnlyList<SmrRequest> requests,
        ModTarget target,
        long changelist,
        IReadOnlyList<InstalledMod> installed,
        Func<IReadOnlyCollection<string>, Task<IReadOnlyDictionary<string, IReadOnlyList<SmrVersion>>>> fetchVersions)
    {
        var problems = new List<string>();
        var versions = new Dictionary<string, IReadOnlyList<SmrVersion>>(StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<string, SmrVersion>(StringComparer.OrdinalIgnoreCase);
        var constraints = new Dictionary<string, List<(VersionRange Range, string From)>>(StringComparer.OrdinalIgnoreCase);
        var installedByRef = installed.Where(m => m.Descriptor is not null)
            .GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        async Task Fetch(IEnumerable<string> refs)
        {
            var missing = refs.Where(r => !versions.ContainsKey(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0) return;
            var got = await fetchVersions(missing);
            foreach (var r in missing) versions[r] = got.TryGetValue(r, out var list) ? list : [];
        }

        bool Fits(SmrVersion v, out string why) => SmrResolver.Fits(v, target, changelist, out why);

        bool MeetsAll(string modRef, SemVersion v) =>
            !constraints.TryGetValue(modRef, out var list) || list.All(c => c.Range.Matches(v));

        SmrVersion? PickNewest(string modRef) =>
            versions[modRef].Where(v => Fits(v, out _) && MeetsAll(modRef, v.Version)).OrderByDescending(v => v.IsRelease).ThenByDescending(v => v.Version).FirstOrDefault();

        string Wants(string modRef) =>
            constraints.TryGetValue(modRef, out var list) && list.Count > 0 ? string.Join(" and ", list.Select(c => $"{c.Range} (for {c.From})")) : "any version";

        // The requested mods. Every range asked for counts, then each mod is picked once (a pinned version wins).
        await Fetch(requests.Select(r => r.ModReference));
        foreach (var req in requests.Where(r => r.Version is null && r.Range is not null))
        {
            if (!constraints.TryGetValue(req.ModReference, out var own)) constraints[req.ModReference] = own = [];
            own.Add((req.Range!, req.For ?? "the modpack"));
        }
        requests = requests.GroupBy(r => r.ModReference, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.FirstOrDefault(r => r.Version is not null) ?? g.First()).ToList();
        var queue = new Queue<SmrVersion>();
        foreach (var req in requests)
        {
            var all = versions[req.ModReference];
            if (all.Count == 0)
            {
                problems.Add($"{req.ModReference} isn't on SMR.");
                continue;
            }
            SmrVersion? pick;
            if (req.Version is { } wanted)
            {
                pick = all.FirstOrDefault(v => v.Version == wanted);
                if (pick is null) { problems.Add($"{req.ModReference} {wanted} isn't on SMR."); continue; }
                if (!Fits(pick, out var why)) { problems.Add($"{req.ModReference} {wanted} {why}."); continue; }
            }
            else
            {
                pick = PickNewest(req.ModReference);
                if (pick is null)
                {
                    var newest = all[0];
                    problems.Add(Fits(newest, out var why)
                        ? $"No version of {req.ModReference} on SMR satisfies {Wants(req.ModReference)}."
                        : $"No version of {req.ModReference} fits this install (the newest, {newest.Version}, {why}).");
                    continue;
                }
            }
            chosen[req.ModReference] = pick;
            queue.Enqueue(pick);
        }

        // Dependencies, breadth-first. A mod can be revisited when a later requirement rules out the version picked.
        var guard = 0;
        while (queue.Count > 0 && guard++ < 500)
        {
            var v = queue.Dequeue();
            if (!chosen.TryGetValue(v.ModReference, out var current) || current != v) continue; // replaced meanwhile
            var deps = v.Dependencies.Where(d => !d.Optional).ToList();
            await Fetch(deps.Select(d => d.ModReference));
            foreach (var dep in deps)
            {
                if (!VersionRange.TryParse(dep.Condition, out var range, out _))
                {
                    problems.Add($"{v.ModReference} {v.Version} has an unreadable requirement '{dep.Condition}' for {dep.ModReference}.");
                    continue;
                }
                if (!constraints.TryGetValue(dep.ModReference, out var list)) constraints[dep.ModReference] = list = [];
                list.Add((range!, $"{v.ModReference} {v.Version}"));

                if (chosen.TryGetValue(dep.ModReference, out var picked))
                {
                    if (range!.Matches(picked.Version)) continue;
                    if (requests.Any(r => r.ModReference.Equals(dep.ModReference, StringComparison.OrdinalIgnoreCase) && r.Version is not null))
                    {
                        problems.Add($"{v.ModReference} {v.Version} needs {dep.ModReference} {dep.Condition}, but {picked.Version} was chosen.");
                        continue;
                    }
                    // Pick again with every requirement so far.
                    var again = versions[dep.ModReference].Count > 0 ? PickNewest(dep.ModReference) : null;
                    if (again is null)
                    {
                        problems.Add($"No version of {dep.ModReference} satisfies {Wants(dep.ModReference)}.");
                        continue;
                    }
                    chosen[dep.ModReference] = again;
                    queue.Enqueue(again);
                    continue;
                }

                if (installedByRef.TryGetValue(dep.ModReference, out var inst) && MeetsAll(dep.ModReference, inst.Descriptor!.EffectiveVersion))
                    continue; // what's installed already does

                if (versions[dep.ModReference].Count == 0)
                {
                    problems.Add(inst is null
                        ? $"{v.ModReference} needs {dep.ModReference} {dep.Condition}, which isn't installed and isn't on SMR."
                        : $"{v.ModReference} needs {dep.ModReference} {dep.Condition}, but {inst.Descriptor!.EffectiveVersion} is installed and SMR doesn't have it.");
                    continue;
                }
                var pick = PickNewest(dep.ModReference);
                if (pick is null)
                {
                    problems.Add($"No version of {dep.ModReference} on SMR satisfies {Wants(dep.ModReference)} and fits this install.");
                    continue;
                }
                chosen[dep.ModReference] = pick;
                queue.Enqueue(pick);
            }
        }

        // Anything chosen must still meet every requirement collected (a later pick can break an earlier one).
        foreach (var (modRef, v) in chosen)
            if (!MeetsAll(modRef, v.Version))
                problems.Add($"{modRef} {v.Version} doesn't satisfy {Wants(modRef)}.");

        var toInstall = chosen.Values
            .Where(v => !(installedByRef.TryGetValue(v.ModReference, out var i) && i.Descriptor!.EffectiveVersion == v.Version && i.Owner == ModOwner.Sdmm &&
                          !requests.Any(r => r.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(v => v.ModReference, StringComparer.OrdinalIgnoreCase).ToList();
        var takeOver = toInstall
            .Where(v => installed.Any(m => m.ModReference.Equals(v.ModReference, StringComparison.OrdinalIgnoreCase) && m.Owner != ModOwner.Sdmm))
            .Select(v => v.ModReference).ToList();
        return new SmrInstallPlan(toInstall, takeOver, problems.Distinct().ToList());
    }
}
