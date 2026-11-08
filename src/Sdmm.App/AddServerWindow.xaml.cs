using System.Windows;
using Sdmm.App.ViewModels;

namespace Sdmm.App;

/// <summary>Asks which kind of server to add: a hosted one (SFTP/FTP login) or a dedicated server on this PC.</summary>
public partial class AddServerWindow : Window
{
    /// <summary>The choice, or null if cancelled.</summary>
    public ServerKind? Choice { get; private set; }

    public AddServerWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
    }

    private void OnHosted(object sender, RoutedEventArgs e) => Pick(ServerKind.Hosted);
    private void OnThisPc(object sender, RoutedEventArgs e) => Pick(ServerKind.ThisPc);

    private void Pick(ServerKind kind)
    {
        Choice = kind;
        DialogResult = true;
    }
}
