// Cross-platform compatibility shims for WPF-specific APIs
// These replace System.Windows.* APIs with Linux-compatible equivalents

using System;
using System.IO;
using Avalonia.Threading;

// Provide CommandManager.InvalidateRequerySuggested() as a no-op
namespace System.Windows.Input
{
    public static class CommandManager
    {
        public static void InvalidateRequerySuggested() { /* No-op on Avalonia */ }
        public static event EventHandler? RequerySuggested { add { } remove { } }
    }
}

// Provide MessageBox shim that uses Avalonia message boxes
namespace System.Windows
{
    public enum MessageBoxButton { OK, OKCancel, YesNoCancel, YesNo }
    public enum MessageBoxImage { None, Error, Warning, Question, Information }
    public enum MessageBoxResult { None, OK, Cancel, Yes, No }
    public enum WindowState { Normal, Minimized, Maximized }

    public static class MessageBox
    {
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            // Show async message box via Avalonia dispatcher
            MessageBoxResult result = MessageBoxResult.OK;
            if (Dispatcher.UIThread.CheckAccess())
            {
                result = ShowAsync(text, caption, button, icon).GetAwaiter().GetResult();
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    result = await ShowAsync(text, caption, button, icon);
                }).GetAwaiter().GetResult();
            }
            return result;
        }

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button)
            => Show(text, caption, button, MessageBoxImage.None);

        public static MessageBoxResult Show(string text)
            => Show(text, "FlipPix", MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption)
            => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

        private static async System.Threading.Tasks.Task<MessageBoxResult> ShowAsync(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            try
            {
                var mboxIcon = icon switch
                {
                    MessageBoxImage.Error => MsBox.Avalonia.Enums.Icon.Error,
                    MessageBoxImage.Warning => MsBox.Avalonia.Enums.Icon.Warning,
                    MessageBoxImage.Question => MsBox.Avalonia.Enums.Icon.Question,
                    MessageBoxImage.Information => MsBox.Avalonia.Enums.Icon.Info,
                    _ => MsBox.Avalonia.Enums.Icon.None
                };

                var mboxButton = button switch
                {
                    MessageBoxButton.YesNo => global::MessageBox.Avalonia.Enums.ButtonEnum.YesNo,
                    MessageBoxButton.OKCancel => global::MessageBox.Avalonia.Enums.ButtonEnum.OkCancel,
                    MessageBoxButton.YesNoCancel => global::MessageBox.Avalonia.Enums.ButtonEnum.YesNoCancel,
                    _ => global::MessageBox.Avalonia.Enums.ButtonEnum.Ok
                };

                var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(caption, text, mboxButton, mboxIcon);
                var btnResult = await box.ShowAsync();

                return btnResult switch
                {
                    MsBox.Avalonia.Enums.ButtonResult.Yes => MessageBoxResult.Yes,
                    MsBox.Avalonia.Enums.ButtonResult.No => MessageBoxResult.No,
                    MsBox.Avalonia.Enums.ButtonResult.Cancel => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.OK
                };
            }
            catch
            {
                return MessageBoxResult.OK;
            }
        }
    }

    public static class SystemParameters
    {
        public static double PrimaryScreenWidth => Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Screens.Primary?.WorkingArea.Width ?? 1920
            : 1920;

        public static double PrimaryScreenHeight => Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Screens.Primary?.WorkingArea.Height ?? 1080
            : 1080;
    }

    public static class Application
    {
        public static ApplicationShim? Current { get; } = new ApplicationShim();
    }

    public class ApplicationShim
    {
        public DispatcherShim Dispatcher { get; } = new DispatcherShim();
    }

    public class DispatcherShim
    {
        public bool CheckAccess() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess();

        public void Invoke(Action action)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                action();
            else
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }

        public void BeginInvoke(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
        }

        public void BeginInvoke(Delegate method, DispatcherPriority priority, params object[] args)
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => method.DynamicInvoke(args));
        }
    }

    public enum DispatcherPriority
    {
        ApplicationIdle = 1,
        Background = 2,
        ContextIdle = 3,
        DataBind = 8,
        Input = 5,
        Loaded = 6,
        Normal,
        Render,
        Send,
        SystemIdle
    }
}

// BitmapImage shim - in Linux ViewModels we store string paths instead
// but some VMs still have BitmapImage type references
namespace System.Windows.Media.Imaging
{
    public class BitmapImage
    {
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public int DecodePixelWidth { get; set; }
        public int DecodePixelHeight { get; set; }
        public Uri? UriSource { get; set; }
        public BitmapCacheOption CacheOption { get; set; }
        public BitmapCreateOptions CreateOptions { get; set; }
        public Stream? StreamSource { get; set; }

        public Avalonia.Media.Imaging.Bitmap? AvaloniaBitmap { get; private set; }

        public void BeginInit() { }

        public void EndInit()
        {
            try
            {
                if (StreamSource != null)
                {
                    if (StreamSource.CanSeek)
                        StreamSource.Position = 0;
                    AvaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(StreamSource);
                }
                else if (UriSource != null)
                {
                    string localPath = UriSource.IsAbsoluteUri ? UriSource.LocalPath : UriSource.ToString();
                    if (File.Exists(localPath))
                        AvaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(localPath);
                }

                if (AvaloniaBitmap != null)
                {
                    PixelWidth = (int)AvaloniaBitmap.Size.Width;
                    PixelHeight = (int)AvaloniaBitmap.Size.Height;
                }
            }
            catch { }
        }

        public void Freeze() { }
    }

    public class BitmapDecoder
    {
        public System.Collections.ObjectModel.ReadOnlyCollection<BitmapFrame> Frames { get; } =
            new System.Collections.ObjectModel.ReadOnlyCollection<BitmapFrame>(new List<BitmapFrame>());

        public static BitmapDecoder Create(System.IO.Stream stream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
            => new BitmapDecoder();
        public static BitmapDecoder Create(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
            => new BitmapDecoder();
    }

    public class BitmapFrame : BitmapImage
    {
        public int Width { get; }
        public int Height { get; }
    }

    [Flags]
    public enum BitmapCreateOptions { None = 0, PreservePixelFormat = 1, IgnoreImageCache = 2, OnDemand = 4, DelayCreation = 8 }
    public enum BitmapCacheOption { Default, None, OnDemand, OnLoad }
}

namespace System.Windows
{
    public static class Clipboard
    {
        public static void SetText(string text)
        {
            FlipPix.UI.Linux.Services.ClipboardService.SetText(text);
        }

        public static string? GetText() => string.Empty;
        public static bool ContainsText() => false;
    }
}

namespace System.Windows.Threading
{
    // Allow using System.Windows.Threading without breaking
    public enum DispatcherPriority
    {
        ApplicationIdle = 1, Background = 2, ContextIdle = 3,
        DataBind = 8, Input = 5, Loaded = 6, Normal = 9,
        Render = 7, Send = 10, SystemIdle = 0
    }
}

// Windows.Forms stub for InputBox (used in FlipPixViewModel)
namespace Microsoft.VisualBasic
{
    public static class Interaction
    {
        public static string InputBox(string prompt, string title = "", string defaultResponse = "")
        {
            // On Linux, we return a simple dialog result
            // The actual implementation would need to show an Avalonia input dialog
            // For now return empty to avoid crashes
            string result = defaultResponse;
            try
            {
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    result = ShowInputBoxAsync(prompt, title, defaultResponse).GetAwaiter().GetResult();
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        result = await ShowInputBoxAsync(prompt, title, defaultResponse);
                    }).GetAwaiter().GetResult();
                }
            }
            catch { }
            return result;
        }

        private static async System.Threading.Tasks.Task<string> ShowInputBoxAsync(string prompt, string title, string defaultResponse)
        {
            // Show a simple text input dialog
            var dialog = new FlipPix.UI.Linux.Windows.InputDialog(title, prompt, defaultResponse);
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel != null)
                return await dialog.ShowDialog<string>(topLevel) ?? string.Empty;

            return defaultResponse;
        }
    }
}
