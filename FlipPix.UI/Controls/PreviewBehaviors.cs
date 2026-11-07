using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlipPix.UI.Services;

namespace FlipPix.UI.Controls
{
    /// <summary>
    /// <c>controls:AsyncImage.Path="{Binding SomePath}"</c> on an <see cref="Image"/>, in place of binding
    /// <c>Source</c> to a path string. A plain path binding makes WPF decode the whole file at full
    /// resolution on the UI thread, again each time the item is re-templated; this decodes on a worker
    /// thread, at <see cref="DecodeWidthProperty"/> pixels wide.
    /// </summary>
    public static class AsyncImage
    {
        public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
            "Path", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, OnPathChanged));

        public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
            "DecodeWidth", typeof(int), typeof(AsyncImage), new PropertyMetadata(320));

        public static string? GetPath(DependencyObject d) => (string?)d.GetValue(PathProperty);
        public static void SetPath(DependencyObject d, string? value) => d.SetValue(PathProperty, value);
        public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);
        public static void SetDecodeWidth(DependencyObject d, int value) => d.SetValue(DecodeWidthProperty, value);

        private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.Image image) return;
            var path = e.NewValue as string;
            image.Source = null;
            if (string.IsNullOrEmpty(path)) return;

            // Through the local cache: a thumbnail on a slow network share is read once, not per re-template.
            AsyncThumbnail.Load(() => LocalMediaCache.GetLocal(path), GetDecodeWidth(image), 0, bitmap =>
            {
                // Only if the image still wants this file; a recycled container may have moved on.
                if (GetPath(image) == path) image.Source = bitmap;
            });
        }
    }

    /// <summary>
    /// <c>controls:HoverPlay.IsActive="{Binding IsSelected}"</c> on a looping preview
    /// <see cref="MediaElement"/> (set <c>LoadedBehavior="Manual"</c>). It plays while
    /// <c>IsActive</c> is true or the mouse is over it, and otherwise rests paused on its first frame.
    /// A row of tiles that all loop at once each keeps a video decoder busy, and a dozen of them is
    /// what made the H3 timeline sluggish; paused ones cost next to nothing.
    /// </summary>
    public static class HoverPlay
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(HoverPlay), new PropertyMetadata(false, OnIsActiveChanged));

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(HoverPlay), new PropertyMetadata(false));

        public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
        public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MediaElement media) return;
            Hook(media);
            Update(media);
        }

        private static void Hook(MediaElement media)
        {
            if ((bool)media.GetValue(HookedProperty)) return;
            media.SetValue(HookedProperty, true);

            // Play() opens the file; pausing once it has opened leaves the first frame showing.
            media.Loaded += (_, _) => { if (media.Source != null) media.Play(); };
            media.MediaOpened += (_, _) => Update(media);
            media.MouseEnter += (_, _) => Update(media);
            media.MouseLeave += (_, _) => Update(media);
            media.MediaEnded += (_, _) =>
            {
                media.Position = TimeSpan.FromMilliseconds(1);
                if (ShouldPlay(media)) media.Play();
            };
        }

        /// <summary>The video to show, as a path (bind this rather than <c>Source</c>): with a manual
        /// MediaElement a new file has to be opened before it can show even its first frame.</summary>
        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(HoverPlay), new PropertyMetadata(null, OnSourceChanged));

        public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
        public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);

        private static async void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MediaElement media) return;
            Hook(media);
            var path = e.NewValue as string;
            try
            {
                media.Close();
                media.Source = null;
                if (string.IsNullOrEmpty(path)) return;

                // A file on a network share is copied locally first, off the UI thread; until then the
                // tile's thumbnail shows through the empty player.
                var local = await LocalMediaCache.GetLocalAsync(path);
                if (GetSource(media) != path) return; // the tile moved on while the copy ran

                media.Source = new Uri(local, UriKind.Absolute);
                if (media.IsLoaded) media.Play(); // opens it; MediaOpened settles play/pause
            }
            catch
            {
                media.Source = null;
            }
        }

        private static bool ShouldPlay(MediaElement media) => GetIsActive(media) || media.IsMouseOver;

        private static void Update(MediaElement media)
        {
            if (media.Source == null) return;
            try
            {
                if (ShouldPlay(media)) media.Play();
                else media.Pause();
            }
            catch
            {
                // A file still being written: MediaFailed handles it, the thumbnail shows meanwhile.
            }
        }
    }
}
