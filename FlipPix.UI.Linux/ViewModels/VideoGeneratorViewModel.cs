using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.Linux.ViewModels
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
        /// WanAnimate ViewModel - handles reference image, face image, and input video
        /// for the Wan Animate + Steady Dancer + OneToAll Animation + SCAIL workflow,
        /// processed in 81-frame chunks.
        /// </summary>
        public WanAnimateViewModel WanAnimateVM { get; }

        /// <summary>
        /// WAN SCAIL ViewModel - handles character image + reference video for the
        /// SCAIL Multi-Character Motion Transfer workflow, processed in 121-frame chunks.
        /// </summary>
        public WanScailViewModel WanScailVM { get; }
        public WanScailGgufViewModel WanScailGgufVM { get; }

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

            WanAnimateVM = new WanAnimateViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            WanScailVM = new WanScailViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            WanScailGgufVM = new WanScailGgufViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            // Forward PlayRequested events from sub-VMs
            MainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            VaceVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            LTX2AudioVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MochaVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            InfiniteTalkVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            WanAnimateVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            WanScailVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            WanScailGgufVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);

            // Forward PropertyChanged events from all sub-VMs for backward compatibility
            MainVM.PropertyChanged += ForwardPropertyChanged;
            VaceVM.PropertyChanged += ForwardPropertyChanged;
            LTX2AudioVM.PropertyChanged += ForwardPropertyChanged;
            MochaVM.PropertyChanged += ForwardPropertyChanged;
            InfiniteTalkVM.PropertyChanged += ForwardPropertyChanged;
            WanAnimateVM.PropertyChanged += ForwardPropertyChanged;
            WanScailVM.PropertyChanged += ForwardPropertyChanged;
            WanScailGgufVM.PropertyChanged += ForwardPropertyChanged;

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
        public ICommand ClearQueueCommand => MainVM.ClearQueueCommand;
        public ICommand StopQueueCommand => MainVM.StopQueueCommand;
        public ICommand ReprocessItemCommand => MainVM.ReprocessItemCommand;
        public ICommand ReprocessAllFailedCommand => MainVM.ReprocessAllFailedCommand;
        public ICommand PauseQueueCommand => MainVM.PauseQueueCommand;
        public ICommand ResumeQueueCommand => MainVM.ResumeQueueCommand;
        public ICommand SelectStoryPromptJsonCommand => MainVM.SelectStoryPromptJsonCommand;
        public ICommand SelectStoryImagesFolderCommand => MainVM.SelectStoryImagesFolderCommand;
        public ICommand LoadStoryQueueCommand => MainVM.LoadStoryQueueCommand;
        public ICommand ProcessStoryQueueCommand => MainVM.ProcessStoryQueueCommand;
        public ICommand ClearStoryQueueCommand => MainVM.ClearStoryQueueCommand;
        public ICommand StopStoryQueueCommand => MainVM.StopStoryQueueCommand;
        public ICommand ReprocessAllStoryFailedCommand => MainVM.ReprocessAllStoryFailedCommand;
        public bool HasStoryFailedItems => MainVM.HasStoryFailedItems;
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
        public ICommand ClearVaceQueueCommand => VaceVM.ClearQueueCommand;
        public ICommand StopVaceQueueCommand => VaceVM.StopQueueCommand;
        public ICommand ReprocessAllVaceFailedCommand => VaceVM.ReprocessAllFailedCommand;
        public bool VaceHasFailedItems => VaceVM.HasFailedItems;
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

        #region WanAnimateVM Backward Compatibility Properties

        public string WanAnimateRefImagePath { get => WanAnimateVM.ReferenceImagePath; set => WanAnimateVM.ReferenceImagePath = value; }
        public BitmapImage? WanAnimateRefImagePreview { get => WanAnimateVM.ReferenceImagePreview; set => WanAnimateVM.ReferenceImagePreview = value; }
        public string WanAnimateRefImageInfo { get => WanAnimateVM.ReferenceImageInfo; set => WanAnimateVM.ReferenceImageInfo = value; }
        public bool WanAnimateHasRefImage => WanAnimateVM.HasReferenceImage;

        public string WanAnimateFaceImagePath { get => WanAnimateVM.FaceImagePath; set => WanAnimateVM.FaceImagePath = value; }
        public BitmapImage? WanAnimateFaceImagePreview { get => WanAnimateVM.FaceImagePreview; set => WanAnimateVM.FaceImagePreview = value; }
        public string WanAnimateFaceImageInfo { get => WanAnimateVM.FaceImageInfo; set => WanAnimateVM.FaceImageInfo = value; }
        public bool WanAnimateHasFaceImage => WanAnimateVM.HasFaceImage;

        public string WanAnimateVideoPath { get => WanAnimateVM.InputVideoPath; set => WanAnimateVM.InputVideoPath = value; }
        public string WanAnimateVideoInfo { get => WanAnimateVM.InputVideoInfo; set => WanAnimateVM.InputVideoInfo = value; }
        public bool WanAnimateHasVideo => WanAnimateVM.HasInputVideo;

        public string WanAnimatePrompt { get => WanAnimateVM.Prompt; set => WanAnimateVM.Prompt = value; }
        public string WanAnimateNegativePrompt { get => WanAnimateVM.NegativePrompt; set => WanAnimateVM.NegativePrompt = value; }
        public int WanAnimateOutputWidth { get => WanAnimateVM.OutputWidth; set => WanAnimateVM.OutputWidth = value; }
        public int WanAnimateOutputHeight { get => WanAnimateVM.OutputHeight; set => WanAnimateVM.OutputHeight = value; }

        public int WanAnimateTotalFrames => WanAnimateVM.TotalFrames;
        public int WanAnimateTotalChunks => WanAnimateVM.TotalChunks;

        public bool IsProcessingWanAnimate { get => WanAnimateVM.IsProcessing; set => WanAnimateVM.IsProcessing = value; }
        public string WanAnimateProcessingStatus => WanAnimateVM.ProcessingStatus;
        public double WanAnimateProcessingProgress => WanAnimateVM.ProcessingProgress;
        public string WanAnimateProgressPercentage => WanAnimateVM.ProgressPercentage;
        public string WanAnimateLogOutput => WanAnimateVM.LogOutput;

        public bool HasWanAnimateResult => WanAnimateVM.HasResult;
        public string WanAnimateResultPath => WanAnimateVM.ResultVideoPath;
        public string WanAnimateResultVideoInfo => WanAnimateVM.ResultVideoInfo;

        public bool WanAnimateCanAddToQueue => WanAnimateVM.CanAddToQueue;
        public bool WanAnimateIsAnalyzing => WanAnimateVM.IsAnalyzing;

        public ObservableCollection<WanAnimateQueueItem> WanAnimateQueue => WanAnimateVM.Queue;
        public bool WanAnimateHasQueueItems => WanAnimateVM.HasQueueItems;
        public bool WanAnimateIsProcessingQueue => WanAnimateVM.IsProcessingQueue;
        public string WanAnimateQueueStatus => WanAnimateVM.QueueStatus;
        public bool WanAnimateHasFailedItems => WanAnimateVM.HasFailedItems;

        public ICommand SelectWanAnimateRefImageCommand => WanAnimateVM.SelectReferenceImageCommand;
        public ICommand SelectWanAnimateFaceImageCommand => WanAnimateVM.SelectFaceImageCommand;
        public ICommand SelectWanAnimateVideoCommand => WanAnimateVM.SelectVideoCommand;
        public ICommand GenerateWanAnimateVideoCommand => WanAnimateVM.GenerateVideoCommand;
        public ICommand RemoveWanAnimateQueueItemCommand => WanAnimateVM.RemoveQueueItemCommand;
        public ICommand ClearWanAnimateQueueCommand => WanAnimateVM.ClearQueueCommand;
        public ICommand StopWanAnimateQueueCommand => WanAnimateVM.StopQueueCommand;
        public ICommand ReprocessAllWanAnimateFailedCommand => WanAnimateVM.ReprocessAllFailedCommand;
        public ICommand PlayWanAnimateVideoCommand => WanAnimateVM.PlayVideoCommand;
        public ICommand OpenWanAnimateResultFolderCommand => WanAnimateVM.OpenResultFolderCommand;
        public ICommand SendWanAnimateToEditCameraCommand => WanAnimateVM.SendToEditCameraCommand;
        public ICommand AnalyzeWanAnimateImageCommand => WanAnimateVM.AnalyzeImageCommand;

        #endregion

        #region WanScailVM Backward Compatibility Properties

        public string WanScailCharacterImagePath { get => WanScailVM.CharacterImagePath; set => WanScailVM.CharacterImagePath = value; }
        public BitmapImage? WanScailCharacterImagePreview { get => WanScailVM.CharacterImagePreview; set => WanScailVM.CharacterImagePreview = value; }
        public string WanScailCharacterImageInfo { get => WanScailVM.CharacterImageInfo; set => WanScailVM.CharacterImageInfo = value; }
        public bool WanScailHasCharacterImage => WanScailVM.HasCharacterImage;

        public string WanScailVideoPath { get => WanScailVM.InputVideoPath; set => WanScailVM.InputVideoPath = value; }
        public string WanScailVideoInfo { get => WanScailVM.InputVideoInfo; set => WanScailVM.InputVideoInfo = value; }
        public bool WanScailHasVideo => WanScailVM.HasInputVideo;

        public string WanScailPrompt { get => WanScailVM.Prompt; set => WanScailVM.Prompt = value; }
        public string WanScailNegativePrompt { get => WanScailVM.NegativePrompt; set => WanScailVM.NegativePrompt = value; }
        public int WanScailFps { get => WanScailVM.Fps; set => WanScailVM.Fps = value; }
        public int WanScailMaxEdge { get => WanScailVM.MaxEdge; set => WanScailVM.MaxEdge = value; }
        public long WanScailSeed { get => WanScailVM.Seed; set => WanScailVM.Seed = value; }

        public int WanScailTotalFrames => WanScailVM.TotalFrames;
        public int WanScailTotalChunks => WanScailVM.TotalChunks;

        public bool IsProcessingWanScail { get => WanScailVM.IsProcessing; set => WanScailVM.IsProcessing = value; }
        public string WanScailProcessingStatus => WanScailVM.ProcessingStatus;
        public double WanScailProcessingProgress => WanScailVM.ProcessingProgress;
        public string WanScailProgressPercentage => WanScailVM.ProgressPercentage;
        public string WanScailLogOutput => WanScailVM.LogOutput;

        public bool HasWanScailResult => WanScailVM.HasResult;
        public string WanScailResultPath => WanScailVM.ResultVideoPath;
        public string WanScailResultVideoInfo => WanScailVM.ResultVideoInfo;

        public bool WanScailCanAddToQueue => WanScailVM.CanAddToQueue;
        public bool WanScailIsAnalyzing => WanScailVM.IsAnalyzing;

        public ObservableCollection<WanScailQueueItem> WanScailQueue => WanScailVM.Queue;
        public bool WanScailHasQueueItems => WanScailVM.HasQueueItems;
        public bool WanScailIsProcessingQueue => WanScailVM.IsProcessingQueue;
        public string WanScailQueueStatus => WanScailVM.QueueStatus;
        public bool WanScailHasFailedItems => WanScailVM.HasFailedItems;

        public ICommand SelectWanScailCharacterImageCommand => WanScailVM.SelectCharacterImageCommand;
        public ICommand SelectWanScailVideoCommand => WanScailVM.SelectVideoCommand;
        public ICommand GenerateWanScailVideoCommand => WanScailVM.GenerateVideoCommand;
        public ICommand ProcessSelectedWanScailChunkCommand => WanScailVM.ProcessSelectedChunkCommand;
        public ICommand SelectWanScailChunkCommand => WanScailVM.SelectChunkCommand;
        public ICommand RemoveWanScailQueueItemCommand => WanScailVM.RemoveQueueItemCommand;
        public ICommand ClearWanScailQueueCommand => WanScailVM.ClearQueueCommand;
        public ICommand StopWanScailQueueCommand => WanScailVM.StopQueueCommand;
        public ICommand ReprocessAllWanScailFailedCommand => WanScailVM.ReprocessAllFailedCommand;
        public ICommand PlayWanScailVideoCommand => WanScailVM.PlayVideoCommand;
        public ICommand OpenWanScailResultFolderCommand => WanScailVM.OpenResultFolderCommand;
        public ICommand SendWanScailToEditCameraCommand => WanScailVM.SendToEditCameraCommand;
        public ICommand AnalyzeWanScailImageCommand => WanScailVM.AnalyzeImageCommand;
        public ICommand RandomWanScailSeedCommand => WanScailVM.RandomSeedCommand;

        // Video editor
        public string? WanScailVideoFileUri => WanScailVM.VideoFileUri;
        public bool WanScailHasVideoInfo => WanScailVM.HasVideoInfo;
        public string WanScailVideoDuration => WanScailVM.VideoDuration;
        public string WanScailVideoFpsDisplay => WanScailVM.VideoFpsDisplay;
        public string WanScailVideoFrameCountDisplay => WanScailVM.VideoFrameCountDisplay;
        public string WanScailVideoChunksDisplay => WanScailVM.VideoChunksDisplay;
        public string WanScailChunkSelectionInfo => WanScailVM.ChunkSelectionInfo;
        public ObservableCollection<WanScailChunkItem> WanScailChunkItems => WanScailVM.ChunkItems;

        #endregion

        #region WanScailGgufVM Backward Compatibility Properties

        public string WanScailGgufCharacterImagePath { get => WanScailGgufVM.CharacterImagePath; set => WanScailGgufVM.CharacterImagePath = value; }
        public System.Windows.Media.Imaging.BitmapImage? WanScailGgufCharacterImagePreview { get => WanScailGgufVM.CharacterImagePreview; set => WanScailGgufVM.CharacterImagePreview = value; }
        public string WanScailGgufCharacterImageInfo { get => WanScailGgufVM.CharacterImageInfo; set => WanScailGgufVM.CharacterImageInfo = value; }
        public bool WanScailGgufHasCharacterImage => WanScailGgufVM.HasCharacterImage;

        public string WanScailGgufVideoPath { get => WanScailGgufVM.InputVideoPath; set => WanScailGgufVM.InputVideoPath = value; }
        public string WanScailGgufVideoInfo { get => WanScailGgufVM.InputVideoInfo; set => WanScailGgufVM.InputVideoInfo = value; }
        public bool WanScailGgufHasVideo => WanScailGgufVM.HasInputVideo;

        public string WanScailGgufPrompt { get => WanScailGgufVM.Prompt; set => WanScailGgufVM.Prompt = value; }
        public string WanScailGgufNegativePrompt { get => WanScailGgufVM.NegativePrompt; set => WanScailGgufVM.NegativePrompt = value; }
        public int WanScailGgufFps { get => WanScailGgufVM.Fps; set => WanScailGgufVM.Fps = value; }
        public int WanScailGgufMaxEdge { get => WanScailGgufVM.MaxEdge; set => WanScailGgufVM.MaxEdge = value; }
        public long WanScailGgufSeed { get => WanScailGgufVM.Seed; set => WanScailGgufVM.Seed = value; }

        public int WanScailGgufTotalFrames => WanScailGgufVM.TotalFrames;
        public int WanScailGgufTotalChunks => WanScailGgufVM.TotalChunks;

        public bool IsProcessingWanScailGguf { get => WanScailGgufVM.IsProcessing; set => WanScailGgufVM.IsProcessing = value; }
        public string WanScailGgufProcessingStatus => WanScailGgufVM.ProcessingStatus;
        public double WanScailGgufProcessingProgress => WanScailGgufVM.ProcessingProgress;
        public string WanScailGgufProgressPercentage => WanScailGgufVM.ProgressPercentage;
        public string WanScailGgufLogOutput => WanScailGgufVM.LogOutput;

        public bool HasWanScailGgufResult => WanScailGgufVM.HasResult;
        public string WanScailGgufResultPath => WanScailGgufVM.ResultVideoPath;
        public string WanScailGgufResultVideoInfo => WanScailGgufVM.ResultVideoInfo;

        public bool WanScailGgufCanAddToQueue => WanScailGgufVM.CanAddToQueue;
        public bool WanScailGgufIsAnalyzing => WanScailGgufVM.IsAnalyzing;

        public System.Collections.ObjectModel.ObservableCollection<Models.WanScailQueueItem> WanScailGgufQueue => WanScailGgufVM.Queue;
        public bool WanScailGgufHasQueueItems => WanScailGgufVM.HasQueueItems;
        public bool WanScailGgufIsProcessingQueue => WanScailGgufVM.IsProcessingQueue;
        public string WanScailGgufQueueStatus => WanScailGgufVM.QueueStatus;
        public bool WanScailGgufHasFailedItems => WanScailGgufVM.HasFailedItems;

        public System.Windows.Input.ICommand SelectWanScailGgufCharacterImageCommand => WanScailGgufVM.SelectCharacterImageCommand;
        public System.Windows.Input.ICommand SelectWanScailGgufVideoCommand => WanScailGgufVM.SelectVideoCommand;
        public System.Windows.Input.ICommand GenerateWanScailGgufVideoCommand => WanScailGgufVM.GenerateVideoCommand;
        public System.Windows.Input.ICommand RemoveWanScailGgufQueueItemCommand => WanScailGgufVM.RemoveQueueItemCommand;
        public System.Windows.Input.ICommand ClearWanScailGgufQueueCommand => WanScailGgufVM.ClearQueueCommand;
        public System.Windows.Input.ICommand StopWanScailGgufQueueCommand => WanScailGgufVM.StopQueueCommand;
        public System.Windows.Input.ICommand ReprocessAllWanScailGgufFailedCommand => WanScailGgufVM.ReprocessAllFailedCommand;
        public System.Windows.Input.ICommand PlayWanScailGgufVideoCommand => WanScailGgufVM.PlayVideoCommand;
        public System.Windows.Input.ICommand OpenWanScailGgufResultFolderCommand => WanScailGgufVM.OpenResultFolderCommand;
        public System.Windows.Input.ICommand SendWanScailGgufToEditCameraCommand => WanScailGgufVM.SendToEditCameraCommand;
        public System.Windows.Input.ICommand AnalyzeWanScailGgufImageCommand => WanScailGgufVM.AnalyzeImageCommand;
        public System.Windows.Input.ICommand RandomWanScailGgufSeedCommand => WanScailGgufVM.RandomSeedCommand;

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
                WanAnimateVM.PropertyChanged -= ForwardPropertyChanged;
                WanScailVM.PropertyChanged -= ForwardPropertyChanged;

                // Dispose all sub-ViewModels
                (MainVM as IDisposable)?.Dispose();
                (VaceVM as IDisposable)?.Dispose();
                (LTX2AudioVM as IDisposable)?.Dispose();
                (MochaVM as IDisposable)?.Dispose();
                (InfiniteTalkVM as IDisposable)?.Dispose();
                (WanAnimateVM as IDisposable)?.Dispose();
                (WanScailVM as IDisposable)?.Dispose();

                _disposed = true;
            }
        }

        #endregion

    }
}
