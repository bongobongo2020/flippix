// WPF value types and dialogs that the shared view models (FlipPix.UI/ViewModels, compiled into
// this project unchanged) expose or call. They are plain data here; the views turn them into
// Avalonia values through Converters/WpfValueConverter.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace System.Windows
{
    public enum Visibility { Visible = 0, Hidden = 1, Collapsed = 2 }

    public enum WindowStartupLocation { Manual, CenterScreen, CenterOwner }

    public enum HorizontalAlignment { Left, Center, Right, Stretch }

    public enum VerticalAlignment { Top, Center, Bottom, Stretch }

    public enum TextWrapping { WrapWithOverflow, NoWrap, Wrap }

    public readonly struct Thickness
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }

        public Thickness(double left, double top, double right, double bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }
    }

    /// <summary>WPF's Duration, as MediaElement.NaturalDuration reports it.</summary>
    public readonly struct Duration
    {
        private readonly TimeSpan? _span;
        public Duration(TimeSpan span) => _span = span;
        public static Duration Automatic => default;
        public bool HasTimeSpan => _span.HasValue;
        public TimeSpan TimeSpan => _span ?? throw new InvalidOperationException("Duration has no TimeSpan");
    }

    public readonly struct FontWeight
    {
        internal FontWeight(int weight) => Weight = weight;
        public int Weight { get; }
    }

    public static class FontWeights
    {
        public static FontWeight Normal => new(400);
        public static FontWeight Medium => new(500);
        public static FontWeight SemiBold => new(600);
        public static FontWeight Bold => new(700);
    }
}

namespace System.Windows.Media
{
    public readonly struct Color : IEquatable<Color>
    {
        public byte A { get; init; }
        public byte R { get; init; }
        public byte G { get; init; }
        public byte B { get; init; }

        public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };

        public bool Equals(Color other) => A == other.A && R == other.R && G == other.G && B == other.B;
        public override bool Equals(object? obj) => obj is Color c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(A, R, G, B);
        public static bool operator ==(Color a, Color b) => a.Equals(b);
        public static bool operator !=(Color a, Color b) => !a.Equals(b);
        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }

    public static class Colors
    {
        public static Color Transparent => Color.FromArgb(0, 255, 255, 255);
        public static Color White => Color.FromRgb(255, 255, 255);
        public static Color Black => Color.FromRgb(0, 0, 0);
        public static Color Red => Color.FromRgb(255, 0, 0);
        public static Color Green => Color.FromRgb(0, 128, 0);
        public static Color Orange => Color.FromRgb(255, 165, 0);
        public static Color Gray => Color.FromRgb(128, 128, 128);
    }

    public abstract class Brush
    {
        public bool IsFrozen { get; private set; }
        public void Freeze() => IsFrozen = true;
        public abstract Avalonia.Media.IBrush ToAvalonia();
    }

    public sealed class SolidColorBrush : Brush
    {
        public SolidColorBrush() { }
        public SolidColorBrush(Color color) => Color = color;

        public Color Color { get; set; }

        public override Avalonia.Media.IBrush ToAvalonia()
            => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));

        public override string ToString() => Color.ToString();
    }

    public static class Brushes
    {
        public static SolidColorBrush Transparent => new(Colors.Transparent);
        public static SolidColorBrush White => new(Colors.White);
        public static SolidColorBrush Black => new(Colors.Black);
        public static SolidColorBrush Red => new(Colors.Red);
        public static SolidColorBrush Green => new(Colors.Green);
        public static SolidColorBrush LimeGreen => new(Color.FromRgb(50, 205, 50));
        public static SolidColorBrush Orange => new(Colors.Orange);
        public static SolidColorBrush Gray => new(Colors.Gray);
        public static SolidColorBrush Gold => new(Color.FromRgb(255, 215, 0));
    }
}

namespace Microsoft.Win32
{
    /// <summary>
    /// WPF's synchronous file dialogs. A few shared view models still call these directly;
    /// they run the Avalonia storage picker in a nested dispatcher frame, so the call blocks
    /// the way the WPF one does while the UI keeps rendering.
    /// </summary>
    public abstract class FileDialog
    {
        public string Title { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public int FilterIndex { get; set; } = 1;
        public string InitialDirectory { get; set; } = string.Empty;
        public string DefaultExt { get; set; } = string.Empty;
        public bool AddExtension { get; set; } = true;
        public bool CheckFileExists { get; set; }
        public bool CheckPathExists { get; set; }
        public bool RestoreDirectory { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string[] FileNames { get; protected set; } = Array.Empty<string>();

        protected static TopLevel? ActiveTopLevel()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return null;
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }

        protected List<FilePickerFileType> ParseFilter()
        {
            var parts = Filter.Split('|');
            var result = new List<FilePickerFileType>();
            for (var i = 0; i + 1 < parts.Length; i += 2)
                result.Add(new FilePickerFileType(parts[i])
                {
                    Patterns = parts[i + 1].Split(';').Select(p => p.Trim()).ToArray()
                });
            return result;
        }

        protected async System.Threading.Tasks.Task<IStorageFolder?> StartFolderAsync(TopLevel top)
        {
            var dir = InitialDirectory;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(FileName))
                dir = System.IO.Path.GetDirectoryName(FileName) ?? string.Empty;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return null;
            return await top.StorageProvider.TryGetFolderFromPathAsync(dir);
        }

        protected abstract System.Threading.Tasks.Task<bool> RunAsync(TopLevel top);

        public bool? ShowDialog() => ShowDialog(null);

        public bool? ShowDialog(object? owner)
        {
            var top = ActiveTopLevel();
            if (top == null) return false;

            if (!Dispatcher.UIThread.CheckAccess())
                return Dispatcher.UIThread.InvokeAsync(() => RunAsync(top)).GetAwaiter().GetResult();

            var accepted = false;
            var frame = new DispatcherFrame();
            _ = RunAsync(top).ContinueWith(t =>
            {
                accepted = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result;
                frame.Continue = false;
            }, System.Threading.Tasks.TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            return accepted;
        }
    }

    public sealed class OpenFileDialog : FileDialog
    {
        public bool Multiselect { get; set; }

        protected override async System.Threading.Tasks.Task<bool> RunAsync(TopLevel top)
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Title,
                AllowMultiple = Multiselect,
                FileTypeFilter = ParseFilter(),
                SuggestedStartLocation = await StartFolderAsync(top)
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToArray();
            if (paths.Length == 0) return false;
            FileNames = paths;
            FileName = paths[0];
            return true;
        }
    }

    public sealed class SaveFileDialog : FileDialog
    {
        public bool OverwritePrompt { get; set; } = true;

        protected override async System.Threading.Tasks.Task<bool> RunAsync(TopLevel top)
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Title,
                FileTypeChoices = ParseFilter(),
                DefaultExtension = DefaultExt,
                SuggestedFileName = System.IO.Path.GetFileName(FileName),
                SuggestedStartLocation = await StartFolderAsync(top),
                ShowOverwritePrompt = OverwritePrompt
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return false;
            FileName = path;
            FileNames = new[] { path };
            return true;
        }
    }
}
