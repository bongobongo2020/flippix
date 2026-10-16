using System;
using FlipPix.UI.ViewModels;

namespace FlipPix.UI.Linux.Windows;

/// <summary>Port of FlipPix.UI/ComfyUIFolderSetupWindow.xaml.cs.</summary>
public partial class ComfyUIFolderSetupWindow : DialogWindow
{
    /// <summary>For the XAML loader and previewer only.</summary>
    public ComfyUIFolderSetupWindow()
    {
        InitializeComponent();
    }

    public ComfyUIFolderSetupWindow(ComfyUIFolderSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, result) => DialogResult = result;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable) disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }
}
