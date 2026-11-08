namespace Sdmm.Core.Installs;

/// <summary>
/// Finds the game folder (the one that contains FactoryGame) below a starting folder. Hosts' SFTP/FTP logins often
/// start one or more levels above it, and the right "Game folder" path isn't always obvious from the host's panel.
/// </summary>
public static class GameRootFinder
{
    public const int DefaultDepth = 3;
    private const int MaxFoldersPerLevel = 300; // a huge root (a whole Linux box): don't crawl it

    /// <summary>The nearest match (see <see cref="FindAll"/>), or null if none was found.</summary>
    public static string? Find(IInstallFileSystem fs, int maxDepth = DefaultDepth) => FindAll(fs, maxDepth).FirstOrDefault();

    /// <summary>
    /// Searches breadth-first, up to <paramref name="maxDepth"/> levels down, and returns every match at the
    /// shallowest level that has one, as paths relative to <paramref name="fs"/>'s root ("" is the root itself).
    /// Folders with FactoryGame/Mods (a modded server) come first. Empty if nothing was found.
    /// </summary>
    public static IReadOnlyList<string> FindAll(IInstallFileSystem fs, int maxDepth = DefaultDepth)
    {
        var level = new List<string> { "" };
        for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            var withGame = level.Where(p => fs.DirectoryExists(InstallPaths.Combine(p, "FactoryGame"))).ToList();
            if (withGame.Count > 0)
                return withGame.OrderByDescending(p => fs.DirectoryExists(InstallPaths.Combine(p, InstallPaths.ModsDir))).ToList();
            if (depth == maxDepth) break;

            var next = new List<string>();
            foreach (var p in level)
            {
                foreach (var child in fs.ListDirectories(p))
                {
                    // Skip hidden folders and system or game folders that never hold another install.
                    if (child.StartsWith('.') || child is "Engine" or "FactoryGame" or "proc" or "sys" or "dev" or "Saved" or "Logs") continue;
                    next.Add(InstallPaths.Combine(p, child));
                }
                if (next.Count > MaxFoldersPerLevel) break;
            }
            level = next;
        }
        return [];
    }
}
