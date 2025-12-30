using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlipPix.UI.Models
{
    public class CameraQueueItem : INotifyPropertyChanged
    {
        private string _status = "Queued";
        private double _progress = 0;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ImageFilePath { get; set; } = string.Empty;
        public string CameraControl { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string NegativePrompt { get; set; } = string.Empty;
        public int Steps { get; set; } = 8;
        public double Cfg { get; set; } = 1.5;
        public double Denoise { get; set; } = 1.0;
        public string SamplerName { get; set; } = "euler";
        public string Scheduler { get; set; } = "beta57";

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ResultImagePath { get; set; }
        public string? ErrorMessage { get; set; }

        public double Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}