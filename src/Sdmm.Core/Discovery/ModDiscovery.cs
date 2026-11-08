using Sdmm.Core.GitHub;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Discovery;

/// <summary>Where to look for mod releases. All three modes can be used together.</summary>
public sealed class SourceConfig
{
    /// <summary>GitHub users whose repos are scanned (public repos, plus private ones the token can see).</summary>
    public List<string> Users { get; set; } = [];
    /// <summary>GitHub organizations whose repos are scanned.</summary>
    public List<string> Orgs { get; set; } = [];
    /// <summary>Explicit "owner/repo" entries. Always scanned, regardless of the filters below.</summary>
    public List<string> Repos { get; set; } = [];
    /// <summary>If set, user and org scans only include repos with this topic (for example "satisfactory-mod").</summary>
    public string? Topic { get; set; }
    public bool IncludeForks { get; set; }
    public bool IncludeArchived { get; set; }

    /// <summary>
    /// Turns what people paste into a plain name: "https://github.com/user" → "user",
    /// "github.com/owner/repo.git" → "owner/repo". Plain names pass through unchanged.
    /// </summary>
    public static string NormalizeEntry(string s)
    {
        s = s.Trim();
        foreach (var prefix in new[] { "https://www.github.com/", "https://github.com/", "http://github.com/", "www.github.com/", "github.com/" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        s = s.Split('?', '#')[0].Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s;
    }
}

/// <summary>One GitHub release of one mod.</summary>
/// <param name="Version">Version parsed from the tag, or null when the tag is not a semantic version.</param>
public sealed record ModBuild(
    string ModReference,
    string Owner,
    string Repo,
    string Tag,
    SemVersion? Version,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    string? ReleaseName,
    string? HtmlUrl,
    IReadOnlyDictionary<ModTarget, GitHubAsset> Assets)
{
    public string RepoFullName => $"{Owner}/{Repo}";
    /// <summary>The release's notes (Markdown), if any.</summary>
    public string? ReleaseNotes { get; init; }
    /// <summary>The repo's one-line description, when it was listed through a user or org scan.</summary>
    public string? RepoDescription { get; init; }
    /// <summary>For a zip imported by hand (see <see cref="Cache.ImportedZips"/>): the local file per platform. Null for GitHub builds.</summary>
    public IReadOnlyDictionary<ModTarget, string>? LocalZips { get; init; }
    public bool IsImported => LocalZips is not null;
}

/// <param name="Builds">Newest first.</param>
public sealed record DiscoveredMod(string ModReference, IReadOnlyList<ModBuild> Builds)
{
    public ModBuild? Latest(bool includePrereleases) =>
        Builds.FirstOrDefault(b => b.Version is not null && (includePrereleases || !b.Prerelease));
}

public enum ProblemKind
{
    /// <summary>The source couldn't be read at all (not found, no access, GitHub error...).</summary>
    Failed,
    /// <summary>The source couldn't be read because the GitHub request limit ran out.</summary>
    RateLimited,
    /// <summary>The source was shown from the last successful check, because GitHub was down or the limit ran out.</summary>
    Stale,
}

/// <param name="Source">What couldn't be read, for example "user alice" or "bob/ModA".</param>
public sealed record DiscoveryProblem(ProblemKind Kind, string Source, string Message);

public sealed record DiscoveryResult(IReadOnlyList<DiscoveredMod> Mods, IReadOnlyList<string> ScannedRepos, IReadOnlyList<string> Errors)
{
    public IReadOnlyList<DiscoveryProblem> Problems { get; init; } = [];

    /// <summary>When the GitHub request limit ran out during this check; null if it didn't.</summary>
    public DateTimeOffset? RateLimitResetsAt { get; init; }

    /// <summary>True when some sources were missing or out of date, so the mod list may not be complete or current.</summary>
    public bool IsIncomplete => Problems.Count > 0;
}

public sealed class ModDiscovery
{
    private readonly GitHubClient _gh;

    public ModDiscovery(GitHubClient gh) => _gh = gh;

    public async Task<DiscoveryResult> DiscoverAsync(SourceConfig config, CancellationToken ct = default)
    {
        var problems = new List<DiscoveryProblem>();
        var repos = new Dictionary<string, (string Owner, string Name, string? Description)>(StringComparer.OrdinalIgnoreCase);

        void Fail(string source, string message, Exception? ex = null) => problems.Add(new DiscoveryProblem(
            ex is GitHubApiException { IsRateLimited: true } ? ProblemKind.RateLimited : ProblemKind.Failed, source, message));

        bool Keep(GitHubRepo r) =>
            (config.IncludeForks || !r.Fork) &&
            (config.IncludeArchived || !r.Archived) &&
            (string.IsNullOrWhiteSpace(config.Topic) || (r.Topics?.Contains(config.Topic.Trim(), StringComparer.OrdinalIgnoreCase) ?? false));

        void Add(GitHubRepo r) => repos.TryAdd(r.FullName, (r.Owner.Login, r.Name, r.Description));

        var users = config.Users.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (users.Count > 0 && _gh.HasToken)
        {
            // /users/{u}/repos only lists public repos, so private repos come from what the token can see.
            try
            {
                foreach (var r in await _gh.GetAccessibleReposAsync(ct))
                    if (users.Contains(r.Owner.Login, StringComparer.OrdinalIgnoreCase) && Keep(r)) Add(r);
            }
            catch (GitHubApiException ex)
            {
                Fail("private repos visible to the token", $"Listing repos visible to the token: {ex.Message}", ex);
            }
        }
        foreach (var user in users)
        {
            try
            {
                foreach (var r in await _gh.GetUserReposAsync(user, ct))
                    if (Keep(r)) Add(r);
            }
            catch (GitHubApiException ex)
            {
                Fail($"user {user}", $"User {user}: {ex.Message}", ex);
            }
        }

        foreach (var org in config.Orgs.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var r in await _gh.GetOrgReposAsync(org, ct))
                    if (Keep(r)) Add(r);
            }
            catch (GitHubApiException ex)
            {
                Fail($"org {org}", $"Org {org}: {ex.Message}", ex);
            }
        }

        foreach (var entry in config.Repos.Select(r => r.Trim()).Where(r => r.Length > 0))
        {
            var parts = entry.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                Fail(entry, $"'{entry}' is not in owner/repo form.");
                continue;
            }
            repos.TryAdd(entry, (parts[0], parts[1], null));
        }

        // Fetch releases a few repos at a time.
        var builds = new List<ModBuild>();
        var gate = new SemaphoreSlim(4);
        var tasks = repos.Values.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var releases = await _gh.GetReleasesAsync(r.Owner, r.Name, ct);
                return (r, releases, error: (GitHubApiException?)null);
            }
            catch (GitHubApiException ex)
            {
                return (r, releases: new List<GitHubRelease>(), error: ex);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var (r, releases, error) in await Task.WhenAll(tasks))
        {
            if (error is not null) { Fail($"{r.Owner}/{r.Name}", $"{r.Owner}/{r.Name}: {error.Message}", error); continue; }
            foreach (var rel in releases.Where(x => !x.Draft))
                builds.AddRange(BuildsFromRelease(r.Owner, r.Name, rel, r.Description));
        }

        foreach (var note in _gh.TakeStaleNotes())
            problems.Add(new DiscoveryProblem(ProblemKind.Stale, note.Path,
                (note.RateLimited ? "GitHub request limit reached" : "GitHub unavailable") + ", showing cached data for " + note.Message));

        var mods = builds
            .GroupBy(b => b.ModReference, StringComparer.Ordinal)
            .Select(g => new DiscoveredMod(g.Key, Order(g).ToList()))
            .OrderBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DiscoveryResult(mods, repos.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            problems.Select(p => p.Message).ToList())
        {
            Problems = problems,
            RateLimitResetsAt = _gh.RateLimitedUntil,
        };
    }

    /// <summary>One release can hold several mods (a multi-mod repo); assets are grouped by mod reference.</summary>
    internal static IEnumerable<ModBuild> BuildsFromRelease(string owner, string repo, GitHubRelease rel, string? repoDescription = null)
    {
        SemVersion.TryParse(rel.TagName, out var version);
        var byRef = new Dictionary<string, Dictionary<ModTarget, GitHubAsset>>(StringComparer.Ordinal);
        foreach (var asset in rel.Assets)
        {
            if (!ModPackage.TryParseAssetName(asset.Name, out var modRef, out var target)) continue;
            if (!byRef.TryGetValue(modRef, out var targets)) byRef[modRef] = targets = new();
            targets[target] = asset;
        }
        foreach (var (modRef, targets) in byRef)
            yield return new ModBuild(modRef, owner, repo, rel.TagName, version, rel.Prerelease, rel.PublishedAt, rel.Name, rel.HtmlUrl, targets)
            {
                ReleaseNotes = string.IsNullOrWhiteSpace(rel.Body) ? null : rel.Body.Trim(),
                RepoDescription = string.IsNullOrWhiteSpace(repoDescription) ? null : repoDescription.Trim(),
            };
    }

    private static IEnumerable<ModBuild> Order(IEnumerable<ModBuild> builds) =>
        builds.OrderByDescending(b => b.Version is not null)
              .ThenByDescending(b => b.Version)
              .ThenByDescending(b => b.PublishedAt ?? DateTimeOffset.MinValue);
}
