using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// The message box behind the System.Windows.MessageBox shim: caption, icon, text and the button
/// row of a Windows message box, laid out the same way (icon left, buttons right-aligned below).
/// </summary>
public sealed class MessageBoxWindow : Window
{
    private MessageBoxResult _result;

    private MessageBoxWindow(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        Title = string.IsNullOrEmpty(caption) ? "FlipPix" : caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        MinWidth = 320;
        MaxWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        _result = buttons is MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        if (buttons == MessageBoxButton.YesNo) _result = MessageBoxResult.No;

        var glyph = icon switch
        {
            MessageBoxImage.Error => ("⛔", "#DC2626"),
            MessageBoxImage.Warning => ("⚠", "#D97706"),
            MessageBoxImage.Question => ("?", "#2563EB"),
            MessageBoxImage.Information => ("ℹ", "#2563EB"),
            _ => (string.Empty, "#000000")
        };

        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };
        if (glyph.Item1.Length > 0)
        {
            var iconBlock = new TextBlock
            {
                Text = glyph.Item1,
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse(glyph.Item2),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 16, 0)
            };
            DockPanel.SetDock(iconBlock, Dock.Left);
            body.Children.Add(iconBlock);
        }
        body.Children.Add(new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        void Add(string label, MessageBoxResult result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button
            {
                Content = label,
                MinWidth = 84,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = isDefault,
                IsCancel = isCancel
            };
            b.Click += (_, _) => { _result = result; Close(); };
            buttonRow.Children.Add(b);
        }

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                Add("OK", MessageBoxResult.OK, isDefault: true);
                Add("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                Add("No", MessageBoxResult.No, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Yes", MessageBoxResult.Yes, isDefault: true);
                Add("No", MessageBoxResult.No);
                Add("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
            default:
                Add("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
        }

        var footer = new Border
        {
            Background = Brush.Parse("#F0F0F0"),
            Padding = new Thickness(16, 10),
            Child = buttonRow
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && buttons != MessageBoxButton.YesNo) Close();
        };
    }

    public static async Task<MessageBoxResult> ShowAsync(
        Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        var box = new MessageBoxWindow(text, caption, buttons, icon);
        if (owner != null && owner.IsVisible)
        {
            await box.ShowDialog(owner);
        }
        else
        {
            box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            box.Closed += (_, _) => closed.TrySetResult();
            box.Show();
            await closed.Task;
        }
        return box._result;
    }
}
