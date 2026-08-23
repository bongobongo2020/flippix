using Avalonia;
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
            .WithInterFont()
            .LogToTrace();

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
}
