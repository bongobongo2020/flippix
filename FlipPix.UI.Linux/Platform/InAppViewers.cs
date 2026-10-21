using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FlipPix.UI.Linux.Controls;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// FlipPix's own image viewer, video player and folder browser. On a Chromebook they stand in
    /// for the desktop apps, which Linux apps can't reach: the shell hands every file and folder
    /// to Chrome, and Chrome can't read the Linux container's files.
    /// </summary>
    public static class InAppViewers
    {
        public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tiff", ".tif" };

        public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v" };

        public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
        public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>Points the shared view models' open-image/folder/video calls at these viewers.</summary>
        public static void Register()
        {
            ImageOpener.Handler = path => { ShowImage(path); return true; };
            VideoOpener.Handler = PlayVideo;
            FolderOpener.Handler = (folder, select) => { BrowseFolder(folder, select); return true; };
        }

        public static void ShowImage(string path) => Show(() => new ImageViewerWindow(path));

        /// <summary>False when libvlc isn't installed, so the caller falls back to the desktop.</summary>
        public static bool PlayVideo(string path)
        {
            if (!VideoPreview.PlaybackAvailable) return false;
            Show(() => new VideoPlayerWindow(path));
            return true;
        }

        /// <summary>Browses <paramref name="folder"/>, with <paramref name="selectPath"/> selected
        /// and scrolled into view when given.</summary>
        public static void BrowseFolder(string folder, string? selectPath = null)
            => Show(() => ImagePickerWindow.ForBrowsing(folder, selectPath));

        /// <summary>Opens a file in the matching viewer. False for other kinds of file.</summary>
        public static bool TryOpenFile(string path)
        {
            if (IsImage(path)) { ShowImage(path); return true; }
            if (IsVideo(path)) return PlayVideo(path);
            return false;
        }

        // Callers may be on any thread; windows are made on the UI thread, owned by the active one.
        private static void Show(Func<Window> create) => Dispatcher.UIThread.Post(() =>
        {
            var window = create();
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
            if (owner != null && owner.IsVisible && !ReferenceEquals(owner, window)) window.Show(owner);
            else window.Show();
        });
    }
}
