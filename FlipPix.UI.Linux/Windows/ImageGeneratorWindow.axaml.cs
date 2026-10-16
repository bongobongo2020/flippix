using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Controls;
using FlipPix.UI.Linux.Windows;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using SixLabors.ImageSharp.PixelFormats;
using Color = Avalonia.Media.Color;

namespace FlipPix.UI;

/// <summary>Port of FlipPix.UI/ImageGeneratorWindow.xaml.cs.</summary>
public partial class ImageGeneratorWindow : AppWindow
{
    private readonly ImageGeneratorViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly WindowPositionService _windowPositionService;

    // Qwen Edit base-scene video: scrub-bar position tracking (slider ↔ player)
    private DispatcherTimer? _qwenBasePosTimer;
    private bool _qwenBaseUserScrubbing;
    private bool _qwenBaseIsPlaying;

    /// <summary>For the XAML loader and previewer only.</summary>
    public ImageGeneratorWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _settingsService = null!;
        _windowPositionService = null!;
    }

    public ImageGeneratorWindow(ImageGeneratorViewModel viewModel, SettingsService settingsService, WindowPositionService windowPositionService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _settingsService = settingsService;
        _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
        Opened += OnLoaded;

        if (_viewModel.Analyzer != null)
            _viewModel.Analyzer.QueueItemAdded += OnAnalyzerQueueItemAdded;

        // WPF caught the scrub-bar click in PreviewMouseLeftButtonUp, before the slider handled it.
        // Avalonia's Slider marks the release handled, so listen for handled events too.
        QwenBaseScrubSlider.PointerReleased -= QwenBaseScrub_Click;
        QwenBaseScrubSlider.AddHandler(PointerReleasedEvent, QwenBaseScrub_Click,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        EnsureOnScreen();
        if (MaskInkCanvas != null)
        {
            // Semi-transparent red strokes at the brush size, as the WPF DrawingAttributes.
            MaskInkCanvas.StrokeBrush = new SolidColorBrush(Color.FromArgb(180, 220, 20, 20));
            MaskInkCanvas.BrushSize = _viewModel.InpaintEditor.BrushSize;
        }
    }

    /// <summary>The queue border flashes green and fades back (WPF's QueueFlashAnimation).</summary>
    private void OnAnalyzerQueueItemAdded()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (AnalyzerQueueBorder == null) return;
            var from = Color.Parse("#28A745");
            var to = Color.Parse("#DEE2E6");
            var started = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1.0, (DateTime.UtcNow - started).TotalSeconds);
                byte Lerp(byte a, byte b) => (byte)(a + (b - a) * t);
                AnalyzerQueueBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(Lerp(from.R, to.R), Lerp(from.G, to.G), Lerp(from.B, to.B)));
                if (t >= 1.0) timer.Stop();
            };
            timer.Start();
        });
    }

    // ── Ideogram composition-region canvas (drag / resize) ──────────────
    private const double IdeogramMinRegionSize = 24;

    private static IdeogramRegion? RegionFromSender(object? sender) => (sender as Thumb)?.DataContext as IdeogramRegion;

    private void Region_DragStarted(object? sender, VectorEventArgs e)
    {
        if (RegionFromSender(sender) is { } region)
            _viewModel.Ideogram.SelectRegionCommand.Execute(region);
    }

    private void RegionMove_DragDelta(object? sender, VectorEventArgs e)
    {
        if (RegionFromSender(sender) is not { } region) return;
        double cw = _viewModel.Ideogram.CanvasWidth, ch = _viewModel.Ideogram.CanvasHeight;
        region.X = Clamp(region.X + e.Vector.X, 0, cw - region.Width);
        region.Y = Clamp(region.Y + e.Vector.Y, 0, ch - region.Height);
    }

    private void RegionResize_DragDelta(object? sender, VectorEventArgs e)
    {
        if (RegionFromSender(sender) is not { } region) return;
        double cw = _viewModel.Ideogram.CanvasWidth, ch = _viewModel.Ideogram.CanvasHeight;
        region.Width = Clamp(region.Width + e.Vector.X, IdeogramMinRegionSize, cw - region.X);
        region.Height = Clamp(region.Height + e.Vector.Y, IdeogramMinRegionSize, ch - region.Y);
    }

    private static double Clamp(double v, double min, double max)
        => max < min ? min : v < min ? min : v > max ? max : v;

    private void Header_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e) => Header_PointerPressed(sender, e);

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new SettingsWindow(_settingsService).ShowDialog(this);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to open settings: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private PhoneRemoteWindow? _phoneRemoteWindow;

    private void PhoneRemoteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_phoneRemoteWindow is { IsVisible: true })
        {
            _phoneRemoteWindow.Activate();
            return;
        }
        _phoneRemoteWindow = new PhoneRemoteWindow();
        _phoneRemoteWindow.Show(this);
    }

    private void OpenOutputImage_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: string imagePath } && File.Exists(imagePath))
                DesktopIntegration.OpenFile(imagePath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to open image: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void BrushSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaskInkCanvas != null) MaskInkCanvas.BrushSize = e.NewValue;
    }

    private void ClearMask_Click(object? sender, RoutedEventArgs e) => MaskInkCanvas?.Clear();

    private async void RunInpaint_Click(object? sender, RoutedEventArgs e)
    {
        var vm = _viewModel.InpaintEditor;
        if (!vm.CanGenerate) return;

        try
        {
            var combinedPath = BuildCombinedMaskedImage(vm.SourceImagePath);
            if (combinedPath == null)
            {
                System.Windows.MessageBox.Show(
                    "Failed to prepare the masked image. Please ensure a source image is loaded.",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            await vm.RunInpaintAsync(combinedPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Inpaint error: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Source RGB + the painted mask as alpha, saved as PNG. ComfyUI's LoadAndResizeImage
    /// clipspace convention: alpha 0 = painted (inpaint here), alpha 255 = keep.
    /// </summary>
    private string? BuildCombinedMaskedImage(string sourcePath)
    {
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(sourcePath);
            int w = image.Width, h = image.Height;
            var mask = MaskInkCanvas?.RenderMask(w, h); // 255 where painted; null = nothing painted

            image.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < h; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (var x = 0; x < w; x++)
                        row[x].A = (byte)(255 - (mask?[y * w + x] ?? 0));
                }
            });

            var tempPath = Path.Combine(Path.GetTempPath(), $"inpaint_masked_{DateTime.Now:yyyyMMddHHmmssfff}.png");
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, tempPath);
            return tempPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BuildCombinedMaskedImage error: {ex.Message}");
            return null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel?.Analyzer != null)
            _viewModel.Analyzer.QueueItemAdded -= OnAnalyzerQueueItemAdded;
        if (_viewModel is IDisposable disposable) disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }

    // ── Qwen Edit: base-scene video scrub + snap-a-frame ─────────────────

    private void QwenBaseVideoPlayer_MediaOpened(object? sender, RoutedEventArgs e)
    {
        QwenBaseVideoPlayer.Pause();
        _qwenBaseIsPlaying = false;

        if (QwenBaseVideoPlayer.NaturalDuration.HasTimeSpan)
            QwenBaseScrubSlider.Maximum = QwenBaseVideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        QwenBaseScrubSlider.Value = 0;

        if (_qwenBasePosTimer == null)
        {
            _qwenBasePosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _qwenBasePosTimer.Tick += (_, _) =>
            {
                var p = QwenBaseVideoPlayer;
                if (p?.Source == null || _qwenBaseUserScrubbing || !_qwenBaseIsPlaying) return;
                if (p.NaturalDuration.HasTimeSpan) QwenBaseScrubSlider.Value = p.Position.TotalSeconds;
            };
        }
        _qwenBasePosTimer.Start();
    }

    private void QwenBaseVideoPlayer_MediaEnded(object? sender, RoutedEventArgs e) => _qwenBaseIsPlaying = false;

    private void QwenBasePlay_Click(object? sender, RoutedEventArgs e)
    {
        QwenBaseVideoPlayer?.Play();
        _qwenBaseIsPlaying = QwenBaseVideoPlayer?.Source != null;
    }

    private void QwenBasePause_Click(object? sender, RoutedEventArgs e)
    {
        QwenBaseVideoPlayer?.Pause();
        _qwenBaseIsPlaying = false;
    }

    private void QwenBaseScrub_DragStarted(object? sender, VectorEventArgs e) => _qwenBaseUserScrubbing = true;

    private void QwenBaseScrub_DragCompleted(object? sender, VectorEventArgs e)
    {
        _qwenBaseUserScrubbing = false;
        SeekQwenBaseTo(QwenBaseScrubSlider.Value);
    }

    private void QwenBaseScrub_Click(object? sender, PointerReleasedEventArgs e) => SeekQwenBaseTo(QwenBaseScrubSlider.Value);

    private void SeekQwenBaseTo(double seconds)
    {
        var p = QwenBaseVideoPlayer;
        if (p?.Source == null) return;
        p.Pause();
        _qwenBaseIsPlaying = false;
        p.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>
    /// Snap &amp; Send: the frame under the playhead, at the video's native resolution, becomes
    /// the base image (image 3); then analyze and generate. The WPF build renders the paused
    /// MediaElement; here ffmpeg pulls the same frame straight from the file.
    /// </summary>
    private async void QwenBaseSnapAndSend_Click(object? sender, RoutedEventArgs e)
    {
        var player = QwenBaseVideoPlayer;
        if (player?.Source is not { } source) return;
        player.Pause();
        _qwenBaseIsPlaying = false;

        var videoPath = source is Uri u && u.IsFile ? u.LocalPath : source.ToString()!;
        if (videoPath.StartsWith("file://")) videoPath = new Uri(videoPath).LocalPath;
        var seconds = player.Position.TotalSeconds;

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "flippix_qwenedit");
            Directory.CreateDirectory(dir);
            var tempPath = Path.Combine(dir, $"basescene_snap_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
            if (!await ExtractFrameAsync(videoPath, seconds, tempPath)) return;

            _viewModel.QwenEdit.SetBaseImage(tempPath);
            await _viewModel.QwenEdit.AnalyzeAndGenerateAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"QwenBaseSnapAndSend error: {ex.Message}");
        }
    }

    internal static async Task<bool> ExtractFrameAsync(string videoPath, double seconds, string pngPath)
    {
        var ffmpeg = MediaTools.FFmpegPath;
        if (ffmpeg == null) return false;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { "-v", "error", "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                                  "-i", videoPath, "-frames:v", "1", "-y", pngPath })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return false;
        await p.WaitForExitAsync();
        return p.ExitCode == 0 && File.Exists(pngPath);
    }
}
