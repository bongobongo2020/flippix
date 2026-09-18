using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.Controls
{
    /// <summary>
    /// Stands in for WPF's MediaElement, which Avalonia has no equivalent of: there is no
    /// in-process video renderer here, so a clip is shown as a poster frame plucked out of it
    /// by ffmpeg, with playback handed to whatever player the desktop has registered.
    ///
    /// Bind <see cref="Source"/> to the same property the WPF tab binds its MediaElement to
    /// (a path or a file:// URI). Everything else — grabbing the frame, the play and reveal
    /// buttons, the empty state — is handled here.
    /// </summary>
    public partial class VideoPreview : UserControl
    {
        public static readonly StyledProperty<object?> SourceProperty =
            AvaloniaProperty.Register<VideoPreview, object?>(nameof(Source));

        /// <summary>Path or file:// URI of the clip. Null or empty shows the placeholder.</summary>
        public object? Source
        {
            get => GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public static readonly StyledProperty<string> PlaceholderTextProperty =
            AvaloniaProperty.Register<VideoPreview, string>(
                nameof(PlaceholderText), "No video yet");

        /// <summary>Shown while there is no clip — the WPF tab's own empty-state wording.</summary>
        public string PlaceholderText
        {
            get => GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        /// <summary>
        /// Seconds into the clip the poster frame is taken from. A little past zero, because
        /// the first frame of a generated clip is often a fade-in from black.
        /// </summary>
        public static readonly StyledProperty<double> PosterAtSecondsProperty =
            AvaloniaProperty.Register<VideoPreview, double>(nameof(PosterAtSeconds), 0.5);

        public double PosterAtSeconds
        {
            get => GetValue(PosterAtSecondsProperty);
            set => SetValue(PosterAtSecondsProperty, value);
        }

        public static readonly StyledProperty<double> DurationSecondsProperty =
            AvaloniaProperty.Register<VideoPreview, double>(nameof(DurationSeconds));

        /// <summary>
        /// Length of the current clip, read off it by ffprobe. Set by this control, never by the
        /// caller: a scrub slider binds its Maximum to it, which is what WPF got for free from
        /// MediaElement.NaturalDuration.
        /// </summary>
        public double DurationSeconds
        {
            get => GetValue(DurationSecondsProperty);
            private set => SetValue(DurationSecondsProperty, value);
        }

        private Image? _poster;
        private TextBlock? _placeholder;
        private TextBlock? _fileName;
        private Border? _bar;
        private CancellationTokenSource? _posterCts;
        private DispatcherTimer? _seekDebounce;
        private string? _currentPath;

        public VideoPreview()
        {
            InitializeComponent();

            _poster = this.FindControl<Image>("PART_Poster");
            _placeholder = this.FindControl<TextBlock>("PART_Placeholder");
            _fileName = this.FindControl<TextBlock>("PART_FileName");
            _bar = this.FindControl<Border>("PART_Bar");

            if (this.FindControl<Button>("PART_Open") is { } open)
                open.Click += (_, _) => { if (_currentPath != null) DesktopIntegration.OpenFile(_currentPath); };
            if (this.FindControl<Button>("PART_Reveal") is { } reveal)
                reveal.Click += (_, _) => { if (_currentPath != null) DesktopIntegration.RevealInFileManager(_currentPath); };

            Apply(null);
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SourceProperty)
            {
                Apply(ToPath(change.GetNewValue<object?>()));
            }
            else if (change.Property == PosterAtSecondsProperty)
            {
                // The tabs bind this to a playhead, so it moves as the user scrubs: re-grab the
                // frame rather than the whole control, and only once the drag settles.
                _seekDebounce?.Stop();
                if (_currentPath == null) return;
                _seekDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                _seekDebounce.Tick -= OnSeekSettled;
                _seekDebounce.Tick += OnSeekSettled;
                _seekDebounce.Start();
            }
            else if (change.Property == PlaceholderTextProperty && _placeholder != null && _currentPath == null)
            {
                _placeholder.Text = PlaceholderText;
            }
        }

        private void OnSeekSettled(object? sender, EventArgs e)
        {
            _seekDebounce?.Stop();
            if (_currentPath == null) return;

            _posterCts?.Cancel();
            _posterCts = new CancellationTokenSource();
            _ = LoadPosterAsync(_currentPath, _posterCts.Token);
        }

        /// <summary>
        /// The tabs hand this control whatever their ViewModel already exposes: a plain path,
        /// a file:// URI string, or a Uri built for WPF's MediaElement.
        /// </summary>
        private static string? ToPath(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case Uri uri:
                    return uri.IsFile ? uri.LocalPath : uri.ToString();
                case string s when !string.IsNullOrWhiteSpace(s):
                    if (Uri.TryCreate(s, UriKind.Absolute, out var parsed) && parsed.IsFile)
                        return parsed.LocalPath;
                    return s;
                default:
                    return null;
            }
        }

        private void Apply(string? path)
        {
            _posterCts?.Cancel();
            _currentPath = path != null && File.Exists(path) ? path : null;

            if (_poster != null)
            {
                _poster.Source = null;
                _poster.IsVisible = false;
            }
            if (_bar != null) _bar.IsVisible = _currentPath != null;
            if (_fileName != null) _fileName.Text = _currentPath != null ? Path.GetFileName(_currentPath) : string.Empty;
            if (_placeholder != null)
            {
                _placeholder.IsVisible = true;
                _placeholder.Text = _currentPath == null ? PlaceholderText : "🎞 " + Path.GetFileName(_currentPath);
            }

            DurationSeconds = 0;
            if (_currentPath == null) return;

            _posterCts = new CancellationTokenSource();
            _ = LoadPosterAsync(_currentPath, _posterCts.Token);
            _ = LoadDurationAsync(_currentPath, _posterCts.Token);
        }

        private async Task LoadDurationAsync(string path, CancellationToken token)
        {
            try
            {
                var seconds = await Task.Run(() => Probe(path), token);
                if (token.IsCancellationRequested || seconds <= 0) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) DurationSeconds = seconds;
                });
            }
            catch
            {
                // No ffprobe, or an unreadable container: a scrub range of 0 is the honest answer.
            }
        }

        private static double Probe(string path)
        {
            var ffprobe = MediaTools.FFprobePath;
            if (string.IsNullOrEmpty(ffprobe)) return 0;

            var psi = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
                     {
                         "-v", "error",
                         "-show_entries", "format=duration",
                         "-of", "default=noprint_wrappers=1:nokey=1",
                         path
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc == null) return 0;
            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(true); } catch { }
                return 0;
            }

            return double.TryParse(stdout.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : 0;
        }

        /// <summary>
        /// Writes the frame currently on show to a PNG and returns its path, at the clip's own
        /// resolution rather than the on-screen size. This is what a tab's "use this frame"
        /// button calls; WPF rendered the live MediaElement through a VisualBrush instead.
        /// </summary>
        public Task<string?> CaptureFrameAsync()
        {
            var path = _currentPath;
            if (path == null) return Task.FromResult<string?>(null);
            var at = PosterAtSeconds;
            return Task.Run(() => GrabFrame(path, at, CancellationToken.None, scaled: false));
        }

        private async Task LoadPosterAsync(string path, CancellationToken token)
        {
            try
            {
                var frame = await Task.Run(() => GrabFrame(path, PosterAtSeconds, token), token);
                if (token.IsCancellationRequested || frame == null) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        var bitmap = new Bitmap(frame);
                        if (_poster != null)
                        {
                            _poster.Source = bitmap;
                            _poster.IsVisible = true;
                        }
                        if (_placeholder != null) _placeholder.IsVisible = false;
                    }
                    catch
                    {
                        // A poster is a nicety; the play button still works without it.
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Source changed again before the frame came back.
            }
            catch
            {
                // ffmpeg missing or unhappy with the file: keep the text stand-in.
            }
        }

        /// <summary>
        /// Writes one frame to the cache dir and returns its path, or null if ffmpeg is not
        /// installed or the clip yielded nothing. Cached by path + write time, so flipping
        /// between clips does not re-run ffmpeg every time.
        /// </summary>
        private static string? GrabFrame(string path, double atSeconds, CancellationToken token,
                                        bool scaled = true)
        {
            var ffmpeg = MediaTools.FFmpegPath;
            if (string.IsNullOrEmpty(ffmpeg)) return null;

            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            var key = Convert.ToHexString(
                MD5.HashData(Encoding.UTF8.GetBytes($"{path}|{stamp}|{atSeconds}|{scaled}")))[..16];
            var outPath = Path.Combine(UserPaths.CacheDir, "posters", key + ".png");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            if (File.Exists(outPath)) return outPath;

            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(atSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            if (scaled)
            {
                // The on-screen poster only needs to be preview-sized; a captured frame that is
                // going back through a workflow must keep the clip's own resolution.
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("scale=640:-2");
            }
            psi.ArgumentList.Add(outPath);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            token.ThrowIfCancellationRequested();
            return File.Exists(outPath) ? outPath : null;
        }
    }
}
