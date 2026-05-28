using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.ViewModels
{
    public partial class StoryImageGeneratorAmateurViewModel : StoryImageGeneratorBaseViewModel
    {
        // Amateur LoRA is always enabled
        private const string AmateurLoraName = "amateur_photography_zimage_v1.safetensors";
        private const double AmateurLoraStrength1 = 0.4; // Node 105
        private const double AmateurLoraStrength2 = 0.9; // Node 752

        // Character LoRA settings (optional)
        private ObservableCollection<string> _availableCharacterLoras = new();
        private string _selectedCharacterLora = string.Empty;
        private bool _characterLoraEnabled = false;
        private double _characterLoraStrength = 0.8;

        // Additional denoise setting
        private double _denoise2 = 0.3;

        public StoryImageGeneratorAmateurViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService,
            LoraManager loraManager,
            ComfyUIImageRetriever imageRetriever)
            : base(comfyUIService, logger, settingsService, workflowCoordinator, fileDialogService, loraManager, imageRetriever)
        {
        }

        // --- Abstract member implementations ---

        protected override string VariantDisplayName => "Story Image Generator Amateur";
        protected override string WorkflowTypeName => "StoryImageAmateur";
        protected override string QueuePersistenceFileName => "story_image_amateur_queue.json";
        protected override string OutputFolderName => "story-generator-amateur";
        protected override int DefaultSteps => 9;
        protected override double DefaultCfg => 1.0;
        protected override double DefaultDenoise => 0.5;

        // --- Virtual overrides ---

        protected override bool ShowCompletionMessageBox => true;

        // --- Variant-specific initialization ---

        protected override void InitializeVariant()
        {
            RefreshLorasCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(RefreshLoras);
            LoadAvailableCharacterLoras();
        }

        // --- Variant-specific properties ---

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

        public ICommand RefreshLorasCommand { get; private set; } = null!;

        private void RefreshLoras()
        {
            LoadAvailableCharacterLoras();
            AddLog("Refreshed character LoRA list");
        }

        // --- LoRA loading ---

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
            var progress = CreateProgressReporter(item);
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);

            // Get output images
            var outputImages = await GetOutputImagesFromComfyUI(promptId);
            if (!outputImages.Any())
            {
                throw new InvalidOperationException("No output images were generated");
            }

            var outputImage = outputImages.First();
            var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", OutputFolderName);
            Directory.CreateDirectory(outputDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputPath = Path.Combine(outputDir, $"story_amateur_{item.Index:D2}_{timestamp}.png");

            await File.WriteAllBytesAsync(outputPath, outputImage);
            await LocalCopyService.CopyImageAsync(outputPath);
            AddLog($"Story image #{item.Index} saved: {outputPath} ({outputImage.Length} bytes)");
            return outputPath;
        }

        private JsonElement UpdateWorkflowParameters(JsonElement workflow, string promptText)
        {
            var workflowJson = workflow.GetRawText();

            // Add the photographer prefix to all prompts
            const string promptPrefix = "A photo taken by the photographer Deedeemegadoodo, raw, unedited, ";
            string fullPrompt = promptPrefix + promptText;

            // 1. Update positive prompt (node 6)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "6", "text", fullPrompt);

            // 2. Update negative prompt (node 7)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "7", "text", NegativePrompt);

            // 3. Update seed (node 28) - max value is 2^50 (1125899906842624)
            long maxSeed = 1125899906842624;
            var random = new Random();
            byte[] bytes = new byte[8];
            random.NextBytes(bytes);
            long seed = Math.Abs(BitConverter.ToInt64(bytes, 0) % maxSeed);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "28", "seed", seed);

            // 4. Update ClownsharKSampler settings (node 582)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "582", new Dictionary<string, object>
            {
                { "denoise", Denoise },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 5. Update second ClownsharKSampler settings (node 620)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "620", new Dictionary<string, object>
            {
                { "denoise", Denoise2 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 6. Update KSampler settings (node 754)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "754", new Dictionary<string, object>
            {
                { "denoise", 0.9 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 7. Update KSampler settings (node 768)
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "768", new Dictionary<string, object>
            {
                { "denoise", 1.0 },
                { "steps", Steps },
                { "cfg", Cfg }
            });

            // 8. Update LoRA strengths
            // Node 105 - amateur photography LoRA (always applied)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "105", "strength_model", AmateurLoraStrength1);
            // Node 752 - amateur photography LoRA second instance (always applied)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "752", "strength_model", AmateurLoraStrength2);

            // 9. Update character LoRA (node 760) - always set a valid LoRA to avoid validation errors
            if (CharacterLoraEnabled && !string.IsNullOrEmpty(SelectedCharacterLora) && SelectedCharacterLora != "No LoRAs available")
            {
                AddLog($"Setting character LoRA: zimage\\{SelectedCharacterLora}.safetensors with strength {CharacterLoraStrength}");
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "760", new Dictionary<string, object>
                {
                    { "lora_name", $"zimage\\{SelectedCharacterLora}.safetensors" },
                    { "strength_model", CharacterLoraStrength }
                });
            }
            else
            {
                // Use amateur LoRA with minimal strength as fallback (prevents invalid LoRA errors)
                AddLog($"Using fallback LoRA: zimage\\{AmateurLoraName} with strength 0.0");
                WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "760", new Dictionary<string, object>
                {
                    { "lora_name", $"zimage\\{AmateurLoraName}" },
                    { "strength_model", 0.0 }
                });
            }

            // 10. Remove problematic metadata/watermark nodes that reference non-existent image (nodes 107, 109, 747, 748, 749, 751)
            AddLog("Removing metadata and watermark nodes to prevent file loading errors");
            RemoveNodesFromWorkflow(ref workflowJson, new[] { "107", "109", "747", "748", "749", "751" });

            // 11. Update latent image dimensions
            UpdateLatentDimensions(ref workflowJson, "46", 576, 416);
            UpdateLatentDimensions(ref workflowJson, "693", 208, 288);
            UpdateLatentDimensions(ref workflowJson, "758", 416, 576);
            UpdateLatentDimensions(ref workflowJson, "772", 1248, 1728);

            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        private void UpdateLatentDimensions(ref string workflowJson, string nodeId, int width, int height)
        {
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, nodeId, new Dictionary<string, object>
            {
                { "width", width },
                { "height", height }
            });
        }

        private void RemoveNodesFromWorkflow(ref string workflowJson, string[] nodeIds)
        {
            try
            {
                var workflow = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflowJson);
                if (workflow != null)
                {
                    foreach (var nodeId in nodeIds)
                    {
                        if (workflow.ContainsKey(nodeId))
                        {
                            workflow.Remove(nodeId);
                            AddLog($"Removed node {nodeId} from workflow");
                        }
                    }
                    workflowJson = JsonSerializer.Serialize(workflow, new JsonSerializerOptions { WriteIndented = false });
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR removing nodes from workflow: {ex.Message}");
            }
        }

        private async Task<List<byte[]>> GetOutputImagesFromComfyUI(string promptId)
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

                        // The workflow saves to ZImage folder with "AmateurImage" prefix
                        var searchDirs = new List<string>();

                        // Prioritize ZImage folder
                        var zimageDir = Path.Combine(comfyUIOutputDir, "ZImage");
                        if (Directory.Exists(zimageDir))
                        {
                            searchDirs.Add(zimageDir);
                            AddLog("Added ZImage folder to search directories");
                        }

                        // Also check main output folder as fallback
                        searchDirs.Add(comfyUIOutputDir);

                        // Also check date folders as fallback
                        try
                        {
                            var dateFolders = Directory.GetDirectories(comfyUIOutputDir)
                                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                                .Take(3);

                            foreach (var dateDir in dateFolders)
                            {
                                searchDirs.Add(dateDir);
                            }
                        }
                        catch { }

                        AddLog($"Searching in {searchDirs.Count} directories for output images");

                        foreach (var searchDir in searchDirs)
                        {
                            var dirName = Path.GetFileName(searchDir);
                            // Look for AmateurImage pattern in ZImage folder, or any recent PNG elsewhere
                            var pattern = dirName.Equals("ZImage", StringComparison.OrdinalIgnoreCase) ? "AmateurImage*.png" : "*.png";

                            var recentFiles = Directory.GetFiles(searchDir, pattern)
                                .Select(f => new FileInfo(f))
                                .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                            if (recentFiles.Any())
                            {
                                AddLog($"Found {recentFiles.Count} recent PNG files in: {dirName}");
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
                                    var dirName = Path.GetFileName(searchDir);
                                    var pattern = dirName.Equals("ZImage", StringComparison.OrdinalIgnoreCase) ? "AmateurImage*.png" : "*.png";

                                    var olderFiles = Directory.GetFiles(searchDir, pattern)
                                        .Select(f => new FileInfo(f))
                                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 10)
                                        .OrderByDescending(f => f.LastWriteTime)
                                        .ToList();

                                    if (olderFiles.Any())
                                    {
                                        AddLog($"Fallback: Found {olderFiles.Count} PNG files in last 10 minutes in: {dirName}");
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

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                // Clear additional collections
                AvailableCharacterLoras?.Clear();

                // Clear additional string properties
                _selectedCharacterLora = string.Empty;

                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private bool _disposed = false;
    }
}
