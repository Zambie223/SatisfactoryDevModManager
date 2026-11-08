using System.Text;
using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;
using Sdmm.Remote;

namespace Sdmm.Core.Tests;

public class RemotePathTests
{
    [Theory]
    [InlineData("/home/container", "FactoryGame/Mods", "/home/container/FactoryGame/Mods")]
    [InlineData("/home/container/", "FactoryGame/Mods/", "/home/container/FactoryGame/Mods")]
    [InlineData("/", "FactoryGame/Mods", "/FactoryGame/Mods")]
    [InlineData("", "FactoryGame", "/FactoryGame")]
    [InlineData("\\srv\\game", "a\\b", "/srv/game/a/b")]
    public void Joins(string root, string rel, string expected) =>
        Assert.Equal(expected, RemotePath.Join(RemotePath.NormalizeRoot(root), rel));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("FactoryGame/../../x")]
    [InlineData("./x")]
    public void RefusesEscapes(string rel) =>
        Assert.Throws<InvalidOperationException>(() => RemotePath.Join("/home/container", rel));
}

public class HostKeyTrustTests
{
    [Fact]
    public void AcceptsAndRemembersFirstKeyOnlyWhenConfirmed()
    {
        string? saved = null;
        var trust = new HostKeyTrust(null, _ => true) { Accepted = f => saved = f };
        Assert.True(trust.Check("SHA256:aaa"));
        Assert.Equal("SHA256:aaa", saved);
        Assert.True(trust.Check("SHA256:aaa"));
        Assert.False(trust.Check("SHA256:bbb")); // changed key in the same session
        Assert.Contains("changed", trust.Rejected);

        Assert.False(new HostKeyTrust(null, _ => false).Check("SHA256:aaa"));
    }

    [Fact]
    public void RefusesChangedSavedKeyWithoutAsking()
    {
        bool asked = false;
        var trust = new HostKeyTrust("SHA256:old", _ => asked = true);
        Assert.False(trust.Check("SHA256:new"));
        Assert.False(asked);
    }
}

public class ServerSyncTests
{
    private static InstalledMod Mod(string modRef, string version, ModOwner owner, string? repo = null, string? tag = null, string extra = "") =>
        new(modRef, $"FactoryGame/Mods/{modRef}", false, owner,
            UPluginDescriptor.Parse(modRef, $$"""{ "SemVersion": "{{version}}"{{extra}} }"""),
            owner == ModOwner.Sdmm ? new SdmmMarker { ModReference = modRef, Version = version, Sha256 = "x", SourceRepo = repo, SourceTag = tag } : null,
            null);

    private const string ServerSideOnly = """, "RequiredOnRemote": false""";

    private static ModBuild Build(string modRef, string tag, params ModTarget[] targets) =>
        new(modRef, "friend", modRef, tag, SemVersion.Parse(tag), false, null, null, null,
            targets.ToDictionary(t => t, t => new GitHubAsset { Id = 1, Name = $"{modRef}-{t}.zip" }));

    [Fact]
    public void ServerIsTheSourceOfTruth()
    {
        var server = new[]
        {
            Mod("ModA", "0.7.1", ModOwner.Sdmm, "friend/ModA", "v0.7.1"),
            Mod("ModB", "1.4.7", ModOwner.Sdmm, "friend/ModB", "v1.4.7"),
            Mod("SML", "3.12.0", ModOwner.Smm),
            Mod("Unknown", "1.0.0", ModOwner.Sdmm, "someone/Unknown", "v1.0.0"),
            Mod("Same", "2.0.0", ModOwner.Sdmm),
        };
        var local = new[]
        {
            Mod("ModA", "0.7.0", ModOwner.Sdmm),
            Mod("ModB", "1.4.5", ModOwner.Smm),
            Mod("SML", "3.12.0", ModOwner.Smm),
            Mod("Same", "2.0.0", ModOwner.Sdmm),
            Mod("LocalOnly", "0.1.0", ModOwner.Sdmm),
        };
        var discovery = new DiscoveryResult(
        [
            new DiscoveredMod("ModA", [Build("ModA", "v0.7.1", ModTarget.Windows, ModTarget.LinuxServer), Build("ModA", "v0.7.0", ModTarget.Windows)]),
            new DiscoveredMod("ModB", [Build("ModB", "v1.4.7", ModTarget.Windows)]),
        ], [], []);

        var items = ServerSync.Compare(server, local, discovery).ToDictionary(i => i.ModReference);

        Assert.Equal(SyncStatus.Install, items["ModA"].Status);
        Assert.Equal("v0.7.1", items["ModA"].Build!.Tag);
        Assert.Equal(SyncStatus.ManagedElsewhere, items["ModB"].Status);   // local copy is SMM's
        Assert.Equal(SyncStatus.InSync, items["SML"].Status);
        Assert.Equal(SyncStatus.BuildNotFound, items["Unknown"].Status);
        Assert.Equal(SyncStatus.InSync, items["Same"].Status);
        Assert.Equal(SyncStatus.Remove, items["LocalOnly"].Status);       // SML requires it on both sides by default
        Assert.True(items["LocalOnly"].IsActionable);
    }

    [Fact]
    public void ServerSideOnlyModsAreNotNeededLocally()
    {
        // Like DepotNetworkFix, a server-only mod whose .uplugin sets RequiredOnRemote to false.
        var server = new[] { Mod("DepotFix", "1.0.0", ModOwner.Sdmm, "friend/DepotFix", "v1.0.0", ServerSideOnly), Mod("HostMod", "1.0.0", ModOwner.Unmanaged, extra: ServerSideOnly) };
        var discovery = new DiscoveryResult([new DiscoveredMod("DepotFix", [Build("DepotFix", "v1.0.0", ModTarget.LinuxServer)])], [], []);

        var items = ServerSync.Compare(server, [], discovery).ToDictionary(i => i.ModReference);

        Assert.Equal(SyncStatus.ServerOnly, items["DepotFix"].Status);
        Assert.Equal(SyncStatus.ServerOnly, items["HostMod"].Status);
        Assert.All(items.Values, i => Assert.False(i.IsActionable));
    }

    [Fact]
    public void ServerSideOnlyModPresentOnBothSidesStillNeedsMatchingVersions()
    {
        // SML only skips a RequiredOnRemote:false mod when the other side lacks it; if both have it, the ranges apply.
        var server = new[] { Mod("DepotFix", "1.1.0", ModOwner.Sdmm, "friend/DepotFix", "v1.1.0", ServerSideOnly) };
        var local = new[] { Mod("DepotFix", "1.0.0", ModOwner.Sdmm, extra: ServerSideOnly) };
        var discovery = new DiscoveryResult([new DiscoveredMod("DepotFix", [Build("DepotFix", "v1.1.0", ModTarget.Windows)])], [], []);

        Assert.Equal(SyncStatus.Install, ServerSync.Compare(server, local, discovery).Single().Status);
    }

    [Fact]
    public void ClientSideOnlyModsAreNotNeededOnTheServer()
    {
        var local = new[] { Mod("UiTweak", "2.0.0", ModOwner.Sdmm, extra: ServerSideOnly), Mod("SML", "3.12.0", ModOwner.Smm) };

        var items = ServerSync.Compare([], local, new DiscoveryResult([], [], [])).ToDictionary(i => i.ModReference);

        Assert.Equal(SyncStatus.ClientOnly, items["UiTweak"].Status);
        Assert.Equal(SyncStatus.ClientOnly, items["SML"].Status); // the client-side check ignores SML
    }

    [Fact]
    public void LocalModsNotOnTheServerFromSmmAreReportedNotRemoved()
    {
        var items = ServerSync.Compare([], [Mod("SmmMod", "1.0.0", ModOwner.Smm)], new DiscoveryResult([], [], []));
        Assert.Equal(SyncStatus.ManagedElsewhere, items.Single().Status);
        Assert.False(items.Single().IsActionable);
    }

    [Theory]
    // Server range accepts the client version and vice versa: compatible although the versions differ.
    [InlineData("1.2.0", """, "RemoteVersionRange": "^1.0.0" """, "1.1.0", """, "RemoteVersionRange": "^1.0.0" """, SyncStatus.InSync)]
    // Default ranges ("&gt;= own version"): the older side refuses, so they must match.
    [InlineData("1.2.0", "", "1.1.0", "", SyncStatus.Install)]
    // The server accepts anything, but the client's default range wants >= 1.3.0 from the server.
    [InlineData("1.2.0", """, "RemoteVersionRange": "*" """, "1.3.0", "", SyncStatus.Install)]
    public void VersionsAreComparedWithEachSidesRemoteVersionRange(string sv, string sExtra, string lv, string lExtra, SyncStatus expected)
    {
        var server = new[] { Mod("ModA", sv, ModOwner.Sdmm, "friend/ModA", "v" + sv, sExtra) };
        var local = new[] { Mod("ModA", lv, ModOwner.Sdmm, extra: lExtra) };
        var discovery = new DiscoveryResult([new DiscoveredMod("ModA", [Build("ModA", "v" + sv, ModTarget.Windows)])], [], []);

        Assert.Equal(expected, ServerSync.Compare(server, local, discovery).Single().Status);
    }

    [Fact]
    public void AServerBuildFromAnImportedZipMatchesTheImportedCopy()
    {
        var server = new[] { Mod("ModA", "1.0.0", ModOwner.Sdmm, repo: null, tag: "v1.0.0") };
        var gh = Build("ModA", "v1.0.0", ModTarget.Windows);
        var imported = Build("ModA", "v1.0.0", ModTarget.Windows) with
        {
            Owner = "imported",
            LocalZips = new Dictionary<ModTarget, string> { [ModTarget.Windows] = "x.zip" },
        };
        var discovery = new DiscoveryResult([new DiscoveredMod("ModA", [gh, imported])], [], []);

        var item = ServerSync.Compare(server, [], discovery).Single();

        Assert.Equal(SyncStatus.Install, item.Status);
        Assert.True(item.Build!.IsImported);
        Assert.Contains("Install from zip", ServerSync.Compare(server, [], new DiscoveryResult([], [], [])).Single().Message);
    }

    [Fact]
    public void TheServerCanBeMadeToMatchTheGame()
    {
        var local = new[]
        {
            Mod("ModA", "1.1.0", ModOwner.Sdmm, "friend/ModA", "v1.1.0"),
            Mod("UiTweak", "2.0.0", ModOwner.Sdmm, "friend/UiTweak", "v2.0.0", ServerSideOnly), // client-only: stays off the server
            Mod("FromSmr", "3.0.0", ModOwner.Sdmm, ServerSync.SmrSourcePrefix + "FromSmr", "v3.0.0"),
            Mod("SmmMod", "1.0.0", ModOwner.Smm),
        };
        var server = new[]
        {
            Mod("ModA", "1.0.0", ModOwner.Sdmm, "friend/ModA", "v1.0.0"),
            Mod("DepotFix", "1.0.0", ModOwner.Sdmm, "friend/DepotFix", "v1.0.0", ServerSideOnly), // server-only: kept
            Mod("OldDev", "0.1.0", ModOwner.Sdmm, "friend/OldDev", "v0.1.0"),                      // not in the game: removed
            Mod("HostMod", "1.0.0", ModOwner.Unmanaged),                                            // not ours: reported only
        };
        var discovery = new DiscoveryResult([new DiscoveredMod("ModA", [Build("ModA", "v1.1.0", ModTarget.Windows, ModTarget.LinuxServer)])], [], []);

        var items = ServerSync.Compare(server, local, discovery, ModTarget.LinuxServer, SyncDirection.ServerMatchesGame).ToDictionary(i => i.ModReference);

        Assert.Equal(SyncStatus.Install, items["ModA"].Status);
        Assert.Equal("1.0.0", items["ModA"].ServerVersion);
        Assert.Equal("1.1.0", items["ModA"].LocalVersion);
        Assert.Equal(SyncStatus.ClientOnly, items["UiTweak"].Status);
        Assert.Equal(SyncStatus.FromSmr, items["FromSmr"].Status);
        Assert.Equal("3.0.0", items["FromSmr"].SmrVersion);
        Assert.Equal(SyncStatus.FromSmr, items["SmmMod"].Status); // SMM's mods come from SMR
        Assert.Equal(SyncStatus.ServerOnly, items["DepotFix"].Status);
        Assert.Equal(SyncStatus.Remove, items["OldDev"].Status);
        Assert.Equal(SyncStatus.ManagedElsewhere, items["HostMod"].Status);
    }

    [Fact]
    public void PushingToTheServerNeedsAServerBuild()
    {
        var local = new[] { Mod("ModA", "1.1.0", ModOwner.Sdmm, "friend/ModA", "v1.1.0") };
        var discovery = new DiscoveryResult([new DiscoveredMod("ModA", [Build("ModA", "v1.1.0", ModTarget.Windows)])], [], []);

        var item = ServerSync.Compare([], local, discovery, ModTarget.LinuxServer, SyncDirection.ServerMatchesGame).Single();

        Assert.Equal(SyncStatus.BuildNotFound, item.Status);
        Assert.Contains("LinuxServer", item.Message);
    }

    [Fact]
    public void InstallNeedsABuildForTheLocalPlatform()
    {
        var server = new[] { Mod("ModA", "1.0.0", ModOwner.Sdmm, "friend/ModA", "v1.0.0") };
        var discovery = new DiscoveryResult([new DiscoveredMod("ModA", [Build("ModA", "v1.0.0", ModTarget.LinuxServer)])], [], []);

        Assert.Equal(SyncStatus.BuildNotFound, ServerSync.Compare(server, [], discovery, ModTarget.Windows).Single().Status);
        Assert.Equal(SyncStatus.Install, ServerSync.Compare(server, [], discovery, ModTarget.LinuxServer).Single().Status);
    }
}

public class GameLocatorTests
{
    [Fact]
    public void ReadsSteamLibrariesAndTheAppManifest()
    {
        using var tmp = new TempDir();
        var steam = tmp.Combine("Steam");
        var lib2 = tmp.Combine("Lib2");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{steam.Replace(@"\", @"\\")}}"
            		"apps" { "240" "1" }
            	}
            	"1"
            	{
            		"path"		"{{lib2.Replace(@"\", @"\\")}}"
            		"apps" { "526870" "30378398200" }
            	}
            }
            """);
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "SatisfactoryGame"));
        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_526870.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"526870\"\n\t\"installdir\"\t\t\"SatisfactoryGame\"\n}");

        var found = GameLocator.FindSteam(steam);

        var g = Assert.Single(found);
        Assert.Equal(Path.Combine(lib2, "steamapps", "common", "SatisfactoryGame"), g.Path);
        Assert.Equal(GameStore.Steam, g.Store);
    }

    [Fact]
    public void MissingSteamFindsNothing() => Assert.Empty(GameLocator.FindSteam(null));

    [Theory]
    [InlineData("""{ "AppName": "CrabEA", "DisplayName": "Satisfactory", "InstallLocation": "D:\\Epic\\Satisfactory" }""", true, null)]
    [InlineData("""{ "AppName": "CrabTest", "DisplayName": "Satisfactory Experimental", "InstallLocation": "D:\\Epic\\SatisfactoryExperimental" }""", true, "Experimental")]
    [InlineData("""{ "AppName": "Fortnite", "DisplayName": "Fortnite", "InstallLocation": "D:\\Epic\\Fortnite" }""", false, null)]
    [InlineData("not json", false, null)]
    public void ReadsEpicManifests(string json, bool isGame, string? label)
    {
        var g = GameLocator.ParseEpicManifest(json);
        Assert.Equal(isGame, g is not null);
        if (g is not null) Assert.Equal(label, g.Label);
    }
}

/// <summary>Runs the real planner and installer against a Linux-like (case-sensitive) in-memory server.</summary>
public class RemoteInstallTests
{
    [Fact]
    public void InstallsLinuxServerBuildOnRemoteFileSystem()
    {
        using var tmp = new TempDir();
        var fs = new MemoryFs();
        fs.WriteAllText("Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version", """{ "Changelist": 517554 }""");
        fs.WriteAllText("FactoryGame/Mods/SML/SML.uplugin", """{ "SemVersion": "3.12.0" }""");
        fs.WriteAllText("FactoryGame/Mods/SML/.smm", "hash");
        var install = new GameInstall("server", fs);

        var zip = Zips.Mod(tmp.Path, "ModA", "0.7.1", "LinuxServer");
        var plan = new InstallPlanner(new NoGameRunning()).Plan(install,
            [new InstallRequest(ModPackage.Open(zip), new PackageSource("friend/ModA", "v0.7.1", null))], smm: null);

        Assert.Equal(ModTarget.LinuxServer, plan.Game.Target);
        Assert.False(plan.IsBlocked);
        Assert.Contains(plan.Issues, i => i.Code == "SERVER_RESTART");
        new Installer().Execute(plan);

        Assert.True(fs.FileExists("FactoryGame/Mods/ModA/ModA.uplugin"));
        Assert.False(fs.FileExists("factorygame/mods/moda/moda.uplugin")); // proves case-sensitivity of the fake
        Assert.False(fs.DirectoryExists(Installer.StagingDir));
        var scanned = ModScanner.Scan(fs).Single(m => m.ModReference == "ModA");
        Assert.Equal((ModOwner.Sdmm, "v0.7.1"), (scanned.Owner, scanned.Marker!.SourceTag));
    }

    [Fact]
    public void TargetOverrideWorksWhenHostHidesEngineFolder()
    {
        var fs = new MemoryFs();
        fs.CreateDirectory("FactoryGame/Mods");
        Assert.Throws<InvalidOperationException>(() => new GameInstall("s", fs).Detect());
        var info = new GameInstall("s", fs, ModTarget.LinuxServer).Detect();
        Assert.Equal((ModTarget.LinuxServer, 0L), (info.Target, info.Changelist));
    }

    /// <summary>Case-sensitive in-memory file system with POSIX-like rename semantics.</summary>
    private sealed class MemoryFs : IInstallFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal) { "" };

        public string DisplayRoot => "memory://server";

        private static string N(string p) => string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries));

        private void EnsureDirs(string dir)
        {
            var parts = N(dir).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i <= parts.Length; i++) _dirs.Add(string.Join('/', parts.Take(i)));
        }

        private static string ParentOf(string p) { var n = N(p); int i = n.LastIndexOf('/'); return i < 0 ? "" : n[..i]; }

        public bool FileExists(string path) => _files.ContainsKey(N(path));
        public bool DirectoryExists(string path) => _dirs.Contains(N(path));

        private IEnumerable<string> Children(IEnumerable<string> set, string dir)
        {
            var d = N(dir);
            var prefix = d.Length == 0 ? "" : d + "/";
            return set.Where(x => x.StartsWith(prefix, StringComparison.Ordinal) && x.Length > prefix.Length && !x[prefix.Length..].Contains('/'))
                      .Select(x => x[prefix.Length..]);
        }

        public IReadOnlyList<string> ListDirectories(string path) => Children(_dirs, path).ToList();
        public IReadOnlyList<string> ListFiles(string path) => Children(_files.Keys, path).ToList();
        public string ReadAllText(string path) => Encoding.UTF8.GetString(_files[N(path)]);

        public void WriteAllText(string path, string contents)
        {
            EnsureDirs(ParentOf(path));
            _files[N(path)] = Encoding.UTF8.GetBytes(contents);
        }

        public void WriteFile(string path, Stream contents)
        {
            if (_files.ContainsKey(N(path))) throw new IOException("exists");
            EnsureDirs(ParentOf(path));
            using var ms = new MemoryStream();
            contents.CopyTo(ms);
            _files[N(path)] = ms.ToArray();
        }

        public void CreateDirectory(string path) => EnsureDirs(path);

        public void DeleteDirectory(string path)
        {
            var d = N(path);
            foreach (var f in _files.Keys.Where(k => k.StartsWith(d + "/", StringComparison.Ordinal)).ToList()) _files.Remove(f);
            _dirs.RemoveWhere(x => x == d || x.StartsWith(d + "/", StringComparison.Ordinal));
        }

        public void MoveDirectory(string from, string to)
        {
            var f = N(from); var t = N(to);
            if (_dirs.Contains(t) || _files.ContainsKey(t)) throw new IOException("destination exists");
            if (!_dirs.Contains(f)) throw new IOException("source missing");
            EnsureDirs(ParentOf(t));
            foreach (var k in _files.Keys.Where(k => k.StartsWith(f + "/", StringComparison.Ordinal)).ToList())
            {
                _files[t + k[f.Length..]] = _files[k];
                _files.Remove(k);
            }
            foreach (var x in _dirs.Where(x => x == f || x.StartsWith(f + "/", StringComparison.Ordinal)).ToList())
            {
                _dirs.Remove(x);
                _dirs.Add(t + x[f.Length..]);
            }
        }
    }
}

public class GameRootFinderTests
{
    [Fact]
    public void FindsTheGameFolderBelowTheLoginRoot()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Combine("home", "container"), "Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version");
        Directory.CreateDirectory(tmp.Combine("backups"));
        var fs = new LocalInstallFileSystem(tmp.Path);

        Assert.Equal("home/container", GameRootFinder.Find(fs));
    }

    [Fact]
    public void TheRootItselfIsEmptyAndTooDeepIsNull()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Path);
        Assert.Equal("", GameRootFinder.Find(new LocalInstallFileSystem(tmp.Path)));

        using var deep = new TempDir();
        new FakeGame(deep.Combine("a", "b", "c"));
        Assert.Null(GameRootFinder.Find(new LocalInstallFileSystem(deep.Path), maxDepth: 2));
    }
}

public class DetectWithoutEngineTests
{
    [Theory]
    [InlineData("FactoryGame/Binaries/Linux/FactoryServer-Linux-Shipping", Packaging.ModTarget.LinuxServer)]
    [InlineData("FactoryGame/Binaries/Win64/FactoryServer-Win64-Shipping.exe", Packaging.ModTarget.WindowsServer)]
    [InlineData("FactoryGame/Binaries/Win64/FactoryGameSteam-Win64-Shipping.exe", Packaging.ModTarget.Windows)]
    public void ThePlatformComesFromTheProgramWhenEngineIsHidden(string program, Packaging.ModTarget expected)
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("FactoryGame", "Mods"));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp.Combine(program.Split('/')))!);
        File.WriteAllText(tmp.Combine(program.Split('/')), "x");

        var info = GameInstall.Local("", tmp.Path).Detect();

        Assert.Equal(expected, info.Target);
        Assert.Equal(0, info.Changelist);
    }

    [Fact]
    public void TheErrorSaysWhatTheFolderContains()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("backups"));
        Directory.CreateDirectory(tmp.Combine("satisfactory"));

        var ex = Assert.Throws<InvalidOperationException>(() => GameInstall.Local("", tmp.Path).Detect());
        Assert.Contains("backups", ex.Message);
        Assert.Contains("satisfactory", ex.Message);
    }

    [Fact]
    public void FactoryGameWithoutAnyPlatformCluePointsAtThePlatformSetting()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("FactoryGame", "Content"));
        var ex = Assert.Throws<InvalidOperationException>(() => GameInstall.Local("", tmp.Path).Detect());
        Assert.Contains("Set Platform", ex.Message);
    }
}

public class GameRootFinderAllTests
{
    [Fact]
    public void ReportsEveryMatchAtTheShallowestLevelModdedFirst()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("backup", "FactoryGame"));
        new FakeGame(tmp.Combine("server"));          // has FactoryGame/Mods
        new FakeGame(tmp.Combine("x", "y", "deeper"));  // deeper: not reported

        var all = GameRootFinder.FindAll(new LocalInstallFileSystem(tmp.Path));

        Assert.Equal(["server", "backup"], all);
    }

    [Fact]
    public void SearchesThreeLevelsByDefault()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Combine("a", "b", "c"));
        Assert.Equal("a/b/c", GameRootFinder.Find(new LocalInstallFileSystem(tmp.Path)));
    }
}

public class ModsFolderAccessTests
{
    /// <summary>A local folder that refuses changes under FactoryGame/Mods, like a host that locks it while the server runs.</summary>
    private sealed class LockedModsFs(string root) : IInstallFileSystem
    {
        private readonly LocalInstallFileSystem _inner = new(root);
        private static void Guard(string path)
        {
            if (path.StartsWith(InstallPaths.ModsDir, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Permission denied");
        }
        public string DisplayRoot => _inner.DisplayRoot;
        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);
        public IReadOnlyList<string> ListFiles(string path) => _inner.ListFiles(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public void WriteAllText(string path, string contents) { Guard(path); _inner.WriteAllText(path, contents); }
        public void WriteFile(string path, Stream contents) { Guard(path); _inner.WriteFile(path, contents); }
        public void CreateDirectory(string path) { Guard(path); _inner.CreateDirectory(path); }
        public void DeleteDirectory(string path) { Guard(path); _inner.DeleteDirectory(path); }
        public void MoveDirectory(string from, string to) { Guard(from); Guard(to); _inner.MoveDirectory(from, to); }
    }

    [Fact]
    public void AnUnlockedModsFolderPassesAndLeavesNothingBehind()
    {
        using var tmp = new TempDir();
        var game = new FakeGame(tmp.Path);

        Assert.Null(ModsFolderAccess.Check(new LocalInstallFileSystem(tmp.Path)));
        Assert.False(game.Exists("FactoryGame/.sdmm-staging"));
        Assert.DoesNotContain(Directory.GetDirectories(game.Full("FactoryGame/Mods")), d => Path.GetFileName(d).StartsWith(ModsFolderAccess.ProbePrefix));
    }

    [Fact]
    public void ALockedModsFolderIsReported()
    {
        using var tmp = new TempDir();
        new FakeGame(tmp.Path);

        var error = ModsFolderAccess.Check(new LockedModsFs(tmp.Path));

        Assert.Contains("Permission denied", error);
        Assert.Empty(Directory.GetDirectories(tmp.Combine("FactoryGame", ".sdmm-staging"))); // the staged probe was cleaned up
    }
}
