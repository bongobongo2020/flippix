using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.ViewModels;

namespace FlipPix.UI.Linux.Views.Image
{
    /// <summary>
    /// Ideogram tab, ported from the WPF window's "🔤 Ideogram" TabItem together with the
    /// composition-region drag handlers from its code-behind. DataContext is the window's
    /// ImageGeneratorViewModel, so the bindings keep their "Ideogram." prefix.
    /// </summary>
    public partial class IdeogramView : UserControl
    {
        private const double MinRegionSize = 24;

        public IdeogramView() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private IdeogramViewModel? Ideogram => (DataContext as ImageGeneratorViewModel)?.Ideogram;

        private static IdeogramRegion? RegionFromSender(object? sender)
            => (sender as Thumb)?.DataContext as IdeogramRegion;

        private static double Clamp(double v, double min, double max)
            => max < min ? min : v < min ? min : v > max ? max : v;

        private void Region_DragStarted(object? sender, VectorEventArgs e)
        {
            var region = RegionFromSender(sender);
            if (region != null)
                Ideogram?.SelectRegionCommand.Execute(region);
        }

        private void RegionMove_DragDelta(object? sender, VectorEventArgs e)
        {
            var region = RegionFromSender(sender);
            var vm = Ideogram;
            if (region == null || vm == null) return;
            double cw = vm.CanvasWidth, ch = vm.CanvasHeight;
            region.X = Clamp(region.X + e.Vector.X, 0, cw - region.Width);
            region.Y = Clamp(region.Y + e.Vector.Y, 0, ch - region.Height);
        }

        private void RegionResize_DragDelta(object? sender, VectorEventArgs e)
        {
            var region = RegionFromSender(sender);
            var vm = Ideogram;
            if (region == null || vm == null) return;
            double cw = vm.CanvasWidth, ch = vm.CanvasHeight;
            region.Width = Clamp(region.Width + e.Vector.X, MinRegionSize, cw - region.X);
            region.Height = Clamp(region.Height + e.Vector.Y, MinRegionSize, ch - region.Y);
        }
    }
}
