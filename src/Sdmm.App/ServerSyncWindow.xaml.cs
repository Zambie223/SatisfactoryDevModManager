using System.Windows;

namespace Sdmm.App;

public partial class ServerSyncWindow : Window
{
    public ServerSyncWindow()
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
