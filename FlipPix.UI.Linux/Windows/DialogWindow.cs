using Avalonia.Controls;

namespace FlipPix.UI.Linux.Windows;

/// <summary>A window with WPF's DialogResult: set it and the window closes.</summary>
public class DialogWindow : Window
{
    public DialogWindow()
    {
        Icon = AppWindow.AppIcon.Value;
    }

    private bool? _dialogResult;

    public bool? DialogResult
    {
        get => _dialogResult;
        set
        {
            _dialogResult = value;
            Close(value);
        }
    }
}
