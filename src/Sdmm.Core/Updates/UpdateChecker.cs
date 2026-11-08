using Sdmm.Core.GitHub;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Updates;

/// <summary>Where SDMM's own releases are published.</summary>
/// <param name="AssetName">The release file holding the self-contained exe.</param>
public sealed record UpdateSource(string Owner, string Repo, string AssetName)
{
    public string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";
}

public sealed record AvailableUpdate(SemVersion Version, string Tag, bool Prerelease, string? Notes, string? HtmlUrl, GitHubAsset Asset);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    /// <summary>The release repo doesn't exist or isn't readable yet (for example, not published).</summary>
    SourceNotFound,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableUpdate? Update, string Message);

public static class UpdateChecker
{
    /// <summary>
    /// Finds the newest release whose tag is a semantic version above <paramref name="current"/> and that has the
    /// exe asset. Drafts are ignored; prereleases only when <paramref name="includePrereleases"/> is set.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(GitHubClient gh, UpdateSource source, SemVersion current,
        bool includePrereleases = false, CancellationToken ct = default)
    {
        List<GitHubRelease> releases;
        try
        {
            releases = await gh.GetReleasesAsync(source.Owner, source.Repo, ct);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == 404)
        {
            return new UpdateCheckResult(UpdateCheckStatus.SourceNotFound, null,
                $"No SDMM releases found at {source.Owner}/{source.Repo} yet.");
        }

        var best = releases
            .Where(r => !r.Draft && (includePrereleases || !r.Prerelease))
            .Select(r => (Release: r, Version: SemVersion.TryParse(r.TagName, out var v) ? v : null,
                Asset: r.Assets.FirstOrDefault(a => a.Name.Equals(source.AssetName, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Version is not null && x.Asset is not null && x.Version > current)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (best.Release is null)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, $"SDMM {current} is up to date.");

        var update = new AvailableUpdate(best.Version!, best.Release.TagName, best.Release.Prerelease,
            string.IsNullOrWhiteSpace(best.Release.Body) ? null : best.Release.Body.Trim(), best.Release.HtmlUrl, best.Asset!);
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update, $"SDMM {update.Version} is available (you have {current}).");
    }
}
