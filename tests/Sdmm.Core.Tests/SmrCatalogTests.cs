using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smr;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class SmrResolverTests
{
    private static SmrVersion V(string modRef, string version, string? game = ">=500000", bool approved = true, string stability = "release",
        ModTarget[]? targets = null, params (string Ref, string Cond, bool Optional)[] deps) =>
        new($"{modRef}-{version}", modRef, SemVersion.Parse(version), game, approved, stability, true, null, null,
            (targets ?? [ModTarget.Windows, ModTarget.LinuxServer]).ToDictionary(t => t, t => new SmrTarget(t, $"/v1/version/{modRef}{version}/{t}/download", 1, null)),
            deps.Select(d => new SmrDependency(d.Ref, d.Cond, d.Optional)).ToList());

    private static InstalledMod Installed(string modRef, string version, ModOwner owner = ModOwner.Sdmm) =>
        new(modRef, $"FactoryGame/Mods/{modRef}", false, owner, UPluginDescriptor.Parse(modRef, $$"""{ "SemVersion": "{{version}}" }"""), null, null);

    private static Func<IReadOnlyCollection<string>, Task<IReadOnlyDictionary<string, IReadOnlyList<SmrVersion>>>> Catalog(params SmrVersion[] all) =>
        refs => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SmrVersion>>>(
            all.Where(v => refs.Contains(v.ModReference, StringComparer.OrdinalIgnoreCase))
               .GroupBy(v => v.ModReference, StringComparer.OrdinalIgnoreCase)
               .ToDictionary(g => g.Key, g => (IReadOnlyList<SmrVersion>)g.OrderByDescending(v => v.Version).ToList(), StringComparer.OrdinalIgnoreCase));

    private static Task<SmrInstallPlan> Resolve(IReadOnlyList<InstalledMod> installed, params SmrVersion[] catalog) =>
        SmrResolver.ResolveAsync([new SmrRequest("ModA")], ModTarget.Windows, 517554, installed, Catalog(catalog));

    [Fact]
    public async Task InstallsTheNewestReleaseAndAMissingDependency()
    {
        var plan = await Resolve([],
            V("ModA", "1.0.0", deps: ("SML", "^3.12.0", false)),
            V("ModA", "1.1.0", deps: ("SML", "^3.12.0", false)),
            V("ModA", "1.2.0-beta.1", stability: "beta", deps: ("SML", "^3.12.0", false)),
            V("SML", "3.11.0"), V("SML", "3.12.0"));

        Assert.True(plan.CanInstall);
        Assert.Equal(["ModA 1.1.0", "SML 3.12.0"], plan.ToInstall.Select(v => $"{v.ModReference} {v.Version}"));
    }

    [Fact]
    public async Task KeepsADependencyThatAlreadyFits()
    {
        var plan = await Resolve([Installed("SML", "3.12.1", ModOwner.Smm)],
            V("ModA", "1.0.0", deps: ("SML", "^3.12.0", false)), V("SML", "3.13.0"));

        Assert.Equal(["ModA"], plan.ToInstall.Select(v => v.ModReference));
        Assert.Empty(plan.TakeOver);
    }

    [Fact]
    public async Task ADependencyFromSmmThatIsTooOldMustBeTakenOver()
    {
        var plan = await Resolve([Installed("SML", "3.11.0", ModOwner.Smm)],
            V("ModA", "1.0.0", deps: ("SML", "^3.12.0", false)), V("SML", "3.12.0"));

        Assert.Contains(plan.ToInstall, v => v.ModReference == "SML" && v.Version == SemVersion.Parse("3.12.0"));
        Assert.Equal(["SML"], plan.TakeOver);
    }

    [Fact]
    public async Task SkipsVersionsThatDontFitThePlatformGameOrApproval()
    {
        var plan = await Resolve([],
            V("ModA", "1.3.0", approved: false),
            V("ModA", "1.2.0", targets: [ModTarget.LinuxServer]),
            V("ModA", "1.1.0", game: ">=999999"),
            V("ModA", "1.0.0"));

        Assert.Equal(SemVersion.Parse("1.0.0"), plan.ToInstall.Single().Version);
    }

    [Fact]
    public async Task ReportsWhatCantBeSatisfied()
    {
        var plan = await Resolve([], V("ModA", "1.0.0", deps: ("Lib", "^2.0.0", false)), V("Lib", "1.0.0"));
        Assert.False(plan.CanInstall);
        Assert.Contains(plan.Problems, p => p.Contains("Lib"));

        var missing = await Resolve([], V("ModA", "1.0.0", deps: ("Ghost", "^1.0.0", false)));
        Assert.Contains(missing.Problems, p => p.Contains("Ghost") && p.Contains("isn't on SMR"));
    }

    [Fact]
    public async Task ARequirementFoundLaterPicksADependencyAgain()
    {
        // ModA wants Lib ^1.0.0 (newest 1.5.0); B, also requested, wants Lib 1.2.x. (Not ~1.2.0: in SML that means 1.2.0 only.)
        var plan = await SmrResolver.ResolveAsync([new SmrRequest("ModA"), new SmrRequest("B")], ModTarget.Windows, 0, [],
            Catalog(V("ModA", "1.0.0", deps: ("Lib", "^1.0.0", false)), V("B", "1.0.0", deps: ("Lib", ">=1.2.0 <1.3.0", false)),
                V("Lib", "1.2.3"), V("Lib", "1.5.0")));

        Assert.True(plan.CanInstall, string.Join("; ", plan.Problems));
        Assert.Equal(SemVersion.Parse("1.2.3"), plan.ToInstall.Single(v => v.ModReference == "Lib").Version);
    }

    [Fact]
    public async Task OptionalDependenciesAreSkippedAndAModInstalledByHandIsTakenOver()
    {
        var plan = await Resolve([Installed("ModA", "0.9.0", ModOwner.Unmanaged)],
            V("ModA", "1.0.0", deps: ("Extra", "^1.0.0", true)));

        Assert.Equal(["ModA"], plan.ToInstall.Select(v => v.ModReference));
        Assert.Equal(["ModA"], plan.TakeOver);
    }

    [Fact]
    public async Task APinnedVersionIsUsedEvenIfNewerExist()
    {
        var plan = await SmrResolver.ResolveAsync([new SmrRequest("ModA", SemVersion.Parse("1.0.0"))], ModTarget.Windows, 0, [],
            Catalog(V("ModA", "1.0.0"), V("ModA", "2.0.0")));
        Assert.Equal(SemVersion.Parse("1.0.0"), plan.ToInstall.Single().Version);
    }
}

public class SmrParsingTests
{
    [Fact]
    public void ParsesVersionsWithTargetsAndDependencies()
    {
        const string json = """
            { "data": { "m0": { "mod_reference": "AutoTrains", "versions": [
              { "id": "a", "version": "1.4.4", "game_version": ">=513885", "approved": true, "stability": "release", "required_on_remote": true,
                "targets": [ { "targetName": "Windows", "link": "/v1/version/a/Windows/download", "size": 10, "hash": "ab" },
                             { "targetName": "Unknown", "link": "/x" } ],
                "dependencies": [ { "mod_reference": "SML", "condition": "^3.12.0", "optional": false } ] },
              { "id": "b", "version": "1.4.5", "approved": true, "stability": "release", "required_on_remote": false, "targets": [], "dependencies": [] },
              { "id": "c", "version": "not-a-version" } ] } } }
            """;

        var (modRef, versions) = SmrClient.ParseVersions(json, 1).Single();

        Assert.Equal("AutoTrains", modRef);
        Assert.Equal(["1.4.5", "1.4.4"], versions.Select(v => v.Version.ToString())); // newest first, bad one skipped
        var v = versions[1];
        Assert.Equal(["Windows"], v.Targets.Keys.Select(k => k.ToString()));
        Assert.Equal("ab", v.Targets[ModTarget.Windows].Sha256);
        Assert.Equal(new SmrDependency("SML", "^3.12.0", false), v.Dependencies.Single());
        Assert.False(versions[0].RequiredOnRemote);
    }

    [Fact]
    public void ParsesSearchResults()
    {
        const string json = """
            { "data": { "getMods": { "count": 42, "mods": [ { "id": "x1", "name": "Auto Trains", "mod_reference": "AutoTrains", "downloads": 5, "authors": [] } ] } } }
            """;
        var r = SmrClient.ParseSearch(json);
        Assert.Equal(42, r.Total);
        Assert.Equal("AutoTrains", r.Mods.Single().ModReference);
    }

    [Fact]
    public void AnErrorAnswerBecomesAnSmrException() =>
        Assert.Throws<SmrException>(() => SmrClient.ParseSearch("""{ "errors": [ { "message": "boom" } ] }"""));
}
