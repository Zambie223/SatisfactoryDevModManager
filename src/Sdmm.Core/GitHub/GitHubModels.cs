namespace Sdmm.Core.GitHub;

public sealed class GitHubOwner
{
    public string Login { get; set; } = "";
}

public sealed class GitHubRepo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public GitHubOwner Owner { get; set; } = new();
    public bool Private { get; set; }
    public bool Fork { get; set; }
    public bool Archived { get; set; }
    public List<string>? Topics { get; set; }
    public string? HtmlUrl { get; set; }
    /// <summary>The repo's one-line description, shown under the mod name.</summary>
    public string? Description { get; set; }
}

public sealed class GitHubAsset
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    /// <summary>"sha256:&lt;hex&gt;" when GitHub provides it.</summary>
    public string? Digest { get; set; }
    public string? ContentType { get; set; }
    public string? BrowserDownloadUrl { get; set; }
}

public sealed class GitHubRelease
{
    public long Id { get; set; }
    public string TagName { get; set; } = "";
    public string? Name { get; set; }
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? HtmlUrl { get; set; }
    /// <summary>Release notes (Markdown), shown as the changelog.</summary>
    public string? Body { get; set; }
    public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed record RateLimitInfo(int Limit, int Remaining, DateTimeOffset Reset);

public sealed class GitHubApiException : Exception
{
    public int StatusCode { get; }
    public RateLimitInfo? RateLimit { get; }

    /// <summary>True when GitHub refused because the request limit is used up (primary or secondary limit).</summary>
    public bool IsRateLimited { get; }

    public GitHubApiException(int statusCode, string message, RateLimitInfo? rateLimit = null, bool? rateLimited = null) : base(message)
    {
        StatusCode = statusCode;
        RateLimit = rateLimit;
        IsRateLimited = rateLimited ?? (statusCode is 403 or 429 &&
            (rateLimit is { Remaining: 0 } || statusCode == 429 || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>A response served from the cache instead of from GitHub.</summary>
/// <param name="RateLimited">True when the reason was the request limit (rather than GitHub being down).</param>
public sealed record StaleNote(string Path, string Reason, bool RateLimited)
{
    public string Message => $"{Path}: {Reason}; using the copy from the last successful check.";
    public override string ToString() => Message;
}
