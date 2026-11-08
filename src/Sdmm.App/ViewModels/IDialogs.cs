using Sdmm.Core.Config;
using Sdmm.Core.Updates;

namespace Sdmm.App.ViewModels;

public enum UpdateChoice
{
    /// <summary>Also the result when the prompt is closed with X or Esc.</summary>
    AskLater,
    UpdateNow,
    SkipVersion,
}

public enum ServerKind
{
    /// <summary>Rented from a host, reached over SFTP or FTP.</summary>
    Hosted,
    /// <summary>A dedicated server installed on this PC.</summary>
    ThisPc,
}

/// <param name="SuggestedPath">A better "Game folder" found below the one typed (FactoryGame lives there), or null.</param>
/// <param name="AcceptedFingerprint">The server key accepted during the test, to save with the settings.</param>
public sealed record ServerTestResult(bool Ok, string Message, string? SuggestedPath = null, string? AcceptedFingerprint = null);

/// <summary>UI prompts the view model needs, implemented by the main window. Safe to call from any thread.</summary>
public interface IDialogs
{
    void Info(string title, string message);
    void Error(string title, string message);
    bool Confirm(string title, string message);
    string? PickFolder(string title);
    /// <summary>Lets the user pick one file to open. Null if cancelled.</summary>
    string? PickOpenFile(string title, string filter);
    /// <summary>Lets the user choose where to save a file. Null if cancelled.</summary>
    string? PickSaveFile(string title, string filter, string defaultName);
    /// <summary>Lets the user pick one or more .zip files. Null if cancelled.</summary>
    IReadOnlyList<string>? PickZips(string title);
    /// <summary>
    /// Shows the hosted-server settings dialog. Returns false if cancelled. The password, if typed, is returned in plain
    /// text. <paramref name="test"/> checks the typed settings (config draft, typed password or null) without saving.
    /// </summary>
    bool EditServer(InstallConfig config, Func<InstallConfig, string?, Task<ServerTestResult>> test, out string? newPassword);
    /// <summary>Asks whether the new server is hosted (SFTP/FTP) or on this PC. Null if cancelled.</summary>
    ServerKind? ChooseServerKind();
    /// <summary>Opens Settings on Mod sources, or on GitHub token with its steps expanded.</summary>
    void ShowSources(bool expandTokenHelp = false);
    /// <summary>Opens Settings on Games &amp; servers.</summary>
    void ShowInstalls();
    /// <summary>Offers a few options, each with a line of detail. Returns the index picked, or null if cancelled.</summary>
    int? Choose(string title, string message, IReadOnlyList<(string Title, string Detail)> options);
    /// <summary>Asks whether to install an SDMM update now, later, or skip that version.</summary>
    UpdateChoice AskUpdate(AvailableUpdate update, string currentVersion, bool canSelfUpdate, string cannotSelfUpdateReason);
}
