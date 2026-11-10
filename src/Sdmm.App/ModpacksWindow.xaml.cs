using System.Windows;
using System.Windows.Input;
using Sdmm.App.ViewModels;

namespace Sdmm.App;

/// <summary>The Modpacks window (see <see cref="ModpacksViewModel"/>).</summary>
public partial class ModpacksWindow : Window
{
    public ModpacksWindow(MainViewModel main, IDialogs dialogs)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        var vm = new ModpacksViewModel(main, dialogs);
        DataContext = vm;
        // Edits to names, switches and sides are made in place; save them when the window closes.
        Closing += (_, _) =>
        {
            if (FocusManager.GetFocusedElement(this) is System.Windows.Controls.TextBox tb)
                tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            vm.Save();
        };
    }

    private void CommitName()
    {
        NameBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        NameBox.SelectAll();
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitName();
        e.Handled = true;
    }

    private void OnRename(object sender, RoutedEventArgs e) => CommitName();
}
