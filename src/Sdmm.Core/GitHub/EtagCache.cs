using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sdmm.Core.Json;

namespace Sdmm.Core.GitHub;

/// <summary>
/// Stores GitHub responses by URL with their ETag. GitHub answers a matching If-None-Match with 304, and an
/// authenticated 304 does not count against the rate limit, so repeated refreshes stay cheap.
/// </summary>
public interface IEtagCache
{
    bool TryGet(string key, out string etag, out string body);
    void Set(string key, string etag, string body);
}

public sealed class MemoryEtagCache : IEtagCache
{
    private readonly Dictionary<string, (string Etag, string Body)> _entries = new();
    private readonly Lock _lock = new();

    public bool TryGet(string key, out string etag, out string body)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var e)) { etag = e.Etag; body = e.Body; return true; }
        }
        etag = body = "";
        return false;
    }

    public void Set(string key, string etag, string body)
    {
        lock (_lock) _entries[key] = (etag, body);
    }
}

/// <summary>On-disk cache, one file per key, so refreshes stay cheap across restarts.</summary>
public sealed class FileEtagCache : IEtagCache
{
    private readonly string _dir;

    public FileEtagCache(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string key) =>
        Path.Combine(_dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".json");

    public bool TryGet(string key, out string etag, out string body)
    {
        etag = body = "";
        try
        {
            var p = PathFor(key);
            if (!File.Exists(p)) return false;
            var entry = JsonSerializer.Deserialize(File.ReadAllText(p), CoreJsonContext.Default.EtagEntry);
            if (entry is null || entry.Key != key) return false;
            etag = entry.Etag;
            body = entry.Body;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Set(string key, string etag, string body)
    {
        try
        {
            var p = PathFor(key);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new EtagEntry { Key = key, Etag = etag, Body = body }, CoreJsonContext.Default.EtagEntry));
            File.Move(tmp, p, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is best-effort.
        }
    }
}
