using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "Seed Director" — a fusion of LTX Director and SeedHunt. Drop images onto a horizontal
    /// timeline (each shot has its own prompt + duration like LTX Director), then for EVERY shot
    /// run a SeedHunt batch of 4 fast low-res seed previews. The user picks one (or more) seeds per
    /// shot; the final button upscales each chosen seed to a high-res clip (SeedHunt Stage 2/3) and
    /// FFmpeg-concatenates the shots in timeline order into one continuous video. When a shot has
    /// multiple seeds selected, one joined video is produced per combination (cartesian product).
    /// Drives <c>seed-hunter-api.json</c> per shot, exactly as <see cref="SeedHuntViewModel"/> does.
    /// </summary>
    public partial class SeedDirectorViewModel : VideoProcessingBaseViewModel
    {
        private const string WorkflowFileName = "workflow/video/ltx/seed-hunter-api.json";
        private const string OutputSubfolder = "seeddir";

        // ── Workflow node ids (shared with SeedHuntViewModel / seed-hunter-api.json) ──────────
        private const string NodeImage = "5052";
        private const string NodeStage1Loras = "5078";
        private const string CharacterLoraSubfolder = "LTX-23/characters";
        private const string NodePrompt = "5026:5018";
        private const string NodeTargetWidth = "5013:5215";
        private const string NodeTargetHeight = "5013:5216";
        private const string NodeLength = "5074";
        private const string NodeBatchSeed = "5038";
        private const string NodeStage2Seed = "5039";
        private const string NodeStage3Seed = "5040";
        private const string NodeSelect = "5144";
        private const string NodeSelectSwitch = "5152";
        private const string NodeSepAfterSwitch = "5140";
        private const string NodeFinalOutput = "5033";
        private const string NodeStage2Preview = "5034";

        private static readonly Dictionary<int, string> SamplerOutputBySlot = new()
        {
            { 1, "5087:4829" }, { 2, "5134:5172" }, { 3, "5108:5167" }, { 4, "5100:5162" },
        };
        private static readonly Dictionary<int, string> PreviewNodeBySlot = new()
        {
            { 1, "5086" }, { 2, "5062" }, { 3, "5109" }, { 4, "5101" },
        };
        private static readonly Dictionary<string, int> SlotByPreviewNode =
            PreviewNodeBySlot.ToDictionary(kv => kv.Value, kv => kv.Key);

        public static readonly string[] ResolutionOptions = { "360p", "480p", "720p", "1080p", "1440p", "4k" };
        public static readonly string[] OrientationOptions = { "Landscape", "Portrait", "Square" };

        private readonly IFileDialogService _fileDialogService;
        private readonly LMStudioService _lmStudioService;
        private readonly ObservableCollection<SeedDirectorShot> _timeline = new();
        private readonly ObservableCollection<SeedDirectorResult> _results = new();
        private CancellationTokenSource? _runCts;
        private CancellationTokenSource? _analyzeCts;

        // ── Prompt template selection (drives the analysis system prompt) ──
        private readonly ObservableCollection<SeedHuntPromptTemplate> _promptTemplates = new()
        {
            new SeedHuntPromptTemplate("🎬 Director (motion + audio per shot)", "ltx-director.md",
                "Analyze this keyframe and write the motion + audio prompt for this shot."),
            new SeedHuntPromptTemplate("⚔️ Fight (combat duel)", "ltx-seedhunt-fight.md",
                "Analyze this image and generate an LTX combat action video prompt."),
            new SeedHuntPromptTemplate("🎬 Cinematic (LTXV2)", "ltxv2_system_prompt_addition.md",
                "Analyze this image and generate a detailed cinematic LTXV2 video prompt guided by the elements in the image."),
        };
        private SeedHuntPromptTemplate _selectedPromptTemplate;

        // ── Optional character LoRA (scanned from loras/LTX-23/characters) ──
        private readonly ObservableCollection<SeedHuntCharacterLora> _characterLoras = new() { SeedHuntCharacterLora.None };
        private SeedHuntCharacterLora _selectedCharacterLora = SeedHuntCharacterLora.None;
        private double _characterLoraStrength = 1.0;

        public SeedDirectorViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _selectedPromptTemplate = _promptTemplates[0];

            AddImagesCommand = new RelayCommand(async () => await AddImagesAsync());
            RemoveShotCommand = new RelayCommand<SeedDirectorShot>(RemoveShot);
            MoveLeftCommand = new RelayCommand(MoveSelectedLeft, () => CanMoveLeft);
            MoveRightCommand = new RelayCommand(MoveSelectedRight, () => CanMoveRight);
            ClearTimelineCommand = new RelayCommand(ClearTimeline, () => HasTimeline);
            AnalyzeSelectedCommand = new RelayCommand(async () => await AnalyzeSelectedAsync(), () => CanAnalyze);
            AnalyzeAllCommand = new RelayCommand(async () => await AnalyzeAllAsync(), () => CanAnalyzeAll);
            GenerateAllSeedsCommand = new RelayCommand(async () => await GenerateAllSeedsAsync(), () => CanGenerateSeeds);
            RerollShotCommand = new RelayCommand(async () => await RerollSelectedShotAsync(), () => CanRerollShot);
            PreviewSampleCommand = new RelayCommand<SeedHuntSample>(PreviewSample);
            CreateJoinedVideoCommand = new RelayCommand(async () => await CreateJoinedVideoAsync(), () => CanCreateJoined);
            PlayResultCommand = new RelayCommand<SeedDirectorResult>(PlayResult);
            StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsProcessing);
            ToggleMuteCommand = new RelayCommand(() => IsPreviewMuted = !IsPreviewMuted);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RefreshLorasCommand = new RelayCommand(async () => await RefreshCharacterLorasAsync());

            _ = RefreshCharacterLorasAsync();

            _timeline.CollectionChanged += (s, e) =>
            {
                Reindex();
                OnPropertyChanged(nameof(HasTimeline));
                OnPropertyChanged(nameof(TimelineSummary));
                OnCanExecuteChanged();
            };

            AddLog("Seed Director initialized");
        }

        #region Commands
        public ICommand AddImagesCommand { get; }
        public RelayCommand<SeedDirectorShot> RemoveShotCommand { get; }
        public RelayCommand MoveLeftCommand { get; }
        public RelayCommand MoveRightCommand { get; }
        public RelayCommand ClearTimelineCommand { get; }
        public RelayCommand AnalyzeSelectedCommand { get; }
        public RelayCommand AnalyzeAllCommand { get; }
        public RelayCommand GenerateAllSeedsCommand { get; }
        public RelayCommand RerollShotCommand { get; }
        public RelayCommand<SeedHuntSample> PreviewSampleCommand { get; }
        public RelayCommand CreateJoinedVideoCommand { get; }
        public RelayCommand<SeedDirectorResult> PlayResultCommand { get; }
        public RelayCommand StopCommand { get; }
        public RelayCommand ToggleMuteCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RefreshLorasCommand { get; }
        #endregion

        #region Timeline
        public ObservableCollection<SeedDirectorShot> Timeline => _timeline;
        public ObservableCollection<SeedDirectorResult> Results => _results;
        public IReadOnlyList<string> ResolutionChoices => ResolutionOptions;
        public IReadOnlyList<string> OrientationChoices => OrientationOptions;

        private SeedDirectorShot? _selectedShot;
        public SeedDirectorShot? SelectedShot
        {
            get => _selectedShot;
            set
            {
                if (_selectedShot != value)
                {
                    if (_selectedShot != null) _selectedShot.IsSelected = false;
                    _selectedShot = value;
                    if (_selectedShot != null) _selectedShot.IsSelected = true;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(CanAnalyze));
                    OnPropertyChanged(nameof(CanRerollShot));
                    OnCanExecuteChanged();
                }
            }
        }

        public bool HasTimeline => _timeline.Any();
        public bool HasSelection => _selectedShot != null;
        public double TotalSeconds => _timeline.Sum(s => s.DurationSeconds);

        public string TimelineSummary => _timeline.Count == 0
            ? "Drop images here to start your timeline"
            : $"{_timeline.Count} shot{(_timeline.Count == 1 ? "" : "s")} · {TotalSeconds:0.0}s · select seeds per shot, then join";

        private async Task AddImagesAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var paths = await _fileDialogService.OpenFilesDialogAsync(
                "Add Timeline Images",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: "seeddirector.images");

            if (paths != null && paths.Length > 0)
                AddImagesFromPaths(paths);
        }

        /// <summary>Adds image files to the end of the timeline (used by Browse and drag-drop).</summary>
        public void AddImagesFromPaths(IEnumerable<string> paths)
        {
            var imageExts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            int added = 0;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                if (!imageExts.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;

                var shot = new SeedDirectorShot(path);
                WireShot(shot);
                _timeline.Add(shot);
                added++;
            }
            if (added > 0)
            {
                AddLog($"Added {added} image{(added == 1 ? "" : "s")} to timeline");
                SelectedShot ??= _timeline.LastOrDefault();
            }
        }

        /// <summary>Subscribes to each of a shot's sample checkboxes so selection drives Can-flags + status.</summary>
        private void WireShot(SeedDirectorShot shot)
        {
            foreach (var sample in shot.Samples)
                sample.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SeedHuntSample.IsSelected) ||
                        e.PropertyName == nameof(SeedHuntSample.VideoPath))
                    {
                        UpdateShotStatus(shot);
                        OnPropertyChanged(nameof(CanCreateJoined));
                        OnPropertyChanged(nameof(AllShotsHaveSeed));
                        OnPropertyChanged(nameof(JoinHint));
                        OnCanExecuteChanged();
                    }
                };
        }

        private static void UpdateShotStatus(SeedDirectorShot shot)
        {
            if (shot.HasSelectedSeed)
                shot.Status = $"✓ {shot.SelectedSlots.Count} seed{(shot.SelectedSlots.Count == 1 ? "" : "s")}";
            else if (shot.HasSamples)
                shot.Status = "pick a seed";
            else
                shot.Status = string.Empty;
        }

        private void RemoveShot(SeedDirectorShot? shot)
        {
            if (shot == null) return;
            var idx = _timeline.IndexOf(shot);
            _timeline.Remove(shot);
            if (SelectedShot == shot)
                SelectedShot = _timeline.ElementAtOrDefault(Math.Min(idx, _timeline.Count - 1));
            OnPropertyChanged(nameof(CanCreateJoined));
        }

        public bool CanMoveLeft => SelectedShot != null && _timeline.IndexOf(SelectedShot) > 0;
        public bool CanMoveRight => SelectedShot != null &&
                                    _timeline.IndexOf(SelectedShot) >= 0 &&
                                    _timeline.IndexOf(SelectedShot) < _timeline.Count - 1;

        private void MoveSelectedLeft()
        {
            if (!CanMoveLeft) return;
            var idx = _timeline.IndexOf(SelectedShot!);
            _timeline.Move(idx, idx - 1);
            OnCanExecuteChanged();
        }

        private void MoveSelectedRight()
        {
            if (!CanMoveRight) return;
            var idx = _timeline.IndexOf(SelectedShot!);
            _timeline.Move(idx, idx + 1);
            OnCanExecuteChanged();
        }

        private void ClearTimeline()
        {
            _timeline.Clear();
            SelectedShot = null;
            OnPropertyChanged(nameof(CanCreateJoined));
        }

        private void Reindex()
        {
            for (int i = 0; i < _timeline.Count; i++)
                _timeline[i].Index = i + 1;
            OnPropertyChanged(nameof(TotalSeconds));
        }
        #endregion

        #region Global options
        private string _resolution = "720p";
        public string Resolution { get => _resolution; set { if (_resolution != value) { _resolution = value; OnPropertyChanged(); } } }

        private string _orientation = "Landscape";
        public string Orientation { get => _orientation; set { if (_orientation != value) { _orientation = value; OnPropertyChanged(); } } }

        public ObservableCollection<SeedHuntPromptTemplate> PromptTemplates => _promptTemplates;
        public SeedHuntPromptTemplate SelectedPromptTemplate
        {
            get => _selectedPromptTemplate;
            set
            {
                if (value != null && _selectedPromptTemplate != value)
                {
                    _selectedPromptTemplate = value;
                    OnPropertyChanged();
                    AddLog($"Analysis template: {value.DisplayName}");
                }
            }
        }

        public ObservableCollection<SeedHuntCharacterLora> CharacterLoras => _characterLoras;
        public SeedHuntCharacterLora SelectedCharacterLora
        {
            get => _selectedCharacterLora;
            set
            {
                if (value != null && _selectedCharacterLora != value)
                {
                    _selectedCharacterLora = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCharacterLora));
                    if (value.RelativePath != null)
                        AddLog($"Character LoRA: {value.DisplayName} @ {CharacterLoraStrength:0.##}");
                }
            }
        }
        public bool HasCharacterLora => _selectedCharacterLora?.RelativePath != null;
        public double CharacterLoraStrength
        {
            get => _characterLoraStrength;
            set { if (Math.Abs(_characterLoraStrength - value) > 0.0001) { _characterLoraStrength = value; OnPropertyChanged(); } }
        }

        private bool _isPreviewMuted;
        public bool IsPreviewMuted
        {
            get => _isPreviewMuted;
            set { if (_isPreviewMuted != value) { _isPreviewMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MuteIcon)); } }
        }
        public string MuteIcon => IsPreviewMuted ? "🔇 Muted" : "🔊 Audio";

        private string? _activePreviewUri;
        public string? ActivePreviewUri
        {
            get => _activePreviewUri;
            private set
            {
                if (_activePreviewUri != value)
                {
                    _activePreviewUri = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasActivePreview));
                }
            }
        }
        public bool HasActivePreview => !string.IsNullOrEmpty(ActivePreviewUri);
        #endregion

        #region Can-flags
        private bool _isAnalyzing;
        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnCanExecuteChanged();
                }
            }
        }

        public bool CanAnalyze => SelectedShot != null && !IsAnalyzing && !IsProcessing;
        public bool CanAnalyzeAll => HasTimeline && !IsAnalyzing && !IsProcessing;
        public bool CanGenerateSeeds => HasTimeline && !IsAnalyzing && !IsProcessing;
        public bool CanRerollShot => SelectedShot != null && !IsAnalyzing && !IsProcessing;
        // Clickable once seeds exist and we're idle; the per-shot "every shot needs a picked seed"
        // rule is validated on click (with a clear message) rather than silently disabling the button.
        public bool CanCreateJoined => HasTimeline && !IsProcessing && !IsAnalyzing &&
                                       _timeline.Any(s => s.HasSamples);

        /// <summary>True only when every shot has at least one checked seed — required to join.</summary>
        public bool AllShotsHaveSeed => HasTimeline && _timeline.All(s => s.HasSelectedSeed);

        /// <summary>Short hint shown by the join button when not every shot has a picked seed.</summary>
        public string JoinHint
        {
            get
            {
                if (!HasTimeline || IsProcessing || AllShotsHaveSeed) return string.Empty;
                var pending = _timeline.Where(s => !s.HasSelectedSeed).Select(s => "#" + s.Index).ToList();
                return pending.Count == 0 ? string.Empty
                    : $"Pick a seed for shot {string.Join(", ", pending)} to enable joining";
            }
        }
        #endregion

        #region Analysis
        private Task AnalyzeSelectedAsync()
        {
            var target = SelectedShot;
            return target == null ? Task.CompletedTask : RunAnalysisAsync(new[] { target });
        }

        private Task AnalyzeAllAsync() => RunAnalysisAsync(_timeline.ToList());

        private async Task RunAnalysisAsync(IReadOnlyList<SeedDirectorShot> targets)
        {
            var shots = targets.Where(t => File.Exists(t.ImagePath)).ToList();
            if (shots.Count == 0) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;
            try
            {
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);

                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                var selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(selectedModel) && models.Count > 0)
                    selectedModel = models[0].Id ?? models[0].Name ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                        "LLM Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var template = SelectedPromptTemplate;
                var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "prompts", "prompt2json", template.FileName);
                if (!File.Exists(promptFilePath))
                    throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
                var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

                if (shots.Count > 1)
                    AddLog($"Analyzing {shots.Count} shots with model: {selectedModel} • template: {template.DisplayName}");

                foreach (var target in shots)
                {
                    token.ThrowIfCancellationRequested();
                    SelectedShot = target; // visual progress: highlight the shot being analyzed
                    AddLog($"Analyzing shot #{target.Index}…");

                    var result = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        selectedModel, target.ImagePath, template.UserInstruction,
                        systemPrompt, maxTokens: 4000, cancellationToken: token);

                    var cleaned = CleanOutput(result);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        target.Prompt = cleaned;
                        AddLog($"Shot #{target.Index} prompt generated ({cleaned.Length} chars)");
                    }
                    else AddLog($"WARNING: Shot #{target.Index} analysis returned empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Replace("**", "").Trim();
            var lower = text.ToLowerInvariant();
            if (lower.StartsWith("prompt:") || lower.StartsWith("prompt :"))
                text = text.Substring(text.IndexOf(':') + 1).Trim();
            return text;
        }
        #endregion

        #region Generate seeds (per-shot hunt)
        /// <summary>Analyzes any shots missing a prompt, then hunts 4 seeds for every shot in order.</summary>
        private async Task GenerateAllSeedsAsync()
        {
            if (!CanGenerateSeeds) return;

            var needAnalyze = _timeline.Where(s => string.IsNullOrWhiteSpace(s.Prompt)).ToList();
            if (needAnalyze.Count > 0)
            {
                AddLog($"Auto-analyzing {needAnalyze.Count} shot(s) with no prompt…");
                await RunAnalysisAsync(needAnalyze);
            }

            var shots = _timeline.ToList();
            await RunWorkflowAsync("Generate Seeds", async (token, reportPhase) =>
            {
                _results.Clear();
                ActivePreviewUri = null;
                HasResult = false;

                for (int i = 0; i < shots.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var shot = shots[i];
                    SelectedShot = shot;
                    var from = i * 100.0 / shots.Count;
                    var to = (i + 1) * 100.0 / shots.Count;
                    reportPhase($"Hunting seeds for shot #{shot.Index} ({i + 1}/{shots.Count})…");
                    await RunHuntForShotAsync(shot, from, to, token, reportPhase);
                }
                ProcessingStatus = "Seeds ready — pick one (or more) per shot, then Create Joined Video";
            });
        }

        private async Task RerollSelectedShotAsync()
        {
            var shot = SelectedShot;
            if (shot == null || !CanRerollShot) return;
            await RunWorkflowAsync("Reroll", async (token, reportPhase) =>
            {
                reportPhase($"Rerolling seeds for shot #{shot.Index}…");
                await RunHuntForShotAsync(shot, 0, 100, token, reportPhase);
                ProcessingStatus = $"Shot #{shot.Index}: fresh seeds ready";
            });
        }

        /// <summary>Runs one SeedHunt Stage-1 batch for a single shot → its 4 preview tiles.</summary>
        private async Task RunHuntForShotAsync(SeedDirectorShot shot, double from, double to,
            CancellationToken token, Action<string> reportPhase)
        {
            // Fresh batch seed each hunt so ComfyUI re-samples (and Finish reuses exactly this seed).
            shot.BatchSeed = NewSeed();
            var batchId = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + shot.Index;

            Application.Current.Dispatcher.Invoke(() => { shot.ResetSamples(); shot.Status = "hunting…"; });

            var imageName = await EnsureShotUploadedAsync(shot);
            var prompt = string.IsNullOrWhiteSpace(shot.Prompt)
                ? "Style: realistic - cinematic - smooth natural motion, photorealistic, high quality"
                : shot.Prompt;

            var json = await LoadWorkflowJsonAsync(token);
            ApplyCommonInputs(ref json, imageName, prompt, shot.DurationSeconds);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed", shot.BatchSeed);

            foreach (var (slot, nodeId) in PreviewNodeBySlot)
            {
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, nodeId, new Dictionary<string, object>
                {
                    { "save_output", true },
                    { "filename_prefix", $"{OutputSubfolder}/sd{batchId}_p{slot}" },
                });
            }

            // Only the 4 fast samples — prune everything that depends on Stage 2/3.
            json = RemoveNodes(json, NodeFinalOutput, NodeStage2Preview);

            var filled = new HashSet<int>();
            var downloads = new List<Task>();
            void OnNode(object? s, NodeExecutedEventArgs e) => HandleHuntNode(shot, e, filled, downloads, token);
            _comfyUIService.NodeExecuted += OnNode;
            try
            {
                var promptId = await SubmitAsync(json, from, to, token);
                Task[] pending;
                lock (downloads) pending = downloads.ToArray();
                try { await Task.WhenAll(pending); } catch { /* per-task errors handled inside */ }
                await FillMissingSamplesAsync(shot, promptId, filled, batchId, token);
            }
            finally
            {
                _comfyUIService.NodeExecuted -= OnNode;
            }

            var found = shot.Samples.Count(x => x.HasVideo);
            Application.Current.Dispatcher.Invoke(() => UpdateShotStatus(shot));
            if (found == 0)
                AddLog($"WARNING: shot #{shot.Index} produced no sample previews.");
            else
                AddLog($"Shot #{shot.Index}: {found}/4 seeds ready");
        }

        /// <summary>Live handler: when a preview node finishes, download + show that sample immediately.</summary>
        private void HandleHuntNode(SeedDirectorShot shot, NodeExecutedEventArgs e,
            HashSet<int> filled, List<Task> downloads, CancellationToken token)
        {
            if (!SlotByPreviewNode.TryGetValue(e.NodeId, out var slot)) return;
            var file = e.Files.FirstOrDefault(f => f.Filename.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? e.Files.FirstOrDefault();
            if (file == null) return;

            lock (filled) { if (!filled.Add(slot)) return; }

            var task = Task.Run(async () =>
            {
                try
                {
                    var local = await DownloadRefToTempAsync(file, token);
                    if (local != null) SetSampleVideo(shot, slot, local);
                    else { lock (filled) { filled.Remove(slot); } }
                }
                catch { lock (filled) { filled.Remove(slot); } }
            }, token);
            lock (downloads) downloads.Add(task);
        }

        private void SetSampleVideo(SeedDirectorShot shot, int slot, string localPath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var sample = shot.Samples.First(s => s.Slot == slot);
                sample.VideoPath = localPath;
                sample.VideoFileUri = localPath;
                sample.Status = "ready";
                UpdateShotStatus(shot);
                AddLog($"  Shot #{shot.Index} seed {slot} ready: {Path.GetFileName(localPath)}");
                OnCanExecuteChanged();
            });

            var thumb = ExtractFirstFrame(localPath);
            if (thumb != null)
                Application.Current.Dispatcher.Invoke(() =>
                    shot.Samples.First(s => s.Slot == slot).ThumbnailImage = thumb);
        }

        private async Task FillMissingSamplesAsync(SeedDirectorShot shot, string promptId,
            HashSet<int> filled, string batchId, CancellationToken token)
        {
            List<KeyValuePair<int, string>> missing;
            lock (filled) missing = PreviewNodeBySlot.Where(kv => !filled.Contains(kv.Key)).ToList();
            if (missing.Count == 0) return;

            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            foreach (var (slot, nodeId) in missing)
            {
                string? local = null;
                if (byNode.TryGetValue(nodeId, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0) ?? outs[0];
                    local = await ResolveOutputToLocalAsync(pick);
                }
                local ??= FindSlotFileOnDisk(batchId, slot);

                if (local != null) { lock (filled) filled.Add(slot); SetSampleVideo(shot, slot, local); }
                else AddLog($"  Shot #{shot.Index} seed {slot}: no output found (node {nodeId})");
            }
        }
        #endregion

        #region Create joined video (finish + concat)
        private void PreviewSample(SeedHuntSample? sample)
        {
            if (sample == null || !sample.HasVideo) return;
            ActivePreviewUri = sample.VideoFileUri;
        }

        private void PlayResult(SeedDirectorResult? result)
        {
            if (result != null) ActivePreviewUri = result.VideoFileUri;
        }

        /// <summary>
        /// Final button: render each unique (shot, selected-seed) clip to high-res once, then build
        /// every combination across shots (cartesian product) and FFmpeg-concat each into one video.
        /// </summary>
        private async Task CreateJoinedVideoAsync()
        {
            if (!CanCreateJoined) return;

            var shots = _timeline.ToList();

            // Every shot must contribute a clip, else the joined timeline has a gap. Tell the user
            // exactly which shots still need a seed instead of silently refusing.
            var noSeeds = shots.Where(s => !s.HasSamples).Select(s => s.Index).ToList();
            var unpicked = shots.Where(s => s.HasSamples && !s.HasSelectedSeed).Select(s => s.Index).ToList();
            if (noSeeds.Count > 0 || unpicked.Count > 0)
            {
                var msg = new StringBuilder("Every shot needs at least one seed selected (check ✓) before joining.\n");
                if (noSeeds.Count > 0)
                    msg.Append($"\n• No seed previews yet — generate seeds for shot(s) {string.Join(", ", noSeeds.Select(i => "#" + i))}.");
                if (unpicked.Count > 0)
                    msg.Append($"\n• Previews ready but nothing checked — pick a seed for shot(s) {string.Join(", ", unpicked.Select(i => "#" + i))}.");
                MessageBox.Show(msg.ToString(), "Seed Director", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var perShotSlots = shots.Select(s => s.SelectedSlots).ToList();
            var combinations = CartesianSlots(perShotSlots);

            if (combinations.Count == 0) return;
            if (combinations.Count > 1)
                AddLog($"{combinations.Count} seed combination(s) selected → {combinations.Count} joined video(s) will be produced.");

            await RunWorkflowAsync("Finish", async (token, reportPhase) =>
            {
                _results.Clear();
                ActivePreviewUri = null;
                HasResult = false;

                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null)
                    throw new Exception("FFmpeg not found — required to join the shots.");

                // 1) Render each unique (shotIndex, slot) hi-res clip exactly once.
                var unique = new List<(int shotIdx, int slot)>();
                for (int i = 0; i < shots.Count; i++)
                    foreach (var slot in perShotSlots[i])
                        unique.Add((i, slot));

                var clipCache = new Dictionary<(int, int), string>();
                for (int u = 0; u < unique.Count; u++)
                {
                    token.ThrowIfCancellationRequested();
                    var (shotIdx, slot) = unique[u];
                    var shot = shots[shotIdx];
                    var from = u * 90.0 / unique.Count;
                    var to = (u + 1) * 90.0 / unique.Count;
                    reportPhase($"Rendering hi-res clip — shot #{shot.Index} seed {slot} ({u + 1}/{unique.Count})…");
                    var clip = await FinishShotSlotAsync(shot, slot, from, to, token);
                    if (clip == null)
                        throw new Exception($"Shot #{shot.Index} seed {slot}: no final video produced.");
                    clipCache[(shotIdx, slot)] = clip;
                }

                // 2) Concatenate each combination in timeline order.
                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "SeedDirector");
                Directory.CreateDirectory(outputDir);

                for (int c = 0; c < combinations.Count; c++)
                {
                    token.ThrowIfCancellationRequested();
                    var combo = combinations[c]; // one slot per shot, in timeline order
                    var clips = new List<string>();
                    for (int i = 0; i < shots.Count; i++)
                        clips.Add(clipCache[(i, combo[i])]);

                    var pct = 90.0 + (c + 1) * 10.0 / combinations.Count;
                    ProcessingProgress = pct;
                    reportPhase($"Joining variant {c + 1}/{combinations.Count} ({shots.Count} shots)…");

                    var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var comboTag = string.Join("-", combo);
                    var finalPath = Path.Combine(outputDir,
                        $"SeedDirector_{ts}_v{c + 1}_s{comboTag}.mp4");
                    await ConcatClipsAsync(ffmpeg, clips, finalPath, token);
                    if (!File.Exists(finalPath))
                    {
                        AddLog($"Variant {c + 1}: concat produced no file — skipping");
                        continue;
                    }
                    await LocalCopyService.CopyVideoAsync(finalPath);

                    var fi = new FileInfo(finalPath);
                    var result = new SeedDirectorResult
                    {
                        Label = combinations.Count > 1 ? $"Variant {c + 1}" : "Joined Video",
                        VideoPath = finalPath,
                        VideoFileUri = finalPath,
                        Info = $"{shots.Count} shots • seeds [{comboTag}] • {fi.Length / 1024 / 1024.0:F1}MB"
                    };
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _results.Add(result);
                        ResultVideoPath = finalPath;
                        ResultVideoInfo = result.Info;
                        ActivePreviewUri = finalPath;
                        HasResult = true;
                        OnCanExecuteChanged();
                    });
                    AddLog($"=== Variant {c + 1} complete: {finalPath} ===");
                }

                ProcessingStatus = $"Done — {_results.Count} joined video(s)";
            });
        }

        /// <summary>SeedHunt finish for one (shot, slot): reuse the shot's cached Stage-1 latent and
        /// upscale the chosen seed through Stage 2/3 → a high-res clip; returns its local path.</summary>
        private async Task<string?> FinishShotSlotAsync(SeedDirectorShot shot, int slot,
            double from, double to, CancellationToken token)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var imageName = await EnsureShotUploadedAsync(shot);
            var prompt = string.IsNullOrWhiteSpace(shot.Prompt)
                ? "Style: realistic - cinematic - smooth natural motion, photorealistic, high quality"
                : shot.Prompt;

            var json = await LoadWorkflowJsonAsync(token);
            // Identical Stage-1 inputs (image/prompt/duration/resolution/batch-seed) → cached latent.
            ApplyCommonInputs(ref json, imageName, prompt, shot.DurationSeconds);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeBatchSeed, "seed",
                shot.BatchSeed >= 0 ? shot.BatchSeed : NewSeed());

            // Wire the chosen sampler's latent directly (ImpactSwitch/mxSlider are API-incompatible).
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeSepAfterSwitch, "av_latent",
                new object[] { SamplerOutputBySlot[slot], 0 });

            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage2Seed, "seed", NewSeed());
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage3Seed, "seed", NewSeed());
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeFinalOutput, "filename_prefix",
                $"{OutputSubfolder}/final_s{shot.Index}_p{slot}_{ts}");

            json = RemoveNodes(json, PreviewNodeBySlot.Values
                .Append(NodeStage2Preview).Append(NodeSelect).Append(NodeSelectSwitch).ToArray());

            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            string? outputVideo = null;
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(NodeFinalOutput, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0) ?? outs[0];
                outputVideo = await ResolveOutputToLocalAsync(pick);
            }
            outputVideo ??= await WaitForNewVideoAsync(
                existing, "*.mp4", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5), OutputSubfolder);

            return outputVideo != null && File.Exists(outputVideo) ? outputVideo : null;
        }

        /// <summary>Builds every combination of one selected slot per shot, in timeline order.</summary>
        private static List<List<int>> CartesianSlots(List<IReadOnlyList<int>> perShot)
        {
            var result = new List<List<int>> { new() };
            foreach (var slots in perShot)
            {
                if (slots.Count == 0) return new List<List<int>>(); // a shot with no selection → nothing
                var next = new List<List<int>>();
                foreach (var combo in result)
                    foreach (var slot in slots)
                        next.Add(new List<int>(combo) { slot });
                result = next;
            }
            return result;
        }

        /// <summary>Concatenates clips (all same resolution/fps — they share the workflow output) into
        /// one MP4 via FFmpeg's concat demuxer with a re-encode (robust to copy/codec edge-cases).</summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath, CancellationToken token)
        {
            if (clips.Count == 1)
            {
                File.Copy(clips[0], outPath, true);
                return;
            }

            var listPath = Path.Combine(Path.GetTempPath(), $"seeddir_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
                sb.AppendLine($"file '{clip.Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) throw new Exception("Failed to start FFmpeg.");
                var stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                    AddLog($"FFmpeg concat exited {p.ExitCode}: {Tail(stderr, 400)}");
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* best effort */ }
            }
        }

        private static string Tail(string s, int n) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
        #endregion

        #region Character LoRA discovery (from SeedHunt)
        private async Task RefreshCharacterLorasAsync()
        {
            var previous = _selectedCharacterLora?.RelativePath;
            _characterLoras.Clear();
            _characterLoras.Add(SeedHuntCharacterLora.None);

            try
            {
                var prefix = CharacterLoraSubfolder + "/";
                var reported = await _comfyUIService.HttpClient.GetLoraFilenamesAsync();
                var matches = reported
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matches.Count > 0)
                {
                    foreach (var path in matches)
                        _characterLoras.Add(new SeedHuntCharacterLora(Path.GetFileNameWithoutExtension(path), path));
                    AddLog($"Character LoRAs from ComfyUI: {matches.Count}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Character LoRA scan failed: {ex.Message}");
            }

            SelectedCharacterLora = _characterLoras.FirstOrDefault(l => l.RelativePath == previous)
                                    ?? SeedHuntCharacterLora.None;
        }
        #endregion

        #region Shared workflow runner (from SeedHunt)
        private async Task RunWorkflowAsync(string phase, Func<CancellationToken, Action<string>, Task> body)
        {
            IsProcessing = true;
            ProcessingProgress = 0;
            ProcessingStatus = $"Preparing {phase}...";

            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog($"=== Seed Director {phase} ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync($"SeedDirector-{phase}", token);

                ProcessingStatus = "Checking ComfyUI...";
                if (!await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}")))
                    throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                await body(token, status => Application.Current.Dispatcher.Invoke(() => ProcessingStatus = status));
                ProcessingProgress = 100;
            }
            catch (OperationCanceledException)
            {
                AddLog($"{phase} cancelled");
                ProcessingStatus = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR ({phase}): {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                MessageBox.Show($"{phase} failed:\n{ex.Message}", "Seed Director Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                OnCanExecuteChanged();
            }
        }

        private async Task<string> EnsureShotUploadedAsync(SeedDirectorShot shot)
        {
            if (!string.IsNullOrEmpty(shot.UploadedName)) return shot.UploadedName;
            AddLog($"Uploading shot #{shot.Index} image…");
            var uploaded = await _comfyUIService.UploadImageAsync(shot.ImagePath);
            if (string.IsNullOrEmpty(uploaded))
                throw new Exception($"Failed to upload image for shot #{shot.Index}.");
            shot.UploadedName = uploaded;
            return uploaded;
        }

        private static async Task<string> LoadWorkflowJsonAsync(CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Workflow file not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        private void ApplyCommonInputs(ref string json, string imageName, string prompt, double durationSeconds)
        {
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeImage, "image", imageName);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodePrompt, "text", prompt);

            var charLora = SelectedCharacterLora;
            if (charLora?.RelativePath is { Length: > 0 } loraPath)
            {
                var strength = Math.Clamp(CharacterLoraStrength, 0, 3);
                WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeStage1Loras, "lora_2",
                    new { on = true, lora = loraPath, strength });
            }

            // Global resolution + orientation drives every shot's dims so all clips share WxH.
            var (tw, th) = TargetResolution();
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetWidth, "value", tw);
            WorkflowNodeUpdater.UpdateNodeInput(ref json, NodeTargetHeight, "value", th);

            var len = Math.Clamp(durationSeconds <= 0 ? 3 : durationSeconds, 1, 60);
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref json, NodeLength, new Dictionary<string, object>
            {
                { "Xi", len }, { "Xf", len },
            });
        }

        /// <summary>(width, height) from the global resolution preset + orientation.</summary>
        private (int width, int height) TargetResolution()
        {
            var (lw, lh) = Resolution switch
            {
                "360p" => (640, 360),
                "480p" => (854, 480),
                "720p" => (1280, 720),
                "1080p" => (1920, 1080),
                "1440p" => (2560, 1440),
                "4k" => (3840, 2160),
                _ => (1280, 720),
            };
            return Orientation switch
            {
                "Portrait" => (lh, lw),
                "Square" => (lh, lh),
                _ => (lw, lh),
            };
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"{msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        private static string RemoveNodes(string json, params string[] ids)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict == null) return json;
            foreach (var id in ids) dict.Remove(id);
            return JsonSerializer.Serialize(dict);
        }

        private async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath)) { await WaitForFileStableAsync(localPath); return localPath; }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : "";
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"seeddir_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        private async Task<string?> DownloadRefToTempAsync(OutputFileRef r, CancellationToken token)
        {
            var settings = _settingsService.Settings;
            if (settings != null)
            {
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                string outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                if (!string.IsNullOrEmpty(outputFolder) && r.Type == "output")
                {
                    var localPath = Path.Combine(outputFolder, r.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(localPath)) { await WaitForFileStableAsync(localPath); return localPath; }
                }
            }

            var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(r.Filename, r.Subfolder, r.Type, token);
            if (bytes is { Length: > 0 })
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"seeddir_{Guid.NewGuid():N}_{r.Filename}");
                await File.WriteAllBytesAsync(tempPath, bytes, token);
                return tempPath;
            }
            return null;
        }

        private string? FindSlotFileOnDisk(string batchId, int slot)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                bool isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = isRemote ? settings.RemoteOutputFolderPath : settings.OutputFolderPath;
                if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder)) return null;

                var tokenStr = $"sd{batchId}_p{slot}";
                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4")
                            .Where(f => Path.GetFileName(f).IndexOf(tokenStr, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                if (candidates.Count == 0) return null;
                return candidates
                    .OrderByDescending(f => f.IndexOf("-audio", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenByDescending(File.GetLastWriteTime)
                    .First();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Extracts the first frame of a video to a BitmapImage via ffmpeg (best-effort).</summary>
        private BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"seeddir_thumb_{Guid.NewGuid():N}.png");
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoPath}\" -frames:v 1 -q:v 3 \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                }
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(outPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                AddLog($"Thumbnail extract failed: {ex.Message}");
                return null;
            }
        }

        private static long NewSeed() => Random.Shared.NextInt64(0, 1_000_000_000_000_000L);
        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanMoveLeft));
            OnPropertyChanged(nameof(CanMoveRight));
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanAnalyzeAll));
            OnPropertyChanged(nameof(CanGenerateSeeds));
            OnPropertyChanged(nameof(CanRerollShot));
            OnPropertyChanged(nameof(CanCreateJoined));
            OnPropertyChanged(nameof(AllShotsHaveSeed));
            OnPropertyChanged(nameof(JoinHint));
            MoveLeftCommand.NotifyCanExecuteChanged();
            MoveRightCommand.NotifyCanExecuteChanged();
            ClearTimelineCommand.NotifyCanExecuteChanged();
            AnalyzeSelectedCommand.NotifyCanExecuteChanged();
            AnalyzeAllCommand.NotifyCanExecuteChanged();
            GenerateAllSeedsCommand.NotifyCanExecuteChanged();
            RerollShotCommand.NotifyCanExecuteChanged();
            CreateJoinedVideoCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
        }
    }
}
