using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using FlipPix.UI.Services;

namespace FlipPix.UI.Linux.Controls;

/// <summary>
/// WPF's MediaElement for the ported windows: the same members the WPF code-behind drives
/// (Source, Play/Pause/Stop, Position, NaturalDuration, MediaOpened/MediaEnded/MediaFailed), so
/// the scrub bars, trim tracks and frame grabs port line for line.
///
/// Decoding is libvlc (apt: libvlc5 vlc-plugin-base) rendering into memory; each frame is
/// copied into a bitmap this control draws, so overlays in the XAML stay on top of the video and
/// sound goes to the desktop's mixer. Setting Source shows the first frame, as the WPF windows
/// arrange with their Play/Pause trick. Without libvlc the control still shows a frame grabbed
/// by ffmpeg, and Play hands the clip to the desktop's video player.
/// </summary>
public class VideoPreview : Control
{
    public static readonly StyledProperty<object?> SourceProperty =
        AvaloniaProperty.Register<VideoPreview, object?>(nameof(Source));
    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoPreview, Stretch>(nameof(Stretch), Stretch.Uniform);
    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<VideoPreview, bool>(nameof(IsMuted));
    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<VideoPreview, double>(nameof(Volume), 0.5);
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<VideoPreview, IBrush?>(nameof(Background));

    public static readonly RoutedEvent<RoutedEventArgs> MediaOpenedEvent =
        RoutedEvent.Register<VideoPreview, RoutedEventArgs>(nameof(MediaOpened), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<RoutedEventArgs> MediaEndedEvent =
        RoutedEvent.Register<VideoPreview, RoutedEventArgs>(nameof(MediaEnded), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<MediaFailedEventArgs> MediaFailedEvent =
        RoutedEvent.Register<VideoPreview, MediaFailedEventArgs>(nameof(MediaFailed), RoutingStrategies.Bubble);

    /// <summary>Path, file:// Uri or Uri of the clip; null clears.</summary>
    public object? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Stretch Stretch { get => GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    public bool IsMuted { get => GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public double Volume { get => GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public event EventHandler<RoutedEventArgs>? MediaOpened { add => AddHandler(MediaOpenedEvent, value); remove => RemoveHandler(MediaOpenedEvent, value); }
    public event EventHandler<RoutedEventArgs>? MediaEnded { add => AddHandler(MediaEndedEvent, value); remove => RemoveHandler(MediaEndedEvent, value); }
    public event EventHandler<MediaFailedEventArgs>? MediaFailed { add => AddHandler(MediaFailedEvent, value); remove => RemoveHandler(MediaFailedEvent, value); }

    public System.Windows.Duration NaturalDuration { get; private set; } = System.Windows.Duration.Automatic;
    public int NaturalVideoWidth { get; private set; }
    public int NaturalVideoHeight { get; private set; }
    public bool HasVideo => _bitmap != null;

    /// <summary>The playhead. Setting it seeks (and shows that frame while paused).</summary>
    public TimeSpan Position
    {
        get
        {
            if (_player != null && _media != null)
            {
                var t = _player.Time;
                if (t >= 0) _lastTime = t;
            }
            return TimeSpan.FromMilliseconds(_lastTime);
        }
        set
        {
            _lastTime = Math.Max(0, (long)value.TotalMilliseconds);
            if (_player == null || _media == null) return;
            if (_player.IsSeekable) _player.Time = _lastTime;
            else _pendingSeek = _lastTime;
        }
    }

    // ── playback state ──────────────────────────────────────────────────────────────

    private static readonly Lazy<LibVLC?> Vlc = new(CreateLibVlc);
    private MediaPlayer? _player;
    private Media? _media;
    private string? _path;
    private long _lastTime;
    private long _pendingSeek = -1;
    private bool _wantPlaying;
    private bool _primed;          // first frame shown after a new Source
    private bool _openedRaised;
    private bool _ended;
    private int _generation;

    // ── frame buffer (written by libvlc's thread, copied on the UI thread) ─────────────
    private readonly object _frameLock = new();
    private IntPtr _buffer;
    private int _bufWidth, _bufHeight, _bufPitch;
    private WriteableBitmap? _bitmap;
    private int _copyPending;

    // Delegates handed to native code must outlive it.
    private MediaPlayer.LibVLCVideoFormatCb? _formatCb;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCb;
    private MediaPlayer.LibVLCVideoLockCb? _lockCb;
    private MediaPlayer.LibVLCVideoUnlockCb? _unlockCb;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCb;

    static VideoPreview()
    {
        AffectsRender<VideoPreview>(StretchProperty, BackgroundProperty);
        SourceProperty.Changed.AddClassHandler<VideoPreview>((v, _) => v.OnSourceChanged());
        IsMutedProperty.Changed.AddClassHandler<VideoPreview>((v, _) => v.ApplyAudio());
        VolumeProperty.Changed.AddClassHandler<VideoPreview>((v, _) => v.ApplyAudio());
    }

    public static bool PlaybackAvailable => Vlc.Value != null;

    private static LibVLC? CreateLibVlc()
    {
        try
        {
            // Distros ship libvlc.so.5 without the unversioned name unless libvlc-dev is installed.
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, asm, path) =>
            {
                if (name == "libvlc" && NativeLibrary.TryLoad("libvlc.so.5", out var h)) return h;
                if (name == "libvlccore" && NativeLibrary.TryLoad("libvlccore.so.9", out var c)) return c;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { /* resolver already set */ }

        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            return new LibVLC("--quiet", "--no-video-title-show", "--no-stats", "--no-snapshot-preview",
                "--no-osd", "--avcodec-hw=none");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VideoPreview: libvlc unavailable, falling back to poster frames: {ex.Message}");
            return null;
        }
    }

    private static string? ToPath(object? source) => source switch
    {
        null => null,
        Uri { IsFile: true } u => u.LocalPath,
        Uri u => u.OriginalString,
        string s when s.StartsWith("file://", StringComparison.OrdinalIgnoreCase) => new Uri(s).LocalPath,
        string s => s,
        _ => source.ToString()
    };

    private void OnSourceChanged()
    {
        var path = ToPath(Source);
        _generation++;
        _openedRaised = false;
        _primed = false;
        _ended = false;
        _wantPlaying = false;
        _pendingSeek = -1;
        _lastTime = 0;
        NaturalDuration = System.Windows.Duration.Automatic;

        if (string.IsNullOrEmpty(path))
        {
            _path = null;
            StopPlayer();
            ClearFrame();
            return;
        }

        _path = path;
        if (!File.Exists(path) && !path.Contains("://"))
        {
            RaiseEvent(new MediaFailedEventArgs(MediaFailedEvent, this, new FileNotFoundException("Video not found", path)));
            ClearFrame();
            return;
        }

        if (Vlc.Value is { } vlc) Open(vlc, path);
        else _ = PosterFallbackAsync(path, _generation);
    }

    private void Open(LibVLC vlc, string path)
    {
        EnsurePlayer(vlc);
        _media?.Dispose();
        _media = path.Contains("://") && !path.StartsWith("file://")
            ? new Media(vlc, new Uri(path))
            : new Media(vlc, path, FromType.FromPath);
        var gen = _generation;
        _ = ParseAsync(_media, gen);

        // Prime: play muted until the first frame lands, then pause there — the frame the WPF
        // windows get with Play()+Pause() right after setting Source.
        _player!.Mute = true;
        _player.Play(_media);
    }

    private async Task ParseAsync(Media media, int gen)
    {
        try
        {
            await media.Parse(MediaParseOptions.ParseLocal, timeout: 5000);
            if (gen != _generation) return;
            var ms = media.Duration;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _generation) return;
                if (ms > 0) NaturalDuration = new System.Windows.Duration(TimeSpan.FromMilliseconds(ms));
                RaiseOpenedOnce();
            });
        }
        catch { }
    }

    private void RaiseOpenedOnce()
    {
        if (_openedRaised || !_primedOrParsed) return;
        _openedRaised = true;
        RaiseEvent(new RoutedEventArgs(MediaOpenedEvent, this));
    }

    // MediaOpened waits for both the duration and the first frame, so handlers can read both.
    private bool _primedOrParsed => NaturalDuration.HasTimeSpan && _primed;

    private void EnsurePlayer(LibVLC vlc)
    {
        if (_player != null) return;
        _player = new MediaPlayer(vlc) { EnableHardwareDecoding = false };

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _ended = true;
            _wantPlaying = false;
            RaiseEvent(new RoutedEventArgs(MediaEndedEvent, this));
        });
        _player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() =>
            RaiseEvent(new MediaFailedEventArgs(MediaFailedEvent, this, new IOException($"Could not play {_path}"))));
        _player.LengthChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.Length > 0 && !NaturalDuration.HasTimeSpan)
                NaturalDuration = new System.Windows.Duration(TimeSpan.FromMilliseconds(e.Length));
            RaiseOpenedOnce();
        });
    }

    // ── libvlc video callbacks (libvlc's thread) ───────────────────────────────────

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        // The size offered here can include decoder padding (1280x738 for a 720p H.264), so take
        // the display size from the media's video track when it is known: width × pixel aspect.
        var srcW = (int)width;
        var srcH = (int)height;
        try
        {
            foreach (var t in _media?.Tracks ?? Array.Empty<MediaTrack>())
            {
                if (t.TrackType != TrackType.Video || t.Data.Video.Width == 0 || t.Data.Video.Height == 0) continue;
                var v = t.Data.Video;
                var sar = v.SarNum > 0 && v.SarDen > 0 ? (double)v.SarNum / v.SarDen : 1.0;
                srcW = (int)Math.Round(v.Width * sar);
                srcH = (int)v.Height;
                break;
            }
        }
        catch { /* tracks not readable yet: keep libvlc's numbers */ }
        // A preview needs no more than ~1280px; libvlc scales for us.
        var scale = Math.Min(1.0, 1280.0 / Math.Max(1, srcW));
        var w = Math.Max(2, (int)(srcW * scale)) & ~1;
        var h = Math.Max(2, (int)(srcH * scale)) & ~1;

        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufWidth = w;
            _bufHeight = h;
            _bufPitch = w * 4;
            _buffer = Marshal.AllocHGlobal(_bufPitch * h);
        }
        width = (uint)w;
        height = (uint)h;
        pitches = (uint)(w * 4);
        lines = (uint)h;

        Dispatcher.UIThread.Post(() =>
        {
            NaturalVideoWidth = srcW;
            NaturalVideoHeight = srcH;
        });
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(_frameLock);
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    // lock/unlock run on the decoder thread; display may come from another one.
    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(_frameLock);

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (Interlocked.Exchange(ref _copyPending, 1) == 1) return;
        Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }

    // ── UI thread ──────────────────────────────────────────────────────────────────

    private void CopyFrame()
    {
        Interlocked.Exchange(ref _copyPending, 0);
        lock (_frameLock)
        {
            if (_buffer == IntPtr.Zero) return;
            if (_bitmap == null || _bitmap.PixelSize.Width != _bufWidth || _bitmap.PixelSize.Height != _bufHeight)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(_bufWidth, _bufHeight), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var fb = _bitmap.Lock();
            unsafe
            {
                var rowBytes = Math.Min(fb.RowBytes, _bufPitch);
                for (var y = 0; y < _bufHeight; y++)
                    Buffer.MemoryCopy((byte*)_buffer + y * _bufPitch, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, rowBytes);
            }
        }
        InvalidateVisual();

        if (!_primed)
        {
            _primed = true;
            if (!_wantPlaying)
            {
                // Hold the first frame, as WPF does after Play()+Pause().
                ThreadPool.QueueUserWorkItem(_ => _player?.SetPause(true));
            }
            ApplyAudio();
            if (_pendingSeek >= 0 && _player != null)
            {
                _player.Time = _pendingSeek;
                _pendingSeek = -1;
            }
            RaiseOpenedOnce();
        }
    }

    private void ApplyAudio()
    {
        if (_player == null) return;
        _player.Mute = IsMuted || !_primed;
        _player.Volume = (int)Math.Round(Math.Clamp(Volume, 0, 1) * 100);
    }

    public void Play()
    {
        if (_path == null) return;
        if (Vlc.Value == null)
        {
            DesktopIntegration.OpenFile(_path);
            return;
        }
        if (_player == null || _media == null) { Open(Vlc.Value, _path); }
        _wantPlaying = true;
        var player = _player!;
        var restart = _ended || player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error;
        _ended = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (restart)
            {
                player.Stop();
                player.Play(_media!);
                if (_lastTime > 0 && _lastTime < player.Length - 50) player.Time = _lastTime;
            }
            else player.SetPause(false);
        });
        Dispatcher.UIThread.Post(ApplyAudio);
    }

    public void Pause()
    {
        _wantPlaying = false;
        var player = _player;
        if (player == null) return;
        ThreadPool.QueueUserWorkItem(_ => player.SetPause(true));
    }

    /// <summary>WPF's Stop: back to the start, paused on the first frame.</summary>
    public void Stop()
    {
        _wantPlaying = false;
        _lastTime = 0;
        var player = _player;
        if (player == null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            player.SetPause(true);
            if (player.IsSeekable) player.Time = 0;
        });
    }

    public void Close() => SetCurrentValue(SourceProperty, null);

    private void StopPlayer()
    {
        var player = _player;
        if (player != null) ThreadPool.QueueUserWorkItem(_ => player.Stop());
    }

    private void ClearFrame()
    {
        lock (_frameLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }
        InvalidateVisual();
    }

    /// <summary>No libvlc: a frame from ffmpeg, and the duration from ffprobe.</summary>
    private async Task PosterFallbackAsync(string path, int gen)
    {
        try
        {
            var ffmpeg = MediaTools.FFmpegPath;
            if (ffmpeg == null) return;
            var png = Path.Combine(Path.GetTempPath(), $"flippix-poster-{Guid.NewGuid():N}.png");
            var psi = new ProcessStartInfo(ffmpeg, $"-v error -ss 0.3 -i \"{path}\" -frames:v 1 -vf scale='min(1280,iw)':-2 -y \"{png}\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { if (p != null) await p.WaitForExitAsync(); }
            double seconds = 0;
            if (MediaTools.FFprobePath is { } ffprobe)
            {
                var pi = new ProcessStartInfo(ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = Process.Start(pi);
                if (p != null)
                {
                    var text = await p.StandardOutput.ReadToEndAsync();
                    double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds);
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (gen != _generation) return;
                if (File.Exists(png))
                {
                    using var fs = File.OpenRead(png);
                    var bmp = new Bitmap(fs);
                    _posterBitmap = bmp;
                    NaturalVideoWidth = bmp.PixelSize.Width;
                    NaturalVideoHeight = bmp.PixelSize.Height;
                    try { File.Delete(png); } catch { }
                }
                if (seconds > 0) NaturalDuration = new System.Windows.Duration(TimeSpan.FromSeconds(seconds));
                _primed = true;
                InvalidateVisual();
                RaiseOpenedOnce();
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"VideoPreview poster failed: {ex.Message}");
        }
    }

    private Bitmap? _posterBitmap;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background != null) context.FillRectangle(Background, bounds);

        Bitmap? frame;
        lock (_frameLock) frame = (Bitmap?)_bitmap ?? _posterBitmap;
        if (frame == null) return;

        var src = new Size(frame.PixelSize.Width, frame.PixelSize.Height);
        var dest = Stretch switch
        {
            Stretch.Fill => bounds,
            Stretch.None => CenterRect(bounds, src),
            Stretch.UniformToFill => CenterRect(bounds, src * Math.Max(bounds.Width / src.Width, bounds.Height / src.Height)),
            _ => CenterRect(bounds, src * Math.Min(bounds.Width / src.Width, bounds.Height / src.Height))
        };
        using (context.PushClip(bounds))
            context.DrawImage(frame, new Rect(src), dest);
    }

    private static Rect CenterRect(Rect bounds, Size size) =>
        new((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Like MediaElement: take the video's aspect when unconstrained, else fill what we are given.
        if (NaturalVideoWidth <= 0 || NaturalVideoHeight <= 0) return new Size();
        var w = double.IsInfinity(availableSize.Width) ? NaturalVideoWidth : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? w * NaturalVideoHeight / NaturalVideoWidth : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Leaving the tab: stop sound, keep the frame for when it comes back.
        if (_wantPlaying) Pause();
    }
}

/// <summary>WPF's ExceptionRoutedEventArgs for MediaFailed.</summary>
public class MediaFailedEventArgs : RoutedEventArgs
{
    public MediaFailedEventArgs(RoutedEvent e, object source, Exception error) : base(e, source) => ErrorException = error;
    public Exception ErrorException { get; }
}
