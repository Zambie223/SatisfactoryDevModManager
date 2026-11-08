using Sdmm.Core.Installs;
using Sdmm.Core.Packaging;

namespace Sdmm.Core.Installing;

public enum IssueLevel
{
    /// <summary>Cannot proceed. Never overridable (for example, the folder belongs to SMM).</summary>
    Blocked,
    /// <summary>Will likely break the game, but the user may knowingly proceed (for example, a missing dependency that will be installed next).</summary>
    NeedsConfirmation,
    /// <summary>Informational.</summary>
    Warning,
}

public sealed record InstallIssue(IssueLevel Level, string Code, string? ModReference, string Message);

/// <summary>Where a package came from, recorded in the ownership marker.</summary>
public sealed record PackageSource(string? Repo, string? Tag, string? AssetName);

public sealed record InstallRequest(ModPackage Package, PackageSource Source);

public enum ActionKind
{
    /// <summary>Not installed yet.</summary>
    Install,
    /// <summary>Replaces a different build that this tool installed earlier.</summary>
    Replace,
    /// <summary>The exact same zip (same SHA-256) is already installed; nothing to do.</summary>
    AlreadyInstalled,
}

/// <param name="Destination">Install-relative folder the mod goes into.</param>
/// <param name="Existing">Our own install of this mod at the destination, if any.</param>
/// <param name="StaleOtherRoot">Our own install of this mod in the other root (Mods vs Mods/GameFeatures), removed after the swap.</param>
public sealed record PlannedAction(
    InstallRequest Request,
    ActionKind Kind,
    string Destination,
    InstalledMod? Existing,
    InstalledMod? StaleOtherRoot);

public sealed class InstallPlan
{
    public required GameInstall Install { get; init; }
    public required GameInfo Game { get; init; }
    public required IReadOnlyList<PlannedAction> Actions { get; init; }
    public required IReadOnlyList<InstallIssue> Issues { get; init; }

    public bool IsBlocked => Issues.Any(i => i.Level == IssueLevel.Blocked);

    public IReadOnlyList<InstallIssue> NeedingConfirmation => Issues.Where(i => i.Level == IssueLevel.NeedsConfirmation).ToList();

    /// <summary>True when nothing is blocked and every confirmation-level issue code is in <paramref name="confirmedCodes"/>.</summary>
    public bool CanExecute(IReadOnlySet<string> confirmedCodes) =>
        !IsBlocked && NeedingConfirmation.All(i => confirmedCodes.Contains(i.Code));
}
