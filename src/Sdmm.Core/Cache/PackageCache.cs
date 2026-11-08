using System.Security.Cryptography;
using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Cache;

/// <summary>
/// Downloaded zips, kept so that reinstalling or reverting to an earlier build needs no download.
/// Layout: &lt;root&gt;/github/&lt;owner&gt;/&lt;repo&gt;/&lt;tag&gt;/&lt;asset&gt;.zip, and for SMR downloads
/// &lt;root&gt;/smr/&lt;ModRef&gt;/&lt;version&gt;/&lt;ModRef&gt;-&lt;Target&gt;.zip, each with a ".sha256" sidecar.
/// </summary>
public sealed class PackageCache
{
    public string Root { get; }

    public PackageCache(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string PathFor(ModBuild build, GitHubAsset asset) =>
        Path.Combine(Root, "github", Safe(build.Owner), Safe(build.Repo), Safe(build.Tag), Safe(asset.Name));

    /// <summary>Returns the cached zip for a build target, downloading it first if needed.</summary>
    public async Task<ModPackage> GetAsync(GitHubClient gh, ModBuild build, ModTarget target,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!build.Assets.TryGetValue(target, out var asset))
            throw new InvalidOperationException($"{build.ModReference} {build.Tag} has no {target} build.");

        var path = PathFor(build, asset);
        var shaPath = path + ".sha256";
        if (File.Exists(path) && File.Exists(shaPath))
        {
            var sha = File.ReadAllText(shaPath).Trim();
            // Re-verify: a cached file that changed on disk is downloaded again.
            if (new FileInfo(path).Length == asset.Size && ModPackage.ComputeSha256(path) == sha && DigestMatches(asset, sha))
            {
                // Mark as recently used, so Prune keeps a build you just switched back to.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return ModPackage.Open(path, sha);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        string hash;
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                await gh.DownloadAssetAsync(build.Owner, build.Repo, asset.Id, fs, progress, ct);

            hash = ModPackage.ComputeSha256(part);
            if (!DigestMatches(asset, hash))
                throw new InvalidDataException($"{asset.Name}: downloaded file's SHA-256 {hash} does not match GitHub's digest {asset.Digest}.");
            if (new FileInfo(part).Length != asset.Size)
                throw new InvalidDataException($"{asset.Name}: downloaded {new FileInfo(part).Length} bytes, expected {asset.Size}.");
            File.Move(part, path, overwrite: true);
            File.WriteAllText(shaPath, hash);
            Diagnostics.FileLog.Write("cache", $"downloaded {path} (sha256 {hash})");
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }

        return ModPackage.Open(path, hash);
    }

    /// <summary>
    /// Returns the cached SMR zip for a version's platform, downloading it first if needed. SMR's SHA-256 is checked
    /// on download and again when a cached copy is reused.
    /// </summary>
    public async Task<ModPackage> GetSmrAsync(Smr.SmrClient smr, Smr.SmrVersion version, ModTarget target,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!version.Targets.TryGetValue(target, out var t))
            throw new InvalidOperationException($"{version.ModReference} {version.Version} has no {target} build on SMR.");
        var path = Path.Combine(Root, "smr", Safe(version.ModReference), Safe(version.Version.ToString()), $"{Safe(version.ModReference)}-{target}.zip");
        var shaPath = path + ".sha256";
        if (File.Exists(path) && File.Exists(shaPath))
        {
            var sha = File.ReadAllText(shaPath).Trim();
            if ((t.Sha256 is null || sha.Equals(t.Sha256, StringComparison.OrdinalIgnoreCase)) && ModPackage.ComputeSha256(path) == sha)
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return ModPackage.Open(path, sha);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                await smr.DownloadAsync(t, fs, progress, ct);
            var hash = ModPackage.ComputeSha256(part);
            File.Move(part, path, overwrite: true);
            File.WriteAllText(shaPath, hash);
            Diagnostics.FileLog.Write("cache", $"downloaded {path} (sha256 {hash})");
            return ModPackage.Open(path, hash);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }

    private static readonly string[] Sources = ["github", "smr"];

    /// <summary>Total size of cached builds in bytes.</summary>
    public long GetSizeBytes() => Sources.Select(s => Path.Combine(Root, s)).Where(Directory.Exists)
        .Sum(dir => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));

    /// <summary>Deletes every cached build. They are downloaded again when needed.</summary>
    public void Clear()
    {
        foreach (var dir in Sources.Select(s => Path.Combine(Root, s)).Where(Directory.Exists))
        {
            Directory.Delete(dir, recursive: true);
            Diagnostics.FileLog.Write("cache", $"deleted {dir}");
        }
    }

    /// <summary>
    /// Keeps only the newest <paramref name="keepPerAsset"/> cached builds of each asset (same owner, repo and file
    /// name, across tags) and deletes the rest, so the cache doesn't grow without limit on a small C: drive.
    /// Returns the number of bytes freed.
    /// </summary>
    public long Prune(int keepPerAsset = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepPerAsset, 1);
        // github/<owner>/<repo>/<tag>/<asset>.zip: one asset is (owner, repo, file name) across tags.
        // smr/<ModRef>/<version>/<file>.zip: one asset is (ModRef, file name) across versions.
        return Prune(Path.Combine(Root, "github"), 4, p => $"{p[0]}/{p[1]}/{p[3]}".ToLowerInvariant(), keepPerAsset)
             + Prune(Path.Combine(Root, "smr"), 3, p => $"{p[0]}/{p[2]}".ToLowerInvariant(), keepPerAsset);
    }

    private static long Prune(string dir, int depth, Func<string[], string> assetKey, int keepPerAsset)
    {
        if (!Directory.Exists(dir)) return 0;
        long freed = 0;
        var zips = new DirectoryInfo(dir).EnumerateFiles("*.zip", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(dir, f.FullName).Split(Path.DirectorySeparatorChar).Length == depth)
            .ToList();
        foreach (var group in zips.GroupBy(f => assetKey(Path.GetRelativePath(dir, f.FullName).Split(Path.DirectorySeparatorChar))))
        {
            foreach (var old in group.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keepPerAsset))
            {
                try
                {
                    long size = old.Length;
                    old.Delete();
                    Diagnostics.FileLog.Write("cache", $"pruned {old.FullName}");
                    freed += size;
                    var sha = new FileInfo(old.FullName + ".sha256");
                    if (sha.Exists) sha.Delete();
                    var tagDir = old.Directory!;
                    if (!tagDir.EnumerateFileSystemInfos().Any()) tagDir.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or already gone; try again next time.
                }
            }
        }
        return freed;
    }

    /// <summary>GitHub reports "sha256:&lt;hex&gt;" for assets; older assets may have no digest.</summary>
    private static bool DigestMatches(GitHubAsset asset, string sha256) =>
        asset.Digest is not { } d || !d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
        d[7..].Equals(sha256, StringComparison.OrdinalIgnoreCase);

    private static string Safe(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return s.Length == 0 ? "_" : s;
    }
}
