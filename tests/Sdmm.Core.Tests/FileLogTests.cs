using Sdmm.Core.Diagnostics;
using Sdmm.Core.Installs;

namespace Sdmm.Core.Tests;

/// <summary>FileLog is process-wide, so these run on their own rather than alongside other tests.</summary>
[CollectionDefinition(nameof(FileLogCollection), DisableParallelization = true)]
public sealed class FileLogCollection;

[Collection(nameof(FileLogCollection))]
public sealed class FileLogTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose()
    {
        FileLog.Configure(_tmp.Combine("off"), detailed: false);
        FileLog.Close();
        _tmp.Dispose();
    }

    [Fact]
    public void TheDetailedLogIsOffByDefaultButCrashesAreAlwaysWritten()
    {
        FileLog.Configure(_tmp.Path, detailed: false);
        FileLog.Write("app", "should not appear");
        Assert.False(File.Exists(_tmp.Combine(FileLog.CrashFileName))); // no crash, no crash.log
        FileLog.WriteCrash("Boom", new InvalidOperationException("broken"));
        FileLog.Close();

        Assert.False(File.Exists(_tmp.Combine(FileLog.FileName)));
        var crash = File.ReadAllText(_tmp.Combine(FileLog.CrashFileName));
        Assert.Contains("[crash] Boom (SDMM ", crash);
        Assert.Contains("System.InvalidOperationException: broken", crash);
    }

    [Fact]
    public void ServerLoginNamesAreHiddenUnlessAskedFor()
    {
        try
        {
            FileLog.ShowLoginNames = false;
            FileLog.Configure(_tmp.Path, detailed: true);
            FileLog.Write("fs", "write sftp://alice@host.example:22/FactoryGame/Mods/x and FTPS://bob@h/ and ftp://carol@h:21/y");
            FileLog.WriteCrash("Boom", new IOException("can't reach sftp://dave@host/"));
            FileLog.ShowLoginNames = true;
            FileLog.Write("fs", "write sftp://erin@host/z");
            FileLog.Close();

            var log = File.ReadAllText(_tmp.Combine(FileLog.FileName));
            var crash = File.ReadAllText(_tmp.Combine(FileLog.CrashFileName));
            Assert.Contains("sftp://<login>@host.example:22/FactoryGame/Mods/x", log);
            Assert.Contains("FTPS://<login>@h/", log);
            Assert.Contains("ftp://<login>@h:21/y", log);
            foreach (var name in new[] { "alice", "bob", "carol", "dave" }) Assert.DoesNotContain(name, log + crash);
            Assert.Contains("sftp://<login>@host/", crash);
            Assert.Contains("sftp://erin@host/z", log); // shown once switched on
        }
        finally
        {
            FileLog.ShowLoginNames = false;
        }
    }

    [Fact]
    public void StaysUnderTheLimitAndDropsTheOldestLinesFirst()
    {
        FileLog.Configure(_tmp.Path, detailed: true);
        var filler = new string('x', 1000);
        for (int i = 0; i < 6000; i++) FileLog.Write("app", $"line {i:D5} {filler}"); // about 6 MB
        FileLog.Close();

        var path = _tmp.Combine(FileLog.FileName);
        Assert.True(new FileInfo(path).Length <= FileLog.MaxBytes, $"{new FileInfo(path).Length} bytes");
        var lines = File.ReadAllLines(path);
        Assert.Contains("Older lines were dropped", lines[0]);
        Assert.Contains("line 05999", lines[^1]);                 // the newest is kept
        Assert.DoesNotContain(lines, l => l.Contains("line 00000"));  // the oldest went first
        Assert.All(lines.Skip(1), l => Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[", l)); // no line cut in half
    }

    [Fact]
    public void ChangesToGameFoldersAreLoggedButReadsAreNot()
    {
        FileLog.Configure(_tmp.Path, detailed: true);
        var game = new FakeGame(_tmp.Combine("game"));
        var install = GameInstall.Local("", game.Root);
        install.FileSystem.CreateDirectory("FactoryGame/Mods/LogTest");
        install.FileSystem.WriteAllText("FactoryGame/Mods/LogTest/a.txt", "hi");
        _ = install.FileSystem.ReadAllText("FactoryGame/Mods/LogTest/a.txt");
        install.FileSystem.DeleteDirectory("FactoryGame/Mods/LogTest");
        FileLog.Close();

        var log = File.ReadAllText(_tmp.Combine(FileLog.FileName));
        Assert.Contains("create folder", log);
        Assert.Contains("write ", log);
        Assert.Contains("delete folder", log);
        Assert.DoesNotContain("read", log.Replace("Detailed logging", ""), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(install.LocalRoot); // the logging wrapper doesn't hide that it's a local folder
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/x/y.zip?X-Amz-Signature=abc&X-Amz-Credential=def", "https://objects.githubusercontent.com/x/y.zip?(hidden)")]
    [InlineData("https://www.virustotal.com/api/v3/files?apikey=secret", "https://www.virustotal.com/api/v3/files?(hidden)")]
    [InlineData("https://api.github.com/users/me/repos?per_page=100", "https://api.github.com/users/me/repos?per_page=100")]
    public void SecretLookingQueryStringsAreHidden(string url, string expected) =>
        Assert.Equal(expected, LoggingHandler.Describe(new Uri(url)));
}
