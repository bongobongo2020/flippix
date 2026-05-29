using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.ViewModels;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Linux.Windows;

public partial class ComfyUIFolderSetupWindow : Window
{
    public ComfyUIFolderSetupWindow() { InitializeComponent(); }

    public ComfyUIFolderSetupWindow(ComfyUIFolderSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, result) => Close(result);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
