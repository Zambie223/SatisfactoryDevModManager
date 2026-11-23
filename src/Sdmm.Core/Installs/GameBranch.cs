using System.Text.RegularExpressions;

namespace Sdmm.Core.Installs;

/// <summary>Which Satisfactory branch an install runs. SMR reports mod compatibility separately for each.</summary>
public enum GameBranch
{
    /// <summary>Not known (a hosted server, or no store manifest found). Treated as Early Access.</summary>
    Unknown,
    /// <summary>The normal release branch (SMR calls it Early Access).</summary>
    EarlyAccess,
    /// <summary>The experimental branch.</summary>
    Experimental,
}

/// <summary>Works out the branch of a local install from its store's files. Best effort: unknown when it can't tell.</summary>
public static partial class GameBranchDetector
{
    // Satisfactory and its dedicated server on Steam.
    private static readonly string[] SteamAppIds = [GameLocator.SteamAppId, GameLocator.SteamServerAppId];

    // The installed branch is "BetaKey" under "MountedConfig" (UserConfig holds the one picked, which may not be
    // downloaded yet). "public" is the normal branch.
    [GeneratedRegex("\"MountedConfig\"\\s*\\{[^}]*?\"BetaKey\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MountedBetaKey();

    [GeneratedRegex("\"UserConfig\"\\s*\\{[^}]*?\"BetaKey\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex UserBetaKey();

    /// <param name="localRoot">The game or server folder, for example ...\steamapps\common\Satisfactory.</param>
    /// <param name="epicGames">Epic installs found on this PC (Label "Experimental" for the experimental app).</param>
    public static GameBranch Detect(string localRoot, IEnumerable<FoundGame>? epicGames = null)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
            foreach (var g in epicGames ?? [])
                if (g.Store == GameStore.Epic && Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.Path)).Equals(root, StringComparison.OrdinalIgnoreCase))
                    return g.Label == "Experimental" ? GameBranch.Experimental : GameBranch.EarlyAccess;

            // Steam: <library>\steamapps\common\<folder>, with appmanifest_<id>.acf in <library>\steamapps.
            var common = Path.GetDirectoryName(root);
            var steamapps = common is null ? null : Path.GetDirectoryName(common);
            if (steamapps is null || !Path.GetFileName(common)!.Equals("common", StringComparison.OrdinalIgnoreCase)) return GameBranch.Unknown;
            var folder = Path.GetFileName(root);
            foreach (var id in SteamAppIds)
            {
                var manifest = Path.Combine(steamapps, $"appmanifest_{id}.acf");
                if (!File.Exists(manifest)) continue;
                var text = File.ReadAllText(manifest);
                if (!Regex.IsMatch(text, "\"installdir\"\\s*\"" + Regex.Escape(folder) + "\"", RegexOptions.IgnoreCase)) continue;
                return FromSteamManifest(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return GameBranch.Unknown;
    }

    /// <summary>The branch recorded in a Steam appmanifest's text.</summary>
    public static GameBranch FromSteamManifest(string acf)
    {
        var m = MountedBetaKey().Match(acf);
        if (!m.Success) m = UserBetaKey().Match(acf);
        if (!m.Success) return GameBranch.EarlyAccess;
        return m.Groups[1].Value.Equals("experimental", StringComparison.OrdinalIgnoreCase) ? GameBranch.Experimental : GameBranch.EarlyAccess;
    }
}
