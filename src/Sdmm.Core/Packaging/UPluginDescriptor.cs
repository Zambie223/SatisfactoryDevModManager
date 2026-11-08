using System.Text.Json;
using Sdmm.Core.Versioning;

namespace Sdmm.Core.Packaging;

/// <summary>A dependency entry from a .uplugin "Plugins" array.</summary>
/// <param name="Name">Plugin (mod reference) name.</param>
/// <param name="SemVersionRange">SML version constraint, or null when the entry has none (SML then skips the version check).</param>
public sealed record PluginDependency(string Name, string? SemVersionRange, bool Optional, bool Enabled);

/// <summary>The parts of a mod's .uplugin file that matter for installing it.</summary>
public sealed class UPluginDescriptor
{
    /// <summary>Mod reference: the .uplugin file name without extension.</summary>
    public required string ModReference { get; init; }
    public string? FriendlyName { get; init; }
    public string? VersionName { get; init; }
    /// <summary>Raw "SemVersion" field, if present.</summary>
    public string? SemVersionText { get; init; }
    /// <summary>Raw UE integer "Version" field.</summary>
    public int UeVersion { get; init; }
    public string? GameVersionText { get; init; }
    public bool GameFeature { get; init; }
    public bool? RequiredOnRemote { get; init; }
    public string? RemoteVersionRangeText { get; init; }
    public IReadOnlyList<PluginDependency> Plugins { get; init; } = [];

    /// <summary>
    /// The version SML will report: SemVersion when it parses, otherwise UE "Version".0.0
    /// (FSMLPluginDescriptorMetadata::SetupDefaults / Load).
    /// </summary>
    public SemVersion EffectiveVersion =>
        SemVersion.TryParse(SemVersionText, out var v) ? v! : new SemVersion(Math.Max(UeVersion, 0), 0, 0);

    /// <summary>
    /// SML's RequiredOnRemote: whether the other side of a connection must also have this mod. True unless the
    /// .uplugin sets it to false, as server-side-only mods do (FSMLPluginDescriptorMetadata::Load).
    /// </summary>
    public bool IsRequiredOnRemote => RequiredOnRemote ?? true;

    /// <summary>
    /// The range SML checks the other side's version of this mod against: "RemoteVersionRange" when it parses,
    /// otherwise "&gt;= own version", SML's default.
    /// </summary>
    public VersionRange EffectiveRemoteVersionRange =>
        VersionRange.TryParse(RemoteVersionRangeText, out var r, out _) ? r! : VersionRange.AtLeast(EffectiveVersion);

    /// <summary>True when "SemVersion" is present and valid.</summary>
    public bool HasValidSemVersion => SemVersion.TryParse(SemVersionText, out _);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static UPluginDescriptor Parse(string modReference, string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{modReference}.uplugin is not a JSON object");

        var plugins = new List<PluginDependency>();
        if (root.TryGetProperty("Plugins", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                var name = GetString(p, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                plugins.Add(new PluginDependency(
                    name,
                    GetString(p, "SemVersion"),
                    GetBool(p, "Optional") ?? false,
                    GetBool(p, "Enabled") ?? true));
            }
        }

        return new UPluginDescriptor
        {
            ModReference = modReference,
            FriendlyName = GetString(root, "FriendlyName"),
            VersionName = GetString(root, "VersionName"),
            SemVersionText = GetString(root, "SemVersion"),
            UeVersion = root.TryGetProperty("Version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var iv) ? iv : 0,
            GameVersionText = GetString(root, "GameVersion"),
            GameFeature = GetBool(root, "GameFeature") ?? false,
            RequiredOnRemote = GetBool(root, "RequiredOnRemote"),
            RemoteVersionRangeText = GetString(root, "RemoteVersionRange"),
            Plugins = plugins,
        };
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
