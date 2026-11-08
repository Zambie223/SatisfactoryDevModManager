using Sdmm.Core.Discovery;
using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smr;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class DependencyFixerTests
{
    private static InstalledMod Mod(string modRef, string version, ModOwner owner = ModOwner.Sdmm, params (string Name, string Range)[] deps) =>
        new(modRef, $"FactoryGame/Mods/{modRef}", false, owner,
            UPluginDescriptor.Parse(modRef, $$"""{ "SemVersion": "{{version}}", "Plugins": [{{string.Join(",", deps.Select(d => $$"""{ "Name": "{{d.Name}}", "SemVersion": "{{d.Range}}" }"""))}}] }"""),
            null, null);

    private static ModBuild Build(string modRef, string version, bool prerelease = false, params ModTarget[] targets) =>
        new(modRef, "me", modRef, "v" + version, SemVersion.Parse(version), prerelease, null, null, null,
            (targets.Length == 0 ? [ModTarget.Windows] : targets).ToDictionary(t => t, t => new GitHub.GitHubAsset { Id = 1, Name = $"{modRef}-{t}.zip", Size = 1 }));

    [Fact]
    public void FindsMissingAndTooOldDependenciesWithEveryRequirementOnThem()
    {
        var installed = new[]
        {
            Mod("SML", "3.11.0", ModOwner.Smm),
            Mod("ModA", "1.0.0", deps: [("SML", "^3.12.0"), ("Lib", "^1.0.0")]),
            Mod("ModB", "1.0.0", deps: [("SML", ">=3.0.0")]),
        };

        var needs = DependencyFixer.FindUnmet(installed);

        Assert.Equal(["Lib", "SML"], needs.Select(n => n.ModReference));
        var sml = needs.Single(n => n.ModReference == "SML");
        // ModB's requirement is met, but it's still listed so the fix doesn't break ModB.
        Assert.Equal(["ModA", "ModB"], sml.Requirements.Select(r => r.NeededBy).Order());
        Assert.NotNull(sml.Installed);
        Assert.Null(needs.Single(n => n.ModReference == "Lib").Installed);
    }

    [Fact]
    public void OnlyCountsTheModsAskedAbout()
    {
        var installed = new[] { Mod("Other", "1.0.0", ModOwner.Smm, ("Lib", "^1.0.0")), Mod("ModA", "1.0.0") };
        Assert.Empty(DependencyFixer.FindUnmet(installed, ["ModA"]));
        Assert.Single(DependencyFixer.FindUnmet(installed));
    }

    [Fact]
    public void PicksTheNewestFittingBuildFromYourSourcesElseSmrElseExplains()
    {
        var needs = DependencyFixer.FindUnmet([
            Mod("ModA", "1.0.0", deps: [("Lib", "^1.0.0"), ("SML", "^3.12.0"), ("Old", "^2.0.0")]),
        ]);
        var sources = new List<DiscoveredMod>
        {
            new("Lib", [Build("Lib", "2.0.0"), Build("Lib", "1.3.0-dev.1", prerelease: true), Build("Lib", "1.2.0"), Build("Lib", "1.1.0")]),
            new("Old", [Build("Old", "1.0.0")]),
        };

        var standard = DependencyFixer.Plan(needs, sources, ModTarget.Windows, includePrereleases: false, smrAllowed: false);
        Assert.Equal("v1.2.0", standard.Single(f => f.Need.ModReference == "Lib").Build?.Tag); // not 2.0.0 (out of range), not the prerelease
        Assert.False(standard.Single(f => f.Need.ModReference == "SML").CanFix);
        Assert.Contains("SMM", standard.Single(f => f.Need.ModReference == "SML").WhyNot);
        Assert.False(standard.Single(f => f.Need.ModReference == "Old").CanFix);

        var withPre = DependencyFixer.Plan(needs, sources, ModTarget.Windows, includePrereleases: true, smrAllowed: false);
        Assert.Equal("v1.3.0-dev.1", withPre.Single(f => f.Need.ModReference == "Lib").Build?.Tag);

        var withSmr = DependencyFixer.Plan(needs, sources, ModTarget.Windows, includePrereleases: false, smrAllowed: true);
        Assert.True(withSmr.Single(f => f.Need.ModReference == "SML").FromSmr);
        Assert.True(withSmr.Single(f => f.Need.ModReference == "Old").FromSmr); // no fitting dev build: SMR instead
        Assert.NotNull(withSmr.Single(f => f.Need.ModReference == "Lib").Build);
    }

    [Fact]
    public void ABuildWithoutThisPlatformDoesNotCount()
    {
        var needs = DependencyFixer.FindUnmet([Mod("ModA", "1.0.0", deps: [("Lib", "^1.0.0")])]);
        var sources = new List<DiscoveredMod> { new("Lib", [Build("Lib", "1.0.0", false, ModTarget.Windows)]) };
        Assert.False(DependencyFixer.Plan(needs, sources, ModTarget.LinuxServer, false, false).Single().CanFix);
    }

    [Fact]
    public void AnotherToolsCopyIsOnlyReplacedThroughSmr()
    {
        var needs = DependencyFixer.FindUnmet([Mod("SML", "3.11.0", ModOwner.Smm), Mod("ModA", "1.0.0", deps: [("SML", "^3.12.0")])]);
        var sources = new List<DiscoveredMod> { new("SML", [Build("SML", "3.12.0")]) };

        var standard = DependencyFixer.Plan(needs, sources, ModTarget.Windows, false, smrAllowed: false).Single();
        Assert.False(standard.CanFix);
        Assert.Contains("SMM", standard.WhyNot);
        Assert.True(DependencyFixer.Plan(needs, sources, ModTarget.Windows, false, smrAllowed: true).Single().FromSmr);
    }

    [Fact]
    public async Task SmrPicksOneVersionThatMeetsEveryRequestForTheSameMod()
    {
        static SmrVersion V(string version) =>
            new($"SML-{version}", "SML", SemVersion.Parse(version), null, true, "release", true, null, null,
                new Dictionary<ModTarget, SmrTarget> { [ModTarget.Windows] = new(ModTarget.Windows, "/x", 1, null) }, []);
        var catalog = new Dictionary<string, IReadOnlyList<SmrVersion>>(StringComparer.OrdinalIgnoreCase) { ["SML"] = [V("3.13.0"), V("3.12.2"), V("3.11.0")] };

        var plan = await SmrResolver.ResolveAsync(
            [new SmrRequest("SML", null, VersionRange.Parse("^3.12.0"), "ModA"), new SmrRequest("SML", null, VersionRange.Parse("<3.13.0"), "ModB")],
            ModTarget.Windows, 0, [], _ => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SmrVersion>>>(catalog));

        Assert.True(plan.CanInstall);
        Assert.Equal("3.12.2", plan.ToInstall.Single().Version.ToString());

        var impossible = await SmrResolver.ResolveAsync(
            [new SmrRequest("SML", null, VersionRange.Parse("^3.13.0"), "ModA"), new SmrRequest("SML", null, VersionRange.Parse("<3.12.0"), "ModB")],
            ModTarget.Windows, 0, [], _ => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SmrVersion>>>(catalog));
        Assert.Contains(impossible.Problems, p => p.Contains("ModA") && p.Contains("ModB"));
    }
}
