using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>What a tile on the H3 Batch Upscale board can ask its tab to do.</summary>
    public interface IH3BatchUpscaleHost
    {
        /// <summary>Plays this video in the shared player.</summary>
        void PlayVideo(H3BatchUpscaleItem? item);

        /// <summary>Toggles selection for upscale.</summary>
        void ToggleVideo(H3BatchUpscaleItem? item);

        /// <summary>Plays the upscaled video if available.</summary>
        void PlayUpscaled(H3BatchUpscaleItem? item);

        /// <summary>Opens the folder containing the video.</summary>
        void RevealVideo(H3BatchUpscaleItem? item);
    }

    /// <summary>
    /// One preview video found by the folder scan for batch upscaling.
    /// Represents a video clip ready for H3 latent upscaling.
    /// </summary>
    public partial class H3BatchUpscaleItem : ObservableObject
    {
        private readonly IH3BatchUpscaleHost _host;

        public H3BatchUpscaleItem(IH3BatchUpscaleHost host, string videoPath)
        {
            _host = host;
            VideoPath = videoPath;
            FileName = Path.GetFileNameWithoutExtension(videoPath);

            // Try to get video info
            var info = new FileInfo(videoPath);
            FileSize = FormatFileSize(info.Length);
            CreatedTime = info.CreationTime.ToString("d MMM HH:mm");

            PlayCommand = new RelayCommand(() => _host.PlayVideo(this));
            ToggleCommand = new RelayCommand(() => _host.ToggleVideo(this));
            PlayUpscaledCommand = new RelayCommand(() => _host.PlayUpscaled(this), () => HasUpscale);
            RevealCommand = new RelayCommand(() => _host.RevealVideo(this));
        }

        /// <summary>Full path to the video file.</summary>
        public string VideoPath { get; }

        /// <summary>The filename without extension.</summary>
        public string FileName { get; }

        /// <summary>Formatted file size.</summary>
        public string FileSize { get; }

        /// <summary>When the file was created.</summary>
        public string CreatedTime { get; }

        /// <summary>What the scan sorts and de-duplicates on.</summary>
        public string Key => VideoPath;

        /// <summary>Display title for the tile.</summary>
        public string Title => FileName;

        /// <summary>Details line under the title.</summary>
        public string Details => $"{FileSize} · {CreatedTime}";

        /// <summary>Everything the search box matches against.</summary>
        public string SearchText => $"{FileName} {Path.GetDirectoryName(VideoPath)}";

        // ── State ───────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty] private BitmapImage? _thumbnail;

        [ObservableProperty] private bool _isSelected;

        [ObservableProperty] private bool _isBusy;

        [ObservableProperty] private string _status = string.Empty;

        /// <summary>The upscaled video path, once this item has been upscaled.</summary>
        [ObservableProperty] private string? _upscaledPath;

        public bool HasUpscale => !string.IsNullOrEmpty(UpscaledPath) && File.Exists(UpscaledPath);

        partial void OnUpscaledPathChanged(string? value)
        {
            OnPropertyChanged(nameof(HasUpscale));
            PlayUpscaledCommand.NotifyCanExecuteChanged();
        }

        public RelayCommand PlayCommand { get; }
        public RelayCommand ToggleCommand { get; }
        public RelayCommand PlayUpscaledCommand { get; }
        public RelayCommand RevealCommand { get; }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }
}
