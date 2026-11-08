using System.Windows;
using Sdmm.App.ViewModels;

namespace Sdmm.App;

/// <summary>The SMR catalog browser (see <see cref="SmrBrowserViewModel"/>).</summary>
public partial class SmrBrowserWindow : Window
{
    private readonly SmrBrowserViewModel _vm;

    public SmrBrowserWindow(MainViewModel main)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        _vm = new SmrBrowserViewModel(main);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await _vm.StartAsync();
        };
        Closed += (_, _) => _vm.Dispose();
    }
}
