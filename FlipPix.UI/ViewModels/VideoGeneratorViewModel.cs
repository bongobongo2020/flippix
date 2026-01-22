using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels
{
    public class VideoGeneratorViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly FlipPix.UI.Services.LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;

        private string _imageFilePath = string.Empty;
        private BitmapImage? _imagePreviewSource;
        private string _imageInfo = string.Empty;
        private string _videoPrompt = "The subject stands still, eyes full of determination and strength. The camera slowly moves closer or circles around, highlighting the powerful presence and heroic spirit of the character.";
        private string _negativePrompt = "色调艳丽，过曝，静态，细节模糊不清，字幕，风格，作品，画作，画面，静止，整体发灰，最差质量，低质量，JPEG压缩残留，丑陋的，残缺的，多余的手指，画得不好的手部，画得不好的脸部，畸形的，毁容的，形态畸形的肢体，手指融合，静止不动的画面，杂乱的背景，三条腿，背景人很多，倒着走";
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private string _videoInfo = string.Empty;

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
        private System.Threading.CancellationTokenSource? _analysisCancellationTokenSource;

        // Queue properties
        private string _newQueuePrompt = string.Empty;
        private bool _isProcessingQueue = false;
        private string _queueStatus = "Queue is empty";
        private readonly ObservableCollection<QueueItem> _promptQueue = new();

        // Story Video Generator properties
        private string _storyPromptJsonPath = string.Empty;
        private string _storyImagesFolderPath = string.Empty;
        private bool _isProcessingStoryQueue = false;
        private StoryVideoQueueItem? _currentStoryQueueItem;
        private int _storyQueueProgress = 0;
        private int _storyQueueTotal = 0;
        private string _storyQueueStatus = "No images loaded";
        private readonly ObservableCollection<StoryVideoQueueItem> _storyVideoQueue = new();

        // Workflow selection
        private string _selectedWorkflow = "ltx2_i2v";
        private bool _useLTXWorkflow = true;

        // VACE properties
        private string _vacePrompt = string.Empty;
        private string _vaceBackgroundImagePath = string.Empty;
        private BitmapImage? _vaceBackgroundImagePreview;
        private string _vaceForegroundImagePath = string.Empty;
        private BitmapImage? _vaceForegroundImagePreview;
        private string _vaceVideoPath = string.Empty;
        private bool _isProcessingVACE = false;
        private string _vaceBackgroundImageInfo = string.Empty;
        private string _vaceForegroundImageInfo = string.Empty;
        private string _vaceVideoInfo = string.Empty;

        // LTX2Audio properties
        private string _ltx2AudioImagePath = string.Empty;
        private BitmapImage? _ltx2AudioImagePreview;
        private string _ltx2AudioImageInfo = string.Empty;
        private string _ltx2AudioPath = string.Empty;
        private string _ltx2AudioInfo = string.Empty;
        private string _ltx2AudioPrompt = string.Empty;
        private int _ltx2AudioWidth = 1152;
        private int _ltx2AudioHeight = 768;
        private bool _isProcessingLTX2Audio = false;
        private string _ltx2AudioProcessingStatus = string.Empty;
        private double _ltx2AudioProcessingProgress = 0;
        private string _ltx2AudioLogOutput = string.Empty;
        private bool _hasLTX2AudioResult = false;
        private string _ltx2AudioResultPath = string.Empty;
        private string _ltx2AudioVideoInfo = string.Empty;
        private double _ltx2AudioDuration = 0;
        private int _ltx2AudioTotalFrames = 0;

        // Mocha properties
        private string _mochaVideoPath = string.Empty;
        private string _mochaSourceVideoInfo = string.Empty;
        private string _mochaImagePath = string.Empty;
        private BitmapImage? _mochaImagePreview;
        private string _mochaImageInfo = string.Empty;
        private string _mochaPrompt = string.Empty;
        private int _mochaTotalFrames = 0;
        private bool _isProcessingMocha = false;
        private string _mochaProcessingStatus = string.Empty;
        private double _mochaProcessingProgress = 0;
        private string _mochaLogOutput = string.Empty;
        private bool _hasMochaResult = false;
        private string _mochaResultPath = string.Empty;
        private string _mochaVideoInfo = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? PlayRequested;

        public VideoGeneratorViewModel(ComfyUIService comfyUIService, FlipPix.UI.Services.LMStudioService lmStudioService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;

            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResultVideo);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);
            SendToEditCameraCommand = new RelayCommand(SendToEditCamera, () => HasResultVideo);
            NavigateToImageGeneratorCommand = new RelayCommand(NavigateToImageGenerator);
            NavigateToCameraEditCommand = new RelayCommand(NavigateToCameraEdit);

            // New commands for image analysis and queue
            AnalyzeImageCommand = new RelayCommand(async () => await AnalyzeImageAsync());
            SendAnalysisToQueueCommand = new RelayCommand(SendAnalysisToQueue, () => HasAnalysis);
            OpenLMStudioSettingsCommand = new RelayCommand(OpenLMStudioSettings);
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            RemoveFromQueueCommand = new RelayCommand<QueueItem>(RemoveFromQueue);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ReprocessItemCommand = new RelayCommand<QueueItem>(async (item) => await ReprocessItemAsync(item));
            ReprocessAllFailedCommand = new RelayCommand(async () => await ReprocessAllFailedAsync(), () => HasFailedItems);

            // Story Video Generator commands
            SelectStoryPromptJsonCommand = new RelayCommand(SelectStoryPromptJson);
            SelectStoryImagesFolderCommand = new RelayCommand(SelectStoryImagesFolder);
            LoadStoryQueueCommand = new RelayCommand(async () => await LoadStoryQueueAsync(), () => CanLoadStoryQueue);
            ProcessStoryQueueCommand = new RelayCommand(async () => await ProcessStoryQueueAsync(), () => CanProcessStoryQueue);
            ClearStoryQueueCommand = new RelayCommand(ClearStoryQueue, () => StoryVideoQueue.Any());

            // VACE commands
            SelectVACEBackgroundImageCommand = new RelayCommand(SelectVACEBackgroundImage);
            SelectVACEForegroundImageCommand = new RelayCommand(SelectVACEForegroundImage);
            SelectVACEVideoCommand = new RelayCommand(SelectVACEVideo);
            GenerateVACEVideoCommand = new RelayCommand(async () => await GenerateVACEVideoAsync(), () => CanGenerateVACEVideo);

            // LTX2Audio commands
            SelectLTX2AudioImageCommand = new RelayCommand(SelectLTX2AudioImage);
            SelectLTX2AudioCommand = new RelayCommand(SelectLTX2Audio);
            GenerateLTX2AudioVideoCommand = new RelayCommand(async () => await GenerateLTX2AudioVideoAsync(), () => CanGenerateLTX2AudioVideo);
            PlayLTX2AudioVideoCommand = new RelayCommand(PlayLTX2AudioVideo, () => HasLTX2AudioResult);
            OpenLTX2AudioResultFolderCommand = new RelayCommand(OpenLTX2AudioResultFolder, () => HasLTX2AudioResult);
            SendLTX2AudioToEditCameraCommand = new RelayCommand(SendLTX2AudioToEditCamera, () => HasLTX2AudioResult);

            // Mocha commands
            SelectMochaVideoCommand = new RelayCommand(SelectMochaVideo);
            SelectMochaImageCommand = new RelayCommand(SelectMochaImage);
            GenerateMochaVideoCommand = new RelayCommand(async () => await GenerateMochaVideoAsync(), () => CanGenerateMochaVideo);
            PlayMochaVideoCommand = new RelayCommand(PlayMochaVideo, () => HasMochaResult);
            OpenMochaResultFolderCommand = new RelayCommand(OpenMochaResultFolder, () => HasMochaResult);
            SendMochaToEditCameraCommand = new RelayCommand(SendMochaToEditCamera, () => HasMochaResult);

            // Workflow toggle command
            ToggleWorkflowCommand = new RelayCommand(ToggleWorkflow);

            // Subscribe to story video queue collection changes
            _storyVideoQueue.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanProcessStoryQueue));
                CommandManager.InvalidateRequerySuggested();
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

        // Properties
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
                    CommandManager.InvalidateRequerySuggested();
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

        public string VideoPrompt
        {
            get => _videoPrompt;
            set
            {
                _videoPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                CommandManager.InvalidateRequerySuggested();
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

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                _isProcessing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set
            {
                _processingStatus = value;
                OnPropertyChanged();
            }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                _processingProgress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        public string LogOutput
        {
            get => _logOutput;
            set
            {
                _logOutput = value;
                OnPropertyChanged();
            }
        }

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

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            set
            {
                _hasResultVideo = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ResultVideoPath
        {
            get => _resultVideoPath;
            set
            {
                _resultVideoPath = value;
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

        public bool CanGenerateVideo => !string.IsNullOrEmpty(ImageFilePath) &&
                                        File.Exists(ImageFilePath) &&
                                        !string.IsNullOrWhiteSpace(VideoPrompt) &&
                                        !IsProcessing && !IsProcessingQueue;

        public bool HasImage => !string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath);

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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanProcessQueue => PromptQueue.Any() && !IsProcessingQueue && !IsProcessing && HasImage;

        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(NewQueuePrompt) && HasImage;

        public bool HasFailedItems => PromptQueue.Any(x => x.Status == QueueItemStatus.Failed);

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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
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

        // VACE Properties
        public string VacePrompt
        {
            get => _vacePrompt;
            set
            {
                if (_vacePrompt != value)
                {
                    _vacePrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVACEVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string VaceBackgroundImagePath
        {
            get => _vaceBackgroundImagePath;
            set
            {
                if (_vaceBackgroundImagePath != value)
                {
                    _vaceBackgroundImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVACEBackgroundImage));
                    OnPropertyChanged(nameof(CanGenerateVACEVideo));
                    LoadVACEBackgroundImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? VaceBackgroundImagePreview
        {
            get => _vaceBackgroundImagePreview;
            set
            {
                _vaceBackgroundImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string VaceForegroundImagePath
        {
            get => _vaceForegroundImagePath;
            set
            {
                if (_vaceForegroundImagePath != value)
                {
                    _vaceForegroundImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVACEForegroundImage));
                    OnPropertyChanged(nameof(CanGenerateVACEVideo));
                    LoadVACEForegroundImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? VaceForegroundImagePreview
        {
            get => _vaceForegroundImagePreview;
            set
            {
                _vaceForegroundImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string VaceVideoPath
        {
            get => _vaceVideoPath;
            set
            {
                if (_vaceVideoPath != value)
                {
                    _vaceVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVACEVideo));
                    OnPropertyChanged(nameof(CanGenerateVACEVideo));
                    LoadVACEVideoInfo();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsProcessingVACE
        {
            get => _isProcessingVACE;
            set
            {
                if (_isProcessingVACE != value)
                {
                    _isProcessingVACE = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateVACEVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string VaceBackgroundImageInfo
        {
            get => _vaceBackgroundImageInfo;
            set
            {
                if (_vaceBackgroundImageInfo != value)
                {
                    _vaceBackgroundImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string VaceForegroundImageInfo
        {
            get => _vaceForegroundImageInfo;
            set
            {
                if (_vaceForegroundImageInfo != value)
                {
                    _vaceForegroundImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string VaceVideoInfo
        {
            get => _vaceVideoInfo;
            set
            {
                if (_vaceVideoInfo != value)
                {
                    _vaceVideoInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasVACEBackgroundImage => !string.IsNullOrEmpty(VaceBackgroundImagePath) && File.Exists(VaceBackgroundImagePath);
        public bool HasVACEForegroundImage => !string.IsNullOrEmpty(VaceForegroundImagePath) && File.Exists(VaceForegroundImagePath);
        public bool HasVACEVideo => !string.IsNullOrEmpty(VaceVideoPath) && File.Exists(VaceVideoPath);

        public bool CanGenerateVACEVideo => HasVACEBackgroundImage && HasVACEForegroundImage && HasVACEVideo &&
                                         !string.IsNullOrWhiteSpace(VacePrompt) && !IsProcessingVACE && !IsProcessing;

        // LTX2Audio Properties
        public string LTX2AudioImagePath
        {
            get => _ltx2AudioImagePath;
            set
            {
                if (_ltx2AudioImagePath != value)
                {
                    _ltx2AudioImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLTX2AudioImage));
                    OnPropertyChanged(nameof(CanGenerateLTX2AudioVideo));
                    LoadLTX2AudioImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? LTX2AudioImagePreview
        {
            get => _ltx2AudioImagePreview;
            set
            {
                _ltx2AudioImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string LTX2AudioImageInfo
        {
            get => _ltx2AudioImageInfo;
            set
            {
                if (_ltx2AudioImageInfo != value)
                {
                    _ltx2AudioImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LTX2AudioPath
        {
            get => _ltx2AudioPath;
            set
            {
                if (_ltx2AudioPath != value)
                {
                    _ltx2AudioPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLTX2Audio));
                    OnPropertyChanged(nameof(CanGenerateLTX2AudioVideo));
                    LoadLTX2AudioInfo();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string LTX2AudioInfo
        {
            get => _ltx2AudioInfo;
            set
            {
                if (_ltx2AudioInfo != value)
                {
                    _ltx2AudioInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LTX2AudioPrompt
        {
            get => _ltx2AudioPrompt;
            set
            {
                if (_ltx2AudioPrompt != value)
                {
                    _ltx2AudioPrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateLTX2AudioVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public int LTX2AudioWidth
        {
            get => _ltx2AudioWidth;
            set
            {
                if (_ltx2AudioWidth != value)
                {
                    _ltx2AudioWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        public int LTX2AudioHeight
        {
            get => _ltx2AudioHeight;
            set
            {
                if (_ltx2AudioHeight != value)
                {
                    _ltx2AudioHeight = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsProcessingLTX2Audio
        {
            get => _isProcessingLTX2Audio;
            set
            {
                if (_isProcessingLTX2Audio != value)
                {
                    _isProcessingLTX2Audio = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateLTX2AudioVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string LTX2AudioProcessingStatus
        {
            get => _ltx2AudioProcessingStatus;
            set
            {
                if (_ltx2AudioProcessingStatus != value)
                {
                    _ltx2AudioProcessingStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double LTX2AudioProcessingProgress
        {
            get => _ltx2AudioProcessingProgress;
            set
            {
                if (_ltx2AudioProcessingProgress != value)
                {
                    _ltx2AudioProcessingProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LTX2AudioProgressPercentage));
                }
            }
        }

        public string LTX2AudioProgressPercentage => $"{LTX2AudioProcessingProgress:F0}%";

        public string LTX2AudioLogOutput
        {
            get => _ltx2AudioLogOutput;
            set
            {
                if (_ltx2AudioLogOutput != value)
                {
                    _ltx2AudioLogOutput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasLTX2AudioResult
        {
            get => _hasLTX2AudioResult;
            set
            {
                if (_hasLTX2AudioResult != value)
                {
                    _hasLTX2AudioResult = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string LTX2AudioResultPath
        {
            get => _ltx2AudioResultPath;
            set
            {
                if (_ltx2AudioResultPath != value)
                {
                    _ltx2AudioResultPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LTX2AudioVideoInfo
        {
            get => _ltx2AudioVideoInfo;
            set
            {
                if (_ltx2AudioVideoInfo != value)
                {
                    _ltx2AudioVideoInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public double LTX2AudioDuration
        {
            get => _ltx2AudioDuration;
            set
            {
                if (_ltx2AudioDuration != value)
                {
                    _ltx2AudioDuration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LTX2AudioEstimatedDuration));
                    CalculateLTX2AudioTotalFrames();
                }
            }
        }

        public int LTX2AudioTotalFrames
        {
            get => _ltx2AudioTotalFrames;
            set
            {
                if (_ltx2AudioTotalFrames != value)
                {
                    _ltx2AudioTotalFrames = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LTX2AudioEstimatedDuration => LTX2AudioDuration > 0 ? $"{LTX2AudioDuration:F1} seconds ({LTX2AudioTotalFrames} frames at 24 FPS)" : "No audio loaded";

        public bool HasLTX2AudioImage => !string.IsNullOrEmpty(LTX2AudioImagePath) && File.Exists(LTX2AudioImagePath);
        public bool HasLTX2Audio => !string.IsNullOrEmpty(LTX2AudioPath) && File.Exists(LTX2AudioPath);

        public bool CanGenerateLTX2AudioVideo => HasLTX2AudioImage && HasLTX2Audio &&
                                                !string.IsNullOrWhiteSpace(LTX2AudioPrompt) &&
                                                !IsProcessingLTX2Audio && !IsProcessing && !IsProcessingVACE;

        // Mocha Properties
        public string MochaVideoPath
        {
            get => _mochaVideoPath;
            set
            {
                if (_mochaVideoPath != value)
                {
                    _mochaVideoPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasMochaVideo));
                    OnPropertyChanged(nameof(CanGenerateMochaVideo));
                    LoadMochaVideoInfo();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string MochaSourceVideoInfo
        {
            get => _mochaSourceVideoInfo;
            set
            {
                if (_mochaSourceVideoInfo != value)
                {
                    _mochaSourceVideoInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string MochaImagePath
        {
            get => _mochaImagePath;
            set
            {
                if (_mochaImagePath != value)
                {
                    _mochaImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasMochaImage));
                    OnPropertyChanged(nameof(CanGenerateMochaVideo));
                    LoadMochaImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? MochaImagePreview
        {
            get => _mochaImagePreview;
            set
            {
                _mochaImagePreview = value;
                OnPropertyChanged();
            }
        }

        public string MochaImageInfo
        {
            get => _mochaImageInfo;
            set
            {
                if (_mochaImageInfo != value)
                {
                    _mochaImageInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public string MochaPrompt
        {
            get => _mochaPrompt;
            set
            {
                if (_mochaPrompt != value)
                {
                    _mochaPrompt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateMochaVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public int MochaTotalFrames
        {
            get => _mochaTotalFrames;
            set
            {
                if (_mochaTotalFrames != value)
                {
                    _mochaTotalFrames = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MochaTotalChunks));
                }
            }
        }

        public int MochaTotalChunks => MochaTotalFrames > 0 ? (int)Math.Ceiling((double)MochaTotalFrames / 81) : 0;

        public bool IsProcessingMocha
        {
            get => _isProcessingMocha;
            set
            {
                if (_isProcessingMocha != value)
                {
                    _isProcessingMocha = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanGenerateMochaVideo));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string MochaProcessingStatus
        {
            get => _mochaProcessingStatus;
            set
            {
                if (_mochaProcessingStatus != value)
                {
                    _mochaProcessingStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double MochaProcessingProgress
        {
            get => _mochaProcessingProgress;
            set
            {
                if (_mochaProcessingProgress != value)
                {
                    _mochaProcessingProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MochaProgressPercentage));
                }
            }
        }

        public string MochaProgressPercentage => $"{MochaProcessingProgress:F0}%";

        public string MochaLogOutput
        {
            get => _mochaLogOutput;
            set
            {
                if (_mochaLogOutput != value)
                {
                    _mochaLogOutput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasMochaResult
        {
            get => _hasMochaResult;
            set
            {
                if (_hasMochaResult != value)
                {
                    _hasMochaResult = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string MochaResultPath
        {
            get => _mochaResultPath;
            set
            {
                if (_mochaResultPath != value)
                {
                    _mochaResultPath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string MochaResultVideoInfo
        {
            get => _mochaVideoInfo;
            set
            {
                if (_mochaVideoInfo != value)
                {
                    _mochaVideoInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasMochaVideo => !string.IsNullOrEmpty(MochaVideoPath) && File.Exists(MochaVideoPath);
        public bool HasMochaImage => !string.IsNullOrEmpty(MochaImagePath) && File.Exists(MochaImagePath);

        public bool CanGenerateMochaVideo => HasMochaVideo && HasMochaImage &&
                                             !string.IsNullOrWhiteSpace(MochaPrompt) &&
                                             !IsProcessingMocha && !IsProcessing && !IsProcessingVACE && !IsProcessingLTX2Audio;

        // Workflow Selection Properties
        public string SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                if (_selectedWorkflow != value)
                {
                    _selectedWorkflow = value;
                    UseLTXWorkflow = value == "ltx2_i2v";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    CommandManager.InvalidateRequerySuggested();

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
                    // Update the internal field directly to avoid circular dependency
                    _selectedWorkflow = value ? "ltx2_i2v" : "painter";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    CommandManager.InvalidateRequerySuggested();

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

        public string WorkflowDisplay => UseLTXWorkflow ? "LTXV (LTX-2_image2video_distilledAPI.json)" : "Painter (painteri2vAPI.json)";
        public string WorkflowIndicator => UseLTXWorkflow ? "🟢 LTXV" : "🔵 Painter";

        private void ToggleWorkflow()
        {
            UseLTXWorkflow = !UseLTXWorkflow;
        }

        private void OpenLMStudioSettings()
        {
            try
            {
                // Use fully qualified names to avoid WinForms/WPF conflicts
                var settingsWindow = new System.Windows.Window
                {
                    Title = "LM Studio Settings",
                    Width = 550,
                    Height = 500,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
                    Owner = System.Windows.Application.Current.MainWindow,
                    ResizeMode = System.Windows.ResizeMode.NoResize
                };

                var mainPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };

                // Title
                var title = new System.Windows.Controls.TextBlock
                {
                    Text = "LM Studio Configuration",
                    FontSize = 16,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Margin = new System.Windows.Thickness(0, 0, 0, 20)
                };
                mainPanel.Children.Add(title);

                // LM Studio URL
                var urlLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "LM Studio URL:",
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new System.Windows.Thickness(0, 0, 0, 5)
                };
                mainPanel.Children.Add(urlLabel);

                var currentUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                var urlTextBox = new System.Windows.Controls.TextBox
                {
                    Text = currentUrl,
                    Margin = new System.Windows.Thickness(0, 0, 0, 10),
                    Padding = new System.Windows.Thickness(8)
                };
                mainPanel.Children.Add(urlTextBox);

                // Test Connection and Fetch Models button
                var testButton = new System.Windows.Controls.Button
                {
                    Content = "🔍 Test Connection & Fetch Models",
                    Height = 35,
                    Margin = new System.Windows.Thickness(0, 0, 0, 10),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 123, 255)),
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left
                };
                mainPanel.Children.Add(testButton);

                // Status text for connection test
                var statusText = new System.Windows.Controls.TextBlock
                {
                    Text = "",
                    FontSize = 11,
                    Margin = new System.Windows.Thickness(0, 0, 0, 15),
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                mainPanel.Children.Add(statusText);

                // Model Selection
                var modelLabel = new System.Windows.Controls.TextBlock
                {
                    Text = "Select Model:",
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new System.Windows.Thickness(0, 0, 0, 5)
                };
                mainPanel.Children.Add(modelLabel);

                var currentModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? "";
                var modelComboBox = new System.Windows.Controls.ComboBox
                {
                    Margin = new System.Windows.Thickness(0, 0, 0, 15),
                    Padding = new System.Windows.Thickness(8),
                    Height = 35,
                    IsEditable = false
                };
                mainPanel.Children.Add(modelComboBox);

                // Info text
                var infoText = new System.Windows.Controls.TextBlock
                {
                    Text = "Click 'Test Connection & Fetch Models' to load available models from LM Studio.",
                    FontSize = 11,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    FontStyle = System.Windows.FontStyles.Italic,
                    Margin = new System.Windows.Thickness(0, 0, 0, 15)
                };
                mainPanel.Children.Add(infoText);

                // Buttons
                var buttonPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };

                var saveButton = new System.Windows.Controls.Button
                {
                    Content = "Save",
                    Width = 80,
                    Height = 30,
                    Margin = new System.Windows.Thickness(0, 0, 10, 0),
                    Background = System.Windows.Media.Brushes.Green,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var cancelButton = new System.Windows.Controls.Button
                {
                    Content = "Cancel",
                    Width = 80,
                    Height = 30,
                    Background = System.Windows.Media.Brushes.Gray,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                buttonPanel.Children.Add(saveButton);
                buttonPanel.Children.Add(cancelButton);
                mainPanel.Children.Add(buttonPanel);

                settingsWindow.Content = mainPanel;

                // Test Connection button click handler
                testButton.Click += async (s, e) =>
                {
                    var url = urlTextBox.Text.Trim();
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        statusText.Text = "❌ Please enter a URL first.";
                        statusText.Foreground = System.Windows.Media.Brushes.Red;
                        return;
                    }

                    testButton.IsEnabled = false;
                    statusText.Text = "🔄 Testing connection and fetching models...";
                    statusText.Foreground = System.Windows.Media.Brushes.Blue;

                    try
                    {
                        // Update the service's base URL temporarily
                        await _lmStudioService.SetBaseUrlAsync(url);

                        // Fetch available models
                        var models = await _lmStudioService.GetAvailableModelsAsync(System.Threading.CancellationToken.None);

                        if (models != null && models.Any())
                        {
                            modelComboBox.Items.Clear();
                            foreach (var model in models)
                            {
                                modelComboBox.Items.Add(model.Name);
                            }

                            // Try to select the currently saved model
                            if (!string.IsNullOrEmpty(currentModel))
                            {
                                var selectedIndex = -1;
                                for (int i = 0; i < models.Count; i++)
                                {
                                    if (models[i].Name.Equals(currentModel, StringComparison.OrdinalIgnoreCase))
                                    {
                                        selectedIndex = i;
                                        break;
                                    }
                                }
                                if (selectedIndex >= 0)
                                {
                                    modelComboBox.SelectedIndex = selectedIndex;
                                }
                                else if (modelComboBox.Items.Count > 0)
                                {
                                    modelComboBox.SelectedIndex = 0;
                                }
                            }
                            else if (modelComboBox.Items.Count > 0)
                            {
                                modelComboBox.SelectedIndex = 0;
                            }

                            statusText.Text = $"✅ Found {models.Count} model(s)";
                            statusText.Foreground = System.Windows.Media.Brushes.Green;
                            infoText.Text = $"Available models: {string.Join(", ", models.Take(3).Select(m => System.IO.Path.GetFileNameWithoutExtension(m.Name)))}{(models.Count > 3 ? "..." : "")}";
                        }
                        else
                        {
                            statusText.Text = "⚠️ No models found. Make sure a model is loaded in LM Studio.";
                            statusText.Foreground = System.Windows.Media.Brushes.Orange;
                            modelComboBox.Items.Clear();
                        }
                    }
                    catch (Exception ex)
                    {
                        statusText.Text = $"❌ Connection failed: {ex.Message}";
                        statusText.Foreground = System.Windows.Media.Brushes.Red;
                        modelComboBox.Items.Clear();
                    }
                    finally
                    {
                        testButton.IsEnabled = true;
                    }
                };

                // Save button click handler
                saveButton.Click += (s, e) =>
                {
                    var newUrl = urlTextBox.Text.Trim();
                    var newModel = modelComboBox.SelectedItem?.ToString()?.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(newUrl))
                    {
                        System.Windows.MessageBox.Show("Please enter a valid URL.", "Invalid URL",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    // Update settings
                    var settings = _settingsService.Settings;
                    if (settings != null)
                    {
                        if (settings.LMStudioSettings == null)
                        {
                            settings.LMStudioSettings = new Core.Models.LMStudioSettings();
                        }

                        settings.LMStudioSettings.BaseUrl = newUrl;
                        settings.LMStudioSettings.SelectedModel = newModel;
                        _settingsService.SaveSettings(settings);
                    }

                    AddLog($"LM Studio settings updated: URL={newUrl}, Model={newModel ?? "(auto-detect)"}");
                    System.Windows.MessageBox.Show("Settings saved successfully!", "Success",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    settingsWindow.Close();
                };

                // Cancel button click handler
                cancelButton.Click += (s, e) => settingsWindow.Close();

                // Auto-fetch models on open
                settingsWindow.Loaded += async (s, e) =>
                {
                    // Dispatch to avoid blocking the UI
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(100); // Small delay to let the window fully load
                        testButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    });
                };

                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                AddLog($"Error opening LM Studio settings: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening settings:\n{ex.Message}", "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        // Commands
        public ICommand SelectImageCommand { get; }
        public ICommand GenerateVideoCommand { get; }
        public ICommand PlayVideoCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SendToEditCameraCommand { get; }
        public ICommand NavigateToImageGeneratorCommand { get; }
        public ICommand NavigateToCameraEditCommand { get; }

        // New commands
        public ICommand AnalyzeImageCommand { get; }
        public ICommand SendAnalysisToQueueCommand { get; }
        public ICommand OpenLMStudioSettingsCommand { get; }
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ReprocessItemCommand { get; }
        public ICommand ReprocessAllFailedCommand { get; }

        // Story Video Generator commands
        public ICommand SelectStoryPromptJsonCommand { get; }
        public ICommand SelectStoryImagesFolderCommand { get; }
        public ICommand LoadStoryQueueCommand { get; }
        public ICommand ProcessStoryQueueCommand { get; }
        public ICommand ClearStoryQueueCommand { get; }

        // VACE commands
        public ICommand SelectVACEBackgroundImageCommand { get; }
        public ICommand SelectVACEForegroundImageCommand { get; }
        public ICommand SelectVACEVideoCommand { get; }
        public ICommand GenerateVACEVideoCommand { get; }

        // LTX2Audio commands
        public ICommand SelectLTX2AudioImageCommand { get; }
        public ICommand SelectLTX2AudioCommand { get; }
        public ICommand GenerateLTX2AudioVideoCommand { get; }
        public ICommand PlayLTX2AudioVideoCommand { get; }
        public ICommand OpenLTX2AudioResultFolderCommand { get; }
        public ICommand SendLTX2AudioToEditCameraCommand { get; }

        // Mocha commands
        public ICommand SelectMochaVideoCommand { get; }
        public ICommand SelectMochaImageCommand { get; }
        public ICommand GenerateMochaVideoCommand { get; }
        public ICommand PlayMochaVideoCommand { get; }
        public ICommand OpenMochaResultFolderCommand { get; }
        public ICommand SendMochaToEditCameraCommand { get; }

        // Workflow toggle command
        public ICommand ToggleWorkflowCommand { get; }

        // Methods
        private void SelectImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Input Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ImageFilePath = openFileDialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(openFileDialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected image: {Path.GetFileName(ImageFilePath)}");
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
                bitmap.UriSource = new Uri(ImageFilePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ImagePreviewSource = bitmap;

                var fileInfo = new FileInfo(ImageFilePath);
                ImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";

                // Set aspect ratio based on image orientation
                if (bitmap.PixelWidth > bitmap.PixelHeight)
                {
                    // Landscape: 832 width x 480 height
                    Width = 832;
                    Height = 480;
                    AddLog($"Landscape image detected: Output size set to {Width}x{Height}");
                }
                else
                {
                    // Portrait: 480 width x 832 height
                    Width = 480;
                    Height = 832;
                    AddLog($"Portrait image detected: Output size set to {Width}x{Height}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                ImageInfo = "Error loading image";
            }
        }

        private async Task GenerateVideoAsync()
        {
            if (!CanGenerateVideo) return;

            try
            {
                await GenerateVideoAsyncInternal();
            }
            catch (Exception ex)
            {
                // Exception already logged in GenerateVideoAsyncInternal
                // Show MessageBox for single video generation only
                System.Windows.MessageBox.Show($"An error occurred during video generation:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task GenerateVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting video generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultVideo = false;
                ResultVideoPath = string.Empty;
                VideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Input image: {Path.GetFileName(ImageFilePath)}");
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
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
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
                else
                {
                    AddLog("ComfyUI already connected");
                }

                // Load workflow
                var workflowFileName = UseLTXWorkflow ? "LTX-2_image2video_distilledAPI.json" : "painteri2vAPI.json";
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", workflowFileName);

                AddLog($"=== WORKFLOW DEBUG INFO ===");
                AddLog($"UseLTXWorkflow: {UseLTXWorkflow}");
                AddLog($"SelectedWorkflow: {SelectedWorkflow}");
                AddLog($"Workflow file: {workflowFileName}");
                AddLog($"Full path: {workflowPath}");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {workflowFileName} ({WorkflowDisplay})");
                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Debug: Check if workflow contains expected nodes
                var workflowDictDebug = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflowDictDebug != null)
                {
                    var hasNode131 = workflowDictDebug.ContainsKey("131");
                    var hasNode167 = workflowDictDebug.ContainsKey("167");
                    var hasNode98 = workflowDictDebug.ContainsKey("98");
                    var hasNode121 = workflowDictDebug.ContainsKey("121");
                    var hasNode92_3 = workflowDictDebug.ContainsKey("92:3");

                    // Check node types for accurate detection
                    string node131Type = "N/A";
                    string node167Type = "N/A";
                    string node98Type = "N/A";

                    if (hasNode131 && workflowDictDebug["131"].TryGetProperty("class_type", out var type131))
                        node131Type = type131.GetString() ?? "N/A";
                    if (hasNode167 && workflowDictDebug["167"].TryGetProperty("class_type", out var type167))
                        node167Type = type167.GetString() ?? "N/A";
                    if (hasNode98 && workflowDictDebug["98"].TryGetProperty("class_type", out var type98))
                        node98Type = type98.GetString() ?? "N/A";

                    AddLog($"Workflow contains node 131 ({node131Type}): {hasNode131}");
                    AddLog($"Workflow contains node 167 ({node167Type}): {hasNode167}");
                    AddLog($"Workflow contains node 98 ({node98Type}): {hasNode98}");
                    AddLog($"Workflow contains node 121 (CLIPTextEncode): {hasNode121}");
                    AddLog($"Workflow contains node 92:3 (CLIPTextEncode): {hasNode92_3}");

                    if (UseLTXWorkflow && node131Type == "PainterI2V")
                    {
                        AddLog($"WARNING: LTXV workflow selected but node 131 is PainterI2V!");
                    }
                }
                AddLog($"=== END WORKFLOW DEBUG INFO ===");

                // Upload input image
                ProcessingStatus = "Uploading input image...";
                ProcessingProgress = 10;
                AddLog("Uploading input image to ComfyUI...");

                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImageFilePath);

                // Validate that upload succeeded
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLog("ERROR: Image upload failed - no filename returned from ComfyUI");
                    System.Windows.MessageBox.Show(
                        "Failed to upload image to ComfyUI. Please check the ComfyUI console for errors.",
                        "Upload Failed",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Image uploaded: {uploadedImageName}");

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName);

                // Debug: Log the updated workflow image node
                AddLog("=== DEBUG: Verifying workflow before execution ===");
                var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updatedWorkflow.GetRawText());
                if (workflowDict != null && workflowDict.ContainsKey("143"))
                {
                    var node143 = workflowDict["143"];
                    AddLog($"Node 143 (LoadImage): {node143.GetRawText()}");
                }

                // Execute workflow
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 30;
                AddLog("Executing video generation workflow...");

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

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving video...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated video...");

                // Wait for the video to be saved - use direct folder checking since WebSocket may disconnect
                var outputVideo = await WaitForVideoInOutputFolderAsync(promptId);

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    HasResultVideo = true;

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
                    AddLog("WARNING: No output video found after waiting - continuing queue processing");
                    ProcessingStatus = "No output generated";
                    // Removed blocking MessageBox to allow queue processing to continue automatically
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                AddLog($"Stack trace: {ex.StackTrace}");
                ProcessingStatus = "Error occurred";
                StatusBarMessage = "Error during video generation";
                throw; // Re-throw to allow queue processing to handle ComfyUI restart
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Update image input - check multiple possible nodes
            // LTXV distilled uses node "167", LTXV original uses "98", Painter uses "97", "143", "119"
            string[] imageNodes = UseLTXWorkflow ? new[] { "167", "98" } : new[] { "97", "143", "119" };
            bool imageNodeFound = false;

            foreach (var nodeId in imageNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["image"] = inputImageName;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (LoadImage) - Image updated to: {inputImageName}");
                            imageNodeFound = true;
                        }
                    }
                }
            }

            if (!imageNodeFound)
            {
                AddLog($"WARNING: No image input nodes found in workflow!");
            }

            // Update positive prompt - check multiple possible nodes
            // LTXV distilled uses "121", LTXV original uses "92:3", Painter uses "93", "62", "6"
            string[] positivePromptNodes = UseLTXWorkflow ? new[] { "121", "92:3" } : new[] { "93", "62", "6" };
            foreach (var nodeId in positivePromptNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["text"] = VideoPrompt;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (Positive Prompt) - Prompt updated");
                        }
                    }
                }
            }

            // Update negative prompt - check multiple possible nodes
            string[] negativePromptNodes = { "89", "7" };
            foreach (var nodeId in negativePromptNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["text"] = NegativePrompt;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (Negative Prompt) - Prompt updated");
                        }
                    }
                }
            }

            // Update WanImageToVideo parameters (node 98) - CRITICAL for painter workflow
            if (!UseLTXWorkflow && workflowDict.ContainsKey("98"))
            {
                var node98 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["98"].GetRawText());
                if (node98 != null && node98.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node98["inputs"]));
                    if (inputs != null)
                    {
                        inputs["length"] = VideoLength;
                        inputs["width"] = Width;
                        inputs["height"] = Height;
                        node98["inputs"] = inputs;
                        workflowDict["98"] = JsonSerializer.SerializeToElement(node98);
                        AddLog($"✓ Node 98 (WanImageToVideo) - Length: {VideoLength}, Width: {Width}, Height: {Height}");
                    }
                }
            }

            // Update LTXV video parameters (nodes 92:62, 92:22, 75, 92:97 for original; 112, 131 for distilled) - for LTXV workflow
            if (UseLTXWorkflow)
            {
                // LTXV distilled workflow uses node 112 for video length
                if (workflowDict.ContainsKey("112"))
                {
                    var node112 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["112"].GetRawText());
                    if (node112 != null && node112.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node112["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = VideoLength;
                            node112["inputs"] = inputs;
                            workflowDict["112"] = JsonSerializer.SerializeToElement(node112);
                            AddLog($"✓ Node 112 (PrimitiveInt) - Video Length: {VideoLength}");
                        }
                    }
                }

                // LTXV distilled workflow uses node 131 for frame rate
                if (workflowDict.ContainsKey("131"))
                {
                    var node131 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["131"].GetRawText());
                    if (node131 != null && node131.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node131["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = Fps;
                            node131["inputs"] = inputs;
                            workflowDict["131"] = JsonSerializer.SerializeToElement(node131);
                            AddLog($"✓ Node 131 (PrimitiveInt) - Frame Rate: {Fps}");
                        }
                    }
                }

                // Original LTXV workflow: Update frame length (node 92:62)
                if (workflowDict.ContainsKey("92:62"))
                {
                    var node92_62 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["92:62"].GetRawText());
                    if (node92_62 != null && node92_62.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node92_62["inputs"]));
                        if (inputs != null && inputs.ContainsKey("value"))
                        {
                            inputs["value"] = VideoLength;
                            node92_62["inputs"] = inputs;
                            workflowDict["92:62"] = JsonSerializer.SerializeToElement(node92_62);
                            AddLog($"✓ Node 92:62 (PrimitiveInt) - Video Length: {VideoLength}");
                        }
                    }
                }

                // Original LTXV workflow: Update frame rate in LTXVConditioning (node 92:22)
                if (workflowDict.ContainsKey("92:22"))
                {
                    var node92_22 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["92:22"].GetRawText());
                    if (node92_22 != null && node92_22.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node92_22["inputs"]));
                        if (inputs != null && inputs.ContainsKey("frame_rate"))
                        {
                            inputs["frame_rate"] = Fps;
                            node92_22["inputs"] = inputs;
                            workflowDict["92:22"] = JsonSerializer.SerializeToElement(node92_22);
                            AddLog($"✓ Node 92:22 (LTXVConditioning) - FPS: {Fps}");
                        }
                    }
                }

                // Original LTXV workflow: Update FPS in SaveVideo (node 75)
                if (workflowDict.ContainsKey("75"))
                {
                    var node75 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75"].GetRawText());
                    if (node75 != null && node75.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node75["inputs"]));
                        if (inputs != null && inputs.ContainsKey("video"))
                        {
                            // Node 75's video input connects to node 92:97, we need to update 92:97's fps
                        }
                    }
                }

                // Original LTXV workflow: Update FPS in CreateVideo (node 92:97)
                if (workflowDict.ContainsKey("92:97"))
                {
                    var node92_97 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["92:97"].GetRawText());
                    if (node92_97 != null && node92_97.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node92_97["inputs"]));
                        if (inputs != null && inputs.ContainsKey("fps"))
                        {
                            inputs["fps"] = Fps;
                            node92_97["inputs"] = inputs;
                            workflowDict["92:97"] = JsonSerializer.SerializeToElement(node92_97);
                            AddLog($"✓ Node 92:97 (CreateVideo) - FPS: {Fps}");
                        }
                    }
                }
            }

            // Update PainterI2V parameters (node 131) - CRITICAL: Must match WanImageToVideo length!
            // Only for Painter workflow
            if (!UseLTXWorkflow && workflowDict.ContainsKey("131"))
            {
                var node131 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["131"].GetRawText());
                if (node131 != null && node131.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node131["inputs"]));
                    if (inputs != null)
                    {
                        inputs["length"] = VideoLength;  // Must match node 98!
                        inputs["width"] = Width;
                        inputs["height"] = Height;
                        node131["inputs"] = inputs;
                        workflowDict["131"] = JsonSerializer.SerializeToElement(node131);
                        AddLog($"✓ Node 131 (PainterI2V) - Length: {VideoLength}, Width: {Width}, Height: {Height}");
                    }
                }
            }

            // Update FPS (node 94 or 135) - Only for Painter workflow
            if (!UseLTXWorkflow)
            {
                string[] fpsNodes = { "94", "135" };
            foreach (var nodeId in fpsNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null && inputs.ContainsKey("frame_rate"))
                        {
                            inputs["frame_rate"] = Fps;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (VideoCombine) - FPS: {Fps}");
                        }
                    }
                }
            }
            }

            // Update steps and CFG for KSamplerAdvanced nodes (132 and 133 for painter workflow)
            // Note: LTXV workflow uses fixed sigmas and doesn't expose these parameters in the same way
            if (!UseLTXWorkflow)
            {
                string[] ksamplerNodes = { "85", "86", "132", "133" };
            foreach (var nodeId in ksamplerNodes)
            {
                if (workflowDict.ContainsKey(nodeId))
                {
                    var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                    if (node != null && node.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node["inputs"]));
                        if (inputs != null)
                        {
                            inputs["steps"] = Steps;
                            inputs["cfg"] = Cfg;
                            if (Seed > 0)
                            {
                                inputs["noise_seed"] = Seed;
                            }
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (KSampler) - Steps: {Steps}, CFG: {Cfg}");
                        }
                    }
                }
            }
            }

            var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);
            AddLog("Workflow parameters updated successfully");
            return updatedWorkflow;
        }

        private async Task<string?> GetOutputVideoFromComfyUI(string promptId)
        {
            try
            {
                AddLog("=== GetOutputVideoFromComfyUI START ===");

                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                AddLog($"BaseUrl from settings: {baseUrl}");

                // Parse the URL to get server and port
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;
                var actualPort = uri.Port.ToString();

                // Check if ComfyUI is running locally or remotely
                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"Parsed server: {actualServer}:{actualPort}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    AddLog("Detected remote ComfyUI server, accessing generated video...");

                    // For remote ComfyUI, require a network path to the output folder
                    var remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;

                    // Check if we have a valid remote output path
                    if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                    {
                        AddLog($"Using remote output folder: {remoteOutputPath}");
                        return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                    }

                    // If we don't have a valid remote output path, require user to configure it
                    if (string.IsNullOrEmpty(remoteOutputPath))
                    {
                        AddLog("Remote output folder not configured for remote ComfyUI server.");

                        var result = System.Windows.MessageBox.Show(
                            "ComfyUI is running on a remote server.\n\n" +
                            "To retrieve generated videos, you must configure the network path to the remote ComfyUI output folder.\n\n" +
                            "Would you like to configure it now?",
                            "Remote Output Folder Required",
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxImage.Warning);

                        if (result == System.Windows.MessageBoxResult.OK)
                        {
                            ShowRemoteOutputFolderSetup();
                            // After setup, check again if the folder was configured
                            remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;
                            if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                            {
                                AddLog($"Remote output folder configured: {remoteOutputPath}");
                                return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                            }
                        }
                    }
                    else
                    {
                        // Remote output path is configured but not accessible
                        AddLog($"Remote output folder not accessible: {remoteOutputPath}");

                        var result = System.Windows.MessageBox.Show(
                            "The configured remote output folder is not accessible.\n\n" +
                            "Please check the network path and permissions.\n\n" +
                            "Would you like to reconfigure it?",
                            "Remote Output Folder Not Accessible",
                            System.Windows.MessageBoxButton.OKCancel,
                            System.Windows.MessageBoxImage.Error);

                        if (result == System.Windows.MessageBoxResult.OK)
                        {
                            ShowRemoteOutputFolderSetup();
                            // After setup, check again if the folder was configured
                            remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;
                            if (!string.IsNullOrEmpty(remoteOutputPath) && Directory.Exists(remoteOutputPath))
                            {
                                AddLog($"Remote output folder reconfigured: {remoteOutputPath}");
                                return await CopyVideoFromRemoteFolder(remoteOutputPath, promptId);
                            }
                        }
                    }

                    // If we get here, no valid remote output folder is available
                    AddLog("ERROR: Remote output folder is required for remote ComfyUI server access.");
                    AddLog("Video retrieval failed - please configure the remote output folder and try again.");
                    return null;
                }
                else
                {
                    // Local ComfyUI - check the output folder directly
                    var settings = _settingsService.Settings;
                    if (settings == null || string.IsNullOrEmpty(settings.OutputFolderPath))
                    {
                        AddLog("ERROR: ComfyUI output path not configured");
                        return null;
                    }

                    var outputFolder = Path.Combine(settings.OutputFolderPath, "video");
                    if (!Directory.Exists(outputFolder))
                    {
                        AddLog($"ERROR: Output folder not found: {outputFolder}");
                        return null;
                    }

                    // Get the most recent video file
                    var videoFiles = Directory.GetFiles(outputFolder, "*.mp4")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .ToList();

                    if (videoFiles.Any())
                    {
                        var latestVideo = videoFiles.First();
                        AddLog($"Found output video: {Path.GetFileName(latestVideo)}");
                        return latestVideo;
                    }

                    AddLog("No video files found in output folder");
                }

                return null;
            }
            catch (Exception ex)
            {
                AddLog($"ERROR getting output video: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Waits for the video to appear in the output folder and verifies it's complete.
        /// This approach doesn't rely on ComfyUI API calls since WebSocket may disconnect.
        /// </summary>
        private async Task<string?> WaitForVideoInOutputFolderAsync(string promptId)
        {
            var settings = _settingsService.Settings;
            if (settings == null)
            {
                AddLog("ERROR: Settings not available");
                return null;
            }

            // Determine if ComfyUI is remote
            string baseUrl = ComfyUIServer == "127.0.0.1" ? $"http://{ComfyUIServer}:{ComfyUIPort}" : settings.BaseUrl;
            bool isRemoteComfyUI = IsComfyUIRemote(new Uri(baseUrl).Host);

            // Determine the correct output folder path
            string outputFolder;
            if (isRemoteComfyUI)
            {
                // Remote ComfyUI - use the remote output folder path
                if (string.IsNullOrEmpty(settings.RemoteOutputFolderPath))
                {
                    AddLog("ERROR: Remote ComfyUI output path not configured in settings");
                    return null;
                }
                outputFolder = Path.Combine(settings.RemoteOutputFolderPath, "video");
                AddLog($"Using remote ComfyUI output folder: {outputFolder}");
            }
            else
            {
                // Local ComfyUI - use the local output folder path
                if (string.IsNullOrEmpty(settings.OutputFolderPath))
                {
                    AddLog("ERROR: ComfyUI output path not configured");
                    return null;
                }
                outputFolder = Path.Combine(settings.OutputFolderPath, "video");
                AddLog($"Using local ComfyUI output folder: {outputFolder}");
            }

            if (!Directory.Exists(outputFolder))
            {
                AddLog($"ERROR: Output folder not found: {outputFolder}");
                return null;
            }

            AddLog($"Monitoring output folder: {outputFolder}");

            // Record the starting time and existing files
            var startTime = DateTime.Now;
            var existingFiles = new HashSet<string>(
                Directory.GetFiles(outputFolder, "*.mp4"),
                StringComparer.OrdinalIgnoreCase);

            AddLog($"Found {existingFiles.Count} existing video files at start");

            // Wait up to 60 seconds for new video to appear
            var maxWaitTime = TimeSpan.FromSeconds(60);
            var checkInterval = TimeSpan.FromSeconds(2);

            while (DateTime.Now - startTime < maxWaitTime)
            {
                await Task.Delay(checkInterval);

                // Check for new files
                var currentFiles = Directory.GetFiles(outputFolder, "*.mp4");
                var newFiles = currentFiles.Where(f => !existingFiles.Contains(f)).ToList();

                if (newFiles.Any())
                {
                    AddLog($"Found {newFiles.Count} new video file(s)");

                    // Get the most recently modified new file
                    var newestFile = newFiles
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .First();

                    // Wait a bit more to ensure file is fully written
                    await Task.Delay(TimeSpan.FromSeconds(3));

                    // Verify file exists and has content
                    if (File.Exists(newestFile))
                    {
                        var fileInfo = new FileInfo(newestFile);

                        // Check if file is being written (try to open with exclusive access)
                        bool isFileComplete = false;
                        try
                        {
                            using (var stream = File.Open(newestFile, FileMode.Open, FileAccess.Read, FileShare.None))
                            {
                                // If we get here, file is not locked
                                isFileComplete = true;
                            }
                        }
                        catch (IOException)
                        {
                            AddLog($"File still being written, waiting...");
                            await Task.Delay(TimeSpan.FromSeconds(5));

                            // Try one more time
                            try
                            {
                                using (var stream = File.Open(newestFile, FileMode.Open, FileAccess.Read, FileShare.None))
                                {
                                    isFileComplete = true;
                                }
                            }
                            catch (IOException)
                            {
                                AddLog($"WARNING: File may still be locked, returning anyway");
                                isFileComplete = true; // Return it anyway, queue processing will continue
                            }
                        }

                        if (isFileComplete)
                        {
                            fileInfo = new FileInfo(newestFile); // Refresh file info
                            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                            AddLog($"✓ Video file ready: {Path.GetFileName(newestFile)} ({sizeMB:F2} MB)");
                            AddLog($"  Created: {fileInfo.CreationTime}, Modified: {fileInfo.LastWriteTime}");
                            return newestFile;
                        }
                    }
                }
                else
                {
                    var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                    AddLog($"No new videos yet... ({elapsed}s elapsed, will wait up to 60s)");
                }
            }

            // If we didn't find a new file, check if any file was modified recently
            AddLog("No new file found, checking for recently modified files...");
            var recentThreshold = DateTime.Now.AddSeconds(-30);
            var recentFiles = Directory.GetFiles(outputFolder, "*.mp4")
                .Where(f => File.GetLastWriteTime(f) > recentThreshold)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            if (recentFiles.Any())
            {
                var recentFile = recentFiles.First();
                AddLog($"✓ Found recently modified video: {Path.GetFileName(recentFile)}");
                return recentFile;
            }

            AddLog("Failed to find any output video in the expected time frame");
            AddLog($"Output folder contains: {Directory.GetFiles(outputFolder, "*.mp4").Length} .mp4 files");
            return null;
        }

        private async Task<string?> WaitForLTX2VideoInOutputFolderAsync(string promptId, int chunkIndex, int totalChunks)
        {
            try
            {
                AddLTX2AudioLog($"=== WaitForLTX2VideoInOutputFolderAsync START for chunk {chunkIndex} ===");

                var settings = _settingsService.Settings;
                if (settings == null)
                {
                    AddLTX2AudioLog("ERROR: Settings object is null");
                    return null;
                }

                // Determine if ComfyUI is remote
                string baseUrl = ComfyUIServer == "127.0.0.1" ? $"http://{ComfyUIServer}:{ComfyUIPort}" : settings.BaseUrl;
                bool isRemoteComfyUI = IsComfyUIRemote(new Uri(baseUrl).Host);

                // Determine the correct output folder path
                string outputFolder;
                if (isRemoteComfyUI)
                {
                    // Remote ComfyUI - use the remote output folder path
                    if (string.IsNullOrEmpty(settings.RemoteOutputFolderPath))
                    {
                        AddLTX2AudioLog("ERROR: Remote ComfyUI output path not configured in settings");
                        return null;
                    }
                    outputFolder = Path.Combine(settings.RemoteOutputFolderPath, "video");
                    AddLTX2AudioLog($"Using remote ComfyUI output folder: {outputFolder}");
                }
                else
                {
                    // Local ComfyUI - use the local output folder path
                    if (string.IsNullOrEmpty(settings.OutputFolderPath))
                    {
                        AddLTX2AudioLog("ERROR: ComfyUI output path not configured");
                        return null;
                    }
                    outputFolder = Path.Combine(settings.OutputFolderPath, "video");
                    AddLTX2AudioLog($"Using local ComfyUI output folder: {outputFolder}");
                }

                if (!Directory.Exists(outputFolder))
                {
                    AddLTX2AudioLog($"ERROR: Output folder not found: {outputFolder}");
                    AddLTX2AudioLog($"Checking parent folder exists: {Directory.Exists(settings.OutputFolderPath)}");

                    // Try to create the video folder
                    try
                    {
                        Directory.CreateDirectory(outputFolder);
                        AddLTX2AudioLog($"Created output folder: {outputFolder}");
                    }
                    catch (Exception ex)
                    {
                        AddLTX2AudioLog($"ERROR: Could not create output folder: {ex.Message}");
                        return null;
                    }
                }

                AddLTX2AudioLog($"Monitoring output folder for chunk {chunkIndex}: {outputFolder}");

                // Record the starting time and existing files
                var startTime = DateTime.Now;
                string[] allMp4Files;
                try
                {
                    allMp4Files = Directory.GetFiles(outputFolder, "*.mp4");
                    AddLTX2AudioLog($"Found {allMp4Files.Length} total .mp4 files in output folder");
                }
                catch (Exception ex)
                {
                    AddLTX2AudioLog($"ERROR listing .mp4 files: {ex.Message}");
                    return null;
                }

                var existingFiles = new HashSet<string>(
                    Directory.GetFiles(outputFolder, "LTX_*.mp4"),
                    StringComparer.OrdinalIgnoreCase);

                AddLTX2AudioLog($"Found {existingFiles.Count} existing LTX video files at start");

                // LTX2 workflows take much longer - wait up to 15 minutes per chunk
                var maxWaitTime = TimeSpan.FromMinutes(15);
                var checkInterval = TimeSpan.FromSeconds(5);

                while (DateTime.Now - startTime < maxWaitTime)
                {
                    await Task.Delay(checkInterval);

                    // Check for new files
                    var currentFiles = Directory.GetFiles(outputFolder, "LTX_*.mp4");
                    var newFiles = currentFiles.Where(f => !existingFiles.Contains(f)).ToList();

                    if (newFiles.Any())
                    {
                        AddLTX2AudioLog($"Found {newFiles.Count} new LTX video file(s) for chunk {chunkIndex}");

                        // Get the most recently modified new file
                        var newestFile = newFiles
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .First();

                        // Wait a bit more to ensure file is fully written
                        await Task.Delay(TimeSpan.FromSeconds(5));

                        // Verify file exists and has content
                        if (File.Exists(newestFile))
                        {
                            var fileInfo = new FileInfo(newestFile);

                            // Check if file has reasonable size (at least 1KB)
                            if (fileInfo.Length < 1024)
                            {
                                AddLTX2AudioLog($"File too small ({fileInfo.Length} bytes), waiting...");
                                await Task.Delay(TimeSpan.FromSeconds(10));
                                continue;
                            }

                            var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                            AddLTX2AudioLog($"✓ LTX video file ready for chunk {chunkIndex}: {Path.GetFileName(newestFile)} ({sizeMB:F2} MB)");
                            AddLTX2AudioLog($"  Created: {fileInfo.CreationTime}, Modified: {fileInfo.LastWriteTime}");
                            return newestFile;
                        }
                    }
                    else
                    {
                        var elapsed = (int)(DateTime.Now - startTime).TotalSeconds;
                        var remaining = (int)(maxWaitTime - (DateTime.Now - startTime)).TotalSeconds;
                        AddLTX2AudioLog($"Chunk {chunkIndex}/{totalChunks}: No new videos yet... ({elapsed}s elapsed, {remaining}s remaining)");
                    }
                }

                // If we didn't find a new file, check if any file was modified recently
                AddLTX2AudioLog($"Chunk {chunkIndex}: No new file found, checking for recently modified files...");
                var recentThreshold = DateTime.Now.AddMinutes(-5);
                var recentFiles = Directory.GetFiles(outputFolder, "LTX_*.mp4")
                    .Where(f => File.GetLastWriteTime(f) > recentThreshold)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (recentFiles.Any())
                {
                    var recentFile = recentFiles.First();
                    AddLTX2AudioLog($"✓ Found recently modified video for chunk {chunkIndex}: {Path.GetFileName(recentFile)}");
                    return recentFile;
                }

                AddLTX2AudioLog($"ERROR: Failed to find output video for chunk {chunkIndex}");
                AddLTX2AudioLog($"Output folder contains: {Directory.GetFiles(outputFolder, "LTX_*.mp4").Length} LTX .mp4 files");

                // List all files in output folder for debugging
                try
                {
                    var allFiles = Directory.GetFiles(outputFolder, "*.mp4");
                    AddLTX2AudioLog($"All .mp4 files in output folder:");
                    foreach (var file in allFiles.Take(10)) // Limit to first 10
                    {
                        var fi = new FileInfo(file);
                        AddLTX2AudioLog($"  - {Path.GetFileName(file)} ({fi.Length / 1024.0 / 1024.0:F2} MB, modified: {fi.LastWriteTime})");
                    }
                }
                catch (Exception ex)
                {
                    AddLTX2AudioLog($"Error listing files: {ex.Message}");
                }

                return null;
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"=== EXCEPTION in WaitForLTX2VideoInOutputFolderAsync ===");
                AddLTX2AudioLog($"Message: {ex.Message}");
                AddLTX2AudioLog($"Type: {ex.GetType().Name}");
                AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        private void PlayVideo()
        {
            PlayRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultVideoPath) && File.Exists(ResultVideoPath))
            {
                var folderPath = Path.GetDirectoryName(ResultVideoPath);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    Process.Start("explorer.exe", folderPath);
                    AddLog($"Opened folder: {folderPath}");
                }
            }
        }

        private void SendToEditCamera()
        {
            // This will be implemented to open FlipPixWindow with the first frame of the video
            System.Windows.MessageBox.Show("This feature will extract the first frame of the video and send it to the Edit Camera page.", "Info", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            AddLog("Send to Edit Camera requested");
        }

        private void NavigateToImageGenerator()
        {
            if (_serviceProvider == null)
            {
                AddLog("ERROR: Service provider is null");
                return;
            }

            try
            {
                var imageGeneratorWindow = _serviceProvider.GetService(typeof(ImageGeneratorWindow)) as ImageGeneratorWindow;

                if (imageGeneratorWindow == null)
                {
                    AddLog("ERROR: Failed to create ImageGeneratorWindow - GetService returned null");
                    return;
                }

                imageGeneratorWindow.Show();
                AddLog("Successfully opened Image Generator window");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Generator: {ex.Message}");
            }
        }

        private void NavigateToCameraEdit()
        {
            if (_serviceProvider == null)
            {
                AddLog("ERROR: Service provider is null");
                return;
            }

            try
            {
                var cameraEditWindow = _serviceProvider.GetService(typeof(FlipPixWindow)) as FlipPixWindow;

                if (cameraEditWindow == null)
                {
                    AddLog("ERROR: Failed to create FlipPixWindow - GetService returned null");
                    return;
                }

                cameraEditWindow.Show();
                AddLog("Successfully opened Camera Edit window");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Camera Edit: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{timestamp}] {message}\n";
            LogOutput += logEntry;
            _logger.LogInfo(message);
        }

        private async Task<string?> CopyVideoFromRemoteFolder(string remoteOutputPath, string promptId)
    {
        try
        {
            AddLog("=== CopyVideoFromRemoteFolder START ===");
            AddLog($"Remote output path: {remoteOutputPath}");

            // Wait a moment for files to be written
            await Task.Delay(2000);

            // Look for recent video files in the remote output folder
            var videoFiles = Directory.GetFiles(remoteOutputPath, "*.mp4", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .ToList();

            AddLog($"Found {videoFiles.Count} MP4 files in remote folder");

            // Also check for subfolders that might contain videos
            var subfolders = new[] { "output", "videos", "temp", "input" };
            foreach (var subfolder in subfolders)
            {
                var subfolderPath = Path.Combine(remoteOutputPath, subfolder);
                if (Directory.Exists(subfolderPath))
                {
                    var subfolderVideos = Directory.GetFiles(subfolderPath, "*.mp4")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime);
                    videoFiles.AddRange(subfolderVideos);
                }
            }

            videoFiles = videoFiles.Distinct().OrderByDescending(f => new FileInfo(f).LastWriteTime).ToList();
            AddLog($"Total unique MP4 files found: {videoFiles.Count}");

            // Filter for files created in the last 10 minutes (more generous timeframe)
            var recentFiles = videoFiles.Where(f =>
            {
                var fileInfo = new FileInfo(f);
                var age = DateTime.Now - fileInfo.LastWriteTime;
                return age.TotalMinutes <= 10;
            }).ToList();

            AddLog($"Found {recentFiles.Count} recent video files (within last 10 minutes)");

            if (recentFiles.Any())
            {
                var latestVideo = recentFiles.First();
                var fileInfo = new FileInfo(latestVideo);
                AddLog($"Most recent video: {fileInfo.Name} (Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss})");

                // Create local output directory
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                Directory.CreateDirectory(outputDir);

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var localFileName = $"video_{timestamp}.mp4";
                var outputPath = Path.Combine(outputDir, localFileName);

                AddLog($"Copying video to: {outputPath}");

                // Copy the file
                File.Copy(latestVideo, outputPath, true);

                var copiedFileInfo = new FileInfo(outputPath);
                AddLog($"Video copied successfully: {copiedFileInfo.Name} ({copiedFileInfo.Length / 1024}KB)");
                AddLog("=== CopyVideoFromRemoteFolder END ===");

                return outputPath;
            }
            else
            {
                // If no recent files found, show all files for debugging
                if (videoFiles.Any())
                {
                    AddLog("All video files found (showing last 10):");
                    foreach (var file in videoFiles.Take(10))
                    {
                        var info = new FileInfo(file);
                        var age = DateTime.Now - info.LastWriteTime;
                        AddLog($"  - {info.Name} ({age.TotalMinutes:F1} minutes old)");
                    }
                }
                else
                {
                    AddLog("No MP4 files found in remote output folder or subfolders");
                }

                AddLog("=== CopyVideoFromRemoteFolder END (NO FILES) ===");
                return null;
            }
        }
        catch (Exception ex)
        {
            AddLog($"ERROR accessing remote folder: {ex.Message}");
            AddLog($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }

    private async Task<string?> TryHttpDownloadFallback(string promptId)
    {
        try
        {
            AddLog("=== HTTP Download Fallback START ===");

            // First try the history API approach
            var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
            AddLog($"Found {outputFiles.Count} potential output files");

            // Look for video files in the output
            var videoFiles = outputFiles.Where(f => f.EndsWith(".mp4") || f.EndsWith(".webm") || f.EndsWith(".mov")).ToList();

            if (videoFiles.Any())
            {
                // Download the most recent video file
                var filename = videoFiles.Last(); // Get the last/most recent
                AddLog($"Downloading generated video: {filename}");

                // Try downloading with different subfolder approaches
                var videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename);

                // If direct download fails, try common subfolders
                if (videoData == null)
                {
                    AddLog("Direct download failed, trying with 'output' subfolder...");
                    videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, "output");
                }

                if (videoData == null)
                {
                    AddLog("Trying with 'videos' subfolder...");
                    videoData = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, "videos");
                }

                if (videoData != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"video_{timestamp}.mp4");

                    await File.WriteAllBytesAsync(outputPath, videoData);
                    AddLog($"Video downloaded and saved: {outputPath}");
                    return outputPath;
                }
                else
                {
                    AddLog($"Failed to download video: {filename}");
                }
            }
            else
            {
                AddLog("No video files found in history, trying alternative approach...");

                // Try the fallback approach
                var fallbackVideo = await _comfyUIService.HttpClient.TryDownloadRecentVideoAsync(promptId);
                if (fallbackVideo != null)
                {
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "video-generation");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"video_{timestamp}.mp4");

                    await File.WriteAllBytesAsync(outputPath, fallbackVideo);
                    AddLog($"Video downloaded and saved via fallback method: {outputPath}");
                    return outputPath;
                }
                else
                {
                    AddLog("Failed to download video using all available methods");
                    AddLog("This might be due to:");
                    AddLog("- ComfyUI output folder not being accessible via HTTP");
                    AddLog("- Different filename pattern than expected");
                    AddLog("- ComfyUI server configuration preventing file access");
                }
            }

            // Debug info about what files we found
            if (outputFiles.Any())
            {
                AddLog("All files found in history:");
                foreach (var file in outputFiles.Take(5))
                {
                    AddLog($"  - {file}");
                }
            }

            AddLog("=== HTTP Download Fallback END ===");
            return null;
        }
        catch (Exception ex)
        {
            AddLog($"ERROR in HTTP download fallback: {ex.Message}");
            return null;
        }
    }

    private void ShowRemoteOutputFolderSetup()
        {
            try
            {
                // Use a simple folder browser dialog to select the remote output folder
                using (var folderDialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    folderDialog.Description = "Select the network path to the remote ComfyUI output folder";
                    folderDialog.ShowNewFolderButton = false;

                    // Try to use previously configured path as starting point
                    var currentPath = _settingsService.Settings?.RemoteOutputFolderPath;
                    if (!string.IsNullOrEmpty(currentPath) && System.IO.Directory.Exists(currentPath))
                    {
                        folderDialog.SelectedPath = currentPath;
                    }

                    if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var selectedPath = folderDialog.SelectedPath;

                        // Validate that the path is accessible
                        if (System.IO.Directory.Exists(selectedPath))
                        {
                            // Save the remote output folder path
                            var settings = _settingsService.Settings;
                            if (settings != null)
                            {
                                settings.RemoteOutputFolderPath = selectedPath;
                                _settingsService.SaveSettings(settings);
                            }

                            AddLog($"Remote output folder configured: {selectedPath}");
                        }
                        else
                        {
                            System.Windows.MessageBox.Show(
                                "The selected folder is not accessible. Please check the network path and permissions.",
                                "Folder Not Accessible",
                                System.Windows.MessageBoxButton.OK,
                                System.Windows.MessageBoxImage.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error configuring remote output folder: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error configuring remote output folder: {ex.Message}",
                    "Configuration Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void ShowComfyUIFolderSetup()
        {
            try
            {
                // Create the ViewModel
                var setupViewModel = new ComfyUIFolderSetupViewModel(_settingsService);

                // Create and show the ComfyUI folder setup window
                var setupWindow = new ComfyUIFolderSetupWindow(setupViewModel);

                // Show the window as a dialog
                bool? result = setupWindow.ShowDialog();

                if (result == true)
                {
                    AddLog("ComfyUI settings updated successfully");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error opening ComfyUI setup: {ex.Message}");
            }
        }

        private bool IsComfyUIRemote(string serverAddress)
        {
            try
            {
                // Check if it's a local address
                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Check if it's a local network IP (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        // 192.168.x.x
                        if (bytes[0] == 192 && bytes[1] == 168)
                        {
                            return true; // This is a LAN IP
                        }
                        // 10.x.x.x
                        if (bytes[0] == 10)
                        {
                            return true; // This is a LAN IP
                        }
                        // 172.16-31.x.x
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        {
                            return true; // This is a LAN IP
                        }
                    }
                }

                // If we get here, assume it's remote
                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                // If we can't determine, assume it's remote to be safe
                return true;
            }
        }

        // Image Analysis Methods
        private async Task AnalyzeImageAsync()
        {
            AddLog($"AnalyzeImageAsync called - HasImage: {HasImage}, ImageFilePath: {ImageFilePath}");

            if (!HasImage)
            {
                AddLog("Cannot analyze: No image loaded");
                return;
            }

            _analysisCancellationTokenSource?.Dispose();
            _analysisCancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                IsAnalyzing = true;
                AnalysisStatus = "Analyzing image with LM Studio Qwen-VL...";
                AnalysisProgress = 0;
                ImageAnalysis = "Analyzing image with LM Studio Qwen-VL AI...";

                AddLog("=== Starting image analysis with LM Studio Qwen-VL ===");
                AddLog($"UseLTXWorkflow: {UseLTXWorkflow} (LTX2 selected: {_selectedWorkflow == "ltx2_i2v"})");

                // Get the selected model from settings
                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
                await _lmStudioService.SetBaseUrlAsync(baseUrl);
                AddLog($"Using LM Studio at: {baseUrl}");

                // Get the selected model or try to find a qwen-vl model
                var models = await _lmStudioService.GetAvailableModelsAsync(_analysisCancellationTokenSource.Token);
                string selectedModel = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;

                if (string.IsNullOrEmpty(selectedModel))
                {
                    // Try to find qwen-vl model
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
                        throw new Exception("No models available in LM Studio. Please load a vision model like Qwen-VL.");
                    }
                }
                else
                {
                    AddLog($"Using configured model: {selectedModel}");
                }

                AnalysisStatus = "Analyzing with LM Studio Qwen-VL...";
                AnalysisProgress = 30;

                // Determine which prompt to use based on workflow selection
                string analysisPrompt;
                if (UseLTXWorkflow)
                {
                    // Load LTX action video system prompt
                    var ltxPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "ltx_action_video_system_prompt.md");
                    if (File.Exists(ltxPromptPath))
                    {
                        analysisPrompt = await File.ReadAllTextAsync(ltxPromptPath, _analysisCancellationTokenSource.Token);
                        AddLog("Using LTX-2 Action Video system prompt");
                    }
                    else
                    {
                        AddLog($"WARNING: LTX action video prompt not found at {ltxPromptPath}, using default");
                        analysisPrompt = "Describe this image in detail, focusing on the subject, their actions, the setting, mood, and any camera or motion elements. This description will be used to generate a video from the image.";
                    }
                }
                else
                {
                    // Use default prompt for Painter workflow
                    analysisPrompt = "Describe this image in detail, focusing on the subject, their actions, the setting, mood, and any camera or motion elements. This description will be used to generate a video from the image.";
                    AddLog("Using default image analysis prompt");
                }

                var analysisResult = await _lmStudioService.AnalyzeImageAsync(
                    selectedModel,
                    ImageFilePath,
                    analysisPrompt,
                    maxTokens: 2000,
                    _analysisCancellationTokenSource.Token);

                AnalysisProgress = 90;
                AddLog("Analysis received from LM Studio");

                if (!string.IsNullOrEmpty(analysisResult))
                {
                    ImageAnalysis = analysisResult;
                    AnalysisStatus = "Analysis complete";
                    AnalysisProgress = 100;
                    AddLog("Image analysis completed successfully");
                    StatusBarMessage = "Image analysis complete - you can use this for prompts";

                    // Automatically save the prompt to JSON (like prompt2json does)
                    await SaveAnalysisToJsonAsync(analysisResult, analysisPrompt);
                }
                else
                {
                    ImageAnalysis = "Analysis completed but no text was returned from LM Studio.";
                    AnalysisStatus = "Analysis complete (no output)";
                    AddLog("Analysis completed but no text output was detected");
                }
            }
            catch (OperationCanceledException)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Cancelled";
                AddLog("Image analysis cancelled by user");
            }
            catch (Exception ex)
            {
                IsAnalyzing = false;
                AnalysisStatus = "Error";
                ImageAnalysis = $"Error analyzing image: {ex.Message}";
                AddLog($"ERROR analyzing image: {ex.Message}");
                System.Windows.MessageBox.Show($"Error analyzing image:\n\n{ex.Message}\n\nPlease ensure LM Studio is running and the Qwen-VL model is loaded.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>
        /// Saves the image analysis result to a JSON file (matching prompt2json_app.py functionality)
        /// </summary>
        private async Task SaveAnalysisToJsonAsync(string content, string customPrompt)
        {
            try
            {
                // Determine save directory - use configured directory or prompt user
                var saveDirectory = _settingsService.Settings?.Prompt2JsonSaveDirectory;

                if (string.IsNullOrEmpty(saveDirectory) || !Directory.Exists(saveDirectory))
                {
                    // Prompt user to choose save directory
                    AddLog("Prompt save directory not configured, asking user...");

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        using (var folderDialog = new System.Windows.Forms.FolderBrowserDialog())
                        {
                            folderDialog.Description = "Choose where to save prompt files";
                            folderDialog.ShowNewFolderButton = true;

                            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                            {
                                saveDirectory = folderDialog.SelectedPath;
                                var settings = _settingsService.Settings;
                                if (settings != null)
                                {
                                    settings.Prompt2JsonSaveDirectory = saveDirectory;
                                    _settingsService.SaveSettings(settings);
                                }
                            }
                        }
                    });

                    if (string.IsNullOrEmpty(saveDirectory))
                    {
                        AddLog("Save cancelled by user - no directory selected");
                        return;
                    }
                }

                // Generate intelligent filename based on content
                var filename = GenerateIntelligentFilename(content);
                var outputPath = Path.Combine(saveDirectory, filename);

                // Structure the data in the same format as prompt2json
                var data = new
                {
                    CustomPrompt = customPrompt,
                    Prompts = new[] { content }, // Single prompt for analyze mode
                    SavedAt = DateTime.Now.ToString("o"),
                    Version = "1.0",
                    Workflow = UseLTXWorkflow ? "LTX2" : "Painter"
                };

                // Save to JSON file
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(data, jsonOptions);
                await File.WriteAllTextAsync(outputPath, json);

                AddLog($"Analysis saved to: {outputPath}");
                StatusBarMessage = $"Analysis saved: {filename}";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR saving analysis to JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates an intelligent filename based on the AI response content (matching prompt2json_app.py)
        /// </summary>
        private string GenerateIntelligentFilename(string content)
        {
            var keywords = ExtractContentKeywords(content);
            var timestamp = DateTime.Now.ToString("MMdd_HHmm");

            if (keywords.Any())
            {
                var keywordString = string.Join("-", keywords);
                // Clean up the keyword string
                keywordString = System.Text.RegularExpressions.Regex.Replace(keywordString, @"[^\w\s-]", "");
                keywordString = System.Text.RegularExpressions.Regex.Replace(keywordString, @"[\s]+", "-");
                return $"{keywordString}_{timestamp}.json";
            }
            else
            {
                // Fallback to simple timestamp-based name
                return $"analysis_{timestamp}.json";
            }
        }

        /// <summary>
        /// Extracts key descriptive terms from the AI response content (matching prompt2json_app.py)
        /// </summary>
        private List<string> ExtractContentKeywords(string content)
        {
            var contentLower = content.ToLower();
            var keywords = new List<string>();

            // Extract subject matter from the prompts
            var subjects = new Dictionary<string, string[]>
            {
                { "samurai", new[] { "samurai", "katana", "kimono" } },
                { "sword", new[] { "sword", "blade", "swordsman" } },
                { "fighter", new[] { "fighter", "combatant", "warrior" } },
                { "ninja", new[] { "ninja", "assassin" } },
                { "rain", new[] { "rain", "rainy", "downpour" } },
                { "desert", new[] { "desert", "sand", "dune" } },
                { "urban", new[] { "urban", "city", "street" } },
                { "forest", new[] { "forest", "woods", "trees" } },
                { "martial-arts", new[] { "martial arts", "kung fu", "karate", "judo" } },
            };

            // Camera/shot types
            var shots = new Dictionary<string, string[]>
            {
                { "closeup", new[] { "close-up", "closeup" } },
                { "slowmo", new[] { "slow-motion", "slow motion", "slo-mo" } },
                { "aerial", new[] { "aerial", "overhead", "birds eye" } },
                { "tracking", new[] { "tracking shot", "follow" } },
            };

            // Check for subject matter first (most important)
            foreach (var kvp in subjects)
            {
                if (keywords.Count >= 2) break; // Limit to 2 keywords max
                if (kvp.Value.Any(pattern => contentLower.Contains(pattern)))
                {
                    keywords.Add(kvp.Key);
                }
            }

            // If we only have 1 keyword, try to add a shot type
            if (keywords.Count < 2)
            {
                foreach (var kvp in shots)
                {
                    if (kvp.Value.Any(pattern => contentLower.Contains(pattern)))
                    {
                        keywords.Add(kvp.Key);
                        break;
                    }
                }
            }

            // Fallback: extract the first few significant nouns
            if (keywords.Count == 0)
            {
                var words = System.Text.RegularExpressions.Regex.Matches(contentLower.Substring(0, Math.Min(200, contentLower.Length)), @"\b[a-z]{4,}\b")
                    .Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => m.Value)
                    .Where(w => !new[] { "this", "that", "with", "from", "have", "been", "were", "their" }.Contains(w))
                    .Take(2)
                    .ToList();
                keywords.AddRange(words);
            }

            return keywords.Take(2).ToList();
        }

        private async Task<string> GetAnalysisOutputAsync(string promptId)
        {
            try
            {
                await Task.Delay(2000);

                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var historyUrl = $"{baseUrl}/history/{promptId}";

                AddLog($"Fetching analysis output from: {historyUrl}");

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var response = await httpClient.GetAsync(historyUrl);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var historyData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseContent);

                    if (historyData != null && historyData.ContainsKey(promptId))
                    {
                        var promptData = historyData[promptId];

                        if (promptData.TryGetProperty("outputs", out var outputs))
                        {
                            // Node 60 is the ShowText node that displays QwenVL output
                            if (outputs.TryGetProperty("60", out var node60))
                            {
                                if (node60.TryGetProperty("text", out var textArray))
                                {
                                    if (textArray.ValueKind == JsonValueKind.Array && textArray.GetArrayLength() > 0)
                                    {
                                        var textOutput = textArray[0].GetString() ?? string.Empty;
                                        AddLog($"Retrieved text output: {textOutput.Substring(0, Math.Min(100, textOutput.Length))}...");
                                        return textOutput;
                                    }
                                }
                            }
                        }
                    }
                }

                return "Image analysis complete. The image shows a subject that could be used for video generation.";
            }
            catch (Exception ex)
            {
                AddLog($"Error retrieving analysis output: {ex.Message}");
                return "Image analysis complete. The image shows a subject that could be used for video generation.";
            }
        }

        private void SendAnalysisToQueue()
        {
            if (!string.IsNullOrEmpty(ImageAnalysis) && HasImage)
            {
                var queueItem = new QueueItem
                {
                    Prompt = ImageAnalysis,
                    ImagePath = ImageFilePath,
                    Status = QueueItemStatus.Pending
                };

                PromptQueue.Add(queueItem);
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Analysis sent to queue: {queueItem.DisplayText.Substring(0, Math.Min(80, queueItem.DisplayText.Length))}...");
                StatusBarMessage = "Analysis added to queue";
            }
        }

        // Queue Management Methods
        private void AddToQueue()
        {
            if (string.IsNullOrWhiteSpace(NewQueuePrompt) || !HasImage) return;

            var queueItem = new QueueItem
            {
                Prompt = NewQueuePrompt,
                ImagePath = ImageFilePath,
                Status = QueueItemStatus.Pending
            };

            PromptQueue.Add(queueItem);
            NewQueuePrompt = string.Empty;
            UpdateQueueStatus();
            SaveQueueToFile();
            AddLog($"Added to queue: {queueItem.DisplayText.Substring(0, Math.Min(80, queueItem.DisplayText.Length))}...");
        }

        private void RemoveFromQueue(QueueItem? item)
        {
            if (item != null && PromptQueue.Contains(item))
            {
                PromptQueue.Remove(item);
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Removed from queue: {item.DisplayText.Substring(0, Math.Min(80, item.DisplayText.Length))}...");
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (!PromptQueue.Any()) return;

            IsProcessingQueue = true;
            AddLog($"=== Starting to process queue with {PromptQueue.Count} items ===");

            try
            {
                var pendingItems = PromptQueue.Where(x => x.Status == QueueItemStatus.Pending).ToList();

                foreach (var item in pendingItems)
                {
                    if (IsProcessing) break; // Stop if single video generation starts

                    try
                    {
                        item.Status = QueueItemStatus.Processing;
                        UpdateQueueStatus();
                        SaveQueueToFile();
                        AddLog($"Processing queue item: {item.DisplayText.Substring(0, Math.Min(80, item.DisplayText.Length))}...");

                        // Check if ComfyUI is running before processing each item
                        AddLog("Checking ComfyUI status before processing...");
                        var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Crash Detection] {status}"),
                            default);

                        if (!comfyUIOk)
                        {
                            AddLog("ERROR: ComfyUI is not available");
                            item.Status = QueueItemStatus.Failed;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            continue; // Try next item
                        }

                        // Ensure ComfyUI is connected
                        if (!_comfyUIService.IsConnected)
                        {
                            AddLog("Reconnecting to ComfyUI WebSocket...");
                            try
                            {
                                await _comfyUIService.ConnectAsync();
                                AddLog("Reconnected to ComfyUI");
                            }
                            catch (Exception ex)
                            {
                                AddLog($"ERROR: Failed to reconnect to ComfyUI: {ex.Message}");
                                item.Status = QueueItemStatus.Failed;
                                UpdateQueueStatus();
                                SaveQueueToFile();
                                continue;
                            }
                        }

                        // Check if the image still exists
                        if (!File.Exists(item.ImagePath))
                        {
                            item.Status = QueueItemStatus.Failed;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            AddLog($"❌ Queue item failed - image not found: {item.ImagePath}");
                            continue;
                        }

                        // Store current values
                        var originalPrompt = VideoPrompt;
                        var originalImagePath = ImageFilePath;
                        var originalImageSource = ImagePreviewSource;

                        // Set the image and prompt from queue item
                        ImageFilePath = item.ImagePath;
                        VideoPrompt = item.Prompt;

                        // Load the image preview
                        LoadImagePreview();

                        // Generate video for this prompt (bypass CanGenerateVideo check for queue processing)
                        await GenerateVideoAsyncInternal();

                        if (HasResultVideo)
                        {
                            item.Status = QueueItemStatus.Completed;
                            item.VideoPath = ResultVideoPath;
                            AddLog($"✅ Queue item completed: {item.DisplayText.Substring(0, Math.Min(50, item.DisplayText.Length))}...");
                        }
                        else
                        {
                            item.Status = QueueItemStatus.Failed;
                            AddLog($"❌ Queue item failed: {item.DisplayText.Substring(0, Math.Min(50, item.DisplayText.Length))}...");
                        }

                        // Restore original values
                        VideoPrompt = originalPrompt;
                        ImageFilePath = originalImagePath;
                        ImagePreviewSource = originalImageSource;
                        HasResultVideo = false; // Reset for next item

                        // Save queue after each item completion
                        UpdateQueueStatus();
                        SaveQueueToFile();

                        // Small delay between items
                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        item.Status = QueueItemStatus.Failed;
                        UpdateQueueStatus();
                        SaveQueueToFile();
                        AddLog($"Error processing queue item: {ex.Message}");

                        // Attempt to detect and restart ComfyUI after error
                        AddLog("Attempting to detect and restart ComfyUI after error...");
                        var restarted = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Post-Error Restart] {status}"),
                            default);

                        if (restarted)
                        {
                            AddLog("✅ ComfyUI restarted successfully, continuing with remaining items");
                            // Reconnect to WebSocket
                            if (!_comfyUIService.IsConnected)
                            {
                                try
                                {
                                    await _comfyUIService.ConnectAsync();
                                    AddLog("Reconnected to ComfyUI WebSocket");
                                }
                                catch (Exception reconnectEx)
                                {
                                    AddLog($"Warning: Failed to reconnect to WebSocket: {reconnectEx.Message}");
                                }
                            }
                        }
                        else
                        {
                            AddLog("⚠️ ComfyUI restart failed or is disabled. Remaining items may also fail.");
                        }
                    }
                }

                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog("=== Queue processing completed ===");
            }
            catch (Exception ex)
            {
                AddLog($"Error processing queue: {ex.Message}");
            }
            finally
            {
                IsProcessingQueue = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void UpdateQueueStatus()
        {
            var totalCount = PromptQueue.Count;
            var pendingCount = PromptQueue.Count(x => x.Status == QueueItemStatus.Pending);
            var completedCount = PromptQueue.Count(x => x.Status == QueueItemStatus.Completed);
            var failedCount = PromptQueue.Count(x => x.Status == QueueItemStatus.Failed);

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

            // Update HasFailedItems notification
            OnPropertyChanged(nameof(HasFailedItems));
            CommandManager.InvalidateRequerySuggested();
        }

        // Reprocess methods for crash recovery
        private async Task ReprocessItemAsync(QueueItem? item)
        {
            if (item == null) return;

            try
            {
                AddLog($"=== Reprocessing failed item: {item.DisplayText.Substring(0, Math.Min(80, item.DisplayText.Length))} ===");

                // Check if the image still exists
                if (!File.Exists(item.ImagePath))
                {
                    AddLog($"❌ Cannot reprocess - image not found: {item.ImagePath}");
                    System.Windows.MessageBox.Show(
                        $"The image file for this queue item is no longer available:\n\n{item.ImagePath}",
                        "Image Not Found",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                    return;
                }

                // Store current values
                var originalPrompt = VideoPrompt;
                var originalImagePath = ImageFilePath;
                var originalImageSource = ImagePreviewSource;

                // Set the image and prompt from queue item
                ImageFilePath = item.ImagePath;
                VideoPrompt = item.Prompt;

                // Load the image preview
                LoadImagePreview();

                // Reset item status to processing
                item.Status = QueueItemStatus.Processing;
                UpdateQueueStatus();
                SaveQueueToFile();

                // Generate video for this prompt
                await GenerateVideoAsyncInternal();

                if (HasResultVideo)
                {
                    item.Status = QueueItemStatus.Completed;
                    item.VideoPath = ResultVideoPath;
                    AddLog($"✅ Item reprocessed successfully: {item.DisplayText.Substring(0, Math.Min(50, item.DisplayText.Length))}...");
                    StatusBarMessage = "Item reprocessed successfully";
                }
                else
                {
                    item.Status = QueueItemStatus.Failed;
                    AddLog($"❌ Item reprocessing failed: {item.DisplayText.Substring(0, Math.Min(50, item.DisplayText.Length))}...");
                    StatusBarMessage = "Item reprocessing failed - check ComfyUI connection";
                }

                // Restore original values
                VideoPrompt = originalPrompt;
                ImageFilePath = originalImagePath;
                ImagePreviewSource = originalImageSource;
                HasResultVideo = false; // Reset for next operation

                UpdateQueueStatus();
                SaveQueueToFile();
            }
            catch (Exception ex)
            {
                item.Status = QueueItemStatus.Failed;
                AddLog($"Error reprocessing item: {ex.Message}");
                UpdateQueueStatus();
                SaveQueueToFile();
                System.Windows.MessageBox.Show(
                    $"Error reprocessing item:\n\n{ex.Message}",
                    "Reprocess Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task ReprocessAllFailedAsync()
        {
            var failedItems = PromptQueue.Where(x => x.Status == QueueItemStatus.Failed).ToList();

            if (!failedItems.Any())
            {
                AddLog("No failed items to reprocess");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"Reprocess {failedItems.Count} failed item(s)?\n\nThis will retry generating videos for all failed items.",
                "Confirm Reprocess All",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);

            if (result != System.Windows.MessageBoxResult.OK)
            {
                AddLog("Reprocess all cancelled by user");
                return;
            }

            AddLog($"=== Starting to reprocess {failedItems.Count} failed items ===");

            foreach (var item in failedItems)
            {
                if (item.Status == QueueItemStatus.Failed)
                {
                    await ReprocessItemAsync(item);
                    // Small delay between items
                    await Task.Delay(1000);
                }
            }

            AddLog("=== Reprocess all failed items completed ===");
        }

        // Queue persistence methods for crash recovery
        private string QueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "video_queue.json");
        private string StoryQueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "story_video_queue.json");

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
                AddLog($"Queue saved to file: {PromptQueue.Count} items");
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
                        // Reset processing items to failed (since app crashed during processing)
                        if (item.Status == QueueItemStatus.Processing)
                        {
                            item.Status = QueueItemStatus.Failed;
                        }
                        _promptQueue.Add(item);
                    }
                    UpdateQueueStatus();
                    AddLog($"Queue loaded from file: {_promptQueue.Count} items");
                    StatusBarMessage = $"Queue restored: {_promptQueue.Count} items";
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading queue from file: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the story video queue to a file for crash recovery
        /// </summary>
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
                AddLog($"Story queue saved to file: {StoryVideoQueue.Count} items");
            }
            catch (Exception ex)
            {
                AddLog($"Error saving story queue to file: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the story video queue from a file after a crash
        /// </summary>
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
                    var completedCount = savedItems.Count(i => i.Status == "Completed");
                    var failedCount = savedItems.Count(i => i.Status == "Failed");
                    var pendingCount = savedItems.Count(i => i.Status == "Pending");

                    _storyVideoQueue.Clear();
                    foreach (var item in savedItems)
                    {
                        // Reset processing items to failed (since app crashed during processing)
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _storyVideoQueue.Add(item);
                    }
                    UpdateStoryQueueStatus();
                    AddLog($"Story queue loaded from file: {_storyVideoQueue.Count} items ({completedCount} completed, {failedCount} failed, {pendingCount} pending)");
                    StatusBarMessage = $"Story queue restored: {_storyVideoQueue.Count} items";
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading story queue from file: {ex.Message}");
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Story Video Generator Methods
        private void SelectStoryPromptJson()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorStoryPromptJsonFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Story Prompts JSON File",
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                StoryPromptJsonPath = dialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorStoryPromptJsonFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected story prompts file: {Path.GetFileName(StoryPromptJsonPath)}");
            }
        }

        private void SelectStoryImagesFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select the folder containing the story images";
                dialog.ShowNewFolderButton = false;

                // Try to use persisted path from settings first
                var initialPath = _settingsService.Settings?.VideoGeneratorStoryImagesFolder;
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                {
                    dialog.SelectedPath = initialPath;
                }
                // Fallback to in-memory property
                else if (!string.IsNullOrEmpty(StoryImagesFolderPath) && Directory.Exists(StoryImagesFolderPath))
                {
                    dialog.SelectedPath = StoryImagesFolderPath;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    StoryImagesFolderPath = dialog.SelectedPath;

                    // Save the folder location for next time
                    if (_settingsService.Settings != null)
                    {
                        _settingsService.Settings.VideoGeneratorStoryImagesFolder = dialog.SelectedPath;
                        _settingsService.SaveSettings(_settingsService.Settings);
                    }

                    AddLog($"Selected story images folder: {StoryImagesFolderPath}");
                }
            }
        }

        private async Task LoadStoryQueueAsync()
        {
            if (!CanLoadStoryQueue) return;

            try
            {
                AddLog("Loading story prompts from JSON file...");
                var jsonContent = await File.ReadAllTextAsync(StoryPromptJsonPath);
                var storyData = JsonSerializer.Deserialize<StoryPromptData>(jsonContent);

                if (storyData?.Prompts == null || !storyData.Prompts.Any())
                {
                    AddLog("ERROR: No prompts found in JSON file");
                    System.Windows.MessageBox.Show("No prompts found in the JSON file.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Get all image files from the folder and sort by the numeric index in the filename
                var imageFiles = Directory.GetFiles(StoryImagesFolderPath, "*.png")
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpg"))
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpeg"))
                    .Select(f => new
                    {
                        Path = f,
                        FileName = Path.GetFileName(f)
                    })
                    .OrderBy(x =>
                    {
                        // Extract the numeric index from filename pattern: ...-{number}_00001_.png
                        // For example: vintage-uni-closeup_0103_1045-10_00001_.png -> index 10
                        var match = System.Text.RegularExpressions.Regex.Match(x.FileName, @"-(\d+)_00001_");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int index))
                        {
                            return index;
                        }
                        return int.MaxValue; // Put non-matching files at the end
                    })
                    .Select(x => x.Path)
                    .ToList();

                // Clear existing queue
                StoryVideoQueue.Clear();

                // Create queue items for each prompt
                int count = Math.Min(storyData.Prompts.Count, imageFiles.Count);
                for (int i = 0; i < count; i++)
                {
                    var queueItem = new StoryVideoQueueItem
                    {
                        Index = i + 1,
                        Prompt = storyData.Prompts[i],
                        InputImagePath = imageFiles[i],
                        Status = "Pending"
                    };

                    // Subscribe to item property changes
                    queueItem.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(StoryVideoQueueItem.Status))
                        {
                            OnPropertyChanged(nameof(CanProcessStoryQueue));
                            CommandManager.InvalidateRequerySuggested();
                        }
                    };

                    StoryVideoQueue.Add(queueItem);
                }

                UpdateStoryQueueStatus();
                AddLog($"Loaded {StoryVideoQueue.Count} story video items into queue");
                StatusBarMessage = $"Loaded {StoryVideoQueue.Count} items into story queue";

                // Save queue for crash recovery
                SaveStoryQueueToFile();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading story queue: {ex.Message}");
                _logger.LogError($"Error loading story queue: {ex}");
                System.Windows.MessageBox.Show($"Error loading story queue:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessStoryQueueAsync()
        {
            if (!CanProcessStoryQueue) return;

            try
            {
                IsProcessingStoryQueue = true;
                var pendingItems = StoryVideoQueue.Where(item => item.Status == "Pending").ToList();
                StoryQueueTotal = pendingItems.Count;
                StoryQueueProgress = 0;

                AddLog($"=== Starting story video queue processing ({StoryQueueTotal} videos) ===");

                foreach (var item in pendingItems)
                {
                    CurrentStoryQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    UpdateStoryQueueStatus();

                    AddLog($"Processing story video {StoryQueueProgress + 1}/{StoryQueueTotal}: Prompt #{item.Index}");

                    try
                    {
                        // Check if the image still exists
                        if (!File.Exists(item.InputImagePath))
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Image not found";
                            AddLog($"❌ Story video item failed - image not found: {item.InputImagePath}");
                            UpdateStoryQueueStatus();
                            continue;
                        }

                        // Store current values
                        var originalPrompt = VideoPrompt;
                        var originalImagePath = ImageFilePath;
                        var originalImageSource = ImagePreviewSource;

                        // Set the image and prompt from queue item
                        ImageFilePath = item.InputImagePath;
                        VideoPrompt = item.Prompt;

                        // Load the image preview
                        LoadImagePreview();

                        // Generate video for this prompt
                        await GenerateVideoAsyncInternal();

                        if (HasResultVideo)
                        {
                            item.Status = "Completed";
                            item.OutputVideoPath = ResultVideoPath;
                            item.CompletedAt = DateTime.Now;
                            item.Progress = 100;
                            AddLog($"✅ Story video #{item.Index} completed: {Path.GetFileName(ResultVideoPath)}");
                            // Save queue progress after each completion
                            SaveStoryQueueToFile();
                        }
                        else
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Video generation failed";
                            AddLog($"❌ Story video #{item.Index} failed: Video generation returned no result");
                            // Save queue progress after each failure
                            SaveStoryQueueToFile();
                        }

                        // Restore original values
                        VideoPrompt = originalPrompt;
                        ImageFilePath = originalImagePath;
                        ImagePreviewSource = originalImageSource;
                        HasResultVideo = false; // Reset for next item

                        // Small delay between items
                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        AddLog($"❌ Story video #{item.Index} failed: {ex.Message}");
                        _logger.LogError($"Error processing story video item {item.Id}: {ex}");

                        // Attempt to restart ComfyUI after error
                        AddLog("Attempting to detect and restart ComfyUI after error...");
                        var restarted = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Post-Error Restart] {status}"),
                            default);

                        if (restarted)
                        {
                            AddLog("✅ ComfyUI restarted successfully, continuing with remaining items");
                            // Reconnect to WebSocket
                            if (!_comfyUIService.IsConnected)
                            {
                                try
                                {
                                    await _comfyUIService.ConnectAsync();
                                    AddLog("Reconnected to ComfyUI WebSocket");
                                }
                                catch (Exception reconnectEx)
                                {
                                    AddLog($"Warning: Failed to reconnect to WebSocket: {reconnectEx.Message}");
                                }
                            }
                        }
                        else
                        {
                            AddLog("⚠️ ComfyUI restart failed or is disabled. Remaining items may also fail.");
                        }

                        // Save queue progress after exception
                        SaveStoryQueueToFile();
                    }
                    finally
                    {
                        StoryQueueProgress++;
                        UpdateStoryQueueStatus();
                    }
                }

                var completedCount = StoryVideoQueue.Count(x => x.Status == "Completed");
                var failedCount = StoryVideoQueue.Count(x => x.Status == "Failed");

                AddLog($"=== Story video queue processing completed ({completedCount} successful, {failedCount} failed) ===");
                // Save final queue state
                SaveStoryQueueToFile();
                StatusBarMessage = $"Story queue completed: {completedCount} successful, {failedCount} failed";

                System.Windows.MessageBox.Show($"Story video generation completed!\n\nSuccessful: {completedCount}\nFailed: {failedCount}",
                    "Processing Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Story queue processing failed: {ex.Message}");
                _logger.LogError($"Error processing story queue: {ex}");
                System.Windows.MessageBox.Show($"Story queue processing failed:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessingStoryQueue = false;
                CurrentStoryQueueItem = null;
                StoryQueueProgress = 0;
                StoryQueueTotal = 0;
                UpdateStoryQueueStatus();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ClearStoryQueue()
        {
            if (!StoryVideoQueue.Any()) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to clear all {StoryVideoQueue.Count} items from the story queue?",
                "Clear Story Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                StoryVideoQueue.Clear();
                UpdateStoryQueueStatus();
                AddLog("Story queue cleared");

                // Delete saved queue file
                try
                {
                    if (File.Exists(StoryQueueFilePath))
                    {
                        File.Delete(StoryQueueFilePath);
                        AddLog("Story queue file deleted");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"Warning: Could not delete story queue file: {ex.Message}");
                }

                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void UpdateStoryQueueStatus()
        {
            var totalCount = StoryVideoQueue.Count;
            var pendingCount = StoryVideoQueue.Count(x => x.Status == "Pending");
            var completedCount = StoryVideoQueue.Count(x => x.Status == "Completed");
            var failedCount = StoryVideoQueue.Count(x => x.Status == "Failed");

            if (totalCount == 0)
            {
                StoryQueueStatus = "No images loaded";
            }
            else if (IsProcessingStoryQueue)
            {
                StoryQueueStatus = $"Processing queue... ({StoryQueueProgress}/{StoryQueueTotal}) - {pendingCount} pending, {completedCount} completed, {failedCount} failed";
            }
            else
            {
                StoryQueueStatus = $"Queue: {totalCount} items ({pendingCount} pending, {completedCount} completed, {failedCount} failed)";
            }

            CommandManager.InvalidateRequerySuggested();
        }

        // VACE Methods
        private void SelectVACEBackgroundImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Background Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                VaceBackgroundImagePath = openFileDialog.FileName;
                AddLog($"VACE: Selected background image: {Path.GetFileName(VaceBackgroundImagePath)}");
            }
        }

        private void SelectVACEForegroundImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Foreground Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                VaceForegroundImagePath = openFileDialog.FileName;
                AddLog($"VACE: Selected foreground image: {Path.GetFileName(VaceForegroundImagePath)}");
            }
        }

        private void SelectVACEVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Input Video",
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                VaceVideoPath = openFileDialog.FileName;
                AddLog($"VACE: Selected video: {Path.GetFileName(VaceVideoPath)}");
            }
        }

        private void LoadVACEBackgroundImagePreview()
        {
            if (string.IsNullOrEmpty(VaceBackgroundImagePath) || !File.Exists(VaceBackgroundImagePath))
            {
                VaceBackgroundImagePreview = null;
                VaceBackgroundImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(VaceBackgroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                VaceBackgroundImagePreview = bitmap;

                var fileInfo = new FileInfo(VaceBackgroundImagePath);
                VaceBackgroundImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading background image preview: {ex.Message}");
                VaceBackgroundImageInfo = "Error loading image";
            }
        }

        private void LoadVACEForegroundImagePreview()
        {
            if (string.IsNullOrEmpty(VaceForegroundImagePath) || !File.Exists(VaceForegroundImagePath))
            {
                VaceForegroundImagePreview = null;
                VaceForegroundImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(VaceForegroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                VaceForegroundImagePreview = bitmap;

                var fileInfo = new FileInfo(VaceForegroundImagePath);
                VaceForegroundImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading foreground image preview: {ex.Message}");
                VaceForegroundImageInfo = "Error loading image";
            }
        }

        private void LoadVACEVideoInfo()
        {
            if (string.IsNullOrEmpty(VaceVideoPath) || !File.Exists(VaceVideoPath))
            {
                VaceVideoInfo = string.Empty;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(VaceVideoPath);
                VaceVideoInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB";
            }
            catch (Exception ex)
            {
                AddLog($"Error loading video info: {ex.Message}");
                VaceVideoInfo = "Error loading video info";
            }
        }

        private async Task GenerateVACEVideoAsync()
        {
            if (!CanGenerateVACEVideo) return;

            try
            {
                await GenerateVACEVideoAsyncInternal();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"An error occurred during VACE video generation:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task GenerateVACEVideoAsyncInternal()
        {
            try
            {
                AddLog("=== Starting VACE video generation ===");
                IsProcessingVACE = true;

                // Clear previous result
                HasResultVideo = false;
                ResultVideoPath = string.Empty;
                VideoInfo = string.Empty;

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing VACE workflow...";
                AddLog($"Background image: {Path.GetFileName(VaceBackgroundImagePath)}");
                AddLog($"Foreground image: {Path.GetFileName(VaceForegroundImagePath)}");
                AddLog($"Input video: {Path.GetFileName(VaceVideoPath)}");
                AddLog($"Prompt: {VacePrompt}");

                // Check if ComfyUI has crashed and restart if needed
                ProcessingStatus = "Checking ComfyUI status...";
                AddLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
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
                else
                {
                    AddLog("ComfyUI already connected");
                }

                // Load VACE workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "step1-chunkcreatorAPI.json");

                AddLog($"Loading VACE workflow: step1-chunkcreatorAPI.json");

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"VACE workflow file not found:\n{workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload images and video
                ProcessingStatus = "Uploading assets to ComfyUI...";
                ProcessingProgress = 10;
                AddLog("Uploading background image to ComfyUI...");
                var uploadedBgImageName = await _comfyUIService.UploadImageAsync(VaceBackgroundImagePath);
                if (string.IsNullOrEmpty(uploadedBgImageName))
                {
                    AddLog("ERROR: Background image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload background image to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddLog($"Background image uploaded: {uploadedBgImageName}");

                AddLog("Uploading foreground image to ComfyUI...");
                var uploadedFgImageName = await _comfyUIService.UploadImageAsync(VaceForegroundImagePath);
                if (string.IsNullOrEmpty(uploadedFgImageName))
                {
                    AddLog("ERROR: Foreground image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload foreground image to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddLog($"Foreground image uploaded: {uploadedFgImageName}");

                AddLog("Uploading video to ComfyUI...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(VaceVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddLog("ERROR: Video upload failed");
                    System.Windows.MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddLog($"Video uploaded: {uploadedVideoName}");

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateVACEWorkflowParameters(workflow, uploadedBgImageName, uploadedFgImageName, uploadedVideoName);

                // Execute workflow
                ProcessingStatus = "Generating VACE video...";
                ProcessingProgress = 30;
                AddLog("Executing VACE video generation workflow...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            ProcessingStatus = $"Generating VACE video: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "VACE workflow completed, retrieving video...";
                });

                AddLog($"VACE workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                AddLog("Looking for generated VACE video...");

                var outputVideo = await WaitForVideoInOutputFolderAsync(promptId);

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    HasResultVideo = true;

                    var fileInfo = new FileInfo(outputVideo);
                    VideoInfo = $"VACE Video • {fileInfo.Length / 1024}KB";

                    ProcessingProgress = 100;
                    ProcessingStatus = "VACE Complete!";
                    StatusBarMessage = $"VACE video generation complete - {Path.GetFileName(outputVideo)}";

                    AddLog($"=== VACE video generation completed successfully ===");
                    AddLog($"Video saved to: {outputVideo}");
                }
                else
                {
                    AddLog("WARNING: No output video found after VACE generation");
                    ProcessingStatus = "No output generated";
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.Message}");
                AddLog($"Stack trace: {ex.StackTrace}");
                ProcessingStatus = "Error occurred";
                StatusBarMessage = "Error during VACE video generation";
                throw;
            }
            finally
            {
                IsProcessingVACE = false;
            }
        }

        private JsonElement UpdateVACEWorkflowParameters(JsonElement workflow, string backgroundImageName, string foregroundImageName, string videoName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            AddLog("=== Updating VACE workflow parameters ===");

            // Calculate video dimensions based on foreground image aspect ratio
            int videoWidth = 832;
            int videoHeight = 480;
            int imageWidth = 480;
            int imageHeight = 832;

            try
            {
                var imagePath = VaceForegroundImagePath;
                if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze(); // Freeze to make it thread-safe and allow garbage collection

                    int originalWidth = bitmap.PixelWidth;
                    int originalHeight = bitmap.PixelHeight;
                    double aspectRatio = (double)originalWidth / originalHeight;

                    AddLog($"Image dimensions: {originalWidth}x{originalHeight} (AR: {aspectRatio:F2})");

                    // Calculate dimensions based on aspect ratio
                    const int maxDimension = 832;
                    const int minDimension = 480;

                    if (aspectRatio > 1) // Landscape
                    {
                        videoWidth = maxDimension;
                        videoHeight = (int)(maxDimension / aspectRatio);
                        imageWidth = minDimension;
                        imageHeight = (int)(minDimension / aspectRatio);
                    }
                    else // Portrait or square
                    {
                        videoWidth = (int)(minDimension * aspectRatio);
                        videoHeight = minDimension;
                        imageWidth = (int)(minDimension * aspectRatio);
                        imageHeight = maxDimension;
                    }

                    // Ensure even numbers (required by many video codecs)
                    videoWidth = videoWidth % 2 == 0 ? videoWidth : videoWidth + 1;
                    videoHeight = videoHeight % 2 == 0 ? videoHeight : videoHeight + 1;
                    imageWidth = imageWidth % 2 == 0 ? imageWidth : imageWidth + 1;
                    imageHeight = imageHeight % 2 == 0 ? imageHeight : imageHeight + 1;

                    AddLog($"Calculated video dimensions: {videoWidth}x{videoHeight}");
                    AddLog($"Calculated image dimensions: {imageWidth}x{imageHeight}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Warning: Could not read image dimensions, using defaults: {ex.Message}");
            }

            // Update background image (node 25)
            if (workflowDict.ContainsKey("25"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["25"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = backgroundImageName;
                        node["inputs"] = inputs;
                        workflowDict["25"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 25 (LoadImage) - Background image: {backgroundImageName}");
                    }
                }
            }

            // Update foreground image (node 24)
            if (workflowDict.ContainsKey("24"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["24"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = foregroundImageName;
                        node["inputs"] = inputs;
                        workflowDict["24"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 24 (LoadImage) - Foreground image: {foregroundImageName}");
                    }
                }
            }

            // Update video input (node 14)
            if (workflowDict.ContainsKey("14"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["14"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        // VHS_LoadVideoPath requires a full path to the video file in ComfyUI's input directory
                        var comfyUIInputPath = Path.Combine(_settingsService.Settings?.ComfyUIFolderPath ?? "", "input", videoName);
                        inputs["video"] = comfyUIInputPath;
                        node["inputs"] = inputs;
                        workflowDict["14"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 14 (LoadVideo) - Video: {comfyUIInputPath}");
                    }
                }
            }

            // Update positive prompt (node 26)
            if (workflowDict.ContainsKey("26"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["26"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["string"] = VacePrompt;
                        node["inputs"] = inputs;
                        workflowDict["26"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 26 (StringConstantMultiline) - Prompt updated");
                    }
                }
            }

            // Update image resize dimensions (node 22)
            if (workflowDict.ContainsKey("22"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["22"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = imageWidth;
                        inputs["height"] = imageHeight;
                        node["inputs"] = inputs;
                        workflowDict["22"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 22 (ImageResizeKJv2) - Dimensions: {imageWidth}x{imageHeight}");
                    }
                }
            }

            // Update VACE encode dimensions (node 38)
            if (workflowDict.ContainsKey("38"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["38"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = videoWidth;
                        inputs["height"] = videoHeight;
                        node["inputs"] = inputs;
                        workflowDict["38"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 38 (WanVideoVACEEncode) - Dimensions: {videoWidth}x{videoHeight}");
                    }
                }
            }

            // Update VACE encode dimensions (node 48)
            if (workflowDict.ContainsKey("48"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["48"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = videoWidth;
                        inputs["height"] = videoHeight;
                        node["inputs"] = inputs;
                        workflowDict["48"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"✓ Node 48 (WanVideoVACEEncode) - Dimensions: {videoWidth}x{videoHeight}");
                    }
                }
            }

            AddLog("=== VACE workflow parameters updated successfully ===");

            var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);
            return updatedWorkflow;
        }

        // LTX2Audio Methods
        private void SelectLTX2AudioImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Source Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                LTX2AudioImagePath = openFileDialog.FileName;
                AddLTX2AudioLog($"Selected image: {Path.GetFileName(LTX2AudioImagePath)}");
            }
        }

        private void SelectLTX2Audio()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.m4a|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                LTX2AudioPath = openFileDialog.FileName;
                AddLTX2AudioLog($"Selected audio: {Path.GetFileName(LTX2AudioPath)}");
            }
        }

        private void LoadLTX2AudioImagePreview()
        {
            if (string.IsNullOrEmpty(LTX2AudioImagePath) || !File.Exists(LTX2AudioImagePath))
            {
                LTX2AudioImagePreview = null;
                LTX2AudioImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(LTX2AudioImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                LTX2AudioImagePreview = bitmap;

                var fileInfo = new FileInfo(LTX2AudioImagePath);
                LTX2AudioImageInfo = $"{bitmap.PixelWidth}x{bitmap.PixelHeight} • {fileInfo.Length / 1024}KB";
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"Error loading image preview: {ex.Message}");
                LTX2AudioImageInfo = "Error loading image";
            }
        }

        private void LoadLTX2AudioInfo()
        {
            if (string.IsNullOrEmpty(LTX2AudioPath) || !File.Exists(LTX2AudioPath))
            {
                LTX2AudioInfo = string.Empty;
                LTX2AudioDuration = 0;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(LTX2AudioPath);
                LTX2AudioInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB";

                // Get audio duration using ffmpeg
                GetAudioDuration(LTX2AudioPath);
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"Error loading audio info: {ex.Message}");
                LTX2AudioInfo = "Error loading audio info";
            }
        }

        private void GetAudioDuration(string audioPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    AddLTX2AudioLog("ERROR: ffmpeg not found. Please install ffmpeg to use this feature.");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{audioPath}\" -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;
                    var errorOutput = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Parse duration from ffmpeg output
                    // Example: "Duration: 00:01:23.45"
                    var match = System.Text.RegularExpressions.Regex.Match(errorOutput, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                    if (match.Success)
                    {
                        var hours = double.Parse(match.Groups[1].Value);
                        var minutes = double.Parse(match.Groups[2].Value);
                        var seconds = double.Parse(match.Groups[3].Value);
                        LTX2AudioDuration = hours * 3600 + minutes * 60 + seconds;
                        AddLTX2AudioLog($"Audio duration: {LTX2AudioDuration:F2} seconds");
                    }
                    else
                    {
                        AddLTX2AudioLog("Could not determine audio duration");
                    }
                }
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"Error getting audio duration: {ex.Message}");
            }
        }

        private void CalculateLTX2AudioTotalFrames()
        {
            const int fps = 24;
            LTX2AudioTotalFrames = (int)(LTX2AudioDuration * fps);
        }

        private void AddLTX2AudioLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LTX2AudioLogOutput += $"[{timestamp}] {message}\n";
        }

        private async Task GenerateLTX2AudioVideoAsync()
        {
            if (!CanGenerateLTX2AudioVideo) return;

            try
            {
                await GenerateLTX2AudioVideoAsyncInternal();
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"An error occurred during LTX2 Audio video generation:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task GenerateLTX2AudioVideoAsyncInternal()
        {
            try
            {
                AddLTX2AudioLog("=== Starting LTX2 Audio video generation ===");
                IsProcessingLTX2Audio = true;

                // Clear previous result
                HasLTX2AudioResult = false;
                LTX2AudioResultPath = string.Empty;
                LTX2AudioVideoInfo = string.Empty;

                LTX2AudioProcessingProgress = 0;
                LTX2AudioProcessingStatus = "Preparing workflow...";
                AddLTX2AudioLog($"Source image: {Path.GetFileName(LTX2AudioImagePath)}");
                AddLTX2AudioLog($"Audio file: {Path.GetFileName(LTX2AudioPath)}");
                AddLTX2AudioLog($"Prompt: {LTX2AudioPrompt}");
                AddLTX2AudioLog($"Total frames: {LTX2AudioTotalFrames} ({LTX2AudioDuration:F1} seconds at 24 FPS)");

                // Check if ComfyUI has crashed and restart if needed
                LTX2AudioProcessingStatus = "Checking ComfyUI status...";
                AddLTX2AudioLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLTX2AudioLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddLTX2AudioLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                AddLTX2AudioLog("ComfyUI is running and responsive");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    LTX2AudioProcessingStatus = "Connecting to ComfyUI...";
                    AddLTX2AudioLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync();
                    AddLTX2AudioLog("Connected to ComfyUI");
                }
                else
                {
                    AddLTX2AudioLog("ComfyUI already connected");
                }

                // Load LTX2 Audio workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "LTX2-AudioSync-i2v-Ver2-GGUF (2)(1).json");

                AddLTX2AudioLog($"Loading LTX2 Audio workflow: LTX2-AudioSync-i2v-Ver2-GGUF (2)(1).json");

                if (!File.Exists(workflowPath))
                {
                    AddLTX2AudioLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"LTX2 Audio workflow file not found:\n{workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload image and audio
                LTX2AudioProcessingStatus = "Uploading assets to ComfyUI...";
                LTX2AudioProcessingProgress = 10;
                AddLTX2AudioLog("Uploading image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(LTX2AudioImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddLTX2AudioLog("ERROR: Image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload image to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddLTX2AudioLog($"Image uploaded: {uploadedImageName}");

                AddLTX2AudioLog("Uploading audio to ComfyUI...");
                var uploadedAudioName = await _comfyUIService.UploadAudioAsync(LTX2AudioPath);
                if (string.IsNullOrEmpty(uploadedAudioName))
                {
                    AddLTX2AudioLog("ERROR: Audio upload failed");
                    System.Windows.MessageBox.Show("Failed to upload audio to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddLTX2AudioLog($"Audio uploaded: {uploadedAudioName}");

                // Calculate chunks
                const int chunkDurationSeconds = 20;
                var totalChunks = (int)Math.Ceiling(LTX2AudioDuration / chunkDurationSeconds);
                AddLTX2AudioLog($"Total duration: {LTX2AudioDuration:F1}s, will generate in {totalChunks} chunks of {chunkDurationSeconds}s each");

                var chunkFiles = new List<string>();
                var currentStartIndex = 0.0;

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var chunkDuration = Math.Min(chunkDurationSeconds, LTX2AudioDuration - currentStartIndex);
                        var chunkFrames = (int)(chunkDuration * 24); // 24 FPS

                        AddLTX2AudioLog($"=== Processing chunk {chunkIndex + 1}/{totalChunks} ({chunkDuration:F1}s, {chunkFrames} frames) ===");

                        LTX2AudioProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                        var baseProgress = 20 + (chunkIndex * 60.0 / totalChunks);

                        // Check ComfyUI connection before each chunk
                        if (chunkIndex > 0)
                        {
                            AddLTX2AudioLog($"Checking ComfyUI connection before chunk {chunkIndex + 1}...");
                            bool isComfyUIReady = _comfyUIService.IsConnected;
                            AddLTX2AudioLog($"ComfyUI ready check: {(isComfyUIReady ? "OK" : "FAILED")}");

                            if (!isComfyUIReady)
                            {
                                AddLTX2AudioLog($"ComfyUI not responding, attempting to reconnect...");

                                // Disconnect first
                                try
                                {
                                    if (_comfyUIService.IsConnected)
                                    {
                                        await _comfyUIService.DisconnectAsync();
                                        AddLTX2AudioLog("Disconnected from ComfyUI");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AddLTX2AudioLog($"Disconnect error (can be ignored): {ex.Message}");
                                }

                                // Wait a bit before reconnecting
                                await Task.Delay(TimeSpan.FromSeconds(3));

                                // Reconnect
                                try
                                {
                                    await _comfyUIService.ConnectAsync();
                                    AddLTX2AudioLog("✓ Reconnected to ComfyUI");

                                    // Wait a bit more for ComfyUI to be fully ready
                                    await Task.Delay(TimeSpan.FromSeconds(2));
                                }
                                catch (Exception ex)
                                {
                                    AddLTX2AudioLog($"ERROR: Failed to reconnect to ComfyUI: {ex.Message}");
                                    AddLTX2AudioLog("Please ensure ComfyUI is running and press Enter in the ComfyUI window if it's waiting for input");
                                    throw new Exception("Cannot reconnect to ComfyUI. Please check ComfyUI window.");
                                }
                            }
                        }

                        // Update workflow parameters for this chunk
                        AddLTX2AudioLog($"Updating workflow parameters for chunk {chunkIndex + 1}...");
                        JsonElement updatedWorkflow;
                        try
                        {
                            updatedWorkflow = UpdateLTX2AudioWorkflowParameters(workflow, uploadedImageName, uploadedAudioName, currentStartIndex, chunkDuration, chunkFrames);
                            AddLTX2AudioLog($"Workflow parameters updated successfully for chunk {chunkIndex + 1}");
                        }
                        catch (Exception ex)
                        {
                            AddLTX2AudioLog($"ERROR updating workflow parameters for chunk {chunkIndex + 1}: {ex.Message}");
                            AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                            throw;
                        }

                        // Execute workflow
                        AddLTX2AudioLog($"About to execute workflow for chunk {chunkIndex + 1}...");

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            try
                            {
                                if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                                {
                                    var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        LTX2AudioProcessingProgress = baseProgress + (percent * 0.6 / totalChunks);
                                        LTX2AudioProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLTX2AudioLog($"ERROR in progress callback: {ex.Message}");
                            }
                        });

                        AddLTX2AudioLog($"Calling ExecuteWorkflowAsync for chunk {chunkIndex + 1}...");
                        string promptId;
                        try
                        {
                            promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                            AddLTX2AudioLog($"ExecuteWorkflowAsync returned for chunk {chunkIndex + 1}, prompt ID: {promptId}");
                        }
                        catch (Exception ex)
                        {
                            AddLTX2AudioLog($"ERROR in ExecuteWorkflowAsync for chunk {chunkIndex + 1}: {ex.Message}");
                            AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");

                            // Try to reconnect for next attempt
                            AddLTX2AudioLog("Attempting to recover connection...");
                            try
                            {
                                if (_comfyUIService.IsConnected)
                                {
                                    await _comfyUIService.DisconnectAsync();
                                }
                                await Task.Delay(TimeSpan.FromSeconds(2));
                                await _comfyUIService.ConnectAsync();
                                AddLTX2AudioLog("✓ Connection recovered");
                            }
                            catch (Exception reconnectEx)
                            {
                                AddLTX2AudioLog($"Failed to recover connection: {reconnectEx.Message}");
                            }

                            throw;
                        }

                        // Wait and retrieve the output video
                        AddLTX2AudioLog($"Looking for generated video for chunk {chunkIndex + 1}...");

                        string? outputVideo = null;
                        try
                        {
                            outputVideo = await WaitForLTX2VideoInOutputFolderAsync(promptId, chunkIndex + 1, totalChunks);
                            AddLTX2AudioLog($"WaitForLTX2VideoInOutputFolderAsync returned: {(outputVideo != null ? "FOUND" : "NULL")}");
                        }
                        catch (Exception ex)
                        {
                            AddLTX2AudioLog($"ERROR in WaitForLTX2VideoInOutputFolderAsync: {ex.Message}");
                            AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                            outputVideo = null;
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFileName = Path.Combine(Path.GetTempPath(), $"ltx2_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            AddLTX2AudioLog($"Copying video from {outputVideo} to {chunkFileName}");

                            try
                            {
                                File.Copy(outputVideo, chunkFileName, true);
                                chunkFiles.Add(chunkFileName);
                                AddLTX2AudioLog($"✓ Chunk {chunkIndex + 1}/{totalChunks} saved successfully: {chunkFileName}");
                            }
                            catch (Exception ex)
                            {
                                AddLTX2AudioLog($"ERROR copying file: {ex.Message}");
                                AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                            }
                        }
                        else
                        {
                            AddLTX2AudioLog($"WARNING: No output video found for chunk {chunkIndex + 1}");
                            AddLTX2AudioLog($"outputVideo is null: {outputVideo == null}");
                            if (outputVideo != null)
                            {
                                AddLTX2AudioLog($"File exists: {File.Exists(outputVideo)}");
                            }
                        }

                        currentStartIndex += chunkDuration;
                        AddLTX2AudioLog($"✓ Completed chunk {chunkIndex + 1}/{totalChunks}, moving to next chunk");
                    }
                    catch (Exception ex)
                    {
                        AddLTX2AudioLog($"=== ERROR processing chunk {chunkIndex + 1}/{totalChunks} ===");
                        AddLTX2AudioLog($"Message: {ex.Message}");
                        AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                        AddLTX2AudioLog($"Continuing to next chunk if possible...");

                        // Don't re-throw - try to continue with remaining chunks
                        currentStartIndex += Math.Min(chunkDurationSeconds, LTX2AudioDuration - currentStartIndex);
                    }
                }

                // Merge chunks
                LTX2AudioProcessingProgress = 85;
                LTX2AudioProcessingStatus = "Merging video chunks...";
                AddLTX2AudioLog("=== Merging video chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputPath = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "LTX2Audio");
                    Directory.CreateDirectory(outputPath);

                    var outputFileName = $"LTX2Audio_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    var finalOutputPath = Path.Combine(outputPath, outputFileName);

                    if (chunkFiles.Count == 1)
                    {
                        // Only one chunk, just copy it
                        File.Copy(chunkFiles[0], finalOutputPath, true);
                        AddLTX2AudioLog($"Only one chunk, copying to final output: {finalOutputPath}");
                    }
                    else
                    {
                        // Merge multiple chunks using ffmpeg
                        MergeVideoChunksWithFFmpeg(chunkFiles, finalOutputPath, LTX2AudioPath);
                    }

                    // Clean up chunk files
                    foreach (var chunkFile in chunkFiles)
                    {
                        try
                        {
                            if (File.Exists(chunkFile))
                            {
                                File.Delete(chunkFile);
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLTX2AudioLog($"Warning: Could not delete chunk file {chunkFile}: {ex.Message}");
                        }
                    }

                    LTX2AudioResultPath = finalOutputPath;
                    HasLTX2AudioResult = true;

                    var fileInfo = new FileInfo(finalOutputPath);
                    LTX2AudioVideoInfo = $"LTX2 Audio Video • {fileInfo.Length / 1024 / 1024:F1}MB";

                    LTX2AudioProcessingProgress = 100;
                    LTX2AudioProcessingStatus = "Complete!";

                    AddLTX2AudioLog($"=== LTX2 Audio video generation completed successfully ===");
                    AddLTX2AudioLog($"Video saved to: {finalOutputPath}");
                }
                else
                {
                    AddLTX2AudioLog("ERROR: No video chunks were generated");
                    LTX2AudioProcessingStatus = "No output generated";
                }
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"ERROR: {ex.Message}");
                AddLTX2AudioLog($"Stack trace: {ex.StackTrace}");
                LTX2AudioProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                IsProcessingLTX2Audio = false;
            }
        }

        private JsonElement UpdateLTX2AudioWorkflowParameters(JsonElement workflow, string imageName, string audioName, double startIndex, double duration, int frames)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            AddLTX2AudioLog("=== Updating LTX2 Audio workflow parameters ===");
            AddLTX2AudioLog($"Start index: {startIndex:F2}s, Duration: {duration:F2}s, Frames: {frames}");

            // Update image (node 110)
            if (workflowDict.ContainsKey("110"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["110"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = imageName;
                        node["inputs"] = inputs;
                        workflowDict["110"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 110 (LoadImage) - Image: {imageName}");
                    }
                }
            }

            // Update audio (node 12)
            if (workflowDict.ContainsKey("12"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["12"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["audio"] = audioName;
                        node["inputs"] = inputs;
                        workflowDict["12"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 12 (LoadAudio) - Audio: {audioName}");
                    }
                }
            }

            // Update prompt (node 85)
            if (workflowDict.ContainsKey("85"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["85"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = LTX2AudioPrompt;
                        node["inputs"] = inputs;
                        workflowDict["85"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 85 (Text Multiline) - Prompt updated");
                    }
                }
            }

            // Update video length/frames (node 81)
            if (workflowDict.ContainsKey("81"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["81"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = frames;
                        node["inputs"] = inputs;
                        workflowDict["81"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 81 (PrimitiveInt) - Frames: {frames}");
                    }
                }
            }

            // Update width and height (node 68)
            if (workflowDict.ContainsKey("68"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["68"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = LTX2AudioWidth;
                        inputs["height"] = LTX2AudioHeight;
                        node["inputs"] = inputs;
                        workflowDict["68"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 68 (ImageResize) - Size: {LTX2AudioWidth}x{LTX2AudioHeight}");
                    }
                }
            }

            // Update audio start index (node 101)
            if (workflowDict.ContainsKey("101"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["101"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = startIndex;
                        node["inputs"] = inputs;
                        workflowDict["101"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 101 (FloatConstant) - Start index: {startIndex:F2}s");
                    }
                }
            }

            // Update audio duration (node 102)
            if (workflowDict.ContainsKey("102"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["102"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["value"] = duration;
                        node["inputs"] = inputs;
                        workflowDict["102"] = JsonSerializer.SerializeToElement(node);
                        AddLTX2AudioLog($"✓ Node 102 (FloatConstant) - Duration: {duration:F2}s");
                    }
                }
            }

            AddLTX2AudioLog("=== LTX2 Audio workflow parameters updated successfully ===");

            var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);
            return updatedWorkflow;
        }

        private void MergeVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath, string originalAudioPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    AddLTX2AudioLog("ERROR: ffmpeg not found. Cannot merge video chunks.");
                    throw new InvalidOperationException("ffmpeg is required to merge video chunks.");
                }

                // Create a temporary file list for ffmpeg
                var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_list_{Guid.NewGuid()}.txt");
                using (var writer = new StreamWriter(listFile))
                {
                    foreach (var chunkFile in chunkFiles)
                    {
                        writer.WriteLine($"file '{chunkFile.Replace("\\", "/")}'");
                    }
                }

                AddLTX2AudioLog($"Merging {chunkFiles.Count} video chunks using ffmpeg...");

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    AddLTX2AudioLog($"ffmpeg merge output: {output}");
                    if (!string.IsNullOrEmpty(error))
                    {
                        AddLTX2AudioLog($"ffmpeg merge error: {error}");
                    }
                }

                // Clean up list file
                try
                {
                    File.Delete(listFile);
                }
                catch (Exception ex)
                {
                    AddLTX2AudioLog($"Warning: Could not delete list file: {ex.Message}");
                }

                // Replace audio with original audio to ensure perfect sync
                AddLTX2AudioLog("Replacing audio with original for perfect sync...");
                var tempOutput = outputPath + ".temp.mp4";

                startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{outputPath}\" -i \"{originalAudioPath}\" -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest \"{tempOutput}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    AddLTX2AudioLog($"ffmpeg audio replace output: {output}");
                    if (!string.IsNullOrEmpty(error))
                    {
                        AddLTX2AudioLog($"ffmpeg audio replace error: {error}");
                    }
                }

                // Replace original with temp
                File.Delete(outputPath);
                File.Move(tempOutput, outputPath);

                AddLTX2AudioLog($"Video merged successfully: {outputPath}");
            }
            catch (Exception ex)
            {
                AddLTX2AudioLog($"ERROR merging video chunks: {ex.Message}");
                throw;
            }
        }

        private string? FindFFmpeg()
        {
            // Try to find ffmpeg in common locations
            var commonPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\ffmpeg\bin\ffmpeg.exe"
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Try to find ffmpeg in PATH
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "ffmpeg",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(output) && File.Exists(output.Split('\n')[0].Trim()))
                    {
                        return output.Split('\n')[0].Trim();
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        private void PlayLTX2AudioVideo()
        {
            if (HasLTX2AudioResult && File.Exists(LTX2AudioResultPath))
            {
                try
                {
                    var window = System.Windows.Application.Current.MainWindow;
                    if (window != null)
                    {
                        var player = window.FindName("LTX2AudioVideoPlayer") as MediaElement;
                        if (player != null)
                        {
                            player.Play();
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddLTX2AudioLog($"Error playing video: {ex.Message}");
                }
            }
        }

        private void OpenLTX2AudioResultFolder()
        {
            if (HasLTX2AudioResult && File.Exists(LTX2AudioResultPath))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{LTX2AudioResultPath}\"");
                }
                catch (Exception ex)
                {
                    AddLTX2AudioLog($"Error opening folder: {ex.Message}");
                }
            }
        }

        private void SendLTX2AudioToEditCamera()
        {
            if (HasLTX2AudioResult)
            {
                SetImagePath(LTX2AudioImagePath);
                AddLTX2AudioLog("Video sent to Edit Camera");
            }
        }

        // Mocha Methods

        private void SelectMochaVideo()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Input Video",
                Filter = "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                MochaVideoPath = openFileDialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(openFileDialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected Mocha video: {Path.GetFileName(MochaVideoPath)}");
            }
        }

        private void SelectMochaImage()
        {
            var initialDirectory = _settingsService.Settings?.VideoGeneratorImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Reference Image",
                Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp|All Files|*.*",
                CheckFileExists = true,
                InitialDirectory = initialDirectory
            };

            if (openFileDialog.ShowDialog() == true)
            {
                MochaImagePath = openFileDialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(openFileDialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.VideoGeneratorImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected Mocha image: {Path.GetFileName(MochaImagePath)}");
            }
        }

        private void LoadMochaVideoInfo()
        {
            if (string.IsNullOrEmpty(MochaVideoPath) || !File.Exists(MochaVideoPath))
            {
                MochaSourceVideoInfo = string.Empty;
                MochaTotalFrames = 0;
                return;
            }

            try
            {
                var fileInfo = new FileInfo(MochaVideoPath);
                var duration = GetVideoDuration(MochaVideoPath);
                var totalFrames = GetVideoFrameCount(MochaVideoPath);

                MochaTotalFrames = totalFrames;
                MochaSourceVideoInfo = $"{fileInfo.Name} • {fileInfo.Length / 1024 / 1024:F1}MB • {duration:F1}s • {totalFrames} frames • {MochaTotalChunks} chunks (81 frames each)";

                AddLog($"Mocha video loaded: {fileInfo.Name}, {totalFrames} frames, {MochaTotalChunks} chunks");
            }
            catch (Exception ex)
            {
                MochaSourceVideoInfo = $"Error loading video: {ex.Message}";
                MochaTotalFrames = 0;
            }
        }

        private void LoadMochaImagePreview()
        {
            if (string.IsNullOrEmpty(MochaImagePath) || !File.Exists(MochaImagePath))
            {
                MochaImagePreview = null;
                MochaImageInfo = string.Empty;
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(MochaImagePath);
                bitmap.EndInit();
                bitmap.Freeze();

                MochaImagePreview = bitmap;

                var fileInfo = new FileInfo(MochaImagePath);
                var decoder = BitmapDecoder.Create(new Uri(MochaImagePath), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                MochaImageInfo = $"{fileInfo.Name} • {decoder.Frames[0].PixelWidth}x{decoder.Frames[0].PixelHeight} • {fileInfo.Length / 1024:F1}KB";

                AddLog($"Mocha image loaded: {fileInfo.Name}");
            }
            catch (Exception ex)
            {
                MochaImagePreview = null;
                MochaImageInfo = $"Error loading image: {ex.Message}";
            }
        }

        private double GetVideoDuration(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    return 0;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{videoPath}\" -hide_banner",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return 0;
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Parse duration from ffmpeg output
                    var match = System.Text.RegularExpressions.Regex.Match(error, @"Duration: (\d+):(\d+):(\d+\.\d+)");
                    if (match.Success)
                    {
                        var hours = double.Parse(match.Groups[1].Value);
                        var minutes = double.Parse(match.Groups[2].Value);
                        var seconds = double.Parse(match.Groups[3].Value);
                        return hours * 3600 + minutes * 60 + seconds;
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private int GetVideoFrameCount(string videoPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    return 0;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{videoPath}\" -hide_banner -map 0:v:0 -c copy -f null -",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return 0;
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // Parse frame count from ffmpeg output
                    var match = System.Text.RegularExpressions.Regex.Match(error, @"frame=\s*(\d+)");
                    if (match.Success)
                    {
                        return int.Parse(match.Groups[1].Value);
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private async Task GenerateMochaVideoAsync()
        {
            if (!CanGenerateMochaVideo) return;

            try
            {
                await GenerateMochaVideoAsyncInternal();
            }
            catch (Exception ex)
            {
                AddMochaLog($"ERROR: {ex.Message}");
                System.Windows.MessageBox.Show($"An error occurred during Mocha video generation:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task GenerateMochaVideoAsyncInternal()
        {
            try
            {
                AddMochaLog("=== Starting Mocha video generation ===");
                IsProcessingMocha = true;

                // Clear previous result
                HasMochaResult = false;
                MochaResultPath = string.Empty;
                MochaResultVideoInfo = string.Empty;

                MochaProcessingProgress = 0;
                MochaProcessingStatus = "Preparing workflow...";
                AddMochaLog($"Source video: {Path.GetFileName(MochaVideoPath)} ({MochaTotalFrames} frames)");
                AddMochaLog($"Source image: {Path.GetFileName(MochaImagePath)}");
                AddMochaLog($"Total chunks: {MochaTotalChunks} (81 frames each)");

                // Check if ComfyUI has crashed and restart if needed
                MochaProcessingStatus = "Checking ComfyUI status...";
                AddMochaLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddMochaLog($"[Auto-Restart] {status}"));

                if (!comfyUIOk)
                {
                    AddMochaLog("ERROR: ComfyUI is not running and auto-restart failed or is disabled");
                    System.Windows.MessageBox.Show(
                        "ComfyUI is not running. Please start ComfyUI manually or configure auto-restart in settings.",
                        "ComfyUI Not Running",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                AddMochaLog("ComfyUI is running and responsive");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    MochaProcessingStatus = "Connecting to ComfyUI...";
                    AddMochaLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync();
                    AddMochaLog("Connected to ComfyUI");
                }
                else
                {
                    AddMochaLog("ComfyUI already connected");
                }

                // Load Mocha workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "wanvideo_2_1_14B_MoCha_replace_subject_KJ_02(1).json");

                AddMochaLog($"Loading Mocha workflow: wanvideo_2_1_14B_MoCha_replace_subject_KJ_02(1).json");

                if (!File.Exists(workflowPath))
                {
                    AddMochaLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Mocha workflow file not found:\n{workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                // Upload video and image
                MochaProcessingStatus = "Uploading assets to ComfyUI...";
                MochaProcessingProgress = 10;
                AddMochaLog("Uploading video to ComfyUI...");
                var uploadedVideoName = await _comfyUIService.UploadVideoAsync(MochaVideoPath);
                if (string.IsNullOrEmpty(uploadedVideoName))
                {
                    AddMochaLog("ERROR: Video upload failed");
                    System.Windows.MessageBox.Show("Failed to upload video to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddMochaLog($"Video uploaded: {uploadedVideoName}");

                AddMochaLog("Uploading image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(MochaImagePath);
                if (string.IsNullOrEmpty(uploadedImageName))
                {
                    AddMochaLog("ERROR: Image upload failed");
                    System.Windows.MessageBox.Show("Failed to upload image to ComfyUI.", "Upload Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }
                AddMochaLog($"Image uploaded: {uploadedImageName}");

                // Process 81-frame chunks
                var chunkFiles = new List<string>();
                const int framesPerChunk = 81;
                var totalChunks = MochaTotalChunks;

                AddMochaLog($"=== Will process {totalChunks} chunks of {framesPerChunk} frames each ===");

                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        var startFrame = chunkIndex * framesPerChunk;
                        var framesInChunk = Math.Min(framesPerChunk, MochaTotalFrames - startFrame);

                        AddMochaLog($"=== Processing chunk {chunkIndex + 1}/{totalChunks} (frames {startFrame}-{startFrame + framesInChunk - 1}) ===");

                        MochaProcessingStatus = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                        var baseProgress = 20 + (chunkIndex * 60.0 / totalChunks);

                        // Check ComfyUI connection before each chunk
                        if (chunkIndex > 0)
                        {
                            AddMochaLog($"Checking ComfyUI connection before chunk {chunkIndex + 1}...");
                            bool isComfyUIReady = _comfyUIService.IsConnected;
                            AddMochaLog($"ComfyUI ready check: {(isComfyUIReady ? "OK" : "FAILED")}");

                            if (!isComfyUIReady)
                            {
                                AddMochaLog($"ComfyUI not responding, attempting to reconnect...");

                                try
                                {
                                    if (_comfyUIService.IsConnected)
                                    {
                                        await _comfyUIService.DisconnectAsync();
                                        AddMochaLog("Disconnected from ComfyUI");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AddMochaLog($"Disconnect error (can be ignored): {ex.Message}");
                                }

                                await Task.Delay(TimeSpan.FromSeconds(3));

                                try
                                {
                                    await _comfyUIService.ConnectAsync();
                                    AddMochaLog("✓ Reconnected to ComfyUI");
                                    await Task.Delay(TimeSpan.FromSeconds(2));
                                }
                                catch (Exception ex)
                                {
                                    AddMochaLog($"ERROR: Failed to reconnect to ComfyUI: {ex.Message}");
                                    throw new Exception("Cannot reconnect to ComfyUI. Please check ComfyUI window.");
                                }
                            }
                        }

                        // Update workflow parameters for this chunk
                        AddMochaLog($"Updating workflow parameters for chunk {chunkIndex + 1}...");
                        JsonElement updatedWorkflow;
                        try
                        {
                            updatedWorkflow = UpdateMochaWorkflowParameters(workflow, uploadedVideoName, uploadedImageName, startFrame, framesInChunk);
                            AddMochaLog($"Workflow parameters updated successfully for chunk {chunkIndex + 1}");
                        }
                        catch (Exception ex)
                        {
                            AddMochaLog($"ERROR updating workflow parameters for chunk {chunkIndex + 1}: {ex.Message}");
                            throw;
                        }

                        // Execute workflow
                        AddMochaLog($"About to execute workflow for chunk {chunkIndex + 1}...");

                        var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                        {
                            try
                            {
                                if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                                {
                                    var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        MochaProcessingProgress = baseProgress + (percent * 0.6 / totalChunks);
                                        MochaProcessingStatus = $"Chunk {chunkIndex + 1}/{totalChunks}: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                AddMochaLog($"ERROR in progress callback: {ex.Message}");
                            }
                        });

                        AddMochaLog($"Calling ExecuteWorkflowAsync for chunk {chunkIndex + 1}...");
                        string promptId;
                        try
                        {
                            promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress);
                            AddMochaLog($"ExecuteWorkflowAsync returned for chunk {chunkIndex + 1}, prompt ID: {promptId}");
                        }
                        catch (Exception ex)
                        {
                            AddMochaLog($"ERROR in ExecuteWorkflowAsync for chunk {chunkIndex + 1}: {ex.Message}");
                            throw;
                        }

                        // Wait and retrieve the output video
                        AddMochaLog($"Looking for generated video for chunk {chunkIndex + 1}...");

                        string? outputVideo = null;
                        try
                        {
                            outputVideo = await WaitForMochaVideoInOutputFolderAsync(promptId, chunkIndex + 1, totalChunks);
                            AddMochaLog($"WaitForMochaVideoInOutputFolderAsync returned: {(outputVideo != null ? "FOUND" : "NULL")}");
                        }
                        catch (Exception ex)
                        {
                            AddMochaLog($"ERROR in WaitForMochaVideoInOutputFolderAsync: {ex.Message}");
                            outputVideo = null;
                        }

                        if (outputVideo != null && File.Exists(outputVideo))
                        {
                            var chunkFileName = Path.Combine(Path.GetTempPath(), $"mocha_chunk_{chunkIndex:D3}_{Path.GetFileName(outputVideo)}");
                            AddMochaLog($"Copying video from {outputVideo} to {chunkFileName}");

                            try
                            {
                                File.Copy(outputVideo, chunkFileName, true);
                                chunkFiles.Add(chunkFileName);
                                AddMochaLog($"✓ Chunk {chunkIndex + 1}/{totalChunks} saved successfully: {chunkFileName}");
                            }
                            catch (Exception ex)
                            {
                                AddMochaLog($"ERROR copying file: {ex.Message}");
                            }
                        }
                        else
                        {
                            AddMochaLog($"WARNING: No output video found for chunk {chunkIndex + 1}");
                        }

                        AddMochaLog($"✓ Completed chunk {chunkIndex + 1}/{totalChunks}");
                    }
                    catch (Exception ex)
                    {
                        AddMochaLog($"=== ERROR processing chunk {chunkIndex + 1}/{totalChunks} ===");
                        AddMochaLog($"Message: {ex.Message}");
                        AddMochaLog($"Continuing to next chunk if possible...");
                    }
                }

                // Merge chunks
                MochaProcessingProgress = 85;
                MochaProcessingStatus = "Merging video chunks...";
                AddMochaLog("=== Merging video chunks ===");

                if (chunkFiles.Count > 0)
                {
                    var outputPath = Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "Mocha");
                    Directory.CreateDirectory(outputPath);

                    var outputFileName = $"Mocha_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    var finalOutputPath = Path.Combine(outputPath, outputFileName);

                    if (chunkFiles.Count == 1)
                    {
                        File.Copy(chunkFiles[0], finalOutputPath, true);
                        AddMochaLog($"Only one chunk, copying to final output: {finalOutputPath}");
                    }
                    else
                    {
                        MergeMochaVideoChunksWithFFmpeg(chunkFiles, finalOutputPath);
                    }

                    // Clean up chunk files
                    foreach (var chunkFile in chunkFiles)
                    {
                        try
                        {
                            if (File.Exists(chunkFile))
                            {
                                File.Delete(chunkFile);
                            }
                        }
                        catch (Exception ex)
                        {
                            AddMochaLog($"Warning: Could not delete chunk file {chunkFile}: {ex.Message}");
                        }
                    }

                    MochaResultPath = finalOutputPath;
                    HasMochaResult = true;

                    var fileInfo = new FileInfo(finalOutputPath);
                    MochaResultVideoInfo = $"Mocha Video • {fileInfo.Length / 1024 / 1024:F1}MB";

                    MochaProcessingProgress = 100;
                    MochaProcessingStatus = "Complete!";

                    AddMochaLog($"=== Mocha video generation completed successfully ===");
                    AddMochaLog($"Video saved to: {finalOutputPath}");
                }
                else
                {
                    AddMochaLog("ERROR: No video chunks were generated");
                    MochaProcessingStatus = "No output generated";
                }
            }
            catch (Exception ex)
            {
                AddMochaLog($"ERROR: {ex.Message}");
                MochaProcessingStatus = "Error occurred";
                throw;
            }
            finally
            {
                IsProcessingMocha = false;
            }
        }

        private JsonElement UpdateMochaWorkflowParameters(JsonElement workflow, string videoName, string imageName, int startFrame, int frameCount)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            AddMochaLog("=== Updating Mocha workflow parameters ===");
            AddMochaLog($"Start frame: {startFrame}, Frame count: {frameCount}");

            // Update video (node 128)
            if (workflowDict.ContainsKey("128"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["128"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["video"] = videoName;
                        inputs["frame_load_cap"] = frameCount;
                        inputs["skip_first_frames"] = startFrame;
                        node["inputs"] = inputs;
                        workflowDict["128"] = JsonSerializer.SerializeToElement(node);
                        AddMochaLog($"✓ Node 128 (VHS_LoadVideo) - Video: {videoName}, Frame cap: {frameCount}, Skip: {startFrame}");
                    }
                }
            }

            // Update image (node 212)
            if (workflowDict.ContainsKey("212"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["212"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = imageName;
                        node["inputs"] = inputs;
                        workflowDict["212"] = JsonSerializer.SerializeToElement(node);
                        AddMochaLog($"✓ Node 212 (LoadImage) - Image: {imageName}");
                    }
                }
            }

            AddMochaLog("=== Mocha workflow parameters updated successfully ===");

            var updatedWorkflow = JsonSerializer.SerializeToElement(workflowDict);
            return updatedWorkflow;
        }

        private async Task<string?> WaitForMochaVideoInOutputFolderAsync(string promptId, int chunkIndex, int totalChunks)
        {
            var settings = _settingsService.Settings;
            if (settings == null)
            {
                AddMochaLog("ERROR: Settings object is null");
                return null;
            }

            // Determine the output folder based on whether ComfyUI is local or remote
            string outputFolder;
            var baseUrl = settings.BaseUrl ?? "http://127.0.0.1:8188";
            var isLocalComfyUI = baseUrl.Contains("127.0.0.1") || baseUrl.Contains("localhost");

            if (isLocalComfyUI)
            {
                // Local ComfyUI - use the configured output folder path
                if (string.IsNullOrEmpty(settings.OutputFolderPath))
                {
                    AddMochaLog("ERROR: ComfyUI output path not configured in settings");
                    return null;
                }
                outputFolder = settings.OutputFolderPath;
                AddMochaLog($"Using local ComfyUI output folder: {outputFolder}");
            }
            else
            {
                // Remote ComfyUI - use the remote output folder path
                if (string.IsNullOrEmpty(settings.RemoteOutputFolderPath))
                {
                    AddMochaLog("ERROR: Remote ComfyUI output path not configured in settings");
                    return null;
                }
                outputFolder = settings.RemoteOutputFolderPath;
                AddMochaLog($"Using remote ComfyUI output folder: {outputFolder}");
            }

            if (!Directory.Exists(outputFolder))
            {
                AddMochaLog($"ERROR: Output folder does not exist: {outputFolder}");
                return null;
            }

            const int maxWaitTime = 600; // 10 minutes max
            var waitTime = 0;
            var checkInterval = 2; // Check every 2 seconds

            AddMochaLog($"Waiting for output video (chunk {chunkIndex}/{totalChunks}, prompt ID: {promptId})...");

            while (waitTime < maxWaitTime)
            {
                await Task.Delay(checkInterval * 1000);
                waitTime += checkInterval;

                try
                {
                    // Look for the most recent video file with the WanVideo_MoCha prefix
                    var videoFiles = Directory.GetFiles(outputFolder, "WanVideo_MoCha*.mp4")
                        .Concat(Directory.GetFiles(outputFolder, "WanVideo_MoCha*.webm"))
                        .OrderByDescending(f => File.GetCreationTime(f));

                    foreach (var videoFile in videoFiles)
                    {
                        // Check if file is recent and not locked
                        var fileInfo = new FileInfo(videoFile);
                        if (fileInfo.CreationTime > DateTime.Now.AddMinutes(-15))
                        {
                            try
                            {
                                // Try to open the file to check if it's still being written
                                using (var stream = File.Open(videoFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    // If we can open it, it's probably complete
                                }

                                AddMochaLog($"Found output video: {Path.GetFileName(videoFile)}");
                                return videoFile;
                            }
                            catch (IOException)
                            {
                                // File is still being written, continue waiting
                                AddMochaLog($"Video file found but still being written: {Path.GetFileName(videoFile)}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddMochaLog($"Error checking for output video: {ex.Message}");
                }

                if (waitTime % 10 == 0)
                {
                    AddMochaLog($"Still waiting for output video... ({waitTime}s elapsed)");
                }
            }

            AddMochaLog($"Timeout waiting for output video after {maxWaitTime}s");
            return null;
        }

        private void MergeMochaVideoChunksWithFFmpeg(List<string> chunkFiles, string outputPath)
        {
            try
            {
                var ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    AddMochaLog("ERROR: ffmpeg not found. Cannot merge video chunks.");
                    throw new InvalidOperationException("ffmpeg is required to merge video chunks.");
                }

                // Create a temporary file list for ffmpeg
                var listFile = Path.Combine(Path.GetTempPath(), $"ffmpeg_list_{Guid.NewGuid()}.txt");
                using (var writer = new StreamWriter(listFile))
                {
                    foreach (var chunkFile in chunkFiles)
                    {
                        writer.WriteLine($"file '{chunkFile.Replace("\\", "/")}'");
                    }
                }

                AddMochaLog($"Merging {chunkFiles.Count} video chunks using ffmpeg...");

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return;
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    AddMochaLog($"ffmpeg merge output: {output}");
                    if (!string.IsNullOrEmpty(error))
                    {
                        AddMochaLog($"ffmpeg merge errors: {error}");
                    }
                }

                // Clean up the list file
                try
                {
                    File.Delete(listFile);
                }
                catch
                {
                    // Ignore
                }

                AddMochaLog($"Video merged successfully: {outputPath}");
            }
            catch (Exception ex)
            {
                AddMochaLog($"ERROR merging video chunks: {ex.Message}");
                throw;
            }
        }

        private void AddMochaLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            MochaLogOutput += $"[{timestamp}] {message}\n";
        }

        private void PlayMochaVideo()
        {
            if (HasMochaResult && File.Exists(MochaResultPath))
            {
                try
                {
                    var window = System.Windows.Application.Current.MainWindow;
                    if (window != null)
                    {
                        var player = window.FindName("MochaVideoPlayer") as MediaElement;
                        if (player != null)
                        {
                            player.Play();
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddMochaLog($"Error playing video: {ex.Message}");
                }
            }
        }

        private void OpenMochaResultFolder()
        {
            if (HasMochaResult && File.Exists(MochaResultPath))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{MochaResultPath}\"");
                }
                catch (Exception ex)
                {
                    AddMochaLog($"Error opening folder: {ex.Message}");
                }
            }
        }

        private void SendMochaToEditCamera()
        {
            if (HasMochaResult)
            {
                SetImagePath(MochaImagePath);
                AddMochaLog("Video sent to Edit Camera");
            }
        }
    }
}
