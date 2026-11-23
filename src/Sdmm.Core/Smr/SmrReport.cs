using Sdmm.Core.Installs;

namespace Sdmm.Core.Smr;

public enum SmrReportLevel
{
    /// <summary>Damaged: loads, but parts don't work (SMR's note says which).</summary>
    Damaged,
    /// <summary>Broken: doesn't work with the current game.</summary>
    Broken,
}

/// <summary>
/// What SMR (ficsit.app) reports about a mod working with the current game, when it reports a problem. The report is
/// for the mod's current SMR release and for one game branch (Early Access or Experimental).
/// </summary>
public sealed record SmrReport(string ModReference, SmrReportLevel Level, bool Experimental, bool BranchKnown, string? Note)
{
    public string BranchName => Experimental ? "Experimental" : "Early Access";

    /// <summary>A few words for lists: "Broken on SMR" or "Damaged on SMR".</summary>
    public string Short => Level == SmrReportLevel.Broken ? "Broken on SMR" : "Damaged on SMR";

    /// <summary>The full explanation, with SMR's note and which branch the report is for.</summary>
    public string Message =>
        $"SMR (ficsit.app) reports {ModReference} as {(Level == SmrReportLevel.Broken ? "broken" : "damaged")} on {BranchName}" +
        (Note is { Length: > 0 } n ? $": {n}" + (".!?".Contains(n[^1]) ? "" : ".") : ".") +
        (BranchKnown ? "" : " This install's game branch isn't known, so this is the Early Access report.") +
        " The report is for the mod's current SMR release.";

    /// <summary>The report for this branch, or null when SMR reports no problem (or nothing at all).</summary>
    public static SmrReport? For(SmrMod mod, GameBranch branch)
    {
        bool exp = branch == GameBranch.Experimental;
        var c = exp ? mod.Experimental : mod.EarlyAccess;
        if (c is null) return null;
        SmrReportLevel? level = c.State.Trim().ToLowerInvariant() switch
        {
            "broken" => SmrReportLevel.Broken,
            "damaged" => SmrReportLevel.Damaged,
            _ => null,
        };
        return level is { } l ? new SmrReport(mod.ModReference, l, exp, branch != GameBranch.Unknown, c.Note?.Trim()) : null;
    }
}
