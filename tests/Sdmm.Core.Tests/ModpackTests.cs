using Sdmm.Core.Installs;
using Sdmm.Core.Modpacks;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class ModpackTests
{
    private const string Smr = "ficsit.app/";
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);

    private static InstalledMod Mod(string modRef, string version, ModOwner owner, string? repo = null, string extra = "", string? adoptedFrom = null) =>
        new(modRef, $"FactoryGame/Mods/{modRef}", false, owner,
            UPluginDescriptor.Parse(modRef, $$"""{ "SemVersion": "{{version}}"{{extra}} }"""),
            owner == ModOwner.Sdmm ? new SdmmMarker { ModReference = modRef, Version = version, Sha256 = adoptedFrom is null ? "x" : SdmmMarker.AdoptedSha256, SourceRepo = repo, AdoptedFrom = adoptedFrom } : null,
            null);

    [Fact]
    public void CapturesSourcesAndSides()
    {
        var installed = new[]
        {
            Mod("SML", "3.12.0", ModOwner.Smm),
            Mod("FromSmr", "1.0.0", ModOwner.Sdmm, Smr + "FromSmr"),
            Mod("Dev", "0.3.0-dev.1", ModOwner.Sdmm, "bob/Dev"),
            Mod("Zip", "2.0.0", ModOwner.Sdmm, repo: null),
            Mod("Adopted", "1.1.0", ModOwner.Sdmm, repo: null, adoptedFrom: "SMM"),
            Mod("DepotFix", "1.0.0", ModOwner.Sdmm, "bob/DepotFix", """, "RequiredOnRemote": false"""),
            new InstalledMod("Broken", "FactoryGame/Mods/Broken", false, ModOwner.Unmanaged, null, null, "no uplugin"),
        };

        var onServer = ModpackIO.FromInstall("Coop", installed, isServer: true, Smr, Now).Mods.ToDictionary(m => m.ModReference);
        var onClient = ModpackIO.FromInstall("Coop", installed, isServer: false, Smr, Now).Mods.ToDictionary(m => m.ModReference);

        Assert.Equal(ModpackSource.Smr, onServer["SML"].Source);
        Assert.Equal(ModpackSource.Smr, onServer["FromSmr"].Source);
        Assert.Equal((ModpackSource.GitHub, "bob/Dev"), (onServer["Dev"].Source, onServer["Dev"].Repo));
        Assert.Equal(ModpackSource.Imported, onServer["Zip"].Source);
        Assert.Equal(ModpackSource.Smr, onServer["Adopted"].Source); // taken over: its files came from SMM, so SMR
        Assert.Equal(ModSide.ServerOnly, onServer["DepotFix"].Side);
        Assert.Equal(ModSide.ClientOnly, onClient["DepotFix"].Side);
        Assert.Equal(ModSide.Both, onServer["SML"].Side);
        Assert.False(onServer.ContainsKey("Broken"));
    }

    [Fact]
    public void ExportAndImportRoundTrip()
    {
        var pack = new Modpack
        {
            Name = "Coop",
            Mods = [new ModpackMod { ModReference = "ModA", Version = "1.2.0", Side = ModSide.ServerOnly }, new ModpackMod { ModReference = "Dev", Source = ModpackSource.GitHub, Repo = "bob/Dev", Constraint = ">=0.3.0" }],
        };

        var json = ModpackIO.Export(pack, ModTarget.LinuxServer, 517554);
        var back = ModpackIO.Import(json, Now);

        Assert.Contains("\"format\": \"sdmm-modpack\"", json);
        Assert.Contains("\"platform\": \"LinuxServer\"", json);
        Assert.Equal("Coop", back.Name);
        Assert.Equal(ModSide.ServerOnly, back.Mods[0].Side);
        Assert.Equal("bob/Dev", back.Mods[1].Repo);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else", "mods": [] }""")]
    [InlineData("""{ "format": "sdmm-modpack", "formatVersion": 99, "mods": [] }""")]
    [InlineData("""{ "format": "sdmm-modpack", "mods": [ { "modReference": "../evil" } ] }""")]
    [InlineData("""{ "format": "sdmm-modpack", "mods": [ { "modReference": "ModA", "version": "banana" } ] }""")]
    [InlineData("""{ "format": "sdmm-modpack", "mods": [ { "modReference": "ModA", "source": "GitHub" } ] }""")]
    [InlineData("""{ "format": "sdmm-modpack", "mods": [ { "modReference": "ModA" }, { "modReference": "moda" } ] }""")]
    public void RejectsFilesThatArentValidModpacks(string json) =>
        Assert.Throws<FormatException>(() => ModpackIO.Import(json, Now));

    [Fact]
    public void ImportsAnSmmProfile()
    {
        var profile = new SmmProfile("Coop", new Dictionary<string, SmmProfileMod>
        {
            ["ModA"] = new("ModA", ">=1.0.0", true),
            ["Off"] = new("Off", "", false),
        });
        var pack = ModpackIO.FromSmmProfile(profile, Now);
        Assert.Equal(">=1.0.0", pack.Mods.Single(m => m.ModReference == "ModA").Constraint);
        Assert.False(pack.Mods.Single(m => m.ModReference == "Off").Enabled);
    }

    [Fact]
    public void PlansWhatToInstallRemoveSkipAndKeep()
    {
        var pack = new Modpack
        {
            Mods =
            [
                new ModpackMod { ModReference = "Same", Version = "1.0.0" },
                new ModpackMod { ModReference = "Older", Version = "2.0.0" },
                new ModpackMod { ModReference = "Missing", Version = "1.0.0" },
                new ModpackMod { ModReference = "ServerThing", Version = "1.0.0", Side = ModSide.ServerOnly },
                new ModpackMod { ModReference = "Off", Version = "1.0.0", Enabled = false },
                new ModpackMod { ModReference = "Ranged", Constraint = ">=1.0.0" },
            ],
        };
        var installed = new[]
        {
            Mod("Same", "1.0.0", ModOwner.Sdmm), Mod("Older", "1.5.0", ModOwner.Sdmm), Mod("Ranged", "1.3.0", ModOwner.Smm),
            Mod("Extra", "1.0.0", ModOwner.Sdmm), Mod("SmmExtra", "1.0.0", ModOwner.Smm), Mod("Off", "1.0.0", ModOwner.Sdmm),
        };

        var c = ModpackPlanner.Plan(pack, installed, isServer: false, removeOthers: true);

        Assert.Equal(["Older", "Missing"], c.Install.Select(m => m.ModReference));
        Assert.Equal(["Same", "Ranged"], c.Unchanged);
        Assert.Equal(["ServerThing", "Off"], c.Skipped.Select(s => s.Mod));
        Assert.Equal(["Extra", "Off"], c.Remove.Order());
        Assert.Equal(["SmmExtra"], c.KeptForeign);

        // Updating: Same and Older to 2.1.0 are changes; a mod already at its target (Ranged) isn't reinstalled.
        var targets = new Dictionary<string, SemVersion>(StringComparer.OrdinalIgnoreCase)
            { ["Same"] = SemVersion.Parse("1.1.0"), ["Older"] = SemVersion.Parse("2.1.0"), ["ranged"] = SemVersion.Parse("1.3.0") };
        var newest = ModpackPlanner.Plan(pack, installed, isServer: false, removeOthers: false, targets);
        Assert.Equal(["Same", "Older", "Missing"], newest.Install.Select(m => m.ModReference));
        Assert.Equal(["Ranged"], newest.Unchanged);
        Assert.Empty(newest.Remove);
    }

    [Fact]
    public void CaptureRecordsTheAutomaticSide()
    {
        var pack = ModpackIO.FromInstall("Solo", [Mod("DepotFix", "1.0.0", ModOwner.Sdmm, "bob/DepotFix", """, "RequiredOnRemote": false""")], isServer: false, Smr, Now);
        var m = Assert.Single(pack.Mods);
        Assert.Equal((ModSide.ClientOnly, ModSide.ClientOnly, false), (m.Side, m.AutoSide, m.SideSetByUser));
    }

    [Fact]
    public void CopyIsIndependent()
    {
        var pack = new Modpack { Name = "Coop", Mods = [new ModpackMod { ModReference = "ModA", Version = "1.0.0", Side = ModSide.ServerOnly, SideSetByUser = true }] };
        var copy = pack.Copy("Coop (copy)", Now);
        copy.Mods[0].Version = "2.0.0";
        copy.Mods.Add(new ModpackMod { ModReference = "ModB" });

        Assert.Equal("Coop (copy)", copy.Name);
        Assert.Equal("1.0.0", pack.Mods[0].Version);
        Assert.Single(pack.Mods);
        Assert.Equal((ModSide.ServerOnly, true), (copy.Mods[0].Side, copy.Mods[0].SideSetByUser));
    }

    [Fact]
    public void FollowTakesInSdmmsChangesOnly()
    {
        var pack = new Modpack
        {
            Name = "Coop",
            Mods =
            [
                new ModpackMod { ModReference = "Updated", Version = "1.0.0", Source = ModpackSource.GitHub, Repo = "bob/Updated" },
                new ModpackMod { ModReference = "Removed", Version = "1.0.0" },
                new ModpackMod { ModReference = "Missing", Version = "1.0.0" },     // not installed, but not removed by SDMM
                new ModpackMod { ModReference = "Ranged", Constraint = ">=1.0.0" },
                new ModpackMod { ModReference = "Picked", Version = "1.0.0", Side = ModSide.Both, AutoSide = ModSide.ClientOnly, SideSetByUser = true },
            ],
        };
        var installed = new[]
        {
            Mod("Updated", "1.1.0", ModOwner.Sdmm, "bob/Updated"),
            Mod("Added", "2.0.0", ModOwner.Sdmm, Smr + "Added"),
            Mod("Ranged", "1.4.0", ModOwner.Smm),
            Mod("Picked", "1.2.0", ModOwner.Sdmm, "bob/Picked", """, "RequiredOnRemote": false"""),
            Mod("Outside", "1.0.0", ModOwner.Smm),     // installed elsewhere: not in the list of changes
        };

        Assert.True(ModpackIO.Follow(pack, installed, isServer: false, Smr,
            ["Updated", "Added", "Removed", "Missing", "Ranged", "Picked"], ["Removed"], Now));
        var byRef = pack.Mods.ToDictionary(m => m.ModReference);

        Assert.Equal("1.1.0", byRef["Updated"].Version);
        Assert.Equal(ModpackSource.Smr, byRef["Added"].Source);
        Assert.False(byRef.ContainsKey("Removed"));
        Assert.True(byRef.ContainsKey("Missing"));
        Assert.Equal((">=1.0.0", null), (byRef["Ranged"].Constraint, byRef["Ranged"].Version)); // 1.4.0 still fits the range
        Assert.Equal((ModSide.Both, true, "1.2.0"), (byRef["Picked"].Side, byRef["Picked"].SideSetByUser, byRef["Picked"].Version));
        Assert.False(byRef.ContainsKey("Outside"));
        Assert.Equal(pack.Mods.OrderBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).Select(m => m.ModReference), pack.Mods.Select(m => m.ModReference));

        // Nothing new: no change.
        Assert.False(ModpackIO.Follow(pack, installed, isServer: false, Smr, ["Updated"], null, Now));
    }

    [Fact]
    public void FollowingEverythingMakesThePackMatch()
    {
        var pack = new Modpack
        {
            Name = "Coop",
            Mods =
            [
                new ModpackMod { ModReference = "Gone", Version = "1.0.0" },
                new ModpackMod { ModReference = "ServerThing", Version = "1.0.0", Side = ModSide.ServerOnly, AutoSide = ModSide.ServerOnly },
                new ModpackMod { ModReference = "Off", Version = "1.0.0", Enabled = false },
            ],
        };
        var installed = new[] { Mod("Outside", "1.0.0", ModOwner.Smm) };

        var (missing, extra) = ModpackIO.Compare(pack, installed, isServer: false);
        Assert.Equal(["Gone"], missing);
        Assert.Equal(["Outside"], extra);

        ModpackIO.Follow(pack, installed, isServer: false, Smr, null, null, Now);
        Assert.Equal(["Off", "Outside", "ServerThing"], pack.Mods.Select(m => m.ModReference)); // other side and switched-off entries stay
        Assert.Equal((0, 0), (ModpackIO.Compare(pack, installed, isServer: false).Missing.Count, ModpackIO.Compare(pack, installed, isServer: false).Extra.Count));
    }

    private static ModpackMod P(string modRef, string? version = "1.0.0", ModpackSource source = ModpackSource.Smr, string? repo = null,
        ModSide side = ModSide.Both, bool enabled = true, string? constraint = null) =>
        new() { ModReference = modRef, Version = version, Source = source, Repo = repo, Side = side, Enabled = enabled, Constraint = constraint };

    [Fact]
    public void ComparingTwoPacksShowsSharedAndMissingMods()
    {
        var a = new Modpack { Name = "A", Mods = [P("Shared"), P("OnlyA"), P("Bumped", "1.0.0"), P("zLast")] };
        var b = new Modpack { Name = "B", Mods = [P("bumped", "1.1.0"), P("OnlyB"), P("SHARED"), P("zLast")] };

        var diff = ModpackDiff.Compare(a, b);

        Assert.Equal(["Bumped", "OnlyA", "OnlyB", "Shared", "zLast"], diff.Select(d => d.ModReference));   // matched ignoring case, sorted
        Assert.Equal([ModpackDiffKind.Different, ModpackDiffKind.OnlyLeft, ModpackDiffKind.OnlyRight, ModpackDiffKind.Same, ModpackDiffKind.Same],
            diff.Select(d => d.Kind));
        Assert.Equal(ModpackDiffParts.Version, diff[0].Differences);
        Assert.Null(diff[1].Right);
        Assert.Null(diff[2].Left);
        Assert.Equal("1.0.0", diff[0].Left!.Version);
        Assert.Equal("1.1.0", diff[0].Right!.Version);
    }

    [Theory]
    [InlineData("1.0.0", "v1.0.0", ModpackDiffParts.None)]
    [InlineData("1.0.0+dev.1", "1.0.0+dev.2", ModpackDiffParts.Version)]   // different dev builds
    [InlineData("1.0.0", "1.0.0-beta.1", ModpackDiffParts.Version)]
    [InlineData("1.0.0", null, ModpackDiffParts.Version)]                // a saved version vs "newest"
    public void VersionsCompareAsVersions(string? left, string? right, ModpackDiffParts expected) =>
        Assert.Equal(expected, ModpackDiff.Compare(new Modpack { Mods = [P("M", left)] }, new Modpack { Mods = [P("M", right)] }).Single().Differences);

    [Fact]
    public void ReportsEachKindOfDifference()
    {
        ModpackDiffParts Diff(ModpackMod l, ModpackMod r) =>
            ModpackDiff.Compare(new Modpack { Mods = [l] }, new Modpack { Mods = [r] }).Single().Differences;

        Assert.Equal(ModpackDiffParts.None, Diff(P("M", null, constraint: ">=1.0.0"), P("M", null, constraint: ">=1.0.0")));
        Assert.Equal(ModpackDiffParts.Version, Diff(P("M", null, constraint: ">=1.0.0"), P("M", null, constraint: "^2.0.0")));
        Assert.Equal(ModpackDiffParts.Source, Diff(P("M"), P("M", source: ModpackSource.GitHub, repo: "me/M")));
        Assert.Equal(ModpackDiffParts.Source, Diff(P("M", source: ModpackSource.GitHub, repo: "me/M"), P("M", source: ModpackSource.GitHub, repo: "you/M")));
        Assert.Equal(ModpackDiffParts.None, Diff(P("M", source: ModpackSource.GitHub, repo: "me/M"), P("M", source: ModpackSource.GitHub, repo: "Me/M")));
        Assert.Equal(ModpackDiffParts.Side, Diff(P("M"), P("M", side: ModSide.ServerOnly)));
        Assert.Equal(ModpackDiffParts.Enabled | ModpackDiffParts.Version, Diff(P("M"), P("M", "2.0.0", enabled: false)));
    }

    [Fact]
    public void ComparingAPackWithItselfFindsNoDifferences()
    {
        var a = new Modpack { Mods = [P("A"), P("B", enabled: false), P("C", side: ModSide.ClientOnly)] };
        Assert.All(ModpackDiff.Compare(a, a.Copy("copy", Now)), d => Assert.Equal(ModpackDiffKind.Same, d.Kind));
        Assert.Empty(ModpackDiff.Compare(new Modpack(), new Modpack()));
    }
}
