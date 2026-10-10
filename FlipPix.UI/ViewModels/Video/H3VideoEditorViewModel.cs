using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        public RelayCommand<object?> SelectPictureCommand { get; private set; } = null!;
        public RelayCommand<object?> ClearPictureCommand { get; private set; } = null!;

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
        public RelayCommand SaveProjectCommand { get; private set; } = null!;
        public RelayCommand LoadProjectCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> PlayPreviewCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> SelectClipCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> EditClipPromptCommand { get; private set; } = null!;
        public RelayCommand<H3TimelineClip> RegenerateClipCommand { get; private set; } = null!;

        private void InitializeCommands()
        {
            SelectPictureCommand = new RelayCommand<object?>(o => { if (int.TryParse(o?.ToString(), out var i)) _ = SelectPictureAsync(i); });
            ClearPictureCommand = new RelayCommand<object?>(o => { if (int.TryParse(o?.ToString(), out var i)) ClearPicture(i); });
            InsertTemplateCommand = new RelayCommand(InsertTemplate);
            GeneratePromptCommand = new RelayCommand(() => _ = GeneratePromptAsync());
            ClearPromptCommand = new RelayCommand(() => PromptText = string.Empty);
            ApplyPromptToSelectedCommand = new RelayCommand(ApplyPromptToSelected, () => SelectedClip != null && HasPrompt);
            GenerateClipsCommand = new RelayCommand(() => _ = GenerateClipsAsync(), () => CanGenerate);
            JoinClipsCommand = new RelayCommand(() => _ = JoinClipsAsync(), () => CanJoin);
            UpscaleCommand = new RelayCommand(() => _ = UpscaleAsync(), () => CanUpscale);
            CancelCommand = new RelayCommand(Cancel, () => IsGenerating || IsJoining || IsUpscaling);
            BrowseProjectFolderCommand = new RelayCommand(() => _ = BrowseProjectFolderAsync());
            SaveProjectCommand = new RelayCommand(() => _ = SaveProjectAsync());
            LoadProjectCommand = new RelayCommand(() => _ = LoadProjectAsync());
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

            // Restore timeline from crash recovery
            RestoreTimeline(settings);
        }

        /// <summary>Restores timeline clips from saved state (crash recovery).</summary>
        private void RestoreTimeline(FlipPix.Core.Models.H3VideoEditorSettings settings)
        {
            if (settings.TimelineClips == null || settings.TimelineClips.Count == 0)
                return;

            try
            {
                TimelineClips.Clear();
                int restoredCount = 0;

                foreach (var savedClip in settings.TimelineClips.OrderBy(c => c.Index))
                {
                    var clip = new H3TimelineClip
                    {
                        Index = savedClip.Index,
                        Prompt = savedClip.Prompt,
                        DurationSeconds = savedClip.DurationSeconds,
                        OutputPath = savedClip.OutputPath,
                        ThumbnailPath = savedClip.ThumbnailPath,
                        UseMotionContext = savedClip.UseMotionContext,
                        State = ParseClipState(savedClip.State)
                    };

                    // Verify output file still exists
                    if (!string.IsNullOrEmpty(clip.OutputPath) && !File.Exists(clip.OutputPath))
                    {
                        clip.OutputPath = string.Empty;
                        clip.State = H3ClipState.Pending;
                    }

                    TimelineClips.Add(clip);
                    restoredCount++;
                }

                // Restore prompt text
                if (!string.IsNullOrEmpty(settings.CurrentPromptText))
                    PromptText = settings.CurrentPromptText;

                RecalculateTimeline();

                if (restoredCount > 0)
                {
                    AddLog($"Restored {restoredCount} clip(s) from previous session");
                    var renderedCount = TimelineClips.Count(c => c.IsRendered);
                    if (renderedCount > 0)
                        AddLog($"  {renderedCount} clip(s) already rendered, {TimelineClips.Count - renderedCount} pending");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not restore timeline: {ex.Message}");
            }
        }

        private static H3ClipState ParseClipState(string state) => state switch
        {
            "Queued" => H3ClipState.Queued,
            "Rendering" => H3ClipState.Pending, // Treat interrupted rendering as pending
            "Rendered" => H3ClipState.Rendered,
            "Failed" => H3ClipState.Pending, // Allow retry of failed clips
            _ => H3ClipState.Pending
        };

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

            // Save timeline state for crash recovery
            SaveTimelineState(settings.H3VideoEditor);

            _settingsService.SaveSettings(settings);
        }

        /// <summary>Saves the current timeline state for crash recovery.</summary>
        private void SaveTimelineState(FlipPix.Core.Models.H3VideoEditorSettings settings)
        {
            settings.TimelineClips.Clear();
            settings.CurrentPromptText = PromptText;

            foreach (var clip in TimelineClips)
            {
                settings.TimelineClips.Add(new FlipPix.Core.Models.H3TimelineClipState
                {
                    Index = clip.Index,
                    Prompt = clip.Prompt,
                    DurationSeconds = clip.DurationSeconds,
                    OutputPath = clip.OutputPath,
                    ThumbnailPath = clip.ThumbnailPath,
                    State = clip.State.ToString(),
                    UseMotionContext = clip.UseMotionContext
                });
            }
        }

        /// <summary>Auto-saves the timeline after any change (for crash recovery).</summary>
        protected void AutoSaveTimeline()
        {
            try
            {
                SaveSettings();
            }
            catch
            {
                // Silently ignore auto-save failures to not disrupt workflow
            }
        }

        // ── Project Save/Load ─────────────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions ProjectJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Saves the current project to a .h3proj file.</summary>
        private async Task SaveProjectAsync()
        {
            try
            {
                var filter = "H3 Project Files|*.h3proj|All Files|*.*";
                var defaultName = !string.IsNullOrEmpty(ProjectFolder) ? $"{ProjectFolder}.h3proj" : "project.h3proj";

                var path = await _fileDialogService.SaveFileDialogAsync("Save H3 Project", filter, defaultName);
                if (string.IsNullOrEmpty(path)) return;

                var project = BuildProjectFromCurrentState();
                project.Metadata.ProjectName = Path.GetFileNameWithoutExtension(path);
                project.Metadata.LastModified = DateTime.Now;

                var json = JsonSerializer.Serialize(project, ProjectJsonOptions);
                await File.WriteAllTextAsync(path, json);

                AddLog($"✓ Project saved: {Path.GetFileName(path)}");
                AddLog($"  {TimelineClips.Count} clips, {TimelineClips.Count(c => c.IsRendered)} rendered");
            }
            catch (Exception ex)
            {
                AddLog($"Error saving project: {ex.Message}");
            }
        }

        /// <summary>Loads a project from a .h3proj file.</summary>
        private async Task LoadProjectAsync()
        {
            try
            {
                var filter = "H3 Project Files|*.h3proj|All Files|*.*";
                var paths = await _fileDialogService.OpenFilesDialogAsync("Load H3 Project", filter);
                if (paths == null || paths.Length == 0) return;

                var path = paths[0];
                if (!File.Exists(path))
                {
                    AddLog($"Project file not found: {path}");
                    return;
                }

                var json = await File.ReadAllTextAsync(path);
                var project = JsonSerializer.Deserialize<H3Project>(json, ProjectJsonOptions);
                if (project == null)
                {
                    AddLog("Failed to parse project file");
                    return;
                }

                RestoreProjectState(project);

                AddLog($"✓ Project loaded: {Path.GetFileName(path)}");
                AddLog($"  {TimelineClips.Count} clips, {TimelineClips.Count(c => c.IsRendered)} rendered");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading project: {ex.Message}");
            }
        }

        /// <summary>Builds an H3Project object from the current ViewModel state.</summary>
        private H3Project BuildProjectFromCurrentState()
        {
            var project = new H3Project
            {
                Metadata = new H3ProjectMetadata
                {
                    ProjectName = ProjectFolder,
                    ProjectFolder = ProjectFolder,
                    CreatedDate = DateTime.Now,
                    LastModified = DateTime.Now
                },
                ReferenceImages = new H3ProjectReferenceImages
                {
                    Picture1Path = Picture1.Images.FirstOrDefault()?.Path,
                    Picture2Path = Picture2.Images.FirstOrDefault()?.Path,
                    Picture3Path = Picture3.Images.FirstOrDefault()?.Path,
                    Picture4Path = Picture4.Images.FirstOrDefault()?.Path
                },
                Settings = new H3ProjectSettings
                {
                    PresetName = SelectedPresetName,
                    Steps = Steps,
                    Sampler = SelectedSampler,
                    Scheduler = SelectedScheduler,
                    ShiftVideo = ShiftVideo,
                    ShiftAudio = ShiftAudio,
                    Attention = SelectedAttention,
                    SparseAttention = SelectedSparseAttention,
                    Spectrum = Spectrum,
                    TurboLoader = SelectedTurboLoader,
                    TurboLora = TurboLora,
                    TurboStrength = TurboStrength,
                    AspectRatio = SelectedAspectRatio,
                    Megapixels = Megapixels,
                    DefaultClipDuration = DefaultClipDuration,
                    ChainClips = ChainClips,
                    UpscaleFactor = UpscaleFactor,
                    RefineAmount = RefineAmount,
                    Crf = Crf,
                    Crossfade = Crossfade,
                    LevelLock = LevelLock
                },
                JoinedVideoPath = JoinedVideoPath,
                CurrentPrompt = PromptText
            };

            // Save timeline clips
            foreach (var clip in TimelineClips)
            {
                project.Clips.Add(new H3ProjectClip
                {
                    Id = clip.Id,
                    Index = clip.Index,
                    Prompt = clip.Prompt,
                    DurationSeconds = clip.DurationSeconds,
                    OutputPath = clip.OutputPath,
                    ThumbnailPath = clip.ThumbnailPath,
                    State = clip.State.ToString(),
                    UseMotionContext = clip.UseMotionContext
                });
            }

            // Save story mode state if enabled
            if (StoryModeEnabled)
            {
                project.StoryMode = new H3ProjectStoryMode
                {
                    Enabled = StoryModeEnabled,
                    StoryText = StoryText,
                    StoryFilePath = StoryFilePath,
                    StoryFileName = StoryFileName,
                    StorySetting = StorySetting,
                    TargetDurationSeconds = TargetDurationSeconds,
                    IsStoryAnalyzed = IsStoryAnalyzed,
                    CastPhotoEngine = CastPhotoEngine,
                    AutoGenerateCast = AutoGenerateCast
                };
            }

            return project;
        }

        /// <summary>Restores ViewModel state from an H3Project object.</summary>
        private void RestoreProjectState(H3Project project)
        {
            // Clear current timeline
            TimelineClips.Clear();

            // Restore project folder
            if (!string.IsNullOrEmpty(project.Metadata?.ProjectFolder))
                ProjectFolder = project.Metadata.ProjectFolder;

            // Restore settings
            if (project.Settings != null)
            {
                SelectedPresetName = project.Settings.PresetName;
                Steps = project.Settings.Steps;
                SelectedSampler = project.Settings.Sampler;
                SelectedScheduler = project.Settings.Scheduler;
                ShiftVideo = project.Settings.ShiftVideo;
                ShiftAudio = project.Settings.ShiftAudio;
                SelectedAttention = project.Settings.Attention;
                SelectedSparseAttention = project.Settings.SparseAttention;
                Spectrum = project.Settings.Spectrum;
                SelectedTurboLoader = project.Settings.TurboLoader;
                TurboLora = project.Settings.TurboLora;
                TurboStrength = project.Settings.TurboStrength;
                SelectedAspectRatio = project.Settings.AspectRatio;
                Megapixels = project.Settings.Megapixels;
                DefaultClipDuration = project.Settings.DefaultClipDuration;
                ChainClips = project.Settings.ChainClips;
                UpscaleFactor = project.Settings.UpscaleFactor;
                RefineAmount = project.Settings.RefineAmount;
                Crf = project.Settings.Crf;
                Crossfade = project.Settings.Crossfade;
                LevelLock = project.Settings.LevelLock;
            }

            // Restore reference images
            if (project.ReferenceImages != null)
            {
                LoadReferenceImageIfExists(0, project.ReferenceImages.Picture1Path);
                LoadReferenceImageIfExists(1, project.ReferenceImages.Picture2Path);
                LoadReferenceImageIfExists(2, project.ReferenceImages.Picture3Path);
                LoadReferenceImageIfExists(3, project.ReferenceImages.Picture4Path);
            }

            // Restore timeline clips
            foreach (var savedClip in project.Clips.OrderBy(c => c.Index))
            {
                var clip = new H3TimelineClip
                {
                    Index = savedClip.Index,
                    Prompt = savedClip.Prompt,
                    DurationSeconds = savedClip.DurationSeconds,
                    OutputPath = savedClip.OutputPath,
                    ThumbnailPath = savedClip.ThumbnailPath,
                    UseMotionContext = savedClip.UseMotionContext
                };

                // Restore state and verify files exist
                if (Enum.TryParse<H3ClipState>(savedClip.State, out var state))
                {
                    if (state == H3ClipState.Rendered)
                    {
                        // Verify output file still exists
                        if (!string.IsNullOrEmpty(savedClip.OutputPath) && File.Exists(savedClip.OutputPath))
                        {
                            clip.State = H3ClipState.Rendered;
                        }
                        else
                        {
                            clip.State = H3ClipState.Pending;
                            clip.OutputPath = null;
                            clip.ThumbnailPath = null;
                            AddLog($"Clip {savedClip.Index + 1}: Output file missing, marked as pending");
                        }
                    }
                    else
                    {
                        clip.State = H3ClipState.Pending; // Reset any in-progress states
                    }
                }

                TimelineClips.Add(clip);
            }

            RecalculateTimeline();

            // Restore joined video path if it exists
            if (!string.IsNullOrEmpty(project.JoinedVideoPath) && File.Exists(project.JoinedVideoPath))
            {
                JoinedVideoPath = project.JoinedVideoPath;
                PreviewVideoPath = project.JoinedVideoPath;
            }

            // Restore current prompt
            if (!string.IsNullOrEmpty(project.CurrentPrompt))
                PromptText = project.CurrentPrompt;

            // Restore story mode state
            if (project.StoryMode != null)
            {
                StoryModeEnabled = project.StoryMode.Enabled;
                StoryText = project.StoryMode.StoryText ?? string.Empty;
                StoryFilePath = project.StoryMode.StoryFilePath ?? string.Empty;
                StoryFileName = project.StoryMode.StoryFileName ?? string.Empty;
                StorySetting = project.StoryMode.StorySetting ?? string.Empty;
                TargetDurationSeconds = project.StoryMode.TargetDurationSeconds;
                IsStoryAnalyzed = project.StoryMode.IsStoryAnalyzed;
                CastPhotoEngine = project.StoryMode.CastPhotoEngine;
                AutoGenerateCast = project.StoryMode.AutoGenerateCast;
            }

            // Select first clip if available
            if (TimelineClips.Count > 0)
            {
                SelectedClip = TimelineClips[0];
                if (!string.IsNullOrEmpty(TimelineClips[0].OutputPath) && File.Exists(TimelineClips[0].OutputPath))
                    PreviewVideoPath = TimelineClips[0].OutputPath;
            }

            // Update UI commands
            GenerateClipsCommand.NotifyCanExecuteChanged();
            JoinClipsCommand.NotifyCanExecuteChanged();
            UpscaleCommand.NotifyCanExecuteChanged();
        }

        private void LoadReferenceImageIfExists(int slotIndex, string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (slotIndex < 0 || slotIndex >= ReferenceSlots.Count) return;

            try
            {
                ReferenceSlots[slotIndex].Images.Clear();
                ReferenceSlots[slotIndex].Images.Add(new H3ReferenceImage
                {
                    Path = path,
                    FileName = Path.GetFileName(path)
                });
            }
            catch
            {
                // Ignore errors loading reference images
            }
        }
    }
}
