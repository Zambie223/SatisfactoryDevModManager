using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sdmm.App;

/// <summary>
/// The app's name, icons and accent colours, switchable at runtime.
/// Windows bind to <see cref="Current"/>. The exe's own file icon is baked in at build time and can't change.
/// </summary>
public sealed class Brand : INotifyPropertyChanged
{
    public static Brand Current { get; } = new();

    private bool _variant;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsVariant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            ApplyPalette();
            // Everything below derives from this flag.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private static readonly string[] PaletteKeys = ["AccentBrush", "AccentHoverBrush", "AccentTextBrush", "SelectionBrush"];

    /// <summary>
    /// Points the accent keys at the orange or green brushes in Themes/Theme.xaml. Setting a key on the
    /// application's own dictionary overrides the merged theme, and every DynamicResource reference updates at once.
    /// </summary>
    public void ApplyPalette()
    {
        var res = System.Windows.Application.Current?.Resources;
        if (res is null) return;
        var prefix = _variant ? "Green." : "Orange.";
        foreach (var key in PaletteKeys)
            if (res[prefix + key] is Brush b) res[key] = b;
    }

    public string Name => _variant ? "Satisfactory Black Market" : "Satisfactory Dev Mod Manager";
    public string Short => _variant ? "SBM" : "SDMM";
    public string CheckUpdatesText => $"Check for {Short} updates";
    public ImageSource Icon => _variant ? VariantIcon : SdmmIcon;
    public ImageSource Logo => _variant ? VariantLogo : SdmmLogo;

    private static readonly ImageSource SdmmIcon = Load("sdmm.ico");
    private static readonly ImageSource VariantIcon = Load("variant.ico");
    private static readonly ImageSource SdmmLogo = Load("sdmm-256.png");
    private static readonly ImageSource VariantLogo = Load("variant-256.png");

    private static ImageSource Load(string asset)
    {
        var frame = BitmapFrame.Create(new Uri($"pack://application:,,,/Assets/{asset}"));
        frame.Freeze();
        return frame;
    }
}
