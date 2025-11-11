using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI;

public partial class LongCatWindow : Window
{
    private readonly LongCatViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;

    public LongCatWindow(LongCatViewModel viewModel, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;
        DataContext = _viewModel;
    }

    private void OpenCameraControl_Click(object sender, RoutedEventArgs e)
    {
        var flipPixWindow = _serviceProvider.GetRequiredService<FlipPixWindow>();
        flipPixWindow.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }
}
