using System.Collections.ObjectModel;
using System.Linq;
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
        [NotifyPropertyChangedFor(nameof(IsClassicEngine))]
        private string _selectedPresetName = "vanilla";

        public H3EditorPreset? SelectedPreset => Presets.FirstOrDefault(p => p.Name == SelectedPresetName);

        partial void OnSelectedPresetNameChanged(string value)
        {
            ApplyPreset(value);
            SaveSettings();
        }

        public RelayCommand<string> SelectPresetCommand { get; private set; } = null!;
        public RelayCommand SavePresetCommand { get; private set; } = null!;
        public RelayCommand ResetToDefaultCommand { get; private set; } = null!;

        private void InitializePresets()
        {
            // Load built-in presets
            foreach (var preset in H3EditorPreset.GetBuiltInPresets())
                Presets.Add(preset);

            SelectPresetCommand = new RelayCommand<string>(name => { if (name != null) SelectedPresetName = name; });
            SavePresetCommand = new RelayCommand(SaveCurrentAsPreset);
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

            if (preset.IsUltra)
            {
                // The Ultra card carries its own dials; the classic ones are left as they were, so picking a
                // classic preset again later finds them untouched.
                OfferUltraModel();
                AddLog($"Applied preset: {presetName} — clips render through h3-minimax-ultra.json. " +
                       UltraSamplingSummary);
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
            if (IsUltra)
            {
                AddLog("The Ultra card is saved as you edit it — ↺ puts it back on the workflow's tested values.");
                return;
            }

            // TODO: Show dialog to name the preset
            var name = $"Custom {Presets.Count(p => p.Name.StartsWith("Custom")) + 1}";
            var preset = new H3EditorPreset
            {
                Name = name,
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
                ShiftAudio = ShiftAudio
            };
            Presets.Add(preset);
            SelectedPresetName = name;
            AddLog($"Saved preset: {name}");
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
        partial void OnChainClipsChanged(bool value) => SaveSettings();
        partial void OnMegapixelsChanged(double value) => SaveSettings();
        partial void OnProjectFolderChanged(string value) => SaveSettings();
    }
}
