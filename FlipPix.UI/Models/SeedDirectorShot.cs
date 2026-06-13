using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One "shot" on the Seed Director timeline — an LTX Director-style keyframe (image + prompt +
    /// duration) fused with a SeedHunt batch: each shot owns its own 4 seed-sample previews, the
    /// batch seed that produced them, and its uploaded-image handle. Shots lay out left→right as
    /// video time; the user picks one (or more) seeds per shot before the final join.
    /// </summary>
    public partial class SeedDirectorShot : ObservableObject
    {
        public SeedDirectorShot() { }

        public SeedDirectorShot(string imagePath)
        {
            _imagePath = imagePath;
            LoadThumbnail();
        }

        [ObservableProperty] private string _imagePath = string.Empty;

        [ObservableProperty] private string _prompt = string.Empty;

        /// <summary>Length of this shot in seconds (drives the Stage-1 latent frame count).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DurationLabel))]
        private double _durationSeconds = 3.0;

        /// <summary>1-based position shown on the card; set by the VM when the list changes.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IndexLabel))]
        private int _index;

        /// <summary>Timeline selection (which shot's 4-sample grid is shown).</summary>
        [ObservableProperty] private bool _isSelected;

        /// <summary>Short status chip ("", "hunting…", "4 seeds", "✓ selected").</summary>
        [ObservableProperty] private string _status = string.Empty;

        /// <summary>The 4 low-res seed previews for this shot (slots 1-4).</summary>
        public ObservableCollection<SeedHuntSample> Samples { get; } = new()
        {
            new SeedHuntSample(1), new SeedHuntSample(2),
            new SeedHuntSample(3), new SeedHuntSample(4),
        };

        /// <summary>The Stage-1 batch seed that produced the on-screen 4 samples (reused at finish
        /// for the cached-latent hit). -1 until the shot has been hunted.</summary>
        public long BatchSeed { get; set; } = -1;

        /// <summary>Cached ComfyUI upload filename for this shot's image (uploaded once).</summary>
        public string? UploadedName { get; set; }

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            private set => SetProperty(ref _thumbnail, value);
        }

        public string FileName => string.IsNullOrEmpty(ImagePath) ? string.Empty : Path.GetFileName(ImagePath);
        public string IndexLabel => $"#{Index}";
        public string DurationLabel => $"{DurationSeconds:0.0}s";

        public bool HasSamples => Samples.Any(s => s.HasVideo);

        /// <summary>Checked seed slots (1-based) that have a video, ordered by slot.</summary>
        public IReadOnlyList<int> SelectedSlots =>
            Samples.Where(s => s.IsSelected && s.HasVideo).OrderBy(s => s.Slot).Select(s => s.Slot).ToList();

        public bool HasSelectedSeed => SelectedSlots.Count > 0;

        /// <summary>Clears the 4 previews (used by reroll / re-hunt). Keeps image/prompt/duration.</summary>
        public void ResetSamples()
        {
            foreach (var s in Samples) s.Reset();
        }

        private void LoadThumbnail()
        {
            if (string.IsNullOrEmpty(ImagePath) || !File.Exists(ImagePath))
            {
                Thumbnail = null;
                return;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ImagePath, UriKind.Absolute);
                bmp.DecodePixelHeight = 160;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                Thumbnail = bmp;
            }
            catch
            {
                Thumbnail = null;
            }
        }
    }
}
