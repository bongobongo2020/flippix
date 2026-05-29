using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Windows;

public partial class OllamaWindow : Window
{
    private readonly OllamaViewModel? _viewModel;

    public OllamaWindow()
    {
        InitializeComponent();
    }

    public OllamaWindow(OllamaViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
