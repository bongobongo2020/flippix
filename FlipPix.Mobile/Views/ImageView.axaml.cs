using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FlipPix.Mobile.Views;

public partial class ImageView : UserControl
{
    public ImageView() => InitializeComponent();

    // Dismiss the keyboard and bring the sheet into view, where the new tiles just landed.
    private void OnMakeClicked(object? sender, RoutedEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        if (this.FindControl<ItemsControl>("Sheet") is { } sheet)
            sheet.BringIntoView();
    }
}
