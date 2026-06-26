using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public enum TextGeneratorWorkflow
    {
        Zimage,
        Qwen2512,
        Klien,
        Anima,
        Krea2
    }

    public class ImageGeneratorViewModel : BasePromptViewModel, IDisposable
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;
        private bool _disposed = false;

        private string _imagePrompt = string.Empty;
        private int _aspectRatioIndex = 0;
        private int _steps = 9;
        private double _cfg = 1.5;
        private long _seed = 0;
        private double _denoise = 1.0;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private BitmapImage? _resultImageSource;
        private string _imageInfo = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private ObservableCollection<string> _availableLoras = new();
        private string _selectedLora = string.Empty;
        private bool _loraEnabled = false;
        private ObservableCollection<string> _kreaLoras = new();
        private string _selectedKreaLora = string.Empty;
        private string _kreaLoraSubfolder = "krea2";
        private TextGeneratorWorkflow _selectedWorkflow = TextGeneratorWorkflow.Zimage;
        private JsonElement _lastWorkflow;

        // Style fields (for Zimage ZStyles)
        private List<StyleInfo> _allStyles = new List<StyleInfo>();
        private int _selectedStyleIndex = 0;

        // Queue fields
        private ObservableCollection<ImagePromptQueueItem> _promptQueue = new();
        private ImagePromptQueueItem? _selectedQueueItem;
        private bool _isProcessingQueue = false;
        private bool _isQueuePaused = false;
        private bool _isWaitingForLease = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private CancellationTokenSource? _queueCancellationTokenSource;

        // Nested ViewModels for tabs
        private ImageAnalyzerViewModel _analyzer;
        private FlipPixViewModel _cameraEdit;
        private StoryImageGeneratorQViewModel _storyGeneratorQ;
        private StoryImageGeneratorAmateurViewModel _storyGeneratorAmateur;
        private AmateurGeneratorViewModel _amateurGenerator;
        private CameraAngleViewModel _cameraAngle;
        private InpaintEditorViewModel _inpaintEditor;
        private KleinInpaintViewModel _kleinInpaintEditor;
        private KleinControlViewModel _kleinControl;
        private IdeogramViewModel _ideogram;
        private QwenEditViewModel _qwenEdit;
        private RestoreViewModel _restore;


        public ImageGeneratorViewModel(FlipPix.ComfyUI.Services.ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null, IPromptService? promptService = null)
            : base(promptService ?? new PromptService(logger), logger, "ImageGenerator")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = serviceProvider?.GetRequiredService<WorkflowQueueCoordinator>() ?? throw new InvalidOperationException("WorkflowQueueCoordinator is required");

            // Load default prompt from settings
            _imagePrompt = settingsService.Settings.DefaultImagePrompt;

            // Get IFileDialogService, LoraManager, and ComfyUIImageRetriever from service provider
            var fileDialogService = serviceProvider?.GetRequiredService<IFileDialogService>() ?? throw new InvalidOperationException("IFileDialogService is required");
            var loraManager = serviceProvider?.GetRequiredService<LoraManager>() ?? throw new InvalidOperationException("LoraManager is required");
            var imageRetriever = serviceProvider?.GetRequiredService<ComfyUIImageRetriever>() ?? throw new InvalidOperationException("ComfyUIImageRetriever is required");

            // Initialize nested ViewModels
            var lmStudioService = serviceProvider?.GetRequiredService<LMStudioService>();
            _analyzer = new ImageAnalyzerViewModel(comfyUIService, lmStudioService ?? throw new InvalidOperationException("LMStudioService is required"), logger, settingsService, _workflowCoordinator, fileDialogService, promptService);
            _cameraEdit = new FlipPixViewModel(comfyUIService, logger, settingsService, serviceProvider, promptService, fileDialogService);
            _storyGeneratorQ = new StoryImageGeneratorQViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService, loraManager, imageRetriever, lmStudioService ?? throw new InvalidOperationException("LMStudioService is required"));
            _storyGeneratorAmateur = new StoryImageGeneratorAmateurViewModel(comfyUIService, logger, settingsService, _workflowCoordinator, fileDialogService, loraManager, imageRetriever);
            _amateurGenerator = new AmateurGeneratorViewModel(comfyUIService, logger, settingsService, promptService, loraManager, imageRetriever, _workflowCoordinator, lmStudioService, fileDialogService);
            _cameraAngle = new CameraAngleViewModel(comfyUIService, logger, settingsService, fileDialogService, imageRetriever);
            _inpaintEditor = new InpaintEditorViewModel(comfyUIService, logger, settingsService, fileDialogService);
            _kleinInpaintEditor = new KleinInpaintViewModel(comfyUIService, logger, settingsService, fileDialogService);
            var videoAnalysisService = serviceProvider?.GetRequiredService<VideoAnalysisService>() ?? throw new InvalidOperationException("VideoAnalysisService is required");
            _kleinControl = new KleinControlViewModel(comfyUIService, logger, settingsService, fileDialogService, videoAnalysisService);
            _ideogram = new IdeogramViewModel(comfyUIService, logger, settingsService, fileDialogService, lmStudioService ?? throw new InvalidOperationException("LMStudioService is required"), _workflowCoordinator);
            _qwenEdit = new QwenEditViewModel(comfyUIService, logger, settingsService, fileDialogService, lmStudioService ?? throw new InvalidOperationException("LMStudioService is required"), _workflowCoordinator);
            _restore = new RestoreViewModel(comfyUIService, logger, settingsService, fileDialogService);

            // Keep the shared Tab 1 settings panel pointed at the active mode's VM.
            _analyzer.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ImageAnalyzerViewModel.IsImageAnalysisMode) ||
                    e.PropertyName == nameof(ImageAnalyzerViewModel.IsTextPromptMode))
                {
                    OnPropertyChanged(nameof(ActiveGenerationVM));
                    OnPropertyChanged(nameof(HasActiveResultImage));
                    OnPropertyChanged(nameof(ActiveResultImagePath));
                    CommandManager.InvalidateRequerySuggested();
                }
                else if (e.PropertyName == nameof(ImageAnalyzerViewModel.HasResultImage) ||
                         e.PropertyName == nameof(ImageAnalyzerViewModel.ResultImagePath))
                {
                    // Keep the "send to ..." buttons enabled/disabled in sync with the
                    // analysis-mode result so they work in both Text Prompt and Analysis modes.
                    OnPropertyChanged(nameof(HasActiveResultImage));
                    OnPropertyChanged(nameof(ActiveResultImagePath));
                    CommandManager.InvalidateRequerySuggested();
                }
            };

            // Initialize commands
            SelectNavGroupCommand = new RelayCommand<string>(g =>
            {
                if (int.TryParse(g, out var group)) SelectedNavGroup = group;
            });
            SelectEditorModeCommand = new RelayCommand<string>(m =>
            {
                if (int.TryParse(m, out var mode)) EditorMode = mode;
            });
            GenerateImageCommand = new RelayCommand(async () => await GenerateImageAsync(), () => CanGenerate);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResultImage);
            // No CanExecute predicate: CommunityToolkit's RelayCommand doesn't auto-requery
            // (it ignores CommandManager.InvalidateRequerySuggested), so a predicate here would
            // leave WPF coercing the button disabled from its stale initial CanExecute=false.
            // The buttons' IsEnabled bindings (HasResultImage / Analyzer.HasResultImage) gate
            // visibility, and each handler guards against an empty path.
            SendToCameraAngleCommand = new RelayCommand(SendToCameraAngle);
            SendToVideoGeneratorCommand = new RelayCommand(SendToVideoGenerator);
            SendToStoryCommand = new RelayCommand(SendToStory);
            OpenKeyframesInFflfSeedHunterCommand = new RelayCommand(OpenKeyframesInFflfSeedHunter);
            NavigateToImageAnalyzerCommand = new RelayCommand(NavigateToImageAnalyzer);
            NavigateToVideoGeneratorCommand = new RelayCommand(NavigateToVideoGenerator);
                NavigateToStoryVideoCommand = new RelayCommand(NavigateToStoryVideo);
            NavigateToEnhanceVideoCommand = new RelayCommand(NavigateToEnhanceVideo);
            RefreshLorasCommand = new RelayCommand(RefreshLoras);
            RefreshKreaLorasCommand = new RelayCommand(RefreshKreaLoras);

            // Queue commands
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            SelectQueueResultCommand = new RelayCommand<ImagePromptQueueItem>(SelectQueueResult, (item) => item != null);
            RemoveFromQueueCommand = new RelayCommand<ImagePromptQueueItem>(RemoveFromQueue, (item) => item != null);
            RetryQueueItemCommand = new RelayCommand<ImagePromptQueueItem>(RetryQueueItem, (item) => item != null);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => CanClearQueue);
            ProcessQueueCommand = new RelayCommand(async () =>
            {
                try
                {
                    await ProcessQueueAsync();
                }
                catch (Exception ex)
                {
                    IsProcessingQueue = false;
                    IsWaitingForLease = false;
                    IsQueuePaused = false;
                    _pauseEvent.Set();
                    AddLog($"ERROR: Queue processing failed unexpectedly: {ex.Message}");
                    NotifyActionCommands();
                }
            }, () => CanProcessQueue);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);
            CancelQueueCommand = new RelayCommand(CancelQueue, () => IsProcessingQueue);

            // Load available Loras
            LoadAvailableLoras();
            LoadKreaLoras();

            // Load workflow styles
            LoadWorkflowStyles();

            ScheduleQueueLoad();

            AddLog("Image Generator initialized");

            // Ensure commands are properly enabled on startup
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                NotifyActionCommands();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void NotifyActionCommands()
        {
            if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(NotifyActionCommands);
                return;
            }
            GenerateImageCommand.NotifyCanExecuteChanged();
            AddToQueueCommand.NotifyCanExecuteChanged();
            ProcessQueueCommand.NotifyCanExecuteChanged();
            (CancelQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (PauseQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ResumeQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }

        // Properties
        public string ImagePrompt
        {
            get => _imagePrompt;
            set
            {
                _imagePrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanAddToQueue));
                NotifyActionCommands();
            }
        }

        public override int Steps
        {
            get => _steps;
            set
            {
                _steps = value;
                OnPropertyChanged();
            }
        }

        public override double Cfg
        {
            get => _cfg;
            set
            {
                _cfg = value;
                OnPropertyChanged();
            }
        }

        public override double Denoise
        {
            get => _denoise;
            set
            {
                _denoise = value;
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
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(CanCancel));
                NotifyActionCommands();
            }
        }

        public bool CanCancel => IsProcessing;

        // Nested ViewModel properties
        public ImageAnalyzerViewModel Analyzer => _analyzer;
        public FlipPixViewModel CameraEdit => _cameraEdit;
        public StoryImageGeneratorQViewModel StoryGeneratorQ => _storyGeneratorQ;
        public StoryImageGeneratorAmateurViewModel StoryGeneratorAmateur => _storyGeneratorAmateur;
        public AmateurGeneratorViewModel AmateurGenerator => _amateurGenerator;
        public CameraAngleViewModel CameraAngle => _cameraAngle;
        public InpaintEditorViewModel InpaintEditor => _inpaintEditor;
        public KleinInpaintViewModel KleinInpaintEditor => _kleinInpaintEditor;
        public KleinControlViewModel KleinControl => _kleinControl;
        public IdeogramViewModel Ideogram => _ideogram;
        public QwenEditViewModel QwenEdit => _qwenEdit;
        public RestoreViewModel Restore => _restore;

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

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                _hasResultImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActiveResultImage));
                OnPropertyChanged(nameof(ActiveResultImagePath));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // The result image for whichever mode is currently active. In Image Analysis
        // mode the latest result lives on the Analyzer sub-VM; in Text Prompt mode it
        // lives on this VM. The "send to ..." buttons use these so they work in both.
        public bool HasActiveResultImage =>
            Analyzer.IsImageAnalysisMode ? Analyzer.HasResultImage : HasResultImage;

        public string ActiveResultImagePath =>
            Analyzer.IsImageAnalysisMode ? Analyzer.ResultImagePath : ResultImagePath;

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                _resultImagePath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveResultImagePath));
            }
        }

        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set
            {
                _resultImageSource = value;
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

        public bool CanGenerate => !string.IsNullOrEmpty(ImagePrompt);

        // Lora Properties
        public ObservableCollection<string> AvailableLoras
        {
            get => _availableLoras;
            set
            {
                _availableLoras = value;
                OnPropertyChanged();
            }
        }

        public string SelectedLora
        {
            get => _selectedLora;
            set
            {
                _selectedLora = value;
                OnPropertyChanged();
            }
        }

        public bool LoraEnabled
        {
            get => _loraEnabled;
            set
            {
                _loraEnabled = value;
                OnPropertyChanged();
            }
        }

        // Workflow Properties
        public TextGeneratorWorkflow SelectedWorkflow
        {
            get => _selectedWorkflow;
            set
            {
                if (_selectedWorkflow != value)
                {
                    _selectedWorkflow = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowLoraOptions));
                    OnPropertyChanged(nameof(ShowKreaLoraOptions));
                    OnPropertyChanged(nameof(ShowStyleOptions));
                    OnPropertyChanged(nameof(ShowSamplerSettings));
                }
            }
        }

        public bool ShowLoraOptions => SelectedWorkflow == TextGeneratorWorkflow.Zimage;

        // Krea2 LoRA Properties (loaded from the <loras>/krea2 subfolder)
        public ObservableCollection<string> KreaLoras
        {
            get => _kreaLoras;
            set { _kreaLoras = value; OnPropertyChanged(); }
        }

        public string SelectedKreaLora
        {
            get => _selectedKreaLora;
            set { _selectedKreaLora = value; OnPropertyChanged(); }
        }

        public bool ShowKreaLoraOptions => SelectedWorkflow == TextGeneratorWorkflow.Krea2;

        // Style properties (for Zimage ZStyles)
        public bool ShowStyleOptions => SelectedWorkflow == TextGeneratorWorkflow.Zimage;

        // Anima and Krea2 are turbo/fixed-schedule workflows: steps/cfg/sampler are
        // baked into the JSON and changing them breaks the result, so hide the sampler panel.
        public bool ShowSamplerSettings => SelectedWorkflow != TextGeneratorWorkflow.Anima
                                           && SelectedWorkflow != TextGeneratorWorkflow.Krea2;

        public int SelectedStyleIndex
        {
            get => _selectedStyleIndex;
            set
            {
                _selectedStyleIndex = value;
                OnPropertyChanged();
            }
        }

        public string[] StyleNames => _allStyles.Select(s => s.Name).ToArray();

        public StyleInfo? SelectedStyle => _allStyles.Count > 0
            ? _allStyles[Math.Min(SelectedStyleIndex, _allStyles.Count - 1)]
            : null;

        // Commands
        public RelayCommand GenerateImageCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand OpenResultImageCommand { get; }
        public ICommand SendToCameraAngleCommand { get; }
        public ICommand SendToVideoGeneratorCommand { get; }
        public ICommand SendToStoryCommand { get; }
        public ICommand OpenKeyframesInFflfSeedHunterCommand { get; }
        public ICommand NavigateToImageAnalyzerCommand { get; }
        public ICommand NavigateToVideoGeneratorCommand { get; }
              public ICommand NavigateToStoryVideoCommand { get; }
        public ICommand NavigateToEnhanceVideoCommand { get; }
        public ICommand RefreshLorasCommand { get; }
        public ICommand RefreshKreaLorasCommand { get; }

        // Queue commands
        public RelayCommand AddToQueueCommand { get; }
        public ICommand SelectQueueResultCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand RetryQueueItemCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public RelayCommand ProcessQueueCommand { get; }
        public ICommand CancelQueueCommand { get; }

        // Queue properties
        public ObservableCollection<ImagePromptQueueItem> PromptQueue
        {
            get => _promptQueue;
            set
            {
                _promptQueue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQueueItems));
                OnPropertyChanged(nameof(QueueCount));
            }
        }

        public ImagePromptQueueItem? SelectedQueueItem
        {
            get => _selectedQueueItem;
            set
            {
                _selectedQueueItem = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                _isProcessingQueue = value;
                OnPropertyChanged();
                NotifyActionCommands();
            }
        }

        public bool IsWaitingForLease
        {
            get => _isWaitingForLease;
            set
            {
                _isWaitingForLease = value;
                OnPropertyChanged();
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

        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

        public bool HasQueueItems => _promptQueue.Any();
        public int QueueCount => _promptQueue.Count;
        public int PendingQueueCount => _promptQueue.Count(q => q.Status == "Pending");
        public int CompletedQueueCount => _promptQueue.Count(q => q.Status == "Completed");

        public bool CanAddToQueue => !string.IsNullOrEmpty(ImagePrompt);
        public bool CanRemoveFromQueue => SelectedQueueItem != null;
        public bool CanClearQueue => _promptQueue.Any();
        public bool CanProcessQueue => _promptQueue.Any(q => q.Status == "Pending") && !IsProcessingQueue;

        // Navigation properties
        private int _selectedTabIndex = 0; // Default to Text Generation tab

        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        // Top-level navigation groups: 0 = Create, 1 = Edit, 2 = Advanced.
        // Only the tabs belonging to the active group are visible, so the user
        // sees 2-4 destinations at a time instead of all 10 at once.
        private int _selectedNavGroup = 0;

        public int SelectedNavGroup
        {
            get => _selectedNavGroup;
            set
            {
                if (_selectedNavGroup != value)
                {
                    _selectedNavGroup = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsCreateGroup));
                    OnPropertyChanged(nameof(IsEditGroup));
                    OnPropertyChanged(nameof(IsAdvancedGroup));
                    // Land on the first tab of the chosen group so the content
                    // area is never left on a now-hidden tab (which renders blank).
                    SelectedTabIndex = value switch
                    {
                        1 => EditGroupFirstTab,
                        2 => AdvancedGroupFirstTab,
                        _ => CreateGroupFirstTab,
                    };
                }
            }
        }

        // First tab index of each group (positional within the TabControl).
        // Create -> Image Generator (0), Edit -> Editor (4), Advanced -> Camera Angle (3).
        private const int CreateGroupFirstTab = 0;
        private const int EditGroupFirstTab = 4;
        private const int AdvancedGroupFirstTab = 3;

        public bool IsCreateGroup => _selectedNavGroup == 0;
        public bool IsEditGroup => _selectedNavGroup == 1;
        public bool IsAdvancedGroup => _selectedNavGroup == 2;

        // Editor tab sub-mode: 0 = manual mask painting, 1 = Florence2/SAM2 auto-detect.
        // (Merges what used to be the separate "Editor" and "Editor 2" tabs.)
        private int _editorMode = 0;

        public int EditorMode
        {
            get => _editorMode;
            set
            {
                if (_editorMode != value)
                {
                    _editorMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPaintEditor));
                    OnPropertyChanged(nameof(IsAutoDetectEditor));
                }
            }
        }

        public bool IsPaintEditor => _editorMode == 0;
        public bool IsAutoDetectEditor => _editorMode == 1;

        public ICommand SelectEditorModeCommand { get; }

        // Pills invoke this with the group index ("0"/"1"/"2") as parameter.
        public ICommand SelectNavGroupCommand { get; }

        // Backs the single shared "Generation Settings" panel on Tab 1: the main
        // generator in Text Prompt mode, the analyzer in Image Analysis mode.
        // (Both expose identically-named settings members, so one panel serves both.)
        public object ActiveGenerationVM => Analyzer.IsImageAnalysisMode ? (object)Analyzer : this;

        // The shared "Generate" button binds this; each mode maps it to its own action.
        public ICommand PrimaryGenerateCommand => GenerateImageCommand;


        // Methods
        private async Task GenerateImageAsync()
        {
            // If already processing, add to queue instead
            if (IsProcessing)
            {
                AddToQueue();
                // Auto-start queue processing if not already processing queue
                if (!IsProcessingQueue && PromptQueue.Any(q => q.Status == "Pending"))
                {
                    _ = ProcessQueueAsync();
                }
                return;
            }

            if (!CanGenerate) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                AddLog("=== Starting image generation ===");
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {ImagePrompt}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }
                else
                {
                    AddLog("ComfyUI already connected");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow based on selected workflow
                string workflowPath;

                switch (SelectedWorkflow)
                {
                    case TextGeneratorWorkflow.Qwen2512:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "qwen2512API-text.json");
                        AddLog("Using Qwen2512 workflow");
                        break;

                    case TextGeneratorWorkflow.Klien:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "klein", "KlienX3n-Text-Ultimate-API.json");
                        AddLog("Using Klien workflow");
                        break;

                    case TextGeneratorWorkflow.Anima:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "anima", "Anima.json");
                        AddLog("Using Anima workflow");
                        break;

                    case TextGeneratorWorkflow.Krea2:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "krea", "krea2RealismV1_krea2RealismV1WF.json");
                        AddLog("Using Krea2 workflow");
                        break;

                    case TextGeneratorWorkflow.Zimage:
                    default:
                        if (SelectedStyle != null)
                        {
                            workflowPath = SelectedStyle.WorkflowFile;
                            AddLog($"Using Zimage workflow with style: {SelectedStyle.Name}");
                        }
                        else
                        {
                            workflowPath = WorkflowLocator.Resolve("workflow", "image", "zimage", "base", "Zib-Zit.json");
                            AddLog("No style selected, falling back to Zib-Zit.json");
                        }
                        break;
                }
                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {workflowPath}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Update workflow with parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;

                var updatedWorkflow = UpdateWorkflowParameters(workflow);
                _lastWorkflow = updatedWorkflow; // Store for later use in image retrieval

                // Execute workflow
                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                AddLog("Executing workflow in ComfyUI...");

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

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images from ComfyUI output folder
                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                // Add debug info about what we're doing
                AddLog("=== DEBUG: About to call GetOutputImagesFromComfyUI ===");

                // Retry image retrieval with delays to give ComfyUI time to write the file
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds (20 retries × 5s)

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var prefix = SelectedWorkflow switch
                    {
                        TextGeneratorWorkflow.Qwen2512 => "qwen2512",
                        TextGeneratorWorkflow.Klien => "f2k-txt2img",
                        TextGeneratorWorkflow.Anima => "anima",
                        TextGeneratorWorkflow.Krea2 => "krea2",
                        _ => "z-image"
                    };
                    var outputPath = Path.Combine(outputDir, $"{prefix}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    await LocalCopyService.CopyImageAsync(outputPath);
                    AddLog($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generation complete - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show("No output images were generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Image generation cancelled by user");
                ProcessingStatus = "Cancelled";
                ProcessingProgress = 0;
                StatusBarMessage = "Generation cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AddLog($"Inner Exception: {ex.InnerException.Message}");
                }
                AddLog($"Stack Trace: {ex.StackTrace}");

                _logger.LogError($"Error generating image: {ex}");

                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating image:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Image generation ended ===");
            }
        }

        private void RefreshLoras()
        {
            LoadAvailableLoras();
            AddLog("Refreshed LoRA list");
        }

        private void RefreshKreaLoras()
        {
            LoadKreaLoras();
            AddLog("Refreshed Krea2 LoRA list");
        }

        /// <summary>
        /// Resolves the folder that holds the Krea2 LoRAs. Prefers the explicit
        /// Settings → Krea2 LoRA Folder path (which points straight at the krea2 folder),
        /// then falls back to a "krea2"/"Krea2" subfolder of the general LoRA directory.
        /// </summary>
        private string? ResolveKreaLoraFolder()
        {
            var configured = _settingsService.Settings?.KreaLoraFolderPath;
            if (!string.IsNullOrEmpty(configured))
            {
                if (Directory.Exists(configured))
                {
                    AddLog($"Using configured Krea2 LoRA folder: {configured}");
                    return configured;
                }
                AddLog($"Configured Krea2 LoRA folder not accessible: {configured}");
            }

            var loraBasePath = GetLoraModelPath();
            if (!string.IsNullOrEmpty(loraBasePath))
            {
                foreach (var name in new[] { "krea2", "Krea2" })
                {
                    var candidate = Path.Combine(loraBasePath, name);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads the Krea2 LoRAs from the configured/derived krea2 folder.
        /// These feed the Power Lora Loader (node 17) in the Krea2 realism workflow.
        /// </summary>
        private void LoadKreaLoras()
        {
            try
            {
                var kreaPath = ResolveKreaLoraFolder();

                KreaLoras.Clear();

                if (kreaPath == null)
                {
                    AddLog("Krea2 LoRA folder not found (set it in Settings → Krea2 LoRA Folder, or place a krea2 subfolder in the LoRA directory)");
                    KreaLoras.Add("No LoRAs available");
                    return;
                }

                _kreaLoraSubfolder = new DirectoryInfo(kreaPath).Name;

                var loraFiles = Directory.GetFiles(kreaPath, "*.safetensors")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name)
                    .ToList();

                if (loraFiles.Any())
                {
                    foreach (var lora in loraFiles)
                        KreaLoras.Add(lora!);

                    if (string.IsNullOrEmpty(SelectedKreaLora) || !KreaLoras.Contains(SelectedKreaLora))
                        SelectedKreaLora = KreaLoras.First();

                    AddLog($"Loaded {KreaLoras.Count} Krea2 LoRAs from {kreaPath}");
                }
                else
                {
                    KreaLoras.Add("No LoRAs available");
                    AddLog($"No Krea2 LoRA files found in {kreaPath}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading Krea2 LoRAs: {ex.Message}");
                KreaLoras.Clear();
                KreaLoras.Add("Error loading LoRAs");
            }
        }

        private string? GetLoraModelPath()
        {
            try
            {
                // Check if we're connecting to a remote ComfyUI server
                var baseUrl = _settingsService.Settings?.BaseUrl ?? string.Empty;
                bool isRemoteServer = IsRemoteUrl(baseUrl);

                // Check explicit LoRA folder override first — works regardless of local/remote server.
                // Handles the case where ComfyUI is local but loras live on a mapped network drive.
                var overrideLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
                if (!string.IsNullOrEmpty(overrideLoraPath))
                {
                    if (Directory.Exists(overrideLoraPath))
                    {
                        AddLog($"Using configured LoRA folder: {overrideLoraPath}");
                        return overrideLoraPath;
                    }
                    else
                    {
                        AddLog($"Configured LoRA folder not accessible: {overrideLoraPath}");
                    }
                }

                string? loraBasePath;

                if (isRemoteServer)
                {
                    var explicitLoraPath = _settingsService.Settings?.RemoteLoraFolderPath;
                    if (!string.IsNullOrEmpty(explicitLoraPath))
                    {
                        if (Directory.Exists(explicitLoraPath))
                        {
                            AddLog($"Using explicitly configured remote LoRA path: {explicitLoraPath}");
                            return explicitLoraPath;
                        }
                        else
                        {
                            AddLog($"Configured remote LoRA path not accessible: {explicitLoraPath}");
                            // Fall through to try deriving from output path
                        }
                    }

                    // Priority 2: Derive from RemoteOutputFolderPath
                    var remoteOutputPath = _settingsService.Settings?.RemoteOutputFolderPath;
                    if (string.IsNullOrEmpty(remoteOutputPath))
                    {
                        AddLog("Remote output path not configured and no explicit LoRA path set - cannot load LoRAs");
                        return null;
                    }

                    var comfyUIRoot = Path.GetDirectoryName(remoteOutputPath);
                    if (string.IsNullOrEmpty(comfyUIRoot))
                    {
                        AddLog($"Could not derive ComfyUI root from output path: {remoteOutputPath}");
                        return null;
                    }

                    var loraBasePath2 = Path.Combine(comfyUIRoot, "models", "loras");
                    AddLog($"Derived remote LoRA path from output path: {loraBasePath2}");

                    if (Directory.Exists(loraBasePath2))
                    {
                        AddLog($"Remote LoRA directory exists: {loraBasePath2}");
                        return loraBasePath2;
                    }
                    else
                    {
                        AddLog($"Remote LoRA directory not found: {loraBasePath2}");
                        return null;
                    }
                }
                else
                {
                    // Use local ComfyUI path
                    loraBasePath = _settingsService.Settings?.ComfyUIFolderPath;
                    if (string.IsNullOrEmpty(loraBasePath))
                    {
                        AddLog("ComfyUI installation path not configured");
                        return null;
                    }
                }

                // First try to get path from extra_model_paths.yaml (local only)
                var extraModelPathsFile = Path.Combine(loraBasePath, "extra_model_paths.yaml");
                AddLog($"Looking for extra_model_paths.yaml at: {extraModelPathsFile}");

                if (File.Exists(extraModelPathsFile))
                {
                    try
                    {
                        AddLog("Found extra_model_paths.yaml, reading content...");
                        var yamlContent = File.ReadAllText(extraModelPathsFile);
                        var deserializer = new DeserializerBuilder().Build();
                        var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

                        AddLog($"YAML parsed successfully. Keys found: {string.Join(", ", yamlData.Keys)}");

                        if (yamlData != null)
                        {
                            string basePath = string.Empty;
                            string lorasRelativePath = string.Empty;

                            // Check for "comfyui" section (most common format)
                            if (yamlData.ContainsKey("comfyui"))
                            {
                                AddLog("Found 'comfyui' section in YAML");
                                var comfyuiSectionObject = yamlData["comfyui"];
                                var comfyuiSection = comfyuiSectionObject as Dictionary<object, object>;

                                if (comfyuiSection != null)
                                {
                                    // Convert to Dictionary<string, object> for easier use
                                    var comfyuiStringDict = new Dictionary<string, object>();
                                    foreach (var kvp in comfyuiSection)
                                    {
                                        if (kvp.Key != null)
                                        {
                                            comfyuiStringDict[kvp.Key.ToString() ?? string.Empty] = kvp.Value;
                                        }
                                    }

                                    AddLog($"ComfyUI section keys: {string.Join(", ", comfyuiStringDict.Keys)}");

                                    // Get base_path if it exists
                                    if (comfyuiStringDict.ContainsKey("base_path"))
                                    {
                                        basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                                        AddLog($"Found base_path: {basePath}");
                                    }

                                    // Get loras path if it exists
                                    if (comfyuiStringDict.ContainsKey("loras"))
                                    {
                                        lorasRelativePath = comfyuiStringDict["loras"]?.ToString() ?? string.Empty;
                                        AddLog($"Found loras path: {lorasRelativePath}");
                                    }
                                    else
                                    {
                                        AddLog("No 'loras' key found in comfyui section");
                                    }
                                }
                            }
                            else
                            {
                                AddLog("No 'comfyui' section found in YAML");

                                // Fallback to direct "loras" key
                                if (yamlData.ContainsKey("loras"))
                                {
                                    lorasRelativePath = yamlData["loras"]?.ToString() ?? string.Empty;
                                    AddLog($"Found direct loras path: {lorasRelativePath}");
                                }
                            }

                            // Construct full path
                            if (!string.IsNullOrEmpty(lorasRelativePath))
                            {
                                string fullLoraPath;
                                if (!string.IsNullOrEmpty(basePath))
                                {
                                    // Combine base_path with loras relative path
                                    fullLoraPath = Path.Combine(basePath, lorasRelativePath);
                                    AddLog($"Combined base_path and loras: {basePath} + {lorasRelativePath} = {fullLoraPath}");
                                }
                                else
                                {
                                    // Use just the loras path (might be absolute)
                                    fullLoraPath = lorasRelativePath;
                                    AddLog($"Using loras path directly: {fullLoraPath}");
                                }

                                // Normalize path separators
                                fullLoraPath = fullLoraPath.Replace('/', Path.DirectorySeparatorChar);

                                AddLog($"Final LoRA path: {fullLoraPath}");

                                if (Directory.Exists(fullLoraPath))
                                {
                                    AddLog($"SUCCESS: LoRA directory exists: {fullLoraPath}");
                                    return fullLoraPath;
                                }
                                else
                                {
                                    AddLog($"ERROR: LoRA path from extra_model_paths.yaml exists but directory not found: {fullLoraPath}");
                                }
                            }
                            else
                            {
                                AddLog("ERROR: No loras path found in YAML configuration");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"ERROR reading extra_model_paths.yaml: {ex.Message}");
                        AddLog($"Stack trace: {ex.StackTrace}");
                    }
                }
                else
                {
                    AddLog($"ERROR: extra_model_paths.yaml not found in ComfyUI directory: {extraModelPathsFile}");
                }

                // Fallback to default ComfyUI models directory
                var defaultLoraPath = Path.Combine(loraBasePath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    AddLog($"Using default ComfyUI LoRA path: {defaultLoraPath}");
                    return defaultLoraPath;
                }

                AddLog($"No LoRA directory found in: {loraBasePath}");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"Error getting LoRA model path: {ex.Message}");
                return null;
            }
        }

        private void LoadAvailableLoras()
        {
            try
            {
                // Priority 1: Get LoRA path from ComfyUI extra_model_paths.yaml or default location
                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    // Look for zimage subfolder
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    if (Directory.Exists(zimageLoraPath))
                    {
                        LoadLorasFromDirectory(zimageLoraPath, "ComfyUI LoRA directory");
                        return;
                    }
                    else
                    {
                        // If zimage subfolder doesn't exist, use the base LoRA directory
                        LoadLorasFromDirectory(loraBasePath, "ComfyUI LoRA directory");
                        return;
                    }
                }

                // Priority 2: Fallback to local directory
                var localLoraPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "loras", "zimage");
                LoadLorasFromDirectory(localLoraPath, "local directory");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading LoRAs: {ex.Message}");
                AvailableLoras.Clear();
                AvailableLoras.Add("Error loading LoRAs");
            }
        }

        private void LoadLorasFromDirectory(string loraPath, string pathDescription)
        {
            AddLog($"Looking for LoRAs in {pathDescription}: {loraPath}");

            if (!Directory.Exists(loraPath))
            {
                AddLog($"LoRA directory not found: {loraPath}");
                AvailableLoras.Clear();
                AvailableLoras.Add("No LoRAs available");
                return;
            }

            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name)
                .ToList();

            AvailableLoras.Clear();

            if (loraFiles.Any())
            {
                foreach (var lora in loraFiles)
                {
                    if (!string.IsNullOrEmpty(lora))
                        AvailableLoras.Add(lora);
                }

                if (string.IsNullOrEmpty(SelectedLora) && AvailableLoras.Any())
                {
                    SelectedLora = AvailableLoras.First();
                }

                AddLog($"Loaded {AvailableLoras.Count} LoRAs from {loraPath}");
            }
            else
            {
                AvailableLoras.Add("No LoRAs available");
                AddLog($"No LoRA files found in {pathDescription}");
            }
        }

        private void LoadWorkflowStyles()
        {
            try
            {
                _allStyles.Clear();
                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "zimage");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"ZStyles workflow directory not found at {workflowDir}");
                    return;
                }

                // Recurse so styles organized into subfolders (4k-upscale/, simple/, base/) are found.
                var workflowFiles = Directory.GetFiles(workflowDir, "*.json", SearchOption.AllDirectories);

                foreach (var workflowFile in workflowFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(workflowFile);

                    // Skip full workflows that aren't selectable style presets.
                    if (StyleInfo.IsNonStyleWorkflow(fileName))
                        continue;

                    var styleName = fileName.StartsWith("Z") ? fileName.Substring(1) : fileName;

                    _allStyles.Add(new StyleInfo
                    {
                        Name = styleName,
                        PromptTemplate = "",
                        WorkflowFile = workflowFile,
                        NodeId = ""
                    });
                }

                _allStyles = _allStyles.OrderBy(s => s.Name).ToList();
                OnPropertyChanged(nameof(StyleNames));
                AddLog($"Loaded {_allStyles.Count} styles from ZStyles folder");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading workflow styles: {ex.Message}");
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            switch (SelectedWorkflow)
            {
                case TextGeneratorWorkflow.Zimage:
                    return UpdateZimageWorkflow(workflowDict);
                case TextGeneratorWorkflow.Qwen2512:
                    return UpdateQwen2512Workflow(workflowDict);
                case TextGeneratorWorkflow.Klien:
                    return UpdateKlienWorkflow(workflowDict);
                case TextGeneratorWorkflow.Anima:
                    return UpdateAnimaWorkflow(workflowDict);
                case TextGeneratorWorkflow.Krea2:
                    return UpdateKrea2Workflow(workflowDict);
                default:
                    return workflow;
            }
        }

        /// <summary>
        /// Applies the user's LoRA selection to a ZStyle workflow's own Power Lora Loader
        /// (rgthree) node. The node id varies per workflow (Lo-Fi-Mobile=128, EpicGreg=392,
        /// …), so it's located by class_type rather than a fixed id. Returns true if such a
        /// node was found and updated. Node 583 (Zib-Zit) is handled by its own block before
        /// this is reached, so it is not double-processed.
        /// </summary>
        private bool TryApplyPowerLoraLoader(Dictionary<string, JsonElement> workflowDict)
        {
            // Locate the Power Lora Loader (rgthree) node by class_type.
            string? nodeId = null;
            foreach (var kvp in workflowDict)
            {
                var probe = JsonSerializer.Deserialize<Dictionary<string, object>>(kvp.Value.GetRawText());
                var ct = probe != null && probe.ContainsKey("class_type") ? probe["class_type"]?.ToString() ?? "" : "";
                if (ct.Contains("Power Lora Loader", StringComparison.OrdinalIgnoreCase))
                {
                    nodeId = kvp.Key;
                    break;
                }
            }

            if (nodeId == null) return false;

            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return false;

            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return false;

            // Preserve the slot's current lora name so the disabled state keeps a valid
            // reference (rgthree dislikes an empty/missing lora name).
            var existingLoraName = "";
            if (inputs.ContainsKey("lora_1") && inputs["lora_1"] is JsonElement le && le.ValueKind == JsonValueKind.Object)
            {
                var slot = JsonSerializer.Deserialize<Dictionary<string, object>>(le.GetRawText());
                if (slot != null && slot.ContainsKey("lora"))
                    existingLoraName = slot["lora"]?.ToString() ?? "";
            }

            bool hasSelection = !string.IsNullOrEmpty(SelectedLora)
                && SelectedLora != "No Loras available"
                && SelectedLora != "No LoRAs available";

            object lora1Config;
            if (LoraEnabled && hasSelection)
            {
                // LoRAs live in the ComfyUI "zimage" subfolder; SelectedLora is the bare name.
                lora1Config = new { on = true, lora = $"zimage/{SelectedLora}.safetensors", strength = 1.0 };
                AddLog($"ZStyle LoRA enabled on Power Lora Loader node {nodeId}: zimage/{SelectedLora}.safetensors");
            }
            else
            {
                lora1Config = new { on = false, lora = !string.IsNullOrEmpty(existingLoraName) ? existingLoraName : "None", strength = 0.0 };
                AddLog($"ZStyle LoRA disabled on Power Lora Loader node {nodeId}");
            }

            inputs["lora_1"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(lora1Config))!;
            node["inputs"] = inputs;
            workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
            return true;
        }

        private JsonElement UpdateZimageWorkflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Zib-Zit workflow uses Power Lora Loader (node 583)
            // Handle LoRA: enable/disable lora_1 slot in the Power Lora Loader
            if (workflowDict.ContainsKey("583"))
            {
                // Verify this is actually a Power Lora Loader, not a repurposed node
                var node583 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["583"].GetRawText());
                if (node583 != null)
                {
                    var classType = node583.ContainsKey("class_type") ? node583["class_type"]?.ToString() ?? "" : "";

                    if (classType.Contains("Lora", StringComparison.OrdinalIgnoreCase) && node583.ContainsKey("inputs"))
                    {
                        // Power Lora Loader exists - Zib-Zit workflow
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node583["inputs"]));
                        if (inputs != null)
                        {
                            if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No Loras available")
                            {
                                // Enable lora_1 with the selected lora
                                var lora1Config = new
                                {
                                    on = true,
                                    lora = $"{SelectedLora}.safetensors",
                                    strength = 1.0
                                };
                                inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                    JsonSerializer.Serialize(lora1Config))!;
                                AddLog($"LoRA enabled: {SelectedLora}.safetensors");
                            }
                            else
                            {
                                // Disable lora_1 — keep a valid lora name to avoid rgthree "None" warning
                                var existingLoraName = "";
                                if (inputs.ContainsKey("lora_1"))
                                {
                                    try
                                    {
                                        var lora1Obj = inputs["lora_1"];
                                        if (lora1Obj is JsonElement lora1Elem && lora1Elem.ValueKind == JsonValueKind.Object)
                                        {
                                            var lora1Dict = JsonSerializer.Deserialize<Dictionary<string, object>>(lora1Elem.GetRawText());
                                            if (lora1Dict != null && lora1Dict.ContainsKey("lora"))
                                                existingLoraName = lora1Dict["lora"]?.ToString() ?? "";
                                        }
                                    }
                                    catch { }
                                }

                                var lora1Config = new
                                {
                                    on = false,
                                    lora = !string.IsNullOrEmpty(existingLoraName) ? existingLoraName : "zimage/zimage_anushkasharma_v2_onetrainer.safetensors",
                                    strength = 0.0
                                };
                                inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                    JsonSerializer.Serialize(lora1Config))!;
                                AddLog("LoRA disabled (Power Lora Loader)");
                            }

                            node583["inputs"] = inputs;
                            workflowDict["583"] = JsonSerializer.SerializeToElement(node583);
                        }
                    }
                    else
                    {
                        AddLog($"Node 583 is {classType}, not a LoRA loader — skipping LoRA configuration");
                    }
                }
            }
            else if (TryApplyPowerLoraLoader(workflowDict))
            {
                // The workflow has its own Power Lora Loader (rgthree) at a non-583 node id
                // (ZStyle presets: Lo-Fi-Mobile=128, EpicGreg=392, …). The selected LoRA was
                // applied directly to that node, so skip the legacy LoRA machinery.
            }
            else
            {
                // Check for LoraLoaderModelOnly nodes (Z4k and similar workflows)
                // These workflows have a built-in LoRA node that we update directly
                bool handledLoraLoaderModelOnly = false;
                var loraModifications = new List<KeyValuePair<string, Dictionary<string, object>>>();

                // First pass: identify LoraLoaderModelOnly nodes (don't modify dict during enumeration)
                foreach (var kvp in workflowDict)
                {
                    var nodeObj = JsonSerializer.Deserialize<Dictionary<string, object>>(kvp.Value.GetRawText());
                    if (nodeObj == null) continue;
                    var ct = nodeObj.ContainsKey("class_type") ? nodeObj["class_type"]?.ToString() ?? "" : "";
                    if (ct != "LoraLoaderModelOnly") continue;

                    handledLoraLoaderModelOnly = true;

                    if (!nodeObj.ContainsKey("inputs")) continue;
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(nodeObj["inputs"]));
                    if (inputs == null) continue;

                    if (LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No Loras available")
                    {
                        inputs["lora_name"] = $"zimage/{SelectedLora}.safetensors";
                        inputs["strength_model"] = 1.0;
                        nodeObj["inputs"] = inputs;
                        loraModifications.Add(new KeyValuePair<string, Dictionary<string, object>>(kvp.Key, nodeObj));
                        AddLog($"Updated LoraLoaderModelOnly node {kvp.Key}: zimage/{SelectedLora}.safetensors");
                    }
                    else
                    {
                        // Disable LoRA by setting strength to 0 (preserves node connections)
                        inputs["strength_model"] = 0.0;
                        nodeObj["inputs"] = inputs;
                        loraModifications.Add(new KeyValuePair<string, Dictionary<string, object>>(kvp.Key, nodeObj));
                        AddLog($"Disabled LoraLoaderModelOnly node {kvp.Key} (strength=0)");
                    }
                }

                // Second pass: apply modifications outside the enumeration loop
                foreach (var mod in loraModifications)
                {
                    workflowDict[mod.Key] = JsonSerializer.SerializeToElement(mod.Value);
                }

                if (!handledLoraLoaderModelOnly)
                {
                    // The legacy LoRA machinery (AddLoraToWorkflow on enable, node-58 bypass
                    // on disable) only applies to the old architecture that actually has
                    // node 58. ZStyle preset workflows (Lo-Fi-Mobile, EpicGreg, ...) use a
                    // self-contained Power Lora Loader and have NO node 58 — running either
                    // path against them injects references to nodes that don't exist (e.g.
                    // node 39 / node 46) and breaks the graph. So both halves are gated on
                    // node 58; anything else is left untouched to run exactly like manual.
                    if (workflowDict.ContainsKey("58") && LoraEnabled && !string.IsNullOrEmpty(SelectedLora) && SelectedLora != "No Loras available")
                    {
                        workflowDict = AddLoraToWorkflow(workflowDict, SelectedLora);
                    }
                    else if (workflowDict.ContainsKey("58"))
                    {
                        // LoRA disabled: bypass the built-in LoRA node (58) by wiring the
                        // model/clip consumers straight to the loaders. This rewrite is only
                        // valid for the legacy architecture that actually HAS node 58
                        // (UNETLoader 46, ModelSamplingAuraFlow 47, CLIPTextEncode 45,
                        // CLIPLoader 39). The ZStyle preset workflows (e.g. Lo-Fi-Mobile,
                        // EpicGreg) have NO node 58 and reuse ids 47/45 for unrelated nodes
                        // (47 is a style-prompt string), so they must be left untouched —
                        // otherwise this would point node 47 at a non-existent node 46 and
                        // break the graph with "Node 46 not found".

                        // Update ModelSamplingAuraFlow (node 47) to connect directly to UNETLoader (node 46)
                        if (workflowDict.ContainsKey("47"))
                        {
                            var node47 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["47"].GetRawText());
                            if (node47 != null && node47.ContainsKey("inputs"))
                            {
                                var inputs47 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                    JsonSerializer.Serialize(node47["inputs"]));
                                if (inputs47 != null)
                                {
                                    inputs47["model"] = new object[] { "46", 0 }; // Connect directly to UNETLoader
                                    node47["inputs"] = inputs47;
                                    workflowDict["47"] = JsonSerializer.SerializeToElement(node47);
                                }
                            }
                        }

                        // Update CLIPTextEncode (node 45) to connect directly to CLIPLoader (node 39)
                        if (workflowDict.ContainsKey("45"))
                        {
                            var node45 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["45"].GetRawText());
                            if (node45 != null && node45.ContainsKey("inputs"))
                            {
                                var inputs45 = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                    JsonSerializer.Serialize(node45["inputs"]));
                                if (inputs45 != null)
                                {
                                    inputs45["clip"] = new object[] { "39", 0 }; // Connect directly to CLIPLoader
                                    node45["inputs"] = inputs45;
                                    workflowDict["45"] = JsonSerializer.SerializeToElement(node45);
                                }
                            }
                        }

                        // Remove the orphaned LoRA node (58) from the workflow
                        workflowDict.Remove("58");
                        AddLog("LoRA disabled: bypassing built-in LoRA node");
                    }
                    else
                    {
                        // No built-in LoRA node to bypass (e.g. ZStyle preset workflows).
                        // Leave the workflow's own LoRA configuration untouched so it runs
                        // exactly as it does when loaded manually.
                        AddLog("No built-in LoRA node (58) to bypass — leaving workflow LoRA settings untouched");
                    }
                }
            }

            // Detect workflow architecture and inject user prompt accordingly:
            // 1. Zib-Zit: Node 443 is a "Textbox" → set inputs.text
            // 2. ZStyle workflows: Node 385 is a "StringTrim" → set inputs.string
            //    (Node 443 exists but is PrimitiveInt, NOT a prompt node)
            // 3. amateurZimageAPI: Node 6 is "CLIPTextEncode" → set inputs.text
            //    (Node 443 doesn't exist)

            bool promptUpdated = false;

            // Strategy 1: Check for node 385 (StringTrim) — ZStyle workflows
            // Must check this BEFORE node 443, because ZStyle files also have node 443 as PrimitiveInt
            if (workflowDict.ContainsKey("385"))
            {
                var node385 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["385"].GetRawText());
                if (node385 != null && node385.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node385["inputs"]));
                    if (inputs != null)
                    {
                        inputs["string"] = ImagePrompt;
                        node385["inputs"] = inputs;
                        workflowDict["385"] = JsonSerializer.SerializeToElement(node385);
                        AddLog($"Updated positive prompt via StringTrim (node 385) for ZStyle workflow");
                        promptUpdated = true;
                    }
                }
            }

            // Strategy 2: Check for node 443 (Textbox) — Zib-Zit workflow
            // Only use this if node 385 wasn't found (to avoid overwriting PrimitiveInt in ZStyle files)
            if (!promptUpdated && workflowDict.ContainsKey("443"))
            {
                // Verify node 443 is actually a Textbox (not PrimitiveInt)
                var node443Raw = workflowDict["443"];
                var node443 = JsonSerializer.Deserialize<Dictionary<string, object>>(node443Raw.GetRawText());
                if (node443 != null)
                {
                    var classType = "";
                    if (node443.ContainsKey("class_type"))
                    {
                        classType = node443["class_type"]?.ToString() ?? "";
                    }

                    if (classType == "Textbox" && node443.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node443["inputs"]));
                        if (inputs != null)
                        {
                            inputs["text"] = ImagePrompt;
                            node443["inputs"] = inputs;
                            workflowDict["443"] = JsonSerializer.SerializeToElement(node443);
                            AddLog($"Updated positive prompt via Textbox (node 443) for Zib-Zit workflow");
                            promptUpdated = true;
                        }
                    }
                }
            }

            // Strategy 3: Fallback to node 6 (CLIPTextEncode) — amateurZimageAPI
            if (!promptUpdated && workflowDict.ContainsKey("6"))
            {
                var node6 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["6"].GetRawText());
                if (node6 != null && node6.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node6["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node6["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.SerializeToElement(node6);
                        AddLog($"Updated positive prompt via CLIPTextEncode (node 6) for amateur workflow");
                        promptUpdated = true;
                    }
                }
            }

            // Strategy 4: Scan for PrimitiveStringMultiline nodes — Z Turbo PiT Nvidia 4k etc.
            // These workflows use PrimitiveStringMultiline for the positive prompt (e.g. node 92)
            if (!promptUpdated)
            {
                foreach (var kvp in workflowDict)
                {
                    var nodeObj = JsonSerializer.Deserialize<Dictionary<string, object>>(kvp.Value.GetRawText());
                    if (nodeObj == null) continue;
                    var ct = nodeObj.ContainsKey("class_type") ? nodeObj["class_type"]?.ToString() ?? "" : "";
                    if (ct != "PrimitiveStringMultiline") continue;

                    if (!nodeObj.ContainsKey("inputs")) continue;
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(nodeObj["inputs"]));
                    if (inputs == null || !inputs.ContainsKey("value")) continue;

                    inputs["value"] = ImagePrompt;
                    nodeObj["inputs"] = inputs;
                    workflowDict[kvp.Key] = JsonSerializer.SerializeToElement(nodeObj);
                    AddLog($"Updated positive prompt via PrimitiveStringMultiline (node {kvp.Key})");
                    promptUpdated = true;
                    break;
                }
            }

            if (!promptUpdated)
            {
                AddLog("WARNING: Could not find prompt node (checked nodes 385, 443, 6, PrimitiveStringMultiline)");
            }

            // Zib-Zit workflow uses different node IDs:
            // Node 445: Textbox - Negative Prompt
            // Node 569: Seed String
            // Node 639: KSamplerAdvanced - Z-image (steps, cfg, denoise)
            // Node 176: CR Aspect Ratio

            // Update seed (node 569 - Seed String)
            if (workflowDict.ContainsKey("569"))
            {
                var node569 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["569"].GetRawText());
                if (node569 != null && node569.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node569["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["seed"] = (long)actualSeed;
                        node569["inputs"] = inputs;
                        workflowDict["569"] = JsonSerializer.SerializeToElement(node569);
                        AddLog($"Updated seed: {actualSeed}");
                    }
                }
            }

            // Z4k workflow seeds: node 70 (KSampler) and node 75 (SamplerCustom)
            var actualSeed70 = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
            if (workflowDict.ContainsKey("70") &&
                workflowDict["70"].TryGetProperty("class_type", out var ct70) &&
                ct70.GetString() == "KSampler")
            {
                var node70 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["70"].GetRawText());
                if (node70 != null && node70.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node70["inputs"]));
                    if (inputs != null)
                    {
                        inputs["seed"] = (long)actualSeed70;
                        node70["inputs"] = inputs;
                        workflowDict["70"] = JsonSerializer.SerializeToElement(node70);
                        AddLog($"Updated KSampler (node 70) seed: {actualSeed70}");
                    }
                }
            }
            if (workflowDict.ContainsKey("75") &&
                workflowDict["75"].TryGetProperty("class_type", out var ct75) &&
                ct75.GetString() == "SamplerCustom")
            {
                var node75 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["75"].GetRawText());
                if (node75 != null && node75.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node75["inputs"]));
                    if (inputs != null)
                    {
                        inputs["noise_seed"] = (long)actualSeed70;
                        node75["inputs"] = inputs;
                        workflowDict["75"] = JsonSerializer.SerializeToElement(node75);
                        AddLog($"Updated SamplerCustom (node 75) noise_seed: {actualSeed70}");
                    }
                }
            }

            // Update Z-image sampler settings (node 639 - KSamplerAdvanced)
            if (workflowDict.ContainsKey("639"))
            {
                var node639 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["639"].GetRawText());
                if (node639 != null && node639.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node639["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["denoise"] = Denoise;
                        node639["inputs"] = inputs;
                        workflowDict["639"] = JsonSerializer.SerializeToElement(node639);
                        AddLog($"Updated Z-image sampler: steps={Steps}, cfg={Cfg}, denoise={Denoise}");
                    }
                }
            }

            // Update aspect ratio (node 176 - CR Aspect Ratio)
            if (workflowDict.ContainsKey("176"))
            {
                var aspectRatios = new[]
                {
                    "SDXL - 16:9 landscape 1600x1088",
                    "SDXL - 9:16 portrait 1088x1600",
                    "SDXL - 1:1 square 1600x1600"
                };

                var selectedRatio = aspectRatios[Math.Min(AspectRatioIndex, aspectRatios.Length - 1)];

                var node176 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["176"].GetRawText());
                if (node176 != null && node176.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node176["inputs"]));
                    if (inputs != null)
                    {
                        inputs["aspect_ratio"] = selectedRatio;
                        // Turn off swap_dimensions to prevent width/height inversion
                        inputs["swap_dimensions"] = "Off";
                        node176["inputs"] = inputs;
                        workflowDict["176"] = JsonSerializer.SerializeToElement(node176);
                        AddLog($"Updated aspect ratio: {selectedRatio}");
                    }
                }
            }

            // Node 57: CR Aspect Ratio (main Zimage workflow / image_z_image-TEXTAPI)
            if (workflowDict.ContainsKey("57"))
            {
                var aspectRatios = new[]
                {
                    "SDXL - 16:9 landscape 1344x768",
                    "SDXL - 9:16 portrait 768x1344",
                    "SDXL - 1:1 square 1024x1024"
                };
                var selectedRatio = aspectRatios[Math.Min(AspectRatioIndex, aspectRatios.Length - 1)];
                var node57 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["57"].GetRawText());
                if (node57 != null && node57.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node57["inputs"]));
                    if (inputs != null)
                    {
                        inputs["aspect_ratio"] = selectedRatio;
                        inputs["swap_dimensions"] = "Off";
                        node57["inputs"] = inputs;
                        workflowDict["57"] = JsonSerializer.SerializeToElement(node57);
                        AddLog($"Updated node 57 aspect ratio: {selectedRatio}");
                    }
                }
            }

            // Node 56: EmptyLatentImage (Zstyle workflows)
            if (workflowDict.ContainsKey("56") &&
                workflowDict["56"].TryGetProperty("class_type", out var node56Class) &&
                node56Class.GetString() == "EmptyLatentImage")
            {
                var resolutions = new[]
                {
                    (1408, 944),   // Landscape
                    (944, 1408),   // Portrait
                    (1120, 1120)   // Square
                };
                var (w56, h56) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];
                var node56 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["56"].GetRawText());
                if (node56 != null && node56.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node56["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = w56;
                        inputs["height"] = h56;
                        node56["inputs"] = inputs;
                        workflowDict["56"] = JsonSerializer.SerializeToElement(node56);
                        AddLog($"Updated node 56 (EmptyLatentImage) resolution: {w56}x{h56}");
                    }
                }
            }

            // Node 244: EmptySD3LatentImage (Zstyle workflows — active sampling path)
            // Node 56 above is a dead-end path; node 244 drives the actual SD3 sampler chain.
            // Direct override bypasses the Any Switch routing (nodes 536/537) which has no sel
            // input connected and always defaults to landscape regardless of orientation.
            if (workflowDict.ContainsKey("244") &&
                workflowDict["244"].TryGetProperty("class_type", out var node244Class) &&
                node244Class.GetString() == "EmptySD3LatentImage")
            {
                var sd3Resolutions = new[]
                {
                    (1600, 1088), // Landscape
                    (1088, 1600), // Portrait
                    (1088, 1088), // Square
                };
                var (w244, h244) = sd3Resolutions[Math.Min(AspectRatioIndex, sd3Resolutions.Length - 1)];
                var node244 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["244"].GetRawText());
                if (node244 != null && node244.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node244["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = w244;
                        inputs["height"] = h244;
                        node244["inputs"] = inputs;
                        workflowDict["244"] = JsonSerializer.SerializeToElement(node244);
                        AddLog($"Updated node 244 (EmptySD3LatentImage) resolution: {w244}x{h244}");
                    }
                }
            }

            // Z Turbo PiT Nvidia 4k workflow latents:
            //   Node 68 (EmptySD3LatentImage)        — base/guidance latent feeding KSampler 70
            //   Node 84 (EmptyChromaRadianceLatentImage) — 4K canvas feeding SamplerCustom 75
            // Both are hardcoded square in the source workflow, so aspect ratio was never applied.
            // Node 84 must stay exactly 4× node 68 to preserve the PiD guidance scale relationship.
            {
                var pitBaseResolutions = new[]
                {
                    (1280, 720),   // Landscape (16:9)
                    (720, 1280),   // Portrait
                    (1024, 1024),  // Square
                };
                var (wBase, hBase) = pitBaseResolutions[Math.Min(AspectRatioIndex, pitBaseResolutions.Length - 1)];

                // Node 68: base latent
                if (workflowDict.ContainsKey("68") &&
                    workflowDict["68"].TryGetProperty("class_type", out var node68Class) &&
                    node68Class.GetString() == "EmptySD3LatentImage")
                {
                    var node68 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["68"].GetRawText());
                    if (node68 != null && node68.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node68["inputs"]));
                        if (inputs != null)
                        {
                            inputs["width"] = wBase;
                            inputs["height"] = hBase;
                            node68["inputs"] = inputs;
                            workflowDict["68"] = JsonSerializer.SerializeToElement(node68);
                            AddLog($"Updated node 68 (EmptySD3LatentImage) resolution: {wBase}x{hBase}");
                        }
                    }
                }

                // Node 84: 4K canvas (4× base)
                if (workflowDict.ContainsKey("84") &&
                    workflowDict["84"].TryGetProperty("class_type", out var node84Class) &&
                    node84Class.GetString() == "EmptyChromaRadianceLatentImage")
                {
                    var node84 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["84"].GetRawText());
                    if (node84 != null && node84.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node84["inputs"]));
                        if (inputs != null)
                        {
                            inputs["width"] = wBase * 4;
                            inputs["height"] = hBase * 4;
                            node84["inputs"] = inputs;
                            workflowDict["84"] = JsonSerializer.SerializeToElement(node84);
                            AddLog($"Updated node 84 (EmptyChromaRadianceLatentImage) resolution: {wBase * 4}x{hBase * 4}");
                        }
                    }
                }
            }

            // Node 9: SaveImage — update filename_prefix date so output lands in today's folder
            if (workflowDict.ContainsKey("9"))
            {
                var node9 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["9"].GetRawText());
                if (node9 != null && node9.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node9["inputs"]));
                    if (inputs != null && inputs.TryGetValue("filename_prefix", out var prefixVal))
                    {
                        var prefix = prefixVal?.ToString() ?? string.Empty;
                        if (prefix.StartsWith("ZImage/", StringComparison.OrdinalIgnoreCase))
                        {
                            var dateStr = DateTime.Now.ToString("yyyy_MM_dd");
                            var parts = prefix.Split('/');
                            // Replace the date segment (index 1) with today's date
                            if (parts.Length >= 2)
                            {
                                parts[1] = dateStr;
                                inputs["filename_prefix"] = string.Join("/", parts);
                                node9["inputs"] = inputs;
                                workflowDict["9"] = JsonSerializer.SerializeToElement(node9);
                                AddLog($"Updated filename_prefix to {inputs["filename_prefix"]}");
                            }
                        }
                    }
                }
            }

            // amateurZimageAPI-specific fixes: Handle problematic nodes
            // Check if this is amateurZimageAPI by looking for node 760 (character LoRA) or node 107 (metadata)
            if (workflowDict.ContainsKey("760"))
            {
                // Node 760: LoraLoaderModelOnly - has hardcoded invalid LoRA
                var node760 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["760"].GetRawText());
                if (node760 != null && node760.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node760["inputs"]));
                    if (inputs != null)
                    {
                        // Use amateur photography LoRA with minimal strength as fallback
                        inputs["lora_name"] = "zimage/amateur_photography_zimage_v1.safetensors";
                        inputs["strength_model"] = 0.0;
                        node760["inputs"] = inputs;
                        workflowDict["760"] = JsonSerializer.SerializeToElement(node760);
                        AddLog("Fixed node 760: using amateur LoRA with 0.0 strength");
                    }
                }
            }

            // Remove problematic metadata nodes entirely (107, 109) to prevent file loading errors
            // These nodes are for watermarking/metadata display and aren't essential for generation
            if (workflowDict.ContainsKey("107"))
            {
                workflowDict.Remove("107");
                AddLog("Removed node 107 (metadata) to prevent file loading errors");
            }
            if (workflowDict.ContainsKey("109"))
            {
                workflowDict.Remove("109");
                AddLog("Removed node 109 (metadata viewer) to prevent errors");
            }
            // Also remove watermark nodes that depend on node 107 (747, 748, 749, 751)
            if (workflowDict.ContainsKey("747"))
            {
                workflowDict.Remove("747");
                AddLog("Removed node 747 (watermark label)");
            }
            if (workflowDict.ContainsKey("748"))
            {
                workflowDict.Remove("748");
                AddLog("Removed node 748 (watermark label)");
            }
            if (workflowDict.ContainsKey("749"))
            {
                workflowDict.Remove("749");
                AddLog("Removed node 749 (image concatenation)");
            }
            if (workflowDict.ContainsKey("751"))
            {
                workflowDict.Remove("751");
                AddLog("Removed node 751 (watermark save)");
            }

            // Fix node 651 (main SaveImage for amateurZimageAPI): redirect output to ZImage folder
            // so the image retrieval search finds it in the expected location
            if (workflowDict.ContainsKey("651"))
            {
                var node651 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["651"].GetRawText());
                if (node651 != null && node651.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node651["inputs"]));
                    if (inputs != null)
                    {
                        inputs["filename_prefix"] = "ZImage/AmateurImage";
                        node651["inputs"] = inputs;
                        workflowDict["651"] = JsonSerializer.SerializeToElement(node651);
                        AddLog("Fixed node 651: output redirected to ZImage/AmateurImage");
                    }
                }
            }

            // amateurZimageAPI uses node 28 for seed (not node 569)
            if (workflowDict.ContainsKey("28"))
            {
                var node28 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["28"].GetRawText());
                if (node28 != null && node28.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node28["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["seed"] = (long)actualSeed;
                        node28["inputs"] = inputs;
                        workflowDict["28"] = JsonSerializer.SerializeToElement(node28);
                        AddLog($"Updated seed: {actualSeed}");
                    }
                }
            }

            // amateurZimageAPI: Update aspect ratio
            // Latent sizes (×3 = final output): landscape 576×416→1728×1248, portrait 416×576→1248×1728, square 416×416→1248×1248
            var amateurLatentResolutions = new[] { (576, 416), (416, 576), (416, 416) };
            var amateurFinalResolutions  = new[] { (1728, 1248), (1248, 1728), (1248, 1248) };
            var (latW, latH) = amateurLatentResolutions[Math.Min(AspectRatioIndex, amateurLatentResolutions.Length - 1)];
            var (finW, finH) = amateurFinalResolutions[Math.Min(AspectRatioIndex, amateurFinalResolutions.Length - 1)];

            // Node 46: initial latent (EmptySD3LatentImage)
            if (workflowDict.ContainsKey("46") &&
                workflowDict["46"].TryGetProperty("class_type", out var node46Class) &&
                node46Class.GetString() == "EmptySD3LatentImage")
            {
                var node46 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["46"].GetRawText());
                if (node46 != null && node46.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node46["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = latW;
                        inputs["height"] = latH;
                        node46["inputs"] = inputs;
                        workflowDict["46"] = JsonSerializer.SerializeToElement(node46);
                        AddLog($"Updated node 46 latent: {latW}x{latH}");
                    }
                }
            }

            // Node 618: ImageScale — final upscale to output dimensions
            if (workflowDict.ContainsKey("618"))
            {
                var node618 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["618"].GetRawText());
                if (node618 != null && node618.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node618["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = finW;
                        inputs["height"] = finH;
                        node618["inputs"] = inputs;
                        workflowDict["618"] = JsonSerializer.SerializeToElement(node618);
                        AddLog($"Updated node 618 (ImageScale) output: {finW}x{finH}");
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateQwen2512Workflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Get resolution from aspect ratio index
            var resolutions = new[]
            {
                (1600, 1088), // Landscape
                (1088, 1600), // Portrait
                (1600, 1600), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            // Update prompt (node 71 - CLIPTextEncode)
            if (workflowDict.ContainsKey("71"))
            {
                var node71 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["71"].GetRawText());
                if (node71 != null && node71.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node71["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node71["inputs"] = inputs;
                        workflowDict["71"] = JsonSerializer.SerializeToElement(node71);
                    }
                }
            }

            // Update seed (node 120 - Seed)
            if (workflowDict.ContainsKey("120"))
            {
                var node120 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["120"].GetRawText());
                if (node120 != null && node120.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node120["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? -1 : Seed;
                        inputs["seed"] = actualSeed;
                        node120["inputs"] = inputs;
                        workflowDict["120"] = JsonSerializer.SerializeToElement(node120);
                    }
                }
            }

            // Update sampler settings (node 74 - KSampler)
            if (workflowDict.ContainsKey("74"))
            {
                var node74 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["74"].GetRawText());
                if (node74 != null && node74.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node74["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["denoise"] = Denoise;
                        node74["inputs"] = inputs;
                        workflowDict["74"] = JsonSerializer.SerializeToElement(node74);
                    }
                }
            }

            // Update resolution (node 51 - EmptyLatentImage)
            if (workflowDict.ContainsKey("51"))
            {
                var node51 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["51"].GetRawText());
                if (node51 != null && node51.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node51["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node51["inputs"] = inputs;
                        workflowDict["51"] = JsonSerializer.SerializeToElement(node51);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateKlienWorkflow(Dictionary<string, JsonElement> workflowDict)
        {
            // KlienX3n-Text-Ultimate-API.json node map:
            //   10 = CLIPTextEncode (positive prompt) → inputs.text
            //   12 = KSampler → inputs.seed / steps / cfg
            //   11 = EmptyLatentImage → inputs.width / height

            var resolutions = new[]
            {
                (1600, 1088), // Landscape
                (1088, 1600), // Portrait
                (1600, 1600), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            // Update prompt (node 10 - CLIPTextEncode)
            if (workflowDict.ContainsKey("10"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["10"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node["inputs"] = inputs;
                        workflowDict["10"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Update seed, steps, cfg (node 12 - KSampler)
            if (workflowDict.ContainsKey("12"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["12"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["seed"] = actualSeed;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node["inputs"] = inputs;
                        workflowDict["12"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Update resolution (node 11 - EmptyLatentImage)
            if (workflowDict.ContainsKey("11"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["11"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node["inputs"] = inputs;
                        workflowDict["11"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Strip the image-refinement subgraph — it requires a pre-existing input image
            // and is not relevant for text-to-image generation.
            foreach (var id in new[] { "222", "223", "224", "225", "226", "228", "238", "239", "261", "319", "323" })
                workflowDict.Remove(id);

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateAnimaWorkflow(Dictionary<string, JsonElement> workflowDict)
        {
            // Anima.json node map:
            //   60:11 = CLIPTextEncode (positive prompt) → inputs.text
            //   60:19 = KSampler → inputs.seed / steps / cfg
            //   60:28 = EmptyLatentImage → inputs.width / height

            var resolutions = new[]
            {
                (1024, 768),  // Landscape
                (768, 1024),  // Portrait
                (1024, 1024), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            if (workflowDict.ContainsKey("60:11"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["60:11"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node["inputs"] = inputs;
                        workflowDict["60:11"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            if (workflowDict.ContainsKey("60:19"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["60:19"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        var actualSeed = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        inputs["seed"] = actualSeed;
                        // steps, cfg, sampler_name, scheduler, denoise kept exactly as workflow specifies
                        node["inputs"] = inputs;
                        workflowDict["60:19"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            if (workflowDict.ContainsKey("60:28"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["60:28"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node["inputs"] = inputs;
                        workflowDict["60:28"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateKrea2Workflow(Dictionary<string, JsonElement> workflowDict)
        {
            // krea2RealismV1 workflow node map:
            //   6  = CLIPTextEncode (positive prompt) → inputs.text
            //   2  = KSampler → inputs.seed (steps/cfg/sampler/scheduler are turbo, left as-is)
            //   10 = EmptyLatentImage → inputs.width / height (overrides the FluxResolutionNode link)
            //   17 = Power Lora Loader (rgthree) → inputs.lora_1 (krea2 LoRA selection)
            //   22 = RTXVideoSuperResolution (upscale)
            //   23 = SaveImageKJ → replaced with standard SaveImage fed from node 22

            var resolutions = new[]
            {
                (1280, 1024), // Landscape
                (1024, 1280), // Portrait
                (1024, 1024), // Square
            };
            var (width, height) = resolutions[Math.Min(AspectRatioIndex, resolutions.Length - 1)];

            // Prompt (node 6 - CLIPTextEncode)
            if (workflowDict.ContainsKey("6"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["6"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = ImagePrompt;
                        node["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Seed (node 2 - KSampler); keep turbo steps/cfg/sampler/scheduler
            if (workflowDict.ContainsKey("2"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["2"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["seed"] = Seed == 0 ? new Random().NextInt64(0, 999999999999999) : Seed;
                        node["inputs"] = inputs;
                        workflowDict["2"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Resolution (node 10 - EmptyLatentImage)
            if (workflowDict.ContainsKey("10"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["10"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null)
                    {
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node["inputs"] = inputs;
                        workflowDict["10"] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }

            // Apply the selected Krea2 LoRA to the Power Lora Loader (node 17).
            ApplyKrea2Lora(workflowDict);

            // Replace SaveImageKJ (node 23) with a standard SaveImage fed from the RTX-upscaled
            // image (node 22). SaveImageKJ writes the file but does NOT register its result in
            // ComfyUI's /history outputs, so remote retrieval never finds it; a standard SaveImage
            // always reports ui.images (type "output"). Saves to output root as "Krea2_xxxxx_.png"
            // so the prefix-based local retrieval (TextGeneratorWorkflow.Krea2 => "Krea2_") matches too.
            workflowDict["23"] = JsonSerializer.SerializeToElement(new
            {
                inputs = new { filename_prefix = "Krea2", images = new object[] { "22", 0 } },
                class_type = "SaveImage",
                _meta = new { title = "Save Image (FlipPix)" }
            });

            // Drop the PreviewImage node (5) so the upscaled SaveImage is the only image output.
            workflowDict.Remove("5");

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        /// <summary>
        /// Applies the user's Krea2 LoRA selection to the Power Lora Loader (rgthree) node 17
        /// in the krea2 realism workflow. The LoRA reference is the <loras>/krea2 relative path
        /// that ComfyUI expects (e.g. "krea2/Krea2-realism-V1.safetensors").
        /// </summary>
        private void ApplyKrea2Lora(Dictionary<string, JsonElement> workflowDict)
        {
            if (!workflowDict.ContainsKey("17")) return;

            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["17"].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;

            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;

            // Preserve the slot's current lora name so the disabled fallback keeps a valid reference.
            var existingLoraName = "krea2/Krea2-realism-V1.safetensors";
            if (inputs.ContainsKey("lora_1") && inputs["lora_1"] is JsonElement le && le.ValueKind == JsonValueKind.Object)
            {
                var slot = JsonSerializer.Deserialize<Dictionary<string, object>>(le.GetRawText());
                if (slot != null && slot.ContainsKey("lora"))
                    existingLoraName = slot["lora"]?.ToString() ?? existingLoraName;
            }

            bool hasSelection = !string.IsNullOrEmpty(SelectedKreaLora)
                && SelectedKreaLora != "No LoRAs available"
                && SelectedKreaLora != "Error loading LoRAs";

            object lora1Config = hasSelection
                ? new { on = true, lora = $"{_kreaLoraSubfolder}/{SelectedKreaLora}.safetensors", strength = 1.0 }
                : new { on = false, lora = existingLoraName, strength = 0.0 };

            inputs["lora_1"] = JsonSerializer.Deserialize<object>(JsonSerializer.Serialize(lora1Config))!;
            node["inputs"] = inputs;
            workflowDict["17"] = JsonSerializer.SerializeToElement(node);
            AddLog($"Krea2 LoRA: {(hasSelection ? $"{_kreaLoraSubfolder}/{SelectedKreaLora}.safetensors" : "none")}");
        }

        private Dictionary<string, JsonElement> AddLoraToWorkflow(Dictionary<string, JsonElement> workflowDict, string loraName)
        {
            try
            {
                AddLog($"Applying Lora: {loraName}");

                // Check if this is the Zib-Zit workflow with Power Lora Loader (node 583)
                if (workflowDict.ContainsKey("583"))
                {
                    // Zib-Zit workflow uses Power Lora Loader (rgthree) node
                    AddLog("Detected Power Lora Loader (Zib-Zit workflow)");

                    var node583 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["583"].GetRawText());
                    if (node583 != null && node583.ContainsKey("inputs"))
                    {
                        var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(node583["inputs"]));
                        if (inputs != null)
                        {
                            // Update lora_1 slot to enable it and set the selected lora
                            // The lora structure in Power Lora Loader is: { on: bool, lora: string, strength: float }
                            var lora1Config = new
                            {
                                on = true,
                                lora = $"{loraName}.safetensors",
                                strength = 1.0
                            };

                            inputs["lora_1"] = JsonSerializer.Deserialize<object>(
                                JsonSerializer.Serialize(lora1Config))!;

                            node583["inputs"] = inputs;
                            workflowDict["583"] = JsonSerializer.SerializeToElement(node583);
                            AddLog($"Successfully enabled lora_1 with: {loraName}.safetensors");
                        }
                    }
                }
                else
                {
                    // Legacy workflow: Create LoraLoader node (using a high node number to avoid conflicts)
                    AddLog("Using legacy LoraLoader node");

                    var loraNodeNumber = "100";
                    var loraNode = new
                    {
                        inputs = new
                        {
                            lora_name = $"zimage/{loraName}.safetensors",
                            strength_model = 1.0,
                            strength_clip = 1.0,
                            model = new object[] { "46", 0 }, // Connect to UNETLoader (node 46)
                            clip = new object[] { "39", 0 }   // Connect to CLIPLoader (node 39)
                        },
                        class_type = "LoraLoader",
                        _meta = new
                        {
                            title = "Load LoRA"
                        }
                    };

                    workflowDict[loraNodeNumber] = JsonSerializer.SerializeToElement(loraNode);

                    // Update nodes that use the model to use the Lora-enhanced model instead
                    // Update ModelSamplingAuraFlow (node 47) to use Lora output
                    if (workflowDict.ContainsKey("47"))
                    {
                        var node47 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["47"].GetRawText());
                        if (node47 != null && node47.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node47["inputs"]));
                            if (inputs != null)
                            {
                                inputs["model"] = new object[] { loraNodeNumber, 0 }; // Use Lora-enhanced model
                                node47["inputs"] = inputs;
                                workflowDict["47"] = JsonSerializer.SerializeToElement(node47);
                            }
                        }
                    }

                    // Update CLIPTextEncode (node 45) to use Lora-enhanced CLIP
                    if (workflowDict.ContainsKey("45"))
                    {
                        var node45 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["45"].GetRawText());
                        if (node45 != null && node45.ContainsKey("inputs"))
                        {
                            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                JsonSerializer.Serialize(node45["inputs"]));
                            if (inputs != null && inputs.ContainsKey("clip"))
                            {
                                inputs["clip"] = new object[] { loraNodeNumber, 1 }; // Use Lora-enhanced CLIP (output 1)
                                node45["inputs"] = inputs;
                                workflowDict["45"] = JsonSerializer.SerializeToElement(node45);
                            }
                        }
                    }

                    AddLog($"Successfully added Lora node {loraNodeNumber} for {loraName}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error adding Lora to workflow: {ex.Message}");
            }

            return workflowDict;
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
        {
            var images = new List<byte[]>();

            try
            {
                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl;
                if (string.IsNullOrEmpty(baseUrl))
                {
                    _logger.LogWarning("Settings BaseUrl is null or empty, reloading settings");
                    baseUrl = _settingsService.LoadSettings().BaseUrl;
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        _logger.LogWarning("Failed to load BaseUrl from settings, using default");
                        baseUrl = "http://127.0.0.1:8188";
                    }
                }

                // Parse the URL to get server and port
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;
                var actualPort = uri.Port.ToString();

                // Check if ComfyUI is running locally or remotely
                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"ComfyUI server: {actualServer}:{actualPort}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                if (isRemoteComfyUI)
                {
                    AddLog("Detected remote ComfyUI server, downloading generated image...");

                    // Get output files for this specific prompt ID
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    AddLog($"Found {outputFiles.Count} output files for prompt {promptId}");

                    if (outputFiles.Any())
                    {
                        // Get the first image file (there should typically be just one)
                        var imageFile = outputFiles.FirstOrDefault(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg"));

                        if (!string.IsNullOrEmpty(imageFile))
                        {
                            AddLog($"Downloading generated image: {imageFile}");

                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imageFile);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                            else
                            {
                                AddLog($"Failed to download image: {imageFile}");
                            }
                        }
                        else
                        {
                            AddLog("No image files found in prompt output");
                            foreach (var file in outputFiles)
                            {
                                AddLog($"  - {file}");
                            }
                        }
                    }
                    else
                    {
                        AddLog("No output files found for this prompt, trying fallback approach...");

                        // Try the fallback approach
                        var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                        if (fallbackImage != null)
                        {
                            images.Add(fallbackImage);
                            AddLog($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                        }
                        else
                        {
                            AddLog("Failed to download image using all available methods");
                            AddLog("This might be due to:");
                            AddLog("- ComfyUI output folder not being accessible via HTTP");
                            AddLog("- Different filename pattern than expected");
                            AddLog("- ComfyUI server configuration preventing file access");
                        }
                    }
                }
                else
                {
                    // Local ComfyUI - check the output folder directly
                    var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                    if (string.IsNullOrEmpty(comfyUIOutputDir))
                    {
                        AddLog("ERROR: ComfyUI output folder not configured");
                        AddLog("Please restart the application and configure the ComfyUI folder path");
                        return images;
                    }

                    // Check if workflow is amateurZimageAPI (has node 760 or 107)
                    var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(_lastWorkflow.GetRawText());
                    bool isAmateurZimageApi = workflowDict != null && (workflowDict.ContainsKey("760") || workflowDict.ContainsKey("107"));

                    // For Zimage (Zib-Zit workflow), the output is in a subdirectory: ZImage/%date/
                    string searchDirectory;
                    if (SelectedWorkflow == TextGeneratorWorkflow.Zimage && !isAmateurZimageApi)
                    {
                        // Look in ZImage subdirectory with today's date (for Zib-Zit workflow)
                        var dateSubdir = DateTime.Now.ToString("yyyy_MM_dd");
                        searchDirectory = Path.Combine(comfyUIOutputDir, "ZImage", dateSubdir);
                        AddLog($"Zimage workflow: searching in {searchDirectory}");
                    }
                    else if (isAmateurZimageApi)
                    {
                        // amateurZimageAPI: search directly in ZImage folder for AmateurImage files
                        searchDirectory = Path.Combine(comfyUIOutputDir, "ZImage");
                        AddLog($"amateurZimageAPI workflow: searching in {searchDirectory}");
                    }
                    else
                    {
                        searchDirectory = comfyUIOutputDir;
                    }

                    if (!Directory.Exists(comfyUIOutputDir))
                    {
                        AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                        AddLog("Please check the ComfyUI folder configuration in settings");
                        return images;
                    }

                    // Look for files based on the selected workflow
                    List<string> imageFiles;
                    if (isAmateurZimageApi)
                    {
                        // amateurZimageAPI workflow: files are named "AmateurImage_00001.png"
                        // Look for AmateurImage*.png pattern in the ZImage folder
                        AddLog("Searching for AmateurImage*.png pattern...");
                        if (Directory.Exists(searchDirectory))
                        {
                            imageFiles = Directory.GetFiles(searchDirectory, "AmateurImage*.png")
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .ToList();
                        }
                        else
                        {
                            AddLog($"ZImage output directory not found: {searchDirectory}");
                            imageFiles = new List<string>();
                        }
                    }
                    else if (SelectedWorkflow == TextGeneratorWorkflow.Zimage)
                    {
                        // Zib-Zit workflow: files are named like "False__0_blur_02.png"
                        // Just look for all PNG files in the subdirectory and sort by modification time
                        if (Directory.Exists(searchDirectory))
                        {
                            imageFiles = Directory.GetFiles(searchDirectory, "*.png")
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .ToList();
                        }
                        else
                        {
                            AddLog($"Zimage output directory not found: {searchDirectory}");
                            // Try to find the ZImage directory and its subdirectories
                            var zimageBaseDir = Path.Combine(comfyUIOutputDir, "ZImage");
                            if (Directory.Exists(zimageBaseDir))
                            {
                                AddLog($"Found ZImage directory, searching for recent files...");
                                imageFiles = Directory.GetFiles(zimageBaseDir, "*.png", SearchOption.AllDirectories)
                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                    .Take(20)
                                    .ToList();
                                AddLog($"Found {imageFiles.Count} files in ZImage directory tree");
                                // Update searchDirectory so downstream Directory.Exists check passes
                                searchDirectory = zimageBaseDir;
                            }
                            else
                            {
                                AddLog($"ZImage directory not found at: {zimageBaseDir}");
                                imageFiles = new List<string>();
                            }
                        }
                    }
                    else
                    {
                        // Other workflows use prefix-based file naming
                        var prefix = SelectedWorkflow switch
                        {
                            TextGeneratorWorkflow.Qwen2512 => "qwen2512_",
                            TextGeneratorWorkflow.Klien => "F2K_txt2img_",
                            TextGeneratorWorkflow.Anima => "Anima_",
                            TextGeneratorWorkflow.Krea2 => "Krea2_",
                            _ => "z-image_"
                        };
                        imageFiles = Directory.GetFiles(comfyUIOutputDir, $"{prefix}*.png")
                            .OrderByDescending(f => ExtractFileNumber(f)) // Sort by extracted number for proper numeric ordering
                            .ToList();
                    }

                    AddLog($"Output directory path: {comfyUIOutputDir}");
                    AddLog($"Search directory: {searchDirectory}");
                    AddLog($"Directory exists: {Directory.Exists(searchDirectory)}");

                    if (!Directory.Exists(searchDirectory))
                    {
                        AddLog($"ERROR: Output directory does not exist: {searchDirectory}");
                        return images;
                    }

                    // Debug: List ALL files in the directory to understand what's there
                    try
                    {
                        var allFiles = Directory.GetFiles(searchDirectory, "*.png")
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .Take(10)
                            .Select(f => $"{Path.GetFileName(f)} ({(DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds:F0}s old)");

                        AddLog("All PNG files in directory (first 10 by time):");
                        foreach (var file in allFiles)
                        {
                            AddLog($"  - {file}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Error listing files: {ex.Message}");
                    }

                    var workflowName = SelectedWorkflow.ToString();
                    AddLog($"Found {imageFiles.Count} {workflowName} PNG files in output directory");

                    if (imageFiles.Any())
                    {
                        var latestFile = imageFiles.First();
                        var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                        AddLog($"Latest {workflowName} file: {Path.GetFileName(latestFile)}");
                        AddLog($"File modification time: {File.GetLastWriteTime(latestFile):yyyy-MM-dd HH:mm:ss}");
                        AddLog($"Current time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        AddLog($"File age: {fileAge.TotalSeconds:F0} seconds");

                        // For Zimage, use the most recently modified file
                        // For other workflows, use the highest numbered file
                        AddLog($"Using latest {workflowName} file: {Path.GetFileName(latestFile)}");
                        var imageData = await File.ReadAllBytesAsync(latestFile);
                        images.Add(imageData);
                    }
                    else
                    {
                        AddLog($"No {workflowName} files found, looking for any other PNG files...");

                        // Fallback to any PNG files in the search directory
                        var allImageFiles = Directory.GetFiles(searchDirectory, "*.png")
                            .Where(f => !Path.GetFileName(f).StartsWith("temp_")) // Exclude temporary files
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();

                        // If still no files, try the base output directory
                        if (!allImageFiles.Any() && searchDirectory != comfyUIOutputDir)
                        {
                            AddLog($"No files in subdirectory, checking base output directory...");
                            allImageFiles = Directory.GetFiles(comfyUIOutputDir, "*.png", SearchOption.AllDirectories)
                                .Where(f => !Path.GetFileName(f).StartsWith("temp_"))
                                .OrderByDescending(f => File.GetLastWriteTime(f))
                                .Take(50)
                                .ToList();
                        }

                        AddLog($"Found {allImageFiles.Count} other PNG files");

                        if (allImageFiles.Any())
                        {
                            var latestFile = allImageFiles.First();
                            AddLog($"Using latest file as fallback: {Path.GetFileName(latestFile)}");
                            var imageData = await File.ReadAllBytesAsync(latestFile);
                            images.Add(imageData);
                        }
                        else
                        {
                            AddLog("No PNG output files found in directory...");

                            // Try to list what files are actually there
                            try
                            {
                                var allFiles = Directory.GetFiles(comfyUIOutputDir)
                                    .OrderByDescending(f => File.GetLastWriteTime(f))
                                    .Take(10)
                                    .Select(f => $"{Path.GetFileName(f)} ({(DateTime.Now - File.GetLastWriteTime(f)).TotalSeconds:F0}s old)");

                                AddLog("Files in output directory (first 10):");
                                foreach (var file in allFiles)
                                {
                                    AddLog($"  - {file}");
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLog($"Could not list directory contents: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output images: {ex.Message}");
            }

            return images;
        }

        private int ExtractFileNumber(string filePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Extract number based on the selected workflow
            // Zimage: "z-image_12345_" pattern
            // Qwen: "qwen2512_00001_" pattern (5-digit zero-padded)
            // Klien: "F2K_txt2img_00001_" pattern (5-digit zero-padded)
            var patterns = SelectedWorkflow switch
            {
                TextGeneratorWorkflow.Qwen2512 => new[] { @"qwen2512_(\d+)_", @"qwen2512_(\d+)$" },
                TextGeneratorWorkflow.Klien => new[] { @"F2K_txt2img_(\d+)_", @"F2K_txt2img_(\d+)$" },
                TextGeneratorWorkflow.Anima => new[] { @"Anima_(\d+)_", @"Anima_(\d+)$" },
                TextGeneratorWorkflow.Krea2 => new[] { @"Krea2_(\d+)_", @"Krea2_(\d+)$" },
                _ => new[] { @"z-image_(\d+)_", @"z-image_(\d+)$" }
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(fileName, pattern);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
                {
                    return number;
                }
            }

            // Fallback: return 0 if we can't extract the number
            return 0;
        }

        // Clicking a completed queue item's thumbnail promotes its image into the
        // "Latest Result" preview so it can be sent to Camera Edit / Story / etc.
        private void SelectQueueResult(ImagePromptQueueItem? item)
        {
            if (item == null || string.IsNullOrEmpty(item.OutputImagePath) || !File.Exists(item.OutputImagePath))
                return;

            SelectedQueueItem = item;
            ResultImagePath = item.OutputImagePath;
            LoadResultPreview(item.OutputImagePath);
            HasResultImage = true;
            StatusBarMessage = $"Loaded result: {Path.GetFileName(item.OutputImagePath)}";
        }

        private void LoadResultPreview(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                ResultImageSource = bitmap;

                var fileInfo = new FileInfo(imagePath);
                ImageInfo = $"Size: {fileInfo.Length / 1024}KB | {bitmap.PixelWidth}x{bitmap.PixelHeight}";

                AddLog("Result image preview loaded");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading result preview: {ex.Message}");
            }
        }

        private void CancelGeneration()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested by user");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        private void OpenResultFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(ResultImagePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result folder: {ex.Message}");
            }
        }

        private void OpenResultImage()
        {
            try
            {
                if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ResultImagePath,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening result image: {ex.Message}");
            }
        }

        private void SendToCameraAngle()
        {
            var imagePath = ActiveResultImagePath;
            if (string.IsNullOrEmpty(imagePath)) return;

            try
            {
                // Load the image as the Camera Angle generator input (the setter loads its
                // own preview), then switch to that tab.
                _cameraAngle.InputImagePath = imagePath;

                // Camera Angle lives in the Advanced nav group (index 2) at tab index 3.
                SelectedNavGroup = 2;
                SelectedTabIndex = 3;

                AddLog($"Sent image to Camera Angle: {Path.GetFileName(imagePath)}");
                StatusBarMessage = $"Image sent to Camera Angle: {Path.GetFileName(imagePath)}";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Camera Angle: {ex.Message}");
                _logger.LogError($"Error sending to Camera Angle: {ex}");
                System.Windows.MessageBox.Show($"Error sending image to Camera Angle tab:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void SendToVideoGenerator()
        {
            var imagePath = ActiveResultImagePath;
            if (string.IsNullOrEmpty(imagePath) || _serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                if (videoWindow == null)
                {
                    AddLog("ERROR: Failed to create Video Generator window");
                    System.Windows.MessageBox.Show("Could not open Video Generator window.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (!videoWindow.IsVisible)
                {
                    videoWindow.Show();
                }
                videoWindow.WindowState = System.Windows.WindowState.Normal;
                videoWindow.Activate();
                videoWindow.Focus();

                if (videoWindow.DataContext is VideoGeneratorViewModel viewModel)
                {
                    viewModel.SetImagePath(imagePath);
                }

                AddLog($"Sent image to Video Generator: {Path.GetFileName(imagePath)}");
                StatusBarMessage = $"Image sent to Video Generator: {Path.GetFileName(imagePath)}";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Video Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening Video Generator window:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void SendToStory()
        {
            var imagePath = ActiveResultImagePath;
            if (string.IsNullOrEmpty(imagePath)) return;

            try
            {
                // Load the image as the Story Image Q input, then switch to that tab.
                _storyGeneratorQ.InputImagePath = imagePath;

                // Story Image Q lives in the Create nav group (index 0) at tab index 1.
                SelectedNavGroup = 0;
                SelectedTabIndex = 1;

                AddLog($"Sent image to Story Image Q: {Path.GetFileName(imagePath)}");
                StatusBarMessage = $"Image sent to Story Image Q: {Path.GetFileName(imagePath)}";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR sending to Story Image Q: {ex.Message}");
                _logger.LogError($"Error sending to Story Image Q: {ex}");
                System.Windows.MessageBox.Show($"Error sending image to Story Image Q tab:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the Video Generator's FFLF Seed Hunter tab and loads the current Story Image Q
        /// session's generated keyframes as a folder batch (overlapping FFLF pairs → continuous shot).
        /// </summary>
        private void OpenKeyframesInFflfSeedHunter()
        {
            if (_serviceProvider == null) return;

            try
            {
                var folder = _storyGeneratorQ.KeyframeOutputFolder;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    System.Windows.MessageBox.Show(
                        "No generated keyframes found yet. Generate the story images first, then try again.",
                        "No Keyframes", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var pngCount = Directory.EnumerateFiles(folder, "*.png").Count();
                if (pngCount < 2)
                {
                    System.Windows.MessageBox.Show(
                        $"Need at least 2 generated keyframes to form an FFLF pair (found {pngCount}).",
                        "Not Enough Keyframes", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                if (videoWindow == null)
                {
                    AddLog("ERROR: Failed to open Video Generator window");
                    System.Windows.MessageBox.Show("Could not open Video Generator window.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (!videoWindow.IsVisible) videoWindow.Show();
                videoWindow.WindowState = System.Windows.WindowState.Normal;
                videoWindow.Activate();
                videoWindow.Focus();

                if (videoWindow.DataContext is VideoGeneratorViewModel vm)
                {
                    // FFLF Seed Hunter is the last tab in VideoGeneratorWindow's TabControl (index 11).
                    vm.SelectedTabIndex = 11;
                    vm.FflfSeedHuntVM.LoadFolder(folder);
                    AddLog($"Opened FFLF Seed Hunter with {pngCount} keyframes from: {folder}");
                    StatusBarMessage = $"Loaded {pngCount} keyframes into FFLF Seed Hunter";
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening FFLF Seed Hunter: {ex.Message}");
                _logger.LogError($"Error opening FFLF Seed Hunter: {ex}");
                System.Windows.MessageBox.Show($"Error opening FFLF Seed Hunter:\n\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void NavigateToVideoGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;

                if (videoWindow == null)
                {
                    AddLog("ERROR: Could not create Video Generator window");
                    System.Windows.MessageBox.Show(
                        "Could not open Video Generator. Please check the log for details.",
                        "Navigation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (videoWindow.IsVisible)
                {
                    videoWindow.WindowState = System.Windows.WindowState.Normal;
                    videoWindow.Activate();
                    videoWindow.Focus();
                    return;
                }

                videoWindow.WindowState = System.Windows.WindowState.Normal;

                var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
                var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
                videoWindow.Left = Math.Max(50, (screenW - videoWindow.Width) / 2);
                videoWindow.Top = Math.Max(50, (screenH - videoWindow.Height) / 2);

                videoWindow.Show();
                videoWindow.Activate();
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException;
                var detail = inner != null ? $"\n\nCause: {inner.Message}" : string.Empty;
                AddLog($"ERROR navigating to Video Generator: {ex.Message}{(inner != null ? $" → {inner.Message}" : "")}");
                System.Windows.MessageBox.Show(
                    $"Could not open Video Generator:\n{ex.Message}{detail}",
                    "Navigation Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void NavigateToEnhanceVideo()
        {
            if (_serviceProvider == null) return;

            try
            {
                var enhanceWindow = _serviceProvider.GetService(typeof(VideoEnhanceWindow)) as VideoEnhanceWindow;
                enhanceWindow?.Show();
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Enhance Video: {ex.Message}");
            }
        }

        private void NavigateToImageAnalyzer()
        {
            if (_serviceProvider == null) return;

            try
            {
                var imageAnalyzerWindow = _serviceProvider.GetService(typeof(ImageAnalyzerWindow)) as ImageAnalyzerWindow;
                if (imageAnalyzerWindow != null)
                {
                    // Ensure window appears on screen
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = imageAnalyzerWindow.Width;
                    var windowHeight = imageAnalyzerWindow.Height;

                    // Use conservative positioning
                    imageAnalyzerWindow.Left = 150;
                    imageAnalyzerWindow.Top = 150;

                    // Ensure window is fully visible on screen
                    if (imageAnalyzerWindow.Left + windowWidth > screenWidth)
                        imageAnalyzerWindow.Left = Math.Max(50, screenWidth - windowWidth - 50);
                    if (imageAnalyzerWindow.Top + windowHeight > screenHeight)
                        imageAnalyzerWindow.Top = Math.Max(50, screenHeight - windowHeight - 50);

                    imageAnalyzerWindow.Show();
                    AddLog("Opened Image Analyzer window");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Analyzer: {ex.Message}");
            }
        }

  
        private void NavigateToStoryVideo()
        {
            if (_serviceProvider == null) return;

            try
            {
                var storyVideoWindow = _serviceProvider.GetService(typeof(StoryVideoWindow)) as StoryVideoWindow;
                if (storyVideoWindow != null)
                {
                    // Ensure window appears on screen
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = storyVideoWindow.Width;
                    var windowHeight = storyVideoWindow.Height;

                    // Use conservative positioning
                    storyVideoWindow.Left = 200;
                    storyVideoWindow.Top = 200;

                    // Ensure window is fully visible on screen
                    if (storyVideoWindow.Left + windowWidth > screenWidth)
                        storyVideoWindow.Left = Math.Max(50, screenWidth - windowWidth - 50);
                    if (storyVideoWindow.Top + windowHeight > screenHeight)
                        storyVideoWindow.Top = Math.Max(50, screenHeight - windowHeight - 50);

                    storyVideoWindow.Show();
                    AddLog("Opened Story Video window");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Story Video: {ex.Message}");
            }
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

        private void CancelQueue()
        {
            _queueCancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Cancel();
            // Unblock pause wait so cancellation can propagate
            _pauseEvent.Set();
            IsQueuePaused = false;
            AddLog("Queue cancellation requested");
        }

        private string QueueFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "queue", "image_generator_queue.json");

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
                // Don't persist completed items — they're session history, not pending work.
                // Keeps the queue file small so it never bloats or slows startup.
                var json = JsonSerializer.Serialize(PromptQueue.Where(i => i.Status != "Completed").ToList(), options);
                File.WriteAllText(QueueFilePath, json);
            }
            catch (Exception ex)
            {
                AddLog($"Error saving queue to file: {ex.Message}");
            }
        }

        /// <summary>
        /// Queues the persisted queue load at Background dispatcher priority so a large saved queue
        /// never blocks app startup; the file read + deserialize run off the UI thread.
        /// </summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(
                async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var savedItems = await Task.Run(() =>
                {
                    var json = File.ReadAllText(QueueFilePath);
                    return JsonSerializer.Deserialize<List<ImagePromptQueueItem>>(json);
                });

                if (savedItems != null && savedItems.Any())
                {
                    _promptQueue.Clear();
                    bool prunedCompleted = false;
                    foreach (var item in savedItems)
                    {
                        // Drop completed items so finished history never accumulates in the queue.
                        if (item.Status == "Completed") { prunedCompleted = true; continue; }
                        // Convert legacy "Queued" status to "Pending" for consistency
                        if (item.Status == "Queued")
                        {
                            item.Status = "Pending";
                        }
                        // Handle interrupted processing
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _promptQueue.Add(item);
                    }
                    OnPropertyChanged(nameof(HasQueueItems));
                    OnPropertyChanged(nameof(QueueCount));
                    OnPropertyChanged(nameof(PendingQueueCount));
                    AddLog($"Queue loaded from file: {_promptQueue.Count} items");
                    // Rewrite the (now smaller) file once so previously bloated queues shrink immediately.
                    if (prunedCompleted) SaveQueueToFile();
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading queue from file: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
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

        // Implementation of abstract base class properties
        public override string CurrentPromptText => ImagePrompt;

        public override int AspectRatioIndex
        {
            get => _aspectRatioIndex;
            set
            {
                _aspectRatioIndex = value;
                OnPropertyChanged();
            }
        }

        public override long Seed
        {
            get => _seed;
            set
            {
                _seed = value;
                OnPropertyChanged();
            }
        }

        // Override base class methods
        protected override void OnPromptSaved(string promptName)
        {
            AddLog($"Prompt saved: {promptName}");
            StatusBarMessage = $"Prompt saved: {promptName}";
        }

        protected override void OnPromptDeleted(string promptName)
        {
            AddLog($"Prompt deleted: {promptName}");
            StatusBarMessage = $"Prompt deleted: {promptName}";
        }

        protected override void OnPromptLoaded(SavedPrompt savedPrompt)
        {
            ImagePrompt = savedPrompt.Prompt;
            AspectRatioIndex = savedPrompt.AspectRatioIndex;
            Steps = savedPrompt.Steps;
            Cfg = savedPrompt.Cfg;
            Seed = savedPrompt.Seed;
            Denoise = savedPrompt.Denoise;

            // Load additional settings if they exist in the additional data
            if (savedPrompt.AdditionalData != null && savedPrompt.AdditionalData is Dictionary<string, object> additionalData)
            {
                if (additionalData.TryGetValue("SelectedWorkflow", out var workflowObj) && workflowObj is int workflowInt)
                {
                    SelectedWorkflow = (TextGeneratorWorkflow)workflowInt;
                }

                if (additionalData.TryGetValue("LoraEnabled", out var loraEnabledObj) && loraEnabledObj is bool loraEnabled)
                {
                    LoraEnabled = loraEnabled;
                }

                if (additionalData.TryGetValue("SelectedLora", out var selectedLoraObj) && selectedLoraObj is string selectedLora)
                {
                    SelectedLora = selectedLora;
                }
            }

            AddLog($"Prompt loaded: {savedPrompt.Name}");
            StatusBarMessage = $"Prompt loaded: {savedPrompt.Name}";
        }

        protected override void OnPromptError(string error)
        {
            AddLog($"ERROR: {error}");
            StatusBarMessage = error;
        }

        public override Dictionary<string, object> GetAdditionalPromptData()
        {
            var additionalData = new Dictionary<string, object>
            {
                { "SelectedWorkflow", (int)SelectedWorkflow },
                { "LoraEnabled", LoraEnabled },
                { "SelectedLora", SelectedLora }
            };
            return additionalData;
        }

        // Queue Management Methods

        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var queueItem = new ImagePromptQueueItem
            {
                Prompt = ImagePrompt,
                AspectRatioIndex = AspectRatioIndex,
                Steps = Steps,
                Cfg = Cfg,
                Seed = Seed,
                Denoise = Denoise,
                LoraEnabled = LoraEnabled,
                SelectedLora = SelectedLora,
                SelectedKreaLora = SelectedKreaLora,
                SelectedWorkflow = SelectedWorkflow,
                // Style info capture
                SelectedStyleIndex = SelectedStyleIndex,
                StyleName = SelectedStyle?.Name ?? ""
            };

            PromptQueue.Add(queueItem);
            SaveQueueToFile();
            AddLog($"Added prompt to queue: {queueItem.DisplayPrompt}");
            StatusBarMessage = $"Added to queue ({PromptQueue.Count} items)";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyActionCommands();

            // Auto-start queue processing if not already processing queue and not processing single image
            if (!IsProcessingQueue && !IsProcessing && PromptQueue.Any(q => q.Status == "Pending"))
            {
                _ = ProcessQueueAsync();
            }
        }

        private void RemoveFromQueue(ImagePromptQueueItem? item)
        {
            if (item == null) return;

            PromptQueue.Remove(item);
            SaveQueueToFile();
            AddLog($"Removed prompt from queue: {item.DisplayPrompt}");
            StatusBarMessage = $"Removed from queue ({PromptQueue.Count} items)";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyActionCommands();
        }

        private void RetryQueueItem(ImagePromptQueueItem? item)
        {
            if (item == null) return;

            item.Status = "Pending";
            item.ErrorMessage = null;
            item.Progress = 0;
            SaveQueueToFile();
            AddLog($"Retrying queue item: {item.DisplayPrompt}");
            StatusBarMessage = $"Item queued for retry";

            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyActionCommands();

            if (!IsProcessingQueue && PromptQueue.Any(q => q.Status == "Pending"))
            {
                _ = ProcessQueueAsync();
            }
        }

        private void ClearQueue()
        {
            if (!PromptQueue.Any()) return;

            var count = PromptQueue.Count;
            PromptQueue.Clear();
            SaveQueueToFile();
            AddLog($"Cleared {count} items from queue");
            StatusBarMessage = "Queue cleared";

            OnPropertyChanged(nameof(HasQueueItems));
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(PendingQueueCount));
            NotifyActionCommands();
        }

        private async Task ProcessQueueAsync()
        {
            // Thread-safe guard: only one invocation can proceed
            if (IsProcessingQueue) return;
            if (!_promptQueue.Any(q => q.Status == "Pending")) return;

            IsProcessingQueue = true;

            try
            {
                // Create a queue-specific cancellation token source
                _queueCancellationTokenSource?.Dispose();
                _queueCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
                IsWaitingForLease = true;
                AddLog("Starting queue processing...");
                AddLog("Waiting for other workflows to finish...");

                WorkflowQueueCoordinator.WorkflowLease lease;
                try
                {
                    lease = await _workflowCoordinator.AcquireAsync("ImageGenerator", _queueCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    AddLog("Queue processing cancelled while waiting for lease");
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"Error acquiring workflow lease: {ex.Message}");
                    return;
                }

                AddLog("=== Starting queue processing ===");
                IsWaitingForLease = false;

                using (lease)
                try
                {
                    ImagePromptQueueItem? queueItem;
                    while ((queueItem = PromptQueue.FirstOrDefault(q => q.Status == "Pending")) != null)
                    {
                        // Check for queue cancellation
                        if (_queueCancellationTokenSource?.Token.IsCancellationRequested == true)
                        {
                            AddLog("Queue processing cancelled");
                            break;
                        }

                        // Wait if paused
                        _pauseEvent.Wait(_queueCancellationTokenSource?.Token ?? CancellationToken.None);

                        try
                        {
                            queueItem.Status = "Processing";
                            queueItem.StartedAt = DateTime.Now;
                            queueItem.Progress = 0;
                            SaveQueueToFile();
                            OnPropertyChanged(nameof(PendingQueueCount));

                            AddLog($"Processing queue item: {queueItem.DisplayPrompt}");

                            ImagePrompt = queueItem.Prompt;
                            AspectRatioIndex = queueItem.AspectRatioIndex;
                            Steps = queueItem.Steps;
                            Cfg = queueItem.Cfg;
                            Seed = queueItem.Seed;
                            Denoise = queueItem.Denoise;
                            LoraEnabled = queueItem.LoraEnabled;
                            SelectedLora = queueItem.SelectedLora;
                            SelectedWorkflow = queueItem.SelectedWorkflow;

                            await ProcessQueueItemAsync(queueItem);

                            queueItem.Status = "Completed";
                            queueItem.CompletedAt = DateTime.Now;
                            queueItem.Progress = 100;
                            SaveQueueToFile();
                            AddLog($"Completed queue item: {queueItem.DisplayPrompt}");
                        }
                        catch (OperationCanceledException)
                        {
                            queueItem.Status = "Failed";
                            queueItem.ErrorMessage = "Cancelled";
                            SaveQueueToFile();
                            AddLog($"Queue item cancelled: {queueItem.DisplayPrompt}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            queueItem.Status = "Failed";
                            queueItem.ErrorMessage = ex.Message;
                            SaveQueueToFile();
                            AddLog($"ERROR processing queue item: {ex.Message}");
                        }
                        finally
                        {
                            OnPropertyChanged(nameof(CompletedQueueCount));
                        }
                    }

                    StatusBarMessage = $"Queue processing complete. {CompletedQueueCount}/{QueueCount} items completed.";
                    AddLog("=== Queue processing ended ===");
                }
                catch (Exception ex)
                {
                    AddLog($"Error in queue processing: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Unhandled error in queue processing: {ex.Message}");
                _logger.LogError($"Unhandled error in ProcessQueueAsync: {ex}");
            }
            finally
            {
                // SAFETY NET: Always reset all flags regardless of how we got here
                IsProcessingQueue = false;
                IsWaitingForLease = false;
                IsQueuePaused = false;
                _pauseEvent.Set();
                _queueCancellationTokenSource?.Dispose();
                _queueCancellationTokenSource = null;
                NotifyActionCommands();
            }
        }

        private async Task ProcessQueueItemAsync(ImagePromptQueueItem queueItem)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = _queueCancellationTokenSource != null
                ? System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken, _queueCancellationTokenSource.Token)
                : System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessing = true;

                // Clear previous result
                HasResultImage = false;
                ResultImageSource = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Prompt: {queueItem.Prompt}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    AddLog("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }
                else
                {
                    AddLog("ComfyUI already connected");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Load workflow based on queue item's workflow selection (use item settings, not current UI)
                string workflowPath;

                switch (queueItem.SelectedWorkflow)
                {
                    case TextGeneratorWorkflow.Qwen2512:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "qwen2512API-text.json");
                        AddLog("Using Qwen2512 workflow");
                        break;

                    case TextGeneratorWorkflow.Klien:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "klein", "KlienX3n-Text-Ultimate-API.json");
                        AddLog("Using Klien workflow");
                        break;

                    case TextGeneratorWorkflow.Anima:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "anima", "Anima.json");
                        AddLog("Using Anima workflow");
                        break;

                    case TextGeneratorWorkflow.Krea2:
                        workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "krea", "krea2RealismV1_krea2RealismV1WF.json");
                        AddLog("Using Krea2 workflow");
                        break;

                    case TextGeneratorWorkflow.Zimage:
                    default:
                        // Use ZStyle workflow file if a style was selected
                        if (!string.IsNullOrEmpty(queueItem.StyleName))
                        {
                            var selectedStyle = _allStyles.FirstOrDefault(s => s.Name == queueItem.StyleName);
                            if (selectedStyle != null)
                            {
                                workflowPath = selectedStyle.WorkflowFile;
                                AddLog($"Using Zimage workflow with style: {selectedStyle.Name}");
                            }
                            else
                            {
                                // Fallback to default if style not found
                                workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "zimage", "base", "Zib-Zit.json");
                                AddLog($"Style '{queueItem.StyleName}' not found, falling back to Zib-Zit.json");
                            }
                        }
                        else
                        {
                            workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "image", "zimage", "base", "Zib-Zit.json");
                            AddLog("Using default Zib-Zit workflow (no style selected)");
                        }
                        break;
                }

                if (!File.Exists(workflowPath))
                {
                    AddLog($"ERROR: Workflow file not found: {workflowPath}");
                    throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
                }

                AddLog($"Loading workflow: {workflowPath}");
                var workflowJson = await File.ReadAllTextAsync(workflowPath, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Update workflow with parameters
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 10;
                queueItem.Progress = 10;

                // Before UpdateWorkflowParameters call, temporarily apply queue item settings
                var originalPrompt = ImagePrompt;
                var originalAspectRatio = AspectRatioIndex;
                var originalSteps = Steps;
                var originalCfg = Cfg;
                var originalSeed = Seed;
                var originalDenoise = Denoise;
                var originalLoraEnabled = LoraEnabled;
                var originalSelectedLora = SelectedLora;
                var originalSelectedKreaLora = SelectedKreaLora;
                var originalSelectedWorkflow = SelectedWorkflow;

                ImagePrompt = queueItem.Prompt;
                AspectRatioIndex = queueItem.AspectRatioIndex;
                Steps = queueItem.Steps;
                Cfg = queueItem.Cfg;
                Seed = queueItem.Seed;
                Denoise = queueItem.Denoise;
                LoraEnabled = queueItem.LoraEnabled;
                SelectedLora = queueItem.SelectedLora;
                SelectedKreaLora = queueItem.SelectedKreaLora;
                SelectedWorkflow = queueItem.SelectedWorkflow;

                var updatedWorkflow = UpdateWorkflowParameters(workflow);
                _lastWorkflow = updatedWorkflow; // needed by GetOutputImagesFromComfyUI for workflow detection

                // Restore original properties
                ImagePrompt = originalPrompt;
                AspectRatioIndex = originalAspectRatio;
                Steps = originalSteps;
                Cfg = originalCfg;
                Seed = originalSeed;
                Denoise = originalDenoise;
                LoraEnabled = originalLoraEnabled;
                SelectedLora = originalSelectedLora;
                SelectedKreaLora = originalSelectedKreaLora;
                SelectedWorkflow = originalSelectedWorkflow;

                // Execute workflow
                ProcessingStatus = "Generating image...";
                ProcessingProgress = 30;
                queueItem.Progress = 30;
                AddLog("Executing workflow in ComfyUI...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6);
                            queueItem.Progress = 30 + (percent * 0.6);
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    queueItem.Progress = 90;
                    ProcessingStatus = "Workflow completed, retrieving output...";
                });

                AddLog($"Workflow execution completed with prompt ID: {promptId}");

                // Get output images from ComfyUI output folder
                ProcessingStatus = "Retrieving output image...";
                ProcessingProgress = 95;
                AddLog("Looking for generated image...");

                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20;

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token);
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "image-generator");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var prefix = SelectedWorkflow switch
                    {
                        TextGeneratorWorkflow.Qwen2512 => "qwen2512",
                        TextGeneratorWorkflow.Klien => "f2k-txt2img",
                        TextGeneratorWorkflow.Anima => "anima",
                        TextGeneratorWorkflow.Krea2 => "krea2",
                        _ => "z-image"
                    };
                    var outputPath = Path.Combine(outputDir, $"{prefix}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    await LocalCopyService.CopyImageAsync(outputPath);
                    AddLog($"Output saved: {outputPath}");

                    queueItem.OutputImagePath = outputPath;
                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    queueItem.Progress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Image generation complete - {Path.GetFileName(outputPath)}";
                }
                else
                {
                    AddLog("WARNING: No output images received after all retries");
                    throw new Exception("No output images were generated. Please check the ComfyUI console for errors.");
                }
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private bool IsRemoteUrl(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url))
                    return false;

                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                // Check if it's not a local address
                return !host.Equals("localhost") &&
                       !host.Equals("127.0.0.1") &&
                       !host.Equals("0.0.0.0") &&
                       !host.Equals("::1");
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _queueCancellationTokenSource?.Cancel();
                _queueCancellationTokenSource?.Dispose();
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _pauseEvent?.Dispose();

                // Dispose nested ViewModels
                _analyzer?.Dispose();
                _cameraEdit?.Dispose();
                _storyGeneratorQ?.Dispose();
                _storyGeneratorAmateur?.Dispose();
                _amateurGenerator?.Dispose();
                _cameraAngle?.Dispose();

                // Clear collections
                _availableLoras?.Clear();
                _promptQueue?.Clear();

                // Clear string properties
                _imagePrompt = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;
                _comfyUIServer = string.Empty;
                _comfyUIPort = string.Empty;
                _statusBarMessage = string.Empty;
                _resultImagePath = string.Empty;
                _imageInfo = string.Empty;
                _selectedLora = string.Empty;

                _disposed = true;
            }
        }
    }
}
