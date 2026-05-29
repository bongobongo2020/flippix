using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux;

public partial class SetupChoiceWindow : Window
{
    public bool IsLocalSelected { get; private set; }
    public bool IsRemoteSelected { get; private set; }
    public bool UserConfirmed { get; private set; }

    public SetupChoiceWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LocalButton_Click(object sender, RoutedEventArgs e)
    {
        IsLocalSelected = true;
        IsRemoteSelected = false;
        UserConfirmed = true;
        Close();
    }

    private void RemoteButton_Click(object sender, RoutedEventArgs e)
    {
        IsLocalSelected = false;
        IsRemoteSelected = true;
        UserConfirmed = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        UserConfirmed = false;
        Close();
    }
}
