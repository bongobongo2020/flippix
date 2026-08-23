using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Models;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Keeps the trigger words of a Krea2 LoRA picker remembered between sessions.
    ///
    /// <para>A Krea2 LoRA only fires when its trigger word is in the prompt, so every row in a picker
    /// carries one. The word is guessed from the file name (<see cref="KreaLoraSelection.DeriveTriggerWord"/>),
    /// which is right often enough to be worth doing and wrong often enough to need correcting — this
    /// tracker is what makes a correction stick. It watches a row collection and, whenever the user types
    /// a word, writes it into <c>ComfyUISettings.KreaLoraTriggerWords</c> keyed by the LoRA file name;
    /// whenever a LoRA is picked, it puts the saved word back. Every tab that shows the picker shares that
    /// one map, so "Famegrid" typed in the Image Generator is what the Analyzer and Story Q use too.</para>
    ///
    /// <para>Attach one per picker with <see cref="Track"/>, re-calling it whenever the tab swaps the
    /// collection (the queue paths do, per item).</para>
    /// </summary>
    public sealed class KreaLoraTriggerTracker
    {
        private readonly SettingsService _settingsService;
        private readonly Action<string>? _log;

        private ObservableCollection<KreaLoraSelection>? _rows;
        private bool _suppressSave;

        public KreaLoraTriggerTracker(SettingsService settingsService, Action<string>? log = null)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _log = log;
        }

        /// <summary>Starts watching <paramref name="rows"/>, releasing whatever was watched before.</summary>
        public void Track(ObservableCollection<KreaLoraSelection>? rows)
        {
            if (ReferenceEquals(_rows, rows)) return;

            if (_rows != null)
            {
                _rows.CollectionChanged -= OnRowsChanged;
                foreach (var row in _rows)
                    row.PropertyChanged -= OnRowPropertyChanged;
            }

            _rows = rows;
            if (_rows == null) return;

            _rows.CollectionChanged += OnRowsChanged;
            foreach (var row in _rows)
                TrackRow(row);
        }

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (var row in e.OldItems?.OfType<KreaLoraSelection>() ?? Enumerable.Empty<KreaLoraSelection>())
                row.PropertyChanged -= OnRowPropertyChanged;

            foreach (var row in e.NewItems?.OfType<KreaLoraSelection>() ?? Enumerable.Empty<KreaLoraSelection>())
                TrackRow(row);
        }

        private void TrackRow(KreaLoraSelection row)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            row.PropertyChanged += OnRowPropertyChanged;

            // Filling in on attach must not overwrite a word a saved queue item carries of its own.
            Resolve(row, force: false);
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not KreaLoraSelection row) return;

            if (e.PropertyName == nameof(KreaLoraSelection.LoraName))
                Resolve(row, force: true);
            else if (e.PropertyName == nameof(KreaLoraSelection.TriggerWord) && !_suppressSave)
                Save(row);
        }

        /// <summary>
        /// Settles what a row's trigger word should be: the word saved for its LoRA, else the guess
        /// derived from the file name.
        ///
        /// <para>On a fresh pick (<paramref name="force"/>) that decision is taken outright — the word
        /// describes the LoRA, not the row, so carrying the previous LoRA's word across would be wrong
        /// (the user's own word for it is already saved and comes back when they pick it again). On
        /// attach it only fills in over an empty or still-derived field, so a word a saved queue item
        /// carries of its own survives being loaded.</para>
        ///
        /// <para>The write is suppressed from <see cref="Save"/>: this is the tracker deciding, not the
        /// user typing, and letting it save would overwrite the very word it just read.</para>
        /// </summary>
        private void Resolve(KreaLoraSelection row, bool force)
        {
            var word = Lookup(row.LoraName) ?? KreaLoraSelection.DeriveTriggerWord(row.LoraName);
            if (word == row.TriggerWord) return;

            if (!force
                && !string.IsNullOrWhiteSpace(row.TriggerWord)
                && !string.Equals(row.TriggerWord, KreaLoraSelection.DeriveTriggerWord(row.LoraName), StringComparison.OrdinalIgnoreCase))
                return;

            _suppressSave = true;
            try { row.TriggerWord = word; }
            finally { _suppressSave = false; }
        }

        private string? Lookup(string loraName)
        {
            var map = _settingsService.Settings?.KreaLoraTriggerWords;
            if (map == null || string.IsNullOrEmpty(loraName)) return null;

            return map.TryGetValue(loraName.ToLowerInvariant(), out var word) ? word : null;
        }

        /// <summary>Remembers this row's trigger word for its LoRA (an empty one means "prepend nothing").</summary>
        private void Save(KreaLoraSelection row)
        {
            var settings = _settingsService.Settings;
            if (settings == null || string.IsNullOrEmpty(row.LoraName)) return;
            if (row.LoraName == "No LoRAs available" || row.LoraName == "Error loading LoRAs") return;

            settings.KreaLoraTriggerWords ??= new Dictionary<string, string>();

            var key = row.LoraName.ToLowerInvariant();
            var word = (row.TriggerWord ?? string.Empty).Trim();
            if (settings.KreaLoraTriggerWords.TryGetValue(key, out var existing) && existing == word) return;

            settings.KreaLoraTriggerWords[key] = word;

            try
            {
                _settingsService.SaveSettings(settings);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not remember the trigger word for {row.LoraName}: {ex.Message}");
            }
        }
    }
}
