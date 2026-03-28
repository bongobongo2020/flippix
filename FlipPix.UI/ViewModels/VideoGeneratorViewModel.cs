using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels
{
    /// <summary>
    /// Composer ViewModel for the Video Generator window.
    /// Orchestrates sub-ViewModels for different video generation features:
    /// - MainVM: Core i2v with first/last frame, prompt queue, story queue
    /// - VaceVM: VACE extended video generation
    /// - LTX2AudioVM: Audio-synchronized video generation
    /// - MochaVM: Motion capture video generation
    ///
    /// This class provides backward-compatible property forwarding so existing
    /// XAML bindings continue to work without modification.
    /// </summary>
    public partial class VideoGeneratorViewModel : ObservableObject, IDisposable
    {
        private bool _disposed = false;
        private readonly ComfyUIService _comfyUIService;
        private readonly LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;
        private readonly IFileDialogService _fileDialogService;

        public event EventHandler? PlayRequested;

        /// <summary>
        /// Main video generation ViewModel - handles core i2v with first/last frame,
        /// video settings, image analysis, prompt queue, and story video queue.
        /// </summary>
        public VideoGeneratorMainViewModel MainVM { get; }

        /// <summary>
        /// VACE extended video generation ViewModel - handles reference image and input video
        /// image composition with video output.
        /// </summary>
        public VACEVideoViewModel VaceVM { get; }

        /// <summary>
        /// LTX2 Audio-synchronized video generation ViewModel - handles audio-driven
        /// video generation with chunk processing for long audio files.
        /// </summary>
        public LTX2AudioViewModel LTX2AudioVM { get; }

        /// <summary>
        /// Mocha motion capture video generation ViewModel - handles motion transfer
        /// from source videos with 81-frame chunk processing.
        /// </summary>
        public MochaVideoViewModel MochaVM { get; }

        /// <summary>
        /// InfiniteTalk video generation ViewModel - handles Wan2.1 InfiniteTalk
        /// video generation with audio-driven 81-frame chunk processing.
        /// </summary>
        public InfiniteTalkViewModel InfiniteTalkVM { get; }

        /// <summary>
        /// LTX 2.3 basic image-to-video ViewModel - handles single reference image input,
        /// AI analysis, prompt enhancement, and video generation with the LTX 2.3 GGUF workflow.
        /// </summary>
        public LTX23BasicViewModel LTX23BasicVM { get; }

        /// <summary>
        /// LTX 2.3 text-to-video ViewModel - generates video purely from a text prompt
        /// using the LTX-2.3T2VGGUFAPI workflow with no image reference required.
        /// </summary>
        public LTX23T2VViewModel LTX23T2VVM { get; }

        public VideoGeneratorViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider = null,
            IFileDialogService? fileDialogService = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = serviceProvider?.GetRequiredService<WorkflowQueueCoordinator>()
                ?? throw new InvalidOperationException("WorkflowQueueCoordinator is required");
            _fileDialogService = fileDialogService ?? serviceProvider?.GetRequiredService<IFileDialogService>()
                ?? throw new InvalidOperationException("IFileDialogService is required");

            // Initialize sub-ViewModels
            MainVM = new VideoGeneratorMainViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            VaceVM = new VACEVideoViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            LTX2AudioVM = new LTX2AudioViewModel(
                comfyUIService,
                logger,
                lmStudioService,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            MochaVM = new MochaVideoViewModel(
                comfyUIService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService,
                lmStudioService);

            InfiniteTalkVM = new InfiniteTalkViewModel(
                comfyUIService,
                logger,
                lmStudioService,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            LTX23BasicVM = new LTX23BasicViewModel(
                comfyUIService,
                logger,
                lmStudioService,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            LTX23T2VVM = new LTX23T2VViewModel(
                comfyUIService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator);

            // Forward PlayRequested events from sub-VMs
            MainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            VaceVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            LTX2AudioVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MochaVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            InfiniteTalkVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            LTX23BasicVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            LTX23T2VVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);

            // Forward PropertyChanged events from all sub-VMs for backward compatibility
            MainVM.PropertyChanged += ForwardPropertyChanged;
            VaceVM.PropertyChanged += ForwardPropertyChanged;
            LTX2AudioVM.PropertyChanged += ForwardPropertyChanged;
            MochaVM.PropertyChanged += ForwardPropertyChanged;
            InfiniteTalkVM.PropertyChanged += ForwardPropertyChanged;
            LTX23BasicVM.PropertyChanged += ForwardPropertyChanged;
            LTX23T2VVM.PropertyChanged += ForwardPropertyChanged;

            _logger.LogInfo("VideoGeneratorViewModel initialized with sub-ViewModels");
        }

        private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null) return;

            // Re-fire with the original property name (handles any direct-name bindings).
            OnPropertyChanged(e.PropertyName);

            // Re-fire with empty string to refresh ALL bindings on this DataContext.
            // This is required because the parent VM exposes aliased pass-through properties
            // (e.g. VaceBackgroundImagePreview → VaceVM.BackgroundImagePreview). When the
            // sub-VM fires PropertyChanged("BackgroundImagePreview"), the XAML binding on
            // VaceBackgroundImagePreview would otherwise never see the notification.
            OnPropertyChanged(string.Empty);
        }

        #region MainVM Backward Compatibility Properties

        // First Frame Image properties
        public string FirstFrameImagePath { get => MainVM.FirstFrameImagePath; set => MainVM.FirstFrameImagePath = value; }
        public BitmapImage? FirstFrameImagePreview { get => MainVM.FirstFrameImagePreview; set => MainVM.FirstFrameImagePreview = value; }
        public string FirstFrameImageInfo { get => MainVM.FirstFrameImageInfo; set => MainVM.FirstFrameImageInfo = value; }
        public bool HasFirstFrameImage => MainVM.HasFirstFrameImage;

        // Last Frame Image properties
        public string LastFrameImagePath { get => MainVM.LastFrameImagePath; set => MainVM.LastFrameImagePath = value; }
        public BitmapImage? LastFrameImagePreview { get => MainVM.LastFrameImagePreview; set => MainVM.LastFrameImagePreview = value; }
        public string LastFrameImageInfo { get => MainVM.LastFrameImageInfo; set => MainVM.LastFrameImageInfo = value; }
        public bool HasLastFrameImage => MainVM.HasLastFrameImage;

        // Image properties
        public string ImageFilePath { get => MainVM.ImageFilePath; set => MainVM.ImageFilePath = value; }
        public BitmapImage? ImagePreviewSource { get => MainVM.ImagePreviewSource; set => MainVM.ImagePreviewSource = value; }
        public string ImageInfo { get => MainVM.ImageInfo; set => MainVM.ImageInfo = value; }
        public bool HasImage => MainVM.HasImage;

        // Prompt properties
        public string VideoPrompt { get => MainVM.VideoPrompt; set => MainVM.VideoPrompt = value; }
        public string NegativePrompt { get => MainVM.NegativePrompt; set => MainVM.NegativePrompt = value; }

        // Video Settings
        public int VideoLength { get => MainVM.VideoLength; set => MainVM.VideoLength = value; }
        public string VideoLengthSeconds => MainVM.VideoLengthSeconds;
        public int Fps { get => MainVM.Fps; set => MainVM.Fps = value; }
        public int Steps { get => MainVM.Steps; set => MainVM.Steps = value; }
        public double Cfg { get => MainVM.Cfg; set => MainVM.Cfg = value; }
        public long Seed { get => MainVM.Seed; set => MainVM.Seed = value; }
        public int Width { get => MainVM.Width; set => MainVM.Width = value; }
        public int Height { get => MainVM.Height; set => MainVM.Height = value; }

        // Processing state
        public bool IsProcessing { get => MainVM.IsProcessing; set => MainVM.IsProcessing = value; }
        public string ProcessingStatus { get => MainVM.ProcessingStatus; set => MainVM.ProcessingStatus = value; }
        public double ProcessingProgress { get => MainVM.ProcessingProgress; set => MainVM.ProcessingProgress = value; }
        public string ProgressPercentage => MainVM.ProgressPercentage;
        public string LogOutput => MainVM.LogOutput;
        public string StatusBarMessage { get => MainVM.StatusBarMessage; set => MainVM.StatusBarMessage = value; }
        public string VideoInfo { get => MainVM.VideoInfo; set => MainVM.VideoInfo = value; }

        // Result state
        public bool HasResultVideo { get => MainVM.HasResult; set => MainVM.HasResult = value; }
        public string ResultVideoPath { get => MainVM.ResultVideoPath; set => MainVM.ResultVideoPath = value; }

        // Image analysis properties
        public bool IsAnalyzing { get => MainVM.IsAnalyzing; set => MainVM.IsAnalyzing = value; }
        public string AnalysisStatus { get => MainVM.AnalysisStatus; set => MainVM.AnalysisStatus = value; }
        public double AnalysisProgress { get => MainVM.AnalysisProgress; set => MainVM.AnalysisProgress = value; }
        public string ImageAnalysis { get => MainVM.ImageAnalysis; set => MainVM.ImageAnalysis = value; }
        public bool HasAnalysis => MainVM.HasAnalysis;

        // Queue properties
        public string NewQueuePrompt { get => MainVM.NewQueuePrompt; set => MainVM.NewQueuePrompt = value; }
        public ObservableCollection<QueueItem> PromptQueue => MainVM.PromptQueue;
        public bool IsProcessingQueue { get => MainVM.IsProcessingQueue; set => MainVM.IsProcessingQueue = value; }
        public bool IsQueuePaused { get => MainVM.IsQueuePaused; set => MainVM.IsQueuePaused = value; }
        public bool CanProcessQueue => MainVM.CanProcessQueue;
        public bool CanAddToQueue => MainVM.CanAddToQueue;
        public bool HasFailedItems => MainVM.HasFailedItems;
        public string QueueStatus { get => MainVM.QueueStatus; set => MainVM.QueueStatus = value; }
        public bool CanGenerateVideo => MainVM.CanGenerateVideo;

        // Story Video Generator Properties
        public string StoryPromptJsonPath { get => MainVM.StoryPromptJsonPath; set => MainVM.StoryPromptJsonPath = value; }
        public string StoryImagesFolderPath { get => MainVM.StoryImagesFolderPath; set => MainVM.StoryImagesFolderPath = value; }
        public ObservableCollection<StoryVideoQueueItem> StoryVideoQueue => MainVM.StoryVideoQueue;
        public bool IsProcessingStoryQueue { get => MainVM.IsProcessingStoryQueue; set => MainVM.IsProcessingStoryQueue = value; }
        public bool IsStoryQueuePaused { get => MainVM.IsStoryQueuePaused; set => MainVM.IsStoryQueuePaused = value; }
        public StoryVideoQueueItem? CurrentStoryQueueItem { get => MainVM.CurrentStoryQueueItem; set => MainVM.CurrentStoryQueueItem = value; }
        public int StoryQueueProgress { get => MainVM.StoryQueueProgress; set => MainVM.StoryQueueProgress = value; }
        public int StoryQueueTotal { get => MainVM.StoryQueueTotal; set => MainVM.StoryQueueTotal = value; }
        public string StoryQueueProgressText => MainVM.StoryQueueProgressText;
        public bool CanLoadStoryQueue => MainVM.CanLoadStoryQueue;
        public bool CanProcessStoryQueue => MainVM.CanProcessStoryQueue;
        public string StoryQueueStatus { get => MainVM.StoryQueueStatus; set => MainVM.StoryQueueStatus = value; }

        // Workflow selection
        public string SelectedWorkflow { get => MainVM.SelectedWorkflow; set => MainVM.SelectedWorkflow = value; }
        public bool UseLTXWorkflow { get => MainVM.UseLTXWorkflow; set => MainVM.UseLTXWorkflow = value; }
        public string WorkflowDisplay => MainVM.WorkflowDisplay;
        public string WorkflowIndicator => MainVM.WorkflowIndicator;
        public int SelectedWorkflowIndex { get => MainVM.SelectedWorkflowIndex; set => MainVM.SelectedWorkflowIndex = value; }

        // Single Video Generator Workflow (Tab 1) - separate from Story Video (Tab 2)
        public VideoGeneratorMainViewModel.SingleVideoWorkflow SelectedSingleWorkflow
        {
            get => MainVM.SelectedSingleWorkflow;
            set => MainVM.SelectedSingleWorkflow = value;
        }
        public string SingleWorkflowDisplay => MainVM.SingleWorkflowDisplay;
        public bool UseLTX2V => MainVM.UseLTX2V;
        public bool UseWan22 => MainVM.UseWan22;

        // UI state
        public string ComfyUIServer { get => MainVM.ComfyUIServer; set => MainVM.ComfyUIServer = value; }
        public string ComfyUIPort { get => MainVM.ComfyUIPort; set => MainVM.ComfyUIPort = value; }

        // MainVM Commands
        public ICommand SelectImageCommand => MainVM.SelectImageCommand;
        public ICommand SelectFirstFrameImageCommand => MainVM.SelectFirstFrameImageCommand;
        public ICommand SelectLastFrameImageCommand => MainVM.SelectLastFrameImageCommand;
        public ICommand GenerateVideoCommand => MainVM.GenerateVideoCommand;
        public ICommand PlayVideoCommand => MainVM.PlayVideoCommand;
        public ICommand OpenResultFolderCommand => MainVM.OpenResultFolderCommand;
        public ICommand SendToEditCameraCommand => MainVM.SendToEditCameraCommand;
        public ICommand AnalyzeImageCommand => MainVM.AnalyzeImageCommand;
        public ICommand AnalyzeFirstFrameImageCommand => MainVM.AnalyzeFirstFrameImageCommand;
        public ICommand SendAnalysisToQueueCommand => MainVM.SendAnalysisToQueueCommand;
        public ICommand OpenLMStudioSettingsCommand => MainVM.OpenLMStudioSettingsCommand;
        public ICommand CopyAnalysisCommand => MainVM.CopyAnalysisCommand;
        public ICommand AddToQueueCommand => MainVM.AddToQueueCommand;
        public ICommand RemoveFromQueueCommand => MainVM.RemoveFromQueueCommand;
        public ICommand ProcessQueueCommand => MainVM.ProcessQueueCommand;
        public ICommand ReprocessItemCommand => MainVM.ReprocessItemCommand;
        public ICommand ReprocessAllFailedCommand => MainVM.ReprocessAllFailedCommand;
        public ICommand PauseQueueCommand => MainVM.PauseQueueCommand;
        public ICommand ResumeQueueCommand => MainVM.ResumeQueueCommand;
        public ICommand SelectStoryPromptJsonCommand => MainVM.SelectStoryPromptJsonCommand;
        public ICommand SelectStoryImagesFolderCommand => MainVM.SelectStoryImagesFolderCommand;
        public ICommand LoadStoryQueueCommand => MainVM.LoadStoryQueueCommand;
        public ICommand ProcessStoryQueueCommand => MainVM.ProcessStoryQueueCommand;
        public ICommand ClearStoryQueueCommand => MainVM.ClearStoryQueueCommand;
        public ICommand PauseStoryQueueCommand => MainVM.PauseStoryQueueCommand;
        public ICommand ResumeStoryQueueCommand => MainVM.ResumeStoryQueueCommand;
        public ICommand ToggleWorkflowCommand => MainVM.ToggleWorkflowCommand;
        public ICommand ToggleSingleWorkflowCommand => MainVM.ToggleSingleWorkflowCommand;

        #endregion

        #region VaceVM Backward Compatibility Properties

        public string VacePrompt { get => VaceVM.Prompt; set => VaceVM.Prompt = value; }
        public string VaceForegroundImagePath { get => VaceVM.ForegroundImagePath; set => VaceVM.ForegroundImagePath = value; }
        public BitmapImage? VaceForegroundImagePreview { get => VaceVM.ForegroundImagePreview; set => VaceVM.ForegroundImagePreview = value; }
        public string VaceForegroundImageInfo { get => VaceVM.ForegroundImageInfo; set => VaceVM.ForegroundImageInfo = value; }
        public bool HasVACEForegroundImage => VaceVM.HasForegroundImage;
        public string VaceVideoPath { get => VaceVM.InputVideoPath; set => VaceVM.InputVideoPath = value; }
        public string VaceVideoInfo { get => VaceVM.InputVideoInfo; set => VaceVM.InputVideoInfo = value; }
        public bool HasVACEVideo => VaceVM.HasInputVideo;
        public bool IsProcessingVACE { get => VaceVM.IsProcessing; set => VaceVM.IsProcessing = value; }
        public string VaceProcessingStatus => VaceVM.ProcessingStatus;
        public double VaceProcessingProgress => VaceVM.ProcessingProgress;
        public string VaceLogOutput => VaceVM.LogOutput;
        public bool HasVACEResult => VaceVM.HasResult;
        public string VaceResultPath => VaceVM.ResultVideoPath;
        public bool CanGenerateVACEVideo => VaceVM.CanGenerateVideo;
        public bool IsAnalyzingVACE => VaceVM.IsAnalyzing;
        public int VaceTotalFrames => VaceVM.TotalFrames;
        public int VaceTotalChunks => VaceVM.TotalChunks;
        public string VaceProgressPercentage => VaceVM.ProgressPercentage;
        public string VaceResultVideoInfo => VaceVM.ResultVideoInfo;
        public ICommand AnalyzeVACEImageCommand => VaceVM.AnalyzeImageCommand;

        public bool VaceCanAddToQueue => VaceVM.CanAddToQueue;
        public ObservableCollection<VaceQueueItem> VaceQueue => VaceVM.Queue;
        public bool VaceHasQueueItems => VaceVM.HasQueueItems;
        public bool VaceIsProcessingQueue => VaceVM.IsProcessingQueue;
        public string VaceQueueStatus => VaceVM.QueueStatus;

        // VaceVM Commands
        public ICommand SelectVACEForegroundImageCommand => VaceVM.SelectForegroundImageCommand;
        public ICommand SelectVACEVideoCommand => VaceVM.SelectVideoCommand;
        public ICommand GenerateVACEVideoCommand => VaceVM.GenerateVideoCommand;
        public ICommand RemoveVaceQueueItemCommand => VaceVM.RemoveQueueItemCommand;
        public ICommand PlayVACEVideoCommand => VaceVM.PlayVideoCommand;
        public ICommand OpenVACEResultFolderCommand => VaceVM.OpenResultFolderCommand;

        #endregion

        #region LTX2AudioVM Backward Compatibility Properties

        public string LTX2AudioImagePath { get => LTX2AudioVM.ImagePath; set => LTX2AudioVM.ImagePath = value; }
        public BitmapImage? LTX2AudioImagePreview { get => LTX2AudioVM.ImagePreview; set => LTX2AudioVM.ImagePreview = value; }
        public string LTX2AudioImageInfo { get => LTX2AudioVM.ImageInfo; set => LTX2AudioVM.ImageInfo = value; }
        public string LTX2AudioPath { get => LTX2AudioVM.AudioPath; set => LTX2AudioVM.AudioPath = value; }
        public string LTX2AudioInfo { get => LTX2AudioVM.AudioInfo; set => LTX2AudioVM.AudioInfo = value; }
        public string LTX2AudioPrompt { get => LTX2AudioVM.Prompt; set => LTX2AudioVM.Prompt = value; }
        public int LTX2AudioWidth { get => LTX2AudioVM.Width; set => LTX2AudioVM.Width = value; }
        public int LTX2AudioHeight { get => LTX2AudioVM.Height; set => LTX2AudioVM.Height = value; }
        public bool IsProcessingLTX2Audio { get => LTX2AudioVM.IsProcessing; set => LTX2AudioVM.IsProcessing = value; }
        public string LTX2AudioProcessingStatus => LTX2AudioVM.ProcessingStatus;
        public double LTX2AudioProcessingProgress => LTX2AudioVM.ProcessingProgress;
        public string LTX2AudioLogOutput => LTX2AudioVM.LogOutput;
        public bool HasLTX2AudioResult => LTX2AudioVM.HasResult;
        public string LTX2AudioResultPath => LTX2AudioVM.ResultVideoPath;
        public string LTX2AudioVideoInfo => LTX2AudioVM.ResultVideoInfo;
        public double LTX2AudioDuration => LTX2AudioVM.AudioDuration;
        public int LTX2AudioTotalFrames => LTX2AudioVM.TotalFrames;
        public bool CanGenerateLTX2AudioVideo => LTX2AudioVM.CanGenerateVideo;

        // LTX2AudioVM Commands
        public ICommand SelectLTX2AudioImageCommand => LTX2AudioVM.SelectImageCommand;
        public ICommand SelectLTX2AudioCommand => LTX2AudioVM.SelectAudioCommand;
        public ICommand GenerateLTX2AudioVideoCommand => LTX2AudioVM.GenerateVideoCommand;
        public ICommand PlayLTX2AudioVideoCommand => LTX2AudioVM.PlayVideoCommand;
        public ICommand OpenLTX2AudioResultFolderCommand => LTX2AudioVM.OpenResultFolderCommand;
        public ICommand SendLTX2AudioToEditCameraCommand => LTX2AudioVM.SendToEditCameraCommand;

        // LMStudio AI analysis properties
        public bool LTX2AudioIsAnalyzing   => LTX2AudioVM.IsAnalyzing;
        public bool CanLTX2AnalyzeImage    => LTX2AudioVM.CanAnalyzeImage;
        public bool CanLTX2EnhancePrompt   => LTX2AudioVM.CanEnhancePrompt;
        public bool ShowLTX2AudioVideoPrompt => LTX2AudioVM.ShowVideoPrompt;
        public string LTX2AudioAnalysisResult => LTX2AudioVM.AnalysisResult;
        public bool HasLTX2AudioAnalysis => LTX2AudioVM.HasAnalysis;

        // LMStudio AI analysis commands
        public ICommand AnalyzeLTX2AudioImageCommand  => LTX2AudioVM.AnalyzeImageCommand;
        public ICommand EnhanceLTX2AudioPromptCommand => LTX2AudioVM.EnhancePromptCommand;

        #endregion

        #region MochaVM Backward Compatibility Properties

        public string MochaVideoPath { get => MochaVM.VideoPath; set => MochaVM.VideoPath = value; }
        public string MochaSourceVideoInfo => MochaVM.SourceVideoInfo;
        public string MochaImagePath { get => MochaVM.ImagePath; set => MochaVM.ImagePath = value; }
        public BitmapImage? MochaImagePreview { get => MochaVM.ImagePreview; set => MochaVM.ImagePreview = value; }
        public string MochaImageInfo { get => MochaVM.ImageInfo; set => MochaVM.ImageInfo = value; }
        public string MochaPrompt { get => MochaVM.Prompt; set => MochaVM.Prompt = value; }
        public int MochaTotalFrames => MochaVM.TotalFrames;
        public int MochaTotalChunks => MochaVM.TotalChunks;
        public bool IsProcessingMocha { get => MochaVM.IsProcessing; set => MochaVM.IsProcessing = value; }
        public string MochaProcessingStatus => MochaVM.ProcessingStatus;
        public double MochaProcessingProgress => MochaVM.ProcessingProgress;
        public string MochaLogOutput => MochaVM.LogOutput;
        public bool HasMochaResult => MochaVM.HasResult;
        public string MochaResultPath => MochaVM.ResultVideoPath;
        public string MochaVideoInfo => MochaVM.ResultVideoInfo;
        public bool CanGenerateMochaVideo => MochaVM.CanGenerateVideo;

        public bool IsMochaAnalyzing => MochaVM.IsAnalyzing;
        public bool CanAnalyzeMocha => MochaVM.CanAnalyzeImage;
        public string MochaResultVideoInfo => MochaVM.ResultVideoInfo;

        // MochaVM Commands
        public ICommand SelectMochaVideoCommand => MochaVM.SelectVideoCommand;
        public ICommand SelectMochaImageCommand => MochaVM.SelectImageCommand;
        public ICommand GenerateMochaVideoCommand => MochaVM.GenerateVideoCommand;
        public ICommand PlayMochaVideoCommand => MochaVM.PlayVideoCommand;
        public ICommand OpenMochaResultFolderCommand => MochaVM.OpenResultFolderCommand;
        public ICommand SendMochaToEditCameraCommand => MochaVM.SendToEditCameraCommand;
        public ICommand AnalyzeMochaCommand => MochaVM.AnalyzeImageCommand;

        #endregion

        #region InfiniteTalkVM Backward Compatibility Properties

        public string InfiniteTalkImagePath { get => InfiniteTalkVM.ImagePath; set => InfiniteTalkVM.ImagePath = value; }
        public BitmapImage? InfiniteTalkImagePreview { get => InfiniteTalkVM.ImagePreview; set => InfiniteTalkVM.ImagePreview = value; }
        public string InfiniteTalkImageInfo { get => InfiniteTalkVM.ImageInfo; set => InfiniteTalkVM.ImageInfo = value; }
        public string InfiniteTalkAudioPath { get => InfiniteTalkVM.AudioPath; set => InfiniteTalkVM.AudioPath = value; }
        public string InfiniteTalkAudioInfo { get => InfiniteTalkVM.AudioInfo; set => InfiniteTalkVM.AudioInfo = value; }
        public string InfiniteTalkPrompt { get => InfiniteTalkVM.Prompt; set => InfiniteTalkVM.Prompt = value; }
        public int InfiniteTalkWidth { get => InfiniteTalkVM.Width; set => InfiniteTalkVM.Width = value; }
        public int InfiniteTalkHeight { get => InfiniteTalkVM.Height; set => InfiniteTalkVM.Height = value; }
        public bool IsProcessingInfiniteTalk { get => InfiniteTalkVM.IsProcessing; set => InfiniteTalkVM.IsProcessing = value; }
        public string InfiniteTalkProcessingStatus => InfiniteTalkVM.ProcessingStatus;
        public double InfiniteTalkProcessingProgress => InfiniteTalkVM.ProcessingProgress;
        public string InfiniteTalkLogOutput => InfiniteTalkVM.LogOutput;
        public bool HasInfiniteTalkResult => InfiniteTalkVM.HasResult;
        public string InfiniteTalkResultPath => InfiniteTalkVM.ResultVideoPath;
        public string InfiniteTalkVideoInfo => InfiniteTalkVM.ResultVideoInfo;
        public double InfiniteTalkAudioDuration => InfiniteTalkVM.AudioDuration;
        public int InfiniteTalkTotalFrames => InfiniteTalkVM.TotalFrames;
        public int InfiniteTalkTotalChunks => InfiniteTalkVM.TotalChunks;
        public bool CanGenerateInfiniteTalkVideo => InfiniteTalkVM.CanGenerateVideo;
        public string InfiniteTalkEstimatedDuration => InfiniteTalkVM.EstimatedDuration;
        public string InfiniteTalkProgressPercentage => InfiniteTalkVM.ProgressPercentage;

        // InfiniteTalkVM Commands
        public ICommand SelectInfiniteTalkImageCommand => InfiniteTalkVM.SelectImageCommand;
        public ICommand SelectInfiniteTalkAudioCommand => InfiniteTalkVM.SelectAudioCommand;
        public ICommand GenerateInfiniteTalkVideoCommand => InfiniteTalkVM.GenerateVideoCommand;
        public ICommand PlayInfiniteTalkVideoCommand => InfiniteTalkVM.PlayVideoCommand;
        public ICommand OpenInfiniteTalkResultFolderCommand => InfiniteTalkVM.OpenResultFolderCommand;
        public ICommand SendInfiniteTalkToEditCameraCommand => InfiniteTalkVM.SendToEditCameraCommand;

        // LMStudio AI analysis properties
        public bool InfiniteTalkIsAnalyzing   => InfiniteTalkVM.IsAnalyzing;
        public bool CanInfiniteTalkAnalyzeImage    => InfiniteTalkVM.CanAnalyzeImage;
        public bool CanInfiniteTalkEnhancePrompt   => InfiniteTalkVM.CanEnhancePrompt;
        public bool ShowInfiniteTalkVideoPrompt => InfiniteTalkVM.ShowVideoPrompt;
        public string InfiniteTalkAnalysisResult => InfiniteTalkVM.AnalysisResult;
        public bool HasInfiniteTalkAnalysis => InfiniteTalkVM.HasAnalysis;

        // LMStudio AI analysis commands
        public ICommand AnalyzeInfiniteTalkImageCommand  => InfiniteTalkVM.AnalyzeImageCommand;
        public ICommand EnhanceInfiniteTalkPromptCommand => InfiniteTalkVM.EnhancePromptCommand;

        #endregion

        #region LTX23T2VVM Backward Compatibility Properties

        public string T2VPrompt { get => LTX23T2VVM.Prompt; set => LTX23T2VVM.Prompt = value; }
        public int T2VLength { get => LTX23T2VVM.Length; set => LTX23T2VVM.Length = value; }
        public int T2VWidth { get => LTX23T2VVM.Width; set => LTX23T2VVM.Width = value; }
        public int T2VHeight { get => LTX23T2VVM.Height; set => LTX23T2VVM.Height = value; }
        public long T2VSeed { get => LTX23T2VVM.Seed; set => LTX23T2VVM.Seed = value; }
        public bool T2VCanAddToQueue => LTX23T2VVM.CanAddToQueue;
        public bool IsProcessingT2V { get => LTX23T2VVM.IsProcessing; set => LTX23T2VVM.IsProcessing = value; }
        public string T2VProcessingStatus => LTX23T2VVM.ProcessingStatus;
        public double T2VProcessingProgress => LTX23T2VVM.ProcessingProgress;
        public string T2VProgressPercentage => LTX23T2VVM.ProgressPercentage;
        public string T2VLogOutput => LTX23T2VVM.LogOutput;
        public bool HasT2VResult => LTX23T2VVM.HasResult;
        public string T2VResultPath => LTX23T2VVM.ResultVideoPath;
        public string T2VVideoInfo => LTX23T2VVM.ResultVideoInfo;
        public ObservableCollection<QueueItem> T2VQueue => LTX23T2VVM.Queue;
        public bool T2VHasQueueItems => LTX23T2VVM.HasQueueItems;
        public bool T2VIsProcessingQueue => LTX23T2VVM.IsProcessingQueue;
        public string T2VQueueStatus => LTX23T2VVM.QueueStatus;

        public ICommand T2VGenerateCommand => LTX23T2VVM.GenerateVideoCommand;
        public ICommand T2VRemoveQueueItemCommand => LTX23T2VVM.RemoveQueueItemCommand;
        public ICommand T2VPlayVideoCommand => LTX23T2VVM.PlayVideoCommand;
        public ICommand T2VOpenResultFolderCommand => LTX23T2VVM.OpenResultFolderCommand;

        #endregion

        #region LTX23BasicVM Backward Compatibility Properties

        public string LTX23ImagePath { get => LTX23BasicVM.ImagePath; set => LTX23BasicVM.ImagePath = value; }
        public BitmapImage? LTX23ImagePreview { get => LTX23BasicVM.ImagePreview; set => LTX23BasicVM.ImagePreview = value; }
        public string LTX23ImageInfo { get => LTX23BasicVM.ImageInfo; set => LTX23BasicVM.ImageInfo = value; }
        public string LTX23Prompt { get => LTX23BasicVM.Prompt; set => LTX23BasicVM.Prompt = value; }
        public bool IsProcessingLTX23 { get => LTX23BasicVM.IsProcessing; set => LTX23BasicVM.IsProcessing = value; }
        public string LTX23ProcessingStatus => LTX23BasicVM.ProcessingStatus;
        public double LTX23ProcessingProgress => LTX23BasicVM.ProcessingProgress;
        public string LTX23ProgressPercentage => LTX23BasicVM.ProgressPercentage;
        public string LTX23LogOutput => LTX23BasicVM.LogOutput;
        public bool HasLTX23Result => LTX23BasicVM.HasResult;
        public string LTX23ResultPath => LTX23BasicVM.ResultVideoPath;
        public string LTX23VideoInfo => LTX23BasicVM.ResultVideoInfo;
        public bool CanGenerateLTX23Video => LTX23BasicVM.CanAddToQueue;
        public bool CanLTX23AddToQueue => LTX23BasicVM.CanAddToQueue;

        // LMStudio AI properties
        public bool LTX23IsAnalyzing => LTX23BasicVM.IsAnalyzing;
        public bool CanLTX23AnalyzeImage => LTX23BasicVM.CanAnalyzeImage;
        public bool CanLTX23EnhancePrompt => LTX23BasicVM.CanEnhancePrompt;
        public bool ShowLTX23VideoPrompt => LTX23BasicVM.ShowVideoPrompt;
        public string LTX23AnalysisResult => LTX23BasicVM.AnalysisResult;
        public bool HasLTX23Analysis => LTX23BasicVM.HasAnalysis;

        // Queue
        public ObservableCollection<QueueItem> LTX23Queue => LTX23BasicVM.Queue;
        public bool LTX23HasQueueItems => LTX23BasicVM.HasQueueItems;
        public bool LTX23IsProcessingQueue => LTX23BasicVM.IsProcessingQueue;
        public string LTX23QueueStatus => LTX23BasicVM.QueueStatus;

        // LTX23BasicVM Commands
        public ICommand SelectLTX23ImageCommand => LTX23BasicVM.SelectImageCommand;
        public ICommand AnalyzeLTX23ImageCommand => LTX23BasicVM.AnalyzeImageCommand;
        public ICommand EnhanceLTX23PromptCommand => LTX23BasicVM.EnhancePromptCommand;
        public ICommand GenerateLTX23VideoCommand => LTX23BasicVM.GenerateVideoCommand;
        public ICommand RemoveLTX23QueueItemCommand => LTX23BasicVM.RemoveQueueItemCommand;
        public ICommand PlayLTX23VideoCommand => LTX23BasicVM.PlayVideoCommand;
        public ICommand OpenLTX23ResultFolderCommand => LTX23BasicVM.OpenResultFolderCommand;
        public ICommand SendLTX23ToEditCameraCommand => LTX23BasicVM.SendToEditCameraCommand;

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the image path from external sources (e.g., edit camera).
        /// </summary>
        public void SetImagePath(string imagePath)
        {
            MainVM.SetImagePath(imagePath);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                // Unsubscribe from events
                MainVM.PropertyChanged -= ForwardPropertyChanged;
                VaceVM.PropertyChanged -= ForwardPropertyChanged;
                LTX2AudioVM.PropertyChanged -= ForwardPropertyChanged;
                MochaVM.PropertyChanged -= ForwardPropertyChanged;
                InfiniteTalkVM.PropertyChanged -= ForwardPropertyChanged;
                LTX23BasicVM.PropertyChanged -= ForwardPropertyChanged;
                LTX23T2VVM.PropertyChanged -= ForwardPropertyChanged;

                // Dispose all sub-ViewModels
                (MainVM as IDisposable)?.Dispose();
                (VaceVM as IDisposable)?.Dispose();
                (LTX2AudioVM as IDisposable)?.Dispose();
                (MochaVM as IDisposable)?.Dispose();
                (InfiniteTalkVM as IDisposable)?.Dispose();
                (LTX23BasicVM as IDisposable)?.Dispose();
                (LTX23T2VVM as IDisposable)?.Dispose();

                _disposed = true;
            }
        }

        #endregion

    }
}
