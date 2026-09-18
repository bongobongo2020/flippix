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
            // The ported WPF call sites expect a blocking dialog. Avalonia's dialogs only
            // complete while the UI thread pumps messages, so we cannot simply block on the
            // task: on the UI thread that deadlocks, and off it the old code read `result`
            // before the dialog had closed. Handle the two threads separately.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                // Background thread: marshal over and genuinely wait for the dialog to close.
                var operation = Dispatcher.UIThread.InvokeAsync(() => ShowAsync(text, caption, button, icon));
                return operation.GetAwaiter().GetResult();
            }

            // UI thread: run a nested message loop so the dialog can render and respond
            // while this call appears synchronous to the caller.
            var result = MessageBoxResult.OK;
            var frame = new DispatcherFrame();

            _ = ShowAsync(text, caption, button, icon).ContinueWith(
                task =>
                {
                    if (task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        result = task.Result;
                    frame.Continue = false;
                },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);

            Dispatcher.UIThread.PushFrame(frame);
            return result;
        }

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button)
            => Show(text, caption, button, MessageBoxImage.None);

        /// <summary>
        /// WPF's overload taking a default result. Avalonia's dialogs have no notion of a
        /// pre-selected button, so the value is accepted and ignored.
        /// </summary>
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
            => Show(text, caption, button, icon);

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

        public void BeginInvoke(Action action, System.Windows.Threading.DispatcherPriority priority = System.Windows.Threading.DispatcherPriority.Normal)
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
        }

        public void BeginInvoke(Delegate method, System.Windows.Threading.DispatcherPriority priority, params object[] args)
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => method.DynamicInvoke(args));
        }

        public System.Threading.Tasks.Task InvokeAsync(Action action)
            => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();

        public System.Threading.Tasks.Task InvokeAsync(Action action, System.Windows.Threading.DispatcherPriority priority)
            => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();

        public System.Threading.Tasks.Task<T> InvokeAsync<T>(Func<T> function)
            => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(function).GetTask();
    }

    // DispatcherPriority is declared in System.Windows.Threading, as in WPF.

}

// The System.Windows.Media.Imaging types now live in Compat/WpfImaging.cs, backed by
// ImageSharp so crop/scale/encode actually work rather than returning empty stubs.

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
    /// <summary>
    /// WPF's DispatcherTimer under its original name. Avalonia ships the same Interval/Tick/
    /// Start/Stop surface in Avalonia.Threading, so this only re-homes the type.
    /// </summary>
    public class DispatcherTimer
    {
        private readonly Avalonia.Threading.DispatcherTimer _inner = new();

        public DispatcherTimer() { }

        public DispatcherTimer(DispatcherPriority priority) { }

        public TimeSpan Interval
        {
            get => _inner.Interval;
            set => _inner.Interval = value;
        }

        public bool IsEnabled
        {
            get => _inner.IsEnabled;
            set => _inner.IsEnabled = value;
        }

        public event EventHandler? Tick
        {
            add => _inner.Tick += value;
            remove => _inner.Tick -= value;
        }

        public void Start() => _inner.Start();

        public void Stop() => _inner.Stop();
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
