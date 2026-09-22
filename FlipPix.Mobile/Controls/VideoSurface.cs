using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace FlipPix.Mobile.Controls;

/// <summary>What a platform head provides to put a real video player on screen.</summary>
public interface IVideoSurfaceFactory
{
    /// <summary>Creates the native player view; <paramref name="url"/> may be null until a source is set.</summary>
    IPlatformHandle Create(IPlatformHandle parent, string? url);
    void SetSource(IPlatformHandle handle, string? url);
    void Destroy(IPlatformHandle handle);
}

/// <summary>
/// Plays a streamed video (ComfyUI's /view URL) in a native player. Avalonia has no video element,
/// so the platform head registers a factory (Android: VideoView). Where none is registered — the
/// desktop preview, headless screenshots — this is an empty rectangle and the page shows its own
/// fallback. Native views draw above Avalonia content, so nothing may be layered over this control.
/// </summary>
public class VideoSurface : NativeControlHost
{
    public static IVideoSurfaceFactory? Factory { get; set; }
    public static bool IsSupported => Factory != null;

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<VideoSurface, string?>(nameof(Source));

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private IPlatformHandle? _handle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (Factory == null) return base.CreateNativeControlCore(parent);
        _handle = Factory.Create(parent, Source);
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
        if (change.Property == SourceProperty && _handle != null) Factory?.SetSource(_handle, Source);
    }
}
