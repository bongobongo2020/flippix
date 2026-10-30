using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlipPix.UI.Linux.Controls;
using FlipPix.UI.Linux.Windows;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI;

/// <summary>Port of FlipPix.UI/VideoGeneratorWindow.xaml.cs.</summary>
public partial class VideoGeneratorWindow : AppWindow
{
    private readonly VideoGeneratorViewModel _viewModel;
    private readonly WindowPositionService _windowPositionService;

    // This window is a reused singleton: closing it hides it (so reopening is instant) rather
    // than destroying it. Only a real application shutdown sets this true to allow a true close.
    private bool _allowClose;

    private DispatcherTimer? _scrubTimerScail2;

    // Scail 2 playhead tracking (trim-track playhead ↔ media element)
    private DispatcherTimer? _scail2PosTimer;
    private bool _scail2IsPlaying;

    /// <summary>For the XAML loader and previewer only.</summary>
    public VideoGeneratorWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _windowPositionService = null!;
    }

    public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
        Opened += OnLoaded;

        // The app ends when the main window closes; let this reused window really close then.
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }
            && !ReferenceEquals(main, this))
        {
            main.Closed += (_, _) =>
            {
                _allowClose = true;
                Close();
            };
        }

        _viewModel.PlayRequested += OnPlayRequested;
        _viewModel.Scail2VM.SeekRequested += OnScail2SeekRequested;
        _viewModel.Scail2VM.PropertyChanged += Scail2VM_PropertyChanged;
        _viewModel.H3VrVM.PropertyChanged += H3VrVM_PropertyChanged;
        _viewModel.H3ExpressVM.PropertyChanged += H3ExpressVM_PropertyChanged;
        _viewModel.SeedUpscaleVM.PropertyChanged += SeedUpscaleVM_PropertyChanged;
        _viewModel.H3VideoEditorVM.PropertyChanged += H3VideoEditorVM_PropertyChanged;
        _viewModel.CharacterSwapVM.PropertyChanged += CharacterSwapVM_PropertyChanged;

        // WPF's CastThumbnailStyle carried an EventSetter; Avalonia themes cannot, so the window
        // listens for clicks on any border wearing that theme.
        AddHandler(PointerReleasedEvent, CastThumbnail_PointerReleased, RoutingStrategies.Bubble);
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        EnsureOnScreen();
        ApplyScail2RefSource();
        ApplyH3VrSource();
        ApplyH3ExpressSource();
        ApplySeedUpscaleSource();
        ApplyH3EditorSource();
        ApplyCharacterSwapSource();
    }

    private void Header_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e) => Header_PointerPressed(sender, e);

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    private void OnPlayRequested(object? sender, EventArgs e)
    {
        foreach (var p in new[] { Scail2VideoPlayer, MiniMaxI2VVideoPlayer, H3VrVideoPlayer, H3ExpressVideoPlayer })
        {
            if (p?.Source == null) continue;
            p.Position = TimeSpan.Zero;
            p.Play();
        }
    }

    // Never seek a scrub preview to the exact end of the clip; hold a couple of frames back.
    private static double ClampPreviewSeek(VideoPreview p, double seconds, double fps)
    {
        var t = Math.Max(0, seconds);
        if (p.NaturalDuration.HasTimeSpan)
        {
            double dur = p.NaturalDuration.TimeSpan.TotalSeconds;
            double guard = Math.Max(0.05, 2.0 / (fps > 0 ? fps : 24.0));
            if (dur > guard) t = Math.Min(t, dur - guard);
        }
        return t;
    }

    private const double ScailTrimThumbWidth = 14.0;
    private const int ScailChunkFrames = 81;

    // ── Shared seed-board players (H3 VR, H3 Express, Seed Upscale, H3 Video Editor) ──

    private void H3VrVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(H3VrViewModel.ActivePreviewUri)) OnUi(ApplyH3VrSource);
    }

    private void ApplyH3VrSource() => ApplySharedPlayerSource(H3VrVideoPlayer, _viewModel.H3VrVM.ActivePreviewUri);
    private void H3VrPlayer_MediaOpened(object? sender, RoutedEventArgs e) => H3VrVideoPlayer.Play();
    private void H3VrPlayer_MediaEnded(object? sender, RoutedEventArgs e) => Loop(H3VrVideoPlayer);
    private void H3VrPlayer_MediaFailed(object? sender, MediaFailedEventArgs e) =>
        _viewModel.H3VrVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

    private void H3ExpressVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(H3ExpressViewModel.ActivePreviewUri)) OnUi(ApplyH3ExpressSource);
    }

    private void ApplyH3ExpressSource() => ApplySharedPlayerSource(H3ExpressVideoPlayer, _viewModel.H3ExpressVM.ActivePreviewUri);
    private void H3ExpressPlayer_MediaOpened(object? sender, RoutedEventArgs e) => H3ExpressVideoPlayer.Play();
    private void H3ExpressPlayer_MediaEnded(object? sender, RoutedEventArgs e) => Loop(H3ExpressVideoPlayer);
    private void H3ExpressPlayer_MediaFailed(object? sender, MediaFailedEventArgs e) =>
        _viewModel.H3ExpressVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

    private void SeedUpscaleVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeedUpscaleViewModel.ActivePreviewUri)) OnUi(ApplySeedUpscaleSource);
    }

    private void ApplySeedUpscaleSource() => ApplySharedPlayerSource(SeedUpscaleVideoPlayer, _viewModel.SeedUpscaleVM.ActivePreviewUri);
    private void SeedUpscalePlayer_MediaOpened(object? sender, RoutedEventArgs e) => SeedUpscaleVideoPlayer.Play();
    private void SeedUpscalePlayer_MediaEnded(object? sender, RoutedEventArgs e) => Loop(SeedUpscaleVideoPlayer);
    private void SeedUpscalePlayer_MediaFailed(object? sender, MediaFailedEventArgs e) =>
        _viewModel.SeedUpscaleVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

    private void CharacterSwapVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CharacterSwapViewModel.ActivePreview)) OnUi(ApplyCharacterSwapSource);
    }

    private void ApplyCharacterSwapSource() => ApplySharedPlayerSource(CharacterSwapPlayer, _viewModel.CharacterSwapVM.ActivePreview);
    private void CharacterSwapPlayer_MediaOpened(object? sender, RoutedEventArgs e) => CharacterSwapPlayer.Play();
    private void CharacterSwapPlayer_MediaEnded(object? sender, RoutedEventArgs e) => Loop(CharacterSwapPlayer);

    // Moving the caret to the end is how an Avalonia TextBox scrolls to it.
    private void CharacterSwapLog_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox log) log.CaretIndex = log.Text?.Length ?? 0;
    }

    private void H3VideoEditorVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(H3VideoEditorViewModel.PreviewVideoPath)) OnUi(ApplyH3EditorSource);
    }

    private void ApplyH3EditorSource()
    {
        ApplySharedPlayerSource(H3EditorVideoPlayer, _viewModel.H3VideoEditorVM.PreviewVideoPath);
        SetH3EditorPlaying(!string.IsNullOrEmpty(_viewModel.H3VideoEditorVM.PreviewVideoPath));
    }

    private void H3EditorPlayer_MediaOpened(object? sender, RoutedEventArgs e)
    {
        _viewModel.H3VideoEditorVM.PreviewDurationSeconds = H3EditorVideoPlayer.NaturalDuration.HasTimeSpan
            ? H3EditorVideoPlayer.NaturalDuration.TimeSpan.TotalSeconds
            : 0;
        H3EditorVideoPlayer.Play();
        SetH3EditorPlaying(true);
    }

    // ── Scrub bar ────────────────────────────────────────────────────────────
    // Same scheme as the WPF window: a timer follows the player while it plays, moving the bar seeks to the
    // nearest frame, and a drag pauses until release. _syncingScrub marks the timer's own writes.
    private DispatcherTimer? _h3ScrubTimer;
    private bool _syncingScrub;
    private bool _scrubDragging;
    private bool _resumeAfterScrub;
    private bool _h3ScrubHooked;

    private void HookH3Scrub()
    {
        if (_h3ScrubHooked || H3EditorScrub == null) return;
        _h3ScrubHooked = true;
        H3EditorScrub.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncingScrub) SeekH3Editor(H3EditorScrub.Value);
        };
        H3EditorScrub.AddHandler(PointerPressedEvent, (_, _) =>
        {
            _scrubDragging = true;
            _resumeAfterScrub = _h3EditorPlaying;
            if (_h3EditorPlaying)
            {
                H3EditorVideoPlayer.Pause();
                SetH3EditorPlaying(false);
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        H3EditorScrub.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (!_scrubDragging) return;
            _scrubDragging = false;
            SeekH3Editor(H3EditorScrub.Value);
            if (_resumeAfterScrub)
            {
                H3EditorVideoPlayer.Play();
                SetH3EditorPlaying(true);
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void ShowH3Playhead(double seconds)
    {
        _syncingScrub = true;
        if (H3EditorScrub != null) H3EditorScrub.Value = seconds;
        _syncingScrub = false;
        _viewModel.H3VideoEditorVM.PlayheadSeconds = seconds;
    }

    private void SeekH3Editor(double seconds)
    {
        if (H3EditorVideoPlayer.Source == null) return;
        var t = ClampPreviewSeek(H3EditorVideoPlayer, H3VideoEditorViewModel.SnapToFrame(seconds), H3VideoEditorViewModel.PreviewFps);
        H3EditorVideoPlayer.Position = TimeSpan.FromSeconds(t);
        ShowH3Playhead(t);
    }

    private void H3EditorFrameBack_Click(object? sender, RoutedEventArgs e) => StepH3Frame(-1);
    private void H3EditorFrameForward_Click(object? sender, RoutedEventArgs e) => StepH3Frame(+1);

    private void StepH3Frame(int frames)
    {
        if (H3EditorVideoPlayer.Source == null) return;
        if (_h3EditorPlaying)
        {
            H3EditorVideoPlayer.Pause();
            SetH3EditorPlaying(false);
        }
        SeekH3Editor(_viewModel.H3VideoEditorVM.PlayheadSeconds + frames / H3VideoEditorViewModel.PreviewFps);
    }

    // The transport tracks play state itself, the same way the WPF window does.
    private bool _h3EditorPlaying;

    private void SetH3EditorPlaying(bool playing)
    {
        _h3EditorPlaying = playing;
        if (H3EditorPlayButton != null) H3EditorPlayButton.Content = playing ? "⏸" : "▶";
        HookH3Scrub();

        if (_h3ScrubTimer == null)
        {
            _h3ScrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _h3ScrubTimer.Tick += (_, _) =>
            {
                if (_scrubDragging || H3EditorVideoPlayer.Source == null) return;
                ShowH3Playhead(H3EditorVideoPlayer.Position.TotalSeconds);
            };
        }
        if (playing) _h3ScrubTimer.Start();
        else
        {
            _h3ScrubTimer.Stop();
            if (!_scrubDragging && H3EditorVideoPlayer?.Source != null)
                ShowH3Playhead(H3EditorVideoPlayer.Position.TotalSeconds);
        }
    }

    private void H3EditorPlayPause_Click(object? sender, RoutedEventArgs e)
    {
        if (H3EditorVideoPlayer.Source == null) return;
        if (_h3EditorPlaying) H3EditorVideoPlayer.Pause();
        else H3EditorVideoPlayer.Play();
        SetH3EditorPlaying(!_h3EditorPlaying);
    }

    private void H3EditorStop_Click(object? sender, RoutedEventArgs e)
    {
        if (H3EditorVideoPlayer.Source == null) return;
        H3EditorVideoPlayer.Stop();
        SetH3EditorPlaying(false);
        ShowH3Playhead(0);
    }
    private void H3EditorPlayer_MediaEnded(object? sender, RoutedEventArgs e) => Loop(H3EditorVideoPlayer);
    private void H3EditorPlayer_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"H3Editor preview failed: {e.ErrorException?.Message ?? "unknown error"}");
        e.Handled = true;
    }

    private static void Loop(VideoPreview? player)
    {
        if (player == null) return;
        player.Position = TimeSpan.FromMilliseconds(1);
        player.Play();
    }

    /// <summary>Points a seed-board player at a file. Clicking the same tile twice replays it.</summary>
    private static void ApplySharedPlayerSource(VideoPreview? player, string? path)
    {
        if (player == null) return;
        if (string.IsNullOrEmpty(path))
        {
            player.Stop();
            player.Source = null;
            return;
        }

        string target;
        try { target = System.IO.Path.GetFullPath(path); }
        catch { target = path; }

        if (string.Equals(player.Source?.ToString(), target, StringComparison.Ordinal))
        {
            player.Position = TimeSpan.Zero;
            player.Play();
            return;
        }
        player.Stop();
        player.Source = target; // MediaOpened starts playback.
    }

    // ── Scail 2 reference player + trim markers ─────────────────────────────────────

    private void ApplyScail2RefSource()
    {
        var p = Scail2RefVideoPlayer;
        if (p == null) return;
        var path = _viewModel.Scail2VM.VideoFileUri;
        var target = string.IsNullOrEmpty(path) ? null : path;
        if (string.Equals(p.Source?.ToString(), target, StringComparison.Ordinal)) return;
        p.Source = target;
    }

    private void Scail2RefPlayer_MediaOpened(object? sender, RoutedEventArgs e)
    {
        Scail2RefVideoPlayer.Pause();
        if (_scail2PosTimer == null)
        {
            _scail2PosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _scail2PosTimer.Tick += (_, _) =>
            {
                var p = Scail2RefVideoPlayer;
                if (p?.Source == null || !_scail2IsPlaying) return;
                if (p.NaturalDuration.HasTimeSpan)
                    _viewModel.Scail2VM.PlaybackPositionSeconds = p.Position.TotalSeconds;
            };
        }
        _scail2PosTimer.Start();
    }

    private void Scail2Play_Click(object? sender, RoutedEventArgs e)
    {
        Scail2RefVideoPlayer?.Play();
        _scail2IsPlaying = Scail2RefVideoPlayer?.Source != null;
    }

    private void Scail2Pause_Click(object? sender, RoutedEventArgs e)
    {
        Scail2RefVideoPlayer?.Pause();
        _scail2IsPlaying = false;
        // Pausing settles on a deliberate frame → that's the Klein base frame.
        _viewModel.Scail2VM.NotifyScrubbed();
    }

    private void Scail2RefPlayer_MediaEnded(object? sender, RoutedEventArgs e) => _scail2IsPlaying = false;

    /// <summary>Opens the picture behind a cast thumbnail (Tag = its path) in the image viewer.</summary>
    private void CastThumbnail_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var theme = this.FindResource("CastThumbnailStyle");
        var border = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => theme != null && ReferenceEquals(b.Theme, theme));
        if (border is not { Tag: string path } || string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        try
        {
            DesktopIntegration.OpenFile(path);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not open {System.IO.Path.GetFileName(path)}: {ex.Message}",
                "FlipPix", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>Opens a cast card's ✨ Generate menu on a left click.</summary>
    private void CastGenerateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) Menus.Open(button);
    }

    /// <summary>
    /// Remembers whose card the cast-photo menu opened on and rescans the LoRA lists. Tag carries
    /// the tab's view model (H3Cast or H3Ensemble) across the menu boundary.
    /// </summary>
    private void CastPhotoMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && Menus.GetTarget(menu) is Button button)
        {
            switch (button.Tag)
            {
                case H3CastViewModel cast when button.DataContext is CharacterSlot castSlot:
                    cast.CastPhotoMenuSlot = castSlot;
                    cast.RefreshCastPhotoLoras();
                    break;
                case H3EnsembleViewModel ensemble when button.DataContext is CharacterSlot ensembleSlot:
                    ensemble.CastPhotoMenuSlot = ensembleSlot;
                    ensemble.RefreshCastPhotoLoras();
                    break;
            }
        }
    }

    private void SeekScail2RefTo(double seconds)
    {
        var p = Scail2RefVideoPlayer;
        if (p?.Source == null) return;
        p.Pause();
        _scail2IsPlaying = false;
        var t = ClampPreviewSeek(p, seconds, _viewModel.Scail2VM.Fps);
        p.Position = TimeSpan.FromSeconds(t);
        _viewModel.Scail2VM.PlaybackPositionSeconds = t;
        _viewModel.Scail2VM.NotifyScrubbed();
    }

    private double Scail2TrimTrackWidth =>
        Scail2TrimTrack != null && Scail2TrimTrack.Bounds.Width > 1 ? Scail2TrimTrack.Bounds.Width : 0;

    private double Scail2TrimSecToX(double seconds, double duration)
    {
        var w = Scail2TrimTrackWidth;
        if (duration <= 0 || w <= 0) return 0;
        return Math.Max(0, Math.Min(w, seconds / duration * w));
    }

    private void Scail2VM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WanScailViewModel.VideoFileUri):
                OnUi(ApplyScail2RefSource);
                break;
            case nameof(WanScailViewModel.TrimInSeconds):
            case nameof(WanScailViewModel.TrimOutSeconds):
            case nameof(WanScailViewModel.VideoDurationSeconds):
            case nameof(WanScailViewModel.PlaybackPositionSeconds):
            case nameof(WanScailViewModel.Fps):
            case nameof(WanScailViewModel.TotalFrames):
                OnUi(UpdateScail2TrimMarkers);
                break;
        }
    }

    private void Scail2TrimTrack_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateScail2TrimMarkers();

    private readonly List<Control> _scail2Ticks = new();
    private string _scail2TickSig = "";

    private void RebuildScail2Ticks()
    {
        if (Scail2TrimTrack == null) return;
        var vm = _viewModel.Scail2VM;
        double fps = vm.Fps > 0 ? vm.Fps : 24.0;
        double dur = vm.VideoDurationSeconds;
        double w = Scail2TrimTrackWidth;
        int total = vm.TotalFrames;

        string sig = $"{fps:F3}|{dur:F3}|{w:F1}|{total}";
        if (sig == _scail2TickSig) return;
        _scail2TickSig = sig;

        foreach (var t in _scail2Ticks) Scail2TrimTrack.Children.Remove(t);
        _scail2Ticks.Clear();

        if (dur <= 0 || w <= 0 || fps <= 0) return;
        int totalFrames = total > 0 ? total : (int)Math.Round(dur * fps);

        for (int frame = ScailChunkFrames; frame < totalFrames; frame += ScailChunkFrames)
        {
            double x = Scail2TrimSecToX(frame / fps, dur);
            var tick = new Border
            {
                Width = 1.5,
                Height = 18,
                Background = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Margin = new Thickness(x - 0.75, 0, 0, 0)
            };
            _scail2Ticks.Add(tick);
            Scail2TrimTrack.Children.Insert(1, tick);
        }
    }

    private void UpdateScail2TrimMarkers()
    {
        if (Scail2InThumb == null) return;
        RebuildScail2Ticks();
        var vm = _viewModel.Scail2VM;
        double dur = vm.VideoDurationSeconds;
        double w = Scail2TrimTrackWidth;
        if (dur <= 0 || w <= 0) return;

        double outSec = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
        double inX = Scail2TrimSecToX(vm.TrimInSeconds, dur);
        double outX = Scail2TrimSecToX(outSec, dur);
        double playX = Scail2TrimSecToX(vm.PlaybackPositionSeconds, dur);

        Scail2InThumb.Margin = new Thickness(inX - ScailTrimThumbWidth / 2, 0, 0, 0);
        Scail2OutThumb.Margin = new Thickness(outX - ScailTrimThumbWidth / 2, 0, 0, 0);
        Scail2TrimRegion.Margin = new Thickness(inX, 0, 0, 0);
        Scail2TrimRegion.Width = Math.Max(0, outX - inX);
        Scail2TrimPlayhead.Margin = new Thickness(Math.Max(0, playX - 1), 0, 0, 0);
    }

    private void Scail2InThumb_DragDelta(object? sender, VectorEventArgs e)
    {
        var vm = _viewModel.Scail2VM;
        double dur = vm.VideoDurationSeconds;
        double w = Scail2TrimTrackWidth;
        if (dur <= 0 || w <= 0) return;
        vm.TrimInSeconds += e.Vector.X / w * dur;
        UpdateScail2TrimMarkers();
        SeekScail2RefTo(vm.TrimInSeconds);
    }

    private void Scail2OutThumb_DragDelta(object? sender, VectorEventArgs e)
    {
        var vm = _viewModel.Scail2VM;
        double dur = vm.VideoDurationSeconds;
        double w = Scail2TrimTrackWidth;
        if (dur <= 0 || w <= 0) return;
        double cur = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
        vm.TrimOutSeconds = cur + e.Vector.X / w * dur;
        UpdateScail2TrimMarkers();
        SeekScail2RefTo(vm.TrimOutSeconds);
    }

    private void Scail2Process_Click(object? sender, RoutedEventArgs e) => _ = _viewModel.Scail2VM.OnTrimFinalizedAsync();

    private void OnScail2SeekRequested(object? sender, TimeSpan startPos)
    {
        var player = Scail2RefVideoPlayer;
        if (player?.Source == null) return;

        _scrubTimerScail2?.Stop();

        var vm = _viewModel.Scail2VM;
        var fps = vm.Fps > 0 ? vm.Fps : 24.0;
        var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
        var endPos = chunk != null ? TimeSpan.FromSeconds(chunk.EndFrame / fps) : startPos + TimeSpan.FromSeconds(4);
        var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

        player.Position = startPos;

        var step = 0;
        _scrubTimerScail2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _scrubTimerScail2.Tick += (_, _) =>
        {
            step++;
            if (step == 1) player.Position = midPos;
            else { player.Position = endPos; _scrubTimerScail2!.Stop(); }
        };
        _scrubTimerScail2.Start();
    }

    // ── H3 Video Editor timeline clip previews loop ───────────────────────────────

    private void TimelineClipVideo_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (sender is VideoPreview { Source: not null } player) Loop(player);
    }

    private void TimelineClipVideo_MediaFailed(object? sender, MediaFailedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"TimelineClipVideo failed: {e.ErrorException?.Message}");
        e.Handled = true;
    }

    private void StopAllPlayers()
    {
        _scrubTimerScail2?.Stop();
        Scail2RefVideoPlayer?.Stop();
        Scail2VideoPlayer?.Stop();
        MiniMaxI2VVideoPlayer?.Stop();
        H3VrVideoPlayer?.Stop();
        H3ExpressVideoPlayer?.Stop();
        SeedUpscaleVideoPlayer?.Stop();
        H3EditorVideoPlayer?.Stop();
        CharacterSwapPlayer?.Stop();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Reused singleton: a user close only hides it; a real app shutdown closes it.
        if (!_allowClose && e.CloseReason != WindowCloseReason.ApplicationShutdown)
        {
            e.Cancel = true;
            StopAllPlayers();
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAllPlayers();
        _viewModel.Scail2VM.SeekRequested -= OnScail2SeekRequested;
        _viewModel.Scail2VM.PropertyChanged -= Scail2VM_PropertyChanged;
        _viewModel.H3VideoEditorVM.PropertyChanged -= H3VideoEditorVM_PropertyChanged;
        _viewModel.CharacterSwapVM.PropertyChanged -= CharacterSwapVM_PropertyChanged;
        _viewModel.PlayRequested -= OnPlayRequested;
        if (_viewModel is IDisposable disposable) disposable.Dispose();
        DataContext = null;
        base.OnClosed(e);
    }
}
