using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.UI.Services;

namespace FlipPix.UI.Models
{
    /// <summary>One row of the 🥷 Martial tab's LoRA stack.</summary>
    public partial class MartialLoraRow : ObservableObject
    {
        [ObservableProperty] private bool _on = true;
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private double _strength = 1.0;
        [ObservableProperty] private bool _onRefine = true;

        /// <summary>"⚠ not on the server — …" once the server's list is read; empty when it is there.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMissing))]
        private string _missingNote = string.Empty;

        public bool IsMissing => MissingNote.Length > 0;

        /// <summary>What the LoRA is for, when the catalog knows it.</summary>
        public string Purpose => MartialCatalog.Find(Name)?.Purpose ?? string.Empty;

        partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Purpose));
    }

    /// <summary>A picture sent with every beat, and what it shows.</summary>
    public partial class MartialReference : ObservableObject
    {
        public MartialReference(string path, string label)
        {
            _path = path;
            _label = label;
            LoadThumbnail();
        }

        [ObservableProperty] private string _path;
        [ObservableProperty] private string _label;

        public string FileName => System.IO.Path.GetFileName(Path);
        public bool Exists => File.Exists(Path);

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnPathChanged(string value) { OnPropertyChanged(nameof(FileName)); LoadThumbnail(); }

        private void LoadThumbnail()
        {
            var path = Path;
            AsyncThumbnail.Load(path, 160, 0, bitmap =>
            {
                if (Path != path) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }
    }

    /// <summary>A keyframe pinned inside one beat.</summary>
    public partial class MartialGuide : ObservableObject
    {
        public MartialGuide(string path, int beat, double seconds)
        {
            _path = path;
            _beat = beat;
            _seconds = seconds;
            LoadThumbnail();
        }

        [ObservableProperty] private string _path;

        /// <summary>1-based.</summary>
        [ObservableProperty] private int _beat;

        [ObservableProperty] private double _seconds;

        /// <summary>Set by the tab: "frame 60 of 243".</summary>
        [ObservableProperty] private string _frameHint = string.Empty;

        public string FileName => System.IO.Path.GetFileName(Path);
        public bool Exists => File.Exists(Path);

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnPathChanged(string value) { OnPropertyChanged(nameof(FileName)); LoadThumbnail(); }

        private void LoadThumbnail()
        {
            var path = Path;
            AsyncThumbnail.Load(path, 160, 0, bitmap =>
            {
                if (Path != path) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }
    }
}
