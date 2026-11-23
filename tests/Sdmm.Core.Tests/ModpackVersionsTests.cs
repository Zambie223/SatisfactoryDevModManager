using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Installs;
using Sdmm.Core.Modpacks;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smr;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class ModpackVersionsTests
{
    private static ModBuild B(string version, string repo = "me/M", bool pre = false, ModTarget target = ModTarget.Windows)
    {
        var parts = repo.Split('/');
        return new ModBuild("M", parts[0], parts[1], "v" + version, SemVersion.Parse(version), pre, null, null, null,
            new Dictionary<ModTarget, GitHubAsset> { [target] = new() { Id = 1, Name = $"M-{target}.zip" } });
    }

    private static ModpackMod Entry(string? version, string? constraint = null) =>
        new() { ModReference = "M", Version = version, Constraint = constraint, Source = ModpackSource.GitHub, Repo = "me/M" };

    private static readonly DiscoveredMod Mod = new("M",
    [
        B("2.0.0-beta.1", pre: true), B("1.2.0"), B("1.1.0"), B("1.0.0"),
        B("9.0.0", repo: "someone/M"),                      // a fork with the same mod reference
        B("3.0.0", target: ModTarget.LinuxServer),          // no Windows build
    ]);

    [Fact]
    public void CandidatesComeFromThePacksRepoAndPlatform()
    {
        var c = ModpackVersions.Candidates(Mod, Entry("1.0.0"), ModTarget.Windows);
        Assert.Equal(["2.0.0-beta.1", "1.2.0", "1.1.0", "1.0.0"], c.Select(b => b.Version!.ToString()));
        Assert.Empty(ModpackVersions.Candidates(null, Entry("1.0.0"), ModTarget.Windows));
    }

    [Fact]
    public void NewerFollowsThePreReleaseSetting()
    {
        var c = ModpackVersions.Candidates(Mod, Entry("1.0.0"), ModTarget.Windows);
        Assert.Equal("1.2.0", ModpackVersions.Newer(c, Entry("1.0.0"), includePrereleases: false)!.Newest.ToString());
        Assert.Equal("2.0.0-beta.1", ModpackVersions.Newer(c, Entry("1.0.0"), includePrereleases: true)!.Newest.ToString());
        Assert.Null(ModpackVersions.Newer(c, Entry("1.2.0"), includePrereleases: false));      // already the newest release
        Assert.Null(ModpackVersions.Newer(c, Entry(null, ">=1.0.0"), includePrereleases: true)); // ranges always get the newest
        // A pack that saved a pre-release is offered the next one even with pre-releases switched off.
        var pre = new[] { B("1.1.0-dev.2", pre: true), B("1.1.0-dev.1", pre: true) };
        Assert.Equal("1.1.0-dev.2", ModpackVersions.Newer(pre, Entry("1.1.0-dev.1"), includePrereleases: false)!.Newest.ToString());
    }

    [Fact]
    public void NewerStaysInsideTheVersionRange()
    {
        var c = ModpackVersions.Candidates(Mod, Entry("1.0.0", "<1.2.0"), ModTarget.Windows);
        Assert.Equal("1.1.0", ModpackVersions.Newer(c, Entry("1.0.0", "<1.2.0"), includePrereleases: false)!.Newest.ToString());
    }

    [Fact]
    public void PickUsesTheExactVersionOrTheNewest()
    {
        var c = ModpackVersions.Candidates(Mod, Entry("1.1.0"), ModTarget.Windows);
        Assert.Equal("1.1.0", ModpackVersions.Pick(c, Entry("1.1.0"), SemVersion.Parse("1.1.0"), false)!.Version!.ToString());
        Assert.Null(ModpackVersions.Pick(c, Entry("1.5.0"), SemVersion.Parse("1.5.0"), false));
        Assert.Equal("1.2.0", ModpackVersions.Pick(c, Entry(null), null, includePrereleases: false)!.Version!.ToString());
        // Only pre-releases: still installable (like a dependency would be).
        Assert.Equal("1.1.0-dev.1", ModpackVersions.Pick([B("1.1.0-dev.1", pre: true)], Entry(null), null, false)!.Version!.ToString());
    }
}

public class GameBranchTests
{
    private const string Manifest = """
        "AppState"
        {
            "appid"		"526870"
            "name"		"Satisfactory"
            "installdir"		"Satisfactory"
            "UserConfig"
            {
                "language"		"english"
                "BetaKey"		"{0}"
            }
            "MountedConfig"
            {
                "language"		"english"
                "BetaKey"		"{1}"
            }
        }
        """;

    private static string Acf(string user, string mounted) => Manifest.Replace("{0}", user).Replace("{1}", mounted);

    [Theory]
    [InlineData("public", "public", GameBranch.EarlyAccess)]
    [InlineData("experimental", "experimental", GameBranch.Experimental)]
    [InlineData("experimental", "public", GameBranch.EarlyAccess)]   // picked but not downloaded yet: still the old branch
    public void ReadsTheInstalledSteamBranch(string user, string mounted, GameBranch expected) =>
        Assert.Equal(expected, GameBranchDetector.FromSteamManifest(Acf(user, mounted)));

    [Fact]
    public void FindsTheManifestNextToTheSteamLibrary()
    {
        using var tmp = new TempDir();
        var game = tmp.Combine("steamapps", "common", "Satisfactory");
        Directory.CreateDirectory(game);
        File.WriteAllText(tmp.Combine("steamapps", "appmanifest_526870.acf"), Acf("experimental", "experimental"));
        Assert.Equal(GameBranch.Experimental, GameBranchDetector.Detect(game));
        Assert.Equal(GameBranch.Unknown, GameBranchDetector.Detect(tmp.Combine("elsewhere")));

        var epic = tmp.Combine("Epic", "SatisfactoryExperimental");
        Assert.Equal(GameBranch.Experimental, GameBranchDetector.Detect(epic, [new FoundGame(epic, GameStore.Epic, "Experimental")]));
        Assert.Equal(GameBranch.EarlyAccess, GameBranchDetector.Detect(epic, [new FoundGame(epic, GameStore.Epic)]));
    }
}

public class SmrReportTests
{
    private static SmrMod Mod(SmrCompatibility? ea, SmrCompatibility? exp) =>
        new("id", "M", "M", null, null, null, null, 0, 0, [], [], "1.0.0", null, null, null, ea, exp);

    [Fact]
    public void ReportsBrokenAndDamagedForTheInstallsBranch()
    {
        var mod = Mod(new("Broken", "Broken by Satisfactory v1.2"), new("Damaged", null));
        var ea = SmrReport.For(mod, GameBranch.EarlyAccess)!;
        Assert.Equal(SmrReportLevel.Broken, ea.Level);
        Assert.Contains("Broken by Satisfactory v1.2", ea.Message);
        Assert.Equal(SmrReportLevel.Damaged, SmrReport.For(mod, GameBranch.Experimental)!.Level);
        var unknown = SmrReport.For(mod, GameBranch.Unknown)!;
        Assert.Equal(SmrReportLevel.Broken, unknown.Level);          // unknown branch: the Early Access report
        Assert.Contains("isn't known", unknown.Message);
    }

    [Fact]
    public void WorkingOrUnreportedModsHaveNoReport()
    {
        Assert.Null(SmrReport.For(Mod(new("Works", null), new("Works", null)), GameBranch.EarlyAccess));
        Assert.Null(SmrReport.For(Mod(null, null), GameBranch.Experimental));
    }

    [Fact]
    public void NewestSmrVersionMatchesWhatTheResolverWouldPick()
    {
        SmrVersion V(string v, string stability = "release", bool approved = true, string? game = null) =>
            new(v, "M", SemVersion.Parse(v), game, approved, stability, true, null, null,
                new Dictionary<ModTarget, SmrTarget> { [ModTarget.Windows] = new(ModTarget.Windows, "/v1/x", 1, "ab") }, []);
        var all = new[] { V("2.0.0", "beta"), V("1.3.0", approved: false), V("1.2.0", game: ">=999999"), V("1.1.0"), V("1.0.0") };
        Assert.Equal("1.1.0", SmrResolver.Newest(all, ModTarget.Windows, 500000)!.Version.ToString());
        Assert.Equal("1.0.0", SmrResolver.Newest(all, ModTarget.Windows, 500000, VersionRange.Parse("<1.1.0"))!.Version.ToString());
        Assert.Null(SmrResolver.Newest(all, ModTarget.LinuxServer, 500000));
    }
}
