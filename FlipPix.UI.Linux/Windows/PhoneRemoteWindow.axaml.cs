using Avalonia.Controls;
using FlipPix.UI.Services;

namespace FlipPix.UI.Linux.Windows;

/// <summary>Turns the phone remote on and off and shows the code a phone pairs with.</summary>
public partial class PhoneRemoteWindow : Window
{
    public PhoneRemoteWindow()
    {
        InitializeComponent();
        DataContext = PhoneRemote.Host;
    }
}
