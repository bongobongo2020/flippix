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
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
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
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, workflowCoordinator, fileDialogService)
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
