using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace FlipPix.UI.Linux.Controls
{
    /// <summary>
    /// Stands in for WPF's InkCanvas, which Avalonia has no equivalent of. The user drags to
    /// paint over the region to inpaint; the strokes are drawn translucent red on screen, and
    /// <see cref="RenderMask"/> rasterizes them white-on-black at the source image's resolution
    /// for the workflow.
    ///
    /// Strokes are kept as points plus a width rather than as a rendered bitmap, so the mask can
    /// be re-rasterized at any size no matter what the control was displayed at.
    /// </summary>
    public class MaskPaintCanvas : Control
    {
        private sealed class Stroke
        {
            public List<Point> Points { get; } = new();
            public double Width { get; init; }
        }

        private readonly List<Stroke> _strokes = new();
        private Stroke? _active;

        public static readonly StyledProperty<double> BrushSizeProperty =
            AvaloniaProperty.Register<MaskPaintCanvas, double>(nameof(BrushSize), 30);

        /// <summary>Stroke diameter in control pixels, matching WPF's DrawingAttributes.Width.</summary>
        public double BrushSize
        {
            get => GetValue(BrushSizeProperty);
            set => SetValue(BrushSizeProperty, value);
        }

        public static readonly StyledProperty<IBrush> StrokeBrushProperty =
            AvaloniaProperty.Register<MaskPaintCanvas, IBrush>(
                nameof(StrokeBrush), new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x3B, 0x30)));

        public IBrush StrokeBrush
        {
            get => GetValue(StrokeBrushProperty);
            set => SetValue(StrokeBrushProperty, value);
        }

        /// <summary>True once anything has been painted; the Generate button keys off it.</summary>
        public bool HasStrokes => _strokes.Count > 0;

        public MaskPaintCanvas()
        {
            // The control is otherwise hit-transparent, which would leave the drag going to the
            // image underneath.
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Cross);
        }

        public static readonly StyledProperty<IBrush?> BackgroundProperty =
            Panel.BackgroundProperty.AddOwner<MaskPaintCanvas>();

        public IBrush? Background
        {
            get => GetValue(BackgroundProperty);
            set => SetValue(BackgroundProperty, value);
        }

        /// <summary>Throws every stroke away, as InkCanvas.Strokes.Clear() did.</summary>
        public void Clear()
        {
            _strokes.Clear();
            _active = null;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _active = new Stroke { Width = BrushSize };
            _active.Points.Add(e.GetPosition(this));
            _strokes.Add(_active);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_active == null) return;
            _active.Points.Add(e.GetPosition(this));
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_active == null) return;
            _active = null;
            e.Pointer.Capture(null);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            context.FillRectangle(Background ?? Brushes.Transparent, new Rect(Bounds.Size));

            foreach (var stroke in _strokes)
                DrawStroke(context, stroke, StrokeBrush);
        }

        private static void DrawStroke(DrawingContext context, Stroke stroke, IBrush brush)
        {
            var pen = new Pen(brush, stroke.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            if (stroke.Points.Count == 1)
            {
                // A click with no drag is still a dab of paint.
                var p = stroke.Points[0];
                context.DrawEllipse(brush, null, p, stroke.Width / 2, stroke.Width / 2);
                return;
            }

            for (int i = 1; i < stroke.Points.Count; i++)
                context.DrawLine(pen, stroke.Points[i - 1], stroke.Points[i]);
        }

        /// <summary>
        /// Rasterizes the strokes to an 8-bit mask of the given size: 255 where painted, 0
        /// elsewhere. Coordinates are scaled from the control's own size, so the mask lines up
        /// with the source image however the control happened to be laid out.
        ///
        /// Returns null when nothing has been painted, which the caller treats as "no mask".
        /// </summary>
        public byte[]? RenderMask(int width, int height)
        {
            if (_strokes.Count == 0 || width <= 0 || height <= 0) return null;
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 0 || h <= 0) return null;

            double sx = width / w;
            double sy = height / h;
            var mask = new byte[width * height];

            foreach (var stroke in _strokes)
            {
                // Radius scales with the image too, so a 30px brush covers the same part of the
                // picture whatever size the control was on screen.
                double radius = stroke.Width / 2 * Math.Min(sx, sy);
                if (stroke.Points.Count == 1)
                {
                    StampDisc(mask, width, height, stroke.Points[0].X * sx, stroke.Points[0].Y * sy, radius);
                    continue;
                }

                for (int i = 1; i < stroke.Points.Count; i++)
                {
                    var a = stroke.Points[i - 1];
                    var b = stroke.Points[i];
                    StampSegment(mask, width, height,
                        a.X * sx, a.Y * sy, b.X * sx, b.Y * sy, radius);
                }
            }

            return mask;
        }

        /// <summary>Stamps discs along a segment: the round-capped pen, in pixels.</summary>
        private static void StampSegment(byte[] mask, int width, int height,
                                         double x0, double y0, double x1, double y1, double radius)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double length = Math.Sqrt(dx * dx + dy * dy);
            int steps = (int)Math.Ceiling(length / Math.Max(1, radius / 2)) + 1;

            for (int s = 0; s <= steps; s++)
            {
                double t = steps == 0 ? 0 : (double)s / steps;
                StampDisc(mask, width, height, x0 + dx * t, y0 + dy * t, radius);
            }
        }

        private static void StampDisc(byte[] mask, int width, int height, double cx, double cy, double radius)
        {
            int r = (int)Math.Ceiling(radius);
            if (r < 1) r = 1;
            int minX = Math.Max(0, (int)cx - r), maxX = Math.Min(width - 1, (int)cx + r);
            int minY = Math.Max(0, (int)cy - r), maxY = Math.Min(height - 1, (int)cy + r);
            double r2 = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                double ddy = y - cy;
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    double ddx = x - cx;
                    if (ddx * ddx + ddy * ddy <= r2)
                        mask[row + x] = 255;
                }
            }
        }
    }
}
