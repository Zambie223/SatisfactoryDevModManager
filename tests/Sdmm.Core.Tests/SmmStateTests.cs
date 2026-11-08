using Sdmm.Core.Installs;
using Sdmm.Core.Smm;

namespace Sdmm.Core.Tests;

public class SmmStateTests
{
    [Theory]
    // Same name shapes as lockfiles SMM wrote on a real install (spaces, "!!", trailing digit, hyphen, two words).
    [InlineData("Survival 2", "survival 2-lock.json")]
    [InlineData("Creative!!", "creative!!-lock.json")]
    [InlineData("Coop2", "coop2-lock.json")]
    [InlineData("Light-mods", "light-mods-lock.json")]
    [InlineData("Extra Content", "extra _content-lock.json")]
    [InlineData("Vanilla", "vanilla-lock.json")]
    [InlineData("MyProfileName", "my_profile_name-lock.json")]
    public void ComputesLockfileNameLikeFicsitCli(string profile, string expected) =>
        Assert.Equal(expected, SmmState.LockfileName(profile));

    private static SmmState LoadFixture(TempDir tmp, string gameRoot)
    {
        var dir = tmp.Combine("ficsit");
        Directory.CreateDirectory(dir);
        var escaped = gameRoot.Replace("\\", "\\\\");
        // Same install listed twice with different case (SMM does not de-duplicate such paths).
        File.WriteAllText(Path.Combine(dir, "installations.json"), $$"""
        { "installations": [
            { "path": "{{escaped}}", "profile": "Solo", "vanilla": false },
            { "path": "{{escaped.ToUpperInvariant()}}\\", "profile": "Coop2", "vanilla": false },
            { "path": "sftp://user@host/game", "profile": "Server", "vanilla": false }
          ], "version": 0 }
        """);
        File.WriteAllText(Path.Combine(dir, "profiles.json"), """
        { "profiles": {
            "Solo": { "name": "Solo", "mods": { "OtherMod": { "version": ">=0.0.0", "enabled": true }, "OffMod": { "version": ">=0.0.0", "enabled": false } } },
            "Coop2": { "name": "Coop2", "mods": { "MyMod": { "version": ">=0.0.0", "enabled": true } } },
            "Server": { "name": "Server", "mods": { "ServerOnly": { "version": ">=0.0.0", "enabled": true } } }
          }, "selected_profile": "Solo", "version": 0 }
        """);
        return SmmState.Load(dir);
    }

    [Fact]
    public void MatchesInstallPathsCaseInsensitivelyAndFindsEveryProfile()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Combine("game"));
        var smm = LoadFixture(tmp, game.Root);
        var fs = new LocalInstallFileSystem(game.Root);

        Assert.Equal(2, smm.FindInstallations(game.Root).Count);
        var c = Assert.Single(smm.FindConflicts(game.Root, fs, "MyMod"));
        Assert.Equal("Coop2", c.ProfileName);
        Assert.Single(smm.FindConflicts(game.Root, fs, "OtherMod"));
        Assert.Empty(smm.FindConflicts(game.Root, fs, "OffMod"));      // disabled in the profile
        Assert.Empty(smm.FindConflicts(game.Root, fs, "ServerOnly"));  // a different (remote) install
        Assert.Empty(smm.FindConflicts(game.Root, fs, "Unrelated"));
    }

    [Fact]
    public void FindsModPulledInThroughLockfile()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Combine("game"))
            .Write("FactoryGame/Mods/solo-lock.json", """{ "mods": { "OtherMod": {}, "DepMod": {} }, "version": 1 }""");
        var smm = LoadFixture(tmp, game.Root);
        var c = Assert.Single(smm.FindConflicts(game.Root, new LocalInstallFileSystem(game.Root), "DepMod"));
        Assert.Contains("dependency", c.Reason);
    }

    [Fact]
    public void MissingSmmIsNotAnError()
    {
        using var tmp = new TempDir();
        var smm = SmmState.Load(tmp.Combine("nope"));
        Assert.False(smm.Found);
        Assert.Empty(smm.Installations);
    }
}

public class ModHealthTests
{
    private static InstalledMod Mod(string modRef, string json, ModOwner owner = ModOwner.Sdmm, bool inGf = false) =>
        new(modRef, inGf ? $"FactoryGame/Mods/GameFeatures/{modRef}" : $"FactoryGame/Mods/{modRef}", inGf, owner,
            Packaging.UPluginDescriptor.Parse(modRef, json), null, null);

    [Fact]
    public void FindsDuplicatesBrokenCopiesAndGameVersion()
    {
        var installed = new[]
        {
            Mod("ModA", """{ "SemVersion": "1.0.0" }"""),
            Mod("ModA", """{ "SemVersion": "1.0.0" }""", inGf: true),
            new InstalledMod("Broken", "FactoryGame/Mods/Broken", false, ModOwner.Unmanaged, null, null, "Broken.uplugin not found"),
            Mod("Future", """{ "SemVersion": "1.0.0", "GameVersion": ">=999999" }"""),
        };

        var p = ModHealth.Check(installed, changelist: 517554);

        Assert.Contains(p["ModA"], x => x.Code == "DUPLICATE");
        Assert.Contains(p["Broken"], x => x.Code == "BROKEN");
        Assert.Contains(p["Future"], x => x.Code == "GAME_VERSION");
        Assert.False(ModHealth.Check(installed, changelist: 0).ContainsKey("Future")); // unknown build: skipped, as SML does
    }

    [Fact]
    public void FindsMissingAndMismatchedDependencies()
    {
        var installed = new[]
        {
            Mod("SML", """{ "SemVersion": "3.11.0" }""", ModOwner.Smm),
            Mod("ModA", """{ "SemVersion": "1.0.0", "Plugins": [ { "Name": "SML", "SemVersion": "^3.12.0" }, { "Name": "Lib", "SemVersion": "^1.0.0" }, { "Name": "Opt", "SemVersion": "^1.0.0", "Optional": true }, { "Name": "GameplayAbilities" } ] }"""),
        };

        var p = ModHealth.Check(installed, 0)["ModA"];

        Assert.Contains(p, x => x.Code == "DEPENDENCY_VERSION" && x.Message.Contains("SML"));
        Assert.Contains(p, x => x.Code == "DEPENDENCY_MISSING" && x.Message.Contains("Lib"));
        Assert.DoesNotContain(p, x => x.Message.Contains("Opt") || x.Message.Contains("GameplayAbilities"));
    }

    [Fact]
    public void AHealthyModHasNoProblems()
    {
        var installed = new[]
        {
            Mod("SML", """{ "SemVersion": "3.12.0" }""", ModOwner.Smm),
            Mod("ModA", """{ "SemVersion": "1.0.0", "Plugins": [ { "Name": "SML", "SemVersion": "^3.12.0" } ] }"""),
        };
        Assert.Empty(ModHealth.Check(installed, 517554));
    }
}

public class SmmProfileEditorTests
{
    private const string Profiles = """
        {
          "profiles": {
            "Default": { "name": "Default", "mods": { "ModA": { "version": ">=1.0.0", "enabled": true }, "Keep": { "version": ">=0.0.0", "enabled": true } }, "required_targets": ["Windows"] },
            "Coop2": { "name": "Coop2", "mods": { "ModA": { "version": ">=0.0.0", "enabled": false } } },
            "Solo": { "name": "Solo", "mods": { "Keep": { "version": ">=0.0.0", "enabled": true } } }
          },
          "selected_profile": "Default",
          "version": 0
        }
        """;

    [Fact]
    public void RemovesTheModFromEveryProfileAndKeepsTheRest()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), Profiles);

        var edit = SmmProfileEditor.RemoveMods(tmp.Path, ["ModA"], findRunningSmm: () => []);

        Assert.Equal(["Default", "Coop2"], edit.Profiles);
        Assert.True(File.Exists(edit.BackupPath));
        Assert.Equal(Profiles, File.ReadAllText(edit.BackupPath!));
        var state = SmmState.Load(tmp.Path);
        Assert.All(state.Profiles.Values, p => Assert.False(p.Mods.ContainsKey("ModA")));
        Assert.True(state.Profiles["Default"].Mods.ContainsKey("Keep"));
        var json = File.ReadAllText(Path.Combine(tmp.Path, "profiles.json"));
        Assert.Contains("\"selected_profile\": \"Default\"", json);
        Assert.Contains("required_targets", json);
    }

    [Fact]
    public void NothingListedMeansNoChangeAndNoBackup()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), Profiles);

        var edit = SmmProfileEditor.RemoveMods(tmp.Path, ["NotThere"], findRunningSmm: () => []);

        Assert.Empty(edit.Profiles);
        Assert.Null(edit.BackupPath);
        Assert.Equal(Profiles, File.ReadAllText(Path.Combine(tmp.Path, "profiles.json")));
    }

    [Fact]
    public void ListsProfilesThatHaveAMod()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), Profiles);
        Assert.Equal(["Default", "Solo"], SmmProfileEditor.ProfilesListing(SmmState.Load(tmp.Path), "Keep").Order());
    }
}

public class SmmProfileEditorRunningTests
{
    [Fact]
    public void RefusesWhileSmmRuns()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), """{ "profiles": { "A": { "mods": { "ModA": {} } } } }""");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SmmProfileEditor.RemoveMods(tmp.Path, ["ModA"], findRunningSmm: () => ["Satisfactory Mod Manager (pid 1)"]));
        Assert.Contains("Close SMM", ex.Message);
        Assert.Contains("ModA", File.ReadAllText(Path.Combine(tmp.Path, "profiles.json")));
    }
}

public class SmmLockfileTests
{
    [Fact]
    public void FindsModsThatStillNeedTheTakenOverModAndClearsStaleLockEntries()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), """
            { "profiles": { "P": { "mods": { "Uses": { "enabled": true }, "Off": { "enabled": false } } } } }
            """);
        var game = new FakeGame(tmp.Combine("game"))
            .Write("FactoryGame/Mods/Uses/Uses.uplugin", """{ "SemVersion": "1.0.0", "Plugins": [ { "Name": "Lib", "SemVersion": "^1.0.0" } ] }""")
            .Write("FactoryGame/Mods/Off/Off.uplugin", """{ "SemVersion": "1.0.0", "Plugins": [ { "Name": "Free", "SemVersion": "^1.0.0" } ] }""")
            .Write("FactoryGame/Mods/p-lock.json", """{ "mods": { "Lib": {}, "Free": {}, "Uses": {} } }""");
        var fs = new LocalInstallFileSystem(game.Root);
        var smm = SmmState.Load(tmp.Path);
        var installed = ModScanner.Scan(fs);

        Assert.Equal(["Uses"], SmmProfileEditor.RequiredBy(smm, ["P"], installed, "Lib"));
        Assert.Empty(SmmProfileEditor.RequiredBy(smm, ["P"], installed, "Free")); // its only user is disabled

        SmmProfileEditor.RemoveFromLockfiles(fs, ["P"], ["Free"]);
        var lockJson = game.Read("FactoryGame/Mods/p-lock.json");
        Assert.DoesNotContain("Free", lockJson);
        Assert.Contains("Lib", lockJson);
    }
}
