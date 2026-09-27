using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.Windows;

/// <summary>Turns the phone remote on and off and shows the code a phone pairs with.</summary>
public partial class PhoneRemoteWindow : Window
{
    public PhoneRemoteWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = PhoneRemote.Host;
    }
}
