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
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.ViewModels
{
    public class StoryImageGeneratorAmateurViewModel : INotifyPropertyChanged
    {
        private readonly FlipPix.ComfyUI.Services.ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;

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
        private bool _isQueuePaused = false;
        private readonly ManualResetEventSlim _pauseEvent = new(true);

        // Generation settings
        private int _steps = 9;
        private double _cfg = 1.0;
        private double _denoise = 0.5;
        private double _denoise2 = 0.3;
        private string _negativePrompt = "";

        // Character LoRA settings (optional)
        private ObservableCollection<string> _availableCharacterLoras = new();
        private string _selectedCharacterLora = string.Empty;
        private bool _characterLoraEnabled = false;
        private double _characterLoraStrength = 0.8;

        // Amateur LoRA is always enabled
        private const string AmateurLoraName = "amateur_photography_zimage_v1.safetensors";
        private const double AmateurLoraStrength1 = 0.4; // Node 105
        private const double AmateurLoraStrength2 = 0.9; // Node 752

        public StoryImageGeneratorAmateurViewModel(
            FlipPix.ComfyUI.Services.ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _workflowCoordinator = workflowCoordinator ?? throw new ArgumentNullException(nameof(workflowCoordinator));

            // Initialize commands
            SelectPromptJsonCommand = new RelayCommand(SelectPromptJson);
            SelectInputImageCommand = new RelayCommand(SelectInputImage);
            LoadPromptsCommand = new RelayCommand(async () => await LoadPromptsAsync(), () => CanLoadPrompts);
            ProcessQueueCommand = new RelayCommand(async () => await ProcessQueueAsync(), () => CanProcessQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => QueueItems.Any());
            OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
            CancelProcessingCommand = new RelayCommand(CancelProcessing, () => IsProcessing);
            RefreshLorasCommand = new RelayCommand(RefreshLoras);
            PauseQueueCommand = new RelayCommand(PauseQueue, () => IsProcessingQueue && !IsQueuePaused);
            ResumeQueueCommand = new RelayCommand(ResumeQueue, () => IsProcessingQueue && IsQueuePaused);

            // Load available character Loras
            LoadAvailableCharacterLoras();

            LoadQueueFromFile();

            AddLog("Story Image Generator Amateur initialized");
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

        public ICommand PauseQueueCommand { get; }
        public ICommand ResumeQueueCommand { get; }

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

        public string QueueProgressText => QueueItems.Count > 0 ? $"{CompletedCount}/{QueueItems.Count} ({QueuedCount} remaining)" : "0/0";

        public bool CanLoadPrompts => !string.IsNullOrEmpty(PromptJsonFilePath) &&
                                      File.Exists(PromptJsonFilePath) &&
                                      !string.IsNullOrEmpty(InputImagePath) &&
                                      File.Exists(InputImagePath);

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

        public double Denoise2
        {
            get => _denoise2;
            set
            {
                if (_denoise2 != value)
                {
                    _denoise2 = value;
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

        // Character LoRA Properties
        public ObservableCollection<string> AvailableCharacterLoras
        {
            get => _availableCharacterLoras;
            set
            {
                if (_availableCharacterLoras != value)
                {
                    _availableCharacterLoras = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SelectedCharacterLora
        {
            get => _selectedCharacterLora;
            set
            {
                if (_selectedCharacterLora != value)
                {
                    _selectedCharacterLora = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CharacterLoraEnabled
        {
            get => _characterLoraEnabled;
            set
            {
                if (_characterLoraEnabled != value)
                {
                    _characterLoraEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public double CharacterLoraStrength
        {
            get => _characterLoraStrength;
            set
            {
                if (_characterLoraStrength != value)
                {
                    _characterLoraStrength = value;
                    OnPropertyChanged();
                }
            }
        }

        // Commands
        public ICommand SelectPromptJsonCommand { get; }
        public ICommand SelectInputImageCommand { get; }
        public ICommand LoadPromptsCommand { get; }
        public ICommand ProcessQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }
        public ICommand CancelProcessingCommand { get; }
        public ICommand RefreshLorasCommand { get; }

        // Methods
        private void SelectPromptJson()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Select Story Prompts JSON File",
                InitialDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts")
            };

            if (dialog.ShowDialog() == true)
            {
                PromptJsonFilePath = dialog.FileName;
                AddLog($"Selected prompt file: {Path.GetFileName(PromptJsonFilePath)}");
            }
        }

        private void SelectInputImage()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All Files (*.*)|*.*",
                Title = "Select Input Image"
            };

            if (dialog.ShowDialog() == true)
            {
                InputImagePath = dialog.FileName;
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

                // Append to existing queue (calculate next index from existing items)
                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;

                // Create queue items for each prompt
                for (int i = 0; i < storyData.Prompts.Count; i++)
                {
                    QueueItems.Add(new StoryPromptItem
                    {
                        Index = startIndex + i,
                        Prompt = storyData.Prompts[i],
                        InputImagePath = InputImagePath,
                        Status = "Queued"
                    });
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                AddLog($"Added {storyData.Prompts.Count} prompts to queue (total: {QueueItems.Count})");
                System.Windows.MessageBox.Show($"Added {storyData.Prompts.Count} prompts to the queue.\nTotal queue items: {QueueItems.Count}", "Success",
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

            AddLog("Waiting for other workflows to finish...");

            try
            {
                await _workflowCoordinator.AcquireAsync("StoryImageAmateur", _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue processing cancelled while waiting");
                return;
            }

            try
            {
                IsProcessingQueue = true;
                QueueTotal = QueueItems.Count;
                QueueProgress = 0;

                AddLog($"=== Starting story queue processing ({QueuedCount} images) ===");

                while (true)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        AddLog("Queue processing cancelled");
                        break;
                    }

                    // Wait if paused
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    var item = QueueItems.FirstOrDefault(i => i.Status == "Queued");
                    if (item == null) break;

                    QueueTotal = QueueItems.Count;

                    CurrentQueueItem = item;
                    item.Status = "Processing";
                    item.StartedAt = DateTime.Now;
                    item.InputImagePath = InputImagePath;
                    SaveQueueToFile();

                    AddLog($"Processing story image {QueueProgress + 1}/{QueueTotal}");

                    try
                    {
                        var outputPath = await ProcessQueueItemAsync(item, InputImagePath, _cancellationTokenSource.Token);
                        item.OutputImagePath = outputPath;
                        item.Status = "Completed";
                        item.CompletedAt = DateTime.Now;
                        item.Progress = 100;
                        SaveQueueToFile();

                        AddLog($"Completed story image {QueueProgress + 1}/{QueueTotal}: {Path.GetFileName(outputPath)}");
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "Cancelled";
                        item.ErrorMessage = "Cancelled by user";
                        SaveQueueToFile();
                        AddLog($"Queue item cancelled: Prompt #{item.Index}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = "Failed";
                        item.ErrorMessage = ex.Message;
                        item.Progress = 0;
                        SaveQueueToFile();
                        AddLog($"Queue item failed: Prompt #{item.Index} - {ex.Message}");
                        _logger.LogError($"Error processing queue item {item.Id}: {ex}");
                    }
                    finally
                    {
                        QueueProgress++;
                        UpdateQueueCountNotifications();
                    }
                }

                AddLog($"=== Story queue processing completed ({CompletedCount} successful, {FailedCount} failed) ===");
                System.Windows.MessageBox.Show($"Story generation completed!\n\nSuccessful: {CompletedCount}\nFailed: {FailedCount}",
                    "Processing Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
                _workflowCoordinator.Release();
                IsProcessingQueue = false;
                IsQueuePaused = false;
                _pauseEvent.Set();
                CurrentQueueItem = null;
                QueueProgress = 0;
                QueueTotal = 0;
                UpdateQueueCountNotifications();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void UpdateQueueCountNotifications()
        {
            OnPropertyChanged(nameof(QueuedCount));
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(QueueProgressText));
        }

        private async Task<string> ProcessQueueItemAsync(StoryPromptItem item, string inputImagePath, System.Threading.CancellationToken cancellationToken)
        {
            // Ensure ComfyUI is connected
            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Load workflow (amateurZimageAPI.json)
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "amateurZimageAPI.json");
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            // Update workflow parameters
            var updatedWorkflow = UpdateWorkflowParameters(workflow, item.Prompt);

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
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator-amateur");
            Directory.CreateDirectory(outputDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputPath = Path.Combine(outputDir, $"story_amateur_{item.Index:D2}_{timestamp}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            AddLog($"Story image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string promptText)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());

            if (workflowDict == null) return workflow;

            // Add the photographer prefix to all prompts
            const string promptPrefix = "A photo taken by the photographer Deedeemegadoodo, raw, unedited, ";
            string fullPrompt = promptPrefix + promptText;

            // 1. Update positive prompt (node 6)
            if (workflowDict.ContainsKey("6"))
            {
                var node6 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["6"].GetRawText());
                if (node6 != null && node6.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node6["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = fullPrompt;
                        node6["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.SerializeToElement(node6);
                    }
                }
            }

            // 2. Update negative prompt (node 7)
            if (workflowDict.ContainsKey("7"))
            {
                var node7 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["7"].GetRawText());
                if (node7 != null && node7.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node7["inputs"]));
                    if (inputs != null)
                    {
                        inputs["text"] = NegativePrompt;
                        node7["inputs"] = inputs;
                        workflowDict["7"] = JsonSerializer.SerializeToElement(node7);
                    }
                }
            }

            // 3. Update seed (node 28) - max value is 2^50 (1125899906842624)
            if (workflowDict.ContainsKey("28"))
            {
                var node28 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["28"].GetRawText());
                if (node28 != null && node28.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node28["inputs"]));
                    if (inputs != null)
                    {
                        long maxSeed = 1125899906842624;
                        var random = new Random();
                        byte[] bytes = new byte[8];
                        random.NextBytes(bytes);
                        long seed = Math.Abs(BitConverter.ToInt64(bytes, 0) % maxSeed);
                        inputs["seed"] = seed;
                        node28["inputs"] = inputs;
                        workflowDict["28"] = JsonSerializer.SerializeToElement(node28);
                    }
                }
            }

            // 4. Update ClownsharKSampler settings (node 582)
            if (workflowDict.ContainsKey("582"))
            {
                var node582 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["582"].GetRawText());
                if (node582 != null && node582.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node582["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = Denoise;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node582["inputs"] = inputs;
                        workflowDict["582"] = JsonSerializer.SerializeToElement(node582);
                    }
                }
            }

            // 5. Update second ClownsharKSampler settings (node 620)
            if (workflowDict.ContainsKey("620"))
            {
                var node620 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["620"].GetRawText());
                if (node620 != null && node620.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node620["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = Denoise2;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node620["inputs"] = inputs;
                        workflowDict["620"] = JsonSerializer.SerializeToElement(node620);
                    }
                }
            }

            // 6. Update KSampler settings (node 754)
            if (workflowDict.ContainsKey("754"))
            {
                var node754 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["754"].GetRawText());
                if (node754 != null && node754.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node754["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 0.9;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node754["inputs"] = inputs;
                        workflowDict["754"] = JsonSerializer.SerializeToElement(node754);
                    }
                }
            }

            // 7. Update KSampler settings (node 768)
            if (workflowDict.ContainsKey("768"))
            {
                var node768 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["768"].GetRawText());
                if (node768 != null && node768.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node768["inputs"]));
                    if (inputs != null)
                    {
                        inputs["denoise"] = 1.0;
                        inputs["steps"] = Steps;
                        inputs["cfg"] = Cfg;
                        node768["inputs"] = inputs;
                        workflowDict["768"] = JsonSerializer.SerializeToElement(node768);
                    }
                }
            }

            // 8. Update LoRA strengths
            // Node 105 - amateur photography LoRA (always applied)
            UpdateLoraStrength(workflowDict, "105", AmateurLoraStrength1);
            // Node 752 - amateur photography LoRA second instance (always applied)
            UpdateLoraStrength(workflowDict, "752", AmateurLoraStrength2);

            // 9. Update character LoRA if enabled (node 760 - currently gilliananderson)
            if (CharacterLoraEnabled && !string.IsNullOrEmpty(SelectedCharacterLora) && SelectedCharacterLora != "No LoRAs available")
            {
                UpdateCharacterLora(workflowDict, "760", SelectedCharacterLora, CharacterLoraStrength);
            }

            // 10. Update latent image dimensions
            UpdateLatentDimensions(workflowDict, "46", 576, 416);
            UpdateLatentDimensions(workflowDict, "693", 208, 288);
            UpdateLatentDimensions(workflowDict, "758", 416, 576);
            UpdateLatentDimensions(workflowDict, "772", 1248, 1728);

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void UpdateLoraStrength(Dictionary<string, JsonElement> workflowDict, string nodeId, double strength)
        {
            if (workflowDict.ContainsKey(nodeId))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict[nodeId].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null && inputs.ContainsKey("strength_model"))
                    {
                        inputs["strength_model"] = strength;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }
        }

        private void UpdateCharacterLora(Dictionary<string, JsonElement> workflowDict, string nodeId, string loraName, double strength)
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
                        // Update both lora_name and strength_model
                        inputs["lora_name"] = $"zimage\\{loraName}.safetensors";
                        inputs["strength_model"] = strength;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }
        }

        private void UpdateLatentDimensions(Dictionary<string, JsonElement> workflowDict, string nodeId, int width, int height)
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
                        inputs["width"] = width;
                        inputs["height"] = height;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);
                    }
                }
            }
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
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

                int retryCount = 0;
                int maxRetries = 20;

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

                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            !f.StartsWith("z-image_") &&
                            !f.StartsWith("temp_"))
                            .ToList();

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

                        var searchDirs = new List<string> { comfyUIOutputDir };

                        var zimageDir = Path.Combine(comfyUIOutputDir, "ZImage");
                        if (Directory.Exists(zimageDir))
                        {
                            searchDirs.Add(zimageDir);
                            try
                            {
                                var dateDirs = Directory.GetDirectories(zimageDir)
                                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                                    .Take(3);
                                foreach (var dateDir in dateDirs)
                                {
                                    searchDirs.Add(dateDir);
                                }
                            }
                            catch { }
                        }

                        AddLog($"Searching in {searchDirs.Count} directories for output images");

                        foreach (var searchDir in searchDirs)
                        {
                            var recentFiles = Directory.GetFiles(searchDir, "*.png")
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Found {recentFiles.Count} recent PNG files in: {Path.GetFileName(searchDir)}");
                                var latestFile = recentFiles.First();
                                AddLog($"Using latest file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                                images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                                break;
                            }
                        }

                        if (!images.Any())
                        {
                            AddLog($"No recent images found in retry {retryCount + 1}");

                            if (retryCount >= 5)
                            {
                                foreach (var searchDir in searchDirs)
                                {
                                    var olderFiles = Directory.GetFiles(searchDir, "*.png")
                                        .Select(f => new FileInfo(f))
                                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                                        .OrderByDescending(f => f.LastWriteTime)
                                        .ToList();

                                    if (olderFiles.Any())
                                    {
                                        AddLog($"Fallback: Found {olderFiles.Count} PNG files in last 10 minutes in: {Path.GetFileName(searchDir)}");
                                        var latestFile = olderFiles.First();
                                        AddLog($"Using fallback file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                                        images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
                                        break;
                                    }
                                }
                            }
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

        private void RefreshLoras()
        {
            LoadAvailableCharacterLoras();
            AddLog("Refreshed character LoRA list");
        }

        private string? GetLoraModelPath()
        {
            try
            {
                var comfyUIPath = _settingsService.Settings?.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfyUIPath))
                {
                    AddLog("ComfyUI installation path not configured");
                    return null;
                }

                var extraModelPathsFile = Path.Combine(comfyUIPath, "extra_model_paths.yaml");
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

                            if (yamlData.ContainsKey("comfyui"))
                            {
                                AddLog("Found 'comfyui' section in YAML");
                                var comfyuiSectionObject = yamlData["comfyui"];
                                var comfyuiSection = comfyuiSectionObject as Dictionary<object, object>;

                                if (comfyuiSection != null)
                                {
                                    var comfyuiStringDict = new Dictionary<string, object>();
                                    foreach (var kvp in comfyuiSection)
                                    {
                                        if (kvp.Key != null)
                                        {
                                            comfyuiStringDict[kvp.Key.ToString() ?? string.Empty] = kvp.Value;
                                        }
                                    }

                                    AddLog($"ComfyUI section keys: {string.Join(", ", comfyuiStringDict.Keys)}");

                                    if (comfyuiStringDict.ContainsKey("base_path"))
                                    {
                                        basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                                        AddLog($"Found base_path: {basePath}");
                                    }

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

                                if (yamlData.ContainsKey("loras"))
                                {
                                    lorasRelativePath = yamlData["loras"]?.ToString() ?? string.Empty;
                                    AddLog($"Found direct loras path: {lorasRelativePath}");
                                }
                            }

                            if (!string.IsNullOrEmpty(lorasRelativePath))
                            {
                                string fullLoraPath;
                                if (!string.IsNullOrEmpty(basePath))
                                {
                                    fullLoraPath = Path.Combine(basePath, lorasRelativePath);
                                    AddLog($"Combined base_path and loras: {basePath} + {lorasRelativePath} = {fullLoraPath}");
                                }
                                else
                                {
                                    fullLoraPath = lorasRelativePath;
                                    AddLog($"Using loras path directly: {fullLoraPath}");
                                }

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
                    }
                }
                else
                {
                    AddLog($"ERROR: extra_model_paths.yaml not found in ComfyUI directory: {extraModelPathsFile}");
                }

                var defaultLoraPath = Path.Combine(comfyUIPath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    AddLog($"Using default ComfyUI LoRA path: {defaultLoraPath}");
                    return defaultLoraPath;
                }

                AddLog($"No LoRA directory found in: {comfyUIPath}");
                return null;
            }
            catch (Exception ex)
            {
                AddLog($"Error getting LoRA model path: {ex.Message}");
                return null;
            }
        }

        private void LoadAvailableCharacterLoras()
        {
            try
            {
                var loraBasePath = GetLoraModelPath();
                if (!string.IsNullOrEmpty(loraBasePath))
                {
                    var zimageLoraPath = Path.Combine(loraBasePath, "zimage");
                    if (Directory.Exists(zimageLoraPath))
                    {
                        LoadCharacterLorasFromDirectory(zimageLoraPath, "ComfyUI LoRA directory");
                        return;
                    }
                    else
                    {
                        LoadCharacterLorasFromDirectory(loraBasePath, "ComfyUI LoRA directory");
                        return;
                    }
                }

                var localLoraPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "loras", "zimage");
                LoadCharacterLorasFromDirectory(localLoraPath, "local directory");
            }
            catch (Exception ex)
            {
                AddLog($"Error loading character LoRAs: {ex.Message}");
                AvailableCharacterLoras.Clear();
                AvailableCharacterLoras.Add("Error loading LoRAs");
            }
        }

        private void LoadCharacterLorasFromDirectory(string loraPath, string pathDescription)
        {
            AddLog($"Looking for character LoRAs in {pathDescription}: {loraPath}");

            if (!Directory.Exists(loraPath))
            {
                AddLog($"LoRA directory not found: {loraPath}");
                AvailableCharacterLoras.Clear();
                AvailableCharacterLoras.Add("No LoRAs available");
                return;
            }

            // Filter out amateur photography LoRA since it's always applied
            var loraFiles = Directory.GetFiles(loraPath, "*.safetensors")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name) &&
                               !name.Equals("amateur_photography_zimage_v1", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name)
                .ToList();

            AvailableCharacterLoras.Clear();

            if (loraFiles.Any())
            {
                foreach (var lora in loraFiles)
                {
                    if (!string.IsNullOrEmpty(lora))
                        AvailableCharacterLoras.Add(lora);
                }

                if (string.IsNullOrEmpty(SelectedCharacterLora) && AvailableCharacterLoras.Any())
                {
                    SelectedCharacterLora = AvailableCharacterLoras.First();
                }

                AddLog($"Loaded {AvailableCharacterLoras.Count} character LoRAs from {loraPath}");
            }
            else
            {
                AvailableCharacterLoras.Add("No LoRAs available");
                AddLog($"No character LoRA files found in {pathDescription}");
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
                SaveQueueToFile();
                AddLog("Queue cleared");
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OpenOutputFolder()
        {
            try
            {
                var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-generator-amateur");
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

        private string QueueFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "queue", "story_image_amateur_queue.json");

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
                var json = JsonSerializer.Serialize(QueueItems.ToList(), options);
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
                if (!File.Exists(QueueFilePath)) return;

                var json = File.ReadAllText(QueueFilePath);
                var savedItems = JsonSerializer.Deserialize<List<StoryPromptItem>>(json);

                if (savedItems != null && savedItems.Any())
                {
                    _queueItems.Clear();
                    foreach (var item in savedItems)
                    {
                        if (item.Status == "Processing")
                        {
                            item.Status = "Failed";
                            item.ErrorMessage = "Interrupted by crash or app restart";
                        }
                        _queueItems.Add(item);
                    }
                    UpdateQueueCountNotifications();
                    AddLog($"Queue loaded from file: {_queueItems.Count} items");
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
