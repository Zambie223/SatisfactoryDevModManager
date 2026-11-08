using Sdmm.Core.Config;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void DataLivesInLocalAppDataRegardlessOfExeLocation()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SatisfactoryDevModManager");
        Assert.Equal(expected, AppConfig.DataDirectory);
        Assert.Equal(Path.Combine(expected, AppConfig.FileName), AppConfig.DefaultPath);
        Assert.Equal(Path.Combine(expected, "cache"), new AppConfig().ResolveCacheDirectory());
        Assert.Equal(@"D:\Cache", new AppConfig { CacheDirectory = @"D:\Cache" }.ResolveCacheDirectory());
    }

    [Fact]
    public void SaveCreatesTheFolder()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("new-folder", AppConfig.FileName);
        new AppConfig().Save(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ComputedIsRemoteIsNotWrittenToConfig()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine(AppConfig.FileName);
        var cfg = new AppConfig();
        cfg.Installs.Add(new InstallConfig { Name = "x", Path = "C:\\Games" });
        cfg.Save(path);
        Assert.DoesNotContain("isRemote", File.ReadAllText(path));
    }

    [Fact]
    public void ConfigRoundTripsAndKeepsSourcesConfigurable()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine(AppConfig.FileName);
        var cfg = new AppConfig();
        cfg.Sources.Users.AddRange(["alice", "bob"]);
        cfg.Sources.Repos.Add("someone/SomeMod");
        cfg.Installs.Add(new InstallConfig { Name = "Server", Kind = InstallKind.Sftp, Host = "example.host", Port = 2022, Path = "/home/container" });
        cfg.Save(path);

        Assert.Contains("\"kind\": \"Sftp\"", File.ReadAllText(path));
        var loaded = AppConfig.Load(path);
        Assert.Equal(["alice", "bob"], loaded.Sources.Users);
        Assert.Equal(["someone/SomeMod"], loaded.Sources.Repos);
        Assert.Equal((InstallKind.Sftp, "example.host", 2022), (loaded.Installs[0].Kind, loaded.Installs[0].Host, loaded.Installs[0].Port));
    }

    [Theory]
    [InlineData("https://github.com/alice", "alice")]
    [InlineData("https://github.com/bob/", "bob")]
    [InlineData("github.com/bob/ModA.git", "bob/ModA")]
    [InlineData("https://github.com/bob/ModA?tab=readme", "bob/ModA")]
    [InlineData("  bob  ", "bob")]
    public void NormalizesPastedGitHubLinks(string input, string expected) =>
        Assert.Equal(expected, Discovery.SourceConfig.NormalizeEntry(input));

    [Fact]
    public void MissingConfigGivesDefaults()
    {
        using var tmp = new TempDir();
        var cfg = AppConfig.Load(tmp.Combine("none.json"));
        Assert.Empty(cfg.Sources.Users);
        Assert.True(cfg.IncludePrereleases);
    }

    [Fact]
    public void SecretsRoundTripThroughDpapi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = SecretProtector.Protect("not-a-real-token");
        Assert.DoesNotContain("not-a-real-token", p);
        Assert.Equal("not-a-real-token", SecretProtector.Unprotect(p));
        Assert.Null(SecretProtector.Unprotect("not-base64!"));
    }

    [Fact]
    public void MarkerRoundTrips()
    {
        var m = new SdmmMarker { ModReference = "M", Version = "1.0.0", Target = ModTarget.LinuxServer, Sha256 = "ab" };
        var back = SdmmMarker.TryParse(m.ToJson())!;
        Assert.Equal((ModTarget.LinuxServer, "1.0.0"), (back.Target, back.Version));
        Assert.Contains("\"target\": \"LinuxServer\"", m.ToJson());
    }
}

public class InstallKeyTests
{
    [Fact]
    public void KeyIgnoresTheNameAndCaseOfLocalPaths()
    {
        var a = new Config.InstallConfig { Name = "Steam (C:)", Path = @"C:\Games\Satisfactory\" };
        var b = new Config.InstallConfig { Name = "Renamed", Path = @"c:\games\satisfactory" };
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void ServersWithDifferentHostsOrFoldersHaveDifferentKeys()
    {
        var a = new Config.InstallConfig { Kind = Config.InstallKind.Sftp, Host = "host-a", User = "bob", Path = "/" };
        var b = new Config.InstallConfig { Kind = Config.InstallKind.Sftp, Host = "host-b", User = "bob", Path = "/" };
        var c = new Config.InstallConfig { Kind = Config.InstallKind.Sftp, Host = "host-a", User = "bob", Path = "/game" };
        Assert.NotEqual(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
        Assert.Equal(a.Key, new Config.InstallConfig { Kind = Config.InstallKind.Sftp, Host = "HOST-A", User = "bob", Path = "/", Name = "x" }.Key);
    }
}
