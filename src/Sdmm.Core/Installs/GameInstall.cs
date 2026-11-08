using System.Text.Json;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Installs;

/// <summary>One Satisfactory install (client or dedicated server) that mods can be installed into.</summary>
public sealed class GameInstall
{
    /// <summary>
    /// Version files that identify the platform, in the order ficsit-cli checks them (cli/platforms.go).
    /// The first one that exists wins.
    /// </summary>
    public static readonly IReadOnlyList<(string VersionPath, ModTarget Target)> PlatformVersionFiles =
    [
        ("Engine/Binaries/Linux/UnrealServer-Linux-Shipping.version", ModTarget.LinuxServer),
        ("Engine/Binaries/Win64/UnrealServer-Win64-Shipping.version", ModTarget.WindowsServer),
        ("Engine/Binaries/Win64/FactoryGame-Win64-Shipping.version", ModTarget.Windows),
        ("Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version", ModTarget.LinuxServer),
        ("Engine/Binaries/Win64/FactoryServer-Win64-Shipping.version", ModTarget.WindowsServer),
        ("Engine/Binaries/Win64/FactoryGameSteam-Win64-Shipping.version", ModTarget.Windows),
        ("Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version", ModTarget.Windows),
    ];

    public string Name { get; }
    public IInstallFileSystem FileSystem { get; }

    /// <summary>Local root folder, or null for remote installs.</summary>
    public string? LocalRoot => (FileSystem as LocalInstallFileSystem)?.Root;

    /// <summary>
    /// Platform to assume when no version file is reachable. Some rented hosts only expose the FactoryGame
    /// folder, not Engine/. When this is used, the game build is unknown, so GameVersion checks are skipped
    /// (as SML does for changelist 0).
    /// </summary>
    public ModTarget? TargetOverride { get; }

    public GameInstall(string name, IInstallFileSystem fileSystem, ModTarget? targetOverride = null)
    {
        Name = name;
        FileSystem = fileSystem;
        TargetOverride = targetOverride;
    }

    public static GameInstall Local(string name, string root) => new(name, new LocalInstallFileSystem(root));

    /// <summary>Detects the platform and game build. Throws if this folder is not a Satisfactory install.</summary>
    public GameInfo Detect()
    {
        var detected = TryDetect();
        if (detected is not null) return detected;
        if (TargetOverride is { } t && FileSystem.DirectoryExists(InstallPaths.ModsDir))
            return new GameInfo(t, 0, null, "(platform set manually)");
        if (TryDetectFromBinaries() is { } fromBinaries) return fromBinaries;

        if (FileSystem.DirectoryExists("FactoryGame"))
            throw new InvalidOperationException(
                $"'{FileSystem.DisplayRoot}' has a FactoryGame folder, but SDMM can't tell the platform (no Engine folder or server program visible). " +
                "Set Platform (LinuxServer or WindowsServer) in the server's settings.");
        var seen = FileSystem.ListDirectories("");
        throw new InvalidOperationException(
            $"'{FileSystem.DisplayRoot}' does not look like a Satisfactory install: there's no FactoryGame folder with the game or server in it" +
            (TargetOverride is null ? "" : $", and no {InstallPaths.ModsDir} folder for the manual platform setting") + ". " +
            (seen.Count == 0 ? "That folder is empty or couldn't be listed."
                : $"That folder contains: {string.Join(", ", seen.Take(12))}{(seen.Count > 12 ? $" and {seen.Count - 12} more" : "")}."));
    }

    /// <summary>
    /// Fallback for hosts that hide the Engine folder (only FactoryGame is visible): the platform follows from the
    /// server or game executable in FactoryGame/Binaries. The game build is then unknown (0), as with a manual platform.
    /// </summary>
    private GameInfo? TryDetectFromBinaries()
    {
        static bool Has(IReadOnlyList<string> files, string prefix) => files.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var linux = FileSystem.ListFiles("FactoryGame/Binaries/Linux");
        if (Has(linux, "FactoryServer")) return new GameInfo(ModTarget.LinuxServer, 0, null, "FactoryGame/Binaries/Linux (no Engine folder)");
        var win = FileSystem.ListFiles("FactoryGame/Binaries/Win64");
        if (Has(win, "FactoryServer")) return new GameInfo(ModTarget.WindowsServer, 0, null, "FactoryGame/Binaries/Win64 (no Engine folder)");
        if (Has(win, "FactoryGame")) return new GameInfo(ModTarget.Windows, 0, null, "FactoryGame/Binaries/Win64 (no Engine folder)");
        return null;
    }

    private GameInfo? TryDetect()
    {
        foreach (var (path, target) in PlatformVersionFiles)
        {
            if (!FileSystem.FileExists(path)) continue;
            long changelist = 0;
            string? gameVersion = null;
            try
            {
                using var doc = JsonDocument.Parse(FileSystem.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("Changelist", out var cl) && cl.TryGetInt64(out var c)) changelist = c;
                if (doc.RootElement.TryGetProperty("GameVersion", out var gv) && gv.ValueKind == JsonValueKind.String) gameVersion = gv.GetString();
            }
            catch (JsonException)
            {
                // A broken version file still identifies the platform; the build number is just unknown.
            }
            return new GameInfo(target, changelist, gameVersion, path);
        }
        return null;
    }
}

/// <param name="Changelist">Game build (CL), 0 when unknown. SML skips GameVersion checks when it is 0.</param>
public sealed record GameInfo(ModTarget Target, long Changelist, string? GameVersion, string VersionFile);
