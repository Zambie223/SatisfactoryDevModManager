using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Sdmm.Core.Packaging;

/// <summary>
/// An Alpakit per-target mod zip, for example "ExampleMod-Windows.zip". The .uplugin must sit at the zip root,
/// because the contents are extracted directly into Mods/&lt;Ref&gt; (the same layout ficsit-cli expects).
/// </summary>
public sealed partial class ModPackage
{
    /// <summary>SMR mod references are a letter followed by letters, digits or underscores.</summary>
    [GeneratedRegex(@"^(?<ref>[A-Za-z][A-Za-z0-9_]*)-(?<target>Windows|WindowsServer|LinuxServer)\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetNamePattern();

    public string ZipPath { get; }
    public string Sha256 { get; }
    public UPluginDescriptor Descriptor { get; }
    public string ModReference => Descriptor.ModReference;
    /// <summary>Target from the file name, when the name follows the Alpakit pattern.</summary>
    public ModTarget? Target { get; }
    /// <summary>Relative file paths inside the zip (forward slashes), directories excluded.</summary>
    public IReadOnlyList<string> Files { get; }

    private ModPackage(string zipPath, string sha256, UPluginDescriptor descriptor, ModTarget? target, IReadOnlyList<string> files)
    {
        ZipPath = zipPath;
        Sha256 = sha256;
        Descriptor = descriptor;
        Target = target;
        Files = files;
    }

    /// <summary>Parses "&lt;Ref&gt;-&lt;Target&gt;.zip".</summary>
    public static bool TryParseAssetName(string fileName, out string modReference, out ModTarget target)
    {
        var m = AssetNamePattern().Match(fileName);
        if (m.Success && ModTargets.TryParse(m.Groups["target"].Value, out target))
        {
            modReference = m.Groups["ref"].Value;
            return true;
        }
        modReference = "";
        target = default;
        return false;
    }

    /// <summary>Opens and validates a mod zip. Throws <see cref="InvalidModPackageException"/> when it is unusable.</summary>
    /// <param name="sha256">Pass the hash if it is already known (for example from the cache) to skip rehashing.</param>
    public static ModPackage Open(string zipPath, string? sha256 = null)
    {
        sha256 ??= ComputeSha256(zipPath);

        using var zip = OpenZip(zipPath);
        var files = new List<string>();
        ZipArchiveEntry? uplugin = null;
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeEntryPath(entry.FullName, zipPath);
            if (name.EndsWith('/')) continue; // directory entry
            files.Add(name);
            if (name.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Contains('/'))
                    throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: '{name}' is not at the zip root; Alpakit zips have the .uplugin at the root.");
                if (uplugin is not null)
                    throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: more than one .uplugin at the zip root.");
                uplugin = entry;
            }
        }
        if (uplugin is null)
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: no .uplugin file at the zip root.");

        var modRef = Path.GetFileNameWithoutExtension(uplugin.FullName);
        string json;
        using (var reader = new StreamReader(uplugin.Open()))
            json = reader.ReadToEnd();

        UPluginDescriptor descriptor;
        try
        {
            descriptor = UPluginDescriptor.Parse(modRef, json);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: {modRef}.uplugin is not valid JSON: {ex.Message}", ex);
        }

        ModTarget? target = null;
        if (TryParseAssetName(Path.GetFileName(zipPath), out var nameRef, out var t))
        {
            if (!string.Equals(nameRef, modRef, StringComparison.Ordinal))
                throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: file name says mod '{nameRef}' but the zip contains {modRef}.uplugin.");
            target = t;
        }

        return new ModPackage(zipPath, sha256, descriptor, target, files);
    }

    /// <summary>
    /// Enumerates file entries with validated relative paths. Each Open delegate is only valid while the
    /// enumeration is in progress (the zip is closed when enumeration ends).
    /// </summary>
    public IEnumerable<(string RelativePath, Func<Stream> Open)> ExtractEntries()
    {
        using var zip = OpenZip(ZipPath);
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeEntryPath(entry.FullName, ZipPath);
            if (name.EndsWith('/')) continue;
            var e = entry;
            yield return (name, () => e.Open());
        }
    }

    public static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static ZipArchive OpenZip(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)} is not a valid zip file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts an entry name to a safe relative path with forward slashes. Rejects absolute paths, drive letters
    /// and ".." segments, so a malicious zip cannot write outside the mod folder ("zip slip").
    /// </summary>
    internal static string NormalizeEntryPath(string entryName, string zipPath)
    {
        var name = entryName.Replace('\\', '/');
        bool isDir = name.EndsWith('/');
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (name.StartsWith('/') || parts.Length == 0 && !isDir ||
            parts.Any(p => p is "." or ".." || p.Contains(':')) ||
            parts.Any(p => p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidModPackageException($"{Path.GetFileName(zipPath)}: unsafe entry path '{entryName}'.");
        }
        return string.Join('/', parts) + (isDir ? "/" : "");
    }
}

public sealed class InvalidModPackageException : Exception
{
    public InvalidModPackageException(string message) : base(message) { }
    public InvalidModPackageException(string message, Exception inner) : base(message, inner) { }
}
