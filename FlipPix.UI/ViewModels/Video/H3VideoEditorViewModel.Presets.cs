using System;
using System.Collections.ObjectModel;
using System.Linq;
using FlipPix.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    public partial class H3VideoEditorViewModel
    {
        // ── Presets ──────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<H3EditorPreset> Presets { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SelectedPreset))]
        [NotifyPropertyChangedFor(nameof(IsUltra))]
        [NotifyPropertyChangedFor(nameof(IsTwoPass))]
        [NotifyPropertyChangedFor(nameof(IsClassicEngine))]
        [NotifyPropertyChangedFor(nameof(GenerateLabel))]
        private string _selectedPresetName = "vanilla";

        public H3EditorPreset? SelectedPreset => Presets.FirstOrDefault(p => p.Name == SelectedPresetName);

        partial void OnSelectedPresetNameChanged(string value)
        {
            if (!_suppressPresetApply) ApplyPreset(value);
            if (SelectedPreset?.IsCustom == true) NewPresetName = SelectedPreset.Name;
            DeletePresetCommand?.NotifyCanExecuteChanged();
            SaveSettings();
        }

        /// <summary>The name typed beside 💾 — the preset Save writes (a saved one of the same name is overwritten).
        /// Picking one of your presets fills it in, so re-saving updates that preset.</summary>
        [ObservableProperty]
        private string _newPresetName = string.Empty;

        /// <summary>Set while settings are restored at start-up: the saved cards are already the live state, so a
        /// preset's snapshot is not copied over edits made after it was picked.</summary>
        private bool _restoringPreset;

        /// <summary>Set to change the dropdown without applying the preset's values.</summary>
        private bool _suppressPresetApply;

        public RelayCommand<string> SelectPresetCommand { get; private set; } = null!;
        public RelayCommand SavePresetCommand { get; private set; } = null!;
        public RelayCommand DeletePresetCommand { get; private set; } = null!;
        public RelayCommand ResetToDefaultCommand { get; private set; } = null!;

        private void InitializePresets()
        {
            // Load built-in presets
            foreach (var preset in H3EditorPreset.GetBuiltInPresets())
                Presets.Add(preset);

            // Then the user's own, kept in settings so they survive restarts.
            var saved = _settingsService.Settings?.H3VideoEditor?.CustomPresets;
            if (saved != null)
            {
                foreach (var custom in saved.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList())
                {
                    if (Presets.Any(p => string.Equals(p.Name, custom.Name, StringComparison.OrdinalIgnoreCase)))
                        continue; // never shadow a built-in or duplicate
                    var entry = new H3EditorPreset();
                    FillFromCustom(entry, custom);
                    Presets.Add(entry);
                }
            }

            SelectPresetCommand = new RelayCommand<string>(name => { if (name != null) SelectedPresetName = name; });
            SavePresetCommand = new RelayCommand(SaveCurrentAsPreset);
            DeletePresetCommand = new RelayCommand(DeleteSelectedPreset, () => SelectedPreset?.IsCustom == true);
            ResetToDefaultCommand = new RelayCommand(() => ApplyPreset("vanilla"));

            CutClipCommand = new RelayCommand<H3TimelineClip>(CutClip);
            ExtendClipCommand = new RelayCommand<H3TimelineClip>(c => ExtendClip(c));
            PrependClipCommand = new RelayCommand(PrependClip);
            AppendClipCommand = new RelayCommand(AppendClip);
            RefreshBrowserCommand = new RelayCommand(RefreshBrowser);
            AddFromBrowserCommand = new RelayCommand<H3TimelineClip>(clip => { if (clip != null) AddClipFromBrowser(clip); });
        }

        private void ApplyPreset(string presetName)
        {
            // "vanilla 8 steps" sampled the 20-step model at 8 steps with no turbo: replaced by the fused
            // turbo model, which is what 8 steps actually needs.
            if (presetName == "vanilla 8 steps") presetName = "fused turbo8";
            var preset = Presets.FirstOrDefault(p => p.Name == presetName);
            if (preset == null) return;

            var custom = preset.Saved;
            var copySnapshot = custom != null && !_restoringPreset;

            if (copySnapshot)
            {
                UpscaleFactor = custom!.UpscaleFactor;
                RefineAmount = custom.RefineAmount;
                Crf = custom.CRF;
                Crossfade = custom.Crossfade;
                LevelLock = custom.LevelLock;
            }

            if (preset.IsUltra)
            {
                if (copySnapshot && custom!.Ultra != null) Ultra.CopyFrom(custom.Ultra);
                // The Ultra card carries its own dials; the classic ones are left as they were, so picking a
                // classic preset again later finds them untouched.
                OfferUltraModel();
                AddLog($"Applied preset: {presetName} — clips render through h3-minimax-ultra.json. " +
                       UltraSamplingSummary);
                return;
            }

            if (preset.IsTwoPass)
            {
                if (copySnapshot && custom!.TwoPass != null) LoadTwoPassSnapshot(custom.TwoPass);
                // Its own card too. Generate makes first-pass previews; nothing is refined until approved.
                OnTwoPassSelected();
                AddLog($"Applied preset: {presetName} — Generate previews every beat's first pass; " +
                       "\"Happy — proceed\" runs the full two-pass refinement.");
                return;
            }

            SelectedModel = preset.Model;
            SelectedAttention = preset.Attention;
            SelectedSparseAttention = preset.SparseAttention;
            Spectrum = preset.Spectrum;
            SelectedTurboLoader = preset.TurboLoader;
            TurboLora = preset.TurboLora;
            TurboStrength = preset.TurboStrength;
            Steps = preset.Steps;
            SelectedSampler = preset.Sampler;
            SelectedScheduler = preset.Scheduler;
            ShiftVideo = preset.ShiftVideo;
            ShiftAudio = preset.ShiftAudio;

            AddLog($"Applied preset: {presetName}");
        }

        private void SaveCurrentAsPreset()
        {
            var name = (NewPresetName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                AddLog("Type a name for the preset beside 💾 first.");
                return;
            }
            if (Presets.Any(p => !p.IsCustom && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                AddLog($"\"{name}\" is a built-in preset — pick another name.");
                return;
            }

            var custom = new H3VideoEditorCustomPreset
            {
                Name = name,
                Engine = IsUltra ? H3VideoEditorCustomPreset.UltraEngine
                       : IsTwoPass ? H3VideoEditorCustomPreset.TwoPassEngine
                       : H3VideoEditorCustomPreset.ClassicEngine,
                Ultra = IsUltra ? Ultra.Clone() : null,
                TwoPass = IsTwoPass ? TwoPass.Clone() : null,
                Model = SelectedModel,
                Attention = SelectedAttention,
                SparseAttention = SelectedSparseAttention,
                Spectrum = Spectrum,
                TurboLoader = SelectedTurboLoader,
                TurboLora = TurboLora,
                TurboStrength = TurboStrength,
                Steps = Steps,
                Sampler = SelectedSampler,
                Scheduler = SelectedScheduler,
                ShiftVideo = ShiftVideo,
                ShiftAudio = ShiftAudio,
                UpscaleFactor = UpscaleFactor,
                RefineAmount = RefineAmount,
                CRF = Crf,
                Crossfade = Crossfade,
                LevelLock = LevelLock
            };

            // Persist: replace a saved preset of the same name, else add.
            var store = _settingsService.Settings?.H3VideoEditor?.CustomPresets;
            if (store != null)
            {
                store.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                store.Add(custom);
            }

            // Update the dropdown entry in place (keeps the ComboBox selection steady), or add a new one.
            var entry = Presets.FirstOrDefault(p => p.IsCustom && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var overwritten = entry != null;
            if (entry == null)
            {
                entry = new H3EditorPreset();
                Presets.Add(entry);
            }
            FillFromCustom(entry, custom);

            // Select it without re-applying: its values are what is on screen already.
            _suppressPresetApply = true;
            try
            {
                if (SelectedPresetName == entry.Name) OnSelectedPresetNameChanged(entry.Name);
                else SelectedPresetName = entry.Name;
            }
            finally { _suppressPresetApply = false; }
            OnPropertyChanged(nameof(SelectedPreset));

            SaveSettings();
            AddLog($"{(overwritten ? "Updated" : "Saved")} preset: ★ {entry.Name} ({custom.Engine})");
        }

        private void DeleteSelectedPreset()
        {
            var preset = SelectedPreset;
            if (preset?.IsCustom != true) return;

            _settingsService.Settings?.H3VideoEditor?.CustomPresets
                .RemoveAll(c => string.Equals(c.Name, preset.Name, StringComparison.OrdinalIgnoreCase));

            // Fall back to the built-in of the same engine, leaving the dials as they are.
            var fallback = preset.IsUltra ? H3EditorPreset.UltraName
                         : preset.IsTwoPass ? H3EditorPreset.TwoPassName
                         : "vanilla";
            _suppressPresetApply = true;
            try { SelectedPresetName = fallback; }
            finally { _suppressPresetApply = false; }
            Presets.Remove(preset);
            NewPresetName = string.Empty;

            SaveSettings();
            AddLog($"Deleted preset: ★ {preset.Name}");
        }

        /// <summary>Copies a saved preset onto a dropdown entry.</summary>
        private static void FillFromCustom(H3EditorPreset entry, H3VideoEditorCustomPreset custom)
        {
            entry.Saved = custom;
            entry.Name = custom.Name;
            entry.IsUltra = custom.Engine == H3VideoEditorCustomPreset.UltraEngine;
            entry.IsTwoPass = custom.Engine == H3VideoEditorCustomPreset.TwoPassEngine;
            entry.Model = string.IsNullOrWhiteSpace(custom.Model) ? H3EditorPreset.VanillaModel : custom.Model;
            entry.Attention = custom.Attention;
            entry.SparseAttention = custom.SparseAttention;
            entry.Spectrum = custom.Spectrum;
            entry.TurboLoader = custom.TurboLoader;
            entry.TurboLora = custom.TurboLora;
            entry.TurboStrength = custom.TurboStrength;
            entry.Steps = custom.Steps;
            entry.Sampler = custom.Sampler;
            entry.Scheduler = custom.Scheduler;
            entry.ShiftVideo = custom.ShiftVideo;
            entry.ShiftAudio = custom.ShiftAudio;
        }

        /// <summary>Puts a saved 🌹 card on screen, LoRA rows and RefMods included.</summary>
        private void LoadTwoPassSnapshot(MiniMaxI2VTwoPassOptions snapshot)
        {
            var copy = snapshot.Clone();

            _rebuildingTwoPassLoras = true;
            try
            {
                TwoPassLoras.Clear();
                foreach (var mod in TwoPassRefMods) mod.PropertyChanged -= OnRefModSlotChanged;
                TwoPassRefMods.Clear();
            }
            finally { _rebuildingTwoPassLoras = false; }

            // Continue mode belongs to the editor's per-clip chain, not to the preset.
            var keepContinue = TwoPass.ContinueMode;
            TwoPass.CopyFrom(copy);
            TwoPass.ContinueMode = keepContinue;

            _rebuildingTwoPassLoras = true;
            try
            {
                foreach (var lora in copy.Loras.Take(MiniMaxI2VTwoPassOptions.MaxLoras))
                {
                    OfferTwoPassLora(lora.Name);
                    AttachTwoPassLora(new MiniMaxI2VLoraSlot(TwoPassLoras.Count + 1, lora.Name, lora.Strength));
                }
            }
            finally { _rebuildingTwoPassLoras = false; }
            SyncTwoPassLoras();

            foreach (var mod in copy.RefMods.Take(MiniMaxI2VTwoPassOptions.MaxRefMods))
            {
                if (!RefModOptions.Contains(mod.Mod)) RefModOptions.Add(mod.Mod);
                AttachRefMod(mod);
            }
            AddRefModCommand?.NotifyCanExecuteChanged();
        }

        // ── Engine Settings ──────────────────────────────────────────────────────────────────────

        /// <summary>The H3 diffusion model clips are sampled with — set by the preset, never on its own, so the
        /// step count and LoRA always match the model they were tuned for.</summary>
        [ObservableProperty]
        private string _selectedModel = H3EditorPreset.VanillaModel;

        [ObservableProperty]
        private string _selectedAttention = "comfy_kitchen";

        public ObservableCollection<string> AttentionOptions { get; } = new()
        {
            "pytorch",
            "sage",
            "comfy_kitchen"
        };

        [ObservableProperty]
        private string _selectedSparseAttention = "none";

        public ObservableCollection<string> SparseAttentionOptions { get; } = new()
        {
            "none",
            "vsa"
        };

        [ObservableProperty]
        private bool _spectrum;

        [ObservableProperty]
        private string _selectedTurboLoader = "off";

        public ObservableCollection<string> TurboLoaderOptions { get; } = new()
        {
            "off",
            "normal",
            "larryvrh"
        };

        [ObservableProperty]
        private string _turboLora = string.Empty;

        [ObservableProperty]
        private double _turboStrength = 1.0;

        [ObservableProperty]
        private int _steps = 20;

        [ObservableProperty]
        private string _selectedSampler = "res_multistep";

        public ObservableCollection<string> SamplerOptions { get; } = new()
        {
            "euler",
            "res_multistep",
            "dpmpp_2m",
            "dpmpp_sde"
        };

        [ObservableProperty]
        private string _selectedScheduler = "simple";

        public ObservableCollection<string> SchedulerOptions { get; } = new()
        {
            "simple",
            "beta",
            "karras"
        };

        [ObservableProperty]
        private int _shiftVideo = 12;

        [ObservableProperty]
        private int _shiftAudio = 3;

        // ── Upscale Settings ─────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        private double _upscaleFactor = 2.0;

        [ObservableProperty]
        private double _refineAmount = 0.2;

        [ObservableProperty]
        private int _crf = 19;

        [ObservableProperty]
        private bool _crossfade = true;

        [ObservableProperty]
        private int _crossfadeFrames;

        [ObservableProperty]
        private bool _levelLock = true;

        [ObservableProperty]
        private int _levelLockFrames = 12;

        [ObservableProperty]
        private bool _levelLockFlicker = true;

        [ObservableProperty]
        private bool _audioDeclick;

        // Property change handlers for auto-save
        partial void OnSelectedAttentionChanged(string value) => SaveSettings();
        partial void OnSelectedSparseAttentionChanged(string value) => SaveSettings();
        partial void OnSpectrumChanged(bool value) => SaveSettings();
        partial void OnSelectedTurboLoaderChanged(string value) => SaveSettings();
        partial void OnTurboLoraChanged(string value) => SaveSettings();
        partial void OnTurboStrengthChanged(double value) => SaveSettings();
        partial void OnStepsChanged(int value) => SaveSettings();
        partial void OnSelectedSamplerChanged(string value) => SaveSettings();
        partial void OnSelectedSchedulerChanged(string value) => SaveSettings();
        partial void OnShiftVideoChanged(int value) => SaveSettings();
        partial void OnShiftAudioChanged(int value) => SaveSettings();
        partial void OnUpscaleFactorChanged(double value) => SaveSettings();
        partial void OnRefineAmountChanged(double value) => SaveSettings();
        partial void OnCrfChanged(int value) => SaveSettings();
        partial void OnCrossfadeChanged(bool value) => SaveSettings();
        partial void OnLevelLockChanged(bool value) => SaveSettings();
        partial void OnDefaultClipDurationChanged(double value) => SaveSettings();
        partial void OnChainClipsChanged(bool value)
        {
            SaveSettings();
            OnPropertyChanged(nameof(TwoPassChainSummary));
            RefreshTwoPassGuides();
        }
        partial void OnMegapixelsChanged(double value) => SaveSettings();
        partial void OnProjectFolderChanged(string value) => SaveSettings();
    }
}
