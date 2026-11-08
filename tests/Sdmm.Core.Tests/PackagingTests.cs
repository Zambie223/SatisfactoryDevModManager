using Sdmm.Core.Packaging;

namespace Sdmm.Core.Tests;

public class UPluginDescriptorTests
{
    [Fact]
    public void ParsesExampleModStyleDescriptor()
    {
        var d = UPluginDescriptor.Parse("ExampleMod", Zips.UPlugin("1.0.0", gameFeature: true));
        Assert.Equal("1.0.0", d.EffectiveVersion.ToString());
        Assert.True(d.GameFeature);
        Assert.Equal(">=491125", d.GameVersionText);
        var sml = Assert.Single(d.Plugins);
        Assert.Equal(("SML", "^3.12.0", false), (sml.Name, sml.SemVersionRange, sml.Optional));
    }

    [Fact]
    public void ToleratesCommentsAndTrailingCommas()
    {
        var d = UPluginDescriptor.Parse("M", """{ /* c */ "SemVersion": "2.0.0", "Plugins": [], }""");
        Assert.Equal("2.0.0", d.EffectiveVersion.ToString());
    }

    [Fact]
    public void FallsBackToUeVersionLikeSml()
    {
        var d = UPluginDescriptor.Parse("M", """{ "Version": 7 }""");
        Assert.False(d.HasValidSemVersion);
        Assert.Equal("7.0.0", d.EffectiveVersion.ToString());
    }
}

public class ModPackageTests
{
    [Fact]
    public void OpensAlpakitZip()
    {
        using var tmp = new TempDir();
        var zip = Zips.Mod(tmp.Path, "ExampleMod", "1.0.1-dev.1", gameFeature: true);
        var p = ModPackage.Open(zip);
        Assert.Equal("ExampleMod", p.ModReference);
        Assert.Equal(ModTarget.Windows, p.Target);
        Assert.Equal(64, p.Sha256.Length);
        Assert.Contains("Content/Paks/Windows/ExampleModFactoryGame-Windows.pak", p.Files);
    }

    [Fact]
    public void RejectsUpluginNotAtRoot()
    {
        using var tmp = new TempDir();
        var zip = Zips.Create(tmp.Path, "M-Windows.zip", ("M/M.uplugin", Zips.UPlugin("1.0.0")));
        Assert.Throws<InvalidModPackageException>(() => ModPackage.Open(zip));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Content/../../evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("/abs.txt")]
    public void RejectsUnsafeEntryPaths(string entry)
    {
        using var tmp = new TempDir();
        var zip = Zips.Create(tmp.Path, "M-Windows.zip", ("M.uplugin", Zips.UPlugin("1.0.0")), (entry, "x"));
        Assert.Throws<InvalidModPackageException>(() => ModPackage.Open(zip));
    }

    [Fact]
    public void RejectsFileNameThatDisagreesWithUplugin()
    {
        using var tmp = new TempDir();
        var zip = Zips.Create(tmp.Path, "Other-Windows.zip", ("M.uplugin", Zips.UPlugin("1.0.0")));
        Assert.Throws<InvalidModPackageException>(() => ModPackage.Open(zip));
    }

    [Theory]
    [InlineData("ExampleMod-Windows.zip", "ExampleMod", ModTarget.Windows)]
    [InlineData("ExampleMod-WindowsServer.zip", "ExampleMod", ModTarget.WindowsServer)]
    [InlineData("My_Mod2-LinuxServer.zip", "My_Mod2", ModTarget.LinuxServer)]
    public void ParsesAssetNames(string name, string modRef, ModTarget target)
    {
        Assert.True(ModPackage.TryParseAssetName(name, out var r, out var t));
        Assert.Equal((modRef, target), (r, t));
    }

    [Theory]
    [InlineData("ExampleMod.zip")]
    [InlineData("ExampleMod-Mac.zip")]
    [InlineData("Example-Mod-Windows.zip")]
    [InlineData("source.tar.gz")]
    public void IgnoresOtherAssets(string name) => Assert.False(ModPackage.TryParseAssetName(name, out _, out _));
}
