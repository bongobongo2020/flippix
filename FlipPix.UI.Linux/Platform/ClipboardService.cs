using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace FlipPix.UI.Services;

/// <summary>
/// The system clipboard for the System.Windows.Clipboard shim. WPF's clipboard is synchronous;
/// Avalonia's is async, so reads wait in a nested dispatcher frame (UI stays responsive).
/// </summary>
public static class ClipboardService
{
    private static Avalonia.Input.Platform.IClipboard? Clipboard =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? TopLevel.GetTopLevel(desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow)?.Clipboard
            : null;

    public static void SetText(string text)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            }
            catch { }
        });
    }

    public static string? GetText()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
                return Dispatcher.UIThread.InvokeAsync(ReadAsync).GetAwaiter().GetResult();

            string? result = null;
            var frame = new DispatcherFrame();
            ReadAsync().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) result = t.Result;
                frame.Continue = false;
            }, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadAsync() =>
        Clipboard is { } clipboard ? await clipboard.GetTextAsync() : null;
}
