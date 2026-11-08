using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Cache;

/// <summary>
/// Mod zips added by hand ("Install from zip…"), for builds that aren't on GitHub: a zip passed around directly, or
/// your own Alpakit output. They are copied in, so the original can be moved or deleted, and listed next to the
/// GitHub builds. Layout: &lt;root&gt;/&lt;ModRef&gt;/&lt;version&gt;/&lt;ModRef&gt;-&lt;Target&gt;.zip with a ".sha256" sidecar.
/// The download cache's Clear and Prune never touch this folder.
/// </summary>
public sealed class ImportedZips
{
    /// <summary>Owner shown for imported builds (ModBuild.Owner); not a GitHub account.</summary>
    public const string Owner = "imported";

    public string Root { get; }

    public ImportedZips(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// Validates a mod zip and copies it in under its canonical name. The platform comes from the file name when it
    /// follows Alpakit's &lt;Mod&gt;-&lt;Target&gt;.zip, otherwise from the zip's contents. Throws
    /// <see cref="InvalidModPackageException"/> if the zip isn't a usable mod or its platform can't be told.
    /// </summary>
    public ModPackage Import(string zipPath)
    {
        var pkg = ModPackage.Open(zipPath);
        if (!pkg.Descriptor.HasValidSemVersion)
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: its .uplugin has no valid SemVersion, so its version can't be compared with other builds.");
        var target = pkg.Target ?? InferTarget(pkg.Files)
            ?? throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: can't tell whether this is a Windows, WindowsServer or LinuxServer build. Rename it to <Mod>-<Target>.zip, as Alpakit names it.");
        if (ModPackage.TryParseAssetName(Path.GetFileName(zipPath), out var namedRef, out _) && !pkg.ModReference.Equals(namedRef, StringComparison.OrdinalIgnoreCase))
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: the file name says one mod, but the .uplugin inside is {pkg.ModReference}.");

        var dir = Path.Combine(Root, pkg.ModReference, Safe(pkg.Descriptor.EffectiveVersion.ToString()));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{pkg.ModReference}-{target}.zip");
        if (!Path.GetFullPath(zipPath).Equals(dest, StringComparison.OrdinalIgnoreCase))
            File.Copy(zipPath, dest, overwrite: true);
        File.WriteAllText(dest + ".sha256", pkg.Sha256);
        return ModPackage.Open(dest, pkg.Sha256);
    }

    /// <summary>Every imported build, one per mod and version, with a zip per platform.</summary>
    public IReadOnlyList<ModBuild> List()
    {
        if (!Directory.Exists(Root)) return [];
        var builds = new List<ModBuild>();
        foreach (var versionDir in Directory.EnumerateDirectories(Root).SelectMany(Directory.EnumerateDirectories))
        {
            var zips = new Dictionary<ModTarget, string>();
            var assets = new Dictionary<ModTarget, GitHubAsset>();
            string? modRef = null;
            SemVersion? version = null;
            DateTimeOffset? added = null;
            foreach (var zip in Directory.EnumerateFiles(versionDir, "*.zip"))
            {
                if (!ModPackage.TryParseAssetName(Path.GetFileName(zip), out var r, out var t)) continue;
                var name = Path.GetFileName(Path.GetDirectoryName(versionDir)!);
                if (!r.Equals(name, StringComparison.OrdinalIgnoreCase) || !SemVersion.TryParse(Path.GetFileName(versionDir), out var v)) continue;
                modRef = name;
                version = v;
                var info = new FileInfo(zip);
                zips[t] = zip;
                assets[t] = new GitHubAsset { Name = info.Name, Size = info.Length };
                added = added is { } a && a > info.LastWriteTimeUtc ? a : info.LastWriteTimeUtc;
            }
            if (modRef is null || version is null) continue;
            builds.Add(new ModBuild(modRef, Owner, modRef, "v" + version, version, version.IsPreRelease, added, "Imported zip", null, assets)
            {
                LocalZips = zips,
            });
        }
        return builds;
    }

    /// <summary>Total size of imported zips in bytes.</summary>
    public long GetSizeBytes() =>
        Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>Deletes every imported zip. Installed copies are not affected.</summary>
    public void Clear()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    /// <summary>
    /// Adds imported builds to the GitHub mods: same mod reference, builds ordered newest version first (GitHub's
    /// build first when both have the same version). Mods only imported become entries of their own.
    /// </summary>
    public static IReadOnlyList<DiscoveredMod> Merge(IReadOnlyList<DiscoveredMod> github, IReadOnlyList<ModBuild> imported)
    {
        if (imported.Count == 0) return github;
        var byRef = github.ToDictionary(m => m.ModReference, m => m.Builds.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var b in imported)
        {
            if (!byRef.TryGetValue(b.ModReference, out var list)) byRef[b.ModReference] = list = [];
            list.Add(b);
        }
        return byRef.Select(kv => new DiscoveredMod(
                github.FirstOrDefault(m => m.ModReference.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))?.ModReference ?? kv.Key,
                kv.Value.Select((b, i) => (b, i))
                    .OrderBy(x => x.b.Version is null)
                    .ThenByDescending(x => x.b.Version)
                    .ThenBy(x => x.b.IsImported)
                    .ThenBy(x => x.i)
                    .Select(x => x.b).ToList()))
            .ToList();
    }

    /// <summary>
    /// Tells the platform from the zip's contents: Alpakit puts paks under Content/Paks/&lt;Target&gt;/, and Linux
    /// binaries under Binaries/Linux.
    /// </summary>
    internal static ModTarget? InferTarget(IReadOnlyList<string> files)
    {
        var found = new HashSet<ModTarget>();
        foreach (var f in files)
        {
            var parts = f.Split('/');
            for (int i = 0; i + 2 < parts.Length; i++)
                if (parts[i].Equals("Content", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
                    ModTargets.TryParse(parts[i + 2], out var t))
                    found.Add(t);
            if (f.StartsWith("Binaries/Linux/", StringComparison.OrdinalIgnoreCase)) found.Add(ModTarget.LinuxServer);
        }
        return found.Count == 1 ? found.Single() : null;
    }

    private static string Safe(string segment) =>
        new(segment.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
}
