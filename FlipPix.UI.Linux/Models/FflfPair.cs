using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One overlapping first→last frame pair in a FFLF Seed Hunter folder batch
    /// (image i is the FIRST frame, image i+1 is the LAST frame). Each pair owns its
    /// analyzed prompt, the seed that produced its previews, and its three
    /// <see cref="SeedHuntSample"/> seed previews.
    /// </summary>
    public partial class FflfPair : ObservableObject
    {
        public FflfPair(int index, string firstImagePath, string lastImagePath)
        {
            Index = index;
            FirstImagePath = firstImagePath;
            LastImagePath = lastImagePath;
            Samples = new ObservableCollection<SeedHuntSample>
            {
                new SeedHuntSample(1), new SeedHuntSample(2), new SeedHuntSample(3),
            };
            foreach (var s in Samples)
                s.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SeedHuntSample.IsSelected))
                    {
                        OnPropertyChanged(nameof(SelectedCount));
                        OnPropertyChanged(nameof(HasSelection));
                    }
                    else if (e.PropertyName == nameof(SeedHuntSample.VideoPath))
                    {
                        OnPropertyChanged(nameof(SelectedCount));
                        OnPropertyChanged(nameof(HasSelection));
                        OnPropertyChanged(nameof(IsReady));
                    }
                };
        }

        /// <summary>1-based position of this pair in the batch. Mutable so the pair can be
        /// reordered (moved up/down) and renumbered to drive the final video sequence.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private int _index;

        public string FirstImagePath { get; }
        public string LastImagePath { get; }

        /// <summary>The three seed previews for this pair (Slot 1-3 → workflow sampler outputs).</summary>
        public ObservableCollection<SeedHuntSample> Samples { get; }

        [ObservableProperty]
        private BitmapImage? _firstThumb;

        [ObservableProperty]
        private BitmapImage? _lastThumb;

        /// <summary>The transition prompt written by the LLM for this pair.</summary>
        [ObservableProperty]
        private string _prompt = string.Empty;

        /// <summary>The base seed that produced this pair's three previews (needed at Finish).</summary>
        [ObservableProperty]
        private long _batchSeed = -1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private string _status = "pending";

        public string Label => $"{Index}: {Path.GetFileName(FirstImagePath)} → {Path.GetFileName(LastImagePath)}";

        public string StatusText => Status;

        public bool IsReady => Samples.Any(s => s.HasVideo);

        /// <summary>How many of this pair's previews are queued for Finish.</summary>
        public int SelectedCount => Samples.Count(s => s.IsSelected && s.HasVideo);

        public bool HasSelection => SelectedCount > 0;
    }
}
