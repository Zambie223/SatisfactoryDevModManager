using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Sdmm.Core.Json;

namespace Sdmm.Core.GitHub;

/// <summary>
/// Minimal GitHub REST client for listing repos and releases and downloading release assets (including from
/// private repos). Redirects are followed by hand so the token is only ever sent to api.github.com: asset
/// downloads redirect to a pre-signed storage URL that must not receive the token.
/// </summary>
public sealed partial class GitHubClient : IDisposable
{
    public static readonly Uri ApiBase = new("https://api.github.com/");
    private const int MaxRedirects = 5;
    private const int MaxPages = 10;

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly IEtagCache _cache;
    private readonly string _cacheScope;

    public RateLimitInfo? LastRateLimit { get; private set; }
    public bool HasToken => _token is not null;

    /// <param name="handler">For tests. The default handler has auto-redirect disabled; a custom one should too.</param>
    public GitHubClient(string? token, IEtagCache? cache = null, HttpMessageHandler? handler = null)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _cache = cache ?? new MemoryEtagCache();
        // Responses differ by token (private repos), so cached entries are scoped to a hash of it.
        _cacheScope = _token is null ? "anon" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_token)))[..16];
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(new Diagnostics.LoggingHandler(handler)) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SatisfactoryDevModManager", Installing.Installer.ToolVersion));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Public repos owned by a user.</summary>
    public Task<List<GitHubRepo>> GetUserReposAsync(string user, CancellationToken ct = default) =>
        GetPagedAsync<GitHubRepo>($"users/{Uri.EscapeDataString(user)}/repos?type=owner&per_page=100", ct);

    /// <summary>
    /// Repos the token can see (including private ones the user owns or collaborates on). With a fine-grained
    /// token, only the repos it was granted. Requires a token.
    /// </summary>
    public Task<List<GitHubRepo>> GetAccessibleReposAsync(CancellationToken ct = default) =>
        GetPagedAsync<GitHubRepo>("user/repos?affiliation=owner,collaborator,organization_member&per_page=100", ct);

    public Task<List<GitHubRepo>> GetOrgReposAsync(string org, CancellationToken ct = default) =>
        GetPagedAsync<GitHubRepo>($"orgs/{Uri.EscapeDataString(org)}/repos?type=all&per_page=100", ct);

    public Task<List<GitHubRelease>> GetReleasesAsync(string owner, string repo, CancellationToken ct = default) =>
        GetPagedAsync<GitHubRelease>($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=100", ct);

    /// <summary>Downloads a release asset's bytes into <paramref name="destination"/>.</summary>
    public async Task DownloadAssetAsync(string owner, string repo, long assetId, Stream destination,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var url = new Uri(ApiBase, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases/assets/{assetId}");
        using var response = await SendAsync(url, "application/octet-stream", etag: null, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, url);
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
            progress?.Report(total);
        }
    }

    private async Task<List<T>> GetPagedAsync<T>(string relative, CancellationToken ct)
    {
        var typeInfo = (JsonTypeInfo<List<T>>)(GitHubJsonContext.Default.GetTypeInfo(typeof(List<T>))
            ?? throw new NotSupportedException($"List<{typeof(T).Name}> is not registered in GitHubJsonContext."));
        var result = new List<T>();
        Uri? url = new(ApiBase, relative);
        for (int page = 0; url is not null && page < MaxPages; page++)
        {
            var (body, next) = await GetJsonAsync(url, ct);
            result.AddRange(JsonSerializer.Deserialize(body, typeInfo) ?? []);
            url = next;
        }
        return result;
    }

    /// <summary>Per-request limit for API (JSON) calls. Downloads use the HttpClient's longer timeout.</summary>
    internal TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Waits between retries of transient failures. Tests replace this to avoid real delays.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    private readonly System.Collections.Concurrent.ConcurrentQueue<StaleNote> _staleNotes = new();

    /// <summary>
    /// Returns and clears notes about responses served from the cache because GitHub was unavailable or the
    /// request limit was reached. Callers should show these so people know the data may be out of date.
    /// </summary>
    public IReadOnlyList<StaleNote> TakeStaleNotes()
    {
        var list = new List<StaleNote>();
        while (_staleNotes.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    // UTC ticks until which GitHub said the request limit is used up. While set, no further requests are sent:
    // they would only fail too. Interlocked because discovery reads repos in parallel.
    private long _rateLimitedUntilTicks;

    /// <summary>When the exhausted request limit resets, if it has been hit; otherwise null.</summary>
    public DateTimeOffset? RateLimitedUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _rateLimitedUntilTicks);
            return ticks > DateTimeOffset.UtcNow.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    private void MarkRateLimited(GitHubApiException ex)
    {
        // Primary limit: reset time from X-RateLimit-Reset. Secondary limit (429) may give none; wait a minute.
        var until = ex.RateLimit is { Remaining: 0 } rl && rl.Reset > DateTimeOffset.UtcNow ? rl.Reset : DateTimeOffset.UtcNow.AddMinutes(1);
        Interlocked.Exchange(ref _rateLimitedUntilTicks, until.UtcTicks);
    }

    private static bool IsTransient(HttpStatusCode s) =>
        s is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// GET with ETag caching. Returns the body and the next-page URL from the Link header.
    /// Transient failures (502/503/504, network errors, timeouts) are retried twice. If GitHub is still failing
    /// and a cached copy exists, the cached copy is returned and a note is recorded (see <see cref="TakeStaleNotes"/>).
    /// </summary>
    private async Task<(string Body, Uri? Next)> GetJsonAsync(Uri url, CancellationToken ct)
    {
        var key = _cacheScope + " " + url.AbsoluteUri;
        _cache.TryGet(key, out var cachedEtag, out var cachedBody);

        if (RateLimitedUntil is { } until)
        {
            // The limit is already used up: answer from the cache, or fail without sending anything.
            if (cachedBody.Length > 0)
            {
                _staleNotes.Enqueue(new StaleNote(url.PathAndQuery, "GitHub request limit reached", RateLimited: true));
                return Unwrap(cachedBody);
            }
            throw new GitHubApiException(403,
                $"GitHub request limit reached, so {url.PathAndQuery} was not requested. It resets at {until.ToLocalTime():t}.",
                LastRateLimit, rateLimited: true);
        }

        for (int attempt = 0; ; attempt++)
        {
            bool canRetry = attempt < RetryDelays.Length;
            string failure;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);
            try
            {
                using var response = await SendAsync(url, "application/vnd.github+json", cachedEtag.Length > 0 ? cachedEtag : null, timeout.Token);

                if (response.StatusCode == HttpStatusCode.NotModified && cachedBody.Length > 0)
                    return Unwrap(cachedBody);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(timeout.Token);
                    var next = NextLink(response);
                    if (response.Headers.ETag is { } etag)
                        _cache.Set(key, etag.ToString(), Wrap(body, next));
                    return (body, next);
                }

                if (!IsTransient(response.StatusCode))
                {
                    var error = await ErrorAsync(response, url);
                    if (error.IsRateLimited)
                    {
                        MarkRateLimited(error);
                        if (cachedBody.Length > 0)
                        {
                            _staleNotes.Enqueue(new StaleNote(url.PathAndQuery, "GitHub request limit reached", RateLimited: true));
                            return Unwrap(cachedBody);
                        }
                    }
                    throw error;
                }
                failure = $"GitHub returned {(int)response.StatusCode}";
                if (!canRetry && cachedBody.Length == 0) throw await ErrorAsync(response, url);
            }
            catch (HttpRequestException ex)
            {
                failure = "network error: " + ex.Message;
                if (!canRetry && cachedBody.Length == 0) throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = $"no response within {ApiTimeout.TotalSeconds:0} seconds";
                if (!canRetry && cachedBody.Length == 0)
                    throw new GitHubApiException(0, $"GitHub did not respond to {url.PathAndQuery} within {ApiTimeout.TotalSeconds:0} seconds.");
            }

            if (canRetry)
            {
                await Delay(RetryDelays[attempt], ct);
                continue;
            }
            _staleNotes.Enqueue(new StaleNote(url.PathAndQuery, failure, RateLimited: false));
            return Unwrap(cachedBody);
        }
    }

    // The cached body also remembers the next-page link, since a 304 response may not repeat it.
    private static string Wrap(string body, Uri? next) => (next?.AbsoluteUri ?? "") + "\n" + body;

    private static (string Body, Uri? Next) Unwrap(string cached)
    {
        int nl = cached.IndexOf('\n');
        if (nl < 0) return (cached, null);
        var link = cached[..nl];
        return (cached[(nl + 1)..], link.Length > 0 ? new Uri(link) : null);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string accept, string? etag, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(accept);
            bool toApi = url.Host.Equals(ApiBase.Host, StringComparison.OrdinalIgnoreCase) && url.Scheme == Uri.UriSchemeHttps;
            if (toApi && _token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (toApi && etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (toApi) LastRateLimit = ReadRateLimit(response) ?? LastRateLimit;

            int code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects) throw new GitHubApiException(code, $"Too many redirects fetching {url}");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                etag = null;
                continue;
            }
            return response;
        }
    }

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLinkPattern();

    private static Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var v in values)
        {
            var m = NextLinkPattern().Match(v);
            if (m.Success) return new Uri(m.Groups[1].Value);
        }
        return null;
    }

    private static RateLimitInfo? ReadRateLimit(HttpResponseMessage r)
    {
        static long? H(HttpResponseMessage r, string n) =>
            r.Headers.TryGetValues(n, out var v) && long.TryParse(v.FirstOrDefault(), out var x) ? x : null;
        var limit = H(r, "X-RateLimit-Limit");
        var remaining = H(r, "X-RateLimit-Remaining");
        var reset = H(r, "X-RateLimit-Reset");
        return limit is null || remaining is null || reset is null ? null
            : new RateLimitInfo((int)limit, (int)remaining, DateTimeOffset.FromUnixTimeSeconds(reset.Value));
    }

    private async Task<GitHubApiException> ErrorAsync(HttpResponseMessage response, Uri url)
    {
        int code = (int)response.StatusCode;
        string detail = "";
        try
        {
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var m)) detail = m.GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
        }
        var rl = ReadRateLimit(response);
        string hint = code switch
        {
            401 => " The token is invalid or expired.",
            403 when rl is { Remaining: 0 } => $" Rate limit exhausted until {rl.Reset.ToLocalTime():t}." + (_token is null ? " Add a token to raise the limit." : ""),
            403 or 404 when _token is null => " If this repo is private, add a token that can read it.",
            404 => " Not found, or the token has no access to it.",
            _ => "",
        };
        var path = url.Host == ApiBase.Host ? url.PathAndQuery : url.Host; // never echo pre-signed download URLs
        return new GitHubApiException(code, $"GitHub returned {code} for {path}: {detail}{hint}".Trim(), rl);
    }
}
