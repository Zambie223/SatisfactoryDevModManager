using CommunityToolkit.Mvvm.ComponentModel;
using Sdmm.Core.Config;
using Sdmm.Core.Discovery;
using Sdmm.Core.Installs;

namespace Sdmm.App.ViewModels;

/// <summary>A configured game install, as shown in the install pickers and the Installs tab.</summary>
public sealed partial class InstallItem : ObservableObject
{
    public InstallConfig Config { get; }

    [ObservableProperty] private string _status = "";

    public InstallItem(InstallConfig config)
    {
        Config = config;
        IsServer = config.IsRemote || IsLocalDedicatedServer(config.Path);
    }

    public string Name => Config.Name;
    public bool IsRemote => Config.IsRemote;

    /// <summary>
    /// A dedicated server: any remote install (a rented host over SFTP/FTP), or a local folder holding the dedicated
    /// server (FactoryServer) rather than the game. Servers are managed in Server mode; games in "My game" mode.
    /// </summary>
    public bool IsServer { get; }

    private static bool IsLocalDedicatedServer(string path)
    {
        try
        {
            return Sdmm.Core.Installs.GameInstall.Local("", path).Detect().Target != Sdmm.Core.Packaging.ModTarget.Windows;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false; // missing or unreadable folder: keep it with the games, where its status shows the problem
        }
    }
    public string Location => Config.Kind switch
    {
        InstallKind.Local => Config.Path,
        InstallKind.Sftp => $"sftp://{Config.User}@{Config.Host}:{Config.Port ?? 22}{Config.Path}",
        _ => $"{(Config.UseFtps ? "ftps" : "ftp")}://{Config.User}@{Config.Host}:{Config.Port ?? 21}{Config.Path}",
    };
    public string Kind => Config.Kind switch
    {
        InstallKind.Local => IsServer ? "Server on this PC" : "Game",
        InstallKind.Sftp => "Hosted server (SFTP)",
        _ => Config.UseFtps ? "Hosted server (FTPS)" : "Hosted server (FTP)",
    };

    /// <summary>Raised when a setting was edited in the list (launch options), so the caller saves the settings.</summary>
    public event Action<InstallItem>? Edited;

    /// <summary>Launch options apply to games only; SDMM doesn't start servers.</summary>
    public bool CanHaveLaunchOptions => !IsServer;

    /// <summary>
    /// The switches Play passes to the game exe, edited in Settings → Games &amp; servers. One line: line breaks become
    /// spaces, and surrounding spaces are dropped.
    /// </summary>
    public string LaunchOptions
    {
        get => Config.LaunchOptions ?? "";
        set
        {
            var clean = string.Join(' ', (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (clean == (Config.LaunchOptions ?? "")) return;
            Config.LaunchOptions = clean.Length == 0 ? null : clean;
            OnPropertyChanged();
            Edited?.Invoke(this);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Location));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsRemote));
    }

    public override string ToString() => Name;
}

/// <summary>One choice in a mod row's version dropdown.</summary>
public sealed class BuildOption(ModBuild build)
{
    public ModBuild Build { get; } = build;
    public string Display =>
        $"{Build.Tag}{(Build.Prerelease ? " (pre)" : "")}  ·  {(Build.IsImported ? "imported zip" : Build.RepoFullName)}" +
        (Build.PublishedAt is { } d ? $"  ·  {d.LocalDateTime:yyyy-MM-dd}" : "");
    public override string ToString() => Display;
}

/// <summary>Who a hand-back returns a mod to.</summary>
public enum HandBackKind
{
    None,
    /// <summary>To SMM: its profile entry and .smm marker come back.</summary>
    Smm,
    /// <summary>To the host's tool or a hand copy it was taken over from: SDMM's marker is removed.</summary>
    Release,
}

public enum ModState { NotInstalled, Installed, UpdateAvailable, OtherVersion, ManagedBySmm, Manual, NoBuilds }

/// <summary>One mod in the mod list, for the selected install.</summary>
public sealed partial class ModRow : ObservableObject
{
    public string ModReference { get; }
    public DiscoveredMod? Discovered { get; }
    public InstalledMod? Installed { get; }
    public IReadOnlyList<BuildOption> Builds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(ReleaseNotes))]
    [NotifyPropertyChangedFor(nameof(WebUrl))]
    [NotifyPropertyChangedFor(nameof(InstallTip))]
    [NotifyPropertyChangedFor(nameof(InstallSourceText))]
    private BuildOption? _selectedBuild;

    /// <summary>Install from SMR (true) or from the dev builds in your sources (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGitHubSource), nameof(CanInstall), nameof(ActionText), nameof(InstallTip), nameof(InstallSourceText), nameof(ReleaseNotes))]
    private bool _useSmrSource;

    public bool IsGitHubSource
    {
        get => !UseSmrSource;
        set => UseSmrSource = !value;
    }

    /// <summary>Dev builds from your sources (GitHub releases or imported zips) exist for this platform.</summary>
    public bool HasDevBuilds => Builds.Count > 0;

    /// <summary>Both sources are available, so the details panel offers the choice.</summary>
    public bool CanChooseSource => HasDevBuilds && HasSmr;

    /// <summary>SMR versions for the picker (filled when the mod is selected).</summary>
    public System.Collections.ObjectModel.ObservableCollection<SmrVersionOption> SmrVersions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(ActionText), nameof(ReleaseNotes))]
    private SmrVersionOption? _selectedSmrVersion;

    /// <summary>Says plainly where Install takes the mod from.</summary>
    public string InstallSourceText => UseSmrSource
        ? (HasSmr ? $"Installs from SMR (ficsit.app){(HasDevBuilds ? ", not from your GitHub sources" : "")}." : "Not on SMR.")
        : HasDevBuilds ? $"Installs from {(SelectedBuild?.Build.IsImported == true ? "an imported zip" : SelectedBuild?.Build.RepoFullName ?? "your GitHub sources")}." : "";

    /// <summary>True when the list is for a server; changes how mods installed outside SDMM are described.</summary>
    public bool OnServer { get; }

    /// <summary>Problems found with the installed copy (see Sdmm.Core.Installs.ModHealth). Empty when fine or not installed.</summary>
    public IReadOnlyList<ModProblem> Problems { get; }

    public ModRow(string modReference, DiscoveredMod? discovered, InstalledMod? installed, IReadOnlyList<BuildOption> builds,
        bool onServer = false, IReadOnlyList<ModProblem>? problems = null, bool hosted = false)
    {
        ModReference = modReference;
        Hosted = hosted;
        Discovered = discovered;
        Installed = installed;
        Builds = builds;
        OnServer = onServer;
        Problems = problems ?? [];
        // Default source: GitHub for mods in your GitHub sources (dev builds), SMR for everything else, and SMR for a
        // mod that was installed from SMR, when SMR installs are available.
        _useSmrSource = Brand.Current.IsVariant && (IsFromSmr || builds.Count == 0);
        // Default selection: the installed build if it is listed, otherwise the newest.
        // A marker without a repo means the build came from an imported zip.
        _selectedBuild = builds.FirstOrDefault(b => installed?.Marker is { } m && b.Build.Tag == m.SourceTag &&
                                                    (m.SourceRepo is null ? b.Build.IsImported : b.Build.RepoFullName == m.SourceRepo))
                         ?? builds.FirstOrDefault(b => b.Build.Version is not null);
    }

    /// <summary>"owner/repo" of the newest GitHub build (or of the installed one), else "" for mods only known from imported zips.</summary>
    public string Source => Discovered?.Builds.FirstOrDefault(b => !b.IsImported)?.RepoFullName ?? Installed?.Marker?.SourceRepo ?? "";

    public string InstalledText => Installed switch
    {
        null => "—",
        { Owner: ModOwner.Sdmm, Marker: { IsAdoptedCopy: true, AdoptedFrom: { } from } } m =>
            $"{m.Descriptor?.EffectiveVersion.ToString() ?? "?"}  (taken over from {(from == SdmmMarker.AdoptedFromSmm ? "SMM" : OnServer ? "the host or a hand copy" : "a hand copy")})",
        { Owner: ModOwner.Sdmm } m => $"{m.Descriptor?.EffectiveVersion.ToString() ?? "?"}  (dev{(m.Marker?.SourceTag is { } t ? ", " + t : "")})",
        { Owner: ModOwner.Smm } m => $"{m.Descriptor?.EffectiveVersion.ToString() ?? "?"}  (SMM)",
        var m => $"{m.Descriptor?.EffectiveVersion.ToString() ?? "?"}  (manual)",
    };

    /// <summary>Newest installable build shown (the list already honours the prerelease toggle).</summary>
    public BuildOption? Latest => Builds.FirstOrDefault(b => b.Build.Version is not null);

    private Sdmm.Core.Versioning.SemVersion? InstalledSemVer => Installed?.Descriptor?.EffectiveVersion;

    /// <summary>Status for the list: compares what is installed with the newest build available.</summary>
    public ModState State
    {
        get
        {
            if (Installed is { Owner: ModOwner.Smm }) return ModState.ManagedBySmm;
            if (Installed is { Owner: ModOwner.Unmanaged }) return ModState.Manual;
            if (Installed is null) return Latest is null ? ModState.NoBuilds : ModState.NotInstalled;
            if (IsFromSmr) return SmrUpdateVersion is not null ? ModState.UpdateAvailable : ModState.Installed;
            var latest = Latest?.Build.Version;
            var iv = InstalledSemVer;
            if (latest is null || iv is null) return ModState.Installed;
            if (latest > iv) return ModState.UpdateAvailable;
            return latest == iv ? ModState.Installed : ModState.OtherVersion;
        }
    }

    public string StateText => State switch
    {
        ModState.ManagedBySmm => "Managed by SMM",
        ModState.Manual => OnServer ? "Installed by the host or by hand" : "Installed outside SDMM",
        ModState.NotInstalled => "Not installed",
        ModState.NoBuilds => "No builds for this platform",
        ModState.UpdateAvailable => IsFromSmr ? $"Update available on SMR: {SmrUpdateVersion}" : $"Update available: {Latest?.Build.Tag}",
        ModState.OtherVersion => "Newer than the latest listed build",
        _ => "Installed",
    };

    /// <summary>Longer explanation shown in the details panel.</summary>
    public string StateDetail => State switch
    {
        ModState.ManagedBySmm => OnServer
            ? "SMM installed this mod on the server, so SDMM won't change it. Update or remove it in SMM; to test a dev build here, remove it from the server's SMM profile first."
            : "SMM installed this mod, so SDMM won't change it. To test a dev build, remove it from your SMM profile and apply in SMM, then install it here.",
        ModState.Manual => OnServer
            ? "This mod wasn't installed by SDMM or SMM: most likely your host's own mod tool, or someone copied it in by hand. SDMM won't change it; update or remove it where it was installed (for a host's tool, in the host's panel)."
            : "This mod wasn't installed by SDMM or SMM (it was copied in by hand or by another tool), so SDMM won't change it. Delete its folder yourself if you want SDMM to manage it.",
        ModState.NoBuilds => "No release has a build for this install's platform" + (Discovered is null ? " (it is no longer in your sources)." : "."),
        _ => "",
    };

    // ---- who manages it, and what's wrong with it -------------------------------------------------------------

    /// <summary>Short tag next to the name saying who installed the mod; empty when it isn't installed.</summary>
    public string OwnerBadge => Installed?.Owner switch
    {
        null => "",
        ModOwner.Sdmm => IsFromSmr ? "SMR" : Installed.Marker?.IsAdoptedCopy == true ? "Taken over" : "SDMM dev",
        ModOwner.Smm => "SMM",
        _ => OnServer ? "Host / manual" : "Manual",
    };

    public System.Windows.Media.Brush OwnerBadgeBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[Installed?.Owner switch
        {
            ModOwner.Sdmm => "AccentBrush",
            ModOwner.Smm => "InfoBrush",
            _ => "MutedBrush",
        }];

    public string OwnerBadgeTip => Installed?.Owner switch
    {
        ModOwner.Sdmm => "Installed by SDMM (marked with .sdmm.json); SDMM can update or remove it.",
        ModOwner.Smm => "Installed by SMM (marked with .smm); manage it in SMM.",
        null => "",
        _ => OnServer ? "Installed by your host's mod tool or by hand; SDMM leaves it alone." : "Installed by hand or by another tool; SDMM leaves it alone.",
    };

    /// <summary>Installed by SMM, the host or by hand: SDMM won't install over it or remove it.</summary>
    public bool IsManagedElsewhere => Installed is { Owner: not ModOwner.Sdmm };

    /// <summary>
    /// Can be taken over by SDMM: installed by another tool or by hand, readable, and not
    /// installed twice. The installer re-checks all of this.
    /// </summary>
    public bool CanTakeOver => IsManagedElsewhere && Installed?.Descriptor is not null && Problems.All(p => p.Code != "DUPLICATE");

    public bool HasProblems => Problems.Count > 0;

    /// <summary>A required dependency is missing or the wrong version (offers "Install missing dependencies").</summary>
    public bool HasUnmetDependencies => Problems.Any(p => p.Code is "DEPENDENCY_MISSING" or "DEPENDENCY_VERSION");

    /// <summary>An SMM profile still lists this mod, so SMM would put its own version back.</summary>
    public bool SmmWillReplace => Problems.Any(p => p.Code == "SMM_WILL_REPLACE");

    /// <summary>The first problem in a few words, for the list ("Needs SML ^3.12.0 (+1 more)").</summary>
    public string ProblemShort => Problems.Count == 0 ? "" : Problems[0].Short + (Problems.Count > 1 ? $" (+{Problems.Count - 1} more)" : "");

    /// <summary>Every problem, explained, for the details panel.</summary>
    public string ProblemsText => string.Join("\n\n", Problems.Select(p => "• " + p.Message));

    /// <summary>Tooltip on the Install buttons: why it's disabled, when it is.</summary>
    public string InstallTip => UseSmrSource
        ? (Smr is null ? "Not on SMR." : "Install from SMR with what it needs. Shows what will change first.")
        : Installed?.Owner switch
    {
        ModOwner.Smm => "Managed by SMM: SDMM won't install over it. Change it in SMM.",
        ModOwner.Unmanaged => OnServer ? "Installed by the host or by hand: SDMM won't install over it." : "Installed outside SDMM: SDMM won't install over it.",
        _ when SelectedBuild?.Build.Version is null => "No build for this platform to install.",
        _ => "Install the selected build",
    };

    public string RemoveTip => Installed?.Owner switch
    {
        null => "Not installed",
        ModOwner.Sdmm => "Remove this dev build",
        ModOwner.Smm => "Managed by SMM: remove it in SMM.",
        _ => "Installed outside SDMM: SDMM won't delete it.",
    };

    /// <summary>
    /// Shown when the installed copy's .uplugin sets RequiredOnRemote to false (only known once it is installed, because
    /// GitHub release data doesn't include the .uplugin).
    /// </summary>
    public string RemoteNote => Installed?.Descriptor is { IsRequiredOnRemote: false }
        ? "Server-side only: its .uplugin sets RequiredOnRemote to false, so players joining a dedicated server don't need it. Server sync leaves it out."
        : "";

    /// <summary>Theme brush key for the status dot and text.</summary>
    public string StateBrushKey => State switch
    {
        ModState.Installed => "OkBrush",
        ModState.UpdateAvailable => "WarnBrush",
        ModState.OtherVersion => "InfoBrush",
        ModState.ManagedBySmm => "InfoBrush",
        _ => "MutedBrush",
    };

    public System.Windows.Media.Brush StateBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[StateBrushKey];

    /// <summary>" · installed 1.2.0" for the list row, or nothing when not installed (the status already says so).</summary>
    public string InstalledSuffix => Installed?.Descriptor?.EffectiveVersion is { } v ? $"  ·  installed {v}" : "";
    public string LatestText => Latest is { } l ? l.Build.Tag + (l.Build.Prerelease ? " (prerelease)" : "") : "None";

    public string Owner => (IsFromSmr || Source.Length == 0) && Smr?.Authors.FirstOrDefault() is { } author ? author.Username
        : IsFromSmr ? "SMR"
        : Source.Contains('/') ? Source[..Source.IndexOf('/')]
        : Source.Length > 0 ? Source
        // No GitHub source: an imported zip (an SDMM marker without a repo, or only imported builds), else unknown.
        : Discovered?.Builds.Any(b => b.IsImported) == true || Installed?.Marker is { SourceRepo: null, IsAdoptedCopy: false } ? "imported zip"
        : "";

    /// <summary>" by " before the author, or nothing when the author is unknown.</summary>
    public string ByText => Owner.Length > 0 ? " by " : "";
    /// <summary>The GitHub repo's description.</summary>
    public string GitHubDescription =>
        Latest?.Build.RepoDescription ?? Discovered?.Builds.FirstOrDefault(b => b.RepoDescription is not null)?.RepoDescription
        ?? (Discovered is null
                ? Installed?.Owner == ModOwner.Sdmm
                    ? Installed.Marker?.IsAdoptedCopy == true ? "Taken over by SDMM; not in your GitHub sources." : "Installed by SDMM; no longer in your GitHub sources."
                    : "Not in your GitHub sources."
            : Discovered.Builds.All(b => b.IsImported) ? "Added from a zip with Install from zip; not in your GitHub sources."
            : "No description.");

    /// <summary>For the list row: SMR's short description when the mod is published there, otherwise GitHub's.</summary>
    public string Description => Smr?.ShortDescription is { Length: > 0 } s ? s : GitHubDescription;

    public string ReleaseNotes => UseSmrSource
        ? Smr is null ? "Not on SMR, so there are no release notes."
        : (SelectedSmrVersion?.Version ?? SmrVersions.Skip(1).FirstOrDefault(o => o.Fits)?.Version)?.Changelog is { Length: > 0 } cl ? TextFormat.MarkdownToPlain(cl) : "No changelog for this SMR version."
        : SelectedBuild?.Build.ReleaseNotes is { } md ? TextFormat.MarkdownToPlain(md)
        : SelectedBuild?.Build.IsImported == true ? "Added from a zip with Install from zip, so there are no release notes."
        : "This release has no notes.";

    // ---- SMR (ficsit.app) listing, when the mod is published there ----------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmr), nameof(Description), nameof(DisplayName), nameof(SmrAuthorsText), nameof(SmrStatsText),
        nameof(SmrReleaseText), nameof(SmrCompatText), nameof(SmrTagsText), nameof(SmrFullDescription), nameof(SmrPageUrl), nameof(SmrComparisonText),
        nameof(SmrUpdateVersion), nameof(State), nameof(StateText), nameof(StateBrush), nameof(Owner), nameof(ByText), nameof(CanHandBack), nameof(HandBackTo), nameof(HandBackText), nameof(HandBackTip),
        nameof(CanChooseSource), nameof(CanInstall), nameof(ActionText), nameof(InstallSourceText), nameof(InstallTip), nameof(ReleaseNotes))]
    private Sdmm.Core.Smr.SmrMod? _smr;

    /// <summary>Installed from SMR by SDMM (the marker's source is "ficsit.app/&lt;ModRef&gt;").</summary>
    public bool IsFromSmr => Installed?.Marker?.SourceRepo?.StartsWith(MainViewModel.SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// SMR's newest release when it's newer than what's installed, for mods "Update from SMR" may update: ones installed
    /// from SMR, and ones SMM, a host or a hand copy installed (taken over first). GitHub dev builds aren't, since an
    /// SMR release would replace the dev build being tested. Whether that release fits this install is checked on update.
    /// </summary>
    public string? SmrUpdateVersion =>
        Installed?.Descriptor is { } d && (IsFromSmr || Installed.Owner != ModOwner.Sdmm) &&
        Smr?.LatestRelease is { } latest && Sdmm.Core.Versioning.SemVersion.TryParse(latest, out var lv) && lv! > d.EffectiveVersion
            ? latest : null;

    /// <summary>
    /// Who this mod can be handed back to:
    /// <list type="bullet">
    /// <item>Taken over from a host's tool or a hand copy: back to that (SDMM's marker is removed).</item>
    /// <item>Otherwise SMM, when it's on SMR (SMM can only manage SMR mods) and SDMM knows what to write into SMM's
    /// marker (the one a takeover saved, or the hash of the zip it installed).</item>
    /// </list>
    /// </summary>
    public HandBackKind HandBackTo => Installed is not { Owner: ModOwner.Sdmm, Marker: { } m } ? HandBackKind.None
        : m.AdoptedFrom == SdmmMarker.AdoptedFromOther ? HandBackKind.Release
        : !Hosted && Smr is not null && (m.PreviousSmmMarker is { Length: > 0 } || m.Sha256 is { Length: 64 }) ? HandBackKind.Smm
        : HandBackKind.None;

    public bool CanHandBack => HandBackTo != HandBackKind.None;

    /// <summary>On a hosted (SFTP/FTP) server, where SMM's own settings aren't read, so a hand-back to SMM isn't offered.</summary>
    public bool Hosted { get; }

    public string HandBackText => HandBackTo switch
    {
        HandBackKind.Smm => "Hand back to SMM",
        HandBackKind.Release => OnServer ? "Hand back to the host" : "Stop managing (back to a hand copy)",
        _ => "",
    };

    public string HandBackTip => HandBackTo switch
    {
        HandBackKind.Smm => "Give this mod back to SMM: it's added to your SMM profile and gets SMM's marker again. Asks first.",
        HandBackKind.Release => "SDMM took this mod over from " + (OnServer ? "the host (or a hand copy)" : "a hand copy") +
                                ". This removes SDMM's marker, so it's left exactly as unmanaged as before. The files aren't changed. Asks first.",
        _ => "",
    };

    /// <summary>In your GitHub sources or imported zips (as opposed to only installed here by another tool).</summary>
    public bool IsInYourSources => Discovered is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    private System.Windows.Media.ImageSource? _logo;

    /// <summary>Which details tab is showing: SMR when true, GitHub (the dev build) when false.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitHubTab))]
    private bool _showSmrTab;

    public bool ShowGitHubTab
    {
        get => !ShowSmrTab;
        set => ShowSmrTab = !value;
    }

    public bool HasSmr => Smr is not null;
    public bool HasLogo => Logo is not null;

    /// <summary>SMR's display name ("Auto Trains") when published, else the mod reference.</summary>
    public string DisplayName => Smr?.Name is { Length: > 0 } n ? n : ModReference;

    public string SmrAuthorsText => Smr is null ? "" :
        string.Join(", ", Smr.Authors.Select(a => a.Role is { Length: > 0 } r && r != "creator" ? $"{a.Username} ({r})" : a.Username));

    public string SmrStatsText => Smr is null ? "" : $"{Smr.Downloads:N0} downloads  ·  {Smr.Views:N0} views";

    public string SmrReleaseText
    {
        get
        {
            if (Smr is null) return "";
            if (Smr.LatestRelease is { } v)
                return v + (Smr.LatestReleaseDate is { } d ? $"  ({d.LocalDateTime:yyyy-MM-dd})" : "");
            var pre = Smr.LatestBeta ?? Smr.LatestAlpha;
            return pre is null ? "No release on SMR yet" : $"No release on SMR yet (latest pre-release {pre})";
        }
    }

    public string SmrCompatText
    {
        get
        {
            if (Smr is null) return "";
            static string F(Sdmm.Core.Smr.SmrCompatibility? c) => c is null ? "not reported" : c.Note is { } n ? $"{c.State} ({n})" : c.State;
            return $"Early Access: {F(Smr.EarlyAccess)}  ·  Experimental: {F(Smr.Experimental)}";
        }
    }

    public string SmrTagsText => Smr is null || Smr.Tags.Count == 0 ? "" : string.Join("  ", Smr.Tags.Select(t => "#" + t));

    public string SmrFullDescription => Smr?.FullDescription is { Length: > 0 } md ? TextFormat.MarkdownToPlain(md)
        : Smr?.ShortDescription ?? "";

    public string? SmrPageUrl => Smr?.PageUrl;

    /// <summary>How the newest dev build compares with the published SMR release, shown on the GitHub tab.</summary>
    public string SmrComparisonText
    {
        get
        {
            if (Smr is null) return "";
            if (Smr.LatestRelease is null || !Sdmm.Core.Versioning.SemVersion.TryParse(Smr.LatestRelease, out var smrV)) return "Published on SMR, no release yet.";
            var dev = Latest?.Build.Version;
            if (dev is null) return $"SMR release: {Smr.LatestRelease}";
            return dev > smrV! ? $"SMR release: {Smr.LatestRelease}. The newest dev build ({dev}) is ahead of it."
                : dev == smrV ? $"SMR release: {Smr.LatestRelease}, the same version as the newest dev build."
                : $"SMR release: {Smr.LatestRelease}, newer than the newest dev build ({dev}).";
        }
    }

    public string? WebUrl => SelectedBuild?.Build.HtmlUrl ?? (Source.Contains('/') ? $"https://github.com/{Source}" : null);

    /// <summary>Label for the main action in the details panel, based on the selected build.</summary>
    public string ActionText
    {
        get
        {
            if (UseSmrSource)
            {
                var pinned = SelectedSmrVersion?.Version?.Version;
                if (Installed is null) return pinned is null ? "Install from SMR" : $"Install {pinned} from SMR";
                if (pinned is null) return SmrUpdateVersion is { } u ? $"Update to {u} from SMR" : "Install the newest from SMR";
                return pinned == InstalledSemVer ? $"Reinstall {pinned} from SMR" : $"Switch to {pinned} from SMR";
            }
            if (Installed is { Owner: ModOwner.Smm }) return "Managed by SMM";
            if (Installed is { Owner: ModOwner.Unmanaged }) return OnServer ? "Installed by the host or by hand" : "Installed outside SDMM";
            var v = SelectedBuild?.Build.Version;
            var iv = InstalledSemVer;
            if (Installed is null || iv is null || v is null) return "Install";
            if (v > iv) return $"Update to {SelectedBuild!.Build.Tag}";
            if (v < iv) return $"Switch to {SelectedBuild!.Build.Tag}";
            return "Reinstall";
        }
    }

    /// <summary>Two-letter tile text: the first two capitals of the mod reference ("DepotNetworkFix" → "DN").</summary>
    public string Initials
    {
        get
        {
            var caps = ModReference.Where(char.IsUpper).Take(2).ToArray();
            return caps.Length == 2 ? new string(caps) : ModReference[..Math.Min(2, ModReference.Length)].ToUpperInvariant();
        }
    }

    /// <summary>Used as the list item's accessible name (screen readers, UI automation).</summary>
    public override string ToString() => ModReference;

    private static readonly string[] TileColors = ["#3E5C76", "#5B4B8A", "#2F6F5E", "#7A4E3A", "#4F6D2E", "#7A3E5A", "#36618C", "#6B5B2E"];

    /// <summary>A muted colour picked from the name, so each mod keeps the same tile colour.</summary>
    public System.Windows.Media.Brush TileBrush
    {
        get
        {
            int h = 0;
            foreach (var c in ModReference) h = unchecked(h * 31 + c);
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TileColors[(h & 0x7fffffff) % TileColors.Length]);
            var b = new System.Windows.Media.SolidColorBrush(color);
            b.Freeze();
            return b;
        }
    }

    /// <summary>
    /// False for SMM-managed or hand-installed copies: the planner would refuse them anyway, and this avoids a
    /// pointless download. The status text says why.
    /// </summary>
    public bool CanInstall => UseSmrSource
        // SMR installs take over other tools' copies themselves (after asking), so any owner is fine.
        ? Smr is not null && (SelectedSmrVersion is null || SelectedSmrVersion.Version is null || SelectedSmrVersion.Fits)
        : SelectedBuild?.Build.Version is not null && Installed?.Owner is null or ModOwner.Sdmm;
    public bool CanRemove => Installed?.Owner == ModOwner.Sdmm;
}

/// <summary>One line in the server sync review. Actionable lines (install or remove) have a checkbox.</summary>
/// <param name="canInstallFromSmr">"From SMR" lines can be installed, not just reported.</param>
public sealed partial class SyncLine(Sdmm.Core.Installing.SyncItem item, bool canInstallFromSmr = false) : ObservableObject
{
    public Sdmm.Core.Installing.SyncItem Item { get; } = item;
    public string Mod => Item.ModReference;
    public string Server => Item.ServerVersion ?? "—";
    public string Local => Item.LocalVersion ?? "—";
    public string Message => canInstallFromSmr && Item.Status == Sdmm.Core.Installing.SyncStatus.FromSmr
        ? Item.Message.Replace("so install that version from SMR (for example with SMM).", "so that version is installed from SMR.")
        : Item.Message;
    public bool IsActionable => Item.IsActionable && (Item.Status != Sdmm.Core.Installing.SyncStatus.FromSmr || canInstallFromSmr);

    /// <summary>Whether "Match" applies this line. Every actionable line starts selected.</summary>
    [ObservableProperty] private bool _isSelected = item.IsActionable && (item.Status != Sdmm.Core.Installing.SyncStatus.FromSmr || canInstallFromSmr);

    public string Status => Item.Status switch
    {
        Sdmm.Core.Installing.SyncStatus.InSync => "In sync",
        Sdmm.Core.Installing.SyncStatus.Install => "Install",
        Sdmm.Core.Installing.SyncStatus.FromSmr => canInstallFromSmr ? "Install from SMR" : "From SMR",
        Sdmm.Core.Installing.SyncStatus.Remove => "Remove",
        Sdmm.Core.Installing.SyncStatus.ServerOnly => "Server only",
        Sdmm.Core.Installing.SyncStatus.ClientOnly => "Game only",
        Sdmm.Core.Installing.SyncStatus.BuildNotFound => "Build not found",
        _ => "Managed elsewhere",
    };
}
