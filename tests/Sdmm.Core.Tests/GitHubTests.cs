using System.Net;
using System.Text;
using Sdmm.Core.Cache;
using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Tests;

public class GitHubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Repo(string owner, string name, bool fork = false, bool archived = false, bool priv = false, params string[] topics) =>
        $$"""{ "id": 1, "name": "{{name}}", "full_name": "{{owner}}/{{name}}", "owner": { "login": "{{owner}}" }, "private": {{(priv ? "true" : "false")}}, "fork": {{(fork ? "true" : "false")}}, "archived": {{(archived ? "true" : "false")}}, "topics": [{{string.Join(",", topics.Select(t => $"\"{t}\""))}}] }""";

    private static string Release(string tag, bool prerelease = false, bool draft = false, params string[] assets) =>
        $$"""{ "id": 1, "tag_name": "{{tag}}", "name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}}, "published_at": "2026-11-01T00:00:00Z", "assets": [{{string.Join(",", assets.Select((a, i) => $$"""{ "id": {{100 + i}}, "name": "{{a}}", "size": 10 }"""))}}] }""";

    [Fact]
    public async Task DiscoversModsFromUsersOrgsAndExplicitRepos()
    {
        var http = new FakeHttp()
            .OnJson("/user/repos?affiliation=owner,collaborator,organization_member&per_page=100",
                $"[{Repo("me", "PrivateMod", priv: true)}, {Repo("stranger", "NotMine")}]")
            .OnJson("/users/me/repos?type=owner&per_page=100", $"[{Repo("me", "MyMod")}, {Repo("me", "AFork", fork: true)}, {Repo("me", "Old", archived: true)}]")
            .OnJson("/users/friend/repos?type=owner&per_page=100", $"[{Repo("friend", "FriendMod")}]")
            .OnJson("/orgs/team/repos?type=all&per_page=100", $"[{Repo("team", "TeamMods")}]")
            .OnJson("/repos/me/MyMod/releases?per_page=100",
                $"[{Release("v1.0.0", assets: ["MyMod-Windows.zip", "MyMod-LinuxServer.zip", "notes.txt"])}, {Release("v1.1.0-dev.1", prerelease: true, assets: ["MyMod-Windows.zip"])}, {Release("v2.0.0", draft: true, assets: ["MyMod-Windows.zip"])}]")
            .OnJson("/repos/me/PrivateMod/releases?per_page=100", $"[{Release("v0.1.0", assets: ["PrivateMod-Windows.zip"])}]")
            .OnJson("/repos/friend/FriendMod/releases?per_page=100", $"[{Release("latest", assets: ["FriendMod-Windows.zip"])}]")
            .OnJson("/repos/team/TeamMods/releases?per_page=100", $"[{Release("v3.0.0", assets: ["ModA-Windows.zip", "ModB-Windows.zip"])}]")
            .OnJson("/repos/other/Explicit/releases?per_page=100", $"[{Release("v1.0.0", assets: ["ExplicitMod-Windows.zip"])}]");
        using var gh = new GitHubClient("token", handler: http);

        var result = await new ModDiscovery(gh).DiscoverAsync(new SourceConfig
        {
            Users = ["me", "friend"],
            Orgs = ["team"],
            Repos = ["other/Explicit"],
        }, Ct);

        Assert.Empty(result.Errors);
        Assert.Equal(["friend/FriendMod", "me/MyMod", "me/PrivateMod", "other/Explicit", "team/TeamMods"], result.ScannedRepos);
        Assert.Equal(["ExplicitMod", "FriendMod", "ModA", "ModB", "MyMod", "PrivateMod"], result.Mods.Select(m => m.ModReference));

        var my = result.Mods.Single(m => m.ModReference == "MyMod");
        Assert.Equal(["v1.1.0-dev.1", "v1.0.0"], my.Builds.Select(b => b.Tag)); // draft skipped, newest first
        Assert.Equal("v1.0.0", my.Latest(includePrereleases: false)!.Tag);
        Assert.Equal("v1.1.0-dev.1", my.Latest(includePrereleases: true)!.Tag);
        Assert.Equal([ModTarget.Windows, ModTarget.LinuxServer], my.Builds[1].Assets.Keys);

        var friend = result.Mods.Single(m => m.ModReference == "FriendMod");
        Assert.Null(friend.Builds.Single().Version);  // non-semver tag is listed but not installable as "latest"
        Assert.Null(friend.Latest(true));
    }

    [Fact]
    public async Task TopicFilterAppliesToScansButNotExplicitRepos()
    {
        var http = new FakeHttp()
            .OnJson("/users/me/repos?type=owner&per_page=100", $"[{Repo("me", "Tagged", topics: "satisfactory-mod")}, {Repo("me", "Untagged")}]")
            .OnJson("/repos/me/Tagged/releases?per_page=100", "[]")
            .OnJson("/repos/me/Untagged/releases?per_page=100", "[]");
        using var gh = new GitHubClient(null, handler: http);
        var result = await new ModDiscovery(gh).DiscoverAsync(new SourceConfig { Users = ["me"], Repos = ["me/Untagged"], Topic = "satisfactory-mod" }, Ct);
        Assert.Equal(["me/Tagged", "me/Untagged"], result.ScannedRepos);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath == "/user/repos"); // no token: no private listing
    }

    [Fact]
    public async Task ErrorsAreReportedPerSourceWithoutFailingTheRest()
    {
        var http = new FakeHttp()
            .OnJson("/users/me/repos?type=owner&per_page=100", $"[{Repo("me", "MyMod")}]")
            .OnJson("/repos/me/MyMod/releases?per_page=100", $"[{Release("v1.0.0", assets: ["MyMod-Windows.zip"])}]");
        using var gh = new GitHubClient(null, handler: http);
        var result = await new ModDiscovery(gh).DiscoverAsync(new SourceConfig { Users = ["me", "ghost"], Repos = ["bad-entry"] }, Ct);
        Assert.Single(result.Mods);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("ghost") && e.Contains("404") && e.Contains("private"));
    }

    [Fact]
    public async Task FollowsPaginationAndReusesEtags()
    {
        var cache = new MemoryEtagCache();
        var http = new FakeHttp()
            .OnJson("/repos/me/M/releases?per_page=100", $"[{Release("v1.0.0", assets: ["M-Windows.zip"])}]", etag: "\"e1\"",
                link: "<https://api.github.com/repos/me/M/releases?per_page=100&page=2>; rel=\"next\"")
            .OnJson("/repos/me/M/releases?per_page=100&page=2", $"[{Release("v0.9.0", assets: ["M-Windows.zip"])}]", etag: "\"e2\"");
        using var gh = new GitHubClient("t", cache, http);

        Assert.Equal(2, (await gh.GetReleasesAsync("me", "M", Ct)).Count);
        int before = http.Requests.Count;
        var again = await gh.GetReleasesAsync("me", "M", Ct);   // both pages answered 304 from cache
        Assert.Equal(2, again.Count);
        Assert.Equal(before + 2, http.Requests.Count);
        Assert.All(http.Requests.Skip(before), r => Assert.True(r.Headers.Contains("If-None-Match")));
    }

    private static GitHubClient NoWaitClient(FakeHttp http, IEtagCache? cache = null) =>
        new("t", cache, http) { Delay = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task TransientErrorsAreRetried()
    {
        int calls = 0;
        var ok = $"[{Release("v1.0.0", assets: ["M-Windows.zip"])}]";
        var http = new FakeHttp().On("/repos/me/M/releases?per_page=100", _ =>
            ++calls <= 2
                ? new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok) });
        using var gh = NoWaitClient(http);

        Assert.Single(await gh.GetReleasesAsync("me", "M", Ct));
        Assert.Equal(3, calls);
        Assert.Empty(gh.TakeStaleNotes());
    }

    [Fact]
    public async Task FallsBackToCachedCopyWhenGitHubStaysDown()
    {
        bool down = false;
        var ok = $"[{Release("v1.0.0", assets: ["M-Windows.zip"])}]";
        var http = new FakeHttp().On("/repos/me/M/releases?per_page=100", _ =>
        {
            if (down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok) };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"");
            return r;
        });
        using var gh = NoWaitClient(http, new MemoryEtagCache());

        Assert.Single(await gh.GetReleasesAsync("me", "M", Ct));   // primes the cache
        down = true;
        Assert.Single(await gh.GetReleasesAsync("me", "M", Ct));   // served from cache after retries
        var note = Assert.Single(gh.TakeStaleNotes());
        Assert.Contains("503", note.Message);
        Assert.False(note.RateLimited);
    }

    private static HttpResponseMessage RateLimited()
    {
        var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"API rate limit exceeded for 1.2.3.4."}"""),
        };
        r.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "60");
        r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        r.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString());
        return r;
    }

    [Fact]
    public async Task RateLimitIsDetectedAndFurtherRequestsAreNotSent()
    {
        var http = new FakeHttp()
            .On("/repos/me/A/releases?per_page=100", _ => RateLimited())
            .OnJson("/repos/me/B/releases?per_page=100", "[]");
        using var gh = NoWaitClient(http);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => gh.GetReleasesAsync("me", "A", Ct));
        Assert.True(ex.IsRateLimited);
        Assert.NotNull(gh.RateLimitedUntil);
        Assert.Single(http.Requests);   // not retried: a used-up limit isn't transient

        var ex2 = await Assert.ThrowsAsync<GitHubApiException>(() => gh.GetReleasesAsync("me", "B", Ct));
        Assert.True(ex2.IsRateLimited);
        Assert.Single(http.Requests);   // B was never requested
    }

    [Fact]
    public async Task RateLimitFallsBackToCachedCopy()
    {
        bool limited = false;
        var ok = $"[{Release("v1.0.0", assets: ["M-Windows.zip"])}]";
        var http = new FakeHttp().On("/repos/me/M/releases?per_page=100", _ =>
        {
            if (limited) return RateLimited();
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok) };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e1\"");
            return r;
        });
        using var gh = NoWaitClient(http, new MemoryEtagCache());
        Assert.Single(await gh.GetReleasesAsync("me", "M", Ct));
        limited = true;
        Assert.Single(await gh.GetReleasesAsync("me", "M", Ct));
        Assert.True(Assert.Single(gh.TakeStaleNotes()).RateLimited);
    }

    [Fact]
    public async Task DiscoveryReportsPartialResultsWhenLimitRunsOut()
    {
        var http = new FakeHttp()
            .OnJson("/users/me/repos?type=owner&per_page=100", $"[{Repo("me", "ModOk")}, {Repo("me", "ModLimited")}]")
            .OnJson("/repos/me/ModOk/releases?per_page=100", $"[{Release("v1.0.0", assets: ["ModOk-Windows.zip"])}]")
            .On("/repos/me/ModLimited/releases?per_page=100", _ => RateLimited());
        using var gh = new GitHubClient(null, handler: http);

        var result = await new ModDiscovery(gh).DiscoverAsync(new SourceConfig { Users = ["me"] }, Ct);

        Assert.True(result.IsIncomplete);
        Assert.NotNull(result.RateLimitResetsAt);
        var problem = Assert.Single(result.Problems);
        Assert.Equal((ProblemKind.RateLimited, "me/ModLimited"), (problem.Kind, problem.Source));
        Assert.Equal(["ModOk"], result.Mods.Select(m => m.ModReference));
    }

    [Fact]
    public void OtherForbiddenErrorsAreNotRateLimits()
    {
        Assert.False(new GitHubApiException(403, "Resource not accessible by personal access token").IsRateLimited);
        Assert.True(new GitHubApiException(429, "Too many requests").IsRateLimited);
        Assert.True(new GitHubApiException(403, "x", new RateLimitInfo(60, 0, DateTimeOffset.UtcNow)).IsRateLimited);
    }

    [Fact]
    public async Task PersistentErrorWithoutCacheIsReported()
    {
        int calls = 0;
        var http = new FakeHttp().On("/repos/me/M/releases?per_page=100", _ => { calls++; return new HttpResponseMessage(HttpStatusCode.BadGateway); });
        using var gh = NoWaitClient(http);
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => gh.GetReleasesAsync("me", "M", Ct));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task NonTransientErrorsAreNotRetried()
    {
        int calls = 0;
        var http = new FakeHttp().On("/repos/me/M/releases?per_page=100", _ => { calls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized); });
        using var gh = NoWaitClient(http);
        await Assert.ThrowsAsync<GitHubApiException>(() => gh.GetReleasesAsync("me", "M", Ct));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AssetDownloadDoesNotLeakTokenToRedirectHost()
    {
        var http = new FakeHttp()
            .On("/repos/me/M/releases/assets/100", req =>
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://objects.githubusercontent.com/signed?sig=abc");
                return r;
            })
            .OnHost("objects.githubusercontent.com", req =>
            {
                Assert.Null(req.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("zipbytes")) };
            });
        using var gh = new GitHubClient("secret", handler: http);
        using var ms = new MemoryStream();
        await gh.DownloadAssetAsync("me", "M", 100, ms, ct: Ct);
        Assert.Equal("zipbytes", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task CacheRejectsDigestMismatchAndKeepsNothing()
    {
        using var tmp = new TempDir();
        var zipPath = Zips.Mod(tmp.Combine("src"), "M", "1.0.0");
        var bytes = await File.ReadAllBytesAsync(zipPath, Ct);
        var http = new FakeHttp().On("/repos/me/M/releases/assets/7", _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var gh = new GitHubClient(null, handler: http);
        var cache = new PackageCache(tmp.Combine("cache"));

        GitHubAsset Asset(string? digest) => new() { Id = 7, Name = "M-Windows.zip", Size = bytes.Length, Digest = digest };
        ModBuild Build(GitHubAsset a) => new("M", "me", "M", "v1.0.0", Versioning.SemVersion.Parse("1.0.0"), false, null, null, null,
            new Dictionary<ModTarget, GitHubAsset> { [ModTarget.Windows] = a });

        var bad = Build(Asset("sha256:" + new string('0', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync(gh, bad, ModTarget.Windows, ct: Ct));
        Assert.False(File.Exists(cache.PathFor(bad, bad.Assets[ModTarget.Windows])));

        var good = Build(Asset("sha256:" + ModPackage.ComputeSha256(zipPath)));
        var pkg = await cache.GetAsync(gh, good, ModTarget.Windows, ct: Ct);
        Assert.Equal("M", pkg.ModReference);
        int requests = http.Requests.Count;
        await cache.GetAsync(gh, good, ModTarget.Windows, ct: Ct);   // served from cache
        Assert.Equal(requests, http.Requests.Count);
    }
}
