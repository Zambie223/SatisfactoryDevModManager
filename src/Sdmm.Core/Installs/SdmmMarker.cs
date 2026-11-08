using System.Text.Json;
using System.Text.Json.Serialization;
using Sdmm.Core.Json;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Installs;

/// <summary>
/// Ownership marker written into every mod folder this tool installs (".sdmm.json").
/// The tool only changes or removes folders that carry this marker. It never writes SMM's ".smm" marker:
/// ficsit-cli deletes any ".smm"-marked folder that is not in the profile it applies, so an ".smm" marker
/// would get dev builds wiped by SMM.
/// </summary>
public sealed class SdmmMarker
{
    public const string FileName = ".sdmm.json";
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public required string ModReference { get; set; }
    public required string Version { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<ModTarget>))]
    public ModTarget Target { get; set; }
    /// <summary>SHA-256 of the zip that was installed.</summary>
    public required string Sha256 { get; set; }
    /// <summary>"owner/repo" the build came from, or null for a local file.</summary>
    public string? SourceRepo { get; set; }
    public string? SourceTag { get; set; }
    public string? AssetName { get; set; }
    /// <summary>
    /// Set when SDMM took over a mod another tool installed ("SMM", or "other" for a host's tool or a hand copy): who
    /// to hand it back to. Kept when SDMM later updates the mod; <see cref="IsAdoptedCopy"/> says whether the files are
    /// still the ones taken over (then <see cref="Sha256"/> is <see cref="AdoptedSha256"/>, as there's no zip of SDMM's).
    /// </summary>
    public string? AdoptedFrom { get; set; }

    /// <summary><see cref="AdoptedFrom"/> for a mod SMM installed.</summary>
    public const string AdoptedFromSmm = "SMM";
    /// <summary><see cref="AdoptedFrom"/> for a mod a host's tool or a person installed (no marker of anyone's).</summary>
    public const string AdoptedFromOther = "other";
    /// <summary>The contents of SMM's .smm marker that the takeover removed, kept so it can be traced or put back.</summary>
    public string? PreviousSmmMarker { get; set; }
    /// <summary>The SMM profile entries the takeover removed, so a hand-back to SMM can put them back exactly.</summary>
    public List<Smm.SmmProfileEntry>? PreviousSmmProfiles { get; set; }

    /// <summary>Placeholder hash for a taken-over folder; never matches a real zip, so any build installs over it.</summary>
    public const string AdoptedSha256 = "adopted";

    /// <summary>The files are still the ones another tool installed (taken over, not updated by SDMM since).</summary>
    [JsonIgnore]
    public bool IsAdoptedCopy => AdoptedFrom is not null && Sha256 == AdoptedSha256;
    public DateTimeOffset InstalledAtUtc { get; set; }
    public string? ToolVersion { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, CoreJsonContext.Default.SdmmMarker);

    /// <summary>Returns null when the text is not a readable marker.</summary>
    public static SdmmMarker? TryParse(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SdmmMarker);
            return m is { ModReference.Length: > 0, Sha256.Length: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
