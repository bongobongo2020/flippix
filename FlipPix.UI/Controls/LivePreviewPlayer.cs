using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// Loops a live sampler preview and crossfades to the next one, the way ComfyUI's preview widget does.
    ///
    /// <para>Set <see cref="Source"/> to a file: a .jpg/.png is shown as a still, an .mp4 loops muted. A new
    /// video is opened in the hidden one of two MediaElements and only faded in once it has opened, so a step
    /// never flashes black between clips; the old one is then closed, which also lets go of its file.</para>
    /// </summary>
    public sealed class LivePreviewPlayer : Grid
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(string), typeof(LivePreviewPlayer),
            new PropertyMetadata(null, (d, e) => ((LivePreviewPlayer)d).Load(e.NewValue as string)));

        private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(250));

        private readonly MediaElement[] _media = new MediaElement[2];
        private readonly System.Windows.Controls.Image _still = new() { Stretch = Stretch.Uniform, Opacity = 0 };
        private int _front = -1;

        public LivePreviewPlayer()
        {
            Children.Add(_still);
            for (var i = 0; i < 2; i++)
            {
                var media = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Close,
                    IsMuted = true,
                    Stretch = Stretch.Uniform,
                    Opacity = 0,
                };
                var index = i;
                media.MediaOpened += (_, _) => Show(index);
                media.MediaEnded += (_, _) =>
                {
                    media.Position = TimeSpan.Zero;
                    media.Play();
                };
                media.MediaFailed += (_, _) => media.Close();
                _media[i] = media;
                Children.Add(media);
            }
        }

        public string? Source
        {
            get => (string?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        private void Load(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Reset();
                return;
            }

            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file is free to be reused
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _still.Source = bitmap;
                    FadeTo(_still, 1);
                    foreach (var m in _media) FadeTo(m, 0);
                }
                catch { /* a half-written still: the next step replaces it */ }
                return;
            }

            var back = _front == 0 ? 1 : 0;
            var next = _media[back];
            next.Close();
            next.Source = new Uri(path);
            next.Play(); // opens it; MediaOpened brings it to the front
        }

        private void Show(int index)
        {
            var previous = _front;
            _front = index;
            FadeTo(_media[index], 1);
            FadeTo(_still, 0);
            if (previous >= 0 && previous != index)
            {
                var old = _media[previous];
                FadeTo(old, 0, () =>
                {
                    // Only if it was not re-used for a newer step while fading out.
                    if (_front != previous) { old.Stop(); old.Close(); old.Source = null; }
                });
            }
        }

        private void Reset()
        {
            _front = -1;
            foreach (var m in _media)
            {
                m.BeginAnimation(OpacityProperty, null);
                m.Opacity = 0;
                m.Stop();
                m.Close();
                m.Source = null;
            }
            _still.BeginAnimation(OpacityProperty, null);
            _still.Opacity = 0;
            _still.Source = null;
        }

        private static void FadeTo(UIElement element, double opacity, Action? done = null)
        {
            var animation = new DoubleAnimation(opacity, Fade);
            if (done != null) animation.Completed += (_, _) => done();
            element.BeginAnimation(OpacityProperty, animation);
        }
    }
}
