using Android.Content;
using Android.Media;
using Android.Widget;
using Avalonia.Android;
using Avalonia.Platform;
using FlipPix.Mobile.Controls;
using AUri = Android.Net.Uri;

namespace FlipPix.Mobile.Android;

/// <summary>
/// VideoView with the system transport controls, streaming straight from ComfyUI's /view URLs. A
/// single video loops; a playlist (a story's clips) plays through and starts again.
/// </summary>
public sealed class AndroidVideoSurface : IVideoSurfaceFactory
{
    private readonly Context _context;

    public AndroidVideoSurface(Context context) => _context = context;

    public IPlatformHandle Create(IPlatformHandle parent, IReadOnlyList<string> urls)
    {
        var view = new VideoView(_context);
        var controls = new MediaController(_context);
        controls.SetAnchorView(view);
        view.SetMediaController(controls);
        var player = new Playlist(view);
        view.Tag = player;
        view.SetOnPreparedListener(player);
        view.SetOnCompletionListener(player);
        player.Load(urls);
        return new AndroidViewControlHandle(view);
    }

    public void SetSources(IPlatformHandle handle, IReadOnlyList<string> urls)
    {
        if (handle is AndroidViewControlHandle { View: VideoView { Tag: Playlist player } }) player.Load(urls);
    }

    public void Destroy(IPlatformHandle handle)
    {
        if (handle is AndroidViewControlHandle { View: VideoView view })
        {
            view.StopPlayback();
            view.Dispose();
        }
    }

    private sealed class Playlist : Java.Lang.Object, MediaPlayer.IOnPreparedListener, MediaPlayer.IOnCompletionListener
    {
        private readonly VideoView _view;
        private IReadOnlyList<string> _urls = Array.Empty<string>();
        private int _index;

        public Playlist(VideoView view) => _view = view;

        public void Load(IReadOnlyList<string> urls)
        {
            _view.StopPlayback();
            _urls = urls;
            _index = 0;
            PlayCurrent();
        }

        private void PlayCurrent()
        {
            if (_urls.Count == 0) return;
            _view.SetVideoURI(AUri.Parse(_urls[_index]));
        }

        public void OnPrepared(MediaPlayer? mp)
        {
            if (mp != null) mp.Looping = _urls.Count == 1;
            _view.Start();
        }

        public void OnCompletion(MediaPlayer? mp)
        {
            if (_urls.Count <= 1) return;
            _index = (_index + 1) % _urls.Count;
            PlayCurrent();
        }
    }
}
