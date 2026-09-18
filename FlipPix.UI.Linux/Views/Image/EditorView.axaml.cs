using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Controls;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// Editor tab, ported from the WPF window's "✏️ Editor" TabItem and the inpaint half of its
    /// code-behind. DataContext is the window's ImageGeneratorViewModel, so the bindings keep
    /// their "InpaintEditor." / "KleinInpaintEditor." prefixes.
    ///
    /// WPF painted the mask on an InkCanvas and composited it with WIC. Avalonia has neither, so
    /// the strokes come from <see cref="MaskPaintCanvas"/> and ImageSharp does the compositing —
    /// producing the same clipspace PNG the workflow expects.
    /// </summary>
    public partial class EditorView : UserControl
    {
        public EditorView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private InpaintEditorViewModel? Inpaint => (DataContext as ImageGeneratorViewModel)?.InpaintEditor;

        private void ClearMask_Click(object? sender, RoutedEventArgs e)
            => this.FindControl<MaskPaintCanvas>("MaskInkCanvas")?.Clear();

        private async void RunInpaint_Click(object? sender, RoutedEventArgs e)
        {
            var vm = Inpaint;
            if (vm == null || !vm.CanGenerate) return;

            try
            {
                var combinedPath = BuildCombinedMaskedImage(vm.SourceImagePath);
                if (combinedPath == null)
                {
                    System.Windows.MessageBox.Show(
                        "Failed to prepare the masked image. Please ensure a source image is loaded.",
                        "Error");
                    return;
                }

                await vm.RunInpaintAsync(combinedPath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Inpaint error: {ex.Message}", "Error");
            }
        }

        /// <summary>
        /// Writes source RGB + the painted mask as alpha to a PNG and returns its path.
        ///
        /// ComfyUI's LoadAndResizeImage clipspace convention: alpha=0 means painted (inpaint
        /// here), alpha=255 means keep the original. The node inverts alpha internally to get
        /// mask=1 over the painted area, so the mask is inverted on the way in.
        /// </summary>
        private string? BuildCombinedMaskedImage(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(sourcePath);
                int w = image.Width, h = image.Height;

                var canvas = this.FindControl<MaskPaintCanvas>("MaskInkCanvas");
                var mask = canvas?.RenderMask(w, h);

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int offset = y * w;
                        for (int x = 0; x < row.Length; x++)
                        {
                            // No strokes at all leaves alpha opaque everywhere: nothing to inpaint.
                            byte painted = mask == null ? (byte)0 : mask[offset + x];
                            row[x].A = (byte)(255 - painted);
                        }
                    }
                });

                var dir = Path.Combine(UserPaths.CacheDir, "inpaint");
                Directory.CreateDirectory(dir);
                var outPath = Path.Combine(dir, $"basescene_mask_{DateTime.Now:yyyyMMdd_HHmmssfff}.png");
                image.Save(outPath);
                return outPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
