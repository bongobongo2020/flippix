using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Services;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels
{
    public partial class FlipPixViewModel : BasePromptViewModel, IDisposable
    {
        private bool _disposed = false;
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private new readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly IFileDialogService _fileDialogService;

        private string _imageFilePath = string.Empty;
        private BitmapImage? _imagePreviewSource;
        private BitmapImage? _resultPreviewSource;
        private string _imageInfo = string.Empty;
        private string _selectedCameraControl = "Rotate Right 90°";
        private string _customPrompt = string.Empty;
        private string _negativePrompt = "ugly face, fat, noise, low resolution, lack of detail, wide shoulders, muscular";
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultImage = false;
        private string _resultImagePath = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        // Queue settings
        private ObservableCollection<CameraQueueItem> _queueItems = new();
        private bool _isProcessingQueue = false;
        private CameraQueueItem? _currentQueueItem;
        private int _queueProgress = 0;
        private int _queueTotal = 0;

        // Sampler settings
        private string _samplerName = "euler";
        private string _scheduler = "beta57";

        // Override base class properties
        private int _steps = 8;
        private double _cfg = 1.5;
        private long _seed = 0;
        private double _denoise = 1.0;
        private int _aspectRatioIndex = 0;

        public FlipPixViewModel(FlipPix.ComfyUI.Services.ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null, IPromptService? promptService = null, IFileDialogService? fileDialogService = null)
            : base(promptService ?? new PromptService(logger), logger, "CameraEdit")
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            // Initialize commands
            SelectImageCommand = new RelayCommand(SelectImage);
            ProcessImageCommand = new RelayCommand(async () => await ProcessImageAsync(), () => CanProcess);
            CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultImage);
            SelectCameraControlCommand = new RelayCommand<string>(SelectCameraControl);
            SaveCustomPromptCommand = new RelayCommand(SaveCustomPrompt, () => CanSavePrompt);
            DeleteSavedPromptCommand = new RelayCommand(DeleteSavedPrompt, () => CanDeletePrompt);
            SendToVideoGeneratorCommand = new RelayCommand(SendToVideoGenerator, () => HasResultImage);
            NavigateToImageGeneratorCommand = new RelayCommand(NavigateToImageGenerator);
            NavigateToVideoGeneratorCommand = new RelayCommand(NavigateToVideoGenerator);
            NavigateToStoryVideoCommand = new RelayCommand(NavigateToStoryVideo);
            AddToQueueCommand = new RelayCommand(AddToQueue, () => CanAddToQueue);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => QueueItems.Any());
            RemoveFromQueueCommand = new RelayCommand<CameraQueueItem>(RemoveFromQueue);

            // Initialize camera control options
            InitializeCameraControlOptions();

            AddLog("FlipPix initialized");
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
                    OnPropertyChanged(nameof(CanProcess));
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
                var oldValue = _imagePreviewSource;
                _imagePreviewSource = value;
                AddLog($"ImagePreviewSource changed: {oldValue?.ToString() ?? "null"} -> {value?.ToString() ?? "null"}, Dimensions: {value?.PixelWidth}x{value?.PixelHeight}");
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasInputImage));
                OnPropertyChanged(nameof(NoInputImage));

                // Force UI refresh
                OnPropertyChanged(nameof(ImageFilePath));
                OnPropertyChanged(nameof(ImageInfo));
            }
        }

        public bool HasInputImage => ImagePreviewSource != null;
        public bool NoInputImage => ImagePreviewSource == null;

        public BitmapImage? ResultPreviewSource
        {
            get => _resultPreviewSource;
            set
            {
                var oldValue = _resultPreviewSource;
                _resultPreviewSource = value;
                AddLog($"ResultPreviewSource changed: {oldValue?.ToString() ?? "null"} -> {value?.ToString() ?? "null"}, Dimensions: {value?.PixelWidth}x{value?.PixelHeight}");
                OnPropertyChanged();

                // Force UI refresh
                OnPropertyChanged(nameof(HasResultImage));
                OnPropertyChanged(nameof(ResultImagePath));
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

        public ObservableCollection<CameraControlOption> CameraControlOptions { get; } = new();

        public string SelectedCameraControl
        {
            get => _selectedCameraControl;
            set
            {
                _selectedCameraControl = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDeletePrompt));
                OnPropertyChanged(nameof(IsSelectedPromptSaved));
                UpdateCustomPromptFromSelection();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string CustomPrompt
        {
            get => _customPrompt;
            set
            {
                _customPrompt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSavePrompt));
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
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanCancel));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanCancel => IsProcessing;

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
                OnPropertyChanged(nameof(NoResultImage));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool NoResultImage => !_hasResultImage;

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                _resultImagePath = value;
                OnPropertyChanged();
            }
        }

        // Queue Properties
        public ObservableCollection<CameraQueueItem> QueueItems
        {
            get => _queueItems;
            set
            {
                _queueItems = value;
                OnPropertyChanged();
            }
        }

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            set
            {
                _isProcessingQueue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanProcessQueue));
                OnPropertyChanged(nameof(CanAddToQueue));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public CameraQueueItem? CurrentQueueItem
        {
            get => _currentQueueItem;
            set
            {
                _currentQueueItem = value;
                OnPropertyChanged();
            }
        }

        public int QueueProgress
        {
            get => _queueProgress;
            set
            {
                _queueProgress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QueueProgressText));
            }
        }

        public int QueueTotal
        {
            get => _queueTotal;
            set
            {
                _queueTotal = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QueueProgressText));
            }
        }

        public string QueueProgressText => QueueTotal > 0 ? $"{QueueProgress}/{QueueTotal}" : "0/0";

        public bool CanProcessQueue => QueueItems.Any(item => item.Status == "Queued") && !IsProcessingQueue && !string.IsNullOrEmpty(ImageFilePath);

        public bool CanAddToQueue => !string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath) && !string.IsNullOrEmpty(CustomPrompt);

        public int QueuedCount => QueueItems.Count(item => item.Status == "Queued");

        public int CompletedCount => QueueItems.Count(item => item.Status == "Completed");

        public int FailedCount => QueueItems.Count(item => item.Status == "Failed");

        // Sampler Settings

        public string SamplerName
        {
            get => _samplerName;
            set
            {
                _samplerName = value;
                OnPropertyChanged();
            }
        }

        public string Scheduler
        {
            get => _scheduler;
            set
            {
                _scheduler = value;
                OnPropertyChanged();
            }
        }

        
        public bool CanProcess => !string.IsNullOrEmpty(ImageFilePath) &&
                                  File.Exists(ImageFilePath) &&
                                  !IsProcessing;

        public new bool CanSavePrompt => !string.IsNullOrEmpty(CustomPrompt);

        public new bool CanDeletePrompt
        {
            get
            {
                var selected = CameraControlOptions.FirstOrDefault(x => x.Name == SelectedCameraControl);
                return selected != null && selected.Name != "Custom" && selected.Description == "User saved prompt";
            }
        }

        public bool IsSelectedPromptSaved
        {
            get
            {
                var selected = CameraControlOptions.FirstOrDefault(x => x.Name == SelectedCameraControl);
                return selected != null && selected.Description == "User saved prompt";
            }
        }

        // Commands
        public ICommand SelectImageCommand { get; }
        public ICommand ProcessImageCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SelectCameraControlCommand { get; }
        public ICommand SaveCustomPromptCommand { get; }
        public ICommand DeleteSavedPromptCommand { get; }
        public ICommand SendToVideoGeneratorCommand { get; }
        public ICommand NavigateToImageGeneratorCommand { get; }
        public ICommand NavigateToVideoGeneratorCommand { get; }
        public ICommand NavigateToStoryVideoCommand { get; }
        public ICommand AddToQueueCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }

        // Methods
        private void InitializeCameraControlOptions()
        {
            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Low Angle Shot",
                Icon = "📐",
                Description = "Ultra-low angle shot with exaggerated perspective",
                Prompt = "将镜头向下移动（Move the camera down.）, Rotate the angle of the photo to an ultra-low angle shot of the subject, with the camera's point of view positioned very close to the legs. The perspective should exaggerate the subject's height and create a sense of monumentality, prominently showcasing the details of the legs, thighs, while the rest of the figure dramatically rises towards up, foreshortened but visible. the legs are a focal point of the image, enhanced by the perspective. Important, keep the subject's id, clothes, facial features, pose, and hairstyle identical. Ensure that other elements in the background also change to complement the subject's new imposing presence. Ensure that the lighting and overall composition reinforce this effect of grandeur and power within the new setting.\nMaintain the original body type and soft figure"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "High Angle / Bird's Eye View",
                Icon = "🦅",
                Description = "Top-down view from high above",
                Prompt = "将镜头转为俯视（Turn the camera to a top-down view, Rotate the angle of the photo to an ultra-high angle shot (bird's eye view) of the subject, with the camera's point of view positioned far above and looking directly down."
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Rotate Right 90°",
                Icon = "↪️",
                Description = "Rotate camera 90 degrees to the right",
                Prompt = "将镜头向右旋转90度（Rotate the camera 90 degrees to the right.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Rotate Right 90° + Wide Hips",
                Icon = "🎯",
                Description = "Rotate right with emphasis on proportions",
                Prompt = "将镜头向右旋转90度 ,wide hips and legs, b slim upper body, looking away from the camera"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Ultra Low Angle + Wide Lens",
                Icon = "📷",
                Description = "Extreme low angle with wide lens perspective",
                Prompt = "将镜头向下移动, ultra low angle shot, exaggerated perspective, 将镜头转为广角镜头（wide lens)"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Move Forward",
                Icon = "⬆️",
                Description = "Move the camera forward",
                Prompt = "将镜头向前移动（Move the camera forward.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Move Left",
                Icon = "⬅️",
                Description = "Move the camera left",
                Prompt = "将镜头向左移动（Move the camera left.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Move Right",
                Icon = "➡️",
                Description = "Move the camera right",
                Prompt = "将镜头向右移动（Move the camera right.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Move Down",
                Icon = "⬇️",
                Description = "Move the camera down",
                Prompt = "将镜头向下移动（Move the camera down.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Rotate Left 45°",
                Icon = "↩️",
                Description = "Rotate the camera 45 degrees to the left",
                Prompt = "将镜头向左旋转45度（Rotate the camera 45 degrees to the left.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Rotate Right 45°",
                Icon = "↪️",
                Description = "Rotate the camera 45 degrees to the right",
                Prompt = "将镜头向右旋转45度（Rotate the camera 45 degrees to the right.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Top-Down View",
                Icon = "🔽",
                Description = "Turn the camera to a top-down view",
                Prompt = "将镜头转为俯视（Turn the camera to a top-down view.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Wide-Angle Lens",
                Icon = "📐",
                Description = "Turn the camera to a wide-angle lens",
                Prompt = "将镜头转为广角镜头（Turn the camera to a wide-angle lens.）"
            });

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Close-Up",
                Icon = "🔍",
                Description = "Turn the camera to a close-up",
                Prompt = "将镜头转为特写镜头（Turn the camera to a close-up.）"
            });

            // Load saved prompts from settings
            LoadSavedPrompts();

            CameraControlOptions.Add(new CameraControlOption
            {
                Name = "Custom",
                Icon = "✏️",
                Description = "Enter your own camera control prompt",
                Prompt = ""
            });

            // Initialize the default prompt based on the selected camera control
            UpdateCustomPromptFromSelection();
        }

        protected new void LoadSavedPrompts()
        {
            var savedPrompts = _settingsService.Settings.SavedCameraPrompts;
            if (savedPrompts != null && savedPrompts.Any())
            {
                foreach (var saved in savedPrompts)
                {
                    CameraControlOptions.Add(new CameraControlOption
                    {
                        Name = saved.Name,
                        Icon = saved.Icon,
                        Description = "User saved prompt",
                        Prompt = saved.Prompt
                    });
                }
            }
        }

        private void SelectCameraControl(string? controlName)
        {
            if (!string.IsNullOrEmpty(controlName))
            {
                SelectedCameraControl = controlName;
            }
        }

        private void UpdateCustomPromptFromSelection()
        {
            var selected = CameraControlOptions.FirstOrDefault(x => x.Name == SelectedCameraControl);
            if (selected != null && selected.Name != "Custom")
            {
                CustomPrompt = selected.Prompt;
            }
        }

        private async void SelectImage()
        {
            var filePath = await _fileDialogService.OpenFileDialogAsync(
                "Select Input Image",
                "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                persistKey: "flippix.image");

            if (filePath != null)
            {
                ImageFilePath = filePath;
                AddLog($"Selected image: {Path.GetFileName(ImageFilePath)}");
            }
        }

        public void SetImagePath(string imagePath)
        {
            AddLog($"SetImagePath called with: '{imagePath}', FileExists: {File.Exists(imagePath)}");

            if (File.Exists(imagePath))
            {
                ImageFilePath = imagePath;
                AddLog($"Image loaded from image generator: {Path.GetFileName(ImageFilePath)}");

                // Force refresh UI
                OnPropertyChanged(nameof(ImagePreviewSource));
                OnPropertyChanged(nameof(ImageFilePath));
                OnPropertyChanged(nameof(ImageInfo));
            }
            else
            {
                AddLog($"SetImagePath failed - file does not exist: {imagePath}");
            }
        }

        private void CancelProcessing()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested by user");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        private void LoadImagePreview()
        {
            AddLog($"LoadImagePreview called: ImageFilePath='{ImageFilePath}', FileExists={(!string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath))}");

            if (!string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath))
            {
                try
                {
                    AddLog($"Loading image from: {ImageFilePath}");

                    // Read file into memory first to avoid file locking and caching issues
                    byte[] imageBytes = File.ReadAllBytes(ImageFilePath);

                    var bitmap = new BitmapImage();
                    using (var memoryStream = new MemoryStream(imageBytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memoryStream;
                        bitmap.EndInit();
                    }
                    bitmap.Freeze();

                    // Set the image source directly (we're already on UI thread from command)
                    ImagePreviewSource = bitmap;
                    ImageInfo = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} pixels";

                    AddLog($"Image loaded successfully: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading image preview: {ex.Message}");
                    _logger.LogError($"Error loading image preview from {ImageFilePath}: {ex}");
                    ImageInfo = "Error loading image";
                    ImagePreviewSource = null;
                }
            }
            else
            {
                AddLog($"Cannot load image preview - ImageFilePath is empty or file does not exist");
                ImagePreviewSource = null;
                ImageInfo = string.Empty;
            }
        }

        private async Task ProcessImageAsync()
        {
            if (!CanProcess) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                AddLog("=== Starting new image processing ===");
                IsProcessing = true;

                // Clear previous result and force cleanup
                HasResultImage = false;
                ResultPreviewSource = null;

                // Give GC a chance to cleanup previous resources
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProcessingProgress = 0;
                ProcessingStatus = "Preparing workflow...";
                AddLog($"Processing image: {Path.GetFileName(ImageFilePath)}");
                AddLog($"Camera control: {SelectedCameraControl}");

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

                // Load workflow
                var workflowPath = WorkflowLocator.Resolve("workflow", "qwen-edit-camera-API.json");
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

                // Upload input image
                ProcessingStatus = "Uploading input image...";
                ProcessingProgress = 20;
                AddLog("Uploading input image to ComfyUI...");

                var uploadedImageName = await _comfyUIService.UploadImageAsync(ImageFilePath);
                AddLog($"Image uploaded: {uploadedImageName}");

                // Update workflow parameters
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, CustomPrompt);

                // Execute workflow
                ProcessingStatus = "Processing image...";
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
                            ProcessingStatus = $"Processing: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
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

                // Retry image retrieval with delays to give ComfyUI time to write the file
                List<byte[]> outputImages = new();
                int retryCount = 0;
                int maxRetries = 20; // Increased from 5 to 20 retries

                while (retryCount < maxRetries && !outputImages.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000, _cancellationTokenSource.Token); // Increased from 2s to 5s delay
                    }

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    outputImages = await GetOutputImagesFromComfyUI(promptId);
                    retryCount++;
                }

                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "camera-control");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"camera_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    await LocalCopyService.CopyImageAsync(outputPath);
                    AddLog($"Output saved: {outputPath}");

                    ResultImagePath = outputPath;
                    LoadResultPreview(outputPath);
                    HasResultImage = true;

                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Processing complete - {Path.GetFileName(outputPath)}";
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
                AddLog("Image processing cancelled by user");
                ProcessingStatus = "Cancelled";
                ProcessingProgress = 0;
                StatusBarMessage = "Processing cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                {
                    AddLog($"Inner Exception: {ex.InnerException.Message}");
                }
                AddLog($"Stack Trace: {ex.StackTrace}");

                _logger.LogError($"Error processing image: {ex}");

                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error processing image:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Image processing ended ===");
            }
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Update image input (node 78)
            if (workflowDict.ContainsKey("78"))
            {
                var node78 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["78"].GetRawText());
                if (node78 != null && node78.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node78["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = inputImageName;
                        node78["inputs"] = inputs;
                        workflowDict["78"] = JsonSerializer.SerializeToElement(node78);
                    }
                }
            }

            // Update positive prompt (node 141)
            if (workflowDict.ContainsKey("141"))
            {
                var node141 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["141"].GetRawText());
                if (node141 != null && node141.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node141["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text1"] = promptText;
                        node141["inputs"] = inputs;
                        workflowDict["141"] = JsonSerializer.SerializeToElement(node141);
                    }
                }
            }

            // Update negative prompt (node 110)
            if (workflowDict.ContainsKey("110"))
            {
                var node110 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["110"].GetRawText());
                if (node110 != null && node110.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node110["inputs"]));
                    if (inputs != null)
                    {
                        inputs["prompt"] = NegativePrompt;
                        node110["inputs"] = inputs;
                        workflowDict["110"] = JsonSerializer.SerializeToElement(node110);
                    }
                }
            }

            // Update sampler settings (node 3)
            if (workflowDict.ContainsKey("3"))
            {
                var node3 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["3"].GetRawText());
                if (node3 != null && node3.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node3["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        inputs["sampler_name"] = SamplerName;
                        inputs["scheduler"] = Scheduler;
                        inputs["denoise"] = Denoise;
                        node3["inputs"] = inputs;
                        workflowDict["3"] = JsonSerializer.SerializeToElement(node3);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void LoadResultPreview(string imagePath)
        {
            AddLog($"LoadResultPreview called: imagePath='{imagePath}', FileExists={(!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))}");

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    AddLog($"Loading result image from: {imagePath}");

                    // Read file into memory first to avoid file locking and caching issues
                    byte[] imageBytes = File.ReadAllBytes(imagePath);

                    var bitmap = new BitmapImage();
                    using (var memoryStream = new MemoryStream(imageBytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memoryStream;
                        bitmap.EndInit();
                    }
                    bitmap.Freeze();

                    // Set on UI thread
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ResultPreviewSource = bitmap;
                    });

                    AddLog($"Result image loaded successfully: {bitmap.PixelWidth}x{bitmap.PixelHeight}");
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading result preview: {ex.Message}");
                    _logger.LogError($"Error loading result preview from {imagePath}: {ex}");
                    ResultPreviewSource = null;
                }
            }
            else
            {
                AddLog($"Cannot load result preview - imagePath is empty or file does not exist");
                ResultPreviewSource = null;
            }
        }

        private void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
            {
                Process.Start("explorer.exe", $"/select,\"{ResultImagePath}\"");
            }
        }

        private void SendToVideoGenerator()
        {
            if (string.IsNullOrEmpty(ResultImagePath) || !File.Exists(ResultImagePath))
            {
                AddLog("ERROR: No result image to send to video generator");
                return;
            }

            if (_serviceProvider == null)
            {
                System.Windows.MessageBox.Show("Cannot open Video Generator - service provider not available.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            try
            {
                AddLog($"Opening Video Generator with image: {Path.GetFileName(ResultImagePath)}");

                // Get the VideoGeneratorWindow from DI
                var videoGeneratorWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;

                if (videoGeneratorWindow != null)
                {
                    // Get the ViewModel and set the image path
                    if (videoGeneratorWindow.DataContext is VideoGeneratorViewModel viewModel)
                    {
                        viewModel.SetImagePath(ResultImagePath);
                    }

                    videoGeneratorWindow.Show();
                    AddLog("Video Generator window opened successfully");
                }
                else
                {
                    AddLog("ERROR: Failed to create Video Generator window");
                    System.Windows.MessageBox.Show("Failed to open Video Generator window.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening Video Generator: {ex.Message}");
                System.Windows.MessageBox.Show($"Error opening Video Generator:\n{ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void NavigateToImageGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var imageGeneratorWindow = _serviceProvider.GetService(typeof(ImageGeneratorWindow)) as ImageGeneratorWindow;
                if (imageGeneratorWindow != null)
                {
                    imageGeneratorWindow.WindowState = WindowState.Normal;

                    // Ensure the window opens on screen with title bar visible
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = imageGeneratorWindow.Width;
                    var windowHeight = imageGeneratorWindow.Height;

                    // Position with offset to ensure title bar is visible
                    imageGeneratorWindow.Left = Math.Max(100, (screenWidth - windowWidth) / 2 - 200);
                    imageGeneratorWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2 - 100);

                    // Ensure title bar is visible by keeping some margin from screen edges
                    if (imageGeneratorWindow.Top < 50) imageGeneratorWindow.Top = 50;
                    if (imageGeneratorWindow.Left < 50) imageGeneratorWindow.Left = 50;
                    if (imageGeneratorWindow.Top + windowHeight > screenHeight - 50)
                        imageGeneratorWindow.Top = screenHeight - windowHeight - 50;
                    if (imageGeneratorWindow.Left + windowWidth > screenWidth - 50)
                        imageGeneratorWindow.Left = screenWidth - windowWidth - 50;

                    imageGeneratorWindow.Show();
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Generator: {ex.Message}");
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
                    imageAnalyzerWindow.WindowState = WindowState.Normal;

                    // Ensure the window opens on screen with title bar visible
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = imageAnalyzerWindow.Width;
                    var windowHeight = imageAnalyzerWindow.Height;

                    // Use conservative positioning
                    imageAnalyzerWindow.Left = 100;
                    imageAnalyzerWindow.Top = 100;

                    // Ensure window is fully visible on screen
                    if (imageAnalyzerWindow.Left + windowWidth > screenWidth)
                        imageAnalyzerWindow.Left = Math.Max(25, screenWidth - windowWidth - 25);
                    if (imageAnalyzerWindow.Top + windowHeight > screenHeight)
                        imageAnalyzerWindow.Top = Math.Max(25, screenHeight - windowHeight - 25);

                    imageAnalyzerWindow.Show();
                    AddLog("Opened Image Analyzer window");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Image Analyzer: {ex.Message}");
            }
        }

        private void NavigateToVideoGenerator()
        {
            if (_serviceProvider == null)
            {
                AddLog("ERROR: Service provider is null");
                return;
            }

            try
            {
                AddLog("Attempting to get VideoGeneratorWindow from service provider...");
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;

                if (videoWindow != null)
                {
                    AddLog("VideoGeneratorWindow created successfully");

                    // If window is already open, bring it to front
                    if (videoWindow.IsVisible)
                    {
                        videoWindow.WindowState = WindowState.Normal;
                        videoWindow.Activate();
                        videoWindow.Focus();
                        AddLog("Video Generator window already open - bringing to front");
                        return;
                    }

                    videoWindow.WindowState = WindowState.Normal;

                    // Ensure the window opens on screen with title bar visible
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = videoWindow.Width;
                    var windowHeight = videoWindow.Height;

                    // Position with offset to ensure title bar is visible
                    videoWindow.Left = Math.Max(100, (screenWidth - windowWidth) / 2 + 200);
                    videoWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2 + 100);

                    // Ensure title bar is visible by keeping some margin from screen edges
                    if (videoWindow.Top < 50) videoWindow.Top = 50;
                    if (videoWindow.Left < 50) videoWindow.Left = 50;
                    if (videoWindow.Top + windowHeight > screenHeight - 50)
                        videoWindow.Top = screenHeight - windowHeight - 50;
                    if (videoWindow.Left + windowWidth > screenWidth - 50)
                        videoWindow.Left = screenWidth - windowWidth - 50;

                    AddLog("Calling Show() on VideoGeneratorWindow...");
                    videoWindow.Show();
                    AddLog("Calling Activate() on VideoGeneratorWindow...");
                    videoWindow.Activate();
                    AddLog("Video Generator window opened successfully");
                }
                else
                {
                    AddLog("ERROR: Could not create Video Generator Window - GetService returned null");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Video Generator: {ex.Message}");
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
                    storyVideoWindow.WindowState = WindowState.Normal;

                    // Ensure the window opens on screen with title bar visible
                    var screenWidth = SystemParameters.PrimaryScreenWidth;
                    var screenHeight = SystemParameters.PrimaryScreenHeight;
                    var windowWidth = storyVideoWindow.Width;
                    var windowHeight = storyVideoWindow.Height;

                    // Position with offset to ensure title bar is visible
                    storyVideoWindow.Left = Math.Max(100, (screenWidth - windowWidth) / 2);
                    storyVideoWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2);

                    // Ensure title bar is visible by keeping some margin from screen edges
                    if (storyVideoWindow.Top < 50) storyVideoWindow.Top = 50;
                    if (storyVideoWindow.Left < 50) storyVideoWindow.Left = 50;
                    if (storyVideoWindow.Top + windowHeight > screenHeight - 50)
                        storyVideoWindow.Top = screenHeight - windowHeight - 50;
                    if (storyVideoWindow.Left + windowWidth > screenWidth - 50)
                        storyVideoWindow.Left = screenWidth - windowWidth - 50;

                    storyVideoWindow.Show();
                    AddLog("Opened Story Video window");
                }
                else
                {
                    AddLog("ERROR: Could not create Story Video Window - missing dependencies");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Story Video: {ex.Message}");
            }
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

                // First try to get output files specifically for this prompt ID
                AddLog($"Checking ComfyUI history API for prompt: {promptId}");
                var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                AddLog($"Found {outputFiles.Count} output files for this prompt");

                // Debug: show what files were found
                if (outputFiles.Any())
                {
                    AddLog("Files found for this prompt:");
                    foreach (var file in outputFiles)
                    {
                        AddLog($"  - {file}");
                    }
                }

                // Look for image files
                var imageFiles = outputFiles.Where(f =>
                    f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg"))
                    .ToList();

                if (imageFiles.Any())
                {
                    // Get the last file (should be the output from this prompt)
                    var filename = imageFiles.Last();
                    AddLog($"Attempting to download: {filename}");

                    var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                    if (imageData != null)
                    {
                        images.Add(imageData);
                        AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                        return images;
                    }
                    else
                    {
                        AddLog($"Failed to download via API, trying local file access...");
                    }
                }

                // If API download failed or no files found, try local file system
                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                AddLog($"Configured output folder: {comfyUIOutputDir}");

                // Build list of directories to check
                var dirsToCheck = new List<string>();

                if (!string.IsNullOrEmpty(comfyUIOutputDir) && Directory.Exists(comfyUIOutputDir))
                {
                    dirsToCheck.Add(comfyUIOutputDir);
                }

                // Also check common alternative locations
                var driveLetters = new[] { "Y:", "C:", "D:", "E:" };
                foreach (var drive in driveLetters)
                {
                    var altPath = Path.Combine(drive, "output");
                    if (Directory.Exists(altPath) && !dirsToCheck.Contains(altPath))
                    {
                        dirsToCheck.Add(altPath);
                    }
                }

                if (!dirsToCheck.Any())
                {
                    AddLog("ERROR: No valid output directories found");
                    return images;
                }

                AddLog($"Searching for output images in {dirsToCheck.Count} directories...");

                // Look for recently created images (png, jpg, jpeg) within the last 2 minutes
                var imageExtensions = new[] { "*.png", "*.jpg", "*.jpeg" };
                var allRecentFiles = new List<FileInfo>();

                foreach (var dir in dirsToCheck)
                {
                    AddLog($"Checking: {dir}");
                    foreach (var extension in imageExtensions)
                    {
                        try
                        {
                            var files = Directory.GetFiles(dir, extension)
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2);
                            allRecentFiles.AddRange(files);
                        }
                        catch (Exception ex)
                        {
                            AddLog($"  Error accessing {dir}: {ex.Message}");
                        }
                    }
                }

                // Prioritize Qwen files for camera edit
                var recentFiles = allRecentFiles
                    .OrderByDescending(f => f.Name.Contains("Qwen") ? 1 : 0)
                    .ThenByDescending(f => f.LastWriteTime)
                    .ToList();

                AddLog($"Found {recentFiles.Count} recent image files");

                if (recentFiles.Any())
                {
                    var latestFile = recentFiles.First();
                    AddLog($"Using file: {latestFile.FullName} (modified: {latestFile.LastWriteTime})");
                    images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                }
                else
                {
                    AddLog("WARNING: No recent output images found in any location");
                    AddLog("Please check that ComfyUI completed successfully and saved the output");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output images: {ex.Message}");
            }

            return images;
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

        private void SaveCustomPrompt()
        {
            if (string.IsNullOrWhiteSpace(CustomPrompt))
            {
                System.Windows.MessageBox.Show("Please enter a prompt before saving.", "Save Prompt",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            // Use a simpler input dialog approach
            var promptName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter a name for this camera prompt:",
                "Save Camera Prompt",
                "My Custom Prompt");

            if (string.IsNullOrWhiteSpace(promptName))
            {
                return;
            }

            // Check if name already exists
            var existingPrompt = _settingsService.Settings.SavedCameraPrompts
                .FirstOrDefault(p => p.Name.Equals(promptName, StringComparison.OrdinalIgnoreCase));

            if (existingPrompt != null)
            {
                var result = System.Windows.MessageBox.Show(
                    $"A prompt with the name '{promptName}' already exists. Do you want to replace it?",
                    "Replace Prompt",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (result == System.Windows.MessageBoxResult.No)
                {
                    return;
                }

                // Remove the old one
                _settingsService.Settings.SavedCameraPrompts.Remove(existingPrompt);
            }

            // Add new saved prompt
            var newPrompt = new FlipPix.Core.Models.SavedCameraPrompt
            {
                Name = promptName,
                Prompt = CustomPrompt,
                Icon = "💾"
            };

            _settingsService.Settings.SavedCameraPrompts.Add(newPrompt);
            _settingsService.SaveSettings(_settingsService.Settings);

            // Add to dropdown (before "Custom" option)
            var customIndex = CameraControlOptions.Count - 1; // "Custom" is last
            CameraControlOptions.Insert(customIndex, new CameraControlOption
            {
                Name = newPrompt.Name,
                Icon = newPrompt.Icon,
                Description = "User saved prompt",
                Prompt = newPrompt.Prompt
            });

            AddLog($"Saved camera prompt: {promptName}");
            System.Windows.MessageBox.Show($"Prompt '{promptName}' saved successfully!", "Save Prompt",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private void DeleteSavedPrompt()
        {
            var selected = CameraControlOptions.FirstOrDefault(x => x.Name == SelectedCameraControl);
            if (selected == null || selected.Description != "User saved prompt")
            {
                System.Windows.MessageBox.Show("Please select a saved prompt to delete.", "Delete Prompt",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete the prompt '{selected.Name}'?",
                "Delete Prompt",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                // Remove from settings
                var savedPrompt = _settingsService.Settings.SavedCameraPrompts
                    .FirstOrDefault(p => p.Name == selected.Name);
                if (savedPrompt != null)
                {
                    _settingsService.Settings.SavedCameraPrompts.Remove(savedPrompt);
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                // Remove from dropdown
                CameraControlOptions.Remove(selected);

                // Select "Custom" option
                SelectedCameraControl = "Custom";

                AddLog($"Deleted camera prompt: {selected.Name}");
                System.Windows.MessageBox.Show($"Prompt '{selected.Name}' deleted successfully!", "Delete Prompt",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        // Implementation of abstract base class properties
        public override string CurrentPromptText => CustomPrompt;

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
            CustomPrompt = savedPrompt.Prompt;
            AspectRatioIndex = savedPrompt.AspectRatioIndex;
            Steps = savedPrompt.Steps;
            Cfg = savedPrompt.Cfg;
            Seed = savedPrompt.Seed;
            Denoise = savedPrompt.Denoise;

            // Load additional data if present
            if (savedPrompt.AdditionalData != null)
            {
                LoadAdditionalPromptData(savedPrompt.AdditionalData);
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
            return new Dictionary<string, object>
            {
                ["NegativePrompt"] = NegativePrompt,
                ["SamplerName"] = SamplerName,
                ["Scheduler"] = Scheduler,
                ["SelectedCameraControl"] = SelectedCameraControl
            };
        }

        public override void LoadAdditionalPromptData(Dictionary<string, object> data)
        {
            if (data.TryGetValue("NegativePrompt", out var negPrompt) && negPrompt is string neg)
                NegativePrompt = neg;

            if (data.TryGetValue("SamplerName", out var sampler) && sampler is string sam)
                SamplerName = sam;

            if (data.TryGetValue("Scheduler", out var scheduler) && scheduler is string sch)
                Scheduler = sch;

            if (data.TryGetValue("SelectedCameraControl", out var control) && control is string ctrl)
                SelectedCameraControl = ctrl;
        }

        // Queue Methods
        private void AddToQueue()
        {
            if (!CanAddToQueue) return;

            var queueItem = new CameraQueueItem
            {
                ImageFilePath = ImageFilePath,
                CameraControl = SelectedCameraControl,
                Prompt = CustomPrompt,
                NegativePrompt = NegativePrompt,
                Steps = Steps,
                Cfg = Cfg,
                Denoise = Denoise,
                SamplerName = SamplerName,
                Scheduler = Scheduler,
                Status = "Queued"
            };

            QueueItems.Add(queueItem);
            AddLog($"Added to queue: {SelectedCameraControl} - {Path.GetFileName(ImageFilePath)}");
            StatusBarMessage = $"Item added to queue ({QueuedCount} queued)";
            CommandManager.InvalidateRequerySuggested();

            // Auto-start queue processing if not already processing
            if (!IsProcessingQueue && QueueItems.Any(q => q.Status == "Queued"))
            {
                _ = ProcessQueueAsync();
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (!CanProcessQueue) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsProcessingQueue = true;
                QueueTotal = QueueItems.Count(q => q.Status == "Queued");
                QueueProgress = 0;

                AddLog($"=== Starting queue processing ({QueueTotal} items) ===");

                CameraQueueItem? item;
                while ((item = QueueItems.FirstOrDefault(q => q.Status == "Queued")) != null)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        AddLog("Queue processing cancelled");
                        break;
                    }

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    AddLog($"Processing queue item {QueueProgress + 1}/{QueueTotal}: {item.CameraControl}");

                    try
                    {
                        // Process the current queue item
                        await ProcessQueueItemAsync(item, _cancellationTokenSource.Token);
                        item.Status = "Completed";
                        item.CompletedAt = DateTime.Now;
                        item.Progress = 100;
                        AddLog($"Completed queue item: {item.CameraControl}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "Cancelled";
                        item.ErrorMessage = "Cancelled by user";
                        AddLog($"Queue item cancelled: {item.CameraControl}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        item.Progress = 0;
                        AddLog($"Queue item failed: {item.CameraControl} - {ex.Message}");
                        _logger.LogError($"Error processing queue item {item.Id}: {ex}");
                    }
                    finally
                    {
                        QueueProgress++;
                    }
                }

                AddLog($"=== Queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");
                StatusBarMessage = $"Queue processing completed - {CompletedCount} successful, {FailedCount} failed";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Queue processing failed: {ex.Message}");
                _logger.LogError($"Error processing queue: {ex}");
                StatusBarMessage = "Queue processing failed";
            }
            finally
            {
                IsProcessingQueue = false;
                CurrentQueueItem = null;
                QueueProgress = 0;
                QueueTotal = 0;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task ProcessQueueItemAsync(CameraQueueItem item, System.Threading.CancellationToken cancellationToken)
        {
            // Ensure ComfyUI is connected
            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Load workflow
            var workflowPath = WorkflowLocator.Resolve("workflow", "qwen-edit-camera-API.json");
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            // Upload input image
            var uploadedImageName = await _comfyUIService.UploadImageAsync(item.ImageFilePath);

            // Update workflow parameters for this queue item
            var originalPrompt = CustomPrompt;
            var originalNegativePrompt = NegativePrompt;
            var originalSteps = Steps;
            var originalCfg = Cfg;
            var originalDenoise = Denoise;
            var originalSamplerName = SamplerName;
            var originalScheduler = Scheduler;

            // Temporarily set the ViewModel properties to match the queue item
            CustomPrompt = item.Prompt;
            NegativePrompt = item.NegativePrompt;
            Steps = item.Steps;
            Cfg = item.Cfg;
            Denoise = item.Denoise;
            SamplerName = item.SamplerName;
            Scheduler = item.Scheduler;

            try
            {
                var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, item.Prompt);

                // Execute workflow with progress reporting
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            item.Progress = percent;
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

                // Get output images
                var outputImages = await GetOutputImagesFromComfyUI(promptId);
                if (outputImages.Any())
                {
                    var outputImage = outputImages.First();
                    var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "camera-control");
                    Directory.CreateDirectory(outputDir);

                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var outputPath = Path.Combine(outputDir, $"camera_queue_{item.Id}_{timestamp}.png");

                    await File.WriteAllBytesAsync(outputPath, outputImage);
                    await LocalCopyService.CopyImageAsync(outputPath);
                    item.ResultImagePath = outputPath;

                    // If this is the current item, also update the main result
                    if (CurrentQueueItem?.Id == item.Id)
                    {
                        ResultImagePath = outputPath;
                        LoadResultPreview(outputPath);
                        HasResultImage = true;
                    }
                }
                else
                {
                    throw new InvalidOperationException("No output images were generated");
                }
            }
            finally
            {
                // Restore original ViewModel properties
                CustomPrompt = originalPrompt;
                NegativePrompt = originalNegativePrompt;
                Steps = originalSteps;
                Cfg = originalCfg;
                Denoise = originalDenoise;
                SamplerName = originalSamplerName;
                Scheduler = originalScheduler;
            }
        }

        private void ClearQueue()
        {
            if (!QueueItems.Any()) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to clear all {QueueItems.Count} items from the queue?",
                "Clear Queue",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                QueueItems.Clear();
                AddLog("Queue cleared");
                StatusBarMessage = "Queue cleared";
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void RemoveFromQueue(CameraQueueItem? item)
        {
            if (item == null) return;

            if (item.Status == "Processing")
            {
                System.Windows.MessageBox.Show("Cannot remove an item that is currently being processed.", "Cannot Remove",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            QueueItems.Remove(item);
            AddLog($"Removed from queue: {item.CameraControl}");
            StatusBarMessage = $"Item removed from queue ({QueuedCount} queued)";
            CommandManager.InvalidateRequerySuggested();
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
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();

                // Clear collections
                QueueItems.Clear();
                CameraControlOptions.Clear();

                // Clear string properties
                _statusBarMessage = string.Empty;
                _logOutput = string.Empty;
                _processingStatus = string.Empty;

                _disposed = true;
            }
        }
    }

    public class CameraControlOption
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }

}
