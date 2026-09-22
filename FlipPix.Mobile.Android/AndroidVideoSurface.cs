using Android.Content;
using Android.Media;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Platform;
using FlipPix.Mobile.Controls;
using AUri = Android.Net.Uri;

namespace FlipPix.Mobile.Android;

/// <summary>
/// VideoView with the system transport controls, streaming straight from ComfyUI's /view URL.
/// Loops, because a take is short and is watched more than once.
/// </summary>
public sealed class AndroidVideoSurface : IVideoSurfaceFactory
{
    private readonly Context _context;

    public AndroidVideoSurface(Context context) => _context = context;

    public IPlatformHandle Create(IPlatformHandle parent, string? url)
    {
        var view = new VideoView(_context);
        var controls = new MediaController(_context);
        controls.SetAnchorView(view);
        view.SetMediaController(controls);
        view.SetOnPreparedListener(new LoopOnPrepared(view));
        Load(view, url);
        return new AndroidViewControlHandle(view);
    }

    public void SetSource(IPlatformHandle handle, string? url)
    {
        if (handle is AndroidViewControlHandle { View: VideoView view }) Load(view, url);
    }

    public void Destroy(IPlatformHandle handle)
    {
        if (handle is AndroidViewControlHandle { View: VideoView view })
        {
            view.StopPlayback();
            view.Dispose();
        }
    }

    private static void Load(VideoView view, string? url)
    {
        view.StopPlayback();
        if (!string.IsNullOrEmpty(url)) view.SetVideoURI(AUri.Parse(url));
    }
}

/// <summary>Starts playback as soon as the stream is ready, looping.</summary>
internal sealed class LoopOnPrepared : Java.Lang.Object, MediaPlayer.IOnPreparedListener
{
    private readonly VideoView _view;
    public LoopOnPrepared(VideoView view) => _view = view;

    public void OnPrepared(MediaPlayer? mp)
    {
        if (mp != null) mp.Looping = true;
        _view.Start();
    }
}
