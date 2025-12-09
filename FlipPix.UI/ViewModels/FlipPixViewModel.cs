using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;

namespace FlipPix.UI.ViewModels
{
    public class FlipPixViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;

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

        // Sampler settings
        private int _steps = 8;
        private double _cfg = 1.5;
        private string _samplerName = "euler";
        private string _scheduler = "beta57";
        private double _denoise = 1.0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public FlipPixViewModel(ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;

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
                _imagePreviewSource = value;
                OnPropertyChanged();
            }
        }

        public BitmapImage? ResultPreviewSource
        {
            get => _resultPreviewSource;
            set
            {
                _resultPreviewSource = value;
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
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string ResultImagePath
        {
            get => _resultImagePath;
            set
            {
                _resultImagePath = value;
                OnPropertyChanged();
            }
        }

        // Sampler Settings
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

        public double Denoise
        {
            get => _denoise;
            set
            {
                _denoise = value;
                OnPropertyChanged();
            }
        }

        public bool CanProcess => !string.IsNullOrEmpty(ImageFilePath) &&
                                  File.Exists(ImageFilePath) &&
                                  !IsProcessing;

        public bool CanSavePrompt => !string.IsNullOrEmpty(CustomPrompt);

        public bool CanDeletePrompt
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
        }

        private void LoadSavedPrompts()
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

        private void SelectImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Title = "Select Input Image"
            };

            if (dialog.ShowDialog() == true)
            {
                ImageFilePath = dialog.FileName;
                AddLog($"Selected image: {Path.GetFileName(ImageFilePath)}");
            }
        }

        public void SetImagePath(string imagePath)
        {
            if (File.Exists(imagePath))
            {
                ImageFilePath = imagePath;
                AddLog($"Image loaded from image generator: {Path.GetFileName(ImageFilePath)}");
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
            if (!string.IsNullOrEmpty(ImageFilePath) && File.Exists(ImageFilePath))
            {
                try
                {
                    // Load image from stream to avoid file locking
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;

                    // Load file into memory stream to avoid file locking
                    using (var fileStream = new FileStream(ImageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var memoryStream = new MemoryStream();
                        fileStream.CopyTo(memoryStream);
                        memoryStream.Position = 0;
                        bitmap.StreamSource = memoryStream;
                        bitmap.EndInit();
                    }

                    bitmap.Freeze();
                    ImagePreviewSource = bitmap;
                    ImageInfo = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} pixels";
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading image preview: {ex.Message}");
                    _logger.LogError($"Error loading image preview from {ImageFilePath}: {ex}");
                    ImageInfo = "Error loading image";
                }
            }
        }

        private async Task ProcessImageAsync()
        {
            if (!CanProcess) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

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
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "qwen-edit-camera-API.json");
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
            try
            {
                // Load image from stream to avoid file locking
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                // Load file into memory stream to avoid file locking
                using (var fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var memoryStream = new MemoryStream();
                    fileStream.CopyTo(memoryStream);
                    memoryStream.Position = 0;
                    bitmap.StreamSource = memoryStream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();
                ResultPreviewSource = bitmap;

                AddLog($"Result preview loaded successfully");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading result preview: {ex.Message}");
                _logger.LogError($"Error loading result preview from {imagePath}: {ex}");
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

        private void NavigateToVideoGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                var videoWindow = _serviceProvider.GetService(typeof(VideoGeneratorWindow)) as VideoGeneratorWindow;
                if (videoWindow != null)
                {
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

                    videoWindow.Show();
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR navigating to Video Generator: {ex.Message}");
            }
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
        {
            var images = new List<byte[]>();

            try
            {
                // Get the actual ComfyUI server settings
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";

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

                    // First try the history API approach
                    var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                    AddLog($"Found {outputFiles.Count} potential output files");

                    // Look for recent image files in the output
                    var imageFiles = outputFiles.Where(f =>
                        (f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".jpeg")) &&
                        !f.StartsWith("z-image_")) // Exclude z-image files (those are from image generator)
                        .ToList();

                    if (imageFiles.Any())
                    {
                        // Download the most recent file
                        var filename = imageFiles.Last(); // Get the last/most recent
                        AddLog($"Downloading generated image: {filename}");

                        var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                        if (imageData != null)
                        {
                            images.Add(imageData);
                            AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                        }
                        else
                        {
                            AddLog($"Failed to download image: {filename}");
                        }
                    }
                    else
                    {
                        AddLog("No suitable image files found in history, trying alternative approach...");

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
                        }
                    }

                    // Debug info about what files we found
                    if (outputFiles.Any())
                    {
                        AddLog("All files found in history:");
                        foreach (var file in outputFiles.Take(10))
                        {
                            AddLog($"  - {file}");
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

                    if (!Directory.Exists(comfyUIOutputDir))
                    {
                        AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                        AddLog("Please check the ComfyUI folder configuration in settings");
                        return images;
                    }

                    AddLog($"Searching for output images in: {comfyUIOutputDir}");

                    // Look for recently created images (png, jpg, jpeg) within the last 2 minutes
                    var imageExtensions = new[] { "*.png", "*.jpg", "*.jpeg" };
                    var allRecentFiles = new List<FileInfo>();

                    foreach (var extension in imageExtensions)
                    {
                        var files = Directory.GetFiles(comfyUIOutputDir, extension)
                            .Select(f => new FileInfo(f))
                            .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2);
                        allRecentFiles.AddRange(files);
                    }

                    var recentFiles = allRecentFiles
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    AddLog($"Found {recentFiles.Count} recent image files");

                    if (recentFiles.Any())
                    {
                        var latestFile = recentFiles.First();
                        AddLog($"Using latest file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                        images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                    }
                    else
                    {
                        AddLog("WARNING: No recent output images found");
                        AddLog("Please check that ComfyUI completed successfully and saved the output");
                    }
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

            // Ask for a name for the prompt
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Camera Prompt",
                Filter = "Name|*.name",
                FileName = "MyPrompt"
            };

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

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CameraControlOption
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;

        public void Execute(object? parameter) => _execute((T?)parameter);
    }
}
