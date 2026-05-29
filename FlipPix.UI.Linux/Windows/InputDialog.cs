using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Simple text input dialog for cross-platform use (replaces Microsoft.VisualBasic.Interaction.InputBox)
/// </summary>
public class InputDialog : Window
{
    private readonly TextBox _textBox;
    private string _result = string.Empty;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 200;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _textBox = new TextBox { Text = defaultValue };

        var layout = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 15
        };

        layout.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        layout.Children.Add(_textBox);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        var okBtn = new Button { Content = "OK" };
        okBtn.Click += (_, _) =>
        {
            _result = _textBox.Text ?? string.Empty;
            Close(_result);
        };

        var cancelBtn = new Button { Content = "Cancel" };
        cancelBtn.Click += (_, _) => Close(string.Empty);

        buttonRow.Children.Add(okBtn);
        buttonRow.Children.Add(cancelBtn);
        layout.Children.Add(buttonRow);

        Content = layout;
    }
}
