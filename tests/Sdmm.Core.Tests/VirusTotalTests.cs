using System.Net;
using System.Text;
using Sdmm.Core.Security;

namespace Sdmm.Core.Tests;

public class VirusTotalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Sha = "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999";

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string FileJson(int malicious, int suspicious = 0) =>
        $$"""{ "data": { "id": "{{Sha}}", "type": "file", "attributes": { "last_analysis_stats": { "malicious": {{malicious}}, "suspicious": {{suspicious}}, "harmless": 0, "undetected": 60, "timeout": 2, "type-unsupported": 5 } } } }""";

    [Theory]
    [InlineData("me/MyMod", true)]          // a dev build from GitHub
    [InlineData(null, true)]                // an imported zip
    [InlineData("ficsit.app/SML", false)]   // SMR scans its uploads with VirusTotal itself
    [InlineData("FICSIT.APP/SML", false)]
    public void OnlyDevBuildsAndImportedZipsAreChecked(string? repo, bool expected)
    {
        var request = new Installing.InstallRequest(null!, new Installing.PackageSource(repo, "v1.0.0", "x.zip"));
        Assert.Equal(expected, VirusTotalCheck.NeedsCheck(request));
    }

    [Fact]
    public async Task LooksUpByHashAndReadsTheStats()
    {
        var http = new FakeHttp().On($"/api/v3/files/{Sha}", _ => Json(FileJson(2, 1)));
        using var vt = new VirusTotalClient("key", http, TimeSpan.Zero);

        var stats = await vt.LookupAsync(Sha.ToUpperInvariant(), Ct);

        Assert.Equal((2, 1, 63), (stats!.Malicious, stats.Suspicious, stats.Engines));
        Assert.True(stats.Flagged);
        Assert.Equal("key", http.Requests[0].Headers.GetValues("x-apikey").Single());
    }

    [Fact]
    public async Task AnUnknownFileIsNullAndKeyOrQuotaProblemsAreErrors()
    {
        var http = new FakeHttp()
            .On("/api/v3/files/unknown", _ => Json("""{"error":{"code":"NotFoundError"}}""", HttpStatusCode.NotFound))
            .On("/api/v3/files/badkey", _ => Json("""{"error":{"code":"WrongCredentialsError"}}""", HttpStatusCode.Unauthorized))
            .On("/api/v3/files/quota", _ => Json("""{"error":{"code":"QuotaExceededError"}}""", HttpStatusCode.TooManyRequests));
        using var vt = new VirusTotalClient("key", http, TimeSpan.Zero);

        Assert.Null(await vt.LookupAsync("unknown", Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<VirusTotalException>(() => vt.LookupAsync("badkey", Ct))).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Assert.ThrowsAsync<VirusTotalException>(() => vt.LookupAsync("quota", Ct))).Status);
    }

    [Fact]
    public async Task UploadsAndWaitsForTheAnalysis()
    {
        using var tmp = new TempDir();
        var file = tmp.Combine("Mod-Windows.zip");
        File.WriteAllText(file, "zip");
        int polls = 0;
        var http = new FakeHttp()
            .On("/api/v3/files", req => req.Method == HttpMethod.Post ? Json("""{"data":{"type":"analysis","id":"an1"}}""") : Json("{}", HttpStatusCode.BadRequest))
            .On("/api/v3/analyses/an1", _ => ++polls < 2
                ? Json("""{"data":{"attributes":{"status":"queued","stats":{}}}}""")
                : Json("""{"data":{"attributes":{"status":"completed","stats":{"malicious":0,"suspicious":0,"harmless":0,"undetected":70}}}}"""));
        using var vt = new VirusTotalClient("key", http, TimeSpan.Zero);

        var id = await vt.UploadAsync(file, Ct);
        var stats = await vt.WaitForAnalysisAsync(id, TimeSpan.FromMinutes(1), Ct);

        Assert.Equal("an1", id);
        Assert.Equal(70, stats!.Engines);
        Assert.False(stats.Flagged);
        Assert.Equal(2, polls);
    }

    private sealed class FakeVt : IVirusTotal
    {
        public Dictionary<string, VtStats?> Known { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Lookups { get; } = [];
        public int Uploads;
        public Exception? Throw;
        public Task<VtStats?> LookupAsync(string sha256, CancellationToken ct = default)
        {
            Lookups.Add(sha256);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Known.TryGetValue(sha256, out var s) ? s : null);
        }
        public Task<string> UploadAsync(string path, CancellationToken ct = default) { Uploads++; return Task.FromResult("an"); }
        public Task<VtStats?> WaitForAnalysisAsync(string analysisId, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult<VtStats?>(new VtStats(0, 0, 0, 70, 0));
    }

    [Fact]
    public async Task EachFileIsCheckedOnceAndOnlyUploadedWhenAllowed()
    {
        using var tmp = new TempDir();
        var vt = new FakeVt();
        vt.Known["clean"] = new VtStats(0, 0, 5, 60, 0);
        vt.Known["bad"] = new VtStats(3, 0, 0, 60, 0);
        var check = new VirusTotalCheck(vt, tmp.Combine("checked.json"));
        VtFile F(string sha) => new(sha, tmp.Combine(sha + ".zip"), sha);

        var first = await check.CheckAsync([F("clean"), F("bad"), F("new")], upload: false, ct: Ct);
        Assert.Equal([VtOutcome.Clean, VtOutcome.Flagged, VtOutcome.Unknown], first.Select(r => r.Outcome));
        Assert.Equal(0, vt.Uploads); // nothing leaves the PC unless uploading is on

        var second = await check.CheckAsync([F("clean"), F("bad"), F("new")], upload: true, ct: Ct);
        Assert.Equal([VtOutcome.AlreadyChecked, VtOutcome.Flagged, VtOutcome.Clean], second.Select(r => r.Outcome));
        Assert.Equal(1, vt.Uploads);

        check.MarkAccepted(["bad"]); // installed anyway: not raised again
        var third = await check.CheckAsync([F("bad")], upload: false, ct: Ct);
        Assert.Equal(VtOutcome.AlreadyChecked, third.Single().Outcome);
    }

    [Fact]
    public async Task AKeyOrQuotaProblemDoesNotBlockAndStopsFurtherLookups()
    {
        using var tmp = new TempDir();
        var vt = new FakeVt { Throw = new VirusTotalException("quota", HttpStatusCode.TooManyRequests) };
        var check = new VirusTotalCheck(vt, tmp.Combine("checked.json"));

        var results = await check.CheckAsync([new("a", "a", "a"), new("b", "b", "b")], upload: true, ct: Ct);

        Assert.All(results, r => Assert.Equal(VtOutcome.NotChecked, r.Outcome));
        Assert.Single(vt.Lookups); // no point asking again with the quota used up
    }
}
