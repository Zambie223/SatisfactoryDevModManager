using System.IO.Compression;
using System.Net;
using System.Text;
using Sdmm.Core.Installing;

namespace Sdmm.Core.Tests;

/// <summary>A temp folder under SDMM_TEST_TEMP (set by tools\env.ps1 to M:), deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        var root = Environment.GetEnvironmentVariable("SDMM_TEST_TEMP");
        if (string.IsNullOrEmpty(root)) root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SDMM-tests");
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class Zips
{
    public static string UPlugin(string semVersion, bool gameFeature = false, string? gameVersion = ">=491125",
        params (string Name, string? Range, bool Optional)[] deps)
    {
        var plugins = deps.Length == 0
            ? """[{ "Name": "SML", "Enabled": true, "SemVersion": "^3.12.0" }]"""
            : "[" + string.Join(",", deps.Select(d =>
                $$"""{ "Name": "{{d.Name}}", "Enabled": true{{(d.Range is null ? "" : $", \"SemVersion\": \"{d.Range}\"")}}{{(d.Optional ? ", \"Optional\": true" : "")}} }""")) + "]";
        return $$"""
        {
            "FileVersion": 3,
            "Version": 1,
            "VersionName": "{{semVersion}}",
            "SemVersion": "{{semVersion}}",
            {{(gameVersion is null ? "" : $"\"GameVersion\": \"{gameVersion}\",")}}
            "FriendlyName": "Test Mod",
            "CanContainContent": true,
            "Plugins": {{plugins}},
            "GameFeature": {{(gameFeature ? "true" : "false")}}
        }
        """;
    }

    /// <summary>Creates "&lt;dir&gt;/&lt;fileName&gt;" with the given entries.</summary>
    public static string Create(string dir, string fileName, params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, c) in entries)
        {
            var e = zip.CreateEntry(p);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(c);
        }
        return path;
    }

    /// <summary>A typical Alpakit zip: .uplugin at root plus a pak file.</summary>
    public static string Mod(string dir, string modRef, string version, string target = "Windows", bool gameFeature = false,
        string? gameVersion = ">=491125", params (string Name, string? Range, bool Optional)[] deps) =>
        Create(System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N")[..8]), $"{modRef}-{target}.zip",
            ($"{modRef}.uplugin", UPlugin(version, gameFeature, gameVersion, deps)),
            ($"Content/Paks/{target}/{modRef}FactoryGame-{target}.pak", $"pak {modRef} {version}"));
}

/// <summary>Builds a fake game install folder.</summary>
public sealed class FakeGame
{
    public string Root { get; }

    public FakeGame(string root, string versionFile = "Engine/Binaries/Win64/FactoryGameSteam-Win64-Shipping.version", long changelist = 517554)
    {
        Root = root;
        Write(versionFile, $$"""{ "MajorVersion": 5, "Changelist": {{changelist}}, "GameVersion": "1.2.5.0" }""");
        // SML installed by SMM, as on a real modded install.
        Write("FactoryGame/Mods/SML/SML.uplugin", """{ "SemVersion": "3.12.0", "Version": 3, "GameVersion": ">=491125" }""");
        Write("FactoryGame/Mods/SML/.smm", "ff2f1d5a");
    }

    public string Full(string rel) => System.IO.Path.Combine(Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public FakeGame Write(string rel, string content)
    {
        var p = Full(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return this;
    }

    public bool Exists(string rel) => File.Exists(Full(rel)) || Directory.Exists(Full(rel));
    public string Read(string rel) => File.ReadAllText(Full(rel));
}

public sealed class NoGameRunning : IGameProcessDetector
{
    public IReadOnlyList<string> FindRunning(string localRoot) => [];
}

public sealed class GameRunning : IGameProcessDetector
{
    public IReadOnlyList<string> FindRunning(string localRoot) => ["FactoryGameSteam-Win64-Shipping (pid 1234)"];
}

/// <summary>Fake HTTP handler: routes by path+query, records requests.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public delegate HttpResponseMessage Route(HttpRequestMessage request);

    private readonly List<(Func<Uri, bool> Match, Route Respond)> _routes = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttp On(string pathAndQuery, Route respond)
    {
        _routes.Add((u => u.PathAndQuery == pathAndQuery, respond));
        return this;
    }

    public FakeHttp OnHost(string host, Route respond)
    {
        _routes.Add((u => u.Host == host, respond));
        return this;
    }

    public FakeHttp OnJson(string pathAndQuery, string json, string? etag = null, string? link = null) =>
        On(pathAndQuery, req =>
        {
            if (etag is not null && req.Headers.TryGetValues("If-None-Match", out var v) && v.Contains(etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            if (link is not null) r.Headers.TryAddWithoutValidation("Link", link);
            return r;
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        foreach (var (match, respond) in _routes)
            if (match(request.RequestUri!)) return Task.FromResult(respond(request));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}"""),
        });
    }
}
