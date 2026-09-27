using System.Windows;
using FlipPix.UI.Services;

namespace FlipPix.UI
{
    /// <summary>Turns the phone remote on and off and shows the code a phone pairs with.</summary>
    public partial class PhoneRemoteWindow : Window
    {
        public PhoneRemoteWindow()
        {
            InitializeComponent();
            DataContext = PhoneRemote.Host;
        }
    }
}
