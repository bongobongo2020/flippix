using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FlipPix.UI.Linux.Windows;

/// <summary>
/// Full-size image viewer. On a Chromebook it stands in for the OS image app, which Linux apps
/// can't launch (see FlipPix.UI.Services.ImageOpener).
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double ZoomStep = 1.25;
    private const double MinScale = 0.02;
    private const double MaxScale = 16;

    private readonly string _path;
    private Bitmap? _bitmap;
    private double _scale = 1;
    private Point _offset;
    private bool _fitMode = true;
    private Point? _dragStart;
    private Point _dragStartOffset;

    // Parameterless constructor for the XAML loader and designer.
    public ImageViewerWindow() : this("") { }

    public ImageViewerWindow(string path)
    {
        _path = path;
        InitializeComponent();
        Title = string.IsNullOrEmpty(path) ? "Image" : Path.GetFileName(path);
        KeyDown += OnKeyDown;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Viewport.Focus();
        if (string.IsNullOrEmpty(_path)) return;
        InfoText.Text = "Loading…";
        try
        {
            // Off the UI thread: the file may sit on a slow shared folder.
            _bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(_path);
                return new Bitmap(stream);
            });
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Couldn't open this image: {ex.Message}";
            return;
        }

        Picture.Source = _bitmap;
        Picture.Width = _bitmap.Size.Width;
        Picture.Height = _bitmap.Size.Height;
        Fit();
    }

    protected override void OnClosed(EventArgs e)
    {
        Picture.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnClosed(e);
    }

    // ── View state ────────────────────────────────────────────────────────────────────────

    private void Fit()
    {
        if (_bitmap == null) return;
        var view = Viewport.Bounds.Size;
        if (view.Width <= 0 || view.Height <= 0) return;
        // Shrink large images to fit; never blow small ones up past actual size.
        _scale = Math.Min(1, Math.Min(view.Width / _bitmap.Size.Width, view.Height / _bitmap.Size.Height));
        _fitMode = true;
        Center();
    }

    private void ActualSize(Point? anchor = null)
    {
        if (_bitmap == null) return;
        if (anchor is { } a)
            ZoomAround(a, 1 / _scale);
        else
        {
            _scale = 1;
            _fitMode = false;
            Center();
        }
    }

    private void Center()
    {
        var view = Viewport.Bounds.Size;
        _offset = new Point((view.Width - _bitmap!.Size.Width * _scale) / 2,
                            (view.Height - _bitmap.Size.Height * _scale) / 2);
        Apply();
    }

    /// <summary>Scales by <paramref name="factor"/> keeping the image point under
    /// <paramref name="anchor"/> (viewport coordinates) where it is.</summary>
    private void ZoomAround(Point anchor, double factor)
    {
        if (_bitmap == null) return;
        var newScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        var ratio = newScale / _scale;
        _offset = new Point(anchor.X - (anchor.X - _offset.X) * ratio,
                            anchor.Y - (anchor.Y - _offset.Y) * ratio);
        _scale = newScale;
        _fitMode = false;
        Apply();
    }

    private void ZoomAtCenter(double factor)
    {
        var view = Viewport.Bounds.Size;
        ZoomAround(new Point(view.Width / 2, view.Height / 2), factor);
    }

    private void Apply()
    {
        if (_bitmap == null) return;
        Picture.RenderTransform = new MatrixTransform(new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y));
        InfoText.Text = $"{Path.GetFileName(_path)}  ·  {_bitmap.PixelSize.Width}×{_bitmap.PixelSize.Height}  ·  {_scale * 100:0}%";
    }

    // ── Input ─────────────────────────────────────────────────────────────────────────────

    private void Viewport_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_fitMode) Fit();
    }

    private void Viewport_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ZoomAround(e.GetPosition(Viewport), Math.Pow(ZoomStep, e.Delta.Y));
        e.Handled = true;
    }

    private void Viewport_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(Viewport);
        _dragStartOffset = _offset;
        e.Pointer.Capture(Viewport);
        Viewport.Focus();
    }

    private void Viewport_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _bitmap == null) return;
        var p = e.GetPosition(Viewport);
        _offset = _dragStartOffset + (p - start);
        _fitMode = false;
        Apply();
    }

    private void Viewport_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    private void Viewport_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_fitMode) ActualSize(e.GetPosition(Viewport));
        else Fit();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.F or Key.D0 or Key.NumPad0: Fit(); break;
            case Key.D1 or Key.NumPad1: ActualSize(); break;
            case Key.OemPlus or Key.Add: ZoomAtCenter(ZoomStep); break;
            case Key.OemMinus or Key.Subtract: ZoomAtCenter(1 / ZoomStep); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => ZoomAtCenter(ZoomStep);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => ZoomAtCenter(1 / ZoomStep);
    private void Fit_Click(object? sender, RoutedEventArgs e) => Fit();
    private void Actual_Click(object? sender, RoutedEventArgs e) => ActualSize();
}
