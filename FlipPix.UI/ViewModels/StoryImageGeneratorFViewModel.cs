using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public partial class StoryImageGeneratorFViewModel : StoryImageGeneratorBaseViewModel
    {
        private readonly LMStudioService _lmStudioService;

        // Image resolution constants
        private const string LandscapeResolution = "1344x768";
        private const string PortraitResolution = "768x1344";

        private bool _settingsVisible = false;
        private bool _isPortraitMode = false; // Default to landscape mode

        public StoryImageGeneratorFViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LoraManager loraManager,
            ComfyUIImageRetriever imageRetriever,
            LMStudioService lmStudioService)
            : base(comfyUIService, logger, settingsService, workflowCoordinator, fileDialogService, loraManager, imageRetriever)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));

            // Re-evaluate AnalyzeImageWithQwenVLCommand when InputImagePath changes
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InputImagePath))
                {
                    (AnalyzeImageWithQwenVLCommand as CommunityToolkit.Mvvm.Input.RelayCommand)?.NotifyCanExecuteChanged();
                }
            };
        }

        // --- Abstract member implementations ---

        protected override string VariantDisplayName => "Story Image Generator F";
        protected override string WorkflowTypeName => "StoryImageF";
        protected override string QueuePersistenceFileName => "story_image_f_queue.json";
        protected override string OutputFolderName => "story-generator-f";
        protected override int DefaultSteps => 4;
        protected override double DefaultCfg => 1.0;
        protected override double DefaultDenoise => 0.98;

        // --- Virtual overrides ---

        protected override bool UseSessionOutputFolder => true;

        // --- Variant-specific initialization ---

        protected override void InitializeVariant()
        {
            ToggleSettingsVisibilityCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ToggleSettingsVisibility);
            AnalyzeImageWithQwenVLCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                async () => await AnalyzeImageWithQwenVLAsync(),
                () => !string.IsNullOrEmpty(InputImagePath) && File.Exists(InputImagePath) && !IsAnalyzingImage);
        }

        // --- Overrides for folder saving ---

        protected override string GetPromptJsonInitialDirectory()
        {
            var folder = _settingsService.Settings?.StoryGeneratorPromptJsonFolder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : base.GetPromptJsonInitialDirectory();
        }

        protected override void SavePromptJsonFolder(string folderPath)
        {
            if (_settingsService.Settings != null)
            {
                _settingsService.Settings.StoryGeneratorPromptJsonFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        protected override string GetInputImageInitialDirectory()
        {
            var folder = _settingsService.Settings?.StoryGeneratorInputImageFolder;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? folder
                : base.GetInputImageInitialDirectory();
        }

        protected override void SaveInputImageFolder(string folderPath)
        {
            if (_settingsService.Settings != null)
            {
                _settingsService.Settings.StoryGeneratorInputImageFolder = folderPath;
                _settingsService.SaveSettings(_settingsService.Settings);
            }
        }

        // --- Variant-specific properties ---

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

        public ICommand ToggleSettingsVisibilityCommand { get; private set; } = null!;
        public ICommand AnalyzeImageWithQwenVLCommand { get; private set; } = null!;

        private bool _isAnalyzingImage = false;
        public bool IsAnalyzingImage
        {
            get => _isAnalyzingImage;
            set
            {
                if (SetProperty(ref _isAnalyzingImage, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _analysisStatus = string.Empty;
        public string AnalysisStatus
        {
            get => _analysisStatus;
            set => SetProperty(ref _analysisStatus, value);
        }

        private void ToggleSettingsVisibility()
        {
            SettingsVisible = !SettingsVisible;
        }

        // --- Qwen VL Analysis ---

        private async Task AnalyzeImageWithQwenVLAsync()
        {
            try
            {
                IsAnalyzingImage = true;
                AnalysisStatus = "Analyzing image with Qwen VL...";
                AddLog("Starting image analysis with Qwen VL...");

                // Read system prompt from file - USES KLEIN SYSTEM PROMPT
                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "klien-story-10.md");
                if (!File.Exists(systemPromptPath))
                {
                    throw new FileNotFoundException($"System prompt file not found: {systemPromptPath}");
                }
                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);

                // Get model name from settings
                var modelName = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(modelName))
                {
                    System.Windows.MessageBox.Show(
                        "No LM Studio model selected. Please configure LM Studio in the Image Analyzer tab first.",
                        "Model Not Configured", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Call LM Studio with system prompt
                var userPrompt = "Analyze this character image and generate 10 sequential martial arts action story prompts following the template in the system instructions.";

                var analysisResult = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    modelName,
                    InputImagePath,
                    userPrompt,
                    systemPrompt,
                    maxTokens: 36000,
                    CancellationToken.None);

                if (string.IsNullOrEmpty(analysisResult))
                {
                    AddLog("ERROR: No response from Qwen VL");
                    System.Windows.MessageBox.Show("No response received from Qwen VL.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AddLog($"Received Qwen VL response ({analysisResult.Length} chars)");

                // Parse the 10 prompts from the response
                var prompts = ParsePromptsFromAnalysis(analysisResult);

                if (prompts.Count == 0)
                {
                    AddLog("ERROR: Could not parse any prompts from Qwen VL response");
                    AddLog($"Raw response preview: {analysisResult.Substring(0, Math.Min(500, analysisResult.Length))}");
                    System.Windows.MessageBox.Show(
                        "Could not parse prompts from Qwen VL response. Check the activity log for details.",
                        "Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Add prompts to queue
                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;
                for (int i = 0; i < prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, prompts[i], InputImagePath));
                }

                // Set a default PromptJsonFilePath-based name for output folders/filenames
                if (string.IsNullOrEmpty(PromptJsonFilePath))
                {
                    var imageName = Path.GetFileNameWithoutExtension(InputImagePath);
                    PromptJsonFilePath = Path.Combine(
                        Path.GetDirectoryName(InputImagePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"{imageName}-kleinvl.json");
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                CommandManager.InvalidateRequerySuggested();
                AnalysisStatus = $"Added {prompts.Count} prompts to queue";
                AddLog($"Added {prompts.Count} prompts from Qwen VL analysis to queue (total: {QueueItems.Count})");

                // Auto-start processing if not already processing
                if (CanProcessQueue)
                {
                    AddLog("Auto-starting queue processing...");
                    _ = ProcessQueueAsync();
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
                _logger.LogError($"Error analyzing image with Qwen VL: {ex}");
                AnalysisStatus = "Analysis failed";

                var lmStudioUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
                System.Windows.MessageBox.Show(
                    $"Error analyzing image:\n\n{ex.Message}\n\nPlease ensure LM Studio is running at {lmStudioUrl} with a Qwen VL model loaded.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzingImage = false;
            }
        }

        private List<string> ParsePromptsFromAnalysis(string analysisText)
        {
            var prompts = new List<string>();

            // Strategy 1: Split by "Prompt #N:" or "Prompt N:" pattern
            var pattern = @"Prompt\s*#?\s*(\d+)\s*:\s*";
            var matches = Regex.Matches(analysisText, pattern, RegexOptions.IgnoreCase);

            for (int i = 0; i < matches.Count; i++)
            {
                var startPos = matches[i].Index + matches[i].Length;
                var endPos = (i + 1 < matches.Count) ? matches[i + 1].Index : analysisText.Length;

                var promptText = analysisText.Substring(startPos, endPos - startPos).Trim();

                if (!string.IsNullOrWhiteSpace(promptText))
                {
                    prompts.Add(promptText);
                }
            }

            // Strategy 2 (Fallback): Split by "Subject:" occurrences if no "Prompt #N:" found
            if (prompts.Count == 0)
            {
                AddLog("No 'Prompt #N:' labels found, falling back to 'Subject:' delimiter parsing...");
                var subjectPattern = @"(?=Subject\s*:)";
                var segments = Regex.Split(analysisText, subjectPattern, RegexOptions.IgnoreCase);

                foreach (var segment in segments)
                {
                    var trimmed = segment.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.StartsWith("Subject", StringComparison.OrdinalIgnoreCase))
                    {
                        prompts.Add(trimmed);
                    }
                }
            }

            // Strategy 3 (Last resort): Use PromptParser.ExtractPrompts for generic parsing
            if (prompts.Count == 0)
            {
                AddLog("Fallback: Using generic PromptParser.ExtractPrompts...");
                prompts = PromptParser.ExtractPrompts(analysisText);
            }

            AddLog($"Parsed {prompts.Count} prompts from Qwen VL response");
            return prompts;
        }

        // --- Core processing logic ---

        protected override async Task<string> ProcessQueueItemAsync(
            StoryPromptItem item,
            string inputImagePath,
            string? sessionOutputDir,
            string jsonFileName,
            CancellationToken cancellationToken)
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
            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            // Get output images
            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            // Extract the base folder name (without sequential suffix) for image naming
            var folderName = sessionOutputDir != null ? Path.GetFileName(sessionOutputDir) : jsonFileName;
            var baseFolderName = folderName.Contains(" (")
                ? folderName.Substring(0, folderName.LastIndexOf(" ("))
                : folderName;

            // Generate sequential filename: jsonfilename-1.png, jsonfilename-2.png, etc.
            var outputDir = sessionOutputDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName);
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"{baseFolderName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story F image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            // 1. Update the input image (node 148 - LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "148", "image", inputImageName);

            // 2. Update the prompt (node 154 - TextEncodeEditAdvanced)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "154", new Dictionary<string, object>
            {
                { "prompt", promptText },
                { "max_images_allowed", "1" } // Required by TextEncodeEditAdvanced node (must be string)
            });

            // 3. Update Flux2Scheduler steps (node 109)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "109", "steps", Steps);

            // 4. Update SaveImage node (node 157) - set filename prefix with image index and subfolder
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "157", new Dictionary<string, object>
            {
                { "filename_prefix", $"{jsonFileName}-{imageIndex}" },
                { "subfolder", jsonFileName } // Save in subfolder named after JSON file
            });

            // 5. Update ImageScale node (node 115) - set resolution based on orientation
            var newResolution = IsPortraitMode ? PortraitResolution : LandscapeResolution;
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "115", "resolution", newResolution);
            AddLog($"Image resolution set to: {newResolution} (Portrait Mode: {IsPortraitMode})");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId, string jsonFileName, int imageIndex)
        {
            var images = new List<byte[]>();
            HashSet<string> filesBeforeGeneration = new HashSet<string>();

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
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = _imageRetriever.IsComfyUIRemote(_settingsService);

                AddLog($"ComfyUI server: {actualServer}");
                AddLog($"Is remote ComfyUI: {isRemoteComfyUI}");

                // For local ComfyUI, capture existing files before generation
                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                string subfolderPath = string.Empty;
                string[] searchDirs = Array.Empty<string>();

                if (!isRemoteComfyUI && !string.IsNullOrEmpty(comfyUIOutputDir) && Directory.Exists(comfyUIOutputDir))
                {
                    // Check both the subfolder and main output directory
                    subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                    searchDirs = Directory.Exists(subfolderPath)
                        ? new[] { subfolderPath, comfyUIOutputDir }
                        : new[] { comfyUIOutputDir };

                    filesBeforeGeneration = new HashSet<string>(
                        searchDirs.SelectMany(dir => Directory.GetFiles(dir, "*.png"))
                        .Select(Path.GetFileName)
                        .Where(f => f != null)!,
                        StringComparer.OrdinalIgnoreCase
                    );
                    AddLog($"Tracking {filesBeforeGeneration.Count} existing files before generation");
                }

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

                        List<string> imageFiles = new();
                        var expectedPattern = $"{jsonFileName}-{imageIndex}_";

                        // Strategy 1: Use prompt-specific history lookup (most reliable)
                        var promptOutputFiles = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                        imageFiles = promptOutputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
                            .ToList();

                        if (imageFiles.Any())
                        {
                            AddLog($"Found {imageFiles.Count} output file(s) for prompt {promptId} matching pattern {expectedPattern}");
                        }
                        else
                        {
                            // Strategy 2: Fall back to scanning recent history with pattern matching
                            AddLog($"No output files in history for prompt {promptId} matching pattern {expectedPattern}, trying general pattern match...");

                            var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                            AddLog($"Found {outputFiles.Count} potential output files in recent history");

                            imageFiles = outputFiles.Where(f =>
                                f.EndsWith(".png") &&
                                (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
                                .ToList();

                            AddLog($"Looking for pattern: {expectedPattern} (with or without subfolder prefix)");

                            if (!imageFiles.Any())
                            {
                                AddLog($"No matching files found. Available files: {string.Join(", ", outputFiles.Take(5))}");
                            }
                        }

                        // Download the image
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
                    }
                    else
                    {
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

                        // Search in ComfyUI output directory's subfolder AND root (SaveImage node ignores subfolder input)
                        subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                        var primarySearchDirs = Directory.Exists(subfolderPath)
                            ? new[] { subfolderPath, comfyUIOutputDir }
                            : new[] { comfyUIOutputDir };
                        AddLog($"Searching for images in: {string.Join(", ", primarySearchDirs)}");

                        // ComfyUI names files as: {filename_prefix}_00001_.png
                        // The filename_prefix we set is: {jsonFileName}-{imageIndex}
                        var pattern = $"{jsonFileName}-{imageIndex}_*.png";
                        var matchingFiles = primarySearchDirs
                            .Where(Directory.Exists)
                            .SelectMany(dir => Directory.GetFiles(dir, pattern))
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

                            // Find newly created files by comparing with files before generation
                            subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                            searchDirs = Directory.Exists(subfolderPath)
                                ? new[] { subfolderPath, comfyUIOutputDir }
                                : new[] { comfyUIOutputDir };

                            var currentFiles = new HashSet<string>(
                                searchDirs.SelectMany(dir => Directory.GetFiles(dir, "*.png"))
                                .Select(Path.GetFileName)
                                .Where(f => f != null)!,
                                StringComparer.OrdinalIgnoreCase
                            );

                            var newFiles = currentFiles.Except(filesBeforeGeneration).ToList();
                            AddLog($"Found {newFiles.Count} new files since generation started");

                            if (newFiles.Any())
                            {
                                // Get the newest file among the newly created ones (search in subfolder first)
                                subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                                searchDirs = Directory.Exists(subfolderPath)
                                    ? new[] { subfolderPath, comfyUIOutputDir }
                                    : new[] { comfyUIOutputDir };

                                var newFileInfos = newFiles
                                    .SelectMany(f => searchDirs.Select(dir => Path.Combine(dir, f)))
                                    .Where(path => File.Exists(path))
                                    .Select(path => new FileInfo(path))
                                    .OrderByDescending(f => f.CreationTime > f.LastWriteTime ? f.CreationTime : f.LastWriteTime)
                                    .ToList();

                                var newestFile = newFileInfos.First();
                                var fileTime = newestFile.CreationTime > newestFile.LastWriteTime ? newestFile.CreationTime : newestFile.LastWriteTime;
                                AddLog($"Using newest created file: {newestFile.Name} (created/modified: {fileTime})");
                                images.Add(await File.ReadAllBytesAsync(newestFile.FullName));
                            }
                            else
                            {
                                // List all files in the output directory for debugging
                                var allFiles = Directory.GetFiles(comfyUIOutputDir, "*.png")
                                    .Select(Path.GetFileName)
                                    .ToList();
                                AddLog($"Files in output directory: {string.Join(", ", allFiles.Take(10))}");
                            }
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

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private bool _disposed = false;
    }
}
