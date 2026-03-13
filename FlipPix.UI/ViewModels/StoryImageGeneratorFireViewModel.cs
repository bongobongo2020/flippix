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
    public partial class StoryImageGeneratorFireViewModel : StoryImageGeneratorBaseViewModel
    {
        private readonly LMStudioService _lmStudioService;

        private bool _settingsVisible = false;
        private bool _useLoRA = true;

        public StoryImageGeneratorFireViewModel(
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

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InputImagePath))
                {
                    (AnalyzeImageWithQwenVLCommand as CommunityToolkit.Mvvm.Input.RelayCommand)?.NotifyCanExecuteChanged();
                }
            };
        }

        // --- Abstract member implementations ---

        protected override string VariantDisplayName => "Story Image Generator Fire";
        protected override string WorkflowTypeName => "StoryImageFire";
        protected override string QueuePersistenceFileName => "story_image_fire_queue.json";
        protected override string OutputFolderName => "story-generator-fire";
        protected override int DefaultSteps => 8;
        protected override double DefaultCfg => 1.0;
        protected override double DefaultDenoise => 1.0;

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

        public bool UseLoRA
        {
            get => _useLoRA;
            set
            {
                if (_useLoRA != value)
                {
                    _useLoRA = value;
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

                var systemPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", "klien-story-10.md");
                if (!File.Exists(systemPromptPath))
                {
                    throw new FileNotFoundException($"System prompt file not found: {systemPromptPath}");
                }
                var systemPrompt = await File.ReadAllTextAsync(systemPromptPath);

                var modelName = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(modelName))
                {
                    System.Windows.MessageBox.Show(
                        "No LM Studio model selected. Please configure LM Studio in the Image Analyzer tab first.",
                        "Model Not Configured", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

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

                var startIndex = QueueItems.Any() ? QueueItems.Max(q => q.Index) + 1 : 1;
                for (int i = 0; i < prompts.Count; i++)
                {
                    QueueItems.Add(CreateQueueItem(startIndex + i, prompts[i], InputImagePath));
                }

                if (string.IsNullOrEmpty(PromptJsonFilePath))
                {
                    var imageName = Path.GetFileNameWithoutExtension(InputImagePath);
                    PromptJsonFilePath = Path.Combine(
                        Path.GetDirectoryName(InputImagePath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        $"{imageName}-firered.json");
                }

                UpdateQueueCountNotifications();
                SaveQueueToFile();
                CommandManager.InvalidateRequerySuggested();
                AnalysisStatus = $"Added {prompts.Count} prompts to queue";
                AddLog($"Added {prompts.Count} prompts from Qwen VL analysis to queue (total: {QueueItems.Count})");

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
            if (!_comfyUIService.IsConnected)
            {
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "firered-image-edit-1.1API.json");
            if (!File.Exists(workflowPath))
            {
                throw new FileNotFoundException($"Workflow file not found: {workflowPath}");
            }

            var workflowJson = await File.ReadAllTextAsync(workflowPath, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            cancellationToken.ThrowIfCancellationRequested();

            var uploadedImageName = await _comfyUIService.UploadImageAsync(inputImagePath);

            var updatedWorkflow = UpdateWorkflowParameters(workflow, uploadedImageName, item.Prompt, item.Index, jsonFileName);

            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            var outputImages = await GetOutputImagesFromComfyUI(promptId, jsonFileName, item.Index);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();

            var folderName = sessionOutputDir != null ? Path.GetFileName(sessionOutputDir) : jsonFileName;
            var baseFolderName = folderName.Contains(" (")
                ? folderName.Substring(0, folderName.LastIndexOf(" ("))
                : folderName;

            var outputDir = sessionOutputDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName);
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"{baseFolderName}-{item.Index}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story Fire image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string inputImageName, string promptText, int imageIndex, string jsonFileName)
        {
            var workflowJson = workflow.GetRawText();

            // Node 143 - LoadImage: input image
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "143", "image", inputImageName);

            // Node 118 - TextEncodeQwenImageEditPlus (Positive): prompt
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "118", "prompt", promptText);

            // Node 117 - TextEncodeQwenImageEditPlus (Negative): negative prompt
            if (!string.IsNullOrEmpty(NegativePrompt))
            {
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "117", "prompt", NegativePrompt);
            }

            // Node 153 - PrimitiveBoolean: LoRA toggle
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "153", "value", UseLoRA);

            // Nodes 155 and 156 - PrimitiveInt: steps (both branches use the same slider value)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "155", "value", Steps);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "156", "value", Steps);

            // Node 130 - KSampler: denoise
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "130", "denoise", Denoise);

            // Node 9 - SaveImage: filename with subfolder embedded in prefix
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "9", "filename_prefix", $"{jsonFileName}/{jsonFileName}-{imageIndex}");

            AddLog($"FireRed workflow: LoRA={UseLoRA}, Steps={Steps}, Denoise={Denoise:F2}");

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

                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                string subfolderPath = string.Empty;
                string[] searchDirs = Array.Empty<string>();

                if (!isRemoteComfyUI && !string.IsNullOrEmpty(comfyUIOutputDir) && Directory.Exists(comfyUIOutputDir))
                {
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

                        List<string> imageFiles = new();
                        var expectedPattern = $"{jsonFileName}-{imageIndex}_";

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

                        subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                        AddLog($"Searching for images in: {subfolderPath}");

                        if (!Directory.Exists(subfolderPath))
                        {
                            AddLog($"WARNING: Subfolder not found: {subfolderPath}");
                            AddLog("Falling back to main output directory...");
                            subfolderPath = comfyUIOutputDir;
                        }

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
