using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

        // Workflow selection
        private string _selectedWorkflow = "ltx2_i2v";
        private bool _useLTXWorkflow = true;

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
            PauseStoryQueueCommand = new RelayCommand(PauseStoryQueue, () => IsProcessingStoryQueue && !IsStoryQueuePaused);
            ResumeStoryQueueCommand = new RelayCommand(ResumeStoryQueue, () => IsProcessingStoryQueue && IsStoryQueuePaused);

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
                    CommandManager.InvalidateRequerySuggested();
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
                    CommandManager.InvalidateRequerySuggested();
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

        public bool CanGenerateVideo => HasFirstFrameImage && HasLastFrameImage &&
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
                    OnPropertyChanged(nameof(CanGenerateVideo));
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

        public bool IsQueuePaused
        {
            get => _isQueuePaused;
            set
            {
                if (_isQueuePaused != value)
                {
                    _isQueuePaused = value;
                    OnPropertyChanged();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool CanProcessQueue => PromptQueue.Any(x => x.Status == QueueItemStatus.Pending) && !IsProcessingQueue && !IsProcessing;
        public bool CanAddToQueue => !string.IsNullOrWhiteSpace(NewQueuePrompt) && (HasImage || (HasFirstFrameImage && HasLastFrameImage));
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

        public bool IsStoryQueuePaused
        {
            get => _isStoryQueuePaused;
            set
            {
                if (_isStoryQueuePaused != value)
                {
                    _isStoryQueuePaused = value;
                    OnPropertyChanged();
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
                    _selectedWorkflow = value ? "ltx2_i2v" : "painter";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WorkflowDisplay));
                    OnPropertyChanged(nameof(WorkflowIndicator));
                    OnPropertyChanged(nameof(SelectedWorkflowIndex));
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
        public string WorkflowIndicator => UseLTXWorkflow ? "LTXV" : "Painter";
        public int SelectedWorkflowIndex
        {
            get => UseLTXWorkflow ? 0 : 1;
            set => UseLTXWorkflow = value == 0;
        }

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
        public ICommand GenerateVideoCommand { get; }
        public ICommand PlayVideoCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SendToEditCameraCommand { get; }

        // Image analysis commands
        public ICommand AnalyzeImageCommand { get; }
        public ICommand AnalyzeFirstFrameImageCommand { get; }
        public ICommand SendAnalysisToQueueCommand { get; }
        public ICommand OpenLMStudioSettingsCommand { get; }
        public ICommand CopyAnalysisCommand { get; }

        // Queue commands
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ReprocessItemCommand { get; }
        public ICommand ReprocessAllFailedCommand { get; }
        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

        // Story Video Generator commands
        public ICommand SelectStoryPromptJsonCommand { get; }
        public ICommand SelectStoryImagesFolderCommand { get; }
        public ICommand LoadStoryQueueCommand { get; }
        public ICommand ProcessStoryQueueCommand { get; }
        public ICommand ClearStoryQueueCommand { get; }
        public ICommand PauseStoryQueueCommand { get; }
        public ICommand ResumeStoryQueueCommand { get; }

        // Workflow toggle command
        public ICommand ToggleWorkflowCommand { get; }

        #endregion

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

            await AnalyzeImageInternalAsync(FirstFrameImagePath);
        }

        private async Task AnalyzeImageInternalAsync(string imagePath)
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

                var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
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
                if (UseLTXWorkflow)
                {
                    var ltxPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "ltx_action_video_system_prompt.md");
                    if (File.Exists(ltxPromptPath))
                    {
                        analysisPrompt = await File.ReadAllTextAsync(ltxPromptPath, _analysisCancellationTokenSource.Token);
                        AddLog("Using LTX-2 Action Video system prompt");
                    }
                    else
                    {
                        AddLog($"WARNING: LTX action video prompt not found at {ltxPromptPath}, using default");
                        analysisPrompt = "Describe this image in detail for video generation.";
                    }
                }
                else
                {
                    analysisPrompt = "Describe this image in detail for video generation.";
                    AddLog("Using default image analysis prompt");
                }

                var analysisResult = await _lmStudioService.AnalyzeImageAsync(
                    selectedModel,
                    imagePath,
                    analysisPrompt,
                    maxTokens: 2000,
                    _analysisCancellationTokenSource.Token);

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
                CommandManager.InvalidateRequerySuggested();
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
                    Status = QueueItemStatus.Pending
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
                    Status = QueueItemStatus.Pending
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
                item.Status == QueueItemStatus.Pending);

            if (hasSameImages || Seed == 0)
            {
                var random = new Random();
                return (long)(random.NextDouble() * long.MaxValue);
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
                    Status = QueueItemStatus.Pending
                };

                PromptQueue.Add(queueItem);
                NewQueuePrompt = string.Empty;
                UpdateQueueStatus();
                SaveQueueToFile();
                AddLog($"Added to queue");
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

        private async Task ProcessQueueAsync()
        {
            if (!PromptQueue.Any()) return;

            IsProcessingQueue = true;
            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("VideoGenerator", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                IsProcessingQueue = false;
                return;
            }

            AddLog($"=== Starting to process queue with {PromptQueue.Count} items ===");

            using (lease)
            {
                try
                {
                    var pendingItems = PromptQueue.Where(x => x.Status == QueueItemStatus.Pending).ToList();

                    foreach (var item in pendingItems)
                    {
                        if (IsProcessing) break;

                        _pauseEvent.Wait(CancellationToken.None);

                        try
                        {
                            item.Status = QueueItemStatus.Processing;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            AddLog($"Processing queue item...");

                            // Generate video for this item
                            await GenerateVideoForQueueItemAsync(item);

                            if (HasResult)
                            {
                                item.Status = QueueItemStatus.Completed;
                                item.VideoPath = ResultVideoPath;
                                AddLog($"Queue item completed");
                            }
                            else
                            {
                                item.Status = QueueItemStatus.Failed;
                                AddLog($"Queue item failed");
                            }

                            HasResult = false;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            await Task.Delay(1000);
                        }
                        catch (Exception ex)
                        {
                            item.Status = QueueItemStatus.Failed;
                            UpdateQueueStatus();
                            SaveQueueToFile();
                            AddLog($"Error processing queue item: {ex.Message}");
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
                    IsQueuePaused = false;
                    _pauseEvent.Set();
                    CommandManager.InvalidateRequerySuggested();
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
                AddLog($"Last frame: {Path.GetFileName(LastFrameImagePath)}");
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
                var workflowFileName = UseLTXWorkflow ? "LTXV-DoEverything-v2.json" : "painteri2vAPI.json";
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

                AddLog("Uploading last frame image to ComfyUI...");
                var uploadedLastFrameImageName = await _comfyUIService.UploadImageAsync(LastFrameImagePath);
                if (string.IsNullOrEmpty(uploadedLastFrameImageName))
                {
                    AddLog("ERROR: Last frame image upload failed");
                    return;
                }
                AddLog($"Last frame uploaded: {uploadedLastFrameImageName}");

                // Update workflow parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedFirstFrameImageName, uploadedLastFrameImageName);

                // Execute workflow
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 30;
                AddLog("Executing video generation workflow...");

                // Record existing video files BEFORE execution
                var existingFilesBeforeExecution = GetExistingVideoFiles("*.mp4", "testrun", "testrun/vid", "video");
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
                    "testrun", "testrun/vid", "video");

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
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

            // Update first frame image (node 106 for LTXV-DoEverything-v2)
            string[] firstFrameNodes = { "106" };
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

            // Update last frame image (node 35 for LTXV-DoEverything-v2)
            string[] lastFrameNodes = { "35" };
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

            // Update positive prompt (node 59 for LTXV-DoEverything-v2 uses "value" field)
            string[] positivePromptNodes = UseLTXWorkflow ? new[] { "59", "121", "92:3" } : new[] { "93", "62", "6" };
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

            // Update negative prompt
            string[] negativePromptNodes = { "89", "7" };
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
                            inputs["text"] = NegativePrompt;
                            node["inputs"] = inputs;
                            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                            AddLog($"✓ Node {nodeId} (Negative Prompt) - Updated");
                        }
                    }
                }
            }

            // Update LTXV parameters (frame count, FPS, seed)
            if (UseLTXWorkflow)
            {
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

            AddLog("Workflow parameters updated successfully");
            return JsonSerializer.SerializeToElement(workflowDict);
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

            OnPropertyChanged(nameof(HasFailedItems));
            OnPropertyChanged(nameof(CanProcessQueue));
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task ReprocessItemAsync(QueueItem? item)
        {
            if (item == null) return;

            AddLog($"Reprocessing failed item...");
            item.Status = QueueItemStatus.Processing;
            UpdateQueueStatus();
            SaveQueueToFile();

            await GenerateVideoForQueueItemAsync(item);

            if (HasResult)
            {
                item.Status = QueueItemStatus.Completed;
                item.VideoPath = ResultVideoPath;
                AddLog($"Item reprocessed successfully");
            }
            else
            {
                item.Status = QueueItemStatus.Failed;
                AddLog($"Item reprocessing failed");
            }

            HasResult = false;
            UpdateQueueStatus();
            SaveQueueToFile();
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
                $"Reprocess {failedItems.Count} failed item(s)?",
                "Confirm Reprocess All",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.OK) return;

            AddLog($"=== Starting to reprocess {failedItems.Count} failed items ===");

            foreach (var item in failedItems)
            {
                if (item.Status == QueueItemStatus.Failed)
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
                "Select Story Prompts JSON File",
                "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
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
                    System.Windows.MessageBox.Show("No prompts found in the JSON file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var imageFiles = Directory.GetFiles(StoryImagesFolderPath, "*.png")
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpg"))
                    .Concat(Directory.GetFiles(StoryImagesFolderPath, "*.jpeg"))
                    .OrderBy(f => f)
                    .ToList();

                StoryVideoQueue.Clear();

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
                SaveStoryQueueToFile();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading story queue: {ex.Message}");
                System.Windows.MessageBox.Show($"Error loading story queue:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessStoryQueueAsync()
        {
            if (!CanProcessStoryQueue) return;

            AddLog("Waiting for other workflows to finish...");

            WorkflowQueueCoordinator.WorkflowLease lease;
            try
            {
                lease = await _workflowCoordinator.AcquireAsync("StoryVideo", CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
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
                        _storyPauseEvent.Wait(CancellationToken.None);

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
                    CommandManager.InvalidateRequerySuggested();
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
                // Restore original values
                VideoPrompt = originalPrompt;
                FirstFrameImagePath = originalFirstFramePath;
                LastFrameImagePath = originalLastFramePath;
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
            CommandManager.InvalidateRequerySuggested();
        }

        private void ClearStoryQueue()
        {
            StoryVideoQueue.Clear();
            UpdateStoryQueueStatus();
            AddLog("Story queue cleared");

            // Delete the saved queue file
            var queueFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "story_video_queue.json");
            if (File.Exists(queueFilePath))
            {
                File.Delete(queueFilePath);
            }
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

        #endregion

        #region Queue Persistence

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
                        if (item.Status == QueueItemStatus.Processing)
                        {
                            item.Status = QueueItemStatus.Failed;
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
