using System.Windows;

namespace Sdmm.App;

/// <summary>A message with a few large options (title plus a line of detail) and Cancel.</summary>
public partial class ChoiceWindow : Window
{
    /// <summary>The index of the option picked; null if cancelled.</summary>
    public int? Choice { get; private set; }

    public ChoiceWindow(string title, string message, IReadOnlyList<(string Title, string Detail)> options)
    {
        InitializeComponent();
        DarkTitleBar.Enable(this);
        Title = title;
        MessageText.Text = message;
        Options.ItemsSource = options.Select((o, i) => new { Index = i, o.Title, o.Detail }).ToList();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        Choice = (int)((FrameworkElement)sender).Tag;
        DialogResult = true;
    }
}
