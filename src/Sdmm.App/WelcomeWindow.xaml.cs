using System.Windows;

namespace Sdmm.App;

public enum WelcomeChoice
{
    /// <summary>Also the result when the window is closed with X or Esc.</summary>
    AskLater,
    AddTokenNow,
    DontAskAgain,
}

/// <summary>Startup prompt shown while no GitHub token is saved: add one now, ask again later, or never ask.</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeChoice Choice { get; private set; } = WelcomeChoice.AskLater;

    public WelcomeWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
    }

    private void OnNow(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.AddTokenNow;
        DialogResult = true;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.AskLater;
        DialogResult = false;
    }

    private void OnNever(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.DontAskAgain;
        DialogResult = false;
    }
}
