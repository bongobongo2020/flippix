using Avalonia.Interactivity;

namespace FlipPix.UI.Linux.Windows;

/// <summary>First run: a ComfyUI installed on this machine, or a server elsewhere on the network.</summary>
public partial class SetupChoiceWindow : DialogWindow
{
    public SetupChoiceWindow()
    {
        InitializeComponent();
    }

    public bool IsLocalSelected { get; private set; }
    public bool IsRemoteSelected { get; private set; }

    private void LocalButton_Click(object? sender, RoutedEventArgs e)
    {
        IsLocalSelected = true;
        IsRemoteSelected = false;
        DialogResult = true;
    }

    private void RemoteButton_Click(object? sender, RoutedEventArgs e)
    {
        IsLocalSelected = false;
        IsRemoteSelected = true;
        DialogResult = true;
    }
}
