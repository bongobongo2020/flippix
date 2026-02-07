using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Commands;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    public class StoryImageGeneratorFViewModel : StoryImageGeneratorBaseViewModel
    {
        // Image resolution constants
        private const string LandscapeResolution = "1344x768";
        private const string PortraitResolution = "768x1344";

        private bool _settingsVisible = false;
        private bool _isPortraitMode = false; // Default to landscape mode

        public StoryImageGeneratorFViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator)
            : base(comfyUIService, logger, settingsService, workflowCoordinator)
        {
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
            ToggleSettingsVisibilityCommand = new RelayCommand(ToggleSettingsVisibility);
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

        private void ToggleSettingsVisibility()
        {
            SettingsVisible = !SettingsVisible;
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
            AddLog($"Story F image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
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
                        inputs["max_images_allowed"] = "1"; // Required by TextEncodeEditAdvanced node (must be string)
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

            // 4. Update SaveImage node (node 157) - set filename prefix with image index and subfolder
            if (workflowDict.ContainsKey("157"))
            {
                var node157 = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["157"].GetRawText());
                if (node157 != null && node157.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node157["inputs"]));
                    if (inputs != null)
                    {
                        // Use jsonFileName as both prefix and subfolder for organized storage
                        inputs["filename_prefix"] = $"{jsonFileName}-{imageIndex}";
                        inputs["subfolder"] = jsonFileName; // Save in subfolder named after JSON file
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
            HashSet<string> filesBeforeGeneration = new HashSet<string>();

            try
            {
                var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
                var uri = new Uri(baseUrl);
                var actualServer = uri.Host;

                bool isRemoteComfyUI = IsComfyUIRemote(actualServer);

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

                        var outputFiles = await _comfyUIService.HttpClient.GetOutputFilesAsync();
                        AddLog($"Found {outputFiles.Count} potential output files");

                        // ComfyUI names files as: {filename_prefix}_00001_.png
                        // When using subfolder, files may be named like: {subfolder}/{filename_prefix}_00001_.png
                        // The filename_prefix we set is: {jsonFileName}-{imageIndex}
                        var expectedPattern = $"{jsonFileName}-{imageIndex}_";
                        var imageFiles = outputFiles.Where(f =>
                            f.EndsWith(".png") &&
                            (f.Contains(expectedPattern) || f.Contains($"{jsonFileName}/{expectedPattern}")))
                            .ToList();

                        AddLog($"Looking for pattern: {expectedPattern} (with or without subfolder prefix)");

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

                        // Search in ComfyUI output directory's subfolder
                        subfolderPath = Path.Combine(comfyUIOutputDir, jsonFileName);
                        AddLog($"Searching for images in: {subfolderPath}");

                        if (!Directory.Exists(subfolderPath))
                        {
                            AddLog($"WARNING: Subfolder not found: {subfolderPath}");
                            AddLog("Falling back to main output directory...");
                            subfolderPath = comfyUIOutputDir;
                        }

                        // ComfyUI names files as: {filename_prefix}_00001_.png
                        // The filename_prefix we set is: {jsonFileName}-{imageIndex}
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
    }
}
