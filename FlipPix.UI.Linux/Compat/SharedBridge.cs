// Small bridges between the shared view models and this app.

using System;
using System.Threading;

namespace FlipPix.UI
{
    /// <summary>
    /// The WPF App's static surface the shared view models read (App.ShutdownToken), forwarded to
    /// the Avalonia application.
    /// </summary>
    internal static class App
    {
        public static CancellationToken ShutdownToken => Linux.App.ShutdownToken;
    }
}

namespace System.Drawing
{
    /// <summary>
    /// System.Drawing.Image as the shared code uses it: open a file to read its pixel size.
    /// System.Drawing.Common is Windows-only, so this reads the header with ImageSharp.
    /// </summary>
    public sealed class Image : IDisposable
    {
        private Image(int width, int height) { Width = width; Height = height; }

        public int Width { get; }
        public int Height { get; }

        public static Image FromFile(string path)
        {
            var info = SixLabors.ImageSharp.Image.Identify(path);
            return new Image(info.Width, info.Height);
        }

        public void Dispose() { }
    }
}
