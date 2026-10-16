using Avalonia.Controls;
using Avalonia.Interactivity;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// ➕ The ⚡ H3 Express job sheet — see <see cref="ExpressJobViewModel"/>. The window holds no logic:
/// the two buttons close it, and <see cref="ExpressJobViewModel.Confirmed"/> tells the tab which one
/// was pressed. Shown through the FlipPix.UI.ExpressJobWindow facade the view model creates.
/// </summary>
public partial class ExpressJobWindow : Window
{
    private readonly FlipPix.UI.ExpressJobWindow? _facade;

    public ExpressJobWindow() : this(null!, null) { }

    public ExpressJobWindow(ExpressJobViewModel viewModel, FlipPix.UI.ExpressJobWindow? facade)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        _facade = facade;
    }

    public ExpressJobViewModel ViewModel { get; }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.Confirm();
        if (_facade != null) _facade.DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_facade != null) _facade.DialogResult = false;
        Close();
    }
}
