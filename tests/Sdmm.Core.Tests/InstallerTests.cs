using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Smm;

namespace Sdmm.Core.Tests;

public class InstallerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeGame _game;
    private readonly GameInstall _install;
    private readonly InstallPlanner _planner = new(new NoGameRunning());
    private readonly List<string> _log = [];
    private readonly Installer _installer;

    public InstallerTests()
    {
        _game = new FakeGame(_tmp.Combine("game"));
        _install = GameInstall.Local("test", _game.Root);
        _installer = new Installer(_log.Add);
    }

    public void Dispose() => _tmp.Dispose();

    private InstallRequest Req(string modRef, string version, string target = "Windows", bool gameFeature = false,
        string? tag = null, string? gameVersion = ">=491125", params (string, string?, bool)[] deps)
    {
        var zip = Zips.Mod(_tmp.Combine("zips"), modRef, version, target, gameFeature, gameVersion, deps);
        return new InstallRequest(ModPackage.Open(zip), new PackageSource("me/repo", tag ?? "v" + version, Path.GetFileName(zip)));
    }

    private InstallPlan Plan(params InstallRequest[] reqs) => _planner.Plan(_install, reqs, smm: null);

    private static string[] Codes(InstallPlan p) => p.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public void UpdatingATakenOverModKeepsWhoItCameFromSoItCanStillBeHandedBack()
    {
        _game.Write("FactoryGame/Mods/HostMod/HostMod.uplugin", """{ "SemVersion": "1.0.0" }""")
             .Write("FactoryGame/Mods/SmmMod/SmmMod.uplugin", """{ "SemVersion": "1.0.0" }""").Write("FactoryGame/Mods/SmmMod/.smm", "old-smm-hash");
        _installer.Adopt(_install, "HostMod", new NoGameRunning());
        _installer.Adopt(_install, "SmmMod", new NoGameRunning());

        _installer.Execute(Plan(Req("HostMod", "1.1.0"), Req("SmmMod", "1.1.0")));

        var host = ModScanner.ScanMod(_install.FileSystem, "HostMod").Single().Marker!;
        Assert.Equal(SdmmMarker.AdoptedFromOther, host.AdoptedFrom);
        Assert.False(host.IsAdoptedCopy); // the files are SDMM's now
        _installer.Release(_install, "HostMod", new NoGameRunning());
        Assert.False(_game.Exists("FactoryGame/Mods/HostMod/.sdmm.json"));

        // Handing back to SMM writes the hash of what's on disk now, not SMM's marker for the old files.
        var smm = ModScanner.ScanMod(_install.FileSystem, "SmmMod").Single().Marker!;
        Assert.Equal("old-smm-hash", smm.PreviousSmmMarker);
        _installer.HandBack(_install, "SmmMod", new NoGameRunning());
        Assert.Equal(smm.Sha256, _game.Read("FactoryGame/Mods/SmmMod/.smm"));
    }

    [Fact]
    public void InstallsGameFeatureModWithOurMarkerAndNoSmmMarker()
    {
        var plan = Plan(Req("ExampleMod", "1.0.1-dev.1", gameFeature: true));
        Assert.False(plan.IsBlocked, string.Join("; ", plan.Issues.Select(i => i.Message)));
        Assert.Equal(ActionKind.Install, Assert.Single(plan.Actions).Kind);

        _installer.Execute(plan);

        const string dir = "FactoryGame/Mods/GameFeatures/ExampleMod";
        Assert.True(_game.Exists($"{dir}/ExampleMod.uplugin"));
        Assert.True(_game.Exists($"{dir}/Content/Paks/Windows/ExampleModFactoryGame-Windows.pak"));
        Assert.False(_game.Exists($"{dir}/.smm"));
        var marker = SdmmMarker.TryParse(_game.Read($"{dir}/{SdmmMarker.FileName}"))!;
        Assert.Equal(("ExampleMod", "1.0.1-dev.1", "me/repo", "v1.0.1-dev.1"), (marker.ModReference, marker.Version, marker.SourceRepo, marker.SourceTag));
        Assert.False(_game.Exists(Installer.StagingDir));
        Assert.Equal(ModOwner.Sdmm, ModScanner.Scan(_install.FileSystem).Single(m => m.ModReference == "ExampleMod").Owner);
    }

    [Fact]
    public void ReplacesOwnInstallAndSkipsIdenticalBuild()
    {
        _installer.Execute(Plan(Req("MyMod", "1.0.0-dev.1")));
        _game.Write("FactoryGame/Mods/MyMod/leftover.txt", "old file");

        var update = Req("MyMod", "1.0.0-dev.2");
        var plan = Plan(update);
        Assert.Equal(ActionKind.Replace, Assert.Single(plan.Actions).Kind);
        _installer.Execute(plan);

        Assert.False(_game.Exists("FactoryGame/Mods/MyMod/leftover.txt")); // old folder replaced, not merged
        Assert.Contains("1.0.0-dev.2", _game.Read("FactoryGame/Mods/MyMod/MyMod.uplugin"));

        var again = Plan(new InstallRequest(ModPackage.Open(update.Package.ZipPath), update.Source));
        Assert.Equal(ActionKind.AlreadyInstalled, Assert.Single(again.Actions).Kind);
    }

    [Fact]
    public void RefusesSmmOwnedFolder()
    {
        _game.Write("FactoryGame/Mods/MyMod/MyMod.uplugin", Zips.UPlugin("1.0.0")).Write("FactoryGame/Mods/MyMod/.smm", "hash");
        var plan = Plan(Req("MyMod", "1.1.0"));
        Assert.Contains("SMM_OWNED", Codes(plan));
        Assert.Throws<InvalidOperationException>(() => _installer.Execute(plan));
        Assert.Contains("1.0.0", _game.Read("FactoryGame/Mods/MyMod/MyMod.uplugin"));
    }

    [Fact]
    public void RefusesUnmanagedFolder()
    {
        _game.Write("FactoryGame/Mods/MyMod/MyMod.uplugin", Zips.UPlugin("1.0.0"));
        Assert.Contains("UNMANAGED_FOLDER", Codes(Plan(Req("MyMod", "1.1.0"))));
    }

    [Fact]
    public void FolderWithBothMarkersCountsAsSmm()
    {
        _installer.Execute(Plan(Req("MyMod", "1.0.0")));
        _game.Write("FactoryGame/Mods/MyMod/.smm", "hash");
        Assert.Contains("SMM_OWNED", Codes(Plan(Req("MyMod", "1.1.0"))));
    }

    [Fact]
    public void BlocksWrongTargetAndTagMismatch()
    {
        Assert.Contains("WRONG_TARGET", Codes(Plan(Req("MyMod", "1.0.0", target: "LinuxServer"))));
        Assert.Contains("TAG_MISMATCH", Codes(Plan(Req("MyMod", "1.0.0", tag: "v1.0.1"))));
        Assert.Contains("TAG_NOT_VERSION", Codes(Plan(Req("MyMod", "1.0.0", tag: "latest"))));
    }

    [Fact]
    public void BlocksWhileGameRuns()
    {
        var plan = new InstallPlanner(new GameRunning()).Plan(_install, [Req("MyMod", "1.0.0")], null);
        Assert.Contains("GAME_RUNNING", Codes(plan));
    }

    [Fact]
    public void DetectsServerPlatform()
    {
        var server = new FakeGame(_tmp.Combine("server"), "Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version");
        var install = GameInstall.Local("server", server.Root);
        Assert.Equal(ModTarget.LinuxServer, install.Detect().Target);
        var plan = _planner.Plan(install, [Req("MyMod", "1.0.0", target: "LinuxServer")], null);
        Assert.DoesNotContain("WRONG_TARGET", Codes(plan));
    }

    [Fact]
    public void MissingDependencyNeedsConfirmation()
    {
        var plan = Plan(Req("MyMod", "1.0.0", deps: [("SML", "^3.12.0", false), ("ContentLib", "^1.0.0", false), ("OptionalThing", "^1.0.0", true)]));
        var issue = Assert.Single(plan.NeedingConfirmation);
        Assert.Equal("MISSING_DEPENDENCY", issue.Code);
        Assert.Contains("ContentLib", issue.Message);

        Assert.Throws<InvalidOperationException>(() => _installer.Execute(plan));
        _installer.Execute(plan, new HashSet<string> { "MISSING_DEPENDENCY" });
        Assert.True(_game.Exists("FactoryGame/Mods/MyMod/MyMod.uplugin"));
    }

    [Fact]
    public void DependencySatisfiedByAnotherModInTheSameBatch()
    {
        var plan = Plan(Req("Lib", "2.0.0"), Req("MyMod", "1.0.0", deps: [("SML", "^3.12.0", false), ("Lib", "^2.0.0", false)]));
        Assert.Empty(plan.NeedingConfirmation);
    }

    [Fact]
    public void SmlVersionMismatchIsCaught()
    {
        var plan = Plan(Req("MyMod", "1.0.0", deps: [("SML", "^3.13.0", false)]));
        Assert.Contains("DEPENDENCY_VERSION", Codes(plan));
    }

    [Fact]
    public void ReplacingALibraryThatAnInstalledModDependsOnIsCaught()
    {
        _installer.Execute(Plan(Req("Lib", "1.0.0")));
        _installer.Execute(Plan(Req("User", "1.0.0", deps: [("Lib", "^1.0.0", false)])));
        Assert.Contains("BREAKS_DEPENDENT", Codes(Plan(Req("Lib", "2.0.0"))));
    }

    [Fact]
    public void GameVersionMismatchIsCaught()
    {
        Assert.Contains("GAME_VERSION", Codes(Plan(Req("MyMod", "1.0.0", gameVersion: ">=999999"))));
        Assert.DoesNotContain("GAME_VERSION", Codes(Plan(Req("MyMod", "1.0.0", gameVersion: ">=517554"))));
    }

    [Fact]
    public void MovingBetweenModsAndGameFeaturesRemovesTheOldFolder()
    {
        _installer.Execute(Plan(Req("MyMod", "1.0.0", gameFeature: false)));
        var plan = Plan(Req("MyMod", "1.1.0", gameFeature: true));
        Assert.Equal(ActionKind.Replace, Assert.Single(plan.Actions).Kind);
        _installer.Execute(plan);
        Assert.False(_game.Exists("FactoryGame/Mods/MyMod"));
        Assert.True(_game.Exists("FactoryGame/Mods/GameFeatures/MyMod/MyMod.uplugin"));
    }

    [Fact]
    public void ZipCannotPlantMarkers()
    {
        var zip = Zips.Create(_tmp.Combine("evil"), "MyMod-Windows.zip",
            ("MyMod.uplugin", Zips.UPlugin("1.0.0")), (".smm", "planted"), (SdmmMarker.FileName, "{}"));
        _installer.Execute(Plan(new InstallRequest(ModPackage.Open(zip), new PackageSource(null, null, null))));
        Assert.False(_game.Exists("FactoryGame/Mods/MyMod/.smm"));
        Assert.NotNull(SdmmMarker.TryParse(_game.Read($"FactoryGame/Mods/MyMod/{SdmmMarker.FileName}")));
    }

    [Fact]
    public void FailureRollsBackTheWholeBatch()
    {
        _installer.Execute(Plan(Req("A", "1.0.0")));
        var plan = Plan(Req("A", "2.0.0"), Req("B", "1.0.0"));
        var faulty = new GameInstall("faulty", new FaultyFs(_game.Root, failMoveInto: "FactoryGame/Mods/B"));
        var faultyPlan = new InstallPlan { Install = faulty, Game = plan.Game, Actions = plan.Actions, Issues = plan.Issues };

        Assert.Throws<IOException>(() => _installer.Execute(faultyPlan));

        Assert.Contains("1.0.0", _game.Read("FactoryGame/Mods/A/A.uplugin")); // A restored to the old build
        Assert.False(_game.Exists("FactoryGame/Mods/B"));
        Assert.False(_game.Exists(Installer.StagingDir) && Directory.EnumerateFileSystemEntries(_game.Full(Installer.StagingDir)).Any());
    }

    [Fact]
    public void InstallAtDriveRootResolvesPaths()
    {
        // Path logic only; nothing is read or written at the drive root.
        var driveRoot = Path.GetPathRoot(_game.Root)!;   // e.g. "M:\"
        var fs = new LocalInstallFileSystem(driveRoot);
        Assert.Equal(Path.Combine(driveRoot, "FactoryGame", "Mods"), fs.Resolve("FactoryGame/Mods"));
        // Nothing exists above a drive root, so ".." is clamped and the path stays inside it.
        Assert.Equal(Path.Combine(driveRoot, "outside"), fs.Resolve("../outside"));
    }

    [Fact]
    public void LocalPathsCannotEscapeTheInstallRoot()
    {
        var fs = new LocalInstallFileSystem(_game.Root);
        Assert.Throws<InvalidOperationException>(() => fs.Resolve("../outside"));
        Assert.Throws<InvalidOperationException>(() => fs.Resolve("FactoryGame/../../outside"));
        // A sibling folder whose name starts with the root's name must not count as inside it.
        Assert.Throws<InvalidOperationException>(() => fs.Resolve("../" + Path.GetFileName(_game.Root) + "-evil/x"));
    }

    [Fact]
    public void UninstallRemovesOnlyOurFolders()
    {
        _installer.Execute(Plan(Req("MyMod", "1.0.0")));
        _installer.Uninstall(_install, "MyMod", new NoGameRunning());
        Assert.False(_game.Exists("FactoryGame/Mods/MyMod"));

        Assert.Throws<InvalidOperationException>(() => _installer.Uninstall(_install, "SML", new NoGameRunning()));
        Assert.True(_game.Exists("FactoryGame/Mods/SML/SML.uplugin"));
    }

    [Fact]
    public void SmmProfileConflictBlocks()
    {
        var dir = _tmp.Combine("ficsit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "installations.json"),
            $$"""{ "installations": [ { "path": "{{_game.Root.Replace("\\", "\\\\")}}", "profile": "P", "vanilla": false } ] }""");
        File.WriteAllText(Path.Combine(dir, "profiles.json"),
            """{ "profiles": { "P": { "name": "P", "mods": { "MyMod": { "version": ">=0.0.0", "enabled": true } } } } }""");
        var plan = _planner.Plan(_install, [Req("MyMod", "1.0.0")], SmmState.Load(dir));
        Assert.Contains("SMM_PROFILE", Codes(plan));
    }

    /// <summary>Local file system that fails when a directory is moved into a given destination.</summary>
    private sealed class FaultyFs(string root, string failMoveInto) : IInstallFileSystem
    {
        private readonly LocalInstallFileSystem _inner = new(root);
        public string DisplayRoot => _inner.DisplayRoot;
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => _inner.ListFiles(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);
        public void WriteFile(string path, Stream contents) => _inner.WriteFile(path, contents);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
        public void MoveDirectory(string from, string to)
        {
            if (to == failMoveInto) throw new IOException("simulated failure");
            _inner.MoveDirectory(from, to);
        }
    }
}

public class AdoptTests
{
    [Fact]
    public void TakesOverAnSmmModAndKeepsItsMarker()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path); // SML installed by SMM

        var from = new Installer().Adopt(GameInstall.Local("", tmp.Path), "SML", new NoGameRunning());

        Assert.Equal("SMM", from);
        Assert.False(game.Exists("FactoryGame/Mods/SML/.smm"));
        var marker = SdmmMarker.TryParse(game.Read("FactoryGame/Mods/SML/.sdmm.json"))!;
        Assert.Equal("SMM", marker.AdoptedFrom);
        Assert.Equal("ff2f1d5a", marker.PreviousSmmMarker);
        Assert.Equal("3.12.0", marker.Version);
        Assert.Equal(ModOwner.Sdmm, ModScanner.Scan(new LocalInstallFileSystem(tmp.Path)).Single(m => m.ModReference == "SML").Owner);
        Assert.True(game.Exists("FactoryGame/Mods/SML/SML.uplugin")); // files untouched
    }

    [Fact]
    public void TakesOverAHandInstalledMod()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path).Write("FactoryGame/Mods/Hand/Hand.uplugin", """{ "SemVersion": "1.0.0" }""");

        Assert.Equal("other", new Installer().Adopt(GameInstall.Local("", tmp.Path), "Hand", new NoGameRunning()));
        Assert.Null(SdmmMarker.TryParse(game.Read("FactoryGame/Mods/Hand/.sdmm.json"))!.PreviousSmmMarker);
    }

    [Fact]
    public void RefusesWhileTheGameRunsAndForBrokenOrDuplicateCopies()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Path)
            .Write("FactoryGame/Mods/Broken/readme.txt", "no uplugin")
            .Write("FactoryGame/Mods/Twice/Twice.uplugin", """{ "SemVersion": "1.0.0" }""")
            .Write("FactoryGame/Mods/GameFeatures/Twice/Twice.uplugin", """{ "SemVersion": "1.0.0" }""");
        var install = GameInstall.Local("", tmp.Path);
        var installer = new Installer();

        Assert.Throws<InvalidOperationException>(() => installer.Adopt(install, "SML", new GameRunning()));
        Assert.Throws<InvalidOperationException>(() => installer.Adopt(install, "Broken", new NoGameRunning()));
        Assert.Throws<InvalidOperationException>(() => installer.Adopt(install, "Twice", new NoGameRunning()));
    }

    [Fact]
    public void ATakenOverModCanBeReplacedByADevBuild()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path).Write("FactoryGame/Mods/ModA/ModA.uplugin", """{ "SemVersion": "1.0.0" }""");
        var install = GameInstall.Local("", tmp.Path);
        new Installer().Adopt(install, "ModA", new NoGameRunning());

        var pkg = Packaging.ModPackage.Open(Zips.Mod(tmp.Combine("z"), "ModA", "1.1.0"));
        var plan = new InstallPlanner(new NoGameRunning()).Plan(install, [new InstallRequest(pkg, new PackageSource("bob/ModA", "v1.1.0", "ModA-Windows.zip"))], null);

        Assert.False(plan.IsBlocked);
        Assert.Equal(ActionKind.Replace, plan.Actions.Single().Kind);
    }
}
