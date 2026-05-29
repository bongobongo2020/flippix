using System;
using Avalonia.Threading;

namespace FlipPix.UI.Linux.Services;

public static class ClipboardService
{
    public static void SetText(string text)
    {
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow)
                    : null;
                if (topLevel?.Clipboard != null)
                    await topLevel.Clipboard.SetTextAsync(text);
            }
            catch { }
        });
    }

    public static string? GetText() => string.Empty;
}
