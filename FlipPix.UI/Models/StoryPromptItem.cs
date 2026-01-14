using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlipPix.UI.Models
{
    public class StoryPromptItem : INotifyPropertyChanged
    {
        private string _status = "Queued";
        private double _progress = 0;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int Index { get; set; }  // The prompt index (1-10)
        public string Prompt { get; set; } = string.Empty;
        public string InputImagePath { get; set; } = string.Empty;
        public string? OutputImagePath { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }

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
