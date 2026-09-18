using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels.Video;

namespace FlipPix.UI.Linux.Views.Video
{
    /// <summary>
    /// Scail 2 tab. Ported from VideoGeneratorWindow.xaml's Scail 2 TabItem and the trim-track
    /// half of its code-behind.
    ///
    /// The one real divergence from WPF: there is no MediaElement here, so the reference clip is
    /// shown as a poster frame that re-grabs whenever the playhead moves. Everything the WPF
    /// code-behind drove off playback position - the In/Out markers, the chunk ticks, the frame
    /// the Klein char-swap runs on - is driven off the scrub slider instead.
    /// </summary>
    public partial class Scail2View : UserControl
    {
        /// <summary>Frames per SCAIL chunk; the ticks on the trim track mark the boundaries.</summary>
        private const int ScailChunkFrames = 121;
        private const double TrimThumbWidth = 14;

        private Scail2ViewModel? _vm;
        private readonly List<Control> _ticks = new();
        private string _tickSignature = "";

        public Scail2View()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;

            _vm = DataContext as Scail2ViewModel;

            if (_vm != null)
            {
                _vm.PropertyChanged += OnViewModelPropertyChanged;
                UpdateTrimMarkers();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(WanScailViewModel.TrimInSeconds):
                case nameof(WanScailViewModel.TrimOutSeconds):
                case nameof(WanScailViewModel.VideoDurationSeconds):
                case nameof(WanScailViewModel.PlaybackPositionSeconds):
                case nameof(WanScailViewModel.Fps):
                case nameof(WanScailViewModel.TotalFrames):
                    if (Dispatcher.UIThread.CheckAccess()) UpdateTrimMarkers();
                    else Dispatcher.UIThread.Post(UpdateTrimMarkers);
                    break;
            }
        }

        private Grid? TrimTrack => this.FindControl<Grid>("Scail2TrimTrack");

        private double TrimTrackWidth => TrimTrack is { Bounds.Width: > 1 } g ? g.Bounds.Width : 0;

        private double SecToX(double seconds, double duration)
        {
            var w = TrimTrackWidth;
            if (duration <= 0 || w <= 0) return 0;
            return Math.Max(0, Math.Min(w, seconds / duration * w));
        }

        private void Scail2TrimTrack_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateTrimMarkers();

        /// <summary>
        /// One tick per SCAIL chunk boundary, so the In/Out range can be lined up with the
        /// chunks the workflow will actually render. Rebuilt only when the geometry changes.
        /// </summary>
        private void RebuildTicks()
        {
            var track = TrimTrack;
            if (track == null || _vm == null) return;

            double fps = _vm.Fps > 0 ? _vm.Fps : 24.0;
            double dur = _vm.VideoDurationSeconds;
            double w = TrimTrackWidth;
            int total = _vm.TotalFrames;

            string sig = $"{fps:F3}|{dur:F3}|{w:F1}|{total}";
            if (sig == _tickSignature) return;
            _tickSignature = sig;

            foreach (var t in _ticks) track.Children.Remove(t);
            _ticks.Clear();

            if (dur <= 0 || w <= 0 || fps <= 0) return;
            int totalFrames = total > 0 ? total : (int)Math.Round(dur * fps);

            for (int frame = ScailChunkFrames; frame < totalFrames; frame += ScailChunkFrames)
            {
                double x = SecToX(frame / fps, dur);
                var tick = new Border
                {
                    Width = 1.5,
                    Height = 18,
                    Background = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Margin = new Thickness(x - 0.75, 0, 0, 0)
                };
                _ticks.Add(tick);
                track.Children.Insert(1, tick);
            }
        }

        private void UpdateTrimMarkers()
        {
            var inThumb = this.FindControl<Thumb>("Scail2InThumb");
            var outThumb = this.FindControl<Thumb>("Scail2OutThumb");
            var region = this.FindControl<Border>("Scail2TrimRegion");
            var playhead = this.FindControl<Border>("Scail2TrimPlayhead");
            if (_vm == null || inThumb == null || outThumb == null || region == null || playhead == null)
                return;

            RebuildTicks();

            double dur = _vm.VideoDurationSeconds;
            double w = TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;

            double outSec = _vm.TrimOutSeconds > 0 ? _vm.TrimOutSeconds : dur;
            double inX = SecToX(_vm.TrimInSeconds, dur);
            double outX = SecToX(outSec, dur);
            double playX = SecToX(_vm.PlaybackPositionSeconds, dur);

            inThumb.Margin = new Thickness(inX - TrimThumbWidth / 2, 0, 0, 0);
            outThumb.Margin = new Thickness(outX - TrimThumbWidth / 2, 0, 0, 0);
            region.Margin = new Thickness(inX, 0, 0, 0);
            region.Width = Math.Max(0, outX - inX);
            playhead.Margin = new Thickness(Math.Max(0, playX - 1), 0, 0, 0);
        }

        /// <summary>
        /// Moving a marker also moves the playhead there, so the poster frame shows what is
        /// being marked. WPF seeks its MediaElement at this point; here the seek IS the poster.
        /// </summary>
        private void SeekTo(double seconds)
        {
            if (_vm == null) return;
            var dur = _vm.VideoDurationSeconds;
            var clamped = dur > 0 ? Math.Max(0, Math.Min(dur, seconds)) : Math.Max(0, seconds);
            _vm.PlaybackPositionSeconds = clamped;
            _vm.NotifyScrubbed();
        }

        private void Scail2InThumb_DragDelta(object? sender, VectorEventArgs e)
        {
            if (_vm == null) return;
            double dur = _vm.VideoDurationSeconds;
            double w = TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            _vm.TrimInSeconds += e.Vector.X / w * dur;
            UpdateTrimMarkers();
            SeekTo(_vm.TrimInSeconds);
        }

        private void Scail2OutThumb_DragDelta(object? sender, VectorEventArgs e)
        {
            if (_vm == null) return;
            double dur = _vm.VideoDurationSeconds;
            double w = TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            double cur = _vm.TrimOutSeconds > 0 ? _vm.TrimOutSeconds : dur;
            _vm.TrimOutSeconds = cur + e.Vector.X / w * dur;
            UpdateTrimMarkers();
            SeekTo(_vm.TrimOutSeconds);
        }

        /// <summary>Hands the base scene clip to the desktop's own player.</summary>
        private void Scail2Play_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var path = _vm?.InputVideoPath;
            if (!string.IsNullOrEmpty(path)) DesktopIntegration.OpenFile(path);
        }

        // Generation is explicit: the user presses "Generate video" once the In/Out range is set.
        private void Scail2Process_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_vm != null) _ = _vm.OnTrimFinalizedAsync();
        }
    }
}
