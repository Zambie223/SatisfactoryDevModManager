using System.Text.Json;
using System.Text.Json.Serialization;
using Sdmm.Core.Config;
using Sdmm.Core.GitHub;
using Sdmm.Core.Installs;

namespace Sdmm.Core.Json;

/// <summary>
/// Source-generated JSON metadata, so Core works without reflection-based serialization (trimmed or
/// Native AOT hosts, and .NET 10 file-based apps, which disable reflection serialization by default).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SdmmMarker))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(EtagEntry))]
[JsonSerializable(typeof(Modpacks.ModpackFile))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>GitHub REST payloads use snake_case.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<GitHubRepo>))]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;

/// <summary>One cached GitHub response (see <see cref="FileEtagCache"/>).</summary>
internal sealed class EtagEntry
{
    public string Key { get; set; } = "";
    public string Etag { get; set; } = "";
    public string Body { get; set; } = "";
}
