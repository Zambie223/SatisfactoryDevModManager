using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sdmm.Core.Smm;

/// <param name="Profiles">Profiles that were changed.</param>
/// <param name="BackupPath">The copy of profiles.json made before the change, or null if nothing changed.</param>
/// <param name="Removed">For a removal: each mod's entries as they were, so they can be put back exactly.</param>
public sealed record SmmProfileEdit(IReadOnlyList<string> Profiles, string? BackupPath,
    IReadOnlyDictionary<string, IReadOnlyList<SmmProfileEntry>>? Removed = null);

/// <summary>A mod's entry in one SMM profile ("version" constraint and "enabled"), as recorded on takeover.</summary>
public sealed class SmmProfileEntry
{
    public string Profile { get; set; } = "";
    public string Version { get; set; } = ">=0.0.0";
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Edits mods in SMM's profiles (%APPDATA%\ficsit\profiles.json): removing them on takeover and adding them back on
/// hand-back. This is the one place SDMM writes SMM's files. Everything else in the file is kept as it was: only the "mods" entries for the given mod references change.
/// The file is backed up first (the newest few backups are kept) and replaced atomically. SMM must not be running,
/// because it keeps profiles in memory and writes them back, which would undo the change.
/// </summary>
public static class SmmProfileEditor
{
    public const string BackupPrefix = "profiles.json.sdmm-backup-";
    private const int BackupsKept = 5;
    /// <summary>The constraint SMM itself writes when you add a mod: any version, so the newest that fits.</summary>
    public const string DefaultConstraint = ">=0.0.0";

    /// <summary>Profiles in profiles.json that list <paramref name="modReference"/> (enabled or not).</summary>
    public static IReadOnlyList<string> ProfilesListing(SmmState smm, string modReference) =>
        smm.Profiles.Where(p => p.Value.Mods.ContainsKey(modReference)).Select(p => p.Key).ToList();

    /// <summary>Removes each mod from every profile that lists it. Throws if SMM is running or the file can't be read.</summary>
    /// <param name="findRunningSmm">Finds running SMM processes; defaults to <see cref="FindRunningSmm"/> (tests pass a fake).</param>
    public static SmmProfileEdit RemoveMods(string smmDirectory, IReadOnlyCollection<string> modReferences, TimeProvider? time = null,
        Func<IReadOnlyList<string>>? findRunningSmm = null)
    {
        var removed = new Dictionary<string, List<SmmProfileEntry>>(StringComparer.OrdinalIgnoreCase);
        var edit = Edit(smmDirectory, time, findRunningSmm, profiles =>
        {
            var changed = new List<string>();
            foreach (var (name, node) in profiles)
            {
                if (node?["mods"] is not JsonObject mods) continue;
                bool any = false;
                foreach (var modRef in modReferences)
                {
                    // Keys are compared exactly, as SMM does.
                    if (mods[modRef] is not { } entry) continue;
                    if (!removed.TryGetValue(modRef, out var list)) removed[modRef] = list = [];
                    list.Add(new SmmProfileEntry
                    {
                        Profile = name,
                        Version = entry["version"]?.GetValueKind() == JsonValueKind.String ? entry["version"]!.GetValue<string>() : DefaultConstraint,
                        Enabled = entry["enabled"]?.GetValueKind() != JsonValueKind.False,
                    });
                    any |= mods.Remove(modRef);
                }
                if (any) changed.Add(name);
            }
            return changed;
        });
        return edit with { Removed = removed.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SmmProfileEntry>)kv.Value, StringComparer.OrdinalIgnoreCase) };
    }

    /// <summary>
    /// Adds mods to profiles (hand-back). An entry that's already there is left alone. Profiles that don't exist are
    /// skipped (SDMM doesn't create profiles). Throws if SMM is running or the file can't be read.
    /// </summary>
    public static SmmProfileEdit AddMods(string smmDirectory, IReadOnlyDictionary<string, IReadOnlyList<SmmProfileEntry>> entriesByMod,
        TimeProvider? time = null, Func<IReadOnlyList<string>>? findRunningSmm = null) =>
        Edit(smmDirectory, time, findRunningSmm, profiles =>
        {
            var changed = new List<string>();
            foreach (var (modRef, entries) in entriesByMod)
                foreach (var e in entries)
                {
                    if (profiles[e.Profile] is not JsonObject profile) continue;
                    if (profile["mods"] is not JsonObject mods) profile["mods"] = mods = new JsonObject();
                    if (mods.ContainsKey(modRef)) continue;
                    mods[modRef] = new JsonObject { ["version"] = e.Version, ["enabled"] = e.Enabled };
                    if (!changed.Contains(e.Profile)) changed.Add(e.Profile);
                }
            return changed;
        });

    /// <summary>Loads profiles.json, lets <paramref name="change"/> edit the "profiles" object, and writes it back safely.</summary>
    private static SmmProfileEdit Edit(string smmDirectory, TimeProvider? time, Func<IReadOnlyList<string>>? findRunningSmm,
        Func<JsonObject, List<string>> change)
    {
        if ((findRunningSmm ?? FindRunningSmm)() is { Count: > 0 } running)
            throw new InvalidOperationException($"Close SMM first ({string.Join(", ", running)} is running). It keeps its profiles in memory and would undo the change.");

        var path = Path.Combine(smmDirectory, "profiles.json");
        if (!File.Exists(path)) return new SmmProfileEdit([], null);
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                   as JsonObject ?? throw new InvalidDataException($"{path} isn't a JSON object.");
        if (root["profiles"] is not JsonObject profiles) return new SmmProfileEdit([], null);

        var changed = change(profiles);
        if (changed.Count == 0) return new SmmProfileEdit([], null);

        var stamp = (time ?? TimeProvider.System).GetLocalNow().ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(smmDirectory, BackupPrefix + stamp);
        File.Copy(path, backup, overwrite: true);
        PruneBackups(smmDirectory);

        var tmp = path + ".sdmm-tmp";
        using (var stream = File.Create(tmp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        File.Move(tmp, path, overwrite: true);
        return new SmmProfileEdit(changed, backup);
    }

    /// <summary>
    /// Installed mods in this install that require <paramref name="modReference"/> (a non-optional dependency) and are
    /// enabled in one of <paramref name="profiles"/>. SMM pulls a dependency back in with the mod that needs it, so
    /// such a mod keeps <paramref name="modReference"/> in SMM's hands even after it's removed from the profile.
    /// </summary>
    public static IReadOnlyList<string> RequiredBy(SmmState smm, IEnumerable<string> profiles, IReadOnlyList<Installs.InstalledMod> installed, string modReference)
    {
        var listed = profiles.SelectMany(p => smm.Profiles.TryGetValue(p, out var prof) ? prof.Mods.Values.Where(m => m.Enabled).Select(m => m.ModReference) : [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed
            .Where(m => listed.Contains(m.ModReference) && !m.ModReference.Equals(modReference, StringComparison.OrdinalIgnoreCase) &&
                        m.Descriptor?.Plugins.Any(d => !d.Optional && d.Enabled && d.Name.Equals(modReference, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(m => m.ModReference).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Removes mods from the lockfiles of <paramref name="profiles"/> in this install (Mods/&lt;profile&gt;-lock.json).
    /// A lockfile is SMM's record of the last resolved set; SMM rebuilds it from the profile on its next apply, so a
    /// leftover entry is harmless to SMM but makes SDMM think SMM will reinstall the mod. Only call this for mods that
    /// nothing in the profile depends on (see <see cref="RequiredBy"/>). Returns the lockfiles changed.
    /// </summary>
    public static IReadOnlyList<string> RemoveFromLockfiles(Installs.IInstallFileSystem fs, IEnumerable<string> profiles, IReadOnlyCollection<string> modReferences)
    {
        var changed = new List<string>();
        foreach (var profile in profiles.Distinct())
        {
            var path = Installs.InstallPaths.Combine(Installs.InstallPaths.ModsDir, SmmState.LockfileName(profile));
            if (!fs.FileExists(path)) continue;
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(fs.ReadAllText(path)) as JsonObject;
            }
            catch (JsonException)
            {
                continue; // not ours to repair
            }
            if (root?["mods"] is not JsonObject mods) continue;
            bool any = false;
            foreach (var r in modReferences) any |= mods.Remove(r);
            if (!any) continue;
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true })) root.WriteTo(writer);
            fs.WriteAllText(path, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            changed.Add(path);
        }
        return changed;
    }

    /// <summary>Running SMM or ficsit-cli processes, by name. SDMM itself is excluded.</summary>
    public static IReadOnlyList<string> FindRunningSmm()
    {
        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var n = p.ProcessName.Replace(" ", "").Replace("-", "").ToLowerInvariant();
                if (n.Contains("devmodmanager")) continue;
                if (n.Contains("satisfactorymodmanager") || n is "ficsit" or "ficsitcli")
                    found.Add($"{p.ProcessName} (pid {p.Id})");
            }
        }
        return found;
    }

    private static void PruneBackups(string dir)
    {
        foreach (var old in new DirectoryInfo(dir).GetFiles(BackupPrefix + "*").OrderByDescending(f => f.Name).Skip(BackupsKept))
        {
            try { old.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
