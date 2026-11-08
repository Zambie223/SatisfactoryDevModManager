using Sdmm.Core.Cache;

namespace Sdmm.Core.Tests;

public class CacheTests
{
    private static string Put(PackageCache cache, string owner, string repo, string tag, string asset, int bytes, DateTime written)
    {
        var dir = Path.Combine(cache.Root, "github", owner, repo, tag);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, asset);
        File.WriteAllBytes(path, new byte[bytes]);
        File.WriteAllText(path + ".sha256", "x");
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void PruneKeepsNewestBuildsPerAsset()
    {
        using var tmp = new TempDir();
        var cache = new PackageCache(tmp.Combine("cache"));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var v1 = Put(cache, "bob", "ModA", "v1.0.0", "ModA-Windows.zip", 100, t0);
        var v2 = Put(cache, "bob", "ModA", "v1.1.0", "ModA-Windows.zip", 100, t0.AddDays(1));
        var v3 = Put(cache, "bob", "ModA", "v1.2.0", "ModA-Windows.zip", 100, t0.AddDays(2));
        var server = Put(cache, "bob", "ModA", "v1.0.0", "ModA-LinuxServer.zip", 50, t0);   // different asset: own group
        var other = Put(cache, "bob", "ModB", "v1.0.0", "ModB-Windows.zip", 10, t0);

        Assert.Equal(360 + 5 * 1, cache.GetSizeBytes());   // zips plus 1-byte sidecars
        Assert.Equal(100, cache.Prune(2));

        Assert.False(File.Exists(v1));
        Assert.False(File.Exists(v1 + ".sha256"));
        Assert.True(File.Exists(v2) && File.Exists(v3) && File.Exists(server) && File.Exists(other));
    }

    [Fact]
    public void PruneIsLeastRecentlyUsedNotOldestVersion()
    {
        using var tmp = new TempDir();
        var cache = new PackageCache(tmp.Combine("cache"));
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var v1 = Put(cache, "bob", "ModA", "v1.0.0", "ModA-Windows.zip", 1, t0);
        var v2 = Put(cache, "bob", "ModA", "v1.1.0", "ModA-Windows.zip", 1, t0.AddDays(1));
        var v3 = Put(cache, "bob", "ModA", "v1.2.0", "ModA-Windows.zip", 1, t0.AddDays(2));
        File.SetLastWriteTimeUtc(v1, t0.AddDays(3));   // someone switched back to v1.0.0 (GetAsync touches the file)

        cache.Prune(2);
        Assert.True(File.Exists(v1) && File.Exists(v3));
        Assert.False(File.Exists(v2));
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        using var tmp = new TempDir();
        var cache = new PackageCache(tmp.Combine("cache"));
        Put(cache, "bob", "ModA", "v1.0.0", "ModA-Windows.zip", 10, DateTime.UtcNow);
        cache.Clear();
        Assert.Equal(0, cache.GetSizeBytes());
    }
}

public class ImportedZipsTests
{
    [Fact]
    public void ImportCopiesTheZipAndListsItAsABuild()
    {
        using var tmp = new TempDir();
        var src = Zips.Mod(tmp.Combine("src"), "ModA", "1.2.0-dev.1", "LinuxServer");
        var store = new ImportedZips(tmp.Combine("imported"));

        var pkg = store.Import(src);
        File.Delete(src); // the copy stays usable

        Assert.Equal(Packaging.ModTarget.LinuxServer, pkg.Target);
        var b = Assert.Single(store.List());
        Assert.Equal("ModA", b.ModReference);
        Assert.Equal("v1.2.0-dev.1", b.Tag);
        Assert.True(b.Prerelease);
        Assert.True(b.IsImported);
        Assert.True(File.Exists(b.LocalZips![Packaging.ModTarget.LinuxServer]));
    }

    [Fact]
    public void ARenamedZipGetsItsPlatformFromItsContents()
    {
        using var tmp = new TempDir();
        var src = Zips.Create(tmp.Combine("src"), "my build.zip",
            ("ModA.uplugin", Zips.UPlugin("1.0.0")), ("Content/Paks/WindowsServer/ModAFactoryGame-WindowsServer.pak", "pak"));

        var pkg = new ImportedZips(tmp.Combine("imported")).Import(src);

        Assert.Equal(Packaging.ModTarget.WindowsServer, pkg.Target);
        Assert.Equal("ModA-WindowsServer.zip", Path.GetFileName(pkg.ZipPath));
    }

    [Fact]
    public void AZipWithNoPlatformCluesIsRefused()
    {
        using var tmp = new TempDir();
        var src = Zips.Create(tmp.Combine("src"), "x.zip", ("ModA.uplugin", Zips.UPlugin("1.0.0")));
        Assert.Throws<Packaging.InvalidModPackageException>(() => new ImportedZips(tmp.Combine("imported")).Import(src));
    }

    [Fact]
    public void MergeOrdersBuildsNewestFirstAndAddsImportOnlyMods()
    {
        using var tmp = new TempDir();
        var store = new ImportedZips(tmp.Combine("imported"));
        store.Import(Zips.Mod(tmp.Combine("a"), "ModA", "1.1.0"));
        store.Import(Zips.Mod(tmp.Combine("b"), "Solo", "0.1.0"));
        var gh = new Discovery.ModBuild("ModA", "bob", "ModA", "v1.0.0", Versioning.SemVersion.Parse("1.0.0"), false, null, null, null,
            new Dictionary<Packaging.ModTarget, GitHub.GitHubAsset> { [Packaging.ModTarget.Windows] = new() { Id = 1, Name = "ModA-Windows.zip" } });

        var merged = ImportedZips.Merge([new Discovery.DiscoveredMod("ModA", [gh])], store.List()).ToDictionary(m => m.ModReference);

        Assert.Equal(["v1.1.0", "v1.0.0"], merged["ModA"].Builds.Select(b => b.Tag));
        Assert.True(merged["ModA"].Builds[0].IsImported);
        Assert.Single(merged["Solo"].Builds);
    }
}
