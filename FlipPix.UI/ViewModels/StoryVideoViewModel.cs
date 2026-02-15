using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class WorkflowInfo
    {
        public string Name { get; set; } = string.Empty;
        public string WorkflowFile { get; set; } = string.Empty;
    }

    public partial class StoryVideoViewModel : ObservableObject, IDisposable
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private bool _disposed = false;

        private string _generatedPrompt1 = string.Empty;
        private string _generatedPrompt2 = string.Empty;
        private string _generatedPrompt3 = string.Empty;
        private string _generatedPrompt4 = string.Empty;
        private string _generatedPrompt5 = string.Empty;
        private string _generatedPrompt6 = string.Empty;
        private string _generatedPrompt7 = string.Empty;
        private string _generatedPrompt8 = string.Empty;
        private string _generatedPrompt9 = string.Empty;
        private string _generatedPrompt10 = string.Empty;
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _statusBarMessage = "Ready - Load prompts to get started";
        private bool _hasGeneratedPrompts = false;
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;
        private string _promptsFolderPath = string.Empty;
        private string _loadedPromptsJsonPath = string.Empty;

        // Workflow settings
        private ObservableCollection<WorkflowInfo> _allWorkflows = new ObservableCollection<WorkflowInfo>();
        private int _selectedWorkflowIndex = 0;
        private string _selectedWorkflowName = string.Empty;

        public StoryVideoViewModel(ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IFileDialogService fileDialogService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            // Load persisted prompts folder path
            LoadPromptsFolderFromSettings();

            // Load workflows
            LoadWorkflows();

            // Initialize commands
            GenerateVideoCommand = new RelayCommand(async () => await GenerateVideoAsync(), () => CanGenerateVideo);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);
            SavePromptsCommand = new RelayCommand(SavePrompts);
            LoadPromptsCommand = new RelayCommand(LoadPrompts);

            AddLog("Story Video Generator initialized");
            AddLog("Load prompts from file to begin video generation");
        }

        // Workflow Properties
        public int SelectedWorkflowIndex
        {
            get => _selectedWorkflowIndex;
            set
            {
                if (_selectedWorkflowIndex != value)
                {
                    _selectedWorkflowIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedWorkflowName));
                }
            }
        }

        public ObservableCollection<string> WorkflowNames { get; } = new ObservableCollection<string>();

        public string SelectedWorkflowName
        {
            get => _selectedWorkflowName;
            private set
            {
                if (_selectedWorkflowName != value)
                {
                    _selectedWorkflowName = value;
                    OnPropertyChanged();
                }
            }
        }

        public WorkflowInfo? SelectedWorkflow => _allWorkflows.Count > 0 ? _allWorkflows[Math.Min(SelectedWorkflowIndex, _allWorkflows.Count - 1)] : null;

        // Properties
        public string PromptsFolderPath
        {
            get => _promptsFolderPath;
            set
            {
                _promptsFolderPath = value;
                OnPropertyChanged();
                SavePromptsFolderToSettings();
            }
        }

        private void SavePromptsFolderToSettings()
        {
            try
            {
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryVideoPromptsFolder = PromptsFolderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR saving prompts folder to settings: {ex.Message}");
            }
        }

        private void LoadPromptsFolderFromSettings()
        {
            try
            {
                if (!string.IsNullOrEmpty(_settingsService.Settings?.StoryVideoPromptsFolder) &&
                    Directory.Exists(_settingsService.Settings.StoryVideoPromptsFolder))
                {
                    _promptsFolderPath = _settingsService.Settings.StoryVideoPromptsFolder;
                    AddLog($"Loaded prompts folder from settings: {_promptsFolderPath}");
                }
                else
                {
                    // Default to documents folder
                    _promptsFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    AddLog($"Using default prompts folder: {_promptsFolderPath}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts folder from settings: {ex.Message}");
                _promptsFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
        }

        private void LoadWorkflows()
        {
            try
            {
                // Clear previous workflows
                _allWorkflows.Clear();
                WorkflowNames.Clear();

                var workflowDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow");

                if (!Directory.Exists(workflowDir))
                {
                    AddLog($"Workflow directory not found at {workflowDir}");
                    return;
                }

                // Load all workflow JSON files from workflow folder (excluding ZStyles subfolder)
                var workflowFiles = Directory.GetFiles(workflowDir, "*.json")
                    .Where(f => !Path.GetDirectoryName(f)?.EndsWith("ZStyles", StringComparison.OrdinalIgnoreCase) ?? false)
                    .ToArray();

                AddLog($"Found {workflowFiles.Length} workflow files in {workflowDir}");

                foreach (var workflowFile in workflowFiles)
                {
                    try
                    {
                        // Extract workflow name from filename
                        var fileName = Path.GetFileNameWithoutExtension(workflowFile);
                        var workflowName = fileName;

                        // Add workflow info for this workflow file
                        _allWorkflows.Add(new WorkflowInfo
                        {
                            Name = workflowName,
                            WorkflowFile = workflowFile
                        });

                        WorkflowNames.Add(workflowName);
                        AddLog($"Loaded workflow: {workflowName} from {Path.GetFileName(workflowFile)}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"Error loading workflow file {workflowFile}: {ex.Message}");
                    }
                }

                // Sort workflows alphabetically
                var sortedWorkflows = _allWorkflows.OrderBy(w => w.Name).ToList();
                _allWorkflows.Clear();
                foreach (var workflow in sortedWorkflows)
                {
                    _allWorkflows.Add(workflow);
                }

                WorkflowNames.Clear();
                foreach (var workflow in _allWorkflows)
                {
                    WorkflowNames.Add(workflow.Name);
                }

                AddLog($"Loaded {_allWorkflows.Count} total workflows");

                // Set initial selected workflow name
                if (_allWorkflows.Count > 0)
                {
                    SelectedWorkflowName = _allWorkflows[0].Name;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Error loading workflows: {ex.Message}");
            }
        }

        public string GeneratedPrompt1
        {
            get => _generatedPrompt1;
            set
            {
                _generatedPrompt1 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt2
        {
            get => _generatedPrompt2;
            set
            {
                _generatedPrompt2 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt3
        {
            get => _generatedPrompt3;
            set
            {
                _generatedPrompt3 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt4
        {
            get => _generatedPrompt4;
            set
            {
                _generatedPrompt4 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt5
        {
            get => _generatedPrompt5;
            set
            {
                _generatedPrompt5 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt6
        {
            get => _generatedPrompt6;
            set
            {
                _generatedPrompt6 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt7
        {
            get => _generatedPrompt7;
            set
            {
                _generatedPrompt7 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt8
        {
            get => _generatedPrompt8;
            set
            {
                _generatedPrompt8 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt9
        {
            get => _generatedPrompt9;
            set
            {
                _generatedPrompt9 = value;
                OnPropertyChanged();
            }
        }

        public string GeneratedPrompt10
        {
            get => _generatedPrompt10;
            set
            {
                _generatedPrompt10 = value;
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
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            set
            {
                _statusBarMessage = value;
                OnPropertyChanged();
            }
        }

        public bool HasGeneratedPrompts
        {
            get => _hasGeneratedPrompts;
            set
            {
                _hasGeneratedPrompts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerateVideo));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            set
            {
                _hasResultVideo = value;
                OnPropertyChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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

        public bool CanGenerateVideo => HasGeneratedPrompts && !IsProcessing;

        // Commands
        public ICommand GenerateVideoCommand { get; }
        public ICommand CancelGenerationCommand { get; }
        public ICommand OpenResultFolderCommand { get; }
        public ICommand SavePromptsCommand { get; }
        public ICommand LoadPromptsCommand { get; }

        // Methods
        private async Task GenerateVideoAsync()
        {
            if (!CanGenerateVideo) return;

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                AddLog("=== Starting video generation ===");
                IsProcessing = true;
                ProcessingProgress = 0;
                ProcessingStatus = "Checking ComfyUI status...";

                // Check if ComfyUI has crashed and restart if needed
                AddLog("Checking if ComfyUI is running...");

                var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                    status => AddLog($"[Auto-Restart] {status}"),
                    _cancellationTokenSource.Token);

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
                    await _comfyUIService.ConnectAsync(_cancellationTokenSource.Token);
                    AddLog("Connected to ComfyUI");
                }

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                // Get selected workflow
                var selectedWorkflow = SelectedWorkflow;
                if (selectedWorkflow == null)
                {
                    AddLog("ERROR: No workflow selected. Please select a workflow first.");
                    System.Windows.MessageBox.Show("No workflow selected. Please select a workflow first.", "Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                // Load workflow
                if (!File.Exists(selectedWorkflow.WorkflowFile))
                {
                    AddLog($"ERROR: Workflow file not found: {selectedWorkflow.WorkflowFile}");
                    System.Windows.MessageBox.Show($"Workflow file not found: {selectedWorkflow.WorkflowFile}", "Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"Loading workflow: {selectedWorkflow.Name} ({Path.GetFileName(selectedWorkflow.WorkflowFile)})");
                var workflowJson = await File.ReadAllTextAsync(selectedWorkflow.WorkflowFile, _cancellationTokenSource.Token);
                var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                AddLog("WCFMAPI workflow does not require an uploaded image - using EmptyImage node");

                // Get all prompts
                var prompts = new List<string>
                {
                    GeneratedPrompt1, GeneratedPrompt2, GeneratedPrompt3, GeneratedPrompt4, GeneratedPrompt5,
                    GeneratedPrompt6, GeneratedPrompt7, GeneratedPrompt8, GeneratedPrompt9, GeneratedPrompt10
                };

                // Remove empty prompts
                prompts = prompts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

                if (prompts.Count == 0)
                {
                    AddLog("ERROR: No valid prompts found");
                    System.Windows.MessageBox.Show("No valid prompts found. Please generate prompts first.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                AddLog($"=== STARTING VIDEO GENERATION FOR {prompts.Count} PROMPTS ===");
                AddLog($"This will generate {prompts.Count} video clips sequentially");
                AddLog($"Expected total time: {prompts.Count * 2}-{prompts.Count * 10} minutes depending on your hardware");
                AddLog("=== PROMPT PROCESSING START ===\n");

                var generatedVideos = new List<string>();
                int successfulClips = 0;

                // Process each prompt
                for (int i = 0; i < prompts.Count; i++)
                {
                    var currentPrompt = prompts[i];
                    var promptNumber = i + 1;

                    AddLog($"\n*** PROCESSING PROMPT {promptNumber}/{prompts.Count} ***");
                    var promptPreview = currentPrompt.Length > 80 ? currentPrompt.Substring(0, 80) + "..." : currentPrompt;
                    AddLog($"Prompt: {promptPreview}");

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingStatus = $"Processing prompt {promptNumber}/{prompts.Count}...";
                        ProcessingProgress = (i / (double)prompts.Count) * 100;
                    });

                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                    // Update workflow with current prompt
                    AddLog("Updating workflow with prompt...");
                    var updatedWorkflow = UpdateVideoWorkflowParameters(workflow, currentPrompt);

                    // Track node completion for this clip
                    var nodeProgress = new Dictionary<string, double>();

                    var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                    {
                        if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                        {
                            var nodeId = progressMsg.Data.Node ?? "unknown";
                            var nodePercent = (double)progressMsg.Data.Value / progressMsg.Data.Max;

                            // Track this node's progress
                            nodeProgress[nodeId] = nodePercent;

                            // For WCFMAPI workflow, we track main processing nodes
                            var videoNodeIds = new[] { "177:134", "177:125" };
                            var isVideoNode = videoNodeIds.Any(id => nodeId.Contains(id));

                            if (isVideoNode)
                            {
                                // Calculate overall progress: base progress for completed clips + current clip progress
                                var baseProgress = (i / (double)prompts.Count) * 100;
                                var clipProgress = (nodePercent / prompts.Count) * 100;
                                var overallProgress = Math.Min(baseProgress + clipProgress, 95);

                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    ProcessingProgress = overallProgress;
                                    ProcessingStatus = $"Generating clip {promptNumber}/{prompts.Count}: {nodePercent * 100:F0}%";
                                });

                                AddLog($"[Progress] Clip {promptNumber} - Node {nodeId}: {nodePercent * 100:F0}%");
                            }
                        }
                    });

                    string? promptId = null;
                    try
                    {
                        AddLog("Executing workflow in ComfyUI...");
                        promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, _cancellationTokenSource.Token);
                        AddLog($"✓ Clip {promptNumber} execution completed (Prompt ID: {promptId})");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"✗ ERROR during clip {promptNumber} execution: {ex.Message}");
                        AddLog("Checking if ComfyUI crashed during execution...");

                        // Detect and potentially restart ComfyUI after crash
                        var recovered = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                            status => AddLog($"[Crash Recovery] {status}"),
                            _cancellationTokenSource.Token);

                        if (recovered)
                        {
                            AddLog("ComfyUI was restarted, but the current video generation was interrupted.");
                            var result = System.Windows.MessageBox.Show(
                                $"ComfyUI crashed during video clip {promptNumber}/{prompts.Count} but has been restarted.\n\nDo you want to retry this clip or skip to the next one?",
                                "ComfyUI Crash Detected",
                                System.Windows.MessageBoxButton.YesNo,
                                System.Windows.MessageBoxImage.Warning);

                            if (result == System.Windows.MessageBoxResult.Yes)
                            {
                                AddLog("Retrying current clip...");
                                i--; // Retry this clip
                                continue;
                            }
                            else
                            {
                                AddLog($"Skipping clip {promptNumber} and continuing to next prompt...");
                                continue; // Skip to next clip
                            }
                        }
                        else
                        {
                            AddLog("ComfyUI may not be running properly");
                            throw; // Re-throw to trigger the outer catch block
                        }
                    }

                    // Wait for video to be written
                    AddLog("Waiting for video file to be written...");
                    await Task.Delay(5000, _cancellationTokenSource.Token);

                    // Get output video for this clip
                    var videoPath = GetOutputVideoFromComfyUI();
                    if (!string.IsNullOrEmpty(videoPath))
                    {
                        generatedVideos.Add(videoPath);
                        successfulClips++;
                        AddLog($"✓ Clip {promptNumber} saved: {Path.GetFileName(videoPath)}");

                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            StatusBarMessage = $"Generated {successfulClips}/{prompts.Count} clips";
                        });
                    }
                    else
                    {
                        AddLog($"⚠ WARNING: No output video found for clip {promptNumber}");
                        var result = System.Windows.MessageBox.Show(
                            $"No output video was generated for clip {promptNumber}/{prompts.Count}.\n\nDo you want to continue with the remaining clips?",
                            "Warning",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Warning);

                        if (result == System.Windows.MessageBoxResult.No)
                        {
                            AddLog("User chose to stop generation");
                            break;
                        }
                    }
                }

                AddLog("\n=== ALL PROMPTS PROCESSED ===");
                AddLog($"Successfully generated: {successfulClips}/{prompts.Count} video clips");

                if (generatedVideos.Any())
                {
                    // Set the first video as the main result
                    ResultVideoPath = generatedVideos.First();
                    HasResultVideo = true;
                    ProcessingProgress = 100;
                    ProcessingStatus = "Video generation complete!";
                    StatusBarMessage = $"Generated {successfulClips} video clips";

                    AddLog($"\nGenerated videos:");
                    for (int i = 0; i < generatedVideos.Count; i++)
                    {
                        AddLog($"  {i + 1}. {generatedVideos[i]}");
                    }

                    // Copy all videos to output folder with proper naming
                    // Create subfolder based on JSON filename (similar to StoryImageGeneratorViewModel)
                    var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-video");
                    Directory.CreateDirectory(baseOutputDir);

                    // Get JSON filename for subfolder naming
                    var jsonFileName = "story-videos";
                    if (!string.IsNullOrEmpty(_loadedPromptsJsonPath))
                    {
                        jsonFileName = Path.GetFileNameWithoutExtension(_loadedPromptsJsonPath);
                    }

                    // Create sequential folder if it already exists
                    var sessionOutputDir = GetUniqueFolderPath(baseOutputDir, jsonFileName);
                    Directory.CreateDirectory(sessionOutputDir);

                    var copiedVideos = new List<string>();

                    for (int i = 0; i < generatedVideos.Count; i++)
                    {
                        // Generate sequential filename: jsonfilename-1.mp4, jsonfilename-2.mp4, etc.
                        var destPath = Path.Combine(sessionOutputDir, $"{jsonFileName}-{i + 1}.mp4");
                        File.Copy(generatedVideos[i], destPath, true);
                        await LocalCopyService.CopyVideoAsync(destPath);
                        copiedVideos.Add(destPath);
                        AddLog($"✓ Copied clip {i + 1} to: {destPath}");
                    }

                    AddLog($"\n✓ All {copiedVideos.Count} videos copied to: {sessionOutputDir}");

                    System.Windows.MessageBox.Show(
                        $"Successfully generated {successfulClips} video clips!\n\nVideos saved to:\n{sessionOutputDir}\n\nClip files:\n{string.Join("\n", copiedVideos.Select(Path.GetFileName))}",
                        "Generation Complete",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    AddLog("ERROR: No videos were generated");
                    System.Windows.MessageBox.Show("No video clips were generated. Please check the ComfyUI console for errors.", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Video generation cancelled by user");
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

                _logger.LogError($"Error generating video: {ex}");
                ProcessingStatus = "Error occurred";
                ProcessingProgress = 0;

                System.Windows.MessageBox.Show(
                    $"Error generating video:\n\n{ex.Message}\n\nCheck the log for more details.",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsProcessing = false;
                AddLog("=== Video generation ended ===");
            }
        }

        private JsonElement UpdatePromptWorkflowParameters(JsonElement workflow, string imageFileName, string customPrompt)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            // Update LoadImage node (node 1)
            if (workflowDict.ContainsKey("1"))
            {
                var node1 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["1"].GetRawText());
                if (node1 != null && node1.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node1["inputs"]));
                    if (inputs != null)
                    {
                        inputs["image"] = imageFileName;
                        node1["inputs"] = inputs;
                        workflowDict["1"] = JsonSerializer.SerializeToElement(node1);
                    }
                }
            }

            // Update QwenVL node (node 2) with custom prompt
            if (workflowDict.ContainsKey("2"))
            {
                var node2 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["2"].GetRawText());
                if (node2 != null && node2.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node2["inputs"]));
                    if (inputs != null)
                    {
                        inputs["custom_prompt"] = customPrompt;
                        node2["inputs"] = inputs;
                        workflowDict["2"] = JsonSerializer.SerializeToElement(node2);
                    }
                }
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private JsonElement UpdateVideoWorkflowParameters(JsonElement workflow, string prompt)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            // WCFMAPI workflow uses node "177:109" (PrimitiveStringMultiline) with "value" field for the prompt
            if (workflowDict.ContainsKey("177:109"))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["177:109"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null && inputs.ContainsKey("value"))
                    {
                        inputs["value"] = prompt;
                        node["inputs"] = inputs;
                        workflowDict["177:109"] = JsonSerializer.SerializeToElement(node);

                        var promptPreview = prompt.Length > 50 ? prompt.Substring(0, 50) + "..." : prompt;
                        AddLog($"✓ Node 177:109 - Prompt updated: {promptPreview}");
                    }
                }
            }
            else
            {
                AddLog("⚠ Node 177:109 not found in workflow - prompt not updated");
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        private void UpdateTextEncodeNode(Dictionary<string, JsonElement> workflowDict, string nodeId, string positivePrompt, string negativePrompt)
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
                        // Store the original prompt for comparison
                        var originalPrompt = inputs.ContainsKey("positive_prompt") ? inputs["positive_prompt"].ToString() : "[NOT SET]";

                        inputs["positive_prompt"] = positivePrompt;
                        inputs["negative_prompt"] = negativePrompt;
                        node["inputs"] = inputs;
                        workflowDict[nodeId] = JsonSerializer.SerializeToElement(node);

                        // Only log if the prompt actually changed
                        if (originalPrompt != positivePrompt)
                        {
                            AddLog($"  ✓ Node {nodeId} - Prompt updated successfully");
                        }
                        else
                        {
                            AddLog($"  ⚠ Node {nodeId} - Prompt unchanged (may already be set)");
                        }
                    }
                }
                else
                {
                    AddLog($"  ✗ Node {nodeId} - ERROR: Invalid node structure");
                }
            }
            else
            {
                AddLog($"  ✗ Node {nodeId} - ERROR: Node not found in workflow!");
            }
        }

        private async Task<string> GetGeneratedTextFromHistory(string promptId)
        {
            try
            {
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
                var historyUrl = $"{baseUrl}/history/{promptId}";

                AddLog($"Fetching history from: {historyUrl}");

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var response = await httpClient.GetAsync(historyUrl);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var historyData = JsonSerializer.Deserialize<JsonElement>(content);

                    // Navigate through the JSON structure to find the ShowText output
                    if (historyData.TryGetProperty(promptId, out var promptData) &&
                        promptData.TryGetProperty("outputs", out var outputs))
                    {
                        // Look for node 3 (ShowText)
                        if (outputs.TryGetProperty("3", out var node3Output) &&
                            node3Output.TryGetProperty("text", out var textArray) &&
                            textArray.GetArrayLength() > 0)
                        {
                            return textArray[0].GetString() ?? string.Empty;
                        }
                    }
                }

                AddLog("Could not find generated text in history response");
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving generated text: {ex.Message}");
            }

            return string.Empty;
        }

        private List<string> ExtractPromptsFromText(string generatedText)
        {
            var prompts = new List<string>();

            try
            {
                AddLog("\n*** PROMPT EXTRACTION START ***");
                AddLog($"Full text length: {generatedText.Length} characters");

                // Log the first 800 characters for debugging
                var preview = generatedText.Substring(0, Math.Min(800, generatedText.Length));
                AddLog($"Text preview: {preview}...");

                // Strategy 1: Split by "Prompt #N:" markers and extract content between them
                var promptSections = Regex.Split(generatedText, @"Prompt\s*#?\d+:\s*", RegexOptions.IgnoreCase)
                    .Skip(1) // Skip empty first element
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (promptSections.Count >= 5)
                {
                    AddLog($"Strategy 1: Found {promptSections.Count} 'Prompt #N:' sections");

                    foreach (var section in promptSections.Take(10))
                    {
                        // Extract quoted text if present
                        var quotedMatch = Regex.Match(section, "^\"([^\"]*(?:\"[^\"]*)*)\"");
                        string promptText;

                        if (quotedMatch.Success)
                        {
                            promptText = quotedMatch.Groups[1].Value;
                        }
                        else
                        {
                            // Take everything until the next newline or end, but clean it
                            var lines = section.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            promptText = lines.FirstOrDefault()?.Trim() ?? section.Trim();
                        }

                        var cleaned = PromptParser.CleanPrompt(promptText);

                        if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length > 20)
                        {
                            prompts.Add(cleaned);
                            AddLog($"  → Extracted prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                        }
                    }
                }

                // Strategy 2: Look for "Video Clip #N:" followed by description in parentheses
                if (prompts.Count < 10)
                {
                    var videoClipPattern = "Video Clip #(\\d+):\\s*\"([^\"]+)\"\\s*\\([^)]+\\)\\s*(.*?)(?=Video Clip #\\d+:|$)";
                    var matches = Regex.Matches(generatedText, videoClipPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

                    if (matches.Count >= 5)
                    {
                        AddLog($"Strategy 2: Found {matches.Count} 'Video Clip' sections");

                        foreach (Match match in matches.Take(10 - prompts.Count))
                        {
                            var title = PromptParser.CleanPrompt(match.Groups[2].Value.Trim());
                            var description = PromptParser.CleanPrompt(match.Groups[3].Value.Trim());

                            var fullPrompt = string.IsNullOrWhiteSpace(description) ? title : $"{title}. {description}";

                            if (!string.IsNullOrWhiteSpace(fullPrompt) && fullPrompt.Length > 20)
                            {
                                prompts.Add(fullPrompt);
                                AddLog($"  → Extracted prompt {prompts.Count}: {fullPrompt.Substring(0, Math.Min(60, fullPrompt.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 3: Look for lines starting with "Video Clip #" without quotes
                if (prompts.Count < 10)
                {
                    var lines = generatedText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Trim())
                        .Where(line => line.StartsWith("Video Clip #", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (lines.Count > 0)
                    {
                        AddLog($"Strategy 3: Found {lines.Count} Video Clip lines");
                        foreach (var line in lines.Take(10 - prompts.Count))
                        {
                            // Extract the content after "Video Clip #N: " and before any time indication
                            var content = Regex.Replace(line, @"^Video Clip #\d+:\s*", "", RegexOptions.IgnoreCase);
                            content = Regex.Replace(content, @"\(.*?\d+s.*?\)", "").Trim();

                            if (!string.IsNullOrWhiteSpace(content) && content.Length > 20)
                            {
                                var cleaned = PromptParser.CleanPrompt(content);
                                prompts.Add(cleaned);
                                AddLog($"  → Line prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 4: Extract numbered items with descriptions
                if (prompts.Count < 10)
                {
                    var numberedPattern = "^\\s*\\d+\\.\\s*(.*?)(?=^\\s*\\d+\\.|$)";
                    var matches = Regex.Matches(generatedText, numberedPattern, RegexOptions.Multiline | RegexOptions.Singleline);

                    if (matches.Count > 0)
                    {
                        AddLog($"Strategy 4: Found {matches.Count} numbered items");
                        foreach (Match match in matches.Take(10 - prompts.Count))
                        {
                            var content = PromptParser.CleanPrompt(match.Groups[1].Value.Trim());
                            if (!string.IsNullOrWhiteSpace(content) && content.Length > 30)
                            {
                                prompts.Add(content);
                                AddLog($"  → Numbered prompt {prompts.Count}: {content.Substring(0, Math.Min(60, content.Length))}...");
                            }
                        }
                    }
                }

                // Strategy 5: Extract descriptive sentences
                if (prompts.Count < 10)
                {
                    AddLog($"Strategy 5: Extracting sentences (need {10 - prompts.Count} more)");

                    var sentences = Regex.Split(generatedText, @"(?<=[.!?])\s+(?=[A-Z])")
                        .Where(s => !string.IsNullOrWhiteSpace(s) && s.Length > 50)
                        .Where(s => !s.StartsWith("Here") && !s.StartsWith("Each") && !s.StartsWith("Perfect"))
                        .Where(s => !s.Contains("perfect for") && !s.Contains("social media"))
                        .Take(15)
                        .ToList();

                    foreach (var sentence in sentences)
                    {
                        if (prompts.Count >= 10) break;

                        var cleaned = PromptParser.CleanPrompt(sentence);
                        if (cleaned.Length > 40)
                        {
                            prompts.Add(cleaned);
                            AddLog($"  → Sentence prompt {prompts.Count}: {cleaned.Substring(0, Math.Min(60, cleaned.Length))}...");
                        }
                    }
                }

                // Strategy 6: Fill remaining slots with variations
                if (prompts.Count > 0 && prompts.Count < 10)
                {
                    AddLog($"Strategy 6: Filling {10 - prompts.Count} missing slots");
                    var basePrompts = prompts.ToList();

                    for (int i = prompts.Count; i < 10; i++)
                    {
                        var basePrompt = basePrompts[i % basePrompts.Count];
                        var variation = $"{basePrompt} (Scene {i + 1})";
                        prompts.Add(variation);
                        AddLog($"  → Created variation {i + 1}");
                    }
                }

                AddLog($"\n*** EXTRACTION COMPLETE: {prompts.Count} prompts ***");
                for (int i = 0; i < Math.Min(prompts.Count, 10); i++)
                {
                    AddLog($"  {i + 1}. {prompts[i].Substring(0, Math.Min(80, prompts[i].Length))}...");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR extracting prompts: {ex.Message}");
            }

            return prompts;
        }

        private string GetOutputVideoFromComfyUI()
        {
            try
            {
                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(comfyUIOutputDir))
                {
                    AddLog("ERROR: ComfyUI output folder not configured");
                    return string.Empty;
                }

                if (!Directory.Exists(comfyUIOutputDir))
                {
                    AddLog($"ERROR: ComfyUI output folder not found: {comfyUIOutputDir}");
                    return string.Empty;
                }

                // Look for WCFMAPI output files (WCFMAPI workflow uses "LTX-2" prefix)
                var videoFiles = Directory.GetFiles(comfyUIOutputDir, "LTX-2*.mp4")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (videoFiles.Any())
                {
                    var latestFile = videoFiles.First();
                    var fileAge = DateTime.Now - File.GetLastWriteTime(latestFile);

                    // Only use files created in the last 10 minutes
                    if (fileAge.TotalMinutes < 10)
                    {
                        AddLog($"Found output video: {Path.GetFileName(latestFile)}");

                        // Copy to our output folder
                        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-video");
                        Directory.CreateDirectory(outputDir);

                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var outputPath = Path.Combine(outputDir, $"story-video_{timestamp}.mp4");

                        File.Copy(latestFile, outputPath, true);
                        await LocalCopyService.CopyVideoAsync(outputPath);
                        AddLog($"Video copied to: {outputPath}");

                        return outputPath;
                    }
                    else
                    {
                        AddLog($"Latest file is too old ({fileAge.TotalMinutes:F1} minutes), no new video found");
                    }
                }
                else
                {
                    AddLog("No LTX-2 output files found");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output video: {ex.Message}");
            }

            return string.Empty;
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
                var folder = Path.GetDirectoryName(ResultVideoPath);
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

        private async void SavePrompts()
        {
            try
            {
                var defaultFileName = $"story-prompts_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                var filePath = await _fileDialogService.SaveFileDialogAsync(
                    "Save Generated Prompts",
                    "JSON Files|*.json|All Files|*.*",
                    defaultFileName,
                    PromptsFolderPath);

                if (filePath != null)
                {
                    var promptsData = new
                    {
                        Prompts = new[]
                        {
                            GeneratedPrompt1, GeneratedPrompt2, GeneratedPrompt3, GeneratedPrompt4, GeneratedPrompt5,
                            GeneratedPrompt6, GeneratedPrompt7, GeneratedPrompt8, GeneratedPrompt9, GeneratedPrompt10
                        },
                        SavedAt = DateTime.Now,
                        Version = "1.0"
                    };

                    var json = JsonSerializer.Serialize(promptsData, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(filePath, json);
                    AddLog($"Prompts saved to: {Path.GetFileName(filePath)}");
                    StatusBarMessage = "Prompts saved successfully";

                    // Update the prompts folder to the saved location
                    PromptsFolderPath = Path.GetDirectoryName(filePath) ?? PromptsFolderPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR saving prompts: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error saving prompts: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private async void LoadPrompts()
        {
            try
            {
                var filePath = await _fileDialogService.OpenFileDialogAsync(
                    "Load Generated Prompts",
                    "JSON Files|*.json|All Files|*.*",
                    PromptsFolderPath);

                if (filePath != null)
                {
                    var json = File.ReadAllText(filePath);
                    var data = JsonSerializer.Deserialize<JsonElement>(json);

                    // Load the prompts (support both old and new formats)
                    JsonElement promptsProp;
                    if (data.TryGetProperty("Prompts", out var p) && p.ValueKind == JsonValueKind.Array)
                    {
                        promptsProp = p;
                    }
                    else if (data.ValueKind == JsonValueKind.Array)
                    {
                        // Direct array format
                        promptsProp = data;
                    }
                    else
                    {
                        AddLog("ERROR: Invalid prompts file format - missing Prompts array");
                        System.Windows.MessageBox.Show(
                            "Invalid file format. Expected a JSON file with a 'Prompts' array.",
                            "Error",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error);
                        return;
                    }

                    var prompts = promptsProp.EnumerateArray().ToList();

                    // Support any number of prompts (1-10)
                    var promptCount = Math.Min(prompts.Count, 10);
                    var promptValues = prompts.Select(p => p.GetString() ?? string.Empty).ToList();

                    // Fill in all prompt slots (pad with empty strings if needed)
                    for (int i = 0; i < 10; i++)
                    {
                        var value = i < promptCount ? promptValues[i] : string.Empty;
                        switch (i)
                        {
                            case 0: GeneratedPrompt1 = value; break;
                            case 1: GeneratedPrompt2 = value; break;
                            case 2: GeneratedPrompt3 = value; break;
                            case 3: GeneratedPrompt4 = value; break;
                            case 4: GeneratedPrompt5 = value; break;
                            case 5: GeneratedPrompt6 = value; break;
                            case 6: GeneratedPrompt7 = value; break;
                            case 7: GeneratedPrompt8 = value; break;
                            case 8: GeneratedPrompt9 = value; break;
                            case 9: GeneratedPrompt10 = value; break;
                        }
                    }

                    HasGeneratedPrompts = true;
                    _loadedPromptsJsonPath = filePath;
                    AddLog($"Loaded {promptCount} prompts from: {Path.GetFileName(filePath)}");
                    StatusBarMessage = $"{promptCount} prompts loaded successfully";

                    // Log the loaded prompts for verification
                    AddLog("\n*** LOADED PROMPTS VERIFICATION ***");
                    for (int i = 0; i < promptCount; i++)
                    {
                        var prompt = promptValues[i];
                        if (!string.IsNullOrWhiteSpace(prompt))
                        {
                            AddLog($"  {i + 1}. {prompt.Substring(0, Math.Min(60, prompt.Length))}...");
                        }
                        else
                        {
                            AddLog($"  {i + 1}. [EMPTY PROMPT]");
                        }
                    }
                    AddLog("*** LOAD COMPLETE ***\n");

                    // Update the prompts folder to the loaded location
                    PromptsFolderPath = Path.GetDirectoryName(filePath) ?? PromptsFolderPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
                System.Windows.MessageBox.Show(
                    $"Error loading prompts: {ex.Message}",
                    "Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
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
                WorkflowNames.Clear();
                _allWorkflows.Clear();

                // Clear string properties
                _generatedPrompt1 = string.Empty;
                _generatedPrompt2 = string.Empty;
                _generatedPrompt3 = string.Empty;
                _generatedPrompt4 = string.Empty;
                _generatedPrompt5 = string.Empty;
                _generatedPrompt6 = string.Empty;
                _generatedPrompt7 = string.Empty;
                _generatedPrompt8 = string.Empty;
                _generatedPrompt9 = string.Empty;
                _generatedPrompt10 = string.Empty;
                _processingStatus = string.Empty;
                _logOutput = string.Empty;
                _statusBarMessage = string.Empty;
                _resultVideoPath = string.Empty;
                _promptsFolderPath = string.Empty;
                _loadedPromptsJsonPath = string.Empty;
                _selectedWorkflowName = string.Empty;

                _disposed = true;
            }
        }
    }
}
