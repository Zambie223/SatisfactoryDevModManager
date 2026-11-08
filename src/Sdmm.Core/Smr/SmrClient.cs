using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sdmm.Core.Smr;

public sealed record SmrAuthor(string Username, string Role);

/// <param name="State">"Works", "Damaged", "Broken" or similar, as SMR reports it.</param>
public sealed record SmrCompatibility(string State, string? Note);

/// <summary>A mod's public listing on SMR (ficsit.app), used to show its icon, description and stats.</summary>
public sealed record SmrMod(
    string Id,
    string ModReference,
    string Name,
    string? ShortDescription,
    string? FullDescription,
    string? LogoUrl,
    string? SourceUrl,
    long Views,
    long Downloads,
    IReadOnlyList<SmrAuthor> Authors,
    IReadOnlyList<string> Tags,
    string? LatestRelease,
    DateTimeOffset? LatestReleaseDate,
    string? LatestBeta,
    string? LatestAlpha,
    SmrCompatibility? EarlyAccess,
    SmrCompatibility? Experimental)
{
    public string PageUrl => $"https://ficsit.app/mod/{Uri.EscapeDataString(Id)}";
}

public sealed class SmrException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read-only client for SMR's public GraphQL API (the one SMM uses). Looks up many mods in one request by
/// aliasing getModByReference, and passes mod references as GraphQL variables, never pasted into the query text.
/// </summary>
public sealed partial class SmrClient : IDisposable
{
    public static readonly Uri Endpoint = new("https://api.ficsit.app/v2/query");
    private const int BatchSize = 20;
    private const long MaxLogoBytes = 5 * 1024 * 1024;

    private readonly HttpClient _http;

    public SmrClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(new Diagnostics.LoggingHandler(handler ?? new HttpClientHandler()));
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SatisfactoryDevModManager", Installing.Installer.ToolVersion));
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ModReferencePattern();

    private const string Fields = """
        fragment M on Mod {
          id name mod_reference short_description full_description logo source_url views downloads
          authors { role user { username } }
          tags { name }
          latestVersions { release { version created_at } beta { version } alpha { version } }
          compatibility { EA { state note } EXP { state note } }
        }
        """;

    /// <summary>
    /// Looks up mods by reference. Mods that are not on SMR are simply absent from the result.
    /// Throws <see cref="SmrException"/> if SMR can't be reached or answers with something unusable.
    /// </summary>
    public async Task<Dictionary<string, SmrMod>> GetModsAsync(IEnumerable<string> modReferences, CancellationToken ct = default)
    {
        var refs = modReferences.Where(r => ModReferencePattern().IsMatch(r)).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, SmrMod>(StringComparer.OrdinalIgnoreCase);
        for (int start = 0; start < refs.Count; start += BatchSize)
        {
            var batch = refs.Skip(start).Take(BatchSize).ToList();
            using var content = new StringContent(BuildRequest(batch), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(Endpoint, content, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SmrException($"Could not reach SMR: {ex.Message}", ex);
            }
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new SmrException($"SMR returned {(int)response.StatusCode}.");
                foreach (var mod in ParseResponse(body, batch.Count))
                    result[mod.ModReference] = mod;
            }
        }
        return result;
    }

    /// <summary>Builds { query, variables } with one aliased lookup per reference: m0..mN with $r0..$rN.</summary>
    internal static string BuildRequest(IReadOnlyList<string> refs)
    {
        var q = new StringBuilder("query(");
        q.Append(string.Join(", ", refs.Select((_, i) => $"$r{i}: ModReference!")));
        q.Append(") { ");
        for (int i = 0; i < refs.Count; i++) q.Append($"m{i}: getModByReference(modReference: $r{i}) {{ ...M }} ");
        q.Append("} ").Append(Fields);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("query", q.ToString());
            w.WriteStartObject("variables");
            for (int i = 0; i < refs.Count; i++) w.WriteString($"r{i}", refs[i]);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Reads m0..m(count-1) from the response. A missing mod is null with a "mod not found" error, which is normal.</summary>
    internal static List<SmrMod> ParseResponse(string json, int count)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SmrException("SMR returned something that isn't JSON.", ex);
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                var msg = doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
                    ? Str(errs[0], "message") : null;
                throw new SmrException("SMR returned no data" + (msg is null ? "." : $": {msg}"));
            }
            var mods = new List<SmrMod>();
            for (int i = 0; i < count; i++)
            {
                if (data.TryGetProperty($"m{i}", out var m) && m.ValueKind == JsonValueKind.Object && ParseMod(m) is { } mod)
                    mods.Add(mod);
            }
            return mods;
        }
    }

    private static SmrMod? ParseMod(JsonElement m)
    {
        var id = Str(m, "id");
        var modRef = Str(m, "mod_reference");
        if (id is null || modRef is null) return null;

        var authors = new List<SmrAuthor>();
        if (m.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray())
                if (x.TryGetProperty("user", out var u) && Str(u, "username") is { } name)
                    authors.Add(new SmrAuthor(name, Str(x, "role") ?? ""));

        var tags = new List<string>();
        if (m.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var x in t.EnumerateArray())
                if (Str(x, "name") is { } tag) tags.Add(tag);

        string? release = null, beta = null, alpha = null;
        DateTimeOffset? releaseDate = null;
        if (m.TryGetProperty("latestVersions", out var lv) && lv.ValueKind == JsonValueKind.Object)
        {
            if (lv.TryGetProperty("release", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                release = Str(r, "version");
                releaseDate = Date(r, "created_at");
            }
            if (lv.TryGetProperty("beta", out var b) && b.ValueKind == JsonValueKind.Object) beta = Str(b, "version");
            if (lv.TryGetProperty("alpha", out var al) && al.ValueKind == JsonValueKind.Object) alpha = Str(al, "version");
        }

        SmrCompatibility? ea = null, exp = null;
        if (m.TryGetProperty("compatibility", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            ea = Compat(c, "EA");
            exp = Compat(c, "EXP");
        }

        return new SmrMod(id, modRef, Str(m, "name") ?? modRef, Str(m, "short_description"), Str(m, "full_description"),
            Str(m, "logo"), Str(m, "source_url"), Long(m, "views"), Long(m, "downloads"),
            authors, tags, release, releaseDate, beta, alpha, ea, exp);
    }

    /// <summary>
    /// Downloads a mod logo. Only https URLs on ficsit.app hosts are fetched, and at most 5 MB, because the URL
    /// comes from API data. Returns null if the URL is not allowed or the download fails.
    /// </summary>
    public async Task<byte[]?> DownloadLogoAsync(string url, CancellationToken ct = default)
    {
        if (!IsAllowedLogoUrl(url)) return null;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxLogoBytes) return null;
            await using var s = await response.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await s.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxLogoBytes) return null;
            }
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static bool IsAllowedLogoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("ficsit.app", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".ficsit.app", StringComparison.OrdinalIgnoreCase));

    private static SmrCompatibility? Compat(JsonElement c, string name) =>
        c.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Object && Str(x, "state") is { } state
            ? new SmrCompatibility(state, string.IsNullOrWhiteSpace(Str(x, "note")) ? null : Str(x, "note")) : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
