using System;
using System.Collections.Generic;

namespace FlipPix.UI.Models
{
    public class SavedPrompt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public int AspectRatioIndex { get; set; } = 0;
        public int Steps { get; set; } = 9;
        public double Cfg { get; set; } = 1.0;
        public long Seed { get; set; } = 0;
        public double Denoise { get; set; } = 1.0;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; } = DateTime.Now;
        public int UseCount { get; set; } = 0;

        // Additional data for different prompt types
        public Dictionary<string, object>? AdditionalData { get; set; }
    }
}