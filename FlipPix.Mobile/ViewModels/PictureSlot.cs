using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

/// <summary>
/// A reference picture waiting on the Video or Story page: a photo from this phone (sent to the
/// computer once, when first needed) or a picture already on the computer, chosen in the Library.
/// </summary>
public sealed partial class PictureSlot : ObservableObject
{
    private readonly ReferencePicture? _photo;
    private readonly string? _libraryId;

    private PictureSlot(ReferencePicture? photo, string? libraryId, Bitmap? thumbnail)
    {
        _photo = photo;
        _libraryId = libraryId;
        _thumbnail = thumbnail;
    }

    public static PictureSlot FromPhone(ReferencePicture photo) => new(photo, null, photo.Thumbnail);

    public static PictureSlot FromLibrary(string libraryId, Bitmap? thumbnail) => new(null, libraryId, thumbnail);

    [ObservableProperty] private Bitmap? _thumbnail;

    /// <summary>The photo's bytes, when it came from this phone (the writing assistant reads them).</summary>
    public ReferencePicture? Photo => _photo;

    /// <summary>The reference a job request carries: "upload:…" or "library:…". Uploads on first use.</summary>
    public async Task<string> ReferenceAsync(CancellationToken ct = default)
    {
        if (_libraryId != null) return "library:" + _libraryId;
        // Sent once per computer: pairing with another one means sending it again.
        if (_uploadId == null || _uploadedTo != AppServices.Remote.BaseUrl)
        {
            _uploadId = (await AppServices.Remote.UploadAsync(_photo!.Jpeg, ct)).Id;
            _uploadedTo = AppServices.Remote.BaseUrl;
        }
        return "upload:" + _uploadId;
    }

    private string? _uploadId;
    private string? _uploadedTo;
}
