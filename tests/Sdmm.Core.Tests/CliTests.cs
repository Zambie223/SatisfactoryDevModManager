using Sdmm.Cli;

namespace Sdmm.Core.Tests;

public class CliTests
{
    [Fact]
    public void ParsesTheCommandItsArgumentsAndOptions()
    {
        var a = Args.Parse(["install", "AutoTrains", "v1.4.14", "--on", "IB Server", "-y", "--no-prereleases", "--config", @"D:\sdmm.json"]);

        Assert.Equal("install", a.Command);
        Assert.Equal(["AutoTrains", "v1.4.14"], a.Positional);
        Assert.Equal("IB Server", a.On);
        Assert.True(a.Yes);
        Assert.False(a.Prereleases);
        Assert.Equal(@"D:\sdmm.json", a.Config);
        Assert.False(a.AcceptNewHostKey);
        Assert.False(a.AllowFlagged);
    }

    [Fact]
    public void MatchOptions()
    {
        var a = Args.Parse(["MATCH", "--game", "1", "--server", "2", "--change", "Server"]);
        Assert.Equal(("match", "1", "2", "server"), (a.Command, a.Game, a.Server, a.Change));
    }

    [Theory]
    [InlineData("mods", "--frobnicate")]   // unknown option
    [InlineData("mods", "--on")]           // missing value
    [InlineData("mods", "--on", "--yes")]  // an option where a value should be
    [InlineData("match", "--change", "both")]
    public void BadArgumentsAreUsageErrors(params string[] args)
    {
        var ex = Assert.Throws<CliException>(() => Args.Parse(args));
        Assert.Equal(2, ex.Code);
    }

    [Fact]
    public void HelpCoversDevModsOnly()
    {
        foreach (var word in new[] { "black market", "sbm", "take over", "takeover", "hand back", "smr", "ficsit" })
            Assert.DoesNotContain(word, Program.Help, StringComparison.OrdinalIgnoreCase);
        foreach (var command in new[] { "installs", "mods", "install", "remove", "update", "match", "modpack" })
            Assert.Contains($"  {command}", Program.Help);
    }
}
