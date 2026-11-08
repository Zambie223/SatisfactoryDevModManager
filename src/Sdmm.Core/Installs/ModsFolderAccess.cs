namespace Sdmm.Core.Installs;

/// <summary>
/// Checks whether SDMM can change an install's Mods folder right now. Some hosts lock it while the server runs
/// (Indifferent Broccoli does, for example), which otherwise only shows up as a permission error halfway through an
/// install. The check does what an install does, on a throwaway folder: create it in the staging area with a small
/// file in it, move it into FactoryGame/Mods, and delete it. (Some hosts allow folders but refuse file writes while the
/// server runs, so the file matters.) A leftover from an interrupted check does no harm: it holds no .uplugin, so the game has nothing to load from it, and SDMM's scans skip dot folders.
/// </summary>
public static class ModsFolderAccess
{
    public const string ProbePrefix = ".sdmm-write-test-";

    /// <summary>Returns null if the Mods folder can be changed, otherwise the error the server gave.</summary>
    public static string? Check(IInstallFileSystem fs)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var staged = InstallPaths.Combine(Installing.Installer.StagingDir, ProbePrefix + id);
        var probe = InstallPaths.Combine(InstallPaths.ModsDir, ProbePrefix + id);
        try
        {
            fs.CreateDirectory(staged);
            fs.WriteAllText(InstallPaths.Combine(staged, "probe.txt"), "SDMM checks it can change mods here; safe to delete.");
            fs.MoveDirectory(staged, probe);
            fs.DeleteDirectory(probe);
            // Don't leave the staging folder behind if the check created it.
            if (fs.ListDirectories(Installing.Installer.StagingDir).Count == 0 && fs.ListFiles(Installing.Installer.StagingDir).Count == 0)
                fs.DeleteDirectory(Installing.Installer.StagingDir);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best-effort cleanup; on a locked folder this may fail too, which is fine (see the summary).
            foreach (var p in new[] { probe, staged })
            {
                try { fs.DeleteDirectory(p); }
                catch (Exception) { }
            }
            return ex.Message;
        }
    }
}
