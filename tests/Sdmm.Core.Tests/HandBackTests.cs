using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Smm;

namespace Sdmm.Core.Tests;

public class HandBackTests
{
    private const string Profiles = """
        { "profiles": { "Coop": { "name": "Coop", "mods": { "SML": { "version": ">=3.12.0", "enabled": true }, "ModA": { "version": "^1.2.0", "enabled": false } } },
                        "Solo": { "name": "Solo", "mods": { "ModA": { "version": ">=0.0.0", "enabled": true } } } },
          "selected_profile": "Coop", "version": 0 }
        """;

    [Fact]
    public void RemovingRecordsTheEntriesAndAddingPutsThemBackExactly()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), Profiles);

        var removed = SmmProfileEditor.RemoveMods(tmp.Path, ["ModA"], findRunningSmm: () => []);
        var entries = removed.Removed!["ModA"];
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e is { Profile: "Coop", Version: "^1.2.0", Enabled: false });

        var added = SmmProfileEditor.AddMods(tmp.Path, new Dictionary<string, IReadOnlyList<SmmProfileEntry>> { ["ModA"] = entries }, findRunningSmm: () => []);

        Assert.Equal(["Coop", "Solo"], added.Profiles.Order());
        var state = SmmState.Load(tmp.Path);
        Assert.Equal(new SmmProfileMod("ModA", "^1.2.0", false), state.Profiles["Coop"].Mods["ModA"]);
        Assert.Equal(new SmmProfileMod("ModA", ">=0.0.0", true), state.Profiles["Solo"].Mods["ModA"]);
    }

    [Fact]
    public void AddingLeavesExistingEntriesAndSkipsUnknownProfiles()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "profiles.json"), Profiles);

        var added = SmmProfileEditor.AddMods(tmp.Path, new Dictionary<string, IReadOnlyList<SmmProfileEntry>>
        {
            ["SML"] = [new SmmProfileEntry { Profile = "Coop", Version = ">=9.9.9" }],
            ["New"] = [new SmmProfileEntry { Profile = "Gone" }],
        }, findRunningSmm: () => []);

        Assert.Empty(added.Profiles);
        Assert.Null(added.BackupPath);
        Assert.Equal(">=3.12.0", SmmState.Load(tmp.Path).Profiles["Coop"].Mods["SML"].VersionConstraint);
    }

    [Fact]
    public void HandBackSwapsTheMarkerAndKeepsTheFiles()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path).Write("FactoryGame/Mods/FromSmr/FromSmr.uplugin", """{ "SemVersion": "1.0.0" }""")
            .Write("FactoryGame/Mods/FromSmr/.sdmm.json", new SdmmMarker { ModReference = "FromSmr", Version = "1.0.0", Sha256 = new string('a', 64), SourceRepo = "ficsit.app/FromSmr" }.ToJson());
        var install = GameInstall.Local("", tmp.Path);
        var installer = new Installer();

        // A taken-over SMM mod gets SMM's original marker back.
        installer.Adopt(install, "SML", new NoGameRunning());
        installer.HandBack(install, "SML", new NoGameRunning());
        Assert.Equal("ff2f1d5a", game.Read("FactoryGame/Mods/SML/.smm"));
        Assert.False(game.Exists("FactoryGame/Mods/SML/.sdmm.json"));

        // A mod installed from SMR gets the zip's hash, which is what SMM writes.
        installer.HandBack(install, "FromSmr", new NoGameRunning());
        Assert.Equal(new string('a', 64), game.Read("FactoryGame/Mods/FromSmr/.smm"));
        Assert.True(game.Exists("FactoryGame/Mods/FromSmr/FromSmr.uplugin"));
        Assert.Equal(ModOwner.Smm, ModScanner.Scan(new LocalInstallFileSystem(tmp.Path)).Single(m => m.ModReference == "FromSmr").Owner);
    }

    [Fact]
    public void HandBackRefusesWhileTheGameRunsAndForModsSdmmDoesntManage()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Path);
        var install = GameInstall.Local("", tmp.Path);
        Assert.Throws<InvalidOperationException>(() => new Installer().HandBack(install, "SML", new NoGameRunning())); // SMM's already
        new Installer().Adopt(install, "SML", new NoGameRunning());
        Assert.Throws<InvalidOperationException>(() => new Installer().HandBack(install, "SML", new GameRunning()));
    }

    [Fact]
    public void ReleasingAModTakenOverFromTheHostLeavesItAsItWas()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path).Write("FactoryGame/Mods/HostMod/HostMod.uplugin", """{ "SemVersion": "1.0.0" }""")
            .Write("FactoryGame/Mods/FromSmm/FromSmm.uplugin", """{ "SemVersion": "1.0.0" }""").Write("FactoryGame/Mods/FromSmm/.smm", "abc");
        var install = GameInstall.Local("", tmp.Path);
        var installer = new Installer();
        installer.Adopt(install, "HostMod", new NoGameRunning());
        installer.Adopt(install, "FromSmm", new NoGameRunning());

        installer.Release(install, "HostMod", new NoGameRunning());

        Assert.False(game.Exists("FactoryGame/Mods/HostMod/.sdmm.json"));
        Assert.Equal(["HostMod.uplugin"], Directory.GetFiles(game.Full("FactoryGame/Mods/HostMod")).Select(Path.GetFileName));
        Assert.Equal(ModOwner.Unmanaged, ModScanner.ScanMod(install.FileSystem, "HostMod").Single().Owner);
        // A mod taken over from SMM goes back to SMM instead, which needs its marker; releasing would orphan it.
        Assert.Throws<InvalidOperationException>(() => installer.Release(install, "FromSmm", new NoGameRunning()));
        Assert.Throws<InvalidOperationException>(() => installer.Release(install, "HostMod", new NoGameRunning()));
    }
}
