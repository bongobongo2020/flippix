using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Base for the windows the shared view models reach through DI by their WPF type
/// (ImageGeneratorWindow, VideoGeneratorWindow, VideoEnhanceWindow, StoryVideoWindow). It adds the
/// WPF-shaped members those view models use — WindowState as System.Windows.WindowState, and
/// Left/Top — and the header drag every FlipPix window has.
/// </summary>
public class AppWindow : Window
{
    public AppWindow()
    {
        Icon = AppIcon.Value;
    }

    internal static readonly Lazy<WindowIcon?> AppIcon = new(() =>
    {
        try
        {
            using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://FlipPix.UI.Linux/Assets/flippix.png"));
            return new WindowIcon(s);
        }
        catch { return null; }
    });

    /// <summary>WPF's WindowState, mapped onto Avalonia's.</summary>
    public new System.Windows.WindowState WindowState
    {
        get => base.WindowState switch
        {
            Avalonia.Controls.WindowState.Minimized => System.Windows.WindowState.Minimized,
            Avalonia.Controls.WindowState.Maximized or Avalonia.Controls.WindowState.FullScreen => System.Windows.WindowState.Maximized,
            _ => System.Windows.WindowState.Normal
        };
        set => base.WindowState = value switch
        {
            System.Windows.WindowState.Minimized => Avalonia.Controls.WindowState.Minimized,
            System.Windows.WindowState.Maximized => Avalonia.Controls.WindowState.Maximized,
            _ => Avalonia.Controls.WindowState.Normal
        };
    }

    public double Left
    {
        get => Position.X / Scaling;
        set => Position = new PixelPoint((int)(value * Scaling), Position.Y);
    }

    public double Top
    {
        get => Position.Y / Scaling;
        set => Position = new PixelPoint(Position.X, (int)(value * Scaling));
    }

    // Before a window is first shown its render scale is not known yet; the screen's is.
    private double Scaling => IsVisible && RenderScaling > 0 ? RenderScaling : Screens.Primary?.Scaling ?? 1.0;

    /// <summary>The coloured header strip drags the window, as on Windows.</summary>
    protected void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>
    /// Keeps a restored window on screen. A position saved on a large monitor can land a window
    /// partly off a Chromebook's smaller display; this pulls it back and shrinks it to fit.
    /// </summary>
    protected void EnsureOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var maxW = area.Width / scale;
        var maxH = area.Height / scale;
        if (Width > maxW) Width = maxW;
        if (Height > maxH) Height = maxH;

        var x = Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - (int)(Width * scale)));
        var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(Height * scale)));
        Position = new PixelPoint(x, y);
    }
}
