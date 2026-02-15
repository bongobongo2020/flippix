using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
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
    public partial class StoryImageGeneratorQViewModel : StoryImageGeneratorBaseViewModel
    {
        private readonly LMStudioService _lmStudioService;
        private bool _settingsVisible = false;

        public StoryImageGeneratorQViewModel(
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

        protected override string VariantDisplayName => "Story Image Generator Q";
        protected override string WorkflowTypeName => "StoryImageQ";
        protected override string QueuePersistenceFileName => "story_image_q_queue.json";
        protected override string OutputFolderName => "story-generator-q";
        protected override int DefaultSteps => 8;
        protected override double DefaultCfg => 1.0;
        protected override double DefaultDenoise => 0.98;

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

                // Read system prompt from file
                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "qwen2512.md");
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

                // Add prompts to queue (same as LoadPromptsAsync)
                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;
                for (int i = 0; i < prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, prompts[i], InputImagePath));
                }

                // Set a default PromptJsonFilePath-based name for output folders/filenames
                // when prompts come from Qwen VL analysis instead of a JSON file
                if (string.IsNullOrEmpty(PromptJsonFilePath))
                {
                    // Use input image filename as the session name (e.g. "character1" from "character1.png")
                    var imageName = Path.GetFileNameWithoutExtension(InputImagePath);
                    PromptJsonFilePath = Path.Combine(
                        Path.GetDirectoryName(InputImagePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"{imageName}-qwenvl.json");
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                CommandManager.InvalidateRequerySuggested();
                AnalysisStatus = $"Added {prompts.Count} prompts to queue";
                AddLog($"Added {prompts.Count} prompts from Qwen VL analysis to queue (total: {QueueItems.Count})");

                // Auto-start processing if not already processing (same as LoadPromptsAsync)
                if (CanProcessQueue)
                {
                    AddLog("Auto-starting queue processing...");
                    _ = ProcessQueueAsync(); // Fire and forget
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR analyzing image: {ex.Message}");
                _logger.LogError($"Error analyzing image with Qwen VL: {ex}");
                AnalysisStatus = "Analysis failed";

                var lmStudioUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://localhost:1234";
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

            // Split by "Prompt #N:" or "Prompt N:" pattern
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

            // Load workflow (RapidEditAIO-API.json - Qwen Rapid Edit workflow)
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "RapidEditAIO-API.json");
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

            // Create output directory with folder named after the JSON filename
            var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName, jsonFileName);
            Directory.CreateDirectory(baseOutputDir);

            // Generate filename using prompt index (all images in the same folder)
            var outputPath = Path.Combine(baseOutputDir, $"{jsonFileName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            AddLog($"Story Q image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            // 1. Update the input image (node 213 - LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "213", "image", inputImageName);

            // 2. Update the positive prompt (node 153 - TextEncodeQwenImageEditPlus)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "153", "prompt", promptText);

            // 3. Update the negative prompt (node 154 - TextEncodeQwenImageEditPlus)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "154", "prompt", NegativePrompt);

            // 4. Update KSampler settings (node 3)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "3", new Dictionary<string, object>
            {
                { "steps", Steps },
                { "cfg", Cfg },
                { "denoise", Denoise }
            });

            // 5. Update ModelSamplingAuraFlow shift (node 145)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "145", "shift", 3.1);

            // 6. Update SaveImage filename prefix (node 218) to use single folder named after JSON file
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "218", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{imageIndex}");

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId, string jsonFileName, int imageIndex)
        {
            var images = new List<byte[]>();

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

                        // Look for specific filename pattern: jsonfilename/jsonfilename-{index}_00001.png
                        var expectedPattern = $"{jsonFileName}/{jsonFileName}-{imageIndex}_";
                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            (f.StartsWith(expectedPattern) || f.Contains($"{jsonFileName}-{imageIndex}")))
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

                        // Search in the single folder named after the JSON file
                        var subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);

                        AddLog($"Searching for images in folder: {subfolderPath}");

                        if (Directory.Exists(subfolderPath))
                        {
                            // Look for files matching the pattern: jsonfilename-index_00001.png
                            var pattern = $"{jsonFileName}-{imageIndex}_*.png";
                            var matchingFiles = Directory.GetFiles(subfolderPath, pattern)
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
                                // List all files in the subfolder for debugging
                                var allFiles = Directory.GetFiles(subfolderPath, "*.png")
                                    .Select(Path.GetFileName)
                                    .ToList();
                                AddLog($"Files in subfolder: {string.Join(", ", allFiles)}");
                            }
                        }
                        else
                        {
                            AddLog($"Subfolder does not exist: {subfolderPath}");

                            // Fallback: search in the root output directory
                            var recentFiles = Directory.GetFiles(comfyUIOutputDir, "*.png")
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Fallback: Found {recentFiles.Count} recent PNG files in root output directory");
                                var latestFile = recentFiles.First();
                                AddLog($"Using fallback file: {latestFile.Name}");
                                images.Add(await File.ReadAllBytesAsync(latestFile.FullName));
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
