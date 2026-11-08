using Sdmm.Core.Installs;

namespace Sdmm.Core.Installing;

public sealed record InstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped);

/// <summary>
/// Executes an <see cref="InstallPlan"/>. Each mod is extracted to a staging folder, then swapped in with directory
/// renames. Old copies are kept until the whole batch succeeds, so a failure rolls every mod in the batch back.
/// </summary>
/// <remarks>
/// Staging lives in FactoryGame/.sdmm-staging, outside Mods: Unreal scans Mods recursively for .uplugin files,
/// so a staged copy inside Mods could load as a duplicate plugin if the game started meanwhile. It is on the same
/// volume as Mods, so the renames do not copy data.
/// </remarks>
public sealed class Installer
{
    public const string StagingDir = "FactoryGame/.sdmm-staging";
    public static string ToolVersion { get; } = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly Action<string> _log;
    private readonly TimeProvider _time;

    public Installer(Action<string>? log = null, TimeProvider? time = null)
    {
        _log = log ?? (_ => { });
        _time = time ?? TimeProvider.System;
    }

    public InstallResult Execute(InstallPlan plan, IReadOnlySet<string>? confirmedCodes = null)
    {
        confirmedCodes ??= new HashSet<string>();
        if (plan.IsBlocked)
            throw new InvalidOperationException("The install plan has blocking issues:\n" +
                string.Join("\n", plan.Issues.Where(i => i.Level == IssueLevel.Blocked).Select(i => i.Message)));
        if (!plan.CanExecute(confirmedCodes))
            throw new InvalidOperationException("The install plan has unconfirmed issues:\n" +
                string.Join("\n", plan.NeedingConfirmation.Where(i => !confirmedCodes.Contains(i.Code)).Select(i => i.Message)));

        var fs = plan.Install.FileSystem;
        var batchDir = InstallPaths.Combine(StagingDir, Guid.NewGuid().ToString("N"));
        var done = new List<Swap>();
        var installed = new List<string>();
        var skipped = new List<string>();

        try
        {
            foreach (var action in plan.Actions)
            {
                var modRef = action.Request.Package.ModReference;
                if (action.Kind == ActionKind.AlreadyInstalled)
                {
                    _log($"{modRef}: this exact build is already installed, skipping.");
                    skipped.Add(modRef);
                    continue;
                }

                // Re-check ownership right before touching anything, in case something changed since planning.
                EnsureOursOrAbsent(fs, action.Destination, modRef);
                if (action.StaleOtherRoot is not null) EnsureOursOrAbsent(fs, action.StaleOtherRoot.Folder, modRef);

                var staged = InstallPaths.Combine(batchDir, "new", modRef);
                _log($"{modRef}: extracting {Path.GetFileName(action.Request.Package.ZipPath)}");
                foreach (var (rel, open) in action.Request.Package.ExtractEntries())
                {
                    if (rel.Equals(SdmmMarker.FileName, StringComparison.OrdinalIgnoreCase) ||
                        rel.Equals(ModScanner.SmmMarkerFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // never let a zip plant an ownership marker
                    }
                    using var s = open();
                    fs.WriteFile(InstallPaths.Combine(staged, rel), s);
                }
                fs.WriteAllText(InstallPaths.Combine(staged, SdmmMarker.FileName), BuildMarker(action, plan.Game.Target).ToJson());

                var swap = new Swap(action.Destination, staged);
                if (fs.DirectoryExists(action.Destination))
                {
                    swap.OldDestination = InstallPaths.Combine(batchDir, "old", modRef);
                    fs.MoveDirectory(action.Destination, swap.OldDestination);
                }
                done.Add(swap); // registered before the move-in, so rollback restores the old copy if the move-in fails
                fs.MoveDirectory(staged, action.Destination);
                swap.MovedIn = true;

                if (action.StaleOtherRoot is { } stale && fs.DirectoryExists(stale.Folder))
                {
                    swap.StaleFolder = stale.Folder;
                    swap.StaleBackup = InstallPaths.Combine(batchDir, "old-other", modRef);
                    fs.MoveDirectory(stale.Folder, swap.StaleBackup);
                }

                _log($"{modRef}: installed {action.Request.Package.Descriptor.EffectiveVersion} to {action.Destination}");
                installed.Add(modRef);
            }
        }
        catch (Exception ex)
        {
            _log($"Install failed: {ex.Message}. Rolling back.");
            Rollback(fs, done);
            TryDelete(fs, batchDir);
            throw;
        }

        TryDelete(fs, batchDir);
        TryDeleteIfEmpty(fs, StagingDir);
        return new InstallResult(installed, skipped);
    }

    /// <summary>Removes a mod this tool installed. Refuses folders owned by SMM or without our marker.</summary>
    public void Uninstall(GameInstall install, string modReference, IGameProcessDetector? processes = null)
    {
        if (install.LocalRoot is { } root)
        {
            var running = (processes ?? new GameProcessDetector()).FindRunning(root);
            if (running.Count > 0) throw new InvalidOperationException($"Close the game first: {running[0]} is running from this install.");
        }
        var fs = install.FileSystem;
        // Only this mod's folders: a full scan per mod made batches over SFTP quadratic (minutes per mod on a hosted server).
        var matches = ModScanner.ScanMod(fs, modReference);
        if (matches.Count == 0) throw new InvalidOperationException($"{modReference} is not installed.");
        var notOurs = matches.FirstOrDefault(m => m.Owner != ModOwner.Sdmm);
        if (notOurs is not null)
            throw new InvalidOperationException($"{notOurs.Folder} was not installed by this tool ({notOurs.Owner}); leaving it alone.");

        var batchDir = InstallPaths.Combine(StagingDir, Guid.NewGuid().ToString("N"));
        foreach (var m in matches)
        {
            fs.MoveDirectory(m.Folder, InstallPaths.Combine(batchDir, m.InGameFeatures ? "gf" : "mods", m.ModReference));
            _log($"{modReference}: removed {m.Folder}");
        }
        TryDelete(fs, batchDir);
        TryDeleteIfEmpty(fs, StagingDir);
    }

    /// <summary>
    /// Takes over a mod that SMM, a host's tool or a hand copy installed, so SDMM can update or remove it from then on.
    /// The files stay as they are. SDMM writes its marker, then removes SMM's .smm marker (after copying its contents
    /// into SDMM's marker), because .smm takes precedence and SMM deletes .smm folders it no longer lists.
    /// This deliberately bypasses the ownership protection used everywhere else, so callers must confirm it with the
    /// user first. Refused while the game runs from this install, for broken copies, and for mods installed twice.
    /// </summary>
    /// <returns>Where the mod came from: "SMM" or "other".</returns>
    public string Adopt(GameInstall install, string modReference, IGameProcessDetector? processes = null)
    {
        if (install.LocalRoot is { } root)
        {
            var running = (processes ?? new GameProcessDetector()).FindRunning(root);
            if (running.Count > 0) throw new InvalidOperationException($"Close the game first: {running[0]} is running from this install.");
        }
        var fs = install.FileSystem;
        // Only this mod's folders: a full scan per mod made batches over SFTP quadratic (minutes per mod on a hosted server).
        var matches = ModScanner.ScanMod(fs, modReference);
        if (matches.Count == 0) throw new InvalidOperationException($"{modReference} is not installed.");
        if (matches.Count > 1)
            throw new InvalidOperationException($"{modReference} is installed more than once ({string.Join(", ", matches.Select(m => m.Folder))}). Remove the extra copy first.");
        var mod = matches[0];
        if (mod.Owner == ModOwner.Sdmm) throw new InvalidOperationException($"{modReference} is already managed by SDMM.");
        if (mod.Descriptor is null)
            throw new InvalidOperationException($"{mod.Folder} is broken ({mod.DescriptorError}); reinstall it instead of taking it over.");

        var smmPath = InstallPaths.Combine(mod.Folder, ModScanner.SmmMarkerFileName);
        string from = mod.Owner == ModOwner.Smm ? SdmmMarker.AdoptedFromSmm : SdmmMarker.AdoptedFromOther;
        var marker = new SdmmMarker
        {
            ModReference = mod.ModReference,
            Version = mod.Descriptor.EffectiveVersion.ToString(),
            Target = TargetOf(install),
            Sha256 = SdmmMarker.AdoptedSha256,
            AdoptedFrom = from,
            PreviousSmmMarker = mod.Owner == ModOwner.Smm ? fs.ReadAllText(smmPath) : null,
            InstalledAtUtc = _time.GetUtcNow(),
            ToolVersion = ToolVersion,
        };
        fs.WriteAllText(InstallPaths.Combine(mod.Folder, SdmmMarker.FileName), marker.ToJson());
        if (mod.Owner == ModOwner.Smm) fs.DeleteFile(smmPath);
        _log($"{modReference}: taken over from {(from == "SMM" ? "SMM" : "its previous manager")} ({mod.Folder}); SDMM manages it now.");
        return from;
    }

    /// <summary>
    /// Hands a mod SDMM manages back to SMM: writes SMM's .smm marker and removes SDMM's. The files stay as they are.
    /// The marker is the SHA-256 of the zip SDMM installed, else (for a folder still as taken over) the one the takeover saved.
    /// The hash is what SMM writes for
    /// an SMR download (so an SMR install counts as already installed; a dev build doesn't match, and SMM replaces it with
    /// its own version on its next apply). The mod must be in an SMM profile for this install first, or SMM's next apply
    /// deletes the folder; the caller does that. Refused while the game runs from this install.
    /// </summary>
    public void HandBack(GameInstall install, string modReference, IGameProcessDetector? processes = null)
    {
        if (install.LocalRoot is { } root)
        {
            var running = (processes ?? new GameProcessDetector()).FindRunning(root);
            if (running.Count > 0) throw new InvalidOperationException($"Close the game first: {running[0]} is running from this install.");
        }
        var fs = install.FileSystem;
        // Only this mod's folders: a full scan per mod made batches over SFTP quadratic (minutes per mod on a hosted server).
        var matches = ModScanner.ScanMod(fs, modReference);
        if (matches.Count == 0) throw new InvalidOperationException($"{modReference} is not installed.");
        if (matches.Count > 1) throw new InvalidOperationException($"{modReference} is installed more than once; remove the extra copy first.");
        var mod = matches[0];
        if (mod.Owner != ModOwner.Sdmm || mod.Marker is not { } marker) throw new InvalidOperationException($"{modReference} isn't managed by SDMM.");
        // The zip SDMM installed, when there is one (it's what's on disk now); else SMM's own marker from the takeover.
        var smm = marker.Sha256 is { Length: 64 } hash ? hash
            : marker.PreviousSmmMarker is { Length: > 0 } previous ? previous
            : throw new InvalidOperationException($"{modReference} has no recorded build hash, so SMM's marker can't be written. Reinstall it with SMM instead.");
        fs.WriteAllText(InstallPaths.Combine(mod.Folder, ModScanner.SmmMarkerFileName), smm);
        fs.DeleteFile(InstallPaths.Combine(mod.Folder, SdmmMarker.FileName));
        _log($"{modReference}: handed back to SMM ({mod.Folder}).");
    }

    /// <summary>
    /// Gives a mod SDMM took over from a host's tool or a hand copy back to whoever had it, by removing SDMM's marker:
    /// the folder is then exactly as unmanaged as before the takeover (which only added that marker). Works on hosted
    /// servers too. Mods taken over from SMM go back through <see cref="HandBack"/> instead, since SMM needs its own marker.
    /// </summary>
    public void Release(GameInstall install, string modReference, IGameProcessDetector? processes = null)
    {
        if (install.LocalRoot is { } root)
        {
            var running = (processes ?? new GameProcessDetector()).FindRunning(root);
            if (running.Count > 0) throw new InvalidOperationException($"Close the game first: {running[0]} is running from this install.");
        }
        var fs = install.FileSystem;
        var matches = ModScanner.ScanMod(fs, modReference);
        if (matches.Count == 0) throw new InvalidOperationException($"{modReference} is not installed.");
        if (matches.Count > 1) throw new InvalidOperationException($"{modReference} is installed more than once; remove the extra copy first.");
        var mod = matches[0];
        if (mod.Owner != ModOwner.Sdmm || mod.Marker is not { } marker) throw new InvalidOperationException($"{modReference} isn't managed by SDMM.");
        if (marker.AdoptedFrom != SdmmMarker.AdoptedFromOther)
            throw new InvalidOperationException($"{modReference} wasn't taken over from a host or a hand copy, so there's nobody to give it back to.");
        fs.DeleteFile(InstallPaths.Combine(mod.Folder, SdmmMarker.FileName));
        _log($"{modReference}: no longer managed by SDMM; left as it was before the takeover ({mod.Folder}).");
    }

    private Packaging.ModTarget? _target;
    private GameInstall? _targetFor;

    /// <summary>The install's platform, read once per Installer (Detect reads version files, a round trip each remotely).</summary>
    private Packaging.ModTarget TargetOf(GameInstall install)
    {
        if (_targetFor != install || _target is null)
        {
            _target = install.Detect().Target;
            _targetFor = install;
        }
        return _target.Value;
    }

    /// <summary>Deletes staging leftovers from an interrupted run. Builds are still in the package cache.</summary>
    public void CleanupStaging(GameInstall install)
    {
        var fs = install.FileSystem;
        if (!fs.DirectoryExists(StagingDir)) return;
        _log($"Removing leftover staging folder {StagingDir} from an interrupted run.");
        fs.DeleteDirectory(StagingDir);
    }

    private SdmmMarker BuildMarker(PlannedAction action, Packaging.ModTarget installTarget)
    {
        var pkg = action.Request.Package;
        // Updating a mod SDMM took over keeps who it came from (and SMM's saved profile entries), so it can still be
        // handed back afterwards. Without this an update made the takeover permanent.
        var previous = (action.Existing ?? action.StaleOtherRoot)?.Marker;
        return new SdmmMarker
        {
            AdoptedFrom = previous?.AdoptedFrom,
            PreviousSmmMarker = previous?.PreviousSmmMarker,
            PreviousSmmProfiles = previous?.PreviousSmmProfiles,
            ModReference = pkg.ModReference,
            Version = pkg.Descriptor.EffectiveVersion.ToString(),
            Target = pkg.Target ?? installTarget,
            Sha256 = pkg.Sha256,
            SourceRepo = action.Request.Source.Repo,
            SourceTag = action.Request.Source.Tag,
            AssetName = action.Request.Source.AssetName ?? Path.GetFileName(pkg.ZipPath),
            InstalledAtUtc = _time.GetUtcNow(),
            ToolVersion = ToolVersion,
        };
    }

    private static void EnsureOursOrAbsent(IInstallFileSystem fs, string folder, string modRef)
    {
        if (!fs.DirectoryExists(folder)) return;
        var name = folder[(folder.LastIndexOf('/') + 1)..];
        var m = ModScanner.Inspect(fs, folder, name, folder.Contains("/GameFeatures/", StringComparison.OrdinalIgnoreCase));
        if (m.Owner != ModOwner.Sdmm)
            throw new InvalidOperationException($"{folder} is no longer managed by this tool ({m.Owner}); not replacing {modRef}.");
    }

    private void Rollback(IInstallFileSystem fs, List<Swap> done)
    {
        for (int i = done.Count - 1; i >= 0; i--)
        {
            var s = done[i];
            try
            {
                if (s.StaleBackup is not null && s.StaleFolder is not null && fs.DirectoryExists(s.StaleBackup))
                    fs.MoveDirectory(s.StaleBackup, s.StaleFolder);
                if (s.MovedIn && fs.DirectoryExists(s.Destination))
                    fs.DeleteDirectory(s.Destination);
                if (s.OldDestination is not null && fs.DirectoryExists(s.OldDestination))
                    fs.MoveDirectory(s.OldDestination, s.Destination);
                _log($"Rolled back {s.Destination}");
            }
            catch (Exception ex)
            {
                _log($"ROLLBACK FAILED for {s.Destination}: {ex.Message}. The previous copy may be in {s.OldDestination ?? "(none)"}.");
            }
        }
    }

    private void TryDelete(IInstallFileSystem fs, string dir)
    {
        try
        {
            fs.DeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            _log($"Could not delete {dir}: {ex.Message}. It will be cleaned up next time.");
        }
    }

    private static void TryDeleteIfEmpty(IInstallFileSystem fs, string dir)
    {
        try
        {
            if (fs.DirectoryExists(dir) && fs.ListDirectories(dir).Count == 0 && fs.ListFiles(dir).Count == 0)
                fs.DeleteDirectory(dir);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Swap(string destination, string staged)
    {
        public string Destination { get; } = destination;
        public string Staged { get; } = staged;
        public string? OldDestination { get; set; }
        public bool MovedIn { get; set; }
        public string? StaleFolder { get; set; }
        public string? StaleBackup { get; set; }
    }
}
