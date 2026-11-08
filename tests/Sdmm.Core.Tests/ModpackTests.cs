using Sdmm.Core.Installs;
using Sdmm.Core.Modpacks;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;

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

        var c = ModpackPlanner.Plan(pack, installed, isServer: false, removeOthers: true, useNewest: false);

        Assert.Equal(["Older", "Missing"], c.Install.Select(m => m.ModReference));
        Assert.Equal(["Same", "Ranged"], c.Unchanged);
        Assert.Equal(["ServerThing", "Off"], c.Skipped.Select(s => s.Mod));
        Assert.Equal(["Extra", "Off"], c.Remove.Order());
        Assert.Equal(["SmmExtra"], c.KeptForeign);

        var newest = ModpackPlanner.Plan(pack, installed, isServer: false, removeOthers: false, useNewest: true);
        Assert.Contains(newest.Install, m => m.ModReference == "Same");
        Assert.Empty(newest.Remove);
    }
}
