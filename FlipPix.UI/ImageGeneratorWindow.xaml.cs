using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using FlipPix.Core.Services;

namespace FlipPix.UI
{
    public partial class ImageGeneratorWindow : Window
    {
        private readonly ImageGeneratorViewModel _viewModel;
        private readonly SettingsService _settingsService;
        private readonly WindowPositionService _windowPositionService;

        // Qwen Edit base-scene video: scrub-bar position tracking (slider ↔ media element)
        private System.Windows.Threading.DispatcherTimer? _qwenBasePosTimer;
        private bool _qwenBaseUserScrubbing;
        private bool _qwenBaseIsPlaying;

        public ImageGeneratorWindow(ImageGeneratorViewModel viewModel, SettingsService settingsService, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _settingsService = settingsService;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            // Subscribe to the Analyzer's QueueItemAdded event to trigger flash animation
            if (_viewModel.Analyzer != null)
            {
                _viewModel.Analyzer.QueueItemAdded += OnAnalyzerQueueItemAdded;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _windowPositionService.EnsureWindowVisible(this);
            InitializeMaskCanvas();
        }

        private void InitializeMaskCanvas()
        {
            if (MaskInkCanvas == null) return;

            var da = new DrawingAttributes
            {
                Color = System.Windows.Media.Color.FromArgb(180, 220, 20, 20), // semi-transparent red
                Width = _viewModel.InpaintEditor.BrushSize,
                Height = _viewModel.InpaintEditor.BrushSize,
                StylusTip = StylusTip.Ellipse,
                IsHighlighter = false,
                IgnorePressure = true,
            };
            MaskInkCanvas.DefaultDrawingAttributes = da;
            MaskInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
        }

        private void OnAnalyzerQueueItemAdded()
        {
            // Trigger the flash animation on the UI thread
            Dispatcher.Invoke(() =>
            {
                var flashAnimation = FindResource("QueueFlashAnimation") as Storyboard;
                flashAnimation?.Begin();
            });
        }

        // ── Ideogram composition-region canvas (drag / resize) ──────────────
        private const double IdeogramMinRegionSize = 24;

        private static void RegionFromSender(object sender, out FlipPix.UI.Models.IdeogramRegion? region)
            => region = (sender as System.Windows.Controls.Primitives.Thumb)?.DataContext as FlipPix.UI.Models.IdeogramRegion;

        private void Region_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            RegionFromSender(sender, out var region);
            if (region != null)
                _viewModel.Ideogram.SelectRegionCommand.Execute(region);
        }

        private void RegionMove_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            RegionFromSender(sender, out var region);
            if (region == null) return;
            double cw = _viewModel.Ideogram.CanvasWidth, ch = _viewModel.Ideogram.CanvasHeight;
            region.X = Clamp(region.X + e.HorizontalChange, 0, cw - region.Width);
            region.Y = Clamp(region.Y + e.VerticalChange, 0, ch - region.Height);
        }

        private void RegionResize_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            RegionFromSender(sender, out var region);
            if (region == null) return;
            double cw = _viewModel.Ideogram.CanvasWidth, ch = _viewModel.Ideogram.CanvasHeight;
            region.Width = Clamp(region.Width + e.HorizontalChange, IdeogramMinRegionSize, cw - region.X);
            region.Height = Clamp(region.Height + e.VerticalChange, IdeogramMinRegionSize, ch - region.Y);
        }

        private static double Clamp(double v, double min, double max)
            => max < min ? min : v < min ? min : v > max ? max : v;

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settingsService);
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open settings: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void OpenOutputImage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is System.Windows.Controls.Button button && button.Tag is string imagePath)
                {
                    if (File.Exists(imagePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = imagePath,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open image: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaskInkCanvas == null) return;
            var size = e.NewValue;
            var da = MaskInkCanvas.DefaultDrawingAttributes.Clone();
            da.Width = size;
            da.Height = size;
            MaskInkCanvas.DefaultDrawingAttributes = da;
        }

        private void ClearMask_Click(object sender, RoutedEventArgs e)
        {
            MaskInkCanvas?.Strokes.Clear();
        }

        private async void RunInpaint_Click(object sender, RoutedEventArgs e)
        {
            var vm = _viewModel.InpaintEditor;
            if (!vm.CanGenerate) return;

            try
            {
                // Build combined image (source RGB + mask alpha) on the UI thread
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
                System.Windows.MessageBox.Show(
                    $"Inpaint error: {ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private string? BuildCombinedMaskedImage(string sourcePath)
        {
            try
            {
                // 1. Load source image pixels
                var sourceBmp = new BitmapImage();
                sourceBmp.BeginInit();
                sourceBmp.CacheOption = BitmapCacheOption.OnLoad;
                sourceBmp.UriSource = new Uri(sourcePath, UriKind.Absolute);
                sourceBmp.EndInit();
                sourceBmp.Freeze();

                int srcW = sourceBmp.PixelWidth;
                int srcH = sourceBmp.PixelHeight;

                // Convert source to Bgra32
                var sourceConverted = new FormatConvertedBitmap(sourceBmp, PixelFormats.Bgra32, null, 0);
                var srcPixels = new byte[srcW * srcH * 4];
                sourceConverted.CopyPixels(srcPixels, srcW * 4, 0);

                // 2. Render InkCanvas strokes to a mask bitmap (white strokes on black)
                int canvasW = (int)MaskInkCanvas.ActualWidth;
                int canvasH = (int)MaskInkCanvas.ActualHeight;

                byte[] maskPixels;
                if (MaskInkCanvas.Strokes.Count > 0 && canvasW > 0 && canvasH > 0)
                {
                    // Draw black background + white strokes into a DrawingVisual
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, canvasW, canvasH));
                        var whiteAttr = new DrawingAttributes
                        {
                            Color = Colors.White,
                            StylusTip = StylusTip.Ellipse,
                            IgnorePressure = true,
                        };
                        foreach (var stroke in MaskInkCanvas.Strokes)
                        {
                            whiteAttr.Width = stroke.DrawingAttributes.Width;
                            whiteAttr.Height = stroke.DrawingAttributes.Height;
                            var cloned = stroke.Clone();
                            cloned.DrawingAttributes = whiteAttr.Clone();
                            cloned.Draw(dc);
                        }
                    }

                    var maskRender = new RenderTargetBitmap(canvasW, canvasH, 96, 96, PixelFormats.Pbgra32);
                    maskRender.Render(visual);

                    // Scale mask to source image dimensions
                    var scaledMask = new TransformedBitmap(maskRender,
                        new ScaleTransform((double)srcW / canvasW, (double)srcH / canvasH));
                    var maskConverted = new FormatConvertedBitmap(scaledMask, PixelFormats.Bgra32, null, 0);
                    maskPixels = new byte[srcW * srcH * 4];
                    maskConverted.CopyPixels(maskPixels, srcW * 4, 0);
                }
                else
                {
                    // No strokes — mask is all zeros (nothing to inpaint)
                    maskPixels = new byte[srcW * srcH * 4];
                }

                // 3. Combine: RGB from source + inverted mask as alpha.
                // ComfyUI's LoadAndResizeImage clipspace convention: alpha=0 = painted (inpaint here),
                // alpha=255 = original (keep). The node inverts alpha internally to get mask=1 for painted areas.
                var combined = new byte[srcW * srcH * 4];
                for (int i = 0; i < srcW * srcH; i++)
                {
                    combined[i * 4 + 0] = srcPixels[i * 4 + 0]; // B
                    combined[i * 4 + 1] = srcPixels[i * 4 + 1]; // G
                    combined[i * 4 + 2] = srcPixels[i * 4 + 2]; // R
                    // Invert mask: painted strokes (white→255) become alpha=0 (transparent),
                    // unpainted areas (black→0) become alpha=255 (opaque).
                    combined[i * 4 + 3] = (byte)(255 - maskPixels[i * 4 + 2]);
                }

                // 4. Save to temp PNG
                var tempPath = Path.Combine(Path.GetTempPath(), $"inpaint_masked_{DateTime.Now:yyyyMMddHHmmssfff}.png");
                var wb = new WriteableBitmap(srcW, srcH, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, srcW, srcH), combined, srcW * 4, 0);

                using var stream = File.Create(tempPath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(wb));
                encoder.Save(stream);

                return tempPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BuildCombinedMaskedImage error: {ex.Message}");
                return null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe from event
            if (_viewModel?.Analyzer != null)
            {
                _viewModel.Analyzer.QueueItemAdded -= OnAnalyzerQueueItemAdded;
            }

            // Dispose the ViewModel if it implements IDisposable
            if (_viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }

            DataContext = null;
            base.OnClosed(e);
        }

        // ── Qwen Edit: base-scene video scrub + snap-a-frame ─────────────────
        // The user uploads a video for the base scene, scrubs to a frame and clicks
        // "Snap & Send" to capture the current frame as a PNG and feed it in as the
        // base image (image 3). ScrubbingEnabled lets the paused MediaElement render
        // the exact frame at the scrub position.

        private void QwenBaseVideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            // Enter the paused state so ScrubbingEnabled can render frames on seek.
            QwenBaseVideoPlayer.Play();
            QwenBaseVideoPlayer.Pause();
            _qwenBaseIsPlaying = false;

            if (QwenBaseVideoPlayer.NaturalDuration.HasTimeSpan)
                QwenBaseScrubSlider.Maximum = QwenBaseVideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            QwenBaseScrubSlider.Value = 0;

            // Drive the slider from the playhead while the video is actually playing.
            if (_qwenBasePosTimer == null)
            {
                _qwenBasePosTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _qwenBasePosTimer.Tick += (_, _) =>
                {
                    var p = QwenBaseVideoPlayer;
                    if (p?.Source == null || _qwenBaseUserScrubbing || !_qwenBaseIsPlaying) return;
                    if (p.NaturalDuration.HasTimeSpan)
                        QwenBaseScrubSlider.Value = p.Position.TotalSeconds;
                };
            }
            _qwenBasePosTimer.Start();
        }

        private void QwenBaseVideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
            => _qwenBaseIsPlaying = false;

        private void QwenBasePlay_Click(object sender, RoutedEventArgs e)
        {
            QwenBaseVideoPlayer?.Play();
            _qwenBaseIsPlaying = QwenBaseVideoPlayer?.Source != null;
        }

        private void QwenBasePause_Click(object sender, RoutedEventArgs e)
        {
            QwenBaseVideoPlayer?.Pause();
            _qwenBaseIsPlaying = false;
        }

        private void QwenBaseScrub_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _qwenBaseUserScrubbing = true;

        private void QwenBaseScrub_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _qwenBaseUserScrubbing = false;
            SeekQwenBaseTo(QwenBaseScrubSlider.Value);
        }

        private void QwenBaseScrub_Click(object sender, MouseButtonEventArgs e)
            => SeekQwenBaseTo(QwenBaseScrubSlider.Value);

        private void SeekQwenBaseTo(double seconds)
        {
            var p = QwenBaseVideoPlayer;
            if (p?.Source == null) return;
            p.Pause();
            _qwenBaseIsPlaying = false;
            p.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        private async void QwenBaseSnapAndSend_Click(object sender, RoutedEventArgs e)
        {
            var player = QwenBaseVideoPlayer;
            if (player?.Source == null) return;

            // Make sure we are paused on the current frame so the render is stable.
            player.Pause();
            _qwenBaseIsPlaying = false;

            int w = player.NaturalVideoWidth;
            int h = player.NaturalVideoHeight;
            if (w <= 0 || h <= 0)
            {
                w = (int)player.ActualWidth;
                h = (int)player.ActualHeight;
            }
            if (w <= 0 || h <= 0) return;

            try
            {
                // Render the displayed frame at the video's native resolution via a
                // VisualBrush so we don't lose quality to the on-screen layout size.
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    var brush = new VisualBrush(player) { Stretch = Stretch.Fill };
                    dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
                }
                rtb.Render(dv);

                var dir = Path.Combine(Path.GetTempPath(), "flippix_qwenedit");
                Directory.CreateDirectory(dir);
                var tempPath = Path.Combine(dir, $"basescene_snap_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");

                using (var stream = File.Create(tempPath))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    encoder.Save(stream);
                }

                _viewModel.QwenEdit.SetBaseImage(tempPath);

                // Snap & Send: automatically analyze the three images and, once the
                // edit prompt is ready, run generation — no extra clicks needed.
                await _viewModel.QwenEdit.AnalyzeAndGenerateAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QwenBaseSnapAndSend error: {ex.Message}");
            }
        }
    }
}
