using System.Text;
using Sdmm.Core.Cache;
using Sdmm.Core.Config;
using Sdmm.Core.Diagnostics;
using Sdmm.Core.Discovery;
using Sdmm.Core.GitHub;
using Sdmm.Core.Installing;
using Sdmm.Core.Installs;
using Sdmm.Core.Modpacks;
using Sdmm.Core.Packaging;
using Sdmm.Core.Security;
using Sdmm.Core.Smm;
using Sdmm.Core.Versioning;
using Sdmm.Remote;

namespace Sdmm.Cli;

/// <summary>Exit codes, documented in the help text so scripts can rely on them.</summary>
internal static class Exit
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Stopped = 3;
    public const int ServerLocked = 4;
}

/// <summary>An expected failure: printed as is (no stack trace) with its exit code.</summary>
internal sealed class CliException(string message, int code = Exit.Error) : Exception(message)
{
    public int Code { get; } = code;
}

public static class Program
{
    public const string ExeName = "SatisfactoryDevModManager-cli";

    public const string Help = """
        Satisfactory Dev Mod Manager, command line. Manages dev builds from your GitHub sources and imported zips,
        using the same settings, download cache and logs as the app.

        Usage: SatisfactoryDevModManager-cli <command> [options]

        Commands:
          installs                       List your games and servers.
          mods                           List dev mods for a game or server: installed version, newest build, status.
          install <mod> [<version>]      Install a mod's newest build (or that version), or update it.
          remove <mod>                   Remove a dev build SDMM installed.
          update                         Update every installed dev build that has a newer build.
          match                          Make a game and a server match, so SML lets you join.
                                           --game <name> --server <name>
                                           --change game|server   the side to change (default: game)
          modpack <name or file>         Apply a saved modpack (by name) or a .modpack.json file.
                                           --remove-others   also remove dev builds that aren't in the pack
                                           --newest          newest builds instead of the pack's versions

        Options:
          --on <name>                    The game or server to work on, by name or by number from 'installs'.
                                         Default: the game last picked in the app.
          --yes, -y                      Don't ask; answer yes to confirmations.
          --accept-new-host-key          With --yes: trust a server key seen for the first time (otherwise it stops).
          --allow-flagged                With --yes: install files VirusTotal flags (otherwise it stops).
          --prereleases | --no-prereleases   Include prerelease builds or not (default: as set in the app).
          --config <path>                Use another settings file (as the app's --config).
          --version                      Print the version.
          --help, -h                     Print this help.

        Exit codes: 0 done, 1 error, 2 bad arguments, 3 stopped (declined or refused),
                    4 the server's files are locked (it's probably running: stop it, then try again).

        The game must be closed to change its mods. Restart a server after changing its mods.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Args a;
        try
        {
            a = Args.Parse(args);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine($"Run '{ExeName} --help' for usage.");
            return ex.Code;
        }
        if (a.ShowVersion)
        {
            Console.WriteLine($"{ExeName} {Installer.ToolVersion}");
            return Exit.Ok;
        }
        if (a.Help || a.Command is null)
        {
            Console.Write(Help.Replace("\n", Environment.NewLine) + Environment.NewLine);
            return a.Help ? Exit.Ok : Exit.Usage;
        }

        var configPath = Path.GetFullPath(a.Config ?? AppConfig.DefaultPath);
        var folder = Path.GetDirectoryName(configPath)!;
        FileLog.Configure(folder, detailed: false, version: Installer.ToolVersion);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) FileLog.WriteCrash("Unhandled error in the command line tool", ex);
        };
        var cfg = AppConfig.Load(configPath);
        FileLog.ShowLoginNames = cfg.LogLoginNames;
        FileLog.Configure(folder, cfg.VerboseLogging, Installer.ToolVersion);
        FileLog.Write("cli", $"{ExeName} {Installer.ToolVersion}: {string.Join(' ', args)}");

        try
        {
            return await new Cli(cfg, configPath, a).RunAsync();
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            FileLog.Write("cli", "Stopped: " + ex.Message);
            return ex.Code;
        }
        catch (Exception ex) when (RemoteFailure.FindRefusal(ex) is { } refusal)
        {
            Console.Error.WriteLine("The server refused the change; it's probably running. Hosted servers lock their files while the game " +
                                    "server runs: stop it in your host's panel, wait until it shows as stopped, then try again.");
            Console.Error.WriteLine($"(The server said: {refusal.Message})");
            FileLog.WriteError("cli", "Server refused", ex);
            return Exit.ServerLocked;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            FileLog.WriteError("cli", a.Command, ex);
            return Exit.Error;
        }
        finally
        {
            FileLog.Close();
        }
    }
}

/// <summary>Parsed command line: the command, its positional arguments and the options.</summary>
internal sealed class Args
{
    public string? Command { get; private set; }
    public List<string> Positional { get; } = [];
    public string? On { get; private set; }
    public string? Game { get; private set; }
    public string? Server { get; private set; }
    public string? Change { get; private set; }
    public string? Config { get; private set; }
    public bool Yes { get; private set; }
    public bool AcceptNewHostKey { get; private set; }
    public bool AllowFlagged { get; private set; }
    public bool RemoveOthers { get; private set; }
    public bool Newest { get; private set; }
    public bool? Prereleases { get; private set; }
    public bool Help { get; private set; }
    public bool ShowVersion { get; private set; }

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length && !args[i + 1].StartsWith("--")
                ? args[++i]
                : throw new CliException($"{args[i]} needs a value.", Exit.Usage);
            switch (args[i])
            {
                case "--on": a.On = Value(); break;
                case "--game": a.Game = Value(); break;
                case "--server": a.Server = Value(); break;
                case "--change": a.Change = Value().ToLowerInvariant(); break;
                case "--config": a.Config = Value(); break;
                case "--yes" or "-y": a.Yes = true; break;
                case "--accept-new-host-key": a.AcceptNewHostKey = true; break;
                case "--allow-flagged": a.AllowFlagged = true; break;
                case "--remove-others": a.RemoveOthers = true; break;
                case "--newest": a.Newest = true; break;
                case "--prereleases": a.Prereleases = true; break;
                case "--no-prereleases": a.Prereleases = false; break;
                case "--help" or "-h" or "/?": a.Help = true; break;
                case "--version": a.ShowVersion = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new CliException($"Unknown option '{args[i]}'.", Exit.Usage);
                    if (a.Command is null) a.Command = args[i].ToLowerInvariant();
                    else a.Positional.Add(args[i]);
                    break;
            }
        }
        if (a.Change is not (null or "game" or "server")) throw new CliException("--change must be 'game' or 'server'.", Exit.Usage);
        return a;
    }
}

/// <summary>Writes progress at once, in order (Progress&lt;T&gt; would post it to the thread pool).</summary>
internal sealed class ConsoleProgress(Action<string> write) : IProgress<string>
{
    public void Report(string value) => write(value);
}

/// <summary>The commands. Everything here is dev builds only: sources' GitHub releases and imported zips.</summary>
internal sealed class Cli(AppConfig cfg, string configPath, Args a)
{
    private const int KeepBuildsPerAsset = 2;
    private IReadOnlyList<DiscoveredMod>? _mods;

    private string CacheDir => cfg.ResolveCacheDirectory();
    private bool IncludePrereleases => a.Prereleases ?? cfg.IncludePrereleases;

    public async Task<int> RunAsync() => a.Command switch
    {
        "installs" => ListInstalls(),
        "mods" => await ListModsAsync(),
        "install" => await InstallAsync(),
        "remove" => await RemoveAsync(),
        "update" => await UpdateAsync(),
        "match" => await MatchAsync(),
        "modpack" => await ApplyModpackAsync(),
        _ => throw new CliException($"Unknown command '{a.Command}'.", Exit.Usage),
    };

    // ---- output, prompts and logging -------------------------------------------------------------------------

    private static void Say(string line = "")
    {
        Console.WriteLine(line);
        if (line.Length > 0) FileLog.Write("cli", line);
    }

    /// <summary>Asks yes/no. With --yes it's yes; without a console to ask on (redirected input) it's no.</summary>
    private bool Confirm(string question)
    {
        if (a.Yes)
        {
            Say(question + " yes (--yes)");
            return true;
        }
        if (Console.IsInputRedirected)
        {
            Say(question + " no (no console to ask on; pass --yes to answer yes)");
            return false;
        }
        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        bool yes = answer is "y" or "yes";
        FileLog.Write("cli", $"{question} {(yes ? "yes" : "no")}");
        return yes;
    }

    // ---- games and servers -------------------------------------------------------------------------------------

    private static bool IsServer(InstallConfig c)
    {
        if (c.IsRemote) return true;
        try
        {
            return GameInstall.Local("", c.Path).Detect().Target != ModTarget.Windows;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string KindText(InstallConfig c) => c.Kind switch
    {
        InstallKind.Local => IsServer(c) ? "Server on this PC" : "Game",
        InstallKind.Sftp => "Hosted server (SFTP)",
        _ => c.UseFtps ? "Hosted server (FTPS)" : "Hosted server (FTP)",
    };

    /// <summary>The address without the login name (output gets pasted into bug reports).</summary>
    private static string Where(InstallConfig c) => c.Kind switch
    {
        InstallKind.Local => c.Path,
        InstallKind.Sftp => $"sftp://{c.Host}{(c.Port is { } p and not 22 ? $":{p}" : "")}{c.Path}",
        _ => $"{(c.UseFtps ? "ftps" : "ftp")}://{c.Host}{(c.Port is { } p and not 21 ? $":{p}" : "")}{c.Path}",
    };

    private int ListInstalls()
    {
        if (cfg.Installs.Count == 0)
        {
            Say("No games or servers yet. Add them in the app (Settings > Games & servers).");
            return Exit.Ok;
        }
        Table(["#", "Name", "Type", "Location"], cfg.Installs.Select((c, i) => new[]
        {
            (i + 1).ToString(),
            c.Name + (c.Key == cfg.LastGameInstall ? " (default)" : ""),
            KindText(c),
            Where(c),
        }).ToList());
        return Exit.Ok;
    }

    /// <summary>A game or server by name or number. With no name: the game last picked in the app (or the server, for a server role).</summary>
    private InstallConfig Pick(string? name, bool server = false)
    {
        if (name is null)
        {
            var key = server ? cfg.LastServerInstall : cfg.LastGameInstall;
            return cfg.Installs.FirstOrDefault(c => c.Key == key)
                   ?? throw new CliException($"Say which {(server ? "server" : "game")} with {(server ? "--server" : "--on")} <name> (see 'installs').", Exit.Usage);
        }
        if (int.TryParse(name, out var n) && n >= 1 && n <= cfg.Installs.Count) return cfg.Installs[n - 1];
        return cfg.Installs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new CliException($"No game or server called '{name}'. Known: {string.Join(", ", cfg.Installs.Select(c => $"'{c.Name}'"))}.", Exit.Usage);
    }

    private OpenedInstall Open(InstallConfig c) =>
        InstallFactory.Open(c, SecretProtector.Unprotect,
            fingerprint =>
            {
                Say($"First connection to '{c.Name}'. Server key fingerprint: {fingerprint}");
                if (a.AcceptNewHostKey) return true;
                if (a.Yes || Console.IsInputRedirected)
                {
                    Say("Not trusted automatically. Compare it with the one your host shows, then run again with --accept-new-host-key (or connect once in the app).");
                    return false;
                }
                return Confirm("Trust this key? It's saved, and a different key later is refused.");
            },
            fingerprint =>
            {
                c.TrustedFingerprint = fingerprint;
                cfg.Save(configPath);
                Say($"Saved the server key for '{c.Name}'.");
            });

    /// <summary>Hosted servers lock their files while running; stop before changing anything, with the reason.</summary>
    private static void EnsureWritable(InstallConfig c, OpenedInstall opened)
    {
        if (!c.IsRemote || ModsFolderAccess.Check(opened.Install.FileSystem) is not { } error) return;
        throw new CliException(
            $"The Mods folder on '{c.Name}' can't be changed right now; the server is probably running. Stop it in your host's panel, " +
            $"wait until it shows as stopped, then try again. (The server said: {error})", Exit.ServerLocked);
    }

    private (GameInfo Info, IReadOnlyList<InstalledMod> Mods) Read(InstallConfig c)
    {
        using var opened = Open(c);
        return (opened.Install.Detect(), ModScanner.Scan(opened.Install.FileSystem));
    }

    // ---- builds --------------------------------------------------------------------------------------------------

    /// <summary>Your sources' mods (checked on GitHub) plus imported zips, read once per run.</summary>
    private async Task<IReadOnlyList<DiscoveredMod>> ModsAsync()
    {
        if (_mods is not null) return _mods;
        if (cfg.Sources.Users.Count + cfg.Sources.Orgs.Count + cfg.Sources.Repos.Count == 0)
            Say("No mod sources yet; add them in the app (Settings > Mod sources). Only imported zips are known.");
        IReadOnlyList<DiscoveredMod> github = [];
        if (cfg.Sources.Users.Count + cfg.Sources.Orgs.Count + cfg.Sources.Repos.Count > 0)
        {
            Say("Checking GitHub for builds...");
            using var gh = CreateGitHub();
            var found = await new ModDiscovery(gh).DiscoverAsync(cfg.Sources);
            foreach (var e in found.Errors) Say("GitHub: " + e);
            github = found.Mods;
        }
        IReadOnlyList<ModBuild> imported;
        try
        {
            imported = new ImportedZips(Path.Combine(CacheDir, "imported")).List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidModPackageException)
        {
            Say("Imported zips couldn't be read: " + ex.Message);
            imported = [];
        }
        return _mods = ImportedZips.Merge(github, imported);
    }

    private GitHubClient CreateGitHub() => new(SecretProtector.Unprotect(cfg.ProtectedReadToken), new FileEtagCache(Path.Combine(CacheDir, "http")));

    /// <summary>A mod's builds for a platform, newest first, following the prerelease setting.</summary>
    private List<ModBuild> BuildsFor(DiscoveredMod mod, ModTarget target) =>
        mod.Builds.Where(b => b.Version is not null && b.Assets.ContainsKey(target) && (IncludePrereleases || !b.Prerelease)).ToList();

    private static bool IsDevBuild(InstalledMod m) =>
        m.Owner == ModOwner.Sdmm && m.Marker?.SourceRepo?.StartsWith(ServerSync.SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>
    /// Downloads (or reuses) the builds, runs the VirusTotal check when it's on, plans with the usual safety checks and
    /// installs. Returns false when stopped (declined, refused or flagged); errors throw.
    /// </summary>
    private async Task<bool> InstallBuildsAsync(InstallConfig c, IReadOnlyList<ModBuild> builds, bool restartNote = true)
    {
        using var opened = Open(c);
        var info = opened.Install.Detect();
        EnsureWritable(c, opened);
        var cache = new PackageCache(Path.Combine(CacheDir, "packages"));
        var requests = new List<InstallRequest>();
        using (var gh = CreateGitHub())
        {
            foreach (var b in builds)
            {
                if (!b.Assets.TryGetValue(info.Target, out var asset))
                    throw new CliException($"{b.ModReference} {b.Tag} has no {info.Target} build.");
                if (b.LocalZips?.TryGetValue(info.Target, out var localZip) == true)
                {
                    Say($"{b.ModReference}: using imported {asset.Name} {b.Tag}");
                    requests.Add(new InstallRequest(ModPackage.Open(localZip), new PackageSource(null, b.Tag, asset.Name)));
                    continue;
                }
                Say($"{b.ModReference}: getting {asset.Name} ({asset.Size / 1048576.0:0.0} MB) from {b.RepoFullName} {b.Tag}");
                requests.Add(new InstallRequest(await cache.GetAsync(gh, b, info.Target), new PackageSource(b.RepoFullName, b.Tag, asset.Name)));
            }
        }
        if (!await VirusTotalAsync(requests)) return false;

        var smm = c.IsRemote ? null : SmmState.Load(cfg.SmmDirectory);
        var plan = new InstallPlanner().Plan(opened.Install, requests, smm);
        foreach (var w in plan.Issues.Where(i => i.Level == IssueLevel.Warning)) Say("Note: " + w.Message);
        if (plan.IsBlocked)
        {
            foreach (var reason in plan.Issues.Where(i => i.Level == IssueLevel.Blocked).Select(i => i.Message).Distinct()) Say("Refused: " + reason);
            return false;
        }
        var confirmed = new HashSet<string>();
        if (plan.NeedingConfirmation.Count > 0)
        {
            foreach (var m in plan.NeedingConfirmation.Select(i => i.Message).Distinct()) Say("Problem: " + m);
            if (!Confirm("Install anyway?")) return false;
            foreach (var i in plan.NeedingConfirmation) confirmed.Add(i.Code);
        }
        var installer = new Installer(line => Say("  " + line));
        installer.CleanupStaging(opened.Install);
        var result = installer.Execute(plan, confirmed);
        Say($"Done: installed {result.Installed.Count}, already up to date {result.Skipped.Count}.");
        FollowModpack(c, requests.Select(r => r.Package.ModReference).ToList(), []);
        cache.Prune(KeepBuildsPerAsset);
        if (restartNote && IsServer(c) && result.Installed.Count > 0) Say(RestartNote(c));
        return true;
    }

    /// <summary>
    /// Like the app: the modpack this game or server follows (if any) takes in the mods this command installed or
    /// removed, so the app doesn't report them as changes made elsewhere.
    /// </summary>
    private void FollowModpack(InstallConfig c, IReadOnlyCollection<string> changed, IReadOnlyCollection<string> removed)
    {
        if (changed.Count == 0 || c.ActiveModpack is not { } name ||
            cfg.Modpacks.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } pack)
            return;
        var (_, installed) = Read(c);
        if (!ModpackIO.Follow(pack, installed, IsServer(c), ServerSync.SmrSourcePrefix, changed, removed, DateTimeOffset.UtcNow)) return;
        cfg.Save(configPath);
        Say($"Updated the modpack '{pack.Name}' that '{c.Name}' follows.");
    }

    private static string RestartNote(InstallConfig c) => c.IsRemote
        ? "Start (or restart) the server from your host's panel so it loads the change."
        : "Restart the server so it loads the change.";

    /// <summary>The app's optional VirusTotal check (Settings > Security), for the files about to be installed.</summary>
    private async Task<bool> VirusTotalAsync(IReadOnlyList<InstallRequest> requests)
    {
        if (!cfg.VirusTotalEnabled || SecretProtector.Unprotect(cfg.ProtectedVirusTotalKey) is not { } key) return true;
        var toCheck = requests.Where(VirusTotalCheck.NeedsCheck).ToList();
        if (toCheck.Count == 0) return true;
        using var vt = new VirusTotalClient(key);
        var check = new VirusTotalCheck(vt, Path.Combine(CacheDir, "virustotal-checked.json"));
        var files = toCheck.Select(r => new VtFile($"{r.Package.ModReference} {r.Package.Descriptor.EffectiveVersion}", r.Package.ZipPath, r.Package.Sha256)).ToList();
        var results = await check.CheckAsync(files, cfg.VirusTotalUpload, new ConsoleProgress(s => Say(s)));
        foreach (var r in results.Where(r => r.Outcome != VtOutcome.AlreadyChecked))
            Say(r.Outcome switch
            {
                VtOutcome.Clean => $"VirusTotal: {r.File.Name} is clean ({r.Stats}).",
                VtOutcome.Flagged => $"VirusTotal: {r.File.Name} is FLAGGED ({r.Stats}). Report: {r.ReportUrl}",
                _ => $"VirusTotal: {r.File.Name} not checked: {r.Note}.",
            });
        var flagged = results.Where(r => r.Outcome == VtOutcome.Flagged).ToList();
        if (flagged.Count == 0) return true;
        bool go = a.AllowFlagged || (!a.Yes && Confirm("VirusTotal flags the file(s) above. Install anyway?"));
        if (!go)
        {
            if (a.Yes && !a.AllowFlagged) Say("Stopped: VirusTotal flags a file. Check the report; pass --allow-flagged to install anyway.");
            return false;
        }
        check.MarkAccepted(flagged.Select(r => r.File.Sha256));
        return true;
    }

    // ---- commands -------------------------------------------------------------------------------------------------

    private async Task<int> ListModsAsync()
    {
        var c = Pick(a.On);
        var (info, installed) = Read(c);
        var mods = await ModsAsync();
        var byRef = installed.GroupBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<string[]>();
        foreach (var mod in mods.OrderBy(m => m.ModReference, StringComparer.OrdinalIgnoreCase))
        {
            var newest = BuildsFor(mod, info.Target).FirstOrDefault();
            if (newest is null) continue;
            byRef.TryGetValue(mod.ModReference, out var inst);
            rows.Add([mod.ModReference, inst?.Descriptor?.EffectiveVersion.ToString() ?? "-", newest.Tag, StatusOf(inst, newest),
                      newest.IsImported ? "imported zip" : newest.RepoFullName]);
        }
        // Dev builds installed here whose mod isn't in the sources any more.
        foreach (var inst in installed.Where(m => IsDevBuild(m) && !rows.Any(r => r[0].Equals(m.ModReference, StringComparison.OrdinalIgnoreCase))))
            rows.Add([inst.ModReference, inst.Descriptor?.EffectiveVersion.ToString() ?? "?", "-", "Not in your sources", inst.Marker?.SourceRepo ?? (inst.Marker?.AdoptedFrom is null ? "imported zip" : "-")]);
        Say($"{c.Name} ({info.Target}, {installed.Count} mods installed in total):");
        if (rows.Count == 0) Say("No dev mods from your sources for this platform.");
        else Table(["Mod", "Installed", "Newest", "Status", "Source"], rows);

        var problems = ModHealth.Check(installed, info.Changelist, c.IsRemote ? null : SmmState.Load(cfg.SmmDirectory), c.IsRemote ? null : c.Path);
        var listed = rows.Select(r => r[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (modRef, list) in problems.Where(p => listed.Contains(p.Key)))
            foreach (var p in list) Say($"Problem: {p.Message}");
        return Exit.Ok;
    }

    private static string StatusOf(InstalledMod? inst, ModBuild newest) => inst switch
    {
        null => "Not installed",
        { Owner: ModOwner.Smm } => "Installed by SMM",
        { Owner: ModOwner.Unmanaged } => "Installed by hand or the host",
        { Descriptor: null } => "Broken copy",
        _ when newest.Version! > inst.Descriptor!.EffectiveVersion => "Update available",
        _ when newest.Version == inst.Descriptor!.EffectiveVersion => "Up to date",
        _ => "Other version",
    };

    private async Task<int> InstallAsync()
    {
        if (a.Positional.Count is 0 or > 2) throw new CliException("Usage: install <mod> [<version>]", Exit.Usage);
        var c = Pick(a.On);
        var (info, _) = Read(c);
        var mod = (await ModsAsync()).FirstOrDefault(m => m.ModReference.Equals(a.Positional[0], StringComparison.OrdinalIgnoreCase))
                  ?? throw new CliException($"'{a.Positional[0]}' isn't among your sources' mods or imported zips. Run 'mods' to see them.", Exit.Usage);
        var builds = BuildsFor(mod, info.Target);
        ModBuild? build;
        if (a.Positional.Count == 2)
        {
            var wanted = a.Positional[1].TrimStart('v', 'V');
            build = mod.Builds.FirstOrDefault(b => b.Assets.ContainsKey(info.Target) &&
                                                   (b.Tag.TrimStart('v', 'V').Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                                                    (SemVersion.TryParse(wanted, out var v) && b.Version == v)))
                    ?? throw new CliException($"{mod.ModReference} has no {info.Target} build '{a.Positional[1]}'. Builds: {string.Join(", ", builds.Select(b => b.Tag))}", Exit.Usage);
        }
        else build = builds.FirstOrDefault() ?? throw new CliException($"{mod.ModReference} has no {info.Target} build.", Exit.Stopped);

        if (IsServer(c) && !Confirm($"Install {build.ModReference} {build.Tag} on the server '{c.Name}'? Everyone joining must then have exactly this version."))
            return Exit.Stopped;
        return await InstallBuildsAsync(c, [build]) ? Exit.Ok : Exit.Stopped;
    }

    private async Task<int> RemoveAsync()
    {
        if (a.Positional.Count != 1) throw new CliException("Usage: remove <mod>", Exit.Usage);
        var c = Pick(a.On);
        using var opened = Open(c);
        var installed = ModScanner.Scan(opened.Install.FileSystem);
        var mod = installed.FirstOrDefault(m => m.ModReference.Equals(a.Positional[0], StringComparison.OrdinalIgnoreCase))
                  ?? throw new CliException($"{a.Positional[0]} isn't installed on '{c.Name}'.", Exit.Stopped);
        if (mod.Owner != ModOwner.Sdmm)
            throw new CliException($"{mod.ModReference} wasn't installed by SDMM ({(mod.Owner == ModOwner.Smm ? "SMM manages it" : "it was installed by hand or by the host")}), so it's left alone.", Exit.Stopped);
        var dependents = installed.Where(m => m.Descriptor is not null && !m.ModReference.Equals(mod.ModReference, StringComparison.OrdinalIgnoreCase) &&
                                              m.Descriptor.Plugins.Any(d => !d.Optional && d.Enabled && d.Name.Equals(mod.ModReference, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.ModReference).ToList();
        if (dependents.Count > 0) Say($"Warning: {string.Join(", ", dependents)} need{(dependents.Count == 1 ? "s" : "")} {mod.ModReference}; the game won't load {(dependents.Count == 1 ? "it" : "them")} without it.");
        if (!Confirm($"Remove {mod.ModReference} {mod.Descriptor?.EffectiveVersion} from '{c.Name}'? The build stays in the download cache.")) return Exit.Stopped;
        EnsureWritable(c, opened);
        new Installer(line => Say("  " + line)).Uninstall(opened.Install, mod.ModReference);
        Say($"Removed {mod.ModReference}.");
        FollowModpack(c, [mod.ModReference], [mod.ModReference]);
        if (IsServer(c)) Say(RestartNote(c));
        return Exit.Ok;
    }

    private async Task<int> UpdateAsync()
    {
        var c = Pick(a.On);
        var (info, installed) = Read(c);
        var mods = (await ModsAsync()).ToDictionary(m => m.ModReference, StringComparer.OrdinalIgnoreCase);
        var updates = new List<(InstalledMod Have, ModBuild Newest)>();
        foreach (var inst in installed.Where(m => IsDevBuild(m) && m.Descriptor is not null))
            if (mods.TryGetValue(inst.ModReference, out var mod) && BuildsFor(mod, info.Target).FirstOrDefault() is { } newest &&
                newest.Version! > inst.Descriptor!.EffectiveVersion)
                updates.Add((inst, newest));
        if (updates.Count == 0)
        {
            Say($"Every dev build on '{c.Name}' is up to date.");
            return Exit.Ok;
        }
        foreach (var (have, newest) in updates) Say($"  {have.ModReference}: {have.Descriptor!.EffectiveVersion} -> {newest.Tag}");
        if (!Confirm($"Update {updates.Count} dev build{(updates.Count == 1 ? "" : "s")} on '{c.Name}'?")) return Exit.Stopped;
        return await InstallBuildsAsync(c, updates.Select(u => u.Newest).ToList()) ? Exit.Ok : Exit.Stopped;
    }

    private async Task<int> MatchAsync()
    {
        var game = Pick(a.Game);
        var server = Pick(a.Server, server: true);
        if (game == server) throw new CliException("--game and --server are the same install.", Exit.Usage);
        bool changeServer = a.Change == "server";
        var direction = changeServer ? SyncDirection.ServerMatchesGame : SyncDirection.GameMatchesServer;
        var (serverInfo, serverMods) = Read(server);
        var (gameInfo, gameMods) = Read(game);
        var items = ServerSync.Compare(serverMods, gameMods, new DiscoveryResult(await ModsAsync(), [], []),
            changeServer ? serverInfo.Target : gameInfo.Target, direction);
        var target = changeServer ? server : game;
        var other = changeServer ? game : server;

        Say($"Comparing '{game.Name}' (game) with '{server.Name}' (server); changing {(changeServer ? "the server" : "the game")}:");
        Table(["Mod", "Server", "Game", "Status", "Details"], items
            .OrderBy(i => i.Status is SyncStatus.Install or SyncStatus.Remove ? 0 : i.Status == SyncStatus.InSync ? 2 : 1)
            .ThenBy(i => i.ModReference, StringComparer.OrdinalIgnoreCase)
            .Select(i => new[] { i.ModReference, i.ServerVersion ?? "-", i.LocalVersion ?? "-", StatusText(i.Status), i.Message }).ToList(), wrapLast: true);

        var installs = items.Where(i => i.Status == SyncStatus.Install && i.Build is not null).Select(i => i.Build!).ToList();
        var removes = items.Where(i => i.Status == SyncStatus.Remove).Select(i => i.ModReference).ToList();
        int byHand = items.Count(i => i.Status is SyncStatus.BuildNotFound or SyncStatus.ManagedElsewhere or SyncStatus.FromSmr);
        if (installs.Count + removes.Count == 0)
        {
            Say(byHand == 0 ? "They match; SML should let you join." : $"Nothing SDMM can change; {byHand} line(s) need fixing by hand (see Details).");
            return byHand == 0 ? Exit.Ok : Exit.Stopped;
        }
        var plan = new List<string>();
        if (installs.Count > 0) plan.Add("install " + string.Join(", ", installs.Select(b => $"{b.ModReference} {b.Tag}")));
        if (removes.Count > 0) plan.Add("remove " + string.Join(", ", removes));
        if (!Confirm($"Change '{target.Name}' to match '{other.Name}': {string.Join("; ", plan)}?")) return Exit.Stopped;

        if (installs.Count > 0 && !await InstallBuildsAsync(target, installs, restartNote: false)) return Exit.Stopped;
        if (removes.Count > 0)
        {
            using var opened = Open(target);
            EnsureWritable(target, opened);
            var installer = new Installer(line => Say("  " + line));
            foreach (var r in removes) installer.Uninstall(opened.Install, r);
            FollowModpack(target, removes, removes);
        }
        Say($"Finished: {installs.Count} installed, {removes.Count} removed." + (byHand > 0 ? $" {byHand} line(s) still need fixing by hand." : ""));
        if (IsServer(target)) Say(RestartNote(target));
        return byHand > 0 ? Exit.Stopped : Exit.Ok;
    }

    private static string StatusText(SyncStatus s) => s switch
    {
        SyncStatus.InSync => "In sync",
        SyncStatus.Install => "Install",
        SyncStatus.Remove => "Remove",
        SyncStatus.ServerOnly => "Server only",
        SyncStatus.ClientOnly => "Game only",
        SyncStatus.BuildNotFound => "Build not found",
        SyncStatus.FromSmr => "Install with SMM",
        _ => "Managed elsewhere",
    };

    private async Task<int> ApplyModpackAsync()
    {
        if (a.Positional.Count != 1) throw new CliException("Usage: modpack <name or .modpack.json file>", Exit.Usage);
        var arg = a.Positional[0];
        Modpack pack = File.Exists(arg)
            ? ModpackIO.Import(File.ReadAllText(arg), DateTimeOffset.Now)
            : cfg.Modpacks.FirstOrDefault(p => p.Name.Equals(arg, StringComparison.OrdinalIgnoreCase))
              ?? throw new CliException($"No modpack called '{arg}' (saved: {string.Join(", ", cfg.Modpacks.Select(p => $"'{p.Name}'"))}), and no such file.", Exit.Usage);
        var c = Pick(a.On);
        var (info, installed) = Read(c);
        var changes = ModpackPlanner.Plan(pack, installed, IsServer(c), a.RemoveOthers, a.Newest);
        var skipped = changes.Skipped.Select(x => $"{x.Mod} ({x.Why})").ToList();
        skipped.AddRange(changes.Install.Where(m => m.Source == ModpackSource.Smr).Select(m => $"{m.ModReference} (install it with SMM)"));
        var all = (await ModsAsync()).ToDictionary(m => m.ModReference, StringComparer.OrdinalIgnoreCase);
        var builds = new List<ModBuild>();
        var problems = new List<string>();
        foreach (var m in changes.Install.Where(m => m.Source != ModpackSource.Smr))
        {
            var candidates = (all.TryGetValue(m.ModReference, out var dm) ? dm.Builds : [])
                .Where(b => b.Version is not null && b.Assets.ContainsKey(info.Target) &&
                            (m.Source == ModpackSource.Imported ? b.IsImported : !b.IsImported && b.RepoFullName.Equals(m.Repo, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            ModBuild? build = !a.Newest && m.Version is { } wanted && SemVersion.TryParse(wanted, out var wv)
                ? candidates.FirstOrDefault(b => b.Version == wv)
                : candidates.Where(b => m.Constraint is null || (VersionRange.TryParse(m.Constraint, out var r, out _) && r!.Matches(b.Version!)))
                    .OrderByDescending(b => b.Version).FirstOrDefault();
            if (build is null) problems.Add($"{m.ModReference} {m.Version ?? m.Constraint ?? ""}: no {info.Target} build in your sources or imported zips.");
            else builds.Add(build);
        }
        foreach (var p in problems) Say("Problem: " + p);
        if (problems.Count > 0) return Exit.Stopped;
        foreach (var s in skipped) Say("Skipped: " + s);
        if (builds.Count + changes.Remove.Count == 0)
        {
            Say($"'{c.Name}' already matches '{pack.Name}'.");
            return Exit.Ok;
        }
        foreach (var b in builds) Say($"  install {b.ModReference} {b.Tag}");
        foreach (var r in changes.Remove) Say($"  remove {r}");
        if (!Confirm($"Apply '{pack.Name}' to '{c.Name}'?")) return Exit.Stopped;
        if (builds.Count > 0 && !await InstallBuildsAsync(c, builds, restartNote: false)) return Exit.Stopped;
        if (changes.Remove.Count > 0)
        {
            using var opened = Open(c);
            EnsureWritable(c, opened);
            var installer = new Installer(line => Say("  " + line));
            foreach (var r in changes.Remove) installer.Uninstall(opened.Install, r);
        }
        Say($"Applied '{pack.Name}': {builds.Count} installed or changed, {changes.Remove.Count} removed, {skipped.Count} skipped.");
        if (cfg.Modpacks.Contains(pack) && c.ActiveModpack != pack.Name)
        {
            // As in the app: a saved modpack that was applied is the one this game or server follows from now on.
            c.ActiveModpack = pack.Name;
            cfg.Save(configPath);
            Say($"'{c.Name}' now follows the modpack '{pack.Name}'.");
        }
        if (IsServer(c)) Say(RestartNote(c));
        return Exit.Ok;
    }

    // ---- tables ----------------------------------------------------------------------------------------------------

    /// <summary>Plain-text columns. With <paramref name="wrapLast"/>, the last column wraps to the console width.</summary>
    private static void Table(string[] header, IReadOnlyList<string[]> rows, bool wrapLast = false)
    {
        int cols = header.Length;
        var widths = Enumerable.Range(0, cols).Select(i => Math.Max(header[i].Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        int width = 120;
        try { if (!Console.IsOutputRedirected) width = Math.Max(60, Console.WindowWidth - 1); }
        catch (IOException) { }
        if (wrapLast) widths[cols - 1] = Math.Max(20, width - widths.Take(cols - 1).Sum() - 2 * (cols - 1));
        string Line(string[] cells)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cols; i++)
            {
                var text = cells[i];
                if (wrapLast && i == cols - 1) sb.Append(text);
                else sb.Append(i == cols - 1 ? text : text.PadRight(widths[i] + 2));
            }
            return sb.ToString().TrimEnd();
        }
        Say(Line(header));
        Say(Line(header.Select((h, i) => new string('-', wrapLast && i == cols - 1 ? Math.Min(widths[i], 40) : widths[i])).ToArray()));
        foreach (var r in rows)
        {
            if (!wrapLast || r[cols - 1].Length <= widths[cols - 1])
            {
                Say(Line(r));
                continue;
            }
            // Wrap the last column under itself.
            var words = r[cols - 1].Split(' ');
            var lines = new List<string>();
            var current = new StringBuilder();
            foreach (var w in words)
            {
                if (current.Length > 0 && current.Length + 1 + w.Length > widths[cols - 1]) { lines.Add(current.ToString()); current.Clear(); }
                if (current.Length > 0) current.Append(' ');
                current.Append(w);
            }
            if (current.Length > 0) lines.Add(current.ToString());
            var first = r.ToArray();
            first[cols - 1] = lines[0];
            Say(Line(first));
            var indent = new string(' ', widths.Take(cols - 1).Sum() + 2 * (cols - 1));
            foreach (var l in lines.Skip(1)) Say(indent + l);
        }
    }
}
