using AVFoundation;
using AVKit;
using Avalonia.iOS;
using Avalonia.Platform;
using CoreMedia;
using FlipPix.Mobile.Controls;
using Foundation;
using UIKit;

namespace FlipPix.Mobile.iOS;

/// <summary>
/// AVPlayerViewController with the system transport controls, streaming from the paired computer (the
/// token rides in the URL, as on Android). A single video loops; a playlist (a story's shots) plays
/// through and starts again.
/// </summary>
public sealed class IosVideoSurface : IVideoSurfaceFactory
{
    public IPlatformHandle Create(IPlatformHandle parent, IReadOnlyList<string> urls)
    {
        var host = new PlayerHost();
        host.Load(urls);
        return new UIViewControlHandle(host);
    }

    public void SetSources(IPlatformHandle handle, IReadOnlyList<string> urls)
    {
        if (handle is UIViewControlHandle { View: PlayerHost host }) host.Load(urls);
    }

    public void Destroy(IPlatformHandle handle)
    {
        if (handle is UIViewControlHandle { View: PlayerHost host }) host.Teardown();
    }

    /// <summary>
    /// The controller's view, adopted by the window's root controller once it is on screen: an
    /// AVPlayerViewController left without a parent draws, but its full-screen and PiP buttons do nothing.
    /// </summary>
    private sealed class PlayerHost : UIView
    {
        private readonly AVPlayer _player = new() { ActionAtItemEnd = AVPlayerActionAtItemEnd.None };
        private readonly AVPlayerViewController _controller;
        private NSObject? _endObserver;
        private IReadOnlyList<string> _urls = Array.Empty<string>();
        private int _index;

        public PlayerHost()
        {
            BackgroundColor = UIColor.Black;
            _controller = new AVPlayerViewController { Player = _player, ShowsPlaybackControls = true };
            var view = _controller.View!;
            view.BackgroundColor = UIColor.Black;
            view.Frame = Bounds;
            view.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
            AddSubview(view);
            _endObserver = AVPlayerItem.Notifications.ObserveDidPlayToEndTime((_, e) =>
            {
                if (e.Notification.Object is AVPlayerItem item && ReferenceEquals(item, _player.CurrentItem)) OnEnded();
            });
        }

        public override void MovedToWindow()
        {
            base.MovedToWindow();
            if (Window?.RootViewController is { } root && _controller.ParentViewController == null)
            {
                root.AddChildViewController(_controller);
                _controller.DidMoveToParentViewController(root);
            }
            else if (Window == null && _controller.ParentViewController != null)
            {
                _controller.WillMoveToParentViewController(null);
                _controller.RemoveFromParentViewController();
            }
        }

        public void Load(IReadOnlyList<string> urls)
        {
            _player.Pause();
            _urls = urls;
            _index = 0;
            PlayCurrent();
        }

        private void PlayCurrent()
        {
            if (_urls.Count == 0)
            {
                _player.ReplaceCurrentItemWithPlayerItem(null);
                return;
            }
            var url = NSUrl.FromString(_urls[_index]);
            if (url == null) return;
            _player.ReplaceCurrentItemWithPlayerItem(new AVPlayerItem(url));
            _player.Play();
        }

        private void OnEnded()
        {
            if (_urls.Count <= 1)
            {
                _player.Seek(CMTime.Zero);
                _player.Play();
                return;
            }
            _index = (_index + 1) % _urls.Count;
            PlayCurrent();
        }

        public void Teardown()
        {
            _player.Pause();
            _player.ReplaceCurrentItemWithPlayerItem(null);
            _endObserver?.Dispose();
            _endObserver = null;
            _controller.WillMoveToParentViewController(null);
            _controller.View?.RemoveFromSuperview();
            _controller.RemoveFromParentViewController();
            RemoveFromSuperview();
        }
    }
}
