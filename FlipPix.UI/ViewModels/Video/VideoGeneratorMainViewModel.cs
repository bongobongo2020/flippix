using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Main ViewModel for core i2v video generation functionality.
    /// Handles first/last frame selection, video settings, image analysis,
    /// prompt queue, story video queue, and workflow selection.
    /// </summary>
    public partial class VideoGeneratorMainViewModel : VideoProcessingBaseViewModel
    {
        private readonly LMStudioService _lmStudioService;
        private readonly IFileDialogService _fileDialogService;

        // Image properties
        private string _imageFilePath = string.Empty;
        private BitmapImage? _imagePreviewSource;
        private string _imageInfo = string.Empty;

        // First frame properties
        private string _firstFrameImagePath = string.Empty;
        private BitmapImage? _firstFrameImagePreview;
        private string _firstFrameImageInfo = string.Empty;

        // Last frame properties
        private string _lastFrameImagePath = string.Empty;
        private BitmapImage? _lastFrameImagePreview;
        private string _lastFrameImageInfo = string.Empty;

        // Prompt properties
        private string _videoPrompt = string.Empty;
        private string _negativePrompt = string.Empty;

        // Video settings
        private int _videoLength = 240;
        private int _fps = 24;
        private int _steps = 4;
        private double _cfg = 1.0;
        private long _seed = 0;
        private int _width = 832;
        private int _height = 480;

        // Image analysis properties
        private bool _isAnalyzing = false;
        private string _analysisStatus = string.Empty;
        private double _analysisProgress = 0;
        private string _imageAnalysis = string.Empty;
        private CancellationTokenSource? _analysisCancellationTokenSource;

        // Queue properties
        private string _newQueuePrompt = string.Empty;
        private bool _isProcessingQueue = false;
        private string _queueStatus = "Queue is empty";
        private readonly ObservableCollection<QueueItem> _promptQueue = new();
        private bool _isQueuePaused = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private CancellationTokenSource? _promptQueueCts;

        // Story Video Generator properties
        private string _storyPromptJsonPath = string.Empty;
        private string _storyImagesFolderPath = string.Empty;
        private bool _isProcessingStoryQueue = false;
        private StoryVideoQueueItem? _currentStoryQueueItem;
        private int _storyQueueProgress = 0;
        private int _storyQueueTotal = 0;
        private string _storyQueueStatus = "No images loaded";
        private readonly ObservableCollection<StoryVideoQueueItem> _storyVideoQueue = new();
        private bool _isStoryQueuePaused = false;
        private readonly ManualResetEventSlim _storyPauseEvent = new(true);
        private CancellationTokenSource? _storyQueueCts;

        // Workflow selection
        private string _selectedWorkflow = "ltx2_i2v";
        private bool _useLTXWorkflow = true;
        private SingleVideoWorkflow _selectedSingleWorkflow = SingleVideoWorkflow.LTX2V;
        private StoryVideoWorkflow _selectedStoryWorkflow = StoryVideoWorkflow.VantageSulphur2;
        private bool _isStoryVideoMode = false;
        private string _painterHighNoiseModel = @"wan\wan2.2_i2v_high_noise_14B_Q8_0.gguf";
        private string _painterLowNoiseModel = @"wan\wan2.2_i2v_low_noise_14B_Q8_0.gguf";

        /// <summary>
        /// Workflow options for single video generation (Tab 1).
        /// Story Video (Tab 2) uses SelectedStoryWorkflow separately.
        /// </summary>
        public enum SingleVideoWorkflow
        {
            LTX2V,
            Wan22
        }

        /// <summary>
        /// LTX workflow options for the Story Video Generator (Tab 2).
        /// </summary>
        public enum StoryVideoWorkflow
        {
            VantageSulphur2,
            Eros10S,
            LTX22B,
            Painter,
            PainterEnhanced
        }

        // UI state
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private string _videoInfo = string.Empty;

        public VideoGeneratorMainViewModel(
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

            // Load default prompts from settings
            _videoPrompt = settingsService.Settings.DefaultVideoPrompt;
            _negativePrompt = settingsService.Settings.DefaultNegativePrompt;

            // Load Painter model names from settings
            if (!string.IsNullOrEmpty(settingsService.Settings.PainterHighNoiseModel))
                _painterHighNoiseModel = settingsService.Settings.PainterHighNoiseModel;
            if (!string.IsNullOrEmpty(settingsService.Settings.PainterLowNoiseModel))
                _painterLowNoiseModel = settingsService.Settings.PainterLowNoiseModel;

            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            SelectFirstFrameImageCommand = new RelayCommand(SelectFirstFrameImage);
            SelectLastFrameImageCommand = new RelayCommand(SelectLastFrameImage);
            GenerateVideoCommand = new RelayCommand(AddVideoGenerationToQueue, () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResult);

            // Image analysis commands
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync());
            AnalyzeFirstFrameImageCommand = new RelayCommand(async () => await AnalyzeFirstFrameImageAsync());
            SendAnalysisToQueueCommand = new RelayCommand(SendAnalysisToQueue, () => HasAnalysis);
            OpenLMStudioSettingsCommand = new RelayCommand(OpenLMStudioSettings);
            CopyAnalysisCommand = new RelayCommand(CopyAnalysis, () => HasAnalysis);

            // Queue commands
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<QueueItem>(RemoveFromQueue);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => PromptQueue.Any());
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessItemCommand = new RelayCommand<QueueItem>(async (item) => await ReprocessItemAsync(item));
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);

            // Story Video Generator commands
            SelectStoryPromptJsonCommand = new RelayCommand(SelectStoryPromptJson);
            SelectStoryImagesFolderCommand = new RelayCommand(SelectStoryImagesFolder);
            LoadStoryQueueCommand = new RelayCommand(async () => await LoadStoryQueueAsync(), () => CanLoadStoryQueue);
            ProcessStoryQueueCommand = new RelayCommand(async () => await ProcessStoryQueueAsync(), () => CanProcessStoryQueue);
            ClearStoryQueueCommand = new RelayCommand(ClearStoryQueue, () => StoryVideoQueue.Any());
            StopStoryQueueCommand = new RelayCommand(StopStoryQueue, () => IsProcessingStoryQueue);
            ReprocessAllStoryFailedCommand = new RelayCommand(async () => await ReprocessAllStoryFailedAsync(), () => HasStoryFailedItems);
            PauseStoryQueueCommand = new RelayCommand(PauseStoryQueue, () => IsProcessingStoryQueue && !IsStoryQueuePaused);
            ResumeStoryQueueCommand = new RelayCommand(ResumeStoryQueue, () => IsProcessingStoryQueue && IsStoryQueuePaused);
            RegenerateStoryItemCommand = new RelayCommand<StoryVideoQueueItem>(RegenerateStoryItem);
            DeleteStoryItemCommand = new RelayCommand<StoryVideoQueueItem>(DeleteStoryItem);
            JoinClipsCommand = new RelayCommand(async () => await JoinClipsAsync(), () => HasCompletedStoryItems && !IsJoiningClips);

            // Workflow toggle command
            ToggleWorkflowCommand = new RelayCommand(ToggleWorkflow);
            ToggleSingleWorkflowCommand = new RelayCommand(ToggleSingleWorkflow);

            // Subscribe to story video queue collection changes
            _storyVideoQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanProcessStoryQueue));
                OnPropertyChanged(nameof(HasCompletedStoryItems));
                NotifyCommandsCanExecuteChanged();
            };

            AddLog("Video Generator initialized");

            // Load ComfyUI settings
            var settings = _settingsService.LoadSettings();
            if (settings != null)
            {
                var uri = new Uri(settings.BaseUrl);
                ComfyUIServer = uri.Host;
                ComfyUIPort = uri.Port.ToString();

                // Load saved workflow selection
                var savedWorkflow = settings.SelectedVideoWorkflow ?? "ltx2_i2v";
                SelectedWorkflow = savedWorkflow;
                AddLog($"Loaded workflow selection from settings: {savedWorkflow}");
            }
            else
            {
                AddLog("No settings found, using default LTXV workflow");
            }

            AddLog($"Current workflow: {WorkflowDisplay} (UseLTXWorkflow={UseLTXWorkflow})");

            // Load saved queues from file (for crash recovery)
            LoadQueueFromFile();
            LoadStoryQueueFromFile();
        }

        #region Properties

        // Image properties
        public string ImageFilePath
        {
            get => _imageFilePath;
            set
            {
                if (_imageFilePath != value)
                {
                    _imageFilePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    OnPropertyChanged(nameof(HasImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanProcessQueue));
                    LoadImagePreview();
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public BitmapImage? ImagePreviewSource
        {
            get => _imagePreviewSource;
            set
            {
                _imagePreviewSource = value;
                OnPropertyChanged();
            }
        }

        public string ImageInfo
        {
            get => _imageInfo;
            set
            {
                _imageInfo = value;
                OnPropertyChanged();
            }
        }

        public bool HasImage => !string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath);

        // First Frame properties
        public string FirstFrameImagePath
        {
            get => _firstFrameImagePath;
            set
            {
                if (_firstFrameImagePath != value)
                {
                    _firstFrameImagePath = value;
                    OnPropertyChanged();
                    LoadFirstFrameImagePreview();
                    OnPropertyChanged(nameof(HasFirstFrameImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanProcessQueue));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public BitmapImage? FirstFrameImagePreview
        {
            get => _firstFrameImagePreview;
            set
            {
                if (_firstFrameImagePreview != value)
                {
                    _firstFrameImagePreview = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FirstFrameImageInfo
        {
            get => _firstFrameImageInfo;
            set
            {
                if (_firstFrameImageInfo != value)
                {
                    _firstFrameImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasFirstFrameImage => !string.IsNullOrEmpty(FirstFrameImagePath) && File.Exists(FirstFrameImagePath);

        // Last Frame properties
        public string LastFrameImagePath
        {
            get => _lastFrameImagePath;
            set
            {
                if (_lastFrameImagePath != value)
                {
                    _lastFrameImagePath = value;
                    OnPropertyChanged();
                    LoadLastFrameImagePreview();
                    OnPropertyChanged(nameof(HasLastFrameImage));
                    OnPropertyChanged(nameof(CanAddToQueue));
                    OnPropertyChanged(nameof(CanProcessQueue));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public BitmapImage? LastFrameImagePreview
        {
            get => _lastFrameImagePreview;
            set
            {
                if (_lastFrameImagePreview != value)
                {
                    _lastFrameImagePreview = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastFrameImageInfo
        {
            get => _lastFrameImageInfo;
            set
            {
                if (_lastFrameImageInfo != value)
                {
                    _lastFrameImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasLastFrameImage => !string.IsNullOrEmpty(LastFrameImagePath) && File.Exists(LastFrameImagePath);

        // Prompt properties
        public string VideoPrompt
        {
            get => _videoPrompt;
            set
            {
                _videoPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                NotifyCommandsCanExecuteChanged();
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set
            {
                _negativePrompt = value;
                OnPropertyChanged();
            }
        }

        // Video Settings
        public int VideoLength
        {
            get => _videoLength;
            set
            {
                _videoLength = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoLengthSeconds));
            }
        }

        public string VideoLengthSeconds => $"≈ {(double)VideoLength / Fps:F1} seconds at {Fps} FPS";

        public int Fps
        {
            get => _fps;
            set
            {
                _fps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoLengthSeconds));
            }
        }

        public int Steps
        {
            get => _steps;
            set
            {
                _steps = value;
                OnPropertyChanged();
            }
        }

        public double Cfg
        {
            get => _cfg;
            set
            {
                _cfg = value;
                OnPropertyChanged();
            }
        }

        public long Seed
        {
            get => _seed;
            set
            {
                _seed = value;
                OnPropertyChanged();
            }
        }

        public int Width
        {
            get => _width;
            set
            {
                _width = value;
                OnPropertyChanged();
            }
        }

        public int Height
        {
            get => _height;
            set
            {
                _height = value;
                OnPropertyChanged();
            }
        }

        public bool CanGenerateVideo => HasFirstFrameImage &&
                                        (!string.IsNullOrWhiteSpace(VideoPrompt) || !string.IsNullOrWhiteSpace(ImageAnalysis)) &&
                                        !IsProcessing && !IsProcessingQueue;

        // Image analysis properties
        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing != value)
                {
                    _isAnalyzing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public string AnalysisStatus
        {
            get => _analysisStatus;
            set
            {
                if (_analysisStatus != value)
                {
                    _analysisStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double AnalysisProgress
        {
            get => _analysisProgress;
            set
            {
                if (_analysisProgress != value)
                {
                    _analysisProgress = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ImageAnalysis
        {
            get => _imageAnalysis;
            set
            {
                if (_imageAnalysis != value)
                {
                    _imageAnalysis = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasAnalysis));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public bool HasAnalysis => !string.IsNullOrWhiteSpace(ImageAnalysis);

        // Queue properties
        public string NewQueuePrompt
        {
            get => _newQueuePrompt;
            set
            {
                if (_newQueuePrompt != value)
                {
                    _newQueuePrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanAddToQueue));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public ObservableCollection<QueueItem> PromptQueue => _promptQueue;

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                if (_isProcessingQueue != value)
                {
                    _isProcessingQueue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanProcessQueue));
                    OnPropertyChanged(nameof(CanGenerateVideo));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public bool IsQueuePaused
        {
            get => _isQueuePaused;
            set
            {
                if (_isQueuePaused != value)
                {
                    _isQueuePaused = value;
                    OnPropertyChanged();
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public bool CanProcessQueue => PromptQueue.Any(x => x.ItemStatus == QueueItemStatus.Pending) && !IsProcessingQueue && !IsProcessing;
        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(NewQueuePrompt) && (HasImage || (HasFirstFrameImage && HasLastFrameImage));
        public bool HasFailedItems => PromptQueue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        public string QueueStatus
        {
            get => _queueStatus;
            set
            {
                if (_queueStatus != value)
                {
                    _queueStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        // Story Video Generator Properties
        public string StoryPromptJsonPath
        {
            get => _storyPromptJsonPath;
            set
            {
                if (_storyPromptJsonPath != value)
                {
                    _storyPromptJsonPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadStoryQueue));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public string StoryImagesFolderPath
        {
            get => _storyImagesFolderPath;
            set
            {
                if (_storyImagesFolderPath != value)
                {
                    _storyImagesFolderPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadStoryQueue));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public ObservableCollection<StoryVideoQueueItem> StoryVideoQueue => _storyVideoQueue;

        public bool IsProcessingStoryQueue
        {
            get => _isProcessingStoryQueue;
            set
            {
                if (_isProcessingStoryQueue != value)
                {
                    _isProcessingStoryQueue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanProcessStoryQueue));
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public bool IsStoryQueuePaused
        {
            get => _isStoryQueuePaused;
            set
            {
                if (_isStoryQueuePaused != value)
                {
                    _isStoryQueuePaused = value;
                    OnPropertyChanged();
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        public StoryVideoQueueItem? CurrentStoryQueueItem
        {
            get => _currentStoryQueueItem;
            set
            {
                if (_currentStoryQueueItem != value)
                {
                    _currentStoryQueueItem = value;
                    OnPropertyChanged();
                }
            }
        }

        public int StoryQueueProgress
        {
            get => _storyQueueProgress;
            set
            {
                if (_storyQueueProgress != value)
                {
                    _storyQueueProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StoryQueueProgressText));
                }
            }
        }

        public int StoryQueueTotal
        {
            get => _storyQueueTotal;
            set
            {
                if (_storyQueueTotal != value)
                {
                    _storyQueueTotal = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StoryQueueProgressText));
                }
            }
        }

        public string StoryQueueProgressText => StoryQueueTotal > 0 ? $"{StoryQueueProgress}/{StoryQueueTotal}" : "0/0";

        public bool CanLoadStoryQueue => !string.IsNullOrEmpty(StoryPromptJsonPath) &&
                                        File.Exists(StoryPromptJsonPath) &&
                                        !string.IsNullOrEmpty(StoryImagesFolderPath) &&
                                        Directory.Exists(StoryImagesFolderPath) &&
                                        !IsProcessingStoryQueue;

        public bool CanProcessStoryQueue => StoryVideoQueue.Any(item => item.Status == "Pending") &&
                                          !IsProcessingStoryQueue;

        public string StoryQueueStatus
        {
            get => _storyQueueStatus;
            set
            {
                if (_storyQueueStatus != value)
                {
                    _storyQueueStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        // Workflow selection
        public string SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                if (_selectedWorkflow != value)
                {
                    _selectedWorkflow = value;
                    _useLTXWorkflow = value == "ltx2_i2v";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UseLTXWorkflow));
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    OnPropertyChanged(nameof(SelectedWorkflowIndex));
                    NotifyCommandsCanExecuteChanged();

                    // Save to settings
                    var settings = _settingsService.Settings;
                    if (settings != null)
                    {
                        settings.SelectedVideoWorkflow = value;
                        _settingsService.SaveSettings(settings);
                    }

                    AddLog($"Workflow changed to: {WorkflowDisplay}");
                }
            }
        }

        public bool UseLTXWorkflow
        {
            get => _useLTXWorkflow;
            set
            {
                if (_useLTXWorkflow != value)
                {
                    _useLTXWorkflow = value;
                    _selectedWorkflow = value ? "ltx2_i2v" : "painter";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    OnPropertyChanged(nameof(SelectedWorkflowIndex));
                    NotifyCommandsCanExecuteChanged();

                    // Save to settings
                    var settings = _settingsService.Settings;
                    if (settings != null)
                    {
                        settings.SelectedVideoWorkflow = _selectedWorkflow;
                        _settingsService.SaveSettings(settings);
                    }

                    AddLog($"Workflow changed to: {WorkflowDisplay}");
                }
            }
        }

        public string WorkflowDisplay => _isStoryVideoMode ? StoryWorkflowDisplay : (UseLTXWorkflow ? "LTXV (LTX-2_image2video_distilledAPI.json)" : "Painter (painteri2vAPI.json)");
        public string WorkflowIndicator => _isStoryVideoMode
            ? (_selectedStoryWorkflow switch
            {
                StoryVideoWorkflow.Eros10S => "10Eros",
                StoryVideoWorkflow.LTX22B => "LTX-22-B",
                StoryVideoWorkflow.Painter => "Painter",
                _ => "Vantage"
            })
            : (UseLTXWorkflow ? "LTXV" : "Painter");

        public string PainterHighNoiseModel
        {
            get => _painterHighNoiseModel;
            set
            {
                if (_painterHighNoiseModel != value)
                {
                    _painterHighNoiseModel = value;
                    OnPropertyChanged();
                    var settings = _settingsService.Settings;
                    if (settings != null) { settings.PainterHighNoiseModel = value; _settingsService.SaveSettings(settings); }
                }
            }
        }

        public string PainterLowNoiseModel
        {
            get => _painterLowNoiseModel;
            set
            {
                if (_painterLowNoiseModel != value)
                {
                    _painterLowNoiseModel = value;
                    OnPropertyChanged();
                    var settings = _settingsService.Settings;
                    if (settings != null) { settings.PainterLowNoiseModel = value; _settingsService.SaveSettings(settings); }
                }
            }
        }

        public int SelectedWorkflowIndex
        {
            get => UseLTXWorkflow ? 0 : 1;
            set => UseLTXWorkflow = value == 0;
        }

        // Story Video Generator Workflow (Tab 2)
        public StoryVideoWorkflow SelectedStoryWorkflow
        {
            get => _selectedStoryWorkflow;
            set
            {
                if (_selectedStoryWorkflow != value)
                {
                    _selectedStoryWorkflow = value;
                    _useLTXWorkflow = value != StoryVideoWorkflow.Painter && value != StoryVideoWorkflow.PainterEnhanced;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(UseLTXWorkflow));
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    OnPropertyChanged(nameof(SelectedStoryWorkflowIndex));
                    NotifyCommandsCanExecuteChanged();
                    AddLog($"Story workflow changed to: {StoryWorkflowDisplay}");
                }
            }
        }

        public int SelectedStoryWorkflowIndex
        {
            get => (int)_selectedStoryWorkflow;
            set => SelectedStoryWorkflow = (StoryVideoWorkflow)value;
        }

        public string StoryWorkflowDisplay => _selectedStoryWorkflow switch
        {
            StoryVideoWorkflow.Eros10S => "10Eros InstantAction (10Eros_10SNodes_InstantAction_I2VAPI.json)",
            StoryVideoWorkflow.LTX22B => "LTX-22-B (LTX-22-B.json)",
            StoryVideoWorkflow.Painter => "Painter (painteri2vAPI.json)",
            StoryVideoWorkflow.PainterEnhanced => "Painter Enhanced NSFW-HL (painteri2vAPI-enhancednsfw-HL.json)",
            _ => "Vantage Sulphur 2 (Vantage-Sulphur-2-WorkflowAPI.json)"
        };

        // Single Video Generator Workflow (Tab 1) - separate from Story Video (Tab 2)
        public SingleVideoWorkflow SelectedSingleWorkflow
        {
            get => _selectedSingleWorkflow;
            set
            {
                if (_selectedSingleWorkflow != value)
                {
                    _selectedSingleWorkflow = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SingleWorkflowDisplay));
                    OnPropertyChanged(nameof(UseLTX2V));
                    OnPropertyChanged(nameof(UseWan22));
                    NotifyCommandsCanExecuteChanged();

                    AddLog($"Single video workflow changed to: {SingleWorkflowDisplay}");
                }
            }
        }

        public string SingleWorkflowDisplay => SelectedSingleWorkflow == SingleVideoWorkflow.LTX2V
            ? "LTX2V (LTXV-DoEverything-v2.json)"
            : "Wan 2.2 (LF-t2v-i2v-FFLF-Main v1.1API.json)";

        public bool UseLTX2V => SelectedSingleWorkflow == SingleVideoWorkflow.LTX2V;
        public bool UseWan22 => SelectedSingleWorkflow == SingleVideoWorkflow.Wan22;

        // UI state
        public string ComfyUIServer
        {
            get => _comfyUIServer;
            set
            {
                _comfyUIServer = value;
                OnPropertyChanged();
            }
        }

        public string ComfyUIPort
        {
            get => _comfyUIPort;
            set
            {
                _comfyUIPort = value;
                OnPropertyChanged();
            }
        }

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            set
            {
                _statusBarMessage = value;
                OnPropertyChanged();
            }
        }

        public string VideoInfo
        {
            get => _videoInfo;
            set
            {
                _videoInfo = value;
                OnPropertyChanged();
            }
        }

        #endregion

        #region Commands

        public ICommand SelectImageCommand { get; }
        public ICommand SelectFirstFrameImageCommand { get; }
        public ICommand SelectLastFrameImageCommand { get; }
        public RelayCommand GenerateVideoCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand SendToEditCameraCommand { get; }

        // Image analysis commands
        public ICommand AnalyzeImageCommand { get; }
        public ICommand AnalyzeFirstFrameImageCommand { get; }
        public RelayCommand SendAnalysisToQueueCommand { get; }
        public ICommand OpenLMStudioSettingsCommand { get; }
        public RelayCommand CopyAnalysisCommand { get; }

        // Queue commands
        public RelayCommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public RelayCommand ProcessQueueCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public ICommand ReprocessItemCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PauseQueueCommand { get; }
        public RelayCommand ResumeQueueCommand { get; }

        // Story Video Generator commands
        public ICommand SelectStoryPromptJsonCommand { get; }
        public ICommand SelectStoryImagesFolderCommand { get; }
        public RelayCommand LoadStoryQueueCommand { get; }
        public RelayCommand ProcessStoryQueueCommand { get; }
        public RelayCommand ClearStoryQueueCommand { get; }
        public RelayCommand StopStoryQueueCommand { get; }
        public RelayCommand ReprocessAllStoryFailedCommand { get; }
        public bool HasStoryFailedItems => StoryVideoQueue.Any(x => x.Status == "Failed");
        public RelayCommand PauseStoryQueueCommand { get; }
        public RelayCommand ResumeStoryQueueCommand { get; }
        public RelayCommand<StoryVideoQueueItem> RegenerateStoryItemCommand { get; }
        public RelayCommand<StoryVideoQueueItem> DeleteStoryItemCommand { get; }
        public RelayCommand JoinClipsCommand { get; }

        public bool HasCompletedStoryItems =>
            !_isJoiningClips &&
            StoryVideoQueue.Any(i => i.Status == "Completed" && !string.IsNullOrEmpty(i.OutputVideoPath) && File.Exists(i.OutputVideoPath));

        private bool _isJoiningClips;
        public bool IsJoiningClips
        {
            get => _isJoiningClips;
            set
            {
                if (_isJoiningClips != value)
                {
                    _isJoiningClips = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCompletedStoryItems));
                    JoinClipsCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Workflow toggle command
        public ICommand ToggleWorkflowCommand { get; }
        public ICommand ToggleSingleWorkflowCommand { get; }

        #endregion

        private void NotifyCommandsCanExecuteChanged()
        {
            GenerateVideoCommand.NotifyCanExecuteChanged();
            SendAnalysisToQueueCommand.NotifyCanExecuteChanged();
            CopyAnalysisCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
            ProcessQueueCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PauseQueueCommand.NotifyCanExecuteChanged();
            ResumeQueueCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            SendToEditCameraCommand.NotifyCanExecuteChanged();
            LoadStoryQueueCommand.NotifyCanExecuteChanged();
            ProcessStoryQueueCommand.NotifyCanExecuteChanged();
            ClearStoryQueueCommand.NotifyCanExecuteChanged();
            StopStoryQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllStoryFailedCommand.NotifyCanExecuteChanged();
            PauseStoryQueueCommand.NotifyCanExecuteChanged();
            ResumeStoryQueueCommand.NotifyCanExecuteChanged();
            JoinClipsCommand.NotifyCanExecuteChanged();
        }

        #region Image Selection Methods

        private async void SelectImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                ImageFilePath = filePath;
                SaveImageFolder(filePath);
                AddLog($"Selected image: {Path.GetFileName(ImageFilePath)}");
            }
        }

        private async void SelectFirstFrameImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select First Frame Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                FirstFrameImagePath = filePath;
                SaveImageFolder(filePath);
                AddLog($"Selected first frame: {Path.GetFileName(FirstFrameImagePath)}");
            }
        }

        private async void SelectLastFrameImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Last Frame Image",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                initialDirectory);

            if (filePath != null)
            {
                LastFrameImagePath = filePath;
                SaveImageFolder(filePath);
                AddLog($"Selected last frame: {Path.GetFileName(LastFrameImagePath)}");
            }
        }

        private void SaveImageFolder(string filePath)
        {
            var folderPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
            {
                _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        public void SetImagePath(string imagePath)
        {
            if (File.Exists(imagePath))
            {
                ImageFilePath = imagePath;
                AddLog($"Image loaded from edit camera: {Path.GetFileName(ImageFilePath)}");
            }
        }

        #endregion

        #region Image Preview Methods

        private void LoadImagePreview()
        {
            if (string.IsNullOrEmpty(ImageFilePath) || !File.Exists(ImageFilePath))
            {
                ImagePreviewSource = null;
                ImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(ImageFilePath);
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreviewSource = bitmap;
                var fileInfo = new FileInfo(ImageFilePath);
                ImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImagePreviewSource = null;
                ImageInfo = "Error loading image";
            }
        }

        private void LoadFirstFrameImagePreview()
        {
            if (string.IsNullOrEmpty(FirstFrameImagePath) || !File.Exists(FirstFrameImagePath))
            {
                FirstFrameImagePreview = null;
                FirstFrameImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(FirstFrameImagePath);
                bitmap.EndInit();
                bitmap.Freeze();

                FirstFrameImagePreview = bitmap;
                var fileInfo = new FileInfo(FirstFrameImagePath);
                FirstFrameImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading first frame preview: {ex.Message}");
                FirstFrameImagePreview = null;
                FirstFrameImageInfo = "Error loading image";
            }
        }

        private void LoadLastFrameImagePreview()
        {
            if (string.IsNullOrEmpty(LastFrameImagePath) || !File.Exists(LastFrameImagePath))
            {
                LastFrameImagePreview = null;
                LastFrameImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(LastFrameImagePath);
                bitmap.EndInit();
                bitmap.Freeze();

                LastFrameImagePreview = bitmap;
                var fileInfo = new FileInfo(LastFrameImagePath);
                LastFrameImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading last frame preview: {ex.Message}");
                LastFrameImagePreview = null;
                LastFrameImageInfo = "Error loading image";
            }
        }

        #endregion

        #region Workflow Methods

        private void ToggleWorkflow()
        {
            UseLTXWorkflow = !UseLTXWorkflow;
        }

        private void ToggleSingleWorkflow()
        {
            // Cycle between LTX2V and Wan22 for single video generator
            SelectedSingleWorkflow = SelectedSingleWorkflow == SingleVideoWorkflow.LTX2V
                ? SingleVideoWorkflow.Wan22
                : SingleVideoWorkflow.LTX2V;
        }

        #endregion

        #region Image Analysis Methods

        private async Task AnalyzeImageAsync()
        {
            if (!HasImage)
            {
                AddLog("Cannot analyze: No image loaded");
                return;
            }

            await AnalyzeImageInternalAsync(ImageFilePath);
        }

        private async Task AnalyzeFirstFrameImageAsync()
        {
            if (!HasFirstFrameImage)
            {
                AddLog("Cannot analyze: No first frame image loaded");
                return;
            }

            if (SelectedSingleWorkflow == SingleVideoWorkflow.Wan22 && HasLastFrameImage)
                await AnalyzeImageInternalAsync(FirstFrameImagePath, LastFrameImagePath);
            else
                await AnalyzeImageInternalAsync(FirstFrameImagePath);
        }

        private async Task AnalyzeImageInternalAsync(string imagePath, string? lastImagePath = null)
        {
            _analysisCancellationTokenSource?.Dispose();
            _analysisCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsAnalyzing = true;
                AnalysisStatus = "Analyzing image with LM Studio...";
                AnalysisProgress = 0;
                ImageAnalysis = "Analyzing image...";

                AddLog("=== Starting image analysis with LM Studio ===");

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);
                AddLog($"Using LM Studio at: {baseUrl}");

                var models = await _lmStudioService.GetAvailableModelsAsync(_analysisCancellationTokenSource.Token);
                string selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    var qwenModel = models.FirstOrDefault(m =>
                        m.Name.ToLower().Contains("qwen") && m.Name.ToLower().Contains("vl"));

                    if (qwenModel != null)
                    {
                        selectedModel = qwenModel.Name;
                        AddLog($"Auto-selected Qwen VL model: {selectedModel}");
                    }
                    else if (models.Any())
                    {
                        selectedModel = models.First().Name;
                        AddLog($"Using first available model: {selectedModel}");
                    }
                    else
                    {
                        throw new Exception("No models available in LM Studio. Please load a vision model.");
                    }
                }

                AnalysisStatus = "Analyzing with LM Studio...";
                AnalysisProgress = 30;

                // Determine which prompt to use based on workflow selection
                string analysisPrompt;
                string? promptPath = null;
                string promptLabel;

                if (SelectedSingleWorkflow == SingleVideoWorkflow.Wan22)
                {
                    promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "wan-system.md");
                    promptLabel = "Wan 2.2";
                }
                else
                {
                    promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "ltx_FFLF.md");
                    promptLabel = "LTX-2 Action Video";
                }

                if (File.Exists(promptPath))
                {
                    analysisPrompt = await File.ReadAllTextAsync(promptPath, _analysisCancellationTokenSource.Token);
                    AddLog($"Using {promptLabel} system prompt");
                }
                else
                {
                    AddLog($"WARNING: {promptLabel} prompt not found at {promptPath}, using default");
                    analysisPrompt = "Describe this image in detail for video generation.";
                }

                string analysisResult;
                if (lastImagePath != null)
                {
                    AddLog($"Sending both frames to LM Studio: first={Path.GetFileName(imagePath)}, last={Path.GetFileName(lastImagePath)}");
                    analysisResult = await _lmStudioService.AnalyzeTwoImagesAsync(
                        selectedModel,
                        imagePath,
                        lastImagePath,
                        analysisPrompt,
                        maxTokens: 2000,
                        _analysisCancellationTokenSource.Token);
                }
                else
                {
                    analysisResult = await _lmStudioService.AnalyzeImageAsync(
                        selectedModel,
                        imagePath,
                        analysisPrompt,
                        maxTokens: 2000,
                        _analysisCancellationTokenSource.Token);
                }

                AnalysisProgress = 90;

                if (!string.IsNullOrEmpty(analysisResult))
                {
                    ImageAnalysis = analysisResult;
                    AnalysisStatus = "Analysis complete";
                    AnalysisProgress = 100;
                    AddLog("Image analysis completed successfully");
                    StatusBarMessage = "Image analysis complete";
                }
                else
                {
                    ImageAnalysis = "Analysis completed but no text was returned.";
                    AnalysisStatus = "Analysis complete (no output)";
                }
            }
            catch (OperationCanceledException)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Cancelled";
                AddLog("Image analysis cancelled");
            }
            catch (Exception ex)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Error";
                ImageAnalysis = $"Error analyzing image: {ex.Message}";
                AddLog($"ERROR analyzing image: {ex.Message}");
                System.Windows.MessageBox.Show($"Error analyzing image:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                NotifyCommandsCanExecuteChanged();
            }
        }

        private void CopyAnalysis()
        {
            if (!string.IsNullOrWhiteSpace(ImageAnalysis))
            {
                try
                {
                    System.Windows.Clipboard.SetText(ImageAnalysis);
                    AddLog("Analysis copied to clipboard");
                }
                catch (Exception ex)
                {
                    AddLog($"Failed to copy to clipboard: {ex.Message}");
                }
            }
        }

        private void OpenLMStudioSettings()
        {
            // This would open LM Studio settings dialog
            AddLog("LM Studio settings requested");
            System.Windows.MessageBox.Show("LM Studio settings dialog - configure base URL and model selection.", "LM Studio Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SendAnalysisToQueue()
        {
            if (!string.IsNullOrEmpty(ImageAnalysis) && HasFirstFrameImage && HasLastFrameImage)
            {
                var randomSeed = GenerateRandomSeedIfNeeded(FirstFrameImagePath, LastFrameImagePath);

                var queueItem = new QueueItem
                {
                    Prompt = ImageAnalysis,
                    FirstFrameImagePath = FirstFrameImagePath,
                    LastFrameImagePath = LastFrameImagePath,
                    Seed = randomSeed,
                    ItemStatus = QueueItemStatus.Pending
                };

                PromptQueue.Add(queueItem);
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Analysis sent to queue");
                StatusBarMessage = "Analysis added to queue";
            }
        }

        #endregion

        #region Queue Methods

        private void AddVideoGenerationToQueue()
        {
            if (HasFirstFrameImage && HasLastFrameImage)
            {
                var prompt = !string.IsNullOrWhiteSpace(ImageAnalysis) ? ImageAnalysis : VideoPrompt;
                var randomSeed = GenerateRandomSeedIfNeeded(FirstFrameImagePath, LastFrameImagePath);

                var queueItem = new QueueItem
                {
                    Prompt = prompt,
                    FirstFrameImagePath = FirstFrameImagePath,
                    LastFrameImagePath = LastFrameImagePath,
                    Seed = randomSeed,
                    ItemStatus = QueueItemStatus.Pending
                };

                PromptQueue.Add(queueItem);
                OnPropertyChanged(nameof(CanProcessQueue));
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Video generation added to queue");
                StatusBarMessage = "Video generation added to queue";
            }
        }

        private long GenerateRandomSeedIfNeeded(string firstImagePath, string lastImagePath)
        {
            var hasSameImages = PromptQueue.Any(item =>
                item.FirstFrameImagePath == firstImagePath &&
                item.LastFrameImagePath == lastImagePath &&
                item.ItemStatus == QueueItemStatus.Pending);

            if (hasSameImages || Seed == 0)
            {
                const long maxRgthreeSeed = 1125899906842624;
                var random = new Random();
                return (long)(random.NextDouble() * maxRgthreeSeed);
            }

            return Seed;
        }

        private void AddToQueue()
        {
            if (string.IsNullOrWhiteSpace(NewQueuePrompt)) return;

            if (HasFirstFrameImage && HasLastFrameImage)
            {
                var randomSeed = GenerateRandomSeedIfNeeded(FirstFrameImagePath, LastFrameImagePath);

                var queueItem = new QueueItem
                {
                    Prompt = NewQueuePrompt,
                    FirstFrameImagePath = FirstFrameImagePath,
                    LastFrameImagePath = LastFrameImagePath,
                    Seed = randomSeed,
                    ItemStatus = QueueItemStatus.Pending
                };

                PromptQueue.Add(queueItem);
                NewQueuePrompt = string.Empty;
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Added to queue");

                // Auto-start queue processing if not already processing
                if (!IsProcessingQueue && PromptQueue.Any(q => q.ItemStatus == QueueItemStatus.Pending))
                {
                    _ = ProcessQueueAsync();
                }
            }
        }

        private void RemoveFromQueue(QueueItem? item)
        {
            if (item != null && PromptQueue.Contains(item))
            {
                PromptQueue.Remove(item);
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Removed from queue");
            }
        }

        private void ClearQueue()
        {
            _promptQueueCts?.Cancel();
            foreach (var item in PromptQueue.ToList())
                PromptQueue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Prompt queue cleared");
        }

        private void StopQueue()
        {
            _promptQueueCts?.Cancel();
            AddLog("Prompt queue stop requested");
        }

        private async Task ProcessQueueAsync()
        {
            if (!PromptQueue.Any()) return;

            IsProcessingQueue = true;
            _promptQueueCts?.Dispose();
            _promptQueueCts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_promptQueueCts.Token, App.ShutdownToken);
            var token = linkedCts.Token;
            NotifyCommandsCanExecuteChanged();
            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("VideoGenerator", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                NotifyCommandsCanExecuteChanged();
                return;
            }

            AddLog($"=== Starting to process queue with {PromptQueue.Count} items ===");

            using (lease)
            {
                try
                {
                    QueueItem? item;
                    while (!token.IsCancellationRequested &&
                           (item = PromptQueue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                    {
                        if (IsProcessing) break;

                        _pauseEvent.Wait(token);

                        try
                        {
                            item.ItemStatus = QueueItemStatus.Processing;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            AddLog($"Processing queue item...");

                            // Generate video for this item
                            await GenerateVideoForQueueItemAsync(item);

                            if (HasResult)
                            {
                                item.ItemStatus = QueueItemStatus.Completed;
                                item.VideoPath = ResultVideoPath;
                                AddLog($"Queue item completed");
                            }
                            else
                            {
                                item.ItemStatus = QueueItemStatus.Failed;
                                AddLog($"Queue item failed");
                            }

                            HasResult = false;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            await Task.Delay(1000);
                        }
                        catch (Exception ex)
                        {
                            var shouldRetry = await TryHandleCrashAndRetryAsync(item, ex);
                            if (shouldRetry)
                            {
                                item.ItemStatus = QueueItemStatus.Pending;
                                UpdateQueueStatus();
                                SaveQueueToFile();
                                AddLog("Item reset to Pending — will retry after ComfyUI restart");
                            }
                            else
                            {
                                item.ItemStatus = QueueItemStatus.Failed;
                                UpdateQueueStatus();
                                SaveQueueToFile();
                                AddLog($"Error processing queue item: {ex.Message}");
                            }
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                    AddLog("=== Queue processing completed ===");
                }
                catch (OperationCanceledException)
                {
                    AddLog("Queue processing stopped by user");
                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
                catch (Exception ex)
                {
                    AddLog($"Error processing queue: {ex.Message}");
                }
                finally
                {
                    IsProcessingQueue = false;
                    IsQueuePaused = false;
                    _pauseEvent.Set();
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        private async Task GenerateVideoForQueueItemAsync(QueueItem item)
        {
            // Store original values
            var originalPrompt = VideoPrompt;
            var originalImagePath = ImageFilePath;
            var originalFirstFramePath = FirstFrameImagePath;
            var originalLastFramePath = LastFrameImagePath;
            var originalSeed = Seed;

            try
            {
                // Set values from queue item
                VideoPrompt = item.Prompt;

                if (!string.IsNullOrEmpty(item.FirstFrameImagePath) && !string.IsNullOrEmpty(item.LastFrameImagePath))
                {
                    // Dual-frame mode
                    FirstFrameImagePath = item.FirstFrameImagePath;
                    LastFrameImagePath = item.LastFrameImagePath;
                    LoadFirstFrameImagePreview();
                    LoadLastFrameImagePreview();
                }
                else if (!string.IsNullOrEmpty(item.ImagePath))
                {
                    // Single-image mode
                    ImageFilePath = item.ImagePath;
                    LoadImagePreview();
                }

                // Set seed from queue item (for randomization)
                if (item.Seed > 0)
                {
                    Seed = item.Seed;
                    AddLog($"Using seed from queue item: {Seed}");
                }

                // Generate video
                await GenerateVideoAsyncInternal();
            }
            finally
            {
                // Restore original values
                VideoPrompt = originalPrompt;
                ImageFilePath = originalImagePath;
                FirstFrameImagePath = originalFirstFramePath;
                LastFrameImagePath = originalLastFramePath;
                Seed = originalSeed;
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResult = false;
                ResultVideoPath = string.Empty;
                VideoInfo = string.Empty;

                // Use ImageAnalysis as prompt if VideoPrompt is empty
                if (string.IsNullOrWhiteSpace(VideoPrompt) && !string.IsNullOrWhiteSpace(ImageAnalysis))
                {
                    VideoPrompt = ImageAnalysis;
                    AddLog("Using ImageAnalysis as video prompt");
                }

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"First frame: {Path.GetFileName(FirstFrameImagePath)}");
                if (!string.IsNullOrEmpty(LastFrameImagePath))
                {
                    AddLog($"Last frame: {Path.GetFileName(LastFrameImagePath)}");
                }
                else
                {
                    AddLog("Last frame: None (single frame generation)");
                }
                AddLog($"Prompt: {VideoPrompt}");
                AddLog($"Video settings: {VideoLength} frames @ {Fps} FPS");

                // Check if ComfyUI has crashed and restart if needed
                ProcessingStatus = "Checking ComfyUI status...";
                AddLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    return;
                }

                AddLog("ComfyUI is running and responsive");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync();
                    AddLog("Connected to ComfyUI");
                }

                // Load workflow
                // Story video uses SelectedStoryWorkflow (3 LTX variants or Painter)
                // Single video uses SelectedSingleWorkflow (LTX2V or Wan22)
                string workflowFileName;
                if (_isStoryVideoMode)
                {
                    workflowFileName = _selectedStoryWorkflow switch
                    {
                        StoryVideoWorkflow.Eros10S => Path.Combine("video", "ltx", "10Eros_10SNodes_InstantAction_I2VAPI.json"),
                        StoryVideoWorkflow.LTX22B => Path.Combine("video", "ltx", "LTX-22-B.json"),
                        StoryVideoWorkflow.Painter => "painteri2vAPI.json",
                        StoryVideoWorkflow.PainterEnhanced => Path.Combine("video", "story", "painteri2vAPI-enhancednsfw-HL.json"),
                        _ => Path.Combine("video", "ltx", "Vantage-Sulphur-2-WorkflowAPI.json")
                    };
                }
                else
                {
                    // Single video generator
                    workflowFileName = SelectedSingleWorkflow == SingleVideoWorkflow.LTX2V
                        ? "LTXV-DoEverything-v2.json"
                        : "LF-t2v-i2v-FFLF-Main v1.1API.json";
                }
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", workflowFileName);

                AddLog($"Loading workflow: {workflowFileName}");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload input images
                ProcessingStatus = "Uploading input images...";
                ProcessingProgress = 10;
                AddLog("Uploading first frame image to ComfyUI...");

                var uploadedFirstFrameImageName = await _comfyUIService.UploadImageAsync(FirstFrameImagePath);
                if (string.IsNullOrEmpty(uploadedFirstFrameImageName))
                {
                    AddLog("ERROR: First frame image upload failed");
                    return;
                }
                AddLog($"First frame uploaded: {uploadedFirstFrameImageName}");

                // Upload last frame only if provided
                string uploadedLastFrameImageName = string.Empty;
                if (!string.IsNullOrEmpty(LastFrameImagePath))
                {
                    AddLog("Uploading last frame image to ComfyUI...");
                    uploadedLastFrameImageName = await _comfyUIService.UploadImageAsync(LastFrameImagePath);
                    if (string.IsNullOrEmpty(uploadedLastFrameImageName))
                    {
                        AddLog("ERROR: Last frame image upload failed");
                        return;
                    }
                    AddLog($"Last frame uploaded: {uploadedLastFrameImageName}");
                }
                else
                {
                    AddLog("Skipping last frame upload (not provided)");
                }

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedFirstFrameImageName, uploadedLastFrameImageName);

                // Execute workflow
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 30;
                AddLog("Executing video generation workflow...");

                // Record existing video files BEFORE execution
                var existingFilesBeforeExecution = GetExistingVideoFiles("*.mp4", "testrun", "testrun/vid", "video", "intpups", "intp", "ups", "ltx2.3/my");
                AddLog($"Recording {existingFilesBeforeExecution.Count} existing video files before execution");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6); // Scale to 30-90%
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated video...");

                var outputVideo = await WaitForNewVideoAsync(
                    existingFilesBeforeExecution,
                    "*.mp4",
                    TimeSpan.FromSeconds(60),
                    TimeSpan.FromSeconds(2),
                    "testrun", "testrun/vid", "video", "intpups", "intp", "ups", "ltx2.3/my");

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    await LocalCopyService.CopyVideoAsync(outputVideo);
                    HasResult = true;

                    var fileInfo = new FileInfo(outputVideo);
                    VideoInfo = $"Video: {VideoLength} frames @ {Fps} FPS • {fileInfo.Length / 1024}KB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Video generation complete - {Path.GetFileName(outputVideo)}";

                    AddLog($"=== Video generation completed successfully ===");
                    AddLog($"Video saved to: {outputVideo}");
                }
                else
                {
                    AddLog("WARNING: No output video found after waiting");
                    ProcessingStatus = "No output generated";
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string firstFrameImageName, string lastFrameImageName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            // Determine which workflow to use:
            // - Story video: SelectedStoryWorkflow (VantageSulphur2, Eros10S, LTX22B, or Painter)
            // - Single video: SelectedSingleWorkflow (LTX2V or Wan22)
            bool isStoryLtx = _isStoryVideoMode && _selectedStoryWorkflow != StoryVideoWorkflow.Painter && _selectedStoryWorkflow != StoryVideoWorkflow.PainterEnhanced;
            bool isLTXV = !_isStoryVideoMode && SelectedSingleWorkflow == SingleVideoWorkflow.LTX2V;
            bool isWan22 = !_isStoryVideoMode && SelectedSingleWorkflow == SingleVideoWorkflow.Wan22;

            // Handle the 3 new story LTX workflows
            if (isStoryLtx)
            {
                return UpdateStoryLtxWorkflowParameters(workflowDict, firstFrameImageName);
            }

            // Update first frame image - node IDs differ by workflow
            // Painter uses node 119 (LoadImage → GetImageRangeFromBatch → start_image)
            string[] firstFrameNodes = isWan22 ? new[] { "55" } : (isLTXV ? new[] { "106" } : new[] { "119" });
            foreach (var nodeId in firstFrameNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs") && node.ContainsKey("class_type") && node["class_type"]?.ToString() == "LoadImage")
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["image"] = firstFrameImageName;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (First Frame) - Image updated");
                        }
                    }
                }
            }

            // Update last frame image - Painter has no separate last frame node (single reference image)
            if (!string.IsNullOrEmpty(lastFrameImageName) && (isLTXV || isWan22))
            {
                string[] lastFrameNodes = isWan22 ? new[] { "643" } : new[] { "35" };
                foreach (var nodeId in lastFrameNodes)
                {
                    if (workflowDict.ContainsKey(nodeId))
                    {
                        var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                        if (node != null && node.ContainsKey("inputs") && node.ContainsKey("class_type") && node["class_type"]?.ToString() == "LoadImage")
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                            if (inputs != null)
                            {
                                inputs["image"] = lastFrameImageName;
                                node["inputs"] = inputs;
                                workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                                AddLog($"✓ Node {nodeId} (Last Frame) - Image updated");
                            }
                        }
                    }
                }
            }
            else if (!isLTXV && !isWan22)
            {
                AddLog("Painter workflow: single reference image used (no separate last frame)");
            }
            else
            {
                AddLog("Last frame not provided - skipping last frame node update");
            }

            // Update positive prompt - node IDs and field names differ by workflow
            string[] positivePromptNodes;
            if (isWan22)
            {
                positivePromptNodes = new[] { "89" }; // Wan 2.2 uses "value" field
            }
            else
            {
                positivePromptNodes = isLTXV ? new[] { "59", "121", "92:3" } : new[] { "6" };
            }
            foreach (var nodeId in positivePromptNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            if (inputs.ContainsKey("value"))
                                inputs["value"] = VideoPrompt;
                            else
                                inputs["text"] = VideoPrompt;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (Positive Prompt) - Updated");
                        }
                    }
                }
            }

            // Update negative prompt - node IDs differ by workflow
            string[] negativePromptNodes = isWan22 ? new[] { "88" } : new[] { "89", "7" };
            foreach (var nodeId in negativePromptNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            // Wan 2.2 uses "value" field, LTXV/Painter use "text"
                            if (inputs.ContainsKey("value"))
                                inputs["value"] = NegativePrompt;
                            else
                                inputs["text"] = NegativePrompt;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (Negative Prompt) - Updated");
                        }
                    }
                }
            }

            // Update workflow-specific parameters
            if (isWan22)
            {
                // Wan 2.2 parameters: duration (node 150) and seed (node 135)

                // Duration (node 150) - uses Xi and Xf fields (both set to same value in seconds)
                if (workflowDict.ContainsKey("150"))
                {
                    var durationSeconds = (double)VideoLength / Fps; // Convert frames to seconds
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["150"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            // Set both Xi and Xf to the same duration value
                            inputs["Xi"] = durationSeconds;
                            inputs["Xf"] = durationSeconds;
                            node["inputs"] = inputs;
                            workflowDict["150"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 150 (Duration) - {durationSeconds:F1}s (Xi={durationSeconds:F1}, Xf={durationSeconds:F1})");
                        }
                    }
                }

                // Seed (node 135) - uses "seed" field
                if (workflowDict.ContainsKey("135"))
                {
                    const long maxRgthreeSeed = 1125899906842624;
                    var seedValue = Seed > 0 ? Seed : (long)(new Random().NextDouble() * maxRgthreeSeed);
                    // Clamp to rgthree max value
                    seedValue = Math.Min(seedValue, maxRgthreeSeed);
                    // Also ensure non-negative
                    if (seedValue < 0) seedValue = Math.Abs(seedValue) % maxRgthreeSeed;
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["135"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["seed"] = seedValue;
                            node["inputs"] = inputs;
                            workflowDict["135"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 135 (Seed) - {seedValue}");
                        }
                    }
                }
            }
            else if (isLTXV)
            {
                // LTXV parameters: frame count, FPS, seed

                // Frame count (node 54)
                if (workflowDict.ContainsKey("54"))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["54"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = VideoLength;
                            node["inputs"] = inputs;
                            workflowDict["54"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 54 (Frame Count) - {VideoLength}");
                        }
                    }
                }

                // Frame rate (node 55)
                if (workflowDict.ContainsKey("55"))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["55"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = Fps;
                            node["inputs"] = inputs;
                            workflowDict["55"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 55 (Frame Rate) - {Fps}");
                        }
                    }
                }

                // Seed (node 128)
                if (workflowDict.ContainsKey("128"))
                {
                    var seedValue = Seed > 0 ? Seed : ((long)new Random().Next() << 32) | (uint)new Random().Next();
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["128"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = seedValue;
                            node["inputs"] = inputs;
                            workflowDict["128"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 128 (Seed) - {seedValue}");
                        }
                    }
                }
            }
            else
            {
                // Painter (WAN 2.2 LightX2V) parameters

                // Randomize seed (node 132, KSamplerAdvanced noise_seed)
                if (workflowDict.ContainsKey("132"))
                {
                    var seedValue = Seed > 0 ? (int)(Seed & 0x7FFFFFFF) : new Random().Next();
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["132"].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["noise_seed"] = seedValue;
                            node["inputs"] = inputs;
                            workflowDict["132"] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node 132 (Seed) - {seedValue}");
                        }
                    }
                }
            }

            // Auto-detect image orientation and set video dimensions
            var (videoW, videoH) = GetVideoDimensionsForImage(FirstFrameImagePath, isLTXV, isWan22);
            if (isLTXV)
            {
                UpdatePrimitiveIntNode(workflowDict, "56", videoW, "Width");
                UpdatePrimitiveIntNode(workflowDict, "57", videoH, "Height");
            }
            else if (!isWan22) // Painter
            {
                UpdatePrimitiveIntNode(workflowDict, "112", videoW, "Width");
                UpdatePrimitiveIntNode(workflowDict, "114", videoH, "Height");
            }
            // Wan22 uses SimpleMath nodes that derive from the image itself — no override needed

            AddLog("Workflow parameters updated successfully");
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateStoryLtxWorkflowParameters(Dictionary<string, JsonElement> workflowDict, string imageName)
        {
            string imageNode, positiveNode, negativeNode, frameNode, fpsNode;
            string[] seedNodes;
            bool frameInSeconds = false;

            switch (_selectedStoryWorkflow)
            {
                case StoryVideoWorkflow.Eros10S:
                    imageNode = "528";
                    positiveNode = "536";
                    negativeNode = "537";
                    frameNode = "511";
                    fpsNode = "542";
                    seedNodes = new[] { "524" };
                    break;
                case StoryVideoWorkflow.LTX22B:
                    imageNode = "5016:2004";
                    positiveNode = "5026:5018";
                    negativeNode = "5026:5019";
                    frameNode = "5026:4988";
                    fpsNode = "5026:4989";
                    seedNodes = new[] { "5002:4832", "5001:4967", "5012:5009" };
                    break;
                default: // VantageSulphur2
                    imageNode = "255";
                    positiveNode = "393";
                    negativeNode = "328";
                    frameNode = "322";
                    fpsNode = "304";
                    seedNodes = new[] { "259" };
                    frameInSeconds = true;
                    break;
            }

            // Image
            SetNodeField(workflowDict, imageNode, "image", imageName, "Image");

            // Positive prompt
            SetNodeTextField(workflowDict, positiveNode, VideoPrompt, "Positive Prompt");

            // Vantage Sulphur 2: set width/height from input image orientation (nodes 261, 299)
            if (_selectedStoryWorkflow == StoryVideoWorkflow.VantageSulphur2)
            {
                var (videoW, videoH) = GetVideoDimensionsForImage(FirstFrameImagePath, isLTXV: true, isWan22: false);
                UpdatePrimitiveIntNode(workflowDict, "261", videoW, "Width");
                UpdatePrimitiveIntNode(workflowDict, "299", videoH, "Height");
            }

            // Vantage Sulphur 2: bypass VisionLLMNode (412:390) by redirecting Any Switch (394)
            // any_01 normally points to 412:390 (VisionLLM), redirect it to 393 (manual prompt)
            if (_selectedStoryWorkflow == StoryVideoWorkflow.VantageSulphur2 && workflowDict.ContainsKey("394"))
            {
                var switchNode = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["394"].GetRawText());
                if (switchNode != null && switchNode.ContainsKey("inputs"))
                {
                    var switchInputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(switchNode["inputs"]));
                    if (switchInputs != null)
                    {
                        switchInputs["any_01"] = new object[] { "393", 0 };
                        switchNode["inputs"] = switchInputs;
                        workflowDict["394"] = JsonSerializer.SerializeToElement(switchNode);
                        AddLog("✓ Node 394 (Prompt Selector) - Bypassed VisionLLM, using manual prompt");
                    }
                }
            }

            // Negative prompt
            if (!string.IsNullOrEmpty(NegativePrompt))
                SetNodeTextField(workflowDict, negativeNode, NegativePrompt, "Negative Prompt");

            // FPS
            SetNodeField(workflowDict, fpsNode, "value", (double)Fps, "FPS");

            // Frame count (Vantage uses seconds, others use frames)
            object frameValue = frameInSeconds ? (object)(VideoLength / (double)Fps) : (object)VideoLength;
            SetNodeField(workflowDict, frameNode, "value", frameValue, frameInSeconds ? "Duration(s)" : "Frame Count");

            // Seed
            var seedValue = Seed > 0 ? Seed : ((long)new Random().Next() << 32) | (uint)new Random().Next();
            foreach (var sid in seedNodes)
                SetNodeField(workflowDict, sid, "noise_seed", seedValue, "Seed");

            AddLog("Story LTX workflow parameters updated successfully");
            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void SetNodeField(Dictionary<string, JsonElement> dict, string nodeId, string field, object value, string label)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            inputs[field] = value;
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
            AddLog($"✓ Node {nodeId} ({label}) - Updated");
        }

        private void SetNodeTextField(Dictionary<string, JsonElement> dict, string nodeId, string text, string label)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            if (inputs.ContainsKey("value")) inputs["value"] = text;
            else inputs["text"] = text;
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
            AddLog($"✓ Node {nodeId} ({label}) - Updated");
        }

        private (int width, int height) GetVideoDimensionsForImage(string imagePath, bool isLTXV, bool isWan22)
        {
            // Wan22 auto-derives from image — skip
            if (isWan22) return (Width, Height);

            try
            {
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();

                    int imgW = bi.PixelWidth;
                    int imgH = bi.PixelHeight;

                    bool portrait = imgH > imgW;
                    bool square = imgW == imgH;

                    if (isLTXV)
                    {
                        // LTXV native resolutions (multiples of 32, within model limits)
                        if (square)   return (720, 720);
                        if (portrait) return (720, 1280);
                        return (1280, 720); // landscape
                    }
                    else // Painter
                    {
                        if (square)   return (512, 512);
                        if (portrait) return (480, 832);
                        return (832, 480); // landscape
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"WARNING: Could not read image dimensions for orientation detection: {ex.Message}");
            }

            // Fallback to defaults
            return isLTXV ? (1280, 720) : (832, 480);
        }

        private void UpdatePrimitiveIntNode(Dictionary<string, JsonElement> workflowDict, string nodeId, int value, string label)
        {
            if (!workflowDict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            inputs["value"] = value;
            node["inputs"] = inputs;
            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
            AddLog($"✓ Node {nodeId} ({label}) - {value}px");
        }

        private void UpdateQueueStatus()
        {
            var totalCount = PromptQueue.Count;
            var pendingCount = PromptQueue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var completedCount = PromptQueue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failedCount = PromptQueue.Count(x => x.ItemStatus == QueueItemStatus.Failed);

            if (totalCount == 0)
            {
                QueueStatus = "Queue is empty";
            }
            else if (IsProcessingQueue)
            {
                QueueStatus = $"Processing queue... ({pendingCount} pending, {completedCount} completed, {failedCount} failed)";
            }
            else
            {
                QueueStatus = $"Queue: {totalCount} items ({pendingCount} pending, {completedCount} completed, {failedCount} failed)";
            }

            OnPropertyChanged(nameof(HasFailedItems));
            OnPropertyChanged(nameof(CanProcessQueue));
            NotifyCommandsCanExecuteChanged();
        }

        private async Task ReprocessItemAsync(QueueItem? item)
        {
            if (item == null) return;

            AddLog($"Reprocessing failed item...");
            item.ItemStatus = QueueItemStatus.Processing;
            UpdateQueueStatus();
            SaveQueueToFile();

            await GenerateVideoForQueueItemAsync(item);

            if (HasResult)
            {
                item.ItemStatus = QueueItemStatus.Completed;
                item.VideoPath = ResultVideoPath;
                AddLog($"Item reprocessed successfully");
            }
            else
            {
                item.ItemStatus = QueueItemStatus.Failed;
                AddLog($"Item reprocessing failed");
            }

            HasResult = false;
            UpdateQueueStatus();
            SaveQueueToFile();
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failedItems = PromptQueue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();

            if (!failedItems.Any())
            {
                AddLog("No failed items to reprocess");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"Reprocess {failedItems.Count} failed item(s)?",
                "Confirm Reprocess All",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.OK) return;

            AddLog($"=== Starting to reprocess {failedItems.Count} failed items ===");

            foreach (var item in failedItems)
            {
                if (item.ItemStatus == QueueItemStatus.Failed)
                {
                    await ReprocessItemAsync(item);
                    await Task.Delay(1000);
                }
            }

            AddLog("=== Reprocess all failed items completed ===");
        }

        private void PauseQueue()
        {
            IsQueuePaused = true;
            _pauseEvent.Reset();
            AddLog("Queue paused");
        }

        private void ResumeQueue()
        {
            IsQueuePaused = false;
            _pauseEvent.Set();
            AddLog("Queue resumed");
        }

        #endregion

        #region Story Video Queue Methods

        private async void SelectStoryPromptJson()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorStoryPromptJsonFolder;
            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Story Prompts File",
                "Prompt Files (*.json;*.txt)|*.json;*.txt|JSON Files (*.json)|*.json|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                initialDirectory);

            if (filePath != null)
            {
                StoryPromptJsonPath = filePath;

                var folderPath = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorStoryPromptJsonFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected story prompts file: {Path.GetFileName(StoryPromptJsonPath)}");

                // Auto-detect images folder if the same directory contains images
                if (!string.IsNullOrEmpty(folderPath) &&
                    (string.IsNullOrEmpty(StoryImagesFolderPath) || !Directory.Exists(StoryImagesFolderPath)))
                {
                    var hasImages = Directory.GetFiles(folderPath, "*.png")
                        .Concat(Directory.GetFiles(folderPath, "*.jpg"))
                        .Concat(Directory.GetFiles(folderPath, "*.jpeg"))
                        .Any();
                    if (hasImages)
                    {
                        StoryImagesFolderPath = folderPath;
                        AddLog($"Auto-detected images folder: {folderPath}");
                    }
                }
            }
        }

        private async void SelectStoryImagesFolder()
        {
            var initialPath = _settingsService.Settings?.VideoGeneratorStoryImagesFolder;

            var selectedPath = await _fileDialogService.OpenFolderDialogAsync(
                "Select the folder containing the story images",
                !string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath) ? initialPath : null,
                false);

            if (selectedPath != null)
            {
                StoryImagesFolderPath = selectedPath;

                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorStoryImagesFolder = selectedPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected story images folder: {StoryImagesFolderPath}");

                // Auto-detect prompts.txt or a JSON prompts file in the selected folder
                var txtPath = Path.Combine(selectedPath, "prompts.txt");
                if (File.Exists(txtPath))
                {
                    StoryPromptJsonPath = txtPath;
                    AddLog($"Auto-detected prompts file: prompts.txt");
                }
                else
                {
                    var jsonFiles = Directory.GetFiles(selectedPath, "*.json");
                    if (jsonFiles.Length == 1)
                    {
                        StoryPromptJsonPath = jsonFiles[0];
                        AddLog($"Auto-detected prompts file: {Path.GetFileName(jsonFiles[0])}");
                    }
                }
            }
        }

        private async Task LoadStoryQueueAsync()
        {
            if (!CanLoadStoryQueue) return;

            try
            {
                List<(string? ImageName, string Prompt)> promptPairs;
                var ext = Path.GetExtension(StoryPromptJsonPath).ToLowerInvariant();

                if (ext == ".txt")
                {
                    AddLog("Loading story prompts from TXT file...");
                    var txtContent = await File.ReadAllTextAsync(StoryPromptJsonPath);
                    promptPairs = ParsePromptsFromTxt(txtContent);
                }
                else
                {
                    AddLog("Loading story prompts from JSON file...");
                    var jsonContent = await File.ReadAllTextAsync(StoryPromptJsonPath);
                    var storyData = JsonSerializer.Deserialize<StoryPromptData>(jsonContent);
                    promptPairs = (storyData?.Prompts ?? new List<string>())
                        .Select(p => ((string?)null, p))
                        .ToList();
                }

                if (!promptPairs.Any())
                {
                    AddLog("ERROR: No prompts found in file");
                    System.Windows.MessageBox.Show("No prompts found in the file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var imageFiles = Directory.GetFiles(StoryImagesFolderPath, "*.png")
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpg"))
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpeg"))
                    .OrderBy(f => f)
                    .ToList();

                // Build lookup dictionaries for name-based pairing
                var imageByFilename = imageFiles.ToDictionary(
                    f => Path.GetFileName(f),
                    f => f,
                    StringComparer.OrdinalIgnoreCase);
                var imageByStem = imageFiles.ToDictionary(
                    f => Path.GetFileNameWithoutExtension(f),
                    f => f,
                    StringComparer.OrdinalIgnoreCase);

                bool useNamePairing = promptPairs.Any(p => p.ImageName != null);

                StoryVideoQueue.Clear();

                if (useNamePairing)
                {
                    int idx = 1;
                    foreach (var (imageName, prompt) in promptPairs)
                    {
                        string? imagePath = null;
                        if (imageName != null)
                        {
                            imageByFilename.TryGetValue(imageName, out imagePath);
                            if (imagePath == null)
                                imageByStem.TryGetValue(Path.GetFileNameWithoutExtension(imageName), out imagePath);
                        }

                        if (imagePath == null)
                        {
                            AddLog($"WARNING: No matching image found for '{imageName}', skipping");
                            continue;
                        }

                        var queueItem = new StoryVideoQueueItem
                        {
                            Index = idx++,
                            Prompt = prompt,
                            InputImagePath = imagePath,
                            Status = "Pending"
                        };
                        queueItem.PropertyChanged += StoryQueueItem_StatusChanged;
                        StoryVideoQueue.Add(queueItem);
                    }
                }
                else
                {
                    int count = Math.Min(promptPairs.Count, imageFiles.Count);
                    for (int i = 0; i < count; i++)
                    {
                        var queueItem = new StoryVideoQueueItem
                        {
                            Index = i + 1,
                            Prompt = promptPairs[i].Prompt,
                            InputImagePath = imageFiles[i],
                            Status = "Pending"
                        };
                        queueItem.PropertyChanged += StoryQueueItem_StatusChanged;
                        StoryVideoQueue.Add(queueItem);
                    }
                }

                UpdateStoryQueueStatus();
                AddLog($"Loaded {StoryVideoQueue.Count} story video items into queue");
                StatusBarMessage = $"Loaded {StoryVideoQueue.Count} items into story queue";
                SaveStoryQueueToFile();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading story queue: {ex.Message}");
                System.Windows.MessageBox.Show($"Error loading story queue:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StoryQueueItem_StatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StoryVideoQueueItem.Status))
            {
                OnPropertyChanged(nameof(CanProcessStoryQueue));
                NotifyCommandsCanExecuteChanged();
            }
        }

        private List<(string? ImageName, string Prompt)> ParsePromptsFromTxt(string content)
        {
            var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

            // Format A0: "# imagename.ext" header lines followed by prompt text
            if (Regex.IsMatch(content, @"^#\s+\S+\.(png|jpg|jpeg|webp|bmp|gif)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                var result = new List<(string? ImageName, string Prompt)>();
                var parts = Regex.Split(content, @"^(#\s+\S+\.(png|jpg|jpeg|webp|bmp|gif)\s*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                for (int i = 1; i + 2 < parts.Length; i += 3)
                {
                    var imageName = parts[i].TrimStart('#').Trim();
                    var promptText = parts[i + 2].Trim();
                    if (!string.IsNullOrWhiteSpace(promptText))
                        result.Add((imageName, promptText));
                }
                AddLog($"TXT format: # filename.ext headers ({result.Count} prompts)");
                return result;
            }


            // Format A: "Scene N:" headers (written by SaveFinalStoryboard)
            if (Regex.IsMatch(content, @"^Scene\s+\d+:\s*$", RegexOptions.Multiline))
            {
                var dict = new SortedDictionary<int, string>();
                var parts = Regex.Split(content, @"^Scene\s+(\d+):\s*$", RegexOptions.Multiline);
                for (int i = 1; i + 1 < parts.Length; i += 2)
                {
                    if (int.TryParse(parts[i].Trim(), out var sceneNum))
                    {
                        var text = parts[i + 1].Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            dict[sceneNum] = text;
                    }
                }
                AddLog($"TXT format: Scene N: headers ({dict.Count} prompts)");
                return dict.Values.Select(v => ((string?)null, v)).ToList();
            }

            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (!nonEmpty.Any()) return new();

            // Format B: tab-separated "filename.ext\tprompt"
            var tabPairs = nonEmpty
                .Select(l => { var t = l.IndexOf('\t'); return t > 0 ? (l[..t].Trim(), l[(t + 1)..].Trim()) : (null, (string?)null); })
                .Where(p => p.Item1 != null && imageExts.Contains(Path.GetExtension(p.Item1)) && !string.IsNullOrWhiteSpace(p.Item2))
                .Select(p => (p.Item1, p.Item2!))
                .ToList();

            if (tabPairs.Count >= nonEmpty.Count * 0.7)
            {
                AddLog($"TXT format: tab-separated filename\\tprompt ({tabPairs.Count} pairs)");
                return tabPairs.Select(p => ((string?)p.Item1, p.Item2)).ToList();
            }

            // Format C: "filename.ext: prompt" or "C:\path\file.ext: prompt"
            var colonPairs = nonEmpty
                .Select(l =>
                {
                    var c = l.IndexOf(':');
                    if (c <= 0) return ((string?)null, (string?)null);
                    // Skip Windows drive letter colon (e.g. "C:\...")
                    if (c == 1 && char.IsLetter(l[0]))
                        c = l.IndexOf(':', c + 1);
                    if (c <= 0) return (null, (string?)null);
                    var name = l[..c].Trim();
                    if (!imageExts.Contains(Path.GetExtension(name))) return (null, (string?)null);
                    var prompt = l[(c + 1)..].Trim();
                    return string.IsNullOrWhiteSpace(prompt) ? (null, (string?)null) : (name, prompt);
                })
                .Where(p => p.Item1 != null)
                .Select(p => (p.Item1!, p.Item2!))
                .ToList();

            if (colonPairs.Count >= nonEmpty.Count * 0.7)
            {
                AddLog($"TXT format: colon-separated filename.ext: prompt ({colonPairs.Count} pairs)");
                return colonPairs.Select(p => ((string?)p.Item1, p.Item2)).ToList();
            }

            // Format D: blank-line separated paragraphs (each paragraph = one prompt)
            var paragraphs = Regex.Split(content, @"\r?\n(?:\s*\r?\n)+")
                .Select(b => b.Trim())
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .ToList();

            if (paragraphs.Count > 1)
            {
                AddLog($"TXT format: blank-line separated paragraphs ({paragraphs.Count} prompts)");
                return paragraphs.Select(p => ((string?)null, p)).ToList();
            }

            // Format E: one prompt per line
            AddLog($"TXT format: line-by-line ({nonEmpty.Count} prompts)");
            return nonEmpty.Select(l => ((string?)null, l.Trim())).ToList();
        }

        private async Task ProcessStoryQueueAsync()
        {
            if (!CanProcessStoryQueue) return;

            _storyQueueCts?.Dispose();
            _storyQueueCts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_storyQueueCts.Token, App.ShutdownToken);
            var token = linkedCts.Token;
            NotifyCommandsCanExecuteChanged();
            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("StoryVideo", token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                NotifyCommandsCanExecuteChanged();
                return;
            }

            using (lease)
            {
                try
                {
                    IsProcessingStoryQueue = true;
                    var pendingItems = StoryVideoQueue.Where(item => item.Status == "Pending").ToList();
                    StoryQueueTotal = pendingItems.Count;
                    StoryQueueProgress = 0;

                    AddLog($"=== Starting story video queue processing ({StoryQueueTotal} videos) ===");

                    foreach (var item in pendingItems)
                    {
                        if (token.IsCancellationRequested) break;
                        _storyPauseEvent.Wait(token);

                        CurrentStoryQueueItem = item;
                        item.Status = "Processing";
                        item.StartedAt = DateTime.Now;
                        UpdateStoryQueueStatus();

                        AddLog($"Processing story video {StoryQueueProgress + 1}/{StoryQueueTotal}");

                        try
                        {
                            if (!File.Exists(item.InputImagePath))
                            {
                                item.Status = "Failed";
                                item.ErrorMessage = "Image not found";
                                continue;
                            }

                            // Generate video for story item
                            await GenerateVideoForStoryItemAsync(item);

                            if (HasResult)
                            {
                                item.Status = "Completed";
                                item.OutputVideoPath = ResultVideoPath;
                                item.CompletedAt = DateTime.Now;
                                item.Progress = 100;
                                AddLog($"Story video #{item.Index} completed");
                                SaveStoryQueueToFile();
                                _ = ExtractStoryThumbnailAsync(item);
                            }
                            else
                            {
                                item.Status = "Failed";
                                item.ErrorMessage = "Video generation failed";
                            }

                            HasResult = false;
                        }
                        catch (Exception ex)
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = ex.Message;
                            AddLog($"Error processing story video #{item.Index}: {ex.Message}");
                        }

                        StoryQueueProgress++;
                        UpdateStoryQueueStatus();
                        SaveStoryQueueToFile();
                        await Task.Delay(1000);
                    }

                    AddLog("=== Story queue processing completed ===");
                }
                catch (OperationCanceledException)
                {
                    AddLog("Story queue stopped by user");
                    UpdateStoryQueueStatus();
                    SaveStoryQueueToFile();
                }
                catch (Exception ex)
                {
                    AddLog($"Error processing story queue: {ex.Message}");
                }
                finally
                {
                    IsProcessingStoryQueue = false;
                    IsStoryQueuePaused = false;
                    _storyPauseEvent.Set();
                    CurrentStoryQueueItem = null;
                    NotifyCommandsCanExecuteChanged();
                }
            }
        }

        private async Task GenerateVideoForStoryItemAsync(StoryVideoQueueItem item)
        {
            // Store current values
            var originalPrompt = VideoPrompt;
            var originalFirstFramePath = FirstFrameImagePath;
            var originalLastFramePath = LastFrameImagePath;

            try
            {
                // Set story video mode flag so GenerateVideoAsyncInternal knows to use UseLTXWorkflow
                _isStoryVideoMode = true;

                // Set the prompt from queue item
                VideoPrompt = item.Prompt;

                // For story videos, use the same image as first and last frame
                FirstFrameImagePath = item.InputImagePath;
                LastFrameImagePath = item.InputImagePath;
                LoadFirstFrameImagePreview();
                LoadLastFrameImagePreview();

                AddLog($"Story video #{item.Index}: {item.Prompt.Substring(0, Math.Min(50, item.Prompt.Length))}...");

                // Generate video
                await GenerateVideoAsyncInternal();
            }
            finally
            {
                // Reset story video mode flag
                _isStoryVideoMode = false;

                // Restore original values
                VideoPrompt = originalPrompt;
                FirstFrameImagePath = originalFirstFramePath;
                LastFrameImagePath = originalLastFramePath;
            }
        }

        private async Task ExtractStoryThumbnailAsync(StoryVideoQueueItem item)
        {
            var videoPath = item.OutputVideoPath;
            if (string.IsNullOrEmpty(videoPath)) return;

            var ffmpeg = FindFFmpeg();
            if (ffmpeg == null) return;

            var thumbPath = Path.ChangeExtension(videoPath, null) + "_thumb.jpg";

            // Retry up to 3 times — the file may still be flushing/locked right after generation
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                await Task.Delay(attempt * 2000);

                if (!File.Exists(videoPath)) continue;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = $"-y -i \"{videoPath}\" -vframes 1 -q:v 2 \"{thumbPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        if (File.Exists(thumbPath))
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                                () => item.LoadVideoThumbnail(thumbPath));
                            AddLog($"Thumbnail extracted for clip #{item.Index}");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Thumbnail extraction attempt {attempt} failed for clip #{item.Index}: {ex.Message}");
                }
            }
        }

        private void UpdateStoryQueueStatus()
        {
            var totalCount = StoryVideoQueue.Count;
            var pendingCount = StoryVideoQueue.Count(i => i.Status == "Pending");
            var completedCount = StoryVideoQueue.Count(i => i.Status == "Completed");
            var failedCount = StoryVideoQueue.Count(i => i.Status == "Failed");

            if (totalCount == 0)
            {
                StoryQueueStatus = "No images loaded";
            }
            else if (IsProcessingStoryQueue)
            {
                StoryQueueStatus = $"Processing... ({pendingCount} pending, {completedCount} completed, {failedCount} failed)";
            }
            else
            {
                StoryQueueStatus = $"{totalCount} items ({pendingCount} pending, {completedCount} completed, {failedCount} failed)";
            }

            OnPropertyChanged(nameof(CanProcessStoryQueue));
            OnPropertyChanged(nameof(HasStoryFailedItems));
            OnPropertyChanged(nameof(HasCompletedStoryItems));
            NotifyCommandsCanExecuteChanged();
        }

        private void ClearStoryQueue()
        {
            _storyQueueCts?.Cancel();
            StoryVideoQueue.Clear();
            UpdateStoryQueueStatus();
            AddLog("Story queue cleared");

            // Delete the saved queue file
            var queueFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "queue", "story_video_queue.json");
            if (File.Exists(queueFilePath))
            {
                File.Delete(queueFilePath);
            }
        }

        private void StopStoryQueue()
        {
            _storyQueueCts?.Cancel();
            _storyPauseEvent.Set();
            AddLog("Story queue stop requested");
        }

        private async Task ReprocessAllStoryFailedAsync()
        {
            var failed = StoryVideoQueue.Where(x => x.Status == "Failed").ToList();
            if (!failed.Any()) return;

            foreach (var item in failed)
            {
                item.Status = "Pending";
                item.ErrorMessage = null;
                item.Progress = 0;
            }

            UpdateStoryQueueStatus();
            SaveStoryQueueToFile();
            AddLog($"Reprocessing {failed.Count} failed story item(s)...");

            if (!IsProcessingStoryQueue)
                await ProcessStoryQueueAsync();
        }

        private void PauseStoryQueue()
        {
            IsStoryQueuePaused = true;
            _storyPauseEvent.Reset();
            AddLog("Story queue paused");
        }

        private void ResumeStoryQueue()
        {
            IsStoryQueuePaused = false;
            _storyPauseEvent.Set();
            AddLog("Story queue resumed");
        }

        private void RegenerateStoryItem(StoryVideoQueueItem? item)
        {
            if (item == null) return;
            item.Status = "Pending";
            item.Progress = 0;
            item.ErrorMessage = null;
            item.OutputImagePath = null;
            UpdateStoryQueueStatus();
            SaveStoryQueueToFile();
            AddLog($"Regenerating clip #{item.Index}");
            if (!IsProcessingStoryQueue)
                _ = ProcessStoryQueueAsync();
        }

        private void DeleteStoryItem(StoryVideoQueueItem? item)
        {
            if (item == null) return;
            StoryVideoQueue.Remove(item);
            UpdateStoryQueueStatus();
            SaveStoryQueueToFile();
            AddLog($"Deleted clip #{item.Index}");
        }

        private async Task JoinClipsAsync()
        {
            var completedItems = StoryVideoQueue
                .Where(i => i.Status == "Completed" && !string.IsNullOrEmpty(i.OutputVideoPath) && File.Exists(i.OutputVideoPath))
                .OrderBy(i => i.Index)
                .ToList();

            if (!completedItems.Any())
            {
                System.Windows.MessageBox.Show("No completed video clips to join.", "No Clips", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                System.Windows.MessageBox.Show(
                    "FFmpeg not found. Please install FFmpeg at C:\\ffmpeg\\bin\\ffmpeg.exe or add it to your system PATH.",
                    "FFmpeg Not Found", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            IsJoiningClips = true;
            try
            {
                var tempDir = Path.GetTempPath();
                var listFile = Path.Combine(tempDir, "flippix_story_concat.txt");
                var lines = completedItems.Select(i => $"file '{i.OutputVideoPath!.Replace("\\", "/")}'");
                File.WriteAllLines(listFile, lines, System.Text.Encoding.UTF8);

                var videosFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                Directory.CreateDirectory(videosFolder);
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var outputPath = Path.Combine(videosFolder, $"flippix_story_{timestamp}.mp4");

                AddLog($"Joining {completedItems.Count} clips → {outputPath}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                var errorOutput = new System.Text.StringBuilder();
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };
                process.Start();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync();

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    AddLog($"Joined video saved: {outputPath}");
                    var result = System.Windows.MessageBox.Show(
                        $"Story video saved to:\n{outputPath}\n\nOpen the Videos folder?",
                        "Clips Joined", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                    if (result == System.Windows.MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(videosFolder) { UseShellExecute = true });
                }
                else
                {
                    AddLog($"FFmpeg error (exit {process.ExitCode}): {errorOutput}");
                    System.Windows.MessageBox.Show(
                        $"Failed to join clips (exit code {process.ExitCode}).\nCheck the activity log for details.",
                        "Join Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error joining clips: {ex.Message}");
                System.Windows.MessageBox.Show($"Error joining clips:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsJoiningClips = false;
            }
        }

        #endregion

        #region Queue Persistence

        private string QueueFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "queue", "video_queue.json");
        private string StoryQueueFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "queue", "story_video_queue.json");

        private void SaveQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                {
                    Directory.CreateDirectory(queueDir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(PromptQueue.ToList(), options);
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex)
            {
                AddLog($"Error saving queue to file: {ex.Message}");
            }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (!File.Exists(QueueFilePath))
                {
                    AddLog("No saved queue file found");
                    return;
                }

                var json = File.ReadAllText(QueueFilePath);
                var savedItems = JsonSerializer.Deserialize<List<QueueItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _promptQueue.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.ItemStatus == QueueItemStatus.Processing)
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                        }
                        _promptQueue.Add(item);
                    }
                    UpdateQueueStatus();
                    AddLog($"Queue loaded from file: {_promptQueue.Count} items");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading queue from file: {ex.Message}");
            }
        }

        private void SaveStoryQueueToFile()
        {
            try
            {
                var queueDir = Path.GetDirectoryName(StoryQueueFilePath);
                if (!string.IsNullOrEmpty(queueDir) && !Directory.Exists(queueDir))
                {
                    Directory.CreateDirectory(queueDir);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(StoryVideoQueue.ToList(), options);
                File.WriteAllText(StoryQueueFilePath, json);
            }
            catch (Exception ex)
            {
                AddLog($"Error saving story queue to file: {ex.Message}");
            }
        }

        private void LoadStoryQueueFromFile()
        {
            try
            {
                if (!File.Exists(StoryQueueFilePath))
                {
                    AddLog("No saved story queue file found");
                    return;
                }

                var json = File.ReadAllText(StoryQueueFilePath);
                var savedItems = JsonSerializer.Deserialize<List<StoryVideoQueueItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _storyVideoQueue.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _storyVideoQueue.Add(item);
                    }
                    UpdateStoryQueueStatus();
                    AddLog($"Story queue loaded from file: {_storyVideoQueue.Count} items");

                    // Retroactively extract thumbnails for completed clips that don't have one yet
                    var needThumbnail = _storyVideoQueue
                        .Where(i => i.Status == "Completed" && !i.HasVideoThumbnail && !string.IsNullOrEmpty(i.OutputVideoPath))
                        .ToList();
                    if (needThumbnail.Any())
                        _ = Task.Run(async () => { foreach (var i in needThumbnail) await ExtractStoryThumbnailAsync(i); });
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading story queue from file: {ex.Message}");
            }
        }

        #endregion
    }

    /// <summary>
    /// Data class for story prompt JSON deserialization.
    /// </summary>
    public class StoryPromptData
    {
        public List<string> Prompts { get; set; } = new();
    }
}
