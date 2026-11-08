using System.Text.Json;

namespace Sdmm.Core.Security;

/// <summary>A mod file to check before it's installed.</summary>
public sealed record VtFile(string Name, string Path, string Sha256);

public enum VtOutcome
{
    /// <summary>Checked before (clean, or installed anyway after a warning): not looked up again.</summary>
    AlreadyChecked,
    /// <summary>VirusTotal knows it and no engine flags it.</summary>
    Clean,
    /// <summary>At least one engine says malicious or suspicious.</summary>
    Flagged,
    /// <summary>VirusTotal hasn't seen it, and uploading is off.</summary>
    Unknown,
    /// <summary>Uploaded, but the scan didn't finish in time.</summary>
    Pending,
    /// <summary>VirusTotal couldn't be asked (wrong key, quota, network); see the note.</summary>
    NotChecked,
}

public sealed record VtFileResult(VtFile File, VtOutcome Outcome, VtStats? Stats, string? Note)
{
    public string ReportUrl => $"https://www.virustotal.com/gui/file/{File.Sha256.ToLowerInvariant()}";
}

/// <summary>
/// The optional VirusTotal check before installing (Settings → Security, off by default). Each mod file is checked once:
/// files already checked (clean, or installed anyway after a warning) are remembered in a small list next to the
/// download cache and skipped from then on. Looking a file up sends only its SHA-256; the file itself is only sent when
/// uploading is switched on, since VirusTotal's premium customers can download submitted files.
/// <para>
/// A problem reaching VirusTotal (wrong key, used-up quota, network) doesn't block the install: those files come back as
/// <see cref="VtOutcome.NotChecked"/> with the reason, and once the quota or key is the problem, the rest aren't tried.
/// </para>
/// </summary>
public sealed class VirusTotalCheck(IVirusTotal vt, string checkedListPath)
{
    public static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a package is checked here: dev builds and imported zips are. Files downloaded from SMR aren't, because
    /// SMR runs every upload through VirusTotal before approving it, and only approved versions are installed.
    /// </summary>
    public static bool NeedsCheck(Installing.InstallRequest request) =>
        request.Source.Repo?.StartsWith(Installing.ServerSync.SmrSourcePrefix, StringComparison.OrdinalIgnoreCase) != true;

    public async Task<IReadOnlyList<VtFileResult>> CheckAsync(IReadOnlyList<VtFile> files, bool upload, IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var known = Load();
        var results = new List<VtFileResult>();
        string? stopReason = null;
        foreach (var f in files.DistinctBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(f.Sha256))
            {
                results.Add(new VtFileResult(f, VtOutcome.AlreadyChecked, null, null));
                continue;
            }
            if (stopReason is not null)
            {
                results.Add(new VtFileResult(f, VtOutcome.NotChecked, null, stopReason));
                continue;
            }
            try
            {
                status?.Report($"Checking {f.Name} with VirusTotal…");
                var stats = await vt.LookupAsync(f.Sha256, ct);
                if (stats is null && upload)
                {
                    status?.Report($"Uploading {f.Name} to VirusTotal…");
                    var analysis = await vt.UploadAsync(f.Path, ct);
                    status?.Report($"Waiting for VirusTotal to scan {f.Name} (usually a minute or two)…");
                    stats = await vt.WaitForAnalysisAsync(analysis, AnalysisTimeout, ct);
                    if (stats is null)
                    {
                        results.Add(new VtFileResult(f, VtOutcome.Pending, null, "uploaded, but the scan hadn't finished after 5 minutes"));
                        continue;
                    }
                }
                if (stats is null)
                {
                    results.Add(new VtFileResult(f, VtOutcome.Unknown, null, "VirusTotal hasn't seen this file yet (uploading is off)"));
                    continue;
                }
                if (stats.Flagged)
                {
                    results.Add(new VtFileResult(f, VtOutcome.Flagged, stats, null));
                    continue;
                }
                results.Add(new VtFileResult(f, VtOutcome.Clean, stats, null));
                known.Add(f.Sha256);
            }
            catch (VirusTotalException ex)
            {
                results.Add(new VtFileResult(f, VtOutcome.NotChecked, null, ex.Message));
                // A wrong key or a used-up quota won't fix itself during this install.
                if (ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.TooManyRequests) stopReason = ex.Message;
            }
        }
        Save(known);
        return results;
    }

    /// <summary>Remembers files the user chose to install despite a warning, so they aren't flagged again each time.</summary>
    public void MarkAccepted(IEnumerable<string> sha256s)
    {
        var known = Load();
        foreach (var s in sha256s) known.Add(s);
        Save(known);
    }

    private HashSet<string> Load()
    {
        try
        {
            if (File.Exists(checkedListPath))
                return new HashSet<string>(JsonSerializer.Deserialize(File.ReadAllText(checkedListPath), Json.CoreJsonContext.Default.ListString) ?? [],
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save(HashSet<string> known)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkedListPath)!);
            var tmp = checkedListPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(known.Order(StringComparer.OrdinalIgnoreCase).ToList(), Json.CoreJsonContext.Default.ListString));
            File.Move(tmp, checkedListPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
