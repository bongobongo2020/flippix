using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Controls;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// Qwen Edit tab, ported from the WPF window's "🧑‍🤝‍🧑 Qwen Edit" TabItem. DataContext is the
    /// window's ImageGeneratorViewModel, so the bindings keep their "QwenEdit." prefix.
    ///
    /// The base-scene video is the one place this diverges from WPF. There, the user played a
    /// MediaElement, paused on a frame, and "Snap &amp; Send" rendered the paused visual. Here the
    /// scrub slider drives VideoPreview's poster frame and Snap &amp; Send pulls that same frame
    /// back out of the file with ffmpeg, at the clip's own resolution.
    /// </summary>
    public partial class QwenEditView : UserControl
    {
        public QwenEditView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private QwenEditViewModel? QwenEdit => (DataContext as ImageGeneratorViewModel)?.QwenEdit;

        private void QwenBasePlay_Click(object? sender, RoutedEventArgs e)
        {
            var uri = QwenEdit?.BaseVideoFileUri;
            if (string.IsNullOrEmpty(uri)) return;
            var path = System.Uri.TryCreate(uri, System.UriKind.Absolute, out var parsed) && parsed.IsFile
                ? parsed.LocalPath
                : uri;
            DesktopIntegration.OpenFile(path);
        }

        private async void QwenBaseSnapAndSend_Click(object? sender, RoutedEventArgs e)
        {
            var vm = QwenEdit;
            var player = this.FindControl<VideoPreview>("QwenBaseVideoPlayer");
            if (vm == null || player == null) return;

            try
            {
                var framePath = await player.CaptureFrameAsync();
                if (framePath == null) return;

                vm.SetBaseImage(framePath);

                // Snap & Send: analyze the three images and, once the edit prompt is ready, run
                // generation — no extra clicks needed.
                await vm.AnalyzeAndGenerateAsync();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QwenBaseSnapAndSend error: {ex.Message}");
            }
        }
    }
}
