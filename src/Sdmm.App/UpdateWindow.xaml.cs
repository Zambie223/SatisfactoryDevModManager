using System.Windows;
using Sdmm.App.ViewModels;
using Sdmm.Core.Updates;

namespace Sdmm.App;

/// <summary>Offers an SDMM update: update now, ask again later, or skip this version.</summary>
public partial class UpdateWindow : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.AskLater;

    public UpdateWindow(AvailableUpdate update, string currentVersion, bool canSelfUpdate, string cannotSelfUpdateReason)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        HeadingText.Text = $"{Brand.Current.Short} {update.Version} is available";
        SubText.Text = $"You have version {currentVersion}." + (update.Prerelease ? " This is a prerelease." : "");
        NotesText.Text = update.Notes is { } n ? TextFormat.MarkdownToPlain(n) : "This release has no notes.";
        if (!canSelfUpdate)
        {
            UpdateButton.Content = "Open download page";
            NoticeText.Text = cannotSelfUpdateReason + " The release page will open in your browser instead.";
        }
        else
        {
            NoticeText.Text = "SDMM downloads the new version, checks it against GitHub's checksum, and restarts. " +
                              "Update checks at startup can be turned off in the sidebar under SDMM, and you can check manually there any time.";
        }
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.UpdateNow;
        DialogResult = true;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.AskLater;
        DialogResult = false;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Choice = UpdateChoice.SkipVersion;
        DialogResult = false;
    }
}
