using Avalonia;
using Avalonia.Media;
using System;

namespace FlipPix.UI.Linux;

class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // Segoe UI's metrics (the WPF build's font), then colour emoji for the tab and
                // button labels, then DejaVu for the symbols neither has.
                DefaultFamilyName = "avares://FlipPix.UI.Linux/Assets/Fonts#Selawik",
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") },
                    new FontFallback { FontFamily = new FontFamily("DejaVu Sans") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans Symbols 2") },
                }
            });

        // FLIPPIX_AVALONIA_LOG=1 prints binding/layout warnings to stderr (porting diagnostics).
        if (IsEnabled("FLIPPIX_AVALONIA_LOG")) Avalonia.Logging.Logger.Sink = new StderrLogSink();
        else builder = builder.LogToTrace();

        return OperatingSystem.IsLinux() ? ConfigureLinux(builder) : builder;
    }

    /// <summary>
    /// Linux desktops vary far more than Windows does, so the rendering path is configurable.
    /// Defaults suit a normal Arch install with working Mesa; the environment variables are
    /// escape hatches for the setups where that assumption breaks.
    /// </summary>
    private static AppBuilder ConfigureLinux(AppBuilder builder)
    {
        // GPU rendering is the default, but some driver/compositor combinations (notably
        // older Mesa on Wayland, and VMs with llvmpipe) render black or crash on GL init.
        var software = IsEnabled("FLIPPIX_SOFTWARE_RENDER");

        var renderingModes = software
            ? new[] { X11RenderingMode.Software }
            : new[] { X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software };

        builder = builder.With(new X11PlatformOptions
        {
            RenderingMode = renderingModes,

            // Global-menu integration; harmless on desktops that do not export a menu bar.
            UseDBusMenu = true,

            // Lets the compositor place file dialogs and notifications correctly.
            UseDBusFilePicker = true,

            // Fractional scaling is common on Wayland-via-XWayland sessions.
            EnableIme = true,
        });

        // Avalonia 11.2 ships no Wayland backend, so a Wayland session runs this through
        // XWayland. That is transparent apart from fractional scaling, which the X11
        // backend reads from the usual GDK/Qt scaling variables.
        return builder;
    }

    private static bool IsEnabled(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value)
            && !value.Equals("0", StringComparison.Ordinal)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Avalonia warnings (bindings, properties) to stderr, for FLIPPIX_AVALONIA_LOG=1.</summary>
    private sealed class StderrLogSink : Avalonia.Logging.ILogSink
    {
        public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) => level >= Avalonia.Logging.LogEventLevel.Warning;

        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate)
            => Console.Error.WriteLine($"[{level}] {area}: {messageTemplate} ({source})");

        public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            var text = messageTemplate;
            foreach (var v in propertyValues)
            {
                var start = text.IndexOf('{');
                var end = start >= 0 ? text.IndexOf('}', start) : -1;
                if (end < 0) break;
                text = text[..start] + v + text[(end + 1)..];
            }
            Console.Error.WriteLine($"[{level}] {area}: {text} ({source})");
        }
    }
}
