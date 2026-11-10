using System.Net;
using System.Text;
using System.Text.Json;
using Sdmm.Core.GitHub;
using Sdmm.Core.Smr;
using Sdmm.Core.Updates;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Tests;

public class SmrTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Found = """
        { "id": "abc123", "name": "Mod A", "mod_reference": "ModA", "short_description": "Short.", "full_description": "**Long**",
          "logo": "https://storage.ficsit.app/file/x/logo.webp", "source_url": "https://github.com/bob/ModA",
          "views": 10, "downloads": 5, "created_at": "2026-10-01T00:00:00Z", "updated_at": "2026-11-01T00:00:00Z",
          "authors": [ { "role": "creator", "user": { "username": "bob" } } ], "tags": [ { "name": "qol" } ],
          "latestVersions": { "release": { "version": "1.2.0", "created_at": "2026-11-01T00:00:00Z" }, "beta": null, "alpha": { "version": "1.3.0-a" } },
          "compatibility": { "EA": { "state": "Works", "note": "" }, "EXP": { "state": "Damaged", "note": "Menu glitch" } } }
        """;

    [Fact]
    public void BuildsOneAliasedQueryWithVariables()
    {
        var json = SmrClient.BuildRequest(["ModA", "ModB"]);
        using var doc = JsonDocument.Parse(json);
        var query = doc.RootElement.GetProperty("query").GetString()!;
        Assert.Contains("m0: getModByReference(modReference: $r0)", query);
        Assert.Contains("m1: getModByReference(modReference: $r1)", query);
        Assert.DoesNotContain("ModA", query);   // references only travel as variables
        Assert.Equal("ModB", doc.RootElement.GetProperty("variables").GetProperty("r1").GetString());
    }

    [Fact]
    public void ParsesFoundModsAndSkipsMissingOnes()
    {
        var body = $$"""{ "errors": [ { "message": "ent: mod not found", "path": ["m1"] } ], "data": { "m0": {{Found}}, "m1": null } }""";
        var mod = Assert.Single(SmrClient.ParseResponse(body, 2));
        Assert.Equal(("abc123", "ModA", "Mod A"), (mod.Id, mod.ModReference, mod.Name));
        Assert.Equal("1.2.0", mod.LatestRelease);
        Assert.Equal("1.3.0-a", mod.LatestAlpha);
        Assert.Null(mod.LatestBeta);
        Assert.Equal(("bob", "creator"), (mod.Authors[0].Username, mod.Authors[0].Role));
        Assert.Equal(["qol"], mod.Tags);
        Assert.Equal("Works", mod.EarlyAccess!.State);
        Assert.Null(mod.EarlyAccess.Note);           // empty notes become null
        Assert.Equal(("Damaged", "Menu glitch"), (mod.Experimental!.State, mod.Experimental.Note));
        Assert.Equal("https://ficsit.app/mod/abc123", mod.PageUrl);
    }

    [Fact]
    public void NoDataIsAnError()
    {
        var ex = Assert.Throws<SmrException>(() => SmrClient.ParseResponse("""{ "errors": [ { "message": "boom" } ] }""", 1));
        Assert.Contains("boom", ex.Message);
        Assert.Throws<SmrException>(() => SmrClient.ParseResponse("not json", 1));
    }

    [Theory]
    [InlineData("https://storage.ficsit.app/file/x/logo.webp", true)]
    [InlineData("https://ficsit.app/logo.png", true)]
    [InlineData("http://storage.ficsit.app/logo.webp", false)]
    [InlineData("https://evil.example/ficsit.app/logo.webp", false)]
    [InlineData("https://ficsit.app.evil.example/logo.webp", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData(null, false)]
    public void OnlyFetchesHttpsLogosFromFicsitApp(string? url, bool allowed) =>
        Assert.Equal(allowed, SmrClient.IsAllowedLogoUrl(url));

    [Fact]
    public async Task InvalidReferencesAreNeverSent()
    {
        string? sent = null;
        var http = new FakeHttp().On("/v2/query", req =>
        {
            sent = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{ "data": { "m0": {{Found}} } }""", Encoding.UTF8, "application/json") };
        });
        using var smr = new SmrClient(http);
        var mods = await smr.GetModsAsync(["ModA", "bad ref\") { evil }", ""], Ct);
        Assert.Single(mods);
        Assert.DoesNotContain("evil", sent);
    }
}

public class UpdateCheckerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly UpdateSource Source = new("alice", "SDMM", "SatisfactoryDevModManager.exe");

    private static string Release(string tag, bool pre = false, bool draft = false, string asset = "SatisfactoryDevModManager.exe") =>
        $$"""{ "id": 1, "tag_name": "{{tag}}", "prerelease": {{(pre ? "true" : "false")}}, "draft": {{(draft ? "true" : "false")}}, "body": "notes {{tag}}", "html_url": "https://github.com/alice/SDMM/releases/tag/{{tag}}", "assets": [ { "id": 9, "name": "{{asset}}", "size": 100 } ] }""";

    [Fact]
    public async Task FindsNewestStableUpdateWithTheExe()
    {
        var http = new FakeHttp().OnJson("/repos/alice/SDMM/releases?per_page=100",
            $"[{Release("v0.3.0", pre: true)}, {Release("v0.2.1")}, {Release("v0.2.5", draft: true)}, {Release("v0.2.2", asset: "Source.zip")}, {Release("v0.1.0")}]");
        using var gh = new GitHubClient(null, handler: http);
        var r = await UpdateChecker.CheckAsync(gh, Source, SemVersion.Parse("0.1.0"), ct: Ct);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, r.Status);
        Assert.Equal("v0.2.1", r.Update!.Tag);
        Assert.Equal("notes v0.2.1", r.Update.Notes);

        var withPre = await UpdateChecker.CheckAsync(gh, Source, SemVersion.Parse("0.1.0"), includePrereleases: true, ct: Ct);
        Assert.Equal("v0.3.0", withPre.Update!.Tag);
    }

    [Fact]
    public async Task FindsTheCommandLineToolInTheSameRelease()
    {
        var release = """{ "id": 1, "tag_name": "v0.4.0", "prerelease": false, "draft": false, "assets": [ { "id": 9, "name": "SatisfactoryDevModManager.exe", "size": 100 }, { "id": 10, "name": "SatisfactoryDevModManager-cli.exe", "size": 50 } ] }""";
        var http = new FakeHttp().OnJson("/repos/alice/SDMM/releases?per_page=100", $"[{release}]");
        using var gh = new GitHubClient(null, handler: http);

        var withCli = await UpdateChecker.CheckAsync(gh, Source with { CompanionAssetName = "SatisfactoryDevModManager-cli.exe" }, SemVersion.Parse("0.3.0"), ct: Ct);
        Assert.Equal(10, withCli.Update!.CompanionAsset!.Id);

        var without = await UpdateChecker.CheckAsync(gh, Source, SemVersion.Parse("0.3.0"), ct: Ct);
        Assert.Null(without.Update!.CompanionAsset);
    }

    [Fact]
    public async Task UpToDateWhenNothingNewer()
    {
        var http = new FakeHttp().OnJson("/repos/alice/SDMM/releases?per_page=100", $"[{Release("v0.1.0")}]");
        using var gh = new GitHubClient(null, handler: http);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await UpdateChecker.CheckAsync(gh, Source, SemVersion.Parse("0.1.0"), ct: Ct)).Status);
    }

    [Fact]
    public async Task MissingRepoIsReportedNotThrown()
    {
        using var gh = new GitHubClient(null, handler: new FakeHttp());   // every request 404s
        var r = await UpdateChecker.CheckAsync(gh, Source, SemVersion.Parse("0.1.0"), ct: Ct);
        Assert.Equal(UpdateCheckStatus.SourceNotFound, r.Status);
    }
}
