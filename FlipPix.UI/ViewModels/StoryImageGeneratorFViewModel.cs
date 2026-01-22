using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public class StoryImageGeneratorFViewModel : INotifyPropertyChanged
    {
        // Image resolution constants
        private const string LandscapeResolution = "1344x768";
        private const string PortraitResolution = "768x1344";

        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;

        private string _promptJsonFilePath = string.Empty;
        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImagePreview;
        private ObservableCollection<StoryPromptItem> _queueItems = new();
        private bool _isProcessingQueue = false;
        private StoryPromptItem? _currentQueueItem;
        private int _queueProgress = 0;
        private int _queueTotal = 0;
        private string _logOutput = string.Empty;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private bool _settingsVisible = false;

        // Generation settings
        private int _steps = 4;
        private double _cfg = 1.0;
        private double _denoise = 0.98;
        private string _negativePrompt = "";
        private bool _isPortraitMode = false; // Default to landscape mode


        public StoryImageGeneratorFViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            // Initialize commands
            SelectPromptJsonCommand = new RelayCommand(SelectPromptJson);
            SelectInputImageCommand = new RelayCommand(SelectInputImage);
            LoadPromptsCommand = new RelayCommand(async () => await LoadPromptsAsync(), () => CanLoadPrompts);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => QueueItems.Any());
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
            CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
            ToggleSettingsVisibilityCommand = new RelayCommand(ToggleSettingsVisibility);

            AddLog("Story Image Generator F initialized");
        }

        // Methods
        private void ToggleSettingsVisibility()
        {
            SettingsVisible = !SettingsVisible;
        }

        // Properties
        public string PromptJsonFilePath
        {
            get => _promptJsonFilePath;
            set
            {
                if (_promptJsonFilePath != value)
                {
                    _promptJsonFilePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string InputImagePath
        {
            get => _inputImagePath;
            set
            {
                if (_inputImagePath != value)
                {
                    _inputImagePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    LoadInputImagePreview();
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public BitmapImage? InputImagePreview
        {
            get => _inputImagePreview;
            set
            {
                if (_inputImagePreview != value)
                {
                    _inputImagePreview = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<StoryPromptItem> QueueItems
        {
            get => _queueItems;
            set
            {
                if (_queueItems != value)
                {
                    _queueItems = value;
                    OnPropertyChanged();
                }
            }
        }

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
                    OnPropertyChanged(nameof(CanLoadPrompts));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public StoryPromptItem? CurrentQueueItem
        {
            get => _currentQueueItem;
            set
            {
                if (_currentQueueItem != value)
                {
                    _currentQueueItem = value;
                    OnPropertyChanged();
                }
            }
        }

        public int QueueProgress
        {
            get => _queueProgress;
            set
            {
                if (_queueProgress != value)
                {
                    _queueProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(QueueProgressText));
                }
            }
        }

        public int QueueTotal
        {
            get => _queueTotal;
            set
            {
                if (_queueTotal != value)
                {
                    _queueTotal = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(QueueProgressText));
                }
            }
        }

        public string QueueProgressText => QueueTotal > 0 ? $"{QueueProgress}/{QueueTotal}" : "0/0";

        public bool CanLoadPrompts => !string.IsNullOrEmpty(PromptJsonFilePath) &&
                                      File.Exists(PromptJsonFilePath) &&
                                      !string.IsNullOrEmpty(InputImagePath) &&
                                      File.Exists(InputImagePath) &&
                                      !IsProcessingQueue;

        public bool CanProcessQueue => QueueItems.Any(item => item.Status == "Queued") &&
                                       !IsProcessingQueue;

        public int QueuedCount => QueueItems.Count(item => item.Status == "Queued");

        public int CompletedCount => QueueItems.Count(item => item.Status == "Completed");

        public int FailedCount => QueueItems.Count(item => item.Status == "Failed");

        public string LogOutput
        {
            get => _logOutput;
            set
            {
                if (_logOutput != value)
                {
                    _logOutput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set
            {
                if (_processingStatus != value)
                {
                    _processingStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set
            {
                if (_processingProgress != value)
                {
                    _processingProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProgressPercentage));
                }
            }
        }

        public string ProgressPercentage => $"{ProcessingProgress:F0}%";

        // Settings Properties
        public int Steps
        {
            get => _steps;
            set
            {
                if (_steps != value)
                {
                    _steps = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Cfg
        {
            get => _cfg;
            set
            {
                if (_cfg != value)
                {
                    _cfg = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Denoise
        {
            get => _denoise;
            set
            {
                if (_denoise != value)
                {
                    _denoise = value;
                    OnPropertyChanged();
                }
            }
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set
            {
                if (_negativePrompt != value)
                {
                    _negativePrompt = value;
                    OnPropertyChanged();
                }
            }
        }

        // Settings Visibility Property
        public bool SettingsVisible
        {
            get => _settingsVisible;
            set
            {
                if (_settingsVisible != value)
                {
                    _settingsVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        // Image Orientation Property
        public bool IsPortraitMode
        {
            get => _isPortraitMode;
            set
            {
                if (_isPortraitMode != value)
                {
                    _isPortraitMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OrientationText));
                }
            }
        }

        public string OrientationText => IsPortraitMode ? "Portrait (768x1344)" : "Landscape (1344x768)";

        // Commands
        public ICommand SelectPromptJsonCommand { get; }
        public ICommand SelectInputImageCommand { get; }
        public ICommand LoadPromptsCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand ToggleSettingsVisibilityCommand { get; }

        // Methods
        private void SelectPromptJson()
        {
            var initialDirectory = _settingsService.Settings?.StoryGeneratorPromptJsonFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts");
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Select Story Prompts JSON File",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                PromptJsonFilePath = dialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryGeneratorPromptJsonFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected prompt file: {Path.GetFileName(PromptJsonFilePath)}");
            }
        }

        private void SelectInputImage()
        {
            var initialDirectory = _settingsService.Settings?.StoryGeneratorInputImageFolder;

            if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Title = "Select Input Image",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                InputImagePath = dialog.FileName;

                // Save the folder location for next time
                var folderPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folderPath) && _settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryGeneratorInputImageFolder = folderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }

                AddLog($"Selected input image: {Path.GetFileName(InputImagePath)}");
            }
        }

        private void LoadInputImagePreview()
        {
            if (!string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(InputImagePath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    InputImagePreview = bitmap;
                }
                catch (Exception ex)
                {
                    AddLog($"Error loading image preview: {ex.Message}");
                    InputImagePreview = null;
                }
            }
            else
            {
                InputImagePreview = null;
            }
        }

        private async Task LoadPromptsAsync()
        {
            if (!CanLoadPrompts) return;

            try
            {
                AddLog("Loading prompts from JSON file...");
                var jsonContent = await File.ReadAllTextAsync(PromptJsonFilePath);
                var storyData = JsonSerializer.Deserialize<StoryPromptData>(jsonContent);

                if (storyData?.Prompts == null || !storyData.Prompts.Any())
                {
                    AddLog("ERROR: No prompts found in JSON file");
                    System.Windows.MessageBox.Show("No prompts found in the JSON file.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Clear existing queue
                QueueItems.Clear();

                // Create queue items for each prompt
                for (int i = 0; i < storyData.Prompts.Count; i++)
                {
                    QueueItems.Add(new StoryPromptItem
                    {
                        Index = i + 1,
                        Prompt = storyData.Prompts[i],
                        InputImagePath = InputImagePath, // All items use the same input image
                        Status = "Queued"
                    });
                }

                AddLog($"Loaded {QueueItems.Count} prompts into queue");
                System.Windows.MessageBox.Show($"Successfully loaded {QueueItems.Count} prompts into the queue.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
                _logger.LogError($"Error loading prompts: {ex}");
                System.Windows.MessageBox.Show($"Error loading prompts:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (!CanProcessQueue) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new System.Threading.CancellationTokenSource();

            try
            {
                IsProcessingQueue = true;
                var queuedItems = QueueItems.Where(item => item.Status == "Queued").ToList();
                QueueTotal = queuedItems.Count;
                QueueProgress = 0;

                // Create output folder for this queue processing session (named after JSON file)
                var jsonFileName = Path.GetFileNameWithoutExtension(PromptJsonFilePath);
                var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator-f");
                Directory.CreateDirectory(baseOutputDir);

                // Create sequential folder if it already exists
                var sessionOutputDir = GetUniqueFolderPath(baseOutputDir, jsonFileName);
                Directory.CreateDirectory(sessionOutputDir);

                AddLog($"=== Starting story queue processing ({QueueTotal} images) ===");
                AddLog($"Output folder: {sessionOutputDir}");

                foreach (var item in queuedItems)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        AddLog("Queue processing cancelled");
                        break;
                    }

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    item.InputImagePath = InputImagePath; // Always use the original input image

                    AddLog($"Processing story image {QueueProgress + 1}/{QueueTotal}");

                    try
                    {
                        // Process the current queue item using the original input image and shared output folder
                        var outputPath = await ProcessQueueItemAsync(item, InputImagePath, sessionOutputDir, jsonFileName, _cancellationTokenSource.Token);
                        item.OutputImagePath = outputPath;
                        item.Status = "Completed";
                        item.CompletedAt = DateTime.Now;
                        item.Progress = 100;

                        AddLog($"Completed story image {QueueProgress + 1}/{QueueTotal}: {Path.GetFileName(outputPath)}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "Cancelled";
                        item.ErrorMessage = "Cancelled by user";
                        AddLog($"Queue item cancelled: Prompt #{item.Index}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        item.Progress = 0;
                        AddLog($"Queue item failed: Prompt #{item.Index} - {ex.Message}");
                        _logger.LogError($"Error processing queue item {item.Id}: {ex}");
                    }
                    finally
                    {
                        QueueProgress++;
                    }
                }

                AddLog($"=== Story queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR: Queue processing failed: {ex.Message}");
                _logger.LogError($"Error processing queue: {ex}");
                System.Windows.MessageBox.Show($"Queue processing failed:\n\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsProcessingQueue = false;
                CurrentQueueItem = null;
                QueueProgress = 0;
                QueueTotal = 0;
                OnPropertyChanged(nameof(CanLoadPrompts));
                OnPropertyChanged(nameof(CanProcessQueue));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task<string> ProcessQueueItemAsync(StoryPromptItem item, string inputImagePath, string outputDir, string jsonFileName, System.Threading.CancellationToken cancellationToken)
        {
            // Ensure ComfyUI is connected
            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Load workflow (workflow_Flux2_Klein_9bTESTClown.json)
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "workflow_Flux2_Klein_9bTESTClown.json");
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            // Upload input image
            var uploadedImageName = await _comfyUIService.UploadImageAsync(inputImagePath);

            // Update workflow parameters with image index for unique filenames
            var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, item.Prompt, item.Index, jsonFileName);

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
            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            // Extract the base folder name (without sequential suffix) for image naming
            var folderName = Path.GetFileName(outputDir);
            var baseFolderName = folderName.Contains(" (")
                ? folderName.Substring(0, folderName.LastIndexOf(" ("))
                : folderName;

            // Generate sequential filename: jsonfilename-1.png, jsonfilename-2.png, etc.
            var outputPath = Path.Combine(outputDir, $"{baseFolderName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            AddLog($"Story F image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // 1. Update the input image (node 148 - LoadImage)
            if (workflowDict.ContainsKey("148"))
            {
                var node148 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["148"].GetRawText());
                if (node148 != null && node148.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node148["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = inputImageName;
                        node148["inputs"] = inputs;
                        workflowDict["148"] = JsonSerializer.SerializeToElement(node148);
                    }
                }
            }

            // 2. Update the prompt (node 154 - TextEncodeEditAdvanced)
            if (workflowDict.ContainsKey("154"))
            {
                var node154 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["154"].GetRawText());
                if (node154 != null && node154.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node154["inputs"]));
                    if (inputs != null)
                    {
                        inputs["prompt"] = promptText;
                        node154["inputs"] = inputs;
                        workflowDict["154"] = JsonSerializer.SerializeToElement(node154);
                    }
                }
            }

            // 3. Update Flux2Scheduler steps (node 109)
            if (workflowDict.ContainsKey("109"))
            {
                var node109 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["109"].GetRawText());
                if (node109 != null && node109.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node109["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        node109["inputs"] = inputs;
                        workflowDict["109"] = JsonSerializer.SerializeToElement(node109);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // 1. Update the input image (node 148 - LoadImage)
            if (workflowDict.ContainsKey("148"))
            {
                var node148 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["148"].GetRawText());
                if (node148 != null && node148.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node148["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = inputImageName;
                        node148["inputs"] = inputs;
                        workflowDict["148"] = JsonSerializer.SerializeToElement(node148);
                    }
                }
            }

            // 2. Update the prompt (node 154 - TextEncodeEditAdvanced)
            if (workflowDict.ContainsKey("154"))
            {
                var node154 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["154"].GetRawText());
                if (node154 != null && node154.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node154["inputs"]));
                    if (inputs != null)
                    {
                        inputs["prompt"] = promptText;
                        node154["inputs"] = inputs;
                        workflowDict["154"] = JsonSerializer.SerializeToElement(node154);
                    }
                }
            }

            // 3. Update Flux2Scheduler steps (node 109)
            if (workflowDict.ContainsKey("109"))
            {
                var node109 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["109"].GetRawText());
                if (node109 != null && node109.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node109["inputs"]));
                    if (inputs != null)
                    {
                        inputs["steps"] = Steps;
                        node109["inputs"] = inputs;
                        workflowDict["109"] = JsonSerializer.SerializeToElement(node109);
                    }
                }
            }

            // 4. Update SaveImage node (node 157) - set filename prefix only (no subfolder)
            if (workflowDict.ContainsKey("157"))
            {
                var node157 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["157"].GetRawText());
                if (node157 != null && node157.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node157["inputs"]));
                    if (inputs != null)
                    {
                        inputs["filename_prefix"] = jsonFileName;
                        // Don't set subfolder - all images will be saved in the same output folder
                        node157["inputs"] = inputs;
                        workflowDict["157"] = JsonSerializer.SerializeToElement(node157);
                    }
                }
            }

            // 5. Update ImageScale node (node 115) - set resolution based on orientation
            if (workflowDict.ContainsKey("115"))
            {
                var node115 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["115"].GetRawText());
                if (node115 != null && node115.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node115["inputs"]));
                    if (inputs != null)
                    {
                        var newResolution = IsPortraitMode ? PortraitResolution : LandscapeResolution;
                        inputs["resolution"] = newResolution;
                        node115["inputs"] = inputs;
                        workflowDict["115"] = JsonSerializer.SerializeToElement(node115);
                        AddLog($"Image resolution set to: {newResolution} (Portrait Mode: {IsPortraitMode})");
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId, string jsonFileName, int imageIndex)
        {
            var images = new List<byte[]>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

                AddLog($"ComfyUI server: {actualServer}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                // Retry image retrieval with delays to give ComfyUI time to write the file
                int retryCount = 0;
                int maxRetries = 20; // Wait up to 100 seconds (20 retries × 5s)

                while (retryCount < maxRetries && !images.Any())
                {
                    if (retryCount > 0)
                    {
                        AddLog($"Retry {retryCount}/{maxRetries} - waiting 5 seconds before checking again...");
                        await Task.Delay(5000);
                    }

                    if (isRemoteComfyUI)
                    {
                        AddLog("Detected remote ComfyUI server, downloading generated image...");

                        var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                        AddLog($"Found {outputFiles.Count} potential output files");

                        // Look for specific filename pattern: jsonfilename-index_00001.png (no subfolder)
                        var expectedPattern = $"{jsonFileName}-{imageIndex}_";
                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            f.Contains(expectedPattern))
                            .ToList();

                        AddLog($"Looking for pattern: {expectedPattern}");

                        if (imageFiles.Any())
                        {
                            var filename = imageFiles.Last();
                            AddLog($"Downloading generated image: {filename}");

                            var imageData = await _comfyUIService.HttpClient.DownloadOutputImageAsync(filename);
                            if (imageData != null)
                            {
                                images.Add(imageData);
                                AddLog($"Successfully downloaded image ({imageData.Length} bytes)");
                            }
                        }
                        else
                        {
                            AddLog($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
                            var fallbackImage = await _comfyUIService.HttpClient.TryDownloadRecentOutputAsync(promptId);
                            if (fallbackImage != null)
                            {
                                images.Add(fallbackImage);
                                AddLog($"Successfully downloaded image via fallback method ({fallbackImage.Length} bytes)");
                            }
                        }
                    }
                    else
                    {
                        var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                        if (string.IsNullOrEmpty(comfyUIOutputDir))
                        {
                            AddLog("ERROR: ComfyUI output folder not configured");
                            return images;
                        }

                        if (!Directory.Exists(comfyUIOutputDir))
                        {
                            AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                            return images;
                        }

                        // Search directly in ComfyUI output directory (no subfolder)
                        AddLog($"Searching for images in: {comfyUIOutputDir}");

                        // Look for files matching the pattern: jsonfilename-index_00001.png
                        var pattern = $"{jsonFileName}-{imageIndex}_*.png";
                        var matchingFiles = Directory.GetFiles(comfyUIOutputDir, pattern)
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.LastWriteTime)
                            .ToList();

                        if (matchingFiles.Any())
                        {
                            var latestFile = matchingFiles.First();
                            AddLog($"Found matching file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                            images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                        }
                        else
                        {
                            AddLog($"No files found matching pattern: {pattern}");
                            // List all files in the output directory for debugging
                            var allFiles = Directory.GetFiles(comfyUIOutputDir, "*.png")
                                .Select(Path.GetFileName)
                                .ToList();
                            AddLog($"Files in output directory: {string.Join(", ", allFiles.Take(10))}");
                        }

                        if (!images.Any())
                        {
                            AddLog($"No images found in retry {retryCount + 1}");
                        }
                    }

                    retryCount++;
                }

                if (!images.Any())
                {
                    AddLog("WARNING: No output images received after all retries");
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
                if (serverAddress.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    serverAddress.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (System.Net.IPAddress.TryParse(serverAddress, out var ip))
                {
                    var bytes = ip.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                return !string.IsNullOrEmpty(serverAddress) && serverAddress != ".";
            }
            catch
            {
                return true;
            }
        }

        private void ClearQueue()
        {
            if (!QueueItems.Any()) return;

            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to clear all {QueueItems.Count} items from the queue?",
                "Clear Queue",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                QueueItems.Clear();
                AddLog("Queue cleared");
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OpenOutputFolder()
        {
            try
            {
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator-f");
                if (Directory.Exists(outputDir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = outputDir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    System.Windows.MessageBox.Show("Output folder does not exist yet. Generate some images first.", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening output folder: {ex.Message}");
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

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo(message);
        }

        private string GetUniqueFolderPath(string baseDir, string folderName)
        {
            var folderPath = Path.Combine(baseDir, folderName);

            // If the folder doesn't exist, return it as-is
            if (!Directory.Exists(folderPath))
            {
                return folderPath;
            }

            // If it exists, find the next available sequential number
            int counter = 2;
            string newFolderPath;
            do
            {
                newFolderPath = Path.Combine(baseDir, $"{folderName} ({counter})");
                counter++;
            } while (Directory.Exists(newFolderPath));

            return newFolderPath;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // RelayCommand class
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
    }
}
