using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🎬 H3 Video Editor — A timeline-based video editor for H3 MiniMax video generation.
    /// Features drag-and-drop clip arrangement, reference image slots, presets, and join/upscale.
    /// Based on the h3_obvpm_timeline workflow.
    /// </summary>
    public partial class H3VideoEditorViewModel : ObservableObject
    {
        protected readonly ComfyUIService _comfyUIService;
        protected readonly LMStudioService _lmStudioService;
        protected readonly IAppLogger _logger;
        protected readonly FlipPix.Core.Services.SettingsService _settingsService;
        protected readonly IServiceProvider? _serviceProvider;
        protected readonly WorkflowQueueCoordinator _workflowCoordinator;
        protected readonly IFileDialogService _fileDialogService;

        public H3VideoEditorViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
        {
            _comfyUIService = comfyUIService;
            _lmStudioService = lmStudioService;
            _logger = logger;
            _settingsService = settingsService;
            _serviceProvider = serviceProvider;
            _workflowCoordinator = workflowCoordinator;
            _fileDialogService = fileDialogService;

            // Initialize reference slots
            for (int i = 1; i <= 4; i++)
                ReferenceSlots.Add(new H3ReferenceSlot(i));

            // Initialize commands
            InitializeCommands();
            InitializePresets();
            InitializeTimeline();
            InitializeStoryCommands();

            // Load saved settings
            LoadSettings();

            AddLog("🎬 H3 Video Editor initialized — drag clips to the timeline, set prompts, and render.");
        }

        // ── Logging ────────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<string> LogMessages { get; } = new();
        public RelayCommand CopyLogCommand { get; private set; } = null!;
        public RelayCommand ClearLogCommand { get; private set; } = null!;

        protected void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LogMessages.Insert(0, $"[{timestamp}] {message}");
                while (LogMessages.Count > 100) LogMessages.RemoveAt(LogMessages.Count - 1);
            });
        }

        private void CopyLog()
        {
            if (LogMessages.Count == 0) return;
            var logText = string.Join(Environment.NewLine, LogMessages.Reverse());
            System.Windows.Clipboard.SetText(logText);
            AddLog("Log copied to clipboard");
        }

        private void ClearLog()
        {
            LogMessages.Clear();
        }

        // ── Reference Image Slots (Picture 1-4) ──────────────────────────────────────────────────

        public ObservableCollection<H3ReferenceSlot> ReferenceSlots { get; } = new();

        public H3ReferenceSlot Picture1 => ReferenceSlots.Count > 0 ? ReferenceSlots[0] : new H3ReferenceSlot(1);
        public H3ReferenceSlot Picture2 => ReferenceSlots.Count > 1 ? ReferenceSlots[1] : new H3ReferenceSlot(2);
        public H3ReferenceSlot Picture3 => ReferenceSlots.Count > 2 ? ReferenceSlots[2] : new H3ReferenceSlot(3);
        public H3ReferenceSlot Picture4 => ReferenceSlots.Count > 3 ? ReferenceSlots[3] : new H3ReferenceSlot(4);

        public RelayCommand<int> SelectPictureCommand { get; private set; } = null!;
        public RelayCommand<int> ClearPictureCommand { get; private set; } = null!;

        private async Task SelectPictureAsync(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= ReferenceSlots.Count) return;

            var slot = ReferenceSlots[slotIndex];
            var filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*";
            var paths = await _fileDialogService.OpenFilesDialogAsync(
                $"Select image for {slot.Label}",
                filter);

            if (paths == null || paths.Length == 0) return;

            slot.Clear();
            foreach (var path in paths)
            {
                slot.AddImage(path);
            }

            SaveSettings();
            AddLog($"Loaded {paths.Length} image(s) to {slot.Label}");
        }

        private void ClearPicture(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= ReferenceSlots.Count) return;
            var slot = ReferenceSlots[slotIndex];
            slot.Clear();
            SaveSettings();
            AddLog($"Cleared {slot.Label}");
        }

        // ── Prompt Input ─────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPrompt))]
        private string _promptText = string.Empty;

        public bool HasPrompt => !string.IsNullOrWhiteSpace(PromptText);

        /// <summary>Template for the spec prompt format from the workflow.</summary>
        public const string SpecPromptTemplate = @"subject_definitions:
<Subject 1> is the person depicted in <Picture 1>

summary:
[reference generation]

retention_analysis:
<Subject 1> (appears in [Shot 1]): fully_preserved

detailed_description:
Shot 1 is a continuous shot with no cuts.
[Describe the action here]

overall_soundscape:
[Describe ambient sounds]

non_diegetic_music:
N/A
";

        public RelayCommand InsertTemplateCommand { get; private set; } = null!;
        public RelayCommand GeneratePromptCommand { get; private set; } = null!;
        public RelayCommand ClearPromptCommand { get; private set; } = null!;
        public RelayCommand ApplyPromptToSelectedCommand { get; private set; } = null!;

        private void InsertTemplate()
        {
            PromptText = SpecPromptTemplate;
            AddLog("Inserted spec prompt template");
        }

        private void ApplyPromptToSelected()
        {
            if (SelectedClip == null || string.IsNullOrWhiteSpace(PromptText)) return;

            SelectedClip.Prompt = PromptText;
            // Reset the clip state so it will be regenerated
            if (SelectedClip.State == H3ClipState.Rendered || SelectedClip.State == H3ClipState.Failed)
            {
                SelectedClip.State = H3ClipState.Pending;
                SelectedClip.OutputPath = string.Empty;
                SelectedClip.ThumbnailPath = string.Empty;
            }
            AddLog($"Applied prompt to clip {SelectedClip.DisplayIndex}");
        }

        // ── Project Settings ─────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProjectFolder))]
        private string _projectFolder = "my_project";

        public bool HasProjectFolder => !string.IsNullOrWhiteSpace(ProjectFolder);

        [ObservableProperty]
        private string _selectedAspectRatio = "16:9 (Widescreen)";

        public ObservableCollection<string> AspectRatioOptions { get; } = new()
        {
            "16:9 (Widescreen)",
            "9:16 (Portrait)",
            "4:3 (Standard)",
            "3:4 (Portrait Standard)",
            "1:1 (Square)",
            "21:9 (Ultrawide)"
        };

        [ObservableProperty]
        private double _megapixels = 0.5;

        // ── State ────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGenerate))]
        [NotifyPropertyChangedFor(nameof(CanJoin))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private bool _isGenerating;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanJoin))]
        private bool _isJoining;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUpscale))]
        private bool _isUpscaling;

        public bool CanGenerate => !IsGenerating && TimelineClips.Count > 0;
        public bool CanJoin => !IsJoining && !IsGenerating && TimelineClips.Any(c => c.IsRendered);
        public bool CanUpscale => !IsUpscaling && !string.IsNullOrEmpty(JoinedVideoPath);

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _joinedVideoPath = string.Empty;

        // ── Video Preview ────────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        private string _previewVideoPath = string.Empty;

        [ObservableProperty]
        private H3TimelineClip? _selectedClip;

        partial void OnSelectedClipChanged(H3TimelineClip? value)
        {
            foreach (var clip in TimelineClips)
                clip.IsSelected = clip == value;

            if (value?.OutputPath != null && File.Exists(value.OutputPath))
                PreviewVideoPath = value.OutputPath;

            ApplyPromptToSelectedCommand?.NotifyCanExecuteChanged();
        }

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        public RelayCommand GenerateClipsCommand { get; private set; } = null!;
        public RelayCommand JoinClipsCommand { get; private set; } = null!;
        public RelayCommand UpscaleCommand { get; private set; } = null!;
        public RelayCommand CancelCommand { get; private set; } = null!;
        public RelayCommand BrowseProjectFolderCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> PlayPreviewCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> SelectClipCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> EditClipPromptCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> RegenerateClipCommand { get; private set; } = null!;

        private void InitializeCommands()
        {
            SelectPictureCommand = new RelayCommand<int>(i => _ = SelectPictureAsync(i));
            ClearPictureCommand = new RelayCommand<int>(ClearPicture);
            InsertTemplateCommand = new RelayCommand(InsertTemplate);
            GeneratePromptCommand = new RelayCommand(() => _ = GeneratePromptAsync());
            ClearPromptCommand = new RelayCommand(() => PromptText = string.Empty);
            ApplyPromptToSelectedCommand = new RelayCommand(ApplyPromptToSelected, () => SelectedClip != null && HasPrompt);
            GenerateClipsCommand = new RelayCommand(() => _ = GenerateClipsAsync(), () => CanGenerate);
            JoinClipsCommand = new RelayCommand(() => _ = JoinClipsAsync(), () => CanJoin);
            UpscaleCommand = new RelayCommand(() => _ = UpscaleAsync(), () => CanUpscale);
            CancelCommand = new RelayCommand(Cancel, () => IsGenerating || IsJoining || IsUpscaling);
            BrowseProjectFolderCommand = new RelayCommand(() => _ = BrowseProjectFolderAsync());
            PlayPreviewCommand = new RelayCommand<H3TimelineClip>(PlayClip);
            SelectClipCommand = new RelayCommand<H3TimelineClip>(SelectClip);
            EditClipPromptCommand = new RelayCommand<H3TimelineClip>(EditClipPrompt);
            RegenerateClipCommand = new RelayCommand<H3TimelineClip>(c => _ = RegenerateClipAsync(c), _ => !IsGenerating);
            CopyLogCommand = new RelayCommand(CopyLog);
            ClearLogCommand = new RelayCommand(ClearLog);
        }

        private async Task BrowseProjectFolderAsync()
        {
            var folder = await _fileDialogService.OpenFolderDialogAsync("Select project folder");
            if (!string.IsNullOrEmpty(folder))
            {
                ProjectFolder = Path.GetFileName(folder);
                SaveSettings();
            }
        }

        private void PlayClip(H3TimelineClip? clip)
        {
            if (clip?.OutputPath != null && File.Exists(clip.OutputPath))
            {
                PreviewVideoPath = clip.OutputPath;
                AddLog($"Playing clip {clip.DisplayIndex}");
            }
        }

        private void SelectClip(H3TimelineClip? clip)
        {
            if (clip == null) return;
            SelectedClip = clip;
            // Also load the clip's prompt into the editor for easy viewing/editing
            PromptText = clip.Prompt;
            AddLog($"Selected clip {clip.DisplayIndex}");
        }

        private void EditClipPrompt(H3TimelineClip? clip)
        {
            if (clip == null) return;
            // Select the clip and load its prompt into the main prompt editor
            SelectedClip = clip;
            PromptText = clip.Prompt;
            AddLog($"Editing prompt for clip {clip.DisplayIndex} - modify in the prompt editor above, then use 'Apply to Selected'");
        }

        /// <summary>
        /// Regenerates a specific clip, resetting it to pending and generating it again.
        /// </summary>
        private async Task RegenerateClipAsync(H3TimelineClip? clip)
        {
            if (clip == null || IsGenerating) return;

            // Reset the clip state
            clip.State = H3ClipState.Pending;
            clip.OutputPath = string.Empty;
            clip.ThumbnailPath = string.Empty;

            AddLog($"Regenerating clip {clip.DisplayIndex}...");

            IsGenerating = true;
            StatusText = $"Regenerating clip {clip.DisplayIndex}...";

            try
            {
                using var cts = new System.Threading.CancellationTokenSource();
                using var lease = await _workflowCoordinator.AcquireAsync("h3_video_editor", cts.Token);

                clip.State = H3ClipState.Rendering;
                await GenerateSingleClipAsync(clip, cts.Token);
                clip.State = H3ClipState.Rendered;

                AddLog($"Clip {clip.DisplayIndex} regenerated successfully");
                StatusText = "Ready";
            }
            catch (Exception ex)
            {
                clip.State = H3ClipState.Failed;
                AddLog($"Regeneration failed: {ex.Message}");
                StatusText = "Regeneration failed";
            }
            finally
            {
                IsGenerating = false;
                GenerateClipsCommand.NotifyCanExecuteChanged();
                JoinClipsCommand.NotifyCanExecuteChanged();
            }
        }

        private void Cancel()
        {
            // TODO: Implement cancellation
            AddLog("Cancellation requested");
        }

        // ── Settings Persistence ─────────────────────────────────────────────────────────────────

        private void LoadSettings()
        {
            var settings = _settingsService.Settings?.H3VideoEditor;
            if (settings == null) return;

            SelectedPresetName = settings.Preset;
            Steps = settings.Steps;
            SelectedSampler = settings.Sampler;
            SelectedScheduler = settings.Scheduler;
            ShiftVideo = settings.ShiftVideo;
            ShiftAudio = settings.ShiftAudio;
            UpscaleFactor = settings.UpscaleFactor;
            RefineAmount = settings.RefineAmount;
            Crf = settings.CRF;
            Crossfade = settings.Crossfade;
            LevelLock = settings.LevelLock;
            DefaultClipDuration = settings.DefaultClipDuration;
            ChainClips = settings.ChainClips;
            SelectedAspectRatio = settings.AspectRatio;
            Megapixels = settings.Megapixels;
            ProjectFolder = settings.ProjectFolder;

            // Load reference image paths
            if (!string.IsNullOrEmpty(settings.Picture1Path) && File.Exists(settings.Picture1Path))
                Picture1.AddImage(settings.Picture1Path);
            if (!string.IsNullOrEmpty(settings.Picture2Path) && File.Exists(settings.Picture2Path))
                Picture2.AddImage(settings.Picture2Path);
            if (!string.IsNullOrEmpty(settings.Picture3Path) && File.Exists(settings.Picture3Path))
                Picture3.AddImage(settings.Picture3Path);
            if (!string.IsNullOrEmpty(settings.Picture4Path) && File.Exists(settings.Picture4Path))
                Picture4.AddImage(settings.Picture4Path);

            // Load story mode settings
            StoryModeEnabled = settings.StoryModeEnabled;
            TargetDurationSeconds = settings.TargetDurationSeconds;
            CastPhotoEngine = settings.CastPhotoEngine;
            AutoGenerateCast = settings.AutoGenerateCast;
        }

        protected void SaveSettings()
        {
            var settings = _settingsService.Settings;
            if (settings == null) return;

            settings.H3VideoEditor.Preset = SelectedPresetName;
            settings.H3VideoEditor.Steps = Steps;
            settings.H3VideoEditor.Sampler = SelectedSampler;
            settings.H3VideoEditor.Scheduler = SelectedScheduler;
            settings.H3VideoEditor.ShiftVideo = ShiftVideo;
            settings.H3VideoEditor.ShiftAudio = ShiftAudio;
            settings.H3VideoEditor.UpscaleFactor = UpscaleFactor;
            settings.H3VideoEditor.RefineAmount = RefineAmount;
            settings.H3VideoEditor.CRF = Crf;
            settings.H3VideoEditor.Crossfade = Crossfade;
            settings.H3VideoEditor.LevelLock = LevelLock;
            settings.H3VideoEditor.DefaultClipDuration = DefaultClipDuration;
            settings.H3VideoEditor.ChainClips = ChainClips;
            settings.H3VideoEditor.AspectRatio = SelectedAspectRatio;
            settings.H3VideoEditor.Megapixels = Megapixels;
            settings.H3VideoEditor.ProjectFolder = ProjectFolder;

            // Save reference image paths
            settings.H3VideoEditor.Picture1Path = Picture1.Images.FirstOrDefault()?.Path ?? string.Empty;
            settings.H3VideoEditor.Picture2Path = Picture2.Images.FirstOrDefault()?.Path ?? string.Empty;
            settings.H3VideoEditor.Picture3Path = Picture3.Images.FirstOrDefault()?.Path ?? string.Empty;
            settings.H3VideoEditor.Picture4Path = Picture4.Images.FirstOrDefault()?.Path ?? string.Empty;

            // Save story mode settings
            settings.H3VideoEditor.StoryModeEnabled = StoryModeEnabled;
            settings.H3VideoEditor.TargetDurationSeconds = TargetDurationSeconds;
            settings.H3VideoEditor.CastPhotoEngine = CastPhotoEngine;
            settings.H3VideoEditor.AutoGenerateCast = AutoGenerateCast;

            _settingsService.SaveSettings(settings);
        }
    }
}
