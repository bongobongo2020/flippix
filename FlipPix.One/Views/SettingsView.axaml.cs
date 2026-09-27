using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FlipPix.One.ViewModels;

namespace FlipPix.One.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // Back leaves without saving; the shell owns where "back" goes.
    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<MainView>()?.DataContext is MainViewModel main)
            main.CloseSettingsCommand.Execute(null);
    }
}
