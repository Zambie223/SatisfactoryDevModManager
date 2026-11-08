using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sdmm.Core.Security;

/// <summary>Engine counts from a VirusTotal analysis.</summary>
public sealed record VtStats(int Malicious, int Suspicious, int Harmless, int Undetected, int Other)
{
    /// <summary>Engines that gave a verdict (others timed out or couldn't read the file type).</summary>
    public int Engines => Malicious + Suspicious + Harmless + Undetected;
    public bool Flagged => Malicious + Suspicious > 0;
    public override string ToString() => $"{Malicious} malicious, {Suspicious} suspicious, of {Engines} engines";
}

/// <summary>VirusTotal refused or failed a request (wrong key, quota used up, network trouble...).</summary>
public sealed class VirusTotalException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>What the check needs from VirusTotal (an interface so the policy can be tested without the network).</summary>
public interface IVirusTotal
{
    /// <summary>The latest analysis of a file VirusTotal already knows, or null if it has never seen it.</summary>
    Task<VtStats?> LookupAsync(string sha256, CancellationToken ct = default);
    /// <summary>Uploads a file for scanning and returns the analysis id.</summary>
    Task<string> UploadAsync(string path, CancellationToken ct = default);
    /// <summary>Waits for an analysis to finish; null if it didn't within <paramref name="timeout"/>.</summary>
    Task<VtStats?> WaitForAnalysisAsync(string analysisId, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// VirusTotal's public API v3 (https://docs.virustotal.com/reference/overview), with the user's own free key.
/// <list type="bullet">
/// <item>Requests are spaced at least <see cref="MinSpacing"/> apart, because the free key allows 4 a minute (and 500 a
/// day). A wrong key gives 401, a used-up quota 429; both become <see cref="VirusTotalException"/>.</item>
/// <item>Files up to 32 MB are uploaded directly; bigger ones (up to 650 MB) through a one-time upload address.</item>
/// <item>The key travels only in the x-apikey header, which the detailed log never records.</item>
/// </list>
/// </summary>
public sealed class VirusTotalClient : IVirusTotal, IDisposable
{
    public static readonly Uri BaseUri = new("https://www.virustotal.com/api/v3/");
    public static readonly TimeSpan DefaultSpacing = TimeSpan.FromSeconds(15);
    public const long DirectUploadLimit = 32L * 1024 * 1024;
    public const long UploadLimit = 650L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;

    public TimeSpan MinSpacing { get; }

    /// <param name="minSpacing">Time between requests; tests pass zero.</param>
    public VirusTotalClient(string apiKey, HttpMessageHandler? handler = null, TimeSpan? minSpacing = null)
    {
        MinSpacing = minSpacing ?? DefaultSpacing;
        _http = new HttpClient(new Diagnostics.LoggingHandler(handler ?? new HttpClientHandler())) { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("x-apikey", apiKey.Trim());
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SatisfactoryDevModManager", Installing.Installer.ToolVersion));
    }

    public void Dispose() => _http.Dispose();

    public async Task<VtStats?> LookupAsync(string sha256, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "files/" + Uri.EscapeDataString(sha256.ToLowerInvariant())), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = await ReadAsync(response, ct);
        return Stats(doc.RootElement.GetProperty("data").GetProperty("attributes"), "last_analysis_stats");
    }

    public async Task<string> UploadAsync(string path, CancellationToken ct = default)
    {
        long size = new FileInfo(path).Length;
        if (size > UploadLimit) throw new VirusTotalException($"{Path.GetFileName(path)} is larger than VirusTotal accepts ({UploadLimit / 1048576} MB).");
        Uri target = new(BaseUri, "files");
        if (size > DirectUploadLimit)
        {
            using var urlResponse = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "files/upload_url"), ct);
            using var urlDoc = await ReadAsync(urlResponse, ct);
            target = new Uri(urlDoc.RootElement.GetProperty("data").GetString()!);
        }
        using var response = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new StreamContent(File.OpenRead(path));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", Path.GetFileName(path));
            return new HttpRequestMessage(HttpMethod.Post, target) { Content = form };
        }, ct);
        using var doc = await ReadAsync(response, ct);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetString()!;
    }

    public async Task<VtStats?> WaitForAnalysisAsync(string analysisId, TimeSpan timeout, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "analyses/" + Uri.EscapeDataString(analysisId)), ct))
            using (var doc = await ReadAsync(response, ct))
            {
                var attributes = doc.RootElement.GetProperty("data").GetProperty("attributes");
                if (attributes.TryGetProperty("status", out var status) && status.GetString() == "completed")
                    return Stats(attributes, "stats");
            }
            if (DateTime.UtcNow + MinSpacing > end) return null;
            // The spacing in SendAsync paces the polling (a scan usually takes a minute or two).
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequest + MinSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            HttpResponseMessage response;
            try
            {
                using var request = make();
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new VirusTotalException($"VirusTotal couldn't be reached: {ex.Message}", null, ex);
            }
            finally
            {
                _lastRequest = DateTime.UtcNow;
            }
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return response;
            var status = response.StatusCode;
            response.Dispose();
            throw status switch
            {
                HttpStatusCode.Unauthorized => new VirusTotalException("VirusTotal didn't accept the API key. Check it under Settings → Security.", status),
                HttpStatusCode.TooManyRequests => new VirusTotalException("The VirusTotal key's quota is used up for now (the free key allows 4 requests a minute and 500 a day).", status),
                _ => new VirusTotalException($"VirusTotal returned {(int)status}.", status),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new VirusTotalException("VirusTotal returned something that isn't JSON.", response.StatusCode, ex);
        }
    }

    private static VtStats Stats(JsonElement attributes, string property)
    {
        if (!attributes.TryGetProperty(property, out var s) || s.ValueKind != JsonValueKind.Object)
            throw new VirusTotalException("VirusTotal's answer had no scan results.");
        int Get(string name) => s.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;
        int other = s.EnumerateObject().Where(p => p.Name is not ("malicious" or "suspicious" or "harmless" or "undetected"))
            .Sum(p => p.Value.TryGetInt32(out var n) ? n : 0);
        return new VtStats(Get("malicious"), Get("suspicious"), Get("harmless"), Get("undetected"), other);
    }
}
