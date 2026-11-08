using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sdmm.Core.Installs;

public enum GameStore { Steam, Epic }

/// <param name="Path">The game folder (the one containing FactoryGame and Engine).</param>
/// <param name="Label">"Experimental" for Epic's experimental branch, otherwise null.</param>
public sealed record FoundGame(string Path, GameStore Store, string? Label = null);

/// <summary>
/// Finds Satisfactory installs from the stores' own records, so they don't have to be picked by hand:
/// every Steam library (Steam can install games on any drive) and Epic's install manifests.
/// Callers still validate each folder with <see cref="GameInstall.Detect"/>.
/// </summary>
public static partial class GameLocator
{
    /// <summary>Satisfactory's Steam app id. The experimental branch is a beta of the same app, in the same folder.</summary>
    public const string SteamAppId = "526870";
    /// <summary>The Satisfactory Dedicated Server's Steam app id (a separate, free app).</summary>
    public const string SteamServerAppId = "1690800";

    /// <param name="steamRoot">Steam's own folder (from the registry), or null if Steam isn't installed.</param>
    public static IReadOnlyList<FoundGame> FindSteam(string? steamRoot) => FindSteam(steamRoot, SteamAppId, "Satisfactory");

    /// <summary>Dedicated servers installed through Steam on this PC.</summary>
    public static IReadOnlyList<FoundGame> FindSteamServers(string? steamRoot) =>
        FindSteam(steamRoot, SteamServerAppId, "SatisfactoryDedicatedServer", "Dedicated server");

    private static IReadOnlyList<FoundGame> FindSteam(string? steamRoot, string appId, string defaultDir, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot)) return [];
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));

        var found = new List<FoundGame>();
        foreach (var lib in libraries.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            var manifest = Path.Combine(apps, $"appmanifest_{appId}.acf");
            // Without a manifest (a copied-in folder, say), still try the default folder name.
            var dir = (File.Exists(manifest) ? ParseInstallDir(File.ReadAllText(manifest)) : null) ?? defaultDir;
            var game = Path.Combine(apps, "common", dir);
            if (Directory.Exists(game)) found.Add(new FoundGame(game, GameStore.Steam, label));
        }
        return found;
    }

    /// <param name="manifestsDir">Epic's manifest folder: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests.</param>
    public static IReadOnlyList<FoundGame> FindEpic(string manifestsDir)
    {
        if (!Directory.Exists(manifestsDir)) return [];
        var found = new List<FoundGame>();
        foreach (var file in Directory.EnumerateFiles(manifestsDir, "*.item"))
        {
            try
            {
                if (ParseEpicManifest(File.ReadAllText(file)) is { } g && Directory.Exists(g.Path)) found.Add(g);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable manifest belongs to some other game as often as not; skip it.
            }
        }
        return found;
    }

    /// <summary>Library paths from Steam's libraryfolders.vdf ("path" entries, with VDF's \\ escapes undone).</summary>
    internal static IEnumerable<string> ParseLibraryFolders(string vdf) =>
        VdfPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    /// <summary>The "installdir" from an appmanifest_*.acf file.</summary>
    internal static string? ParseInstallDir(string acf)
    {
        var m = VdfInstallDir().Match(acf);
        return m.Success && m.Groups[1].Value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Reads one Epic .item manifest. Satisfactory's Epic app names are CrabEA (release) and CrabTest (experimental);
    /// the display name is checked too in case Epic renames them.
    /// </summary>
    internal static FoundGame? ParseEpicManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var app = Str("AppName") ?? "";
            var display = Str("DisplayName") ?? "";
            var location = Str("InstallLocation");
            bool isSatisfactory = app is "CrabEA" or "CrabTest" || display.StartsWith("Satisfactory", StringComparison.OrdinalIgnoreCase);
            if (!isSatisfactory || string.IsNullOrWhiteSpace(location)) return null;
            bool experimental = app == "CrabTest" || display.Contains("Experimental", StringComparison.OrdinalIgnoreCase);
            return new FoundGame(location, GameStore.Epic, experimental ? "Experimental" : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Normalize(string p) => Path.GetFullPath(p.Replace('/', '\\')).TrimEnd('\\');

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfInstallDir();
}
