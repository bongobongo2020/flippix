using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FlipPix.Mobile.Controls;

/// <summary>What a platform head provides to put a real video player on screen.</summary>
public interface IVideoSurfaceFactory
{
    /// <summary>Creates the native player; <paramref name="urls"/> may be empty until a source is set.</summary>
    IPlatformHandle Create(IPlatformHandle parent, IReadOnlyList<string> urls);

    /// <summary>Plays the list in order, then from the top again. One URL simply loops.</summary>
    void SetSources(IPlatformHandle handle, IReadOnlyList<string> urls);

    void Destroy(IPlatformHandle handle);
}

/// <summary>
/// Plays streamed video (ComfyUI's /view URLs) in a native player. Avalonia has no video element,
/// so the platform head registers a factory (Android: VideoView). Where none is registered, such as
/// the desktop preview or headless screenshots, this is an empty rectangle and the page shows its own
/// fallback. Native views draw above Avalonia content, so nothing may be layered over this control.
/// </summary>
public class VideoSurface : NativeControlHost
{
    public static IVideoSurfaceFactory? Factory { get; set; }
    public static bool IsSupported => Factory != null;

    /// <summary>A single video. Setting it replaces <see cref="Sources"/>.</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<VideoSurface, string?>(nameof(Source));

    /// <summary>A playlist: a story's clips, played back to back.</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> SourcesProperty =
        AvaloniaProperty.Register<VideoSurface, IReadOnlyList<string>?>(nameof(Sources));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IReadOnlyList<string>? Sources
    {
        get => GetValue(SourcesProperty);
        set => SetValue(SourcesProperty, value);
    }

    private IPlatformHandle? _handle;

    private IReadOnlyList<string> Urls =>
        Sources is { Count: > 0 } list ? list
        : string.IsNullOrEmpty(Source) ? Array.Empty<string>() : new[] { Source! };

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (Factory == null) return base.CreateNativeControlCore(parent);
        _handle = Factory.Create(parent, Urls);
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (Factory != null && ReferenceEquals(control, _handle)) Factory.Destroy(control);
        else base.DestroyNativeControlCore(control);
        _handle = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == SourceProperty || change.Property == SourcesProperty) && _handle != null)
            Factory?.SetSources(_handle, Urls);
    }
}
