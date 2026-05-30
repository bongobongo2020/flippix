using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Linux.Models
{
    public class VideoBatch : ObservableObject
    {
        public string BatchId { get; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        public string BatchName { get; set; } = string.Empty;
        public string WorkflowFile { get; set; } = string.Empty;
        public string WorkflowName { get; set; } = string.Empty;
        public List<string> Prompts { get; set; } = new();

        private string _status = "Queued";
        public string Status
        {
            get => _status;
            set
            {
                SetProperty(ref _status, value);
                OnPropertyChanged(nameof(IsQueued));
                OnPropertyChanged(nameof(StatusIcon));
            }
        }

        private int _processedCount;
        public int ProcessedCount
        {
            get => _processedCount;
            set
            {
                SetProperty(ref _processedCount, value);
                OnPropertyChanged(nameof(ProgressText));
            }
        }

        public int TotalCount => Prompts.Count;
        public string ProgressText => $"{ProcessedCount}/{TotalCount}";
        public bool IsQueued => Status == "Queued";

        public string StatusIcon => Status switch
        {
            "Done" => "✓",
            "Processing" => "▶",
            "Failed" => "✗",
            _ => "◯"
        };
    }
}
