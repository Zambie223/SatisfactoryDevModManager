using System.Windows;
using Sdmm.App.ViewModels;
using Sdmm.Core.Modpacks;

namespace Sdmm.App;

/// <summary>The Compare window (see <see cref="ModpackCompareViewModel"/>).</summary>
public partial class ModpackCompareWindow : Window
{
    public ModpackCompareWindow(IReadOnlyList<Modpack> packs, Modpack? left, Func<ModpackMod, ModpackModStatus?>? status = null)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        var vm = new ModpackCompareViewModel(packs, left, status);
        DataContext = vm;
        // DataGrid column headers aren't in the visual tree, so they can't bind to the pack names; set them here.
        void Headers()
        {
            LeftColumn.Header = vm.LeftName;
            RightColumn.Header = vm.RightName;
        }
        Headers();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(vm.LeftName) or nameof(vm.RightName)) Headers(); };
    }
}
