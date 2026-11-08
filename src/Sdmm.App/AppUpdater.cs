using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Sdmm.Core.GitHub;
using Sdmm.Core.Updates;
using Sdmm.Core.Versioning;

namespace Sdmm.App;

/// <summary>SDMM's own version and where its releases come from.</summary>
public static class AppInfo
{
    // PLACEHOLDER until SDMM's GitHub repo exists: change Owner/Repo here. Each release needs a tag v<version>
    // matching <Version> in Directory.Build.props, and the single-file exe from `build.ps1 -Publish` attached
    // with exactly this asset name.
    public static readonly UpdateSource UpdateSource = new("Zambie223", "SatisfactoryDevModManager", "SatisfactoryDevModManager.exe");

    public static SemVersion Version { get; } = SemVersion.Parse(Sdmm.Core.Installing.Installer.ToolVersion);
}

/// <summary>Replaces the running exe with a newer release and restarts.</summary>
public static class AppUpdater
{
    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path.");
    private static string OldPath => ExePath + ".old";
    private static string NewPath => ExePath + ".new";

    /// <summary>
    /// Self-update only works for the single-file published exe in a writable folder. A development build has
    /// its DLLs next to the exe, and swapping the exe alone would break it.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var dir = Path.GetDirectoryName(ExePath)!;
        if (File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(ExePath) + ".dll")))
        {
            reason = "This is a development build (its DLLs sit next to the exe), so it can't replace itself.";
            return false;
        }
        try
        {
            var probe = Path.Combine(dir, $".sdmm-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = $"SDMM can't write to {dir}, so it can't replace itself.";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Downloads the new exe next to the current one, checks it against GitHub's SHA-256 digest, swaps the files
    /// (Windows allows renaming a running exe) and starts the new version. The caller should then shut down.
    /// Refuses when GitHub provides no digest, because the download then can't be verified.
    /// </summary>
    public static async Task ApplyAsync(GitHubClient gh, UpdateSource source, AvailableUpdate update, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (update.Asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub provided no SHA-256 digest for this release, so the download can't be verified. Download it from the release page instead.");

        try
        {
            await using (var fs = new FileStream(NewPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await gh.DownloadAssetAsync(source.Owner, source.Repo, update.Asset.Id, fs, progress, ct);

            string hash;
            await using (var fs = File.OpenRead(NewPath))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded update's SHA-256 ({hash}) doesn't match GitHub's ({digest[7..]}). Nothing was changed.");
            if (new FileInfo(NewPath).Length != update.Asset.Size)
                throw new InvalidDataException("The downloaded update has the wrong size. Nothing was changed.");

            if (File.Exists(OldPath)) File.Delete(OldPath);
            File.Move(ExePath, OldPath);
            try
            {
                File.Move(NewPath, ExePath);
            }
            catch
            {
                File.Move(OldPath, ExePath); // put the running version back
                throw;
            }
        }
        finally
        {
            if (File.Exists(NewPath)) File.Delete(NewPath);
        }

        // --after-update makes the new copy wait for this one to exit (one instance per settings file), and the
        // original arguments (such as --config) carry over.
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        foreach (var a in Environment.GetCommandLineArgs().Skip(1).Where(a => a != App.AfterUpdateArg)) start.ArgumentList.Add(a);
        start.ArgumentList.Add(App.AfterUpdateArg);
        Process.Start(start);
    }

    /// <summary>Deletes the previous exe left behind by an update (it was still running during the swap).</summary>
    public static void CleanupPreviousVersion()
    {
        try
        {
            if (File.Exists(OldPath)) File.Delete(OldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use by an exiting process; next start will try again.
        }
    }
}
