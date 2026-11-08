using System.Text.Json;
using System.Text.RegularExpressions;
using Sdmm.Core.Installs;

namespace Sdmm.Core.Smm;

public sealed record SmmInstallation(string Path, string Profile, bool Vanilla);

public sealed record SmmProfileMod(string ModReference, string VersionConstraint, bool Enabled);

public sealed record SmmProfile(string Name, IReadOnlyDictionary<string, SmmProfileMod> Mods);

/// <summary>Why SMM would replace a dev build on its next apply.</summary>
public sealed record SmmConflict(string ProfileName, string Reason);

/// <summary>
/// Read-only view of SMM / ficsit-cli state (%APPDATA%\ficsit\installations.json and profiles.json).
/// Only takeover and hand-back edit profiles.json, through <see cref="SmmProfileEditor"/>.
/// </summary>
public sealed partial class SmmState
{
    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ficsit");

    public bool Found { get; private init; }
    public string Directory { get; private init; } = "";
    public IReadOnlyList<SmmInstallation> Installations { get; private init; } = [];
    public IReadOnlyDictionary<string, SmmProfile> Profiles { get; private init; } = new Dictionary<string, SmmProfile>();
    /// <summary>Problems reading the files. The state is still usable but may be incomplete.</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static SmmState Load(string? directory = null)
    {
        directory ??= DefaultDirectory;
        var warnings = new List<string>();
        var installations = new List<SmmInstallation>();
        var profiles = new Dictionary<string, SmmProfile>(StringComparer.Ordinal);
        var instPath = System.IO.Path.Combine(directory, "installations.json");
        var profPath = System.IO.Path.Combine(directory, "profiles.json");
        bool found = File.Exists(instPath) || File.Exists(profPath);

        if (File.Exists(instPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(instPath), JsonOptions);
                if (doc.RootElement.TryGetProperty("installations", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var i in arr.EnumerateArray())
                    {
                        var path = Str(i, "path");
                        if (string.IsNullOrEmpty(path)) continue;
                        installations.Add(new SmmInstallation(path, Str(i, "profile") ?? "",
                            i.TryGetProperty("vanilla", out var v) && v.ValueKind == JsonValueKind.True));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warnings.Add($"Could not read {instPath}: {ex.Message}");
            }
        }

        if (File.Exists(profPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(profPath), JsonOptions);
                if (doc.RootElement.TryGetProperty("profiles", out var obj) && obj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in obj.EnumerateObject())
                    {
                        var mods = new Dictionary<string, SmmProfileMod>(StringComparer.Ordinal);
                        if (p.Value.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var mod in m.EnumerateObject())
                            {
                                bool enabled = !mod.Value.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
                                mods[mod.Name] = new SmmProfileMod(mod.Name, Str(mod.Value, "version") ?? "", enabled);
                            }
                        }
                        var name = Str(p.Value, "name") ?? p.Name;
                        profiles[p.Name] = new SmmProfile(name, mods);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warnings.Add($"Could not read {profPath}: {ex.Message}");
            }
        }

        return new SmmState
        {
            Found = found,
            Directory = directory,
            Installations = installations,
            Profiles = profiles,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// SMM installations whose path is this local folder. Matching ignores case and trailing separators,
    /// because Windows paths are case-insensitive. There can be more than one: SMM itself does not
    /// de-duplicate paths that differ only in case.
    /// </summary>
    public IReadOnlyList<SmmInstallation> FindInstallations(string localRoot)
    {
        var target = Normalize(localRoot);
        return Installations.Where(i => !IsRemote(i.Path) && Normalize(i.Path).Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Checks whether SMM would overwrite a dev install of <paramref name="modReference"/> in this install.
    /// ficsit-cli overwrites a mod folder (marker or not) when the mod is in the resolved lockfile of the profile it
    /// applies, so this checks every profile that SMM has attached to this install: the enabled mods it lists, and
    /// its lockfile (which also contains resolved dependencies).
    /// </summary>
    public IReadOnlyList<SmmConflict> FindConflicts(string localRoot, IInstallFileSystem fs, string modReference)
    {
        var conflicts = new List<SmmConflict>();
        foreach (var inst in FindInstallations(localRoot))
        {
            if (inst.Vanilla) continue; // a vanilla apply installs nothing; it only removes .smm-marked folders
            if (conflicts.Any(c => c.ProfileName == inst.Profile)) continue;

            if (Profiles.TryGetValue(inst.Profile, out var profile) &&
                profile.Mods.TryGetValue(modReference, out var mod) && mod.Enabled)
            {
                conflicts.Add(new SmmConflict(inst.Profile,
                    $"SMM profile '{inst.Profile}' includes {modReference}. SMM will replace the dev build the next time it applies that profile. Remove {modReference} from the profile in SMM first."));
                continue;
            }

            var lockPath = InstallPaths.Combine(InstallPaths.ModsDir, LockfileName(inst.Profile));
            if (fs.FileExists(lockPath) && LockfileContains(fs.ReadAllText(lockPath), modReference))
            {
                conflicts.Add(new SmmConflict(inst.Profile,
                    $"SMM profile '{inst.Profile}' pulls in {modReference} as a dependency of another mod. SMM will replace the dev build the next time it applies that profile."));
            }
        }
        return conflicts;
    }

    internal static bool LockfileContains(string json, string modReference)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            return doc.RootElement.TryGetProperty("mods", out var mods) &&
                   mods.ValueKind == JsonValueKind.Object &&
                   mods.TryGetProperty(modReference, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("(.)([A-Z][a-z]+)")] private static partial Regex MatchFirstCap();
    [GeneratedRegex("([a-z0-9])([A-Z])")] private static partial Regex MatchAllCap();
    // ficsit-cli's pattern is `[^a-zA-Z\d]]`. The stray ']' means it only matches a non-alphanumeric character
    // followed by ']', so spaces and most symbols are NOT replaced. Copied as-is so the computed name matches the
    // file SMM really writes (for example "my profile 2-lock.json").
    [GeneratedRegex(@"[^a-zA-Z\d]]")] private static partial Regex LockfileCleaner();

    /// <summary>The lockfile name ficsit-cli writes into FactoryGame/Mods for a profile.</summary>
    public static string LockfileName(string profileName)
    {
        var s = MatchFirstCap().Replace(profileName, "${1}_${2}");
        s = MatchAllCap().Replace(s, "${1}_${2}");
        s = LockfileCleaner().Replace(s, "-");
        return s.ToLowerInvariant() + "-lock.json";
    }

    private static bool IsRemote(string path) => path.Contains("://", StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
