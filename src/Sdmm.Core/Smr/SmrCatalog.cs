using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sdmm.Core.Packaging;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Smr;

/// <summary>One platform's download of an SMR version.</summary>
/// <param name="Link">Relative to <see cref="SmrClient.ApiRoot"/>, for example "/v1/version/{id}/Windows/download".</param>
/// <param name="Sha256">Hex SHA-256 of the zip, checked after download.</param>
public sealed record SmrTarget(ModTarget Target, string Link, long? Size, string? Sha256);

/// <param name="Condition">An SML version range, for example "^3.12.0".</param>
public sealed record SmrDependency(string ModReference, string Condition, bool Optional);

/// <summary>One published version of a mod on SMR.</summary>
/// <param name="Stability">"release", "beta" or "alpha".</param>
public sealed record SmrVersion(
    string Id,
    string ModReference,
    SemVersion Version,
    string? GameVersion,
    bool Approved,
    string Stability,
    bool RequiredOnRemote,
    DateTimeOffset? CreatedAt,
    string? Changelog,
    IReadOnlyDictionary<ModTarget, SmrTarget> Targets,
    IReadOnlyList<SmrDependency> Dependencies)
{
    public bool IsRelease => Stability.Equals("release", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sort orders for <see cref="SmrClient.SearchAsync"/>.</summary>
public enum SmrSort { Popular, RecentlyUpdated, MostDownloaded, Name }

/// <param name="Total">How many mods match in all, for paging.</param>
public sealed record SmrSearchResult(IReadOnlyList<SmrMod> Mods, int Total);

/// <summary>
/// Catalog access: searching SMR, reading a mod's versions (with per-platform downloads and dependencies), and
/// downloading a version's zip. Requests stay small and are made only
/// when the user searches or installs (SMR's fair-use policy forbids bulk downloading).
/// </summary>
public sealed partial class SmrClient
{
    public static readonly Uri ApiRoot = new("https://api.ficsit.app");
    private const int VersionsPerMod = 100;
    private const long MaxZipBytes = 2L * 1024 * 1024 * 1024;

    private const string LightFields = """
        fragment L on Mod {
          id name mod_reference short_description logo source_url views downloads
          authors { role user { username } }
          tags { name }
          latestVersions { release { version created_at } beta { version } alpha { version } }
          compatibility { EA { state note } EXP { state note } }
        }
        """;

    private const string VersionFields = """
        fragment V on Version {
          id version game_version approved stability required_on_remote created_at changelog
          targets { targetName link size hash }
          dependencies { mod_reference condition optional }
        }
        """;

    /// <summary>Searches SMR (or lists mods when <paramref name="search"/> is empty). Hidden mods are excluded by SMR.</summary>
    public async Task<SmrSearchResult> SearchAsync(string? search, SmrSort sort, int offset, int limit, CancellationToken ct = default)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        // With a search term, sort by relevance; SMR's "search" order field does that.
        var (orderBy, order) = search is not null && sort == SmrSort.Popular ? ("search", "desc") : sort switch
        {
            SmrSort.RecentlyUpdated => ("last_version_date", "desc"),
            SmrSort.MostDownloaded => ("downloads", "desc"),
            SmrSort.Name => ("name", "asc"),
            _ => ("popularity", "desc"),
        };
        const string query = "query($f: ModFilter) { getMods(filter: $f) { count mods { ...L } } } " + LightFields;
        var body = await PostAsync(query, w =>
        {
            w.WriteStartObject("f");
            w.WriteNumber("limit", Math.Clamp(limit, 1, 100));
            w.WriteNumber("offset", Math.Max(0, offset));
            w.WriteString("order_by", orderBy);
            w.WriteString("order", order);
            if (search is not null) w.WriteString("search", search);
            w.WriteEndObject();
        }, ct);
        return ParseSearch(body);
    }

    /// <summary>
    /// Every version of each mod (newest first), with targets and dependencies. Mods not on SMR are absent.
    /// </summary>
    public async Task<Dictionary<string, IReadOnlyList<SmrVersion>>> GetVersionsAsync(IEnumerable<string> modReferences, CancellationToken ct = default)
    {
        var refs = modReferences.Where(r => ModReferencePattern().IsMatch(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, IReadOnlyList<SmrVersion>>(StringComparer.OrdinalIgnoreCase);
        for (int start = 0; start < refs.Count; start += BatchSize)
        {
            var batch = refs.Skip(start).Take(BatchSize).ToList();
            var q = new StringBuilder("query(");
            q.Append(string.Join(", ", batch.Select((_, i) => $"$r{i}: ModReference!")));
            q.Append(") { ");
            for (int i = 0; i < batch.Count; i++)
                q.Append($"m{i}: getModByReference(modReference: $r{i}) {{ mod_reference versions(filter: {{ limit: {VersionsPerMod}, order_by: created_at, order: desc }}) {{ ...V }} }} ");
            q.Append("} ").Append(VersionFields);
            var body = await PostAsync(q.ToString(), w =>
            {
                for (int i = 0; i < batch.Count; i++) w.WriteString($"r{i}", batch[i]);
            }, ct);
            foreach (var (modRef, versions) in ParseVersions(body, batch.Count))
                result[modRef] = versions;
        }
        return result;
    }

    /// <summary>
    /// Downloads one platform's zip into <paramref name="destination"/> and checks it against SMR's SHA-256.
    /// Throws <see cref="SmrException"/> on a failed download or a hash mismatch.
    /// </summary>
    public async Task DownloadAsync(SmrTarget target, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!target.Link.StartsWith("/v1/", StringComparison.Ordinal))
            throw new SmrException($"Unexpected download link '{target.Link}'.");
        var uri = new Uri(ApiRoot, target.Link);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SmrException($"Could not download from SMR: {ex.Message}", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new SmrException($"SMR download returned {(int)response.StatusCode}.");
            await using var s = await response.Content.ReadAsStreamAsync(ct);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await s.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxZipBytes) throw new SmrException("The download is larger than any mod should be; stopped.");
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report(total);
            }
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (target.Sha256 is { Length: > 0 } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new SmrException($"The downloaded file's SHA-256 ({hash}) doesn't match SMR's ({expected}); it was not used.");
        }
    }

    private async Task<string> PostAsync(string query, Action<Utf8JsonWriter> writeVariables, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            w.WriteStartObject("variables");
            writeVariables(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
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
            if (!response.IsSuccessStatusCode) throw new SmrException($"SMR returned {(int)response.StatusCode}.");
            return body;
        }
    }

    internal static SmrSearchResult ParseSearch(string json)
    {
        using var doc = ParseData(json, out var data);
        if (!data.TryGetProperty("getMods", out var gm) || gm.ValueKind != JsonValueKind.Object)
            throw new SmrException("SMR's search answer had no results.");
        var mods = new List<SmrMod>();
        if (gm.TryGetProperty("mods", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
                if (ParseMod(m) is { } mod) mods.Add(mod);
        int total = gm.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : mods.Count;
        return new SmrSearchResult(mods, total);
    }

    internal static List<(string ModReference, IReadOnlyList<SmrVersion> Versions)> ParseVersions(string json, int count)
    {
        using var doc = ParseData(json, out var data);
        var result = new List<(string, IReadOnlyList<SmrVersion>)>();
        for (int i = 0; i < count; i++)
        {
            if (!data.TryGetProperty($"m{i}", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            var modRef = Str(m, "mod_reference");
            if (modRef is null) continue;
            var versions = new List<SmrVersion>();
            if (m.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
                foreach (var v in vs.EnumerateArray())
                    if (ParseVersion(modRef, v) is { } version) versions.Add(version);
            result.Add((modRef, versions.OrderByDescending(v => v.Version).ToList()));
        }
        return result;
    }

    private static SmrVersion? ParseVersion(string modRef, JsonElement v)
    {
        var id = Str(v, "id");
        if (id is null || !SemVersion.TryParse(Str(v, "version"), out var version)) return null;
        var targets = new Dictionary<ModTarget, SmrTarget>();
        if (v.TryGetProperty("targets", out var ts) && ts.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in ts.EnumerateArray())
            {
                if (!ModTargets.TryParse(Str(t, "targetName"), out var target) || Str(t, "link") is not { } link) continue;
                targets[target] = new SmrTarget(target, link, t.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : null, Str(t, "hash"));
            }
        }
        var deps = new List<SmrDependency>();
        if (v.TryGetProperty("dependencies", out var ds) && ds.ValueKind == JsonValueKind.Array)
            foreach (var d in ds.EnumerateArray())
                if (Str(d, "mod_reference") is { } depRef)
                    deps.Add(new SmrDependency(depRef, Str(d, "condition") ?? "*",
                        d.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True));
        return new SmrVersion(id, modRef, version!, Str(v, "game_version"),
            v.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True,
            Str(v, "stability") ?? "release",
            !v.TryGetProperty("required_on_remote", out var r) || r.ValueKind != JsonValueKind.False,
            Date(v, "created_at"), Str(v, "changelog"), targets, deps);
    }

    private static JsonDocument ParseData(string json, out JsonElement data)
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
        if (!doc.RootElement.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
        {
            var msg = doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
                ? Str(errs[0], "message") : null;
            doc.Dispose();
            throw new SmrException("SMR returned no data" + (msg is null ? "." : $": {msg}"));
        }
        return doc;
    }
}
