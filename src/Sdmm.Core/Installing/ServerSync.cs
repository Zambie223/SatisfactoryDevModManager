using Sdmm.Core.Discovery;
using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Installing;

public enum SyncStatus
{
    /// <summary>SML will accept this mod on both sides (same version, or each side's RemoteVersionRange accepts the other).</summary>
    InSync,
    /// <summary>The side being matched has a dev build the other side lacks or has at an incompatible version; installable.</summary>
    Install,
    /// <summary>
    /// Like <see cref="Install"/>, but the mod came from SMR (installed from SMR, or by SMM or a host's tool), so the same
    /// version is installed from SMR.
    /// </summary>
    FromSmr,
    /// <summary>The side being changed has a dev build the other side lacks, and SML requires it on both sides; removable.</summary>
    Remove,
    /// <summary>Only the server has it, and its .uplugin says players don't need it (RequiredOnRemote: false).</summary>
    ServerOnly,
    /// <summary>Only the game has it, and its .uplugin says the server doesn't need it (RequiredOnRemote: false).</summary>
    ClientOnly,
    /// <summary>The build to install was not found in the configured sources, or has no build for that platform.</summary>
    BuildNotFound,
    /// <summary>The sides don't match, but SDMM doesn't manage the mod on the side being changed (SMM, the host or by hand).</summary>
    ManagedElsewhere,
}

/// <summary>Which side is changed: the game to match the server, or the server to match the game.</summary>
public enum SyncDirection { GameMatchesServer, ServerMatchesGame }

/// <param name="Build">The build to install when <see cref="Status"/> is <see cref="SyncStatus.Install"/>.</param>
/// <param name="SmrVersion">The version to install from SMR when <see cref="Status"/> is <see cref="SyncStatus.FromSmr"/>.</param>
public sealed record SyncItem(string ModReference, string? ServerVersion, string? LocalVersion, SyncStatus Status, ModBuild? Build, string Message,
    string? SmrVersion = null)
{
    /// <summary>True for the lines "Match" can act on: install or remove on the side being changed.</summary>
    public bool IsActionable => Status is SyncStatus.Install or SyncStatus.Remove or SyncStatus.FromSmr;
}

/// <summary>
/// Compares a server install with a game install and predicts whether SML will let the game join. One side is the
/// source of truth and the other is changed to match it (<see cref="SyncDirection"/>). SML checks both directions when
/// a player connects (SMLNetworkManager.cpp, ValidateSMLConnectionData):
/// <list type="bullet">
/// <item>A mod one side has and the other lacks is refused unless its .uplugin sets RequiredOnRemote to false
/// (server-side-only mods such as DepotNetworkFix, or client-only ones). The client side also ignores SML itself.
/// Such one-sided mods are never installed on, or removed from, the other side.</item>
/// <item>A mod both sides have must pass each side's RemoteVersionRange for the other side's version. The default
/// range is "&gt;= own version", so by default the versions must be equal.</item>
/// </list>
/// </summary>
public static class ServerSync
{
    /// <summary>Marker SourceRepo prefix for mods installed from SMR ("ficsit.app/AutoTrains").</summary>
    public const string SmrSourcePrefix = "ficsit.app/";

    /// <param name="targetPlatform">The platform of the side being changed; builds installed there must exist for it.</param>
    public static IReadOnlyList<SyncItem> Compare(IReadOnlyList<InstalledMod> server, IReadOnlyList<InstalledMod> local,
        DiscoveryResult discovery, ModTarget targetPlatform = ModTarget.Windows, SyncDirection direction = SyncDirection.GameMatchesServer)
    {
        bool toGame = direction == SyncDirection.GameMatchesServer;
        var truth = Unique(toGame ? server : local);
        var follower = Unique(toGame ? local : server);
        string truthName = toGame ? "the server" : "your game", followName = toGame ? "your game" : "the server";
        var items = new List<SyncItem>();

        SyncItem Item(string modRef, InstalledMod? t, InstalledMod? f, SyncStatus status, string message, ModBuild? build = null, string? smr = null)
        {
            var (s, l) = toGame ? (t, f) : (f, t);
            return new SyncItem(modRef, s is null ? null : Version(s), l is null ? null : Version(l), status, build, message, smr);
        }

        foreach (var t in truth.Values)
        {
            follower.TryGetValue(t.ModReference, out var f);
            if (f is null)
            {
                if (t.Descriptor is { IsRequiredOnRemote: false })
                {
                    items.Add(Item(t.ModReference, t, null, toGame ? SyncStatus.ServerOnly : SyncStatus.ClientOnly,
                        $"Only {truthName} needs it: its .uplugin sets RequiredOnRemote to false, so SML doesn't require it on {followName}."));
                    continue;
                }
                items.Add(Need(t, null));
                continue;
            }
            if (IsCompatible(toGame ? t : f, toGame ? f : t, out var why))
            {
                items.Add(Item(t.ModReference, t, f, SyncStatus.InSync, why));
                continue;
            }
            if (f.Owner != ModOwner.Sdmm)
            {
                items.Add(Item(t.ModReference, t, f, SyncStatus.ManagedElsewhere,
                    $"{why} The copy on {followName} is {OwnerText(f.Owner)}, so SDMM won't change it; update it where it was installed."));
                continue;
            }
            items.Add(Need(t, f));
        }

        foreach (var f in follower.Values.Where(m => !truth.ContainsKey(m.ModReference)))
        {
            // One-sided mods stay where they are: a server keeps its server-only mods, a game its client-only ones.
            // The client-side check also skips SML, so a game's SML never blocks anything.
            bool smlOnGame = toGame && f.ModReference.Equals("SML", StringComparison.OrdinalIgnoreCase);
            if (f.Descriptor is { IsRequiredOnRemote: false } || smlOnGame)
            {
                items.Add(Item(f.ModReference, null, f, toGame ? SyncStatus.ClientOnly : SyncStatus.ServerOnly, smlOnGame
                    ? "SML on your game doesn't need to be on the server for you to join, so it stays."
                    : $"Only {followName} needs it: its .uplugin sets RequiredOnRemote to false, so it stays."));
            }
            else if (f.Owner == ModOwner.Sdmm)
            {
                items.Add(Item(f.ModReference, null, f, SyncStatus.Remove,
                    $"{(toGame ? "The server" : "Your game")} doesn't have this dev build, and SML refuses the connection while {followName} has it. Remove it from {followName} (it stays in the download cache)."));
            }
            else
            {
                items.Add(Item(f.ModReference, null, f, SyncStatus.ManagedElsewhere,
                    $"{(toGame ? "The server" : "Your game")} doesn't have it, and SML refuses the connection while {followName} has it ({OwnerText(f.Owner)} there). Remove it where it was installed, or add it to {truthName}."));
            }
        }

        return items.OrderBy(i => i.Status == SyncStatus.InSync).ThenBy(i => i.ModReference, StringComparer.OrdinalIgnoreCase).ToList();

        // The truth side's copy must be installed on the follower side.
        SyncItem Need(InstalledMod t, InstalledMod? f)
        {
            var tv = Version(t) ?? "?";
            var change = f is null ? $"Install {tv} on {followName}" : $"Change {Version(f)} to {tv} on {followName}";
            // Installed from SMR, or by SMM / a host's tool / a hand copy / taken over (all normally from SMR): install the
            // same version from SMR. GitHub and imported dev builds are found in the sources below.
            bool fromSmr = t.Owner != ModOwner.Sdmm || t.Marker?.IsAdoptedCopy == true ||
                           t.Marker?.SourceRepo?.StartsWith(SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) == true;
            if (fromSmr)
                return Item(t.ModReference, t, f, SyncStatus.FromSmr,
                    $"{change} to match {truthName}. It came from SMR{(t.Owner != ModOwner.Sdmm ? $" ({OwnerText(t.Owner)} on {truthName})" : "")}, so install that version from SMR (for example with SMM).",
                    smr: Version(t));
            var build = FindBuild(discovery, t);
            if (build is null)
                return Item(t.ModReference, t, f, SyncStatus.BuildNotFound, t.Marker is { SourceRepo: null }
                    ? $"{truthName[0].ToString().ToUpperInvariant()}{truthName[1..]}'s build ({tv}) was installed from a zip. Add that version's {targetPlatform} zip with Install from zip, then compare again."
                    : $"{truthName[0].ToString().ToUpperInvariant()}{truthName[1..]}'s build ({t.Marker?.SourceRepo ?? "unknown repo"} {t.Marker?.SourceTag ?? tv}) was not found in your GitHub sources.");
            if (!build.Assets.ContainsKey(targetPlatform))
                return Item(t.ModReference, t, f, SyncStatus.BuildNotFound,
                    $"{build.RepoFullName} {build.Tag} has no {targetPlatform} build, but SML requires the mod on both sides.");
            return Item(t.ModReference, t, f, SyncStatus.Install, $"{change} to match {truthName}.", build);
        }
    }

    /// <summary>Both sides have the mod: SML checks each side's RemoteVersionRange against the other side's version.</summary>
    public static bool IsCompatible(InstalledMod server, InstalledMod local, out string reason)
    {
        if (server.Descriptor is not { } sd || local.Descriptor is not { } ld)
        {
            reason = "A .uplugin couldn't be read, so the versions can't be compared.";
            return false;
        }
        var sv = sd.EffectiveVersion;
        var lv = ld.EffectiveVersion;
        bool serverAccepts = sd.EffectiveRemoteVersionRange.Matches(lv);
        bool clientAccepts = ld.EffectiveRemoteVersionRange.Matches(sv);
        if (serverAccepts && clientAccepts)
        {
            reason = sv == lv ? "Same version on both sides." : $"Different versions ({sv} on the server, {lv} on your game), but each side's RemoteVersionRange accepts the other.";
            return true;
        }
        reason = !serverAccepts
            ? $"The server's copy ({sv}) requires {sd.EffectiveRemoteVersionRange} from players; your game has {lv}."
            : $"Your game's copy ({lv}) requires {ld.EffectiveRemoteVersionRange} from the server; the server has {sv}.";
        return false;
    }

    /// <summary>Finds the exact release a copy came from (repo and tag in its marker), else the same version.</summary>
    private static ModBuild? FindBuild(DiscoveryResult discovery, InstalledMod source)
    {
        var mod = discovery.Mods.FirstOrDefault(m => m.ModReference.Equals(source.ModReference, StringComparison.OrdinalIgnoreCase));
        if (mod is null) return null;
        var marker = source.Marker;
        if (marker?.SourceRepo is { } repo && marker.SourceTag is { } tag)
        {
            var exact = mod.Builds.FirstOrDefault(b => !b.IsImported && b.RepoFullName.Equals(repo, StringComparison.OrdinalIgnoreCase) && b.Tag == tag);
            if (exact is not null) return exact;
        }
        else if (marker is { SourceRepo: null, SourceTag: { } importedTag })
        {
            // Installed from an imported zip: prefer the imported copy of that zip.
            var imported = mod.Builds.FirstOrDefault(b => b.IsImported && b.Tag == importedTag);
            if (imported is not null) return imported;
        }
        var version = source.Descriptor?.EffectiveVersion;
        return version is null ? null : mod.Builds.FirstOrDefault(b => b.Version == version);
    }

    private static Dictionary<string, InstalledMod> Unique(IReadOnlyList<InstalledMod> mods) =>
        mods.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static string? Version(InstalledMod m) => m.Descriptor?.EffectiveVersion.ToString();

    private static string OwnerText(ModOwner owner) => owner switch
    {
        ModOwner.Smm => "managed by SMM",
        ModOwner.Sdmm => "an SDMM dev build",
        _ => "installed outside SDMM",
    };
}
