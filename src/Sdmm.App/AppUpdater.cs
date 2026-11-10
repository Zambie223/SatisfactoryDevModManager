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
    // SDMM's own GitHub repo. Each release needs a tag v<version> matching <Version> in Directory.Build.props, and the
    // single-file exe from `build.ps1 -Publish` attached with exactly this asset name.
    // The command line tool is updated with the app only when it's already next to it; it's never downloaded otherwise.
    public static readonly UpdateSource UpdateSource = new("Zambie223", "SatisfactoryDevModManager", "SatisfactoryDevModManager.exe",
        "SatisfactoryDevModManager-cli.exe");

    public static SemVersion Version { get; } = SemVersion.Parse(Sdmm.Core.Installing.Installer.ToolVersion);
}

/// <summary>Replaces the running exe with a newer release and restarts.</summary>
public static class AppUpdater
{
    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path.");
    private static string OldPath => ExePath + ".old";

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

    /// <summary>The command line tool's path next to the app, when it's there.</summary>
    private static string? CompanionPath(UpdateSource source) =>
        source.CompanionAssetName is { } name && Path.Combine(Path.GetDirectoryName(ExePath)!, name) is var p && File.Exists(p) ? p : null;

    /// <summary>
    /// Downloads the new exe next to the current one, checks it against GitHub's SHA-256 digest, swaps the files
    /// (Windows allows renaming a running exe) and starts the new version. The caller should then shut down.
    /// Refuses when GitHub provides no digest, because the download then can't be verified.
    /// The command line tool is updated too when it's next to the app (a running copy is fine: it's renamed, not
    /// deleted); a problem with it is returned as a note and doesn't stop the app's update.
    /// </summary>
    public static async Task<string?> ApplyAsync(GitHubClient gh, UpdateSource source, AvailableUpdate update, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await ReplaceAsync(gh, source, update.Tag, update.Asset, ExePath, progress, ct);

        string? note = null;
        if (CompanionPath(source) is { } cli)
        {
            try
            {
                if (update.CompanionAsset is not { } cliAsset)
                    note = $"{update.Tag} has no {source.CompanionAssetName}, so the command line tool next to SDMM wasn't updated.";
                else
                {
                    await ReplaceAsync(gh, source, update.Tag, cliAsset, cli, null, ct);
                    note = $"The command line tool next to SDMM was updated to {update.Tag} too.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or GitHubApiException or System.Net.Http.HttpRequestException)
            {
                note = $"The command line tool next to SDMM couldn't be updated ({ex.Message}). Download it from the release page.";
            }
            Sdmm.Core.Diagnostics.FileLog.Write("update", note);
        }

        // --after-update makes the new copy wait for this one to exit (one instance per settings file), and the
        // original arguments (such as --config) carry over.
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        foreach (var a in Environment.GetCommandLineArgs().Skip(1).Where(a => a != App.AfterUpdateArg)) start.ArgumentList.Add(a);
        start.ArgumentList.Add(App.AfterUpdateArg);
        Sdmm.Core.Diagnostics.FileLog.Write("update", $"restarting {ExePath}");
        Process.Start(start);
        return note;
    }

    /// <summary>Downloads one release file next to <paramref name="target"/>, verifies it, and swaps it in (keeping the old one as .old).</summary>
    private static async Task ReplaceAsync(GitHubClient gh, UpdateSource source, string tag, GitHubAsset asset, string target, IProgress<long>? progress, CancellationToken ct)
    {
        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub provided no SHA-256 digest for this release, so the download can't be verified. Download it from the release page instead.");
        string newPath = target + ".new", oldPath = target + ".old";
        try
        {
            await using (var fs = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await gh.DownloadAssetAsync(source.Owner, source.Repo, asset.Id, fs, progress, ct);

            string hash;
            await using (var fs = File.OpenRead(newPath))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded update's SHA-256 ({hash}) doesn't match GitHub's ({digest[7..]}). Nothing was changed.");
            if (new FileInfo(newPath).Length != asset.Size)
                throw new InvalidDataException("The downloaded update has the wrong size. Nothing was changed.");

            Sdmm.Core.Diagnostics.FileLog.Write("update", $"downloaded {tag} to {newPath} (sha256 {hash}); swapping it in for {target}");
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(target, oldPath);
            try
            {
                File.Move(newPath, target);
            }
            catch
            {
                File.Move(oldPath, target); // put the current version back
                throw;
            }
        }
        finally
        {
            if (File.Exists(newPath)) File.Delete(newPath);
        }
    }

    /// <summary>Deletes the previous exes left behind by an update (they were still running during the swap).</summary>
    public static void CleanupPreviousVersion()
    {
        foreach (var old in new[] { OldPath, CompanionPath(AppInfo.UpdateSource) is { } cli ? cli + ".old" : null })
        {
            try
            {
                if (old is not null && File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use (an exiting app, or a command line run); the next start tries again.
            }
        }
    }
}
