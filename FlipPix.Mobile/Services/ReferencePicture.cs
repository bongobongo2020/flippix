using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace FlipPix.Mobile.Services;

/// <summary>
/// A photo picked on the phone, shrunk once to what anything downstream can use. A phone camera
/// frame is 12–50 MP; the video model encodes references at its draft canvas and the LLM looks at
/// them at ~1 MP, so sending the original would only cost upload time on mobile data.
///
/// <para>ImageSharp, not Skia, because a phone photo's rotation lives in its EXIF tag: decoded
/// without <c>AutoOrient</c>, every portrait shot reaches the model lying on its side.</para>
/// </summary>
public sealed class ReferencePicture : IDisposable
{
    private const int MaxSide = 1536;

    public required Bitmap Thumbnail { get; init; }
    public required byte[] Jpeg { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string? UploadedName { get; set; }

    public static ReferencePicture FromStream(Stream source)
    {
        using var image = SixLabors.ImageSharp.Image.Load(source);
        image.Mutate(x => x.AutoOrient());
        image.Metadata.ExifProfile = null; // location and device data stay on the phone
        if (Math.Max(image.Width, image.Height) > MaxSide)
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxSide, MaxSide), Mode = ResizeMode.Max }));

        // JPEG at 92: several times faster to encode than PNG on a phone CPU and a fifth of the upload,
        // with no difference the model can see at its reference size.
        using var jpeg = new MemoryStream();
        image.SaveAsJpeg(jpeg, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
        var bytes = jpeg.ToArray();

        using var thumbSource = new MemoryStream(bytes);
        return new ReferencePicture
        {
            Thumbnail = Bitmap.DecodeToWidth(thumbSource, 320),
            Jpeg = bytes,
            Width = image.Width,
            Height = image.Height,
        };
    }

    public void Dispose() => Thumbnail.Dispose();
}
