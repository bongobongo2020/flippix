using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FlipPix.UI.Linux.Models
{
    /// <summary>
    /// One image in the 🔍 Image Upscaler's folder batch: the source file plus the row's live
    /// state as the batch drains. Deliberately not persisted — a batch is re-scanned from the
    /// folder each run, so there is nothing here worth surviving a restart.
    /// </summary>
    public class ImageUpscaleBatchItem : INotifyPropertyChanged
    {
        private ImageUpscaleBatchStatus _status = ImageUpscaleBatchStatus.Pending;
        private string _detail = string.Empty;
        private string _outputPath = string.Empty;

        public ImageUpscaleBatchItem(string sourcePath)
        {
            SourcePath = sourcePath;
        }

        public string SourcePath { get; }

        public string FileName => Path.GetFileName(SourcePath);

        public ImageUpscaleBatchStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusGlyph));
            }
        }

        /// <summary>Short per-row note: the output size, or why the row failed.</summary>
        public string Detail
        {
            get => _detail;
            set { _detail = value; OnPropertyChanged(); }
        }

        public string OutputPath
        {
            get => _outputPath;
            set { _outputPath = value; OnPropertyChanged(); }
        }

        public string StatusGlyph => _status switch
        {
            ImageUpscaleBatchStatus.Pending => "⏳",
            ImageUpscaleBatchStatus.Running => "▶️",
            ImageUpscaleBatchStatus.Done => "✅",
            ImageUpscaleBatchStatus.Failed => "❌",
            ImageUpscaleBatchStatus.Skipped => "⏭️",
            _ => "•"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum ImageUpscaleBatchStatus
    {
        Pending,
        Running,
        Done,
        Failed,
        Skipped
    }
}
