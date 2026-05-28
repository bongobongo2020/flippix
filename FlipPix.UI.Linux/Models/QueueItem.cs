using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FlipPix.UI.Linux.Models
{
    public class QueueItem : BaseQueueItem
    {
        private string _prompt = string.Empty;
        private string _videoPath = string.Empty;
        private string _imagePath = string.Empty;
        private string _firstFrameImagePath = string.Empty;
        private string _lastFrameImagePath = string.Empty;
        private long _seed = 0;
        private int _frameCount = 240;

        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt != value)
                {
                    _prompt = value;
                    OnPropertyChanged(nameof(Prompt));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (_imagePath != value)
                {
                    _imagePath = value;
                    OnPropertyChanged(nameof(ImagePath));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string FirstFrameImagePath
        {
            get => _firstFrameImagePath;
            set
            {
                if (_firstFrameImagePath != value)
                {
                    _firstFrameImagePath = value;
                    OnPropertyChanged(nameof(FirstFrameImagePath));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string LastFrameImagePath
        {
            get => _lastFrameImagePath;
            set
            {
                if (_lastFrameImagePath != value)
                {
                    _lastFrameImagePath = value;
                    OnPropertyChanged(nameof(LastFrameImagePath));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public long Seed
        {
            get => _seed;
            set
            {
                if (_seed != value)
                {
                    _seed = value;
                    OnPropertyChanged(nameof(Seed));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public int FrameCount
        {
            get => _frameCount;
            set
            {
                if (_frameCount != value)
                {
                    _frameCount = value;
                    OnPropertyChanged(nameof(FrameCount));
                }
            }
        }

        public string VideoPath
        {
            get => _videoPath;
            set
            {
                if (_videoPath != value)
                {
                    _videoPath = value;
                    OnPropertyChanged(nameof(VideoPath));
                }
            }
        }

        public string DisplayText
        {
            get
            {
                var imageInfo = !string.IsNullOrEmpty(FirstFrameImagePath)
                    ? $"🖼️ {Path.GetFileNameWithoutExtension(FirstFrameImagePath)}→{Path.GetFileNameWithoutExtension(LastFrameImagePath)}"
                    : $"📎 {Path.GetFileName(ImagePath)}";

                var seedInfo = Seed > 0 ? $" [Seed: {Seed}]" : "";
                return $"{imageInfo}{seedInfo}: {Prompt}";
            }
        }
    }
}
