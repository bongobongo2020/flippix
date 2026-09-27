using System.Collections.Concurrent;
using FlipPix.Remote.Engine;
using FlipPix.Remote.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace FlipPix.Remote.Jobs;

/// <summary>A reference picture ready for a job: its JPEG bytes and its shape.</summary>
public sealed record Picture(string Ref, byte[] Jpeg, int Width, int Height);

/// <summary>
/// Photos sent from the phone, kept on the desktop so a job can be retried (or its card redrawn)
/// after the phone has forgotten them. Also resolves <c>library:</c> references, so a picture already
/// in the output folder can be animated without a round trip through the phone.
///
/// <para>Every picture is normalised the same way whichever side it came from: EXIF rotation applied
/// then stripped, at most 1536 px, JPEG 92. The video model encodes references at its draft canvas and
/// the LLM looks at ~1 MP, so nothing is lost.</para>
/// </summary>
public sealed class UploadStore
{
    public const int MaxBytes = 40 * 1024 * 1024;
    private const int MaxSide = 1536;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    private readonly string _dir;
    private readonly LibraryIndex _library;
    // ComfyUI input names by (server, picture): each picture is uploaded to a server once.
    private readonly ConcurrentDictionary<string, string> _onServer = new();

    public UploadStore(string dir, LibraryIndex library)
    {
        _dir = dir;
        _library = library;
        Directory.CreateDirectory(dir);
    }

    /// <summary>Old uploads are removed at startup; jobs older than that are long gone from the list too.</summary>
    public void Prune()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_dir).EnumerateFiles())
                if (DateTime.UtcNow - f.LastWriteTimeUtc > KeepFor)
                    try { f.Delete(); } catch (IOException) { }
        }
        catch (Exception) { /* housekeeping only */ }
    }

    public async Task<(string Id, int Width, int Height)> SaveAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBytes) throw new InvalidDataException("That photo is larger than 40 MB.");
            buffer.Write(chunk, 0, n);
        }
        return await SaveBytesAsync(buffer.ToArray(), ct);
    }

    public async Task<(string Id, int Width, int Height)> SaveBytesAsync(byte[] bytes, CancellationToken ct)
    {
        var (jpeg, w, h) = await Task.Run(() => Normalize(bytes), ct);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(PathOf(id), jpeg, ct);
        await File.WriteAllBytesAsync(ThumbPathOf(id), Thumbnailer.FromBytes(jpeg, Thumbnailer.ThumbSide), ct);
        return (id, w, h);
    }

    public string? ThumbFor(string id) =>
        IsId(id) && File.Exists(ThumbPathOf(id)) ? ThumbPathOf(id) : null;

    /// <summary>The picture behind <c>upload:id</c> or <c>library:id</c>; throws with a readable reason.</summary>
    public async Task<Picture> ResolveAsync(string reference, CancellationToken ct)
    {
        if (reference.StartsWith("upload:", StringComparison.Ordinal))
        {
            var id = reference[7..];
            if (!IsId(id) || !File.Exists(PathOf(id)))
                throw new InvalidOperationException("A photo this job needs is no longer on the desktop. Add it again.");
            var bytes = await File.ReadAllBytesAsync(PathOf(id), ct);
            var info = Image.Identify(bytes);
            return new Picture(reference, bytes, info.Width, info.Height);
        }
        if (reference.StartsWith("library:", StringComparison.Ordinal))
        {
            var entry = _library.Find(reference[8..]);
            if (entry == null) throw new InvalidOperationException("That picture is no longer in the output folder.");
            if (entry.Kind != Contracts.MediaKinds.Image && entry.PosterPath == null)
                throw new InvalidOperationException("Only pictures can be used as references.");
            var source = await File.ReadAllBytesAsync(entry.Kind == Contracts.MediaKinds.Image ? entry.FullPath : entry.PosterPath!, ct);
            var (jpeg, w, h) = await Task.Run(() => Normalize(source), ct);
            return new Picture(reference, jpeg, w, h);
        }
        throw new InvalidOperationException("Unknown picture reference.");
    }

    /// <summary>Puts a picture in ComfyUI's input folder once per server and returns its name there.</summary>
    public async Task<string> EnsureOnServerAsync(ComfyGateway comfy, Picture picture, CancellationToken ct)
    {
        var key = comfy.Url + "|" + picture.Ref;
        if (_onServer.TryGetValue(key, out var name)) return name;
        name = await comfy.UploadJpegAsync(picture.Jpeg, ct);
        _onServer[key] = name;
        return name;
    }

    private static (byte[] Jpeg, int Width, int Height) Normalize(byte[] bytes)
    {
        Image image;
        try
        {
            image = Image.Load(bytes);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
        {
            throw new InvalidDataException("That file isn't a picture the desktop can read.");
        }
        using (image)
        {
            image.Mutate(x => x.AutoOrient());
            image.Metadata.ExifProfile = null; // location and device data stay private
            if (Math.Max(image.Width, image.Height) > MaxSide)
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxSide, MaxSide), Mode = ResizeMode.Max }));
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 92 });
            return (ms.ToArray(), image.Width, image.Height);
        }
    }

    private static bool IsId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);
    private string PathOf(string id) => Path.Combine(_dir, id + ".jpg");
    private string ThumbPathOf(string id) => Path.Combine(_dir, id + "_t.jpg");
}
