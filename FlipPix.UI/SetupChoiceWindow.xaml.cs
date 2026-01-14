using System;
using System.Windows;

namespace FlipPix.UI
{
    public partial class SetupChoiceWindow : Window
    {
        public bool IsLocalSelected { get; private set; }
        public bool IsRemoteSelected { get; private set; }

        public SetupChoiceWindow()
        {
            InitializeComponent();
        }

        private void LocalButton_Click(object sender, RoutedEventArgs e)
        {
            IsLocalSelected = true;
            IsRemoteSelected = false;
            DialogResult = true;
            Close();
        }

        private void RemoteButton_Click(object sender, RoutedEventArgs e)
        {
            IsLocalSelected = false;
            IsRemoteSelected = true;
            DialogResult = true;
            Close();
        }
    }
}