// WPF imaging APIs (System.Windows.Media.Imaging) reimplemented over ImageSharp.
//
// The ported ViewModels decode, convert to BGRA, crop, scale and re-encode images through
// WPF's imaging stack. None of that exists on Linux, and System.Drawing.Common is
// Windows-only from .NET 7, so ImageSharp backs every type here. Pixels are always held as
// straight (unpremultiplied) BGRA32, which is what the callers ask for via
// PixelFormats.Bgra32 and what Avalonia wants for display.

using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace System.Windows
{
    /// <summary>WPF's integer rectangle, used to describe crop regions.</summary>
    public readonly struct Int32Rect
    {
        public Int32Rect(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public bool IsEmpty => Width == 0 && Height == 0;
        public static Int32Rect Empty => default;
    }
}

namespace System.Windows.Media
{
    /// <summary>Identifies a pixel layout. Only the members the ported code names are modelled.</summary>
    public readonly struct PixelFormat : IEquatable<PixelFormat>
    {
        internal PixelFormat(string name, int bitsPerPixel)
        {
            Name = name; BitsPerPixel = bitsPerPixel;
        }

        public string Name { get; }
        public int BitsPerPixel { get; }

        public bool Equals(PixelFormat other) => Name == other.Name;
        public override bool Equals(object? obj) => obj is PixelFormat other && Equals(other);
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
        public override string ToString() => Name ?? "Unknown";

        public static bool operator ==(PixelFormat a, PixelFormat b) => a.Equals(b);
        public static bool operator !=(PixelFormat a, PixelFormat b) => !a.Equals(b);
    }

    public static class PixelFormats
    {
        public static PixelFormat Bgra32 => new("Bgra32", 32);
        public static PixelFormat Pbgra32 => new("Pbgra32", 32);
        public static PixelFormat Bgr32 => new("Bgr32", 32);
        public static PixelFormat Bgr24 => new("Bgr24", 24);
        public static PixelFormat Rgba32 => new("Rgba32", 32);
        public static PixelFormat Gray8 => new("Gray8", 8);
    }

    /// <summary>Base for the transforms TransformedBitmap accepts.</summary>
    public abstract class Transform
    {
        internal abstract (int Width, int Height) Apply(int width, int height);
    }

    /// <summary>Uniform or per-axis scaling, the only transform the ported code uses.</summary>
    public class ScaleTransform : Transform
    {
        public ScaleTransform() : this(1.0, 1.0) { }

        public ScaleTransform(double scaleX, double scaleY)
        {
            ScaleX = scaleX; ScaleY = scaleY;
        }

        public double ScaleX { get; set; }
        public double ScaleY { get; set; }

        internal override (int Width, int Height) Apply(int width, int height) =>
            (Math.Max(1, (int)Math.Round(width * ScaleX)),
             Math.Max(1, (int)Math.Round(height * ScaleY)));
    }
}

namespace System.Windows.Media.Imaging
{
    [Flags]
    public enum BitmapCreateOptions
    {
        None = 0,
        PreservePixelFormat = 1,
        IgnoreColorProfile = 2,
        DelayCreation = 4,
        IgnoreImageCache = 8,
        OnDemand = 16,
    }

    public enum BitmapCacheOption { Default, None, OnDemand, OnLoad }

    /// <summary>
    /// An immutable-ish BGRA32 raster. WPF's BitmapSource is abstract with many decode paths;
    /// this keeps a single decoded ImageSharp image and derives everything from it.
    /// </summary>
    public class BitmapSource
    {
        private Avalonia.Media.Imaging.Bitmap? _avaloniaBitmap;

        /// <summary>Decoded pixels, or null when only the dimensions were read (DelayCreation).</summary>
        internal Image<Bgra32>? Image { get; set; }

        public int PixelWidth { get; internal set; }
        public int PixelHeight { get; internal set; }

        public double Width => PixelWidth;
        public double Height => PixelHeight;
        public double DpiX => 96.0;
        public double DpiY => 96.0;

        public PixelFormat Format => PixelFormats.Bgra32;

        /// <summary>WPF freezes bitmaps for cross-thread use; these are already effectively frozen.</summary>
        public bool IsFrozen { get; private set; }

        public void Freeze() => IsFrozen = true;

        public BitmapSource Clone() => FromImage(Image?.Clone());

        /// <summary>The Avalonia bitmap the XAML Image controls bind to, built on first use.</summary>
        public Avalonia.Media.Imaging.Bitmap? AvaloniaBitmap => _avaloniaBitmap ??= BuildAvaloniaBitmap();

        /// <summary>
        /// Copies raw BGRA bytes out, matching WPF's contract: <paramref name="stride"/> bytes
        /// per row, starting at <paramref name="offset"/> in the destination.
        /// </summary>
        public void CopyPixels(Array pixels, int stride, int offset)
        {
            if (pixels is not byte[] destination)
                throw new NotSupportedException("Only byte[] destinations are supported.");
            if (Image is null) return;

            var width = PixelWidth;
            var height = PixelHeight;
            Image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < height; y++)
                {
                    var rowStart = offset + y * stride;
                    if (rowStart + width * 4 > destination.Length) break;

                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < width; x++)
                    {
                        var p = row[x];
                        var i = rowStart + x * 4;
                        destination[i + 0] = p.B;
                        destination[i + 1] = p.G;
                        destination[i + 2] = p.R;
                        destination[i + 3] = p.A;
                    }
                }
            });
        }

        public void CopyPixels(System.Windows.Int32Rect sourceRect, Array pixels, int stride, int offset)
        {
            if (Image is null) return;
            var cropped = Image.Clone(ctx => ctx.Crop(
                new Rectangle(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height)));
            FromImage(cropped).CopyPixels(pixels, stride, offset);
            cropped.Dispose();
        }

        private Avalonia.Media.Imaging.Bitmap? BuildAvaloniaBitmap()
        {
            if (Image is null) return null;
            try
            {
                var writeable = new Avalonia.Media.Imaging.WriteableBitmap(
                    new Avalonia.PixelSize(PixelWidth, PixelHeight),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Unpremul);

                using (var buffer = writeable.Lock())
                {
                    var stride = buffer.RowBytes;
                    var address = buffer.Address;
                    var width = PixelWidth;
                    var height = PixelHeight;
                    var row = new byte[stride];

                    Image.ProcessPixelRows(accessor =>
                    {
                        for (var y = 0; y < height; y++)
                        {
                            var pixels = accessor.GetRowSpan(y);
                            for (var x = 0; x < width; x++)
                            {
                                var p = pixels[x];
                                var i = x * 4;
                                row[i + 0] = p.B;
                                row[i + 1] = p.G;
                                row[i + 2] = p.R;
                                row[i + 3] = p.A;
                            }
                            System.Runtime.InteropServices.Marshal.Copy(
                                row, 0, address + y * stride, Math.Min(stride, width * 4));
                        }
                    });
                }

                return writeable;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static BitmapSource FromImage(Image<Bgra32>? image)
        {
            var source = new BitmapSource { Image = image };
            if (image is not null)
            {
                source.PixelWidth = image.Width;
                source.PixelHeight = image.Height;
            }
            return source;
        }

        /// <summary>Decodes a file or stream to BGRA32, optionally downscaled on the way in.</summary>
        internal static Image<Bgra32>? Decode(Stream? stream, string? path, int decodeWidth, int decodeHeight)
        {
            try
            {
                Image<Bgra32> image;
                if (stream is not null)
                {
                    if (stream.CanSeek) stream.Position = 0;
                    image = SixLabors.ImageSharp.Image.Load<Bgra32>(stream);
                }
                else if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    image = SixLabors.ImageSharp.Image.Load<Bgra32>(path);
                }
                else
                {
                    return null;
                }

                // WPF's DecodePixelWidth/Height scale during decode; one axis given means
                // "preserve aspect ratio", which is exactly ImageSharp's Resize(w, 0) behaviour.
                if (decodeWidth > 0 || decodeHeight > 0)
                {
                    var w = decodeWidth > 0 ? decodeWidth : 0;
                    var h = decodeHeight > 0 ? decodeHeight : 0;
                    if (w != image.Width || h != image.Height)
                        image.Mutate(ctx => ctx.Resize(w, h));
                }

                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// WPF's BeginInit/EndInit decode wrapper. Set UriSource or StreamSource between the two
    /// calls, optionally with DecodePixelWidth/Height, then read the pixels.
    /// </summary>
    public class BitmapImage : BitmapSource
    {
        public int DecodePixelWidth { get; set; }
        public int DecodePixelHeight { get; set; }
        public Uri? UriSource { get; set; }
        public Stream? StreamSource { get; set; }
        public BitmapCacheOption CacheOption { get; set; }
        public BitmapCreateOptions CreateOptions { get; set; }

        public BitmapImage() { }

        public BitmapImage(Uri uriSource)
        {
            UriSource = uriSource;
            EndInit();
        }

        public void BeginInit() { }

        public void EndInit()
        {
            string? path = null;
            if (UriSource is not null)
                path = UriSource.IsAbsoluteUri ? UriSource.LocalPath : UriSource.ToString();

            Image = Decode(StreamSource, path, DecodePixelWidth, DecodePixelHeight);
            if (Image is not null)
            {
                PixelWidth = Image.Width;
                PixelHeight = Image.Height;
            }
        }
    }

    /// <summary>A single decoded frame. WPF exposes these through static Create overloads.</summary>
    public class BitmapFrame : BitmapSource
    {
        public static BitmapFrame Create(Stream stream) =>
            Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        public static BitmapFrame Create(Stream stream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
        {
            var frame = new BitmapFrame();

            // DelayCreation with no caching means the caller only wants the dimensions;
            // Identify reads the header instead of decoding the whole image.
            if (createOptions.HasFlag(BitmapCreateOptions.DelayCreation) && cacheOption == BitmapCacheOption.None)
            {
                try
                {
                    if (stream.CanSeek) stream.Position = 0;
                    var info = SixLabors.ImageSharp.Image.Identify(stream);
                    frame.PixelWidth = info.Width;
                    frame.PixelHeight = info.Height;
                    return frame;
                }
                catch (Exception)
                {
                    // Fall through to a full decode.
                }
            }

            frame.Image = Decode(stream, null, 0, 0);
            if (frame.Image is not null)
            {
                frame.PixelWidth = frame.Image.Width;
                frame.PixelHeight = frame.Image.Height;
            }
            return frame;
        }

        public static BitmapFrame Create(Uri uri) =>
            Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        public static BitmapFrame Create(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
        {
            var path = uri.IsAbsoluteUri ? uri.LocalPath : uri.ToString();
            var frame = new BitmapFrame { Image = Decode(null, path, 0, 0) };
            if (frame.Image is not null)
            {
                frame.PixelWidth = frame.Image.Width;
                frame.PixelHeight = frame.Image.Height;
            }
            return frame;
        }

        /// <summary>Wraps an existing source, as the encoders' Frames.Add(...) calls do.</summary>
        public static BitmapFrame Create(BitmapSource source)
        {
            return new BitmapFrame
            {
                Image = source.Image?.Clone(),
                PixelWidth = source.PixelWidth,
                PixelHeight = source.PixelHeight
            };
        }
    }

    /// <summary>
    /// WPF converts between pixel layouts here. Everything in this shim is already BGRA32,
    /// so this copies the source through unchanged.
    /// </summary>
    public class FormatConvertedBitmap : BitmapSource
    {
        public FormatConvertedBitmap() { }

        public FormatConvertedBitmap(BitmapSource source, PixelFormat destinationFormat, object? destinationPalette, double alphaThreshold)
        {
            Image = source.Image?.Clone();
            PixelWidth = source.PixelWidth;
            PixelHeight = source.PixelHeight;
        }
    }

    /// <summary>A rectangular region of another source.</summary>
    public class CroppedBitmap : BitmapSource
    {
        public CroppedBitmap(BitmapSource source, System.Windows.Int32Rect sourceRect)
        {
            if (source.Image is null) return;

            var x = Math.Clamp(sourceRect.X, 0, Math.Max(0, source.PixelWidth - 1));
            var y = Math.Clamp(sourceRect.Y, 0, Math.Max(0, source.PixelHeight - 1));
            var w = Math.Clamp(sourceRect.Width, 1, source.PixelWidth - x);
            var h = Math.Clamp(sourceRect.Height, 1, source.PixelHeight - y);

            Image = source.Image.Clone(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
            PixelWidth = Image.Width;
            PixelHeight = Image.Height;
        }
    }

    /// <summary>A scaled copy of another source.</summary>
    public class TransformedBitmap : BitmapSource
    {
        public TransformedBitmap(BitmapSource source, Transform transform)
        {
            if (source.Image is null) return;

            var size = transform.Apply(source.PixelWidth, source.PixelHeight);
            Image = source.Image.Clone(ctx => ctx.Resize(size.Width, size.Height));
            PixelWidth = Image.Width;
            PixelHeight = Image.Height;
        }
    }

    /// <summary>Common surface of the WPF encoders: add frames, then Save to a stream.</summary>
    public abstract class BitmapEncoder
    {
        public IList<BitmapFrame> Frames { get; } = new List<BitmapFrame>();

        public void Save(Stream stream)
        {
            if (Frames.Count == 0) throw new InvalidOperationException("No frames to encode.");
            var image = Frames[0].Image
                ?? throw new InvalidOperationException("The frame has no decoded pixels.");
            Encode(image, stream);
        }

        protected abstract void Encode(Image<Bgra32> image, Stream stream);
    }

    public class PngBitmapEncoder : BitmapEncoder
    {
        protected override void Encode(Image<Bgra32> image, Stream stream) =>
            image.Save(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
    }

    public class JpegBitmapEncoder : BitmapEncoder
    {
        /// <summary>WPF's 0-100 quality scale, matching ImageSharp's.</summary>
        public int QualityLevel { get; set; } = 75;

        protected override void Encode(Image<Bgra32> image, Stream stream)
        {
            // JPEG has no alpha; flatten onto black the way WPF's encoder does.
            using var opaque = image.Clone(ctx => ctx.BackgroundColor(Color.Black));
            opaque.Save(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
            {
                Quality = Math.Clamp(QualityLevel, 1, 100)
            });
        }
    }

    /// <summary>Multi-frame decoding entry point; the ported code only reads Frames[0].</summary>
    public class BitmapDecoder
    {
        public IList<BitmapFrame> Frames { get; } = new List<BitmapFrame>();

        public static BitmapDecoder Create(Stream stream, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
        {
            var decoder = new BitmapDecoder();
            decoder.Frames.Add(BitmapFrame.Create(stream, createOptions, cacheOption));
            return decoder;
        }

        public static BitmapDecoder Create(Uri uri, BitmapCreateOptions createOptions, BitmapCacheOption cacheOption)
        {
            var decoder = new BitmapDecoder();
            decoder.Frames.Add(BitmapFrame.Create(uri, createOptions, cacheOption));
            return decoder;
        }
    }
}
