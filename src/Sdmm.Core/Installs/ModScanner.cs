using Sdmm.Core.Packaging;

namespace Sdmm.Core.Installs;

public enum ModOwner
{
    /// <summary>Has our .sdmm.json marker.</summary>
    Sdmm,
    /// <summary>Has SMM's .smm marker: SMM installed it and may replace or delete it.</summary>
    Smm,
    /// <summary>No marker: installed by hand or by another tool. Never touched.</summary>
    Unmanaged,
}

/// <param name="Folder">Install-relative folder, for example "FactoryGame/Mods/GameFeatures/ExampleMod".</param>
/// <param name="Descriptor">Parsed .uplugin, or null if it is missing or unreadable.</param>
public sealed record InstalledMod(
    string ModReference,
    string Folder,
    bool InGameFeatures,
    ModOwner Owner,
    UPluginDescriptor? Descriptor,
    SdmmMarker? Marker,
    string? DescriptorError);

public static class ModScanner
{
    public const string SmmMarkerFileName = ".smm";

    /// <summary>
    /// Lists mod folders directly under Mods/ and Mods/GameFeatures/ (the two roots ficsit-cli manages).
    /// Hidden folders (starting with '.') such as our staging folder are skipped.
    /// </summary>
    public static IReadOnlyList<InstalledMod> Scan(IInstallFileSystem fs)
    {
        var result = new List<InstalledMod>();
        foreach (var (root, inGf) in new[] { (InstallPaths.ModsDir, false), (InstallPaths.GameFeaturesDir, true) })
        {
            foreach (var dir in fs.ListDirectories(root))
            {
                if (dir.StartsWith('.')) continue;
                if (!inGf && dir.Equals("GameFeatures", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(Inspect(fs, InstallPaths.Combine(root, dir), dir, inGf));
            }
        }
        return result;
    }

    /// <summary>
    /// The installed copies of one mod (Mods/&lt;Ref&gt; and Mods/GameFeatures/&lt;Ref&gt;), without listing every other mod.
    /// Use this when acting on a single mod: over SFTP/FTP a full <see cref="Scan"/> costs round trips for every mod.
    /// </summary>
    public static IReadOnlyList<InstalledMod> ScanMod(IInstallFileSystem fs, string modReference)
    {
        var found = new List<InstalledMod>();
        foreach (var (root, inGf) in new[] { (InstallPaths.ModsDir, false), (InstallPaths.GameFeaturesDir, true) })
        {
            var folder = InstallPaths.Combine(root, modReference);
            if (fs.DirectoryExists(folder)) found.Add(Inspect(fs, folder, modReference, inGf));
        }
        return found;
    }

    /// <summary>Inspects one mod folder. The mod reference is the folder name, which ficsit-cli and Alpakit keep equal to the .uplugin name.</summary>
    public static InstalledMod Inspect(IInstallFileSystem fs, string folder, string modReference, bool inGameFeatures)
    {
        // One listing instead of three existence checks: over SFTP/FTP each check is a round trip to the server.
        // Names are compared exactly, as before (Linux servers are case-sensitive).
        var files = new HashSet<string>(fs.ListFiles(folder), StringComparer.Ordinal);

        SdmmMarker? marker = null;
        var markerPath = InstallPaths.Combine(folder, SdmmMarker.FileName);
        if (files.Contains(SdmmMarker.FileName)) marker = SdmmMarker.TryParse(fs.ReadAllText(markerPath));

        // .smm wins over our marker: SMM deletes or replaces any .smm-marked folder, so such a folder is never ours to manage.
        var owner = files.Contains(SmmMarkerFileName) ? ModOwner.Smm
            : marker is not null ? ModOwner.Sdmm
            : ModOwner.Unmanaged;

        UPluginDescriptor? descriptor = null;
        string? error = null;
        var upluginPath = InstallPaths.Combine(folder, modReference + ".uplugin");
        if (files.Contains(modReference + ".uplugin"))
        {
            try
            {
                descriptor = UPluginDescriptor.Parse(modReference, fs.ReadAllText(upluginPath));
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                error = $"{modReference}.uplugin is unreadable: {ex.Message}";
            }
        }
        else
        {
            error = $"{modReference}.uplugin not found";
        }

        return new InstalledMod(modReference, folder, inGameFeatures, owner, descriptor, marker, error);
    }
}
