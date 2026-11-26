using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ➕ Extra LoRAs: any number of LoRAs stacked after the tab's main LoRA, each with its own strength and an
    /// on/off tick. They go into the same H3 Power LoRA Stack as the main one (<see cref="FflfBeatRender.ExtraLoras"/>),
    /// so both passes and the face refine run on them. Saved with the settings and with each story.
    /// </summary>
    public partial class H3FflfViewModel
    {
        public ObservableCollection<FflfLoraRow> ExtraLoras { get; } = new();

        public RelayCommand AddExtraLoraCommand { get; private set; } = null!;
        public RelayCommand<FflfLoraRow> RemoveExtraLoraCommand { get; private set; } = null!;

        /// <summary>"2 extra LoRA(s) on · ⚠ 1 not on the server", or empty with none.</summary>
        public string ExtraLoraSummary
        {
            get
            {
                if (ExtraLoras.Count == 0) return string.Empty;
                var on = ActiveExtraLoras().Count;
                var missing = ExtraLoras.Count(r => r.On && IsMissingLora(r.Name));
                return $"{on} of {ExtraLoras.Count} extra LoRA(s) on" + (missing > 0 ? $" · ⚠ {missing} not on the server" : string.Empty);
            }
        }

        private void InitExtraLoras(H3FflfSettings s)
        {
            AddExtraLoraCommand = new RelayCommand(() => ExtraLoras.Add(new FflfLoraRow()), () => !IsProcessing);
            RemoveExtraLoraCommand = new RelayCommand<FflfLoraRow>(r => { if (r != null) ExtraLoras.Remove(r); }, _ => !IsProcessing);
            ExtraLoras.CollectionChanged += ExtraLoras_CollectionChanged;
            ReadExtraLoras(s.ExtraLoras);
        }

        /// <summary>Puts saved rows on the page (settings or a story).</summary>
        private void ReadExtraLoras(IEnumerable<H3FflfLoraRow>? rows)
        {
            ExtraLoras.Clear();
            foreach (var r in rows ?? Enumerable.Empty<H3FflfLoraRow>())
            {
                if (r == null) continue;
                var name = (r.Name ?? string.Empty).Trim().Replace('\\', '/');
                OfferLora(name);
                ExtraLoras.Add(new FflfLoraRow { On = r.On, Name = name, Strength = Math.Clamp(r.Strength, 0, 2) });
            }
        }

        private List<H3FflfLoraRow> ExtraLoraRows() =>
            ExtraLoras.Select(r => new H3FflfLoraRow { On = r.On, Name = r.Name, Strength = r.Strength }).ToList();

        /// <summary>The rows that render: ticked, named, above zero.</summary>
        protected IReadOnlyList<MiniMaxI2VLora> ActiveExtraLoras() =>
            ExtraLoras.Where(r => r.On && !string.IsNullOrWhiteSpace(r.Name) && r.Strength > 0)
                      .Select(r => new MiniMaxI2VLora { Name = r.Name.Trim().Replace('\\', '/'), Strength = r.Strength })
                      .ToList();

        private bool IsMissingLora(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || ServerLoras.Count == 0) return false;
            var norm = name.Trim().Replace('\\', '/');
            return !ServerLoras.Contains(norm, StringComparer.OrdinalIgnoreCase);
        }

        private void WarnMissingExtraLoras()
        {
            foreach (var r in ExtraLoras.Where(r => r.On && IsMissingLora(r.Name)))
                AddLog($"⚠ Extra LoRA {Path.GetFileName(r.Name)} is not on the server.");
            OnPropertyChanged(nameof(ExtraLoraSummary));
        }

        private void ExtraLoras_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (FflfLoraRow r in e.OldItems) r.PropertyChanged -= ExtraLora_PropertyChanged;
            if (e.NewItems != null) foreach (FflfLoraRow r in e.NewItems) r.PropertyChanged += ExtraLora_PropertyChanged;
            ExtraLorasChanged();
        }

        private void ExtraLora_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ExtraLorasChanged();

        private void ExtraLorasChanged()
        {
            OnPropertyChanged(nameof(ExtraLoraSummary));
            if (_loading) return;
            Save();
            ScheduleStorySave();
        }

        private void RefreshExtraLoraCommands()
        {
            AddExtraLoraCommand?.NotifyCanExecuteChanged();
            RemoveExtraLoraCommand?.NotifyCanExecuteChanged();
        }
    }
}
