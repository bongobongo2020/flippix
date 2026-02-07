using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Commands;

namespace FlipPix.UI.ViewModels
{
    public class I2V2AViewModel : INotifyPropertyChanged
    {
        private readonly ComfyUIService _comfyUIService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;

        // Input Image
        private string _inputImagePath = string.Empty;
        private BitmapImage? _inputImageSource;
        private bool _hasInputImage = false;

        // Video Generation Settings
        private string _videoPrompt = "Digital anime girl, reverse cowgirl (facing viewer), large bouncing breasts with extreme jiggle physics, straddling man's hips, her body glistening and slick with sweat and wetness. Extreme upward perspective, camera low. Long, deep penetration. She is heavily bouncing and gripping his cock. She is squirting fluid and slick on every deep thrust.";
        private string _negativePrompt = "low quality, short thrusts, no movement, small penis, face change, realistic, photorealistic, face morph, worst quality, blurry, grainy, noise, jpeg artifacts, watermark, censored, mosaic, signature, text, logo, writing, mutated, deformed, extra limbs, missing limbs, fused fingers, bad hands, bad anatomy, disproportionate, poor lighting, ugly face, sad, crying, bored, pensive, expressionless, looking away, closed eyes, unnecessary clothing, shoes, socks, unsexy, distracting background, out of frame, out of focus, easy pose.";

        // Audio Generation Settings
        private string _audioPrompt = "Wet, gentle throat suction, soft gawk-gawk sounds, slow deepthroat blowjob, subtle nasal breathing, quiet spit bubbles, muffled whimpers, soft gasps caught in the throat, slick mouth movement, warm sloppy sucking, barely-audible chokes, soft wet gliding, restrained moans, continuous slippery throat sounds with breathy edge, distinct muffled thock as the tip hits the back of the throat, occasional deep, sticky gag at full depth";
        private string _audioNegativePrompt = "Robot voice, loud moananimal, very high pitch, metallic sound, screaming, crying, man, male, flat emotion, stuttering,, digital artifacts, static, clipping, harsh noise, out-of-context speech, non-human sounds, distorted frequency, excessive reverb, generic ambient sound, sudden silence, unnecessary echo, microphone feedback";

        // Technical Parameters
        private int _videoWidth = 800;
        private int _videoHeight = 600;
        private int _totalFrames = 125;
        private int _fps = 24;
        private int _steps = 4;
        private int _stepToSwap = 2;
        private double _cfg = 1.0;
        private long _seed = 0;
        private int _crf = 19;
        private int _audioDuration = 5;
        private double _audioCfg = 4.5;
        private int _audioSteps = 25;

        // LoRA Settings
        private Dictionary<string, (bool enabled, double strength, string model)> _availableLoRAs = new()
        {
            { "Bouncing Breasts", (false, 1.0, "wan\\Bouncing Breasts - XL wan 480p .safetensors") },
            { "POV Missionary", (false, 1.0, "wan\\wan_pov_missionary_i2v_v1.1.safetensors") },
            { "Doggy POV", (false, 1.0, "wan\\doggy_pov_9fingers.safetensors") },
            { "Deepthroat Low", (false, 1.0, "wan\\DaSiWa_Wan22_Low_Deepthroat_v11.safetensors") },
            { "Deepthroat High", (false, 1.0, "wan\\DaSiWa_Wan22_High_Deepthroat_v11.safetensors") },
            { "Cumshot Aesthetics", (false, 1.0, "wan\\23High noise-Cumshot Aesthetics.safetensors") },
            { "POV Cowgirl Low", (false, 1.0, "wan\\WAN-2.2-I2V-POV-Cowgirl-LOW-v1.0-fixed.safetensors") },
            { "POV Cowgirl High", (false, 1.0, "wan\\WAN-2.2-I2V-POV-Cowgirl-HIGH-v1.0-fixed.safetensors") },
            { "Missionary Low", (false, 0.8, "wan\\wan2.2_i2v_lownoise_pov_missionary_v1.0.safetensors") },
            { "Missionary High", (false, 0.8, "wan\\wan2.2_i2v_highnoise_pov_missionary_v1.0.safetensors") },
            { "Face Down Ass Up Low", (false, 1.0, "wan\\WAN-2.2-I2V-FaceDownAssUp-LOW-v1.safetensors") },
            { "Face Down Ass Up High", (false, 1.0, "wan\\WAN-2.2-I2V-FaceDownAssUp-HIGH-v1.safetensors") },
            { "Titfuck Paizuri Low", (false, 1.0, "wan\\WAN-2.2-I2V-POV-Titfuck-Paizuri-LOW-v1.0.safetensors") },
            { "Titfuck Paizuri High", (false, 1.0, "wan\\WAN-2.2-I2V-POV-Titfuck-Paizuri-HIGH-v1.0.safetensors") },
            { "Side Deepthroat Low", (false, 1.0, "wan\\wan22-side-deepthroat-12epoc-low-k3nk.safetensors") },
            { "Side Deepthroat High", (true, 1.0, "wan\\wan22-side-deepthroat-54epoc-high-k3nk.safetensors") }
        };

        // Processing State
        private bool _isProcessing = false;
        private string _processingStatus = string.Empty;
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _comfyUIServer = "127.0.0.1";
        private string _comfyUIPort = "8188";
        private string _statusBarMessage = "Ready";
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private System.Threading.CancellationTokenSource? _cancellationTokenSource;

        public event PropertyChangedEventHandler? PropertyChanged;

        public I2V2AViewModel(ComfyUIService comfyUIService, IAppLogger logger, FlipPix.Core.Services.SettingsService settingsService, IServiceProvider? serviceProvider = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;

            LoadSettings();
            InitializeCommands();
        }

        #region Properties

        public string InputImagePath
        {
            get => _inputImagePath;
            set
            {
                if (SetField(ref _inputImagePath, value))
                {
                    HasInputImage = !string.IsNullOrEmpty(value) && File.Exists(value);
                    if (HasInputImage)
                    {
                        LoadInputImage(value);
                    }
                }
            }
        }

        public BitmapImage? InputImageSource
        {
            get => _inputImageSource;
            set => SetField(ref _inputImageSource, value);
        }

        public bool HasInputImage
        {
            get => _hasInputImage;
            set => SetField(ref _hasInputImage, value);
        }

        public string VideoPrompt
        {
            get => _videoPrompt;
            set => SetField(ref _videoPrompt, value);
        }

        public string NegativePrompt
        {
            get => _negativePrompt;
            set => SetField(ref _negativePrompt, value);
        }

        public string AudioPrompt
        {
            get => _audioPrompt;
            set => SetField(ref _audioPrompt, value);
        }

        public string AudioNegativePrompt
        {
            get => _audioNegativePrompt;
            set => SetField(ref _audioNegativePrompt, value);
        }

        public int VideoWidth
        {
            get => _videoWidth;
            set => SetField(ref _videoWidth, value);
        }

        public int VideoHeight
        {
            get => _videoHeight;
            set => SetField(ref _videoHeight, value);
        }

        public int TotalFrames
        {
            get => _totalFrames;
            set => SetField(ref _totalFrames, value);
        }

        public int FPS
        {
            get => _fps;
            set => SetField(ref _fps, value);
        }

        public int Steps
        {
            get => _steps;
            set => SetField(ref _steps, value);
        }

        public int StepToSwap
        {
            get => _stepToSwap;
            set => SetField(ref _stepToSwap, value);
        }

        public double Cfg
        {
            get => _cfg;
            set => SetField(ref _cfg, value);
        }

        public long Seed
        {
            get => _seed;
            set => SetField(ref _seed, value);
        }

        public int CRF
        {
            get => _crf;
            set => SetField(ref _crf, value);
        }

        public int AudioDuration
        {
            get => _audioDuration;
            set => SetField(ref _audioDuration, value);
        }

        public double AudioCfg
        {
            get => _audioCfg;
            set => SetField(ref _audioCfg, value);
        }

        public int AudioSteps
        {
            get => _audioSteps;
            set => SetField(ref _audioSteps, value);
        }

        public Dictionary<string, (bool enabled, double strength, string model)> AvailableLoRAs
        {
            get => _availableLoRAs;
            set
            {
                if (SetField(ref _availableLoRAs, value))
                {
                    OnPropertyChanged(nameof(LoRAItems));
                }
            }
        }

        public List<LoRAItem> LoRAItems => AvailableLoRAs.Select(kvp =>
            new LoRAItem { Name = kvp.Key, Enabled = kvp.Value.enabled, Strength = kvp.Value.strength }).ToList();

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetField(ref _isProcessing, value);
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            set => SetField(ref _processingStatus, value);
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            set => SetField(ref _processingProgress, value);
        }

        public string LogOutput
        {
            get => _logOutput;
            set => SetField(ref _logOutput, value);
        }

        public string ComfyUIServer
        {
            get => _comfyUIServer;
            set => SetField(ref _comfyUIServer, value);
        }

        public string ComfyUIPort
        {
            get => _comfyUIPort;
            set => SetField(ref _comfyUIPort, value);
        }

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            set => SetField(ref _statusBarMessage, value);
        }

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            set => SetField(ref _hasResultVideo, value);
        }

        public string ResultVideoPath
        {
            get => _resultVideoPath;
            set => SetField(ref _resultVideoPath, value);
        }

        #endregion

        #region Commands

        public ICommand? SelectInputImageCommand { get; private set; }
        public ICommand? GenerateVideoCommand { get; private set; }
        public ICommand? CancelGenerationCommand { get; private set; }
        public ICommand? OpenResultFolderCommand { get; private set; }
        public ICommand? PlayResultVideoCommand { get; private set; }
        public ICommand? ClearLogsCommand { get; private set; }
        public ICommand? RefreshConnectionCommand { get; private set; }

        #endregion

        #region Command Implementations

        private void InitializeCommands()
        {
            SelectInputImageCommand = new RelayCommand(SelectInputImage);
            GenerateVideoCommand = new RelayCommand(GenerateVideo, () => HasInputImage && !IsProcessing);
            CancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);
            PlayResultVideoCommand = new RelayCommand(PlayResultVideo, () => HasResultVideo);
            ClearLogsCommand = new RelayCommand(ClearLogs);
            RefreshConnectionCommand = new RelayCommand(RefreshConnection);
        }

        private void SelectInputImage()
        {
            try
            {
                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.webp|All Files (*.*)|*.*",
                    Title = "Select Input Image for Video Generation"
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    InputImagePath = openFileDialog.FileName;
                    StatusBarMessage = $"Image loaded: {Path.GetFileName(InputImagePath)}";
                    _logger.LogInfo($"Input image selected: {InputImagePath}");
                }
            }
            catch (Exception ex)
            {
                StatusBarMessage = $"Error selecting image: {ex.Message}";
                _logger.LogError(ex, "Failed to select input image");
            }
        }

        private async void GenerateVideo()
        {
            if (!HasInputImage)
            {
                StatusBarMessage = "Please select an input image first";
                return;
            }

            try
            {
                IsProcessing = true;
                ProcessingStatus = "Initializing...";
                ProcessingProgress = 0;
                HasResultVideo = false;
                ResultVideoPath = string.Empty;
                LogOutput = string.Empty;
                StatusBarMessage = "Starting video generation...";

                _cancellationTokenSource = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

                LogMessage("Starting I2V2A generation process");
                LogMessage($"Input image: {InputImagePath}");

                // Ensure ComfyUI is connected
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    LogMessage("Connecting to ComfyUI WebSocket...");
                    await _comfyUIService.ConnectAsync();
                    LogMessage("Connected to ComfyUI");
                }
                else
                {
                    LogMessage("ComfyUI already connected");
                }

                // Load the workflow
                var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow", "i2v2a_simple_v2.json");
                if (!File.Exists(workflowPath))
                {
                    throw new FileNotFoundException("I2V2A workflow file not found", workflowPath);
                }

                var workflowJson = await File.ReadAllTextAsync(workflowPath);
                var workflow = JsonDocument.Parse(workflowJson);

                // Upload input image
                ProcessingStatus = "Uploading input image...";
                ProcessingProgress = 10;
                LogMessage("Uploading input image to ComfyUI...");
                var uploadedImageName = await _comfyUIService.UploadImageAsync(InputImagePath);
                LogMessage($"Image uploaded: {uploadedImageName}");

                // Modify workflow with current settings
                ProcessingStatus = "Updating workflow parameters...";
                ProcessingProgress = 20;
                var modifiedWorkflow = ModifyWorkflow(workflow, uploadedImageName);

                // Execute workflow
                ProcessingStatus = "Generating video...";
                ProcessingProgress = 30;
                LogMessage("Executing I2V2A generation workflow...");

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(progressMsg =>
                {
                    if (progressMsg.Data?.Value != null && progressMsg.Data?.Max != null)
                    {
                        var percent = (double)progressMsg.Data.Value / progressMsg.Data.Max * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = 30 + (percent * 0.6); // Scale to 30-90%
                            ProcessingStatus = $"Generating: {progressMsg.Data.Value}/{progressMsg.Data.Max}";
                            StatusBarMessage = $"Generating... {ProcessingProgress:F0}%";
                        });
                    }
                });

                var promptId = await _comfyUIService.ExecuteWorkflowAsync(modifiedWorkflow, progress);

                // Force progress update after workflow completes
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingProgress = 90;
                    ProcessingStatus = "Workflow completed, retrieving video...";
                });

                LogMessage($"Workflow execution completed with prompt ID: {promptId}");

                // Wait and retrieve the output video
                ProcessingStatus = "Retrieving output video...";
                ProcessingProgress = 95;
                LogMessage("Looking for generated video...");

                // Wait for the video to be saved
                await Task.Delay(3000);

                // Get the video from ComfyUI output folder
                var outputVideo = GetOutputVideoFromComfyUI(promptId);

                if (outputVideo != null && File.Exists(outputVideo))
                {
                    ResultVideoPath = outputVideo;
                    HasResultVideo = true;
                    ProcessingProgress = 100;
                    ProcessingStatus = "Complete!";
                    StatusBarMessage = $"Video generation complete - {Path.GetFileName(outputVideo)}";

                    LogMessage("=== Video generation completed successfully ===");
                    LogMessage($"Video saved to: {outputVideo}");

                    // Open the folder containing the result
                    var folder = Path.GetDirectoryName(outputVideo);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = folder,
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    LogMessage("WARNING: No output video found");
                    ProcessingStatus = "No output generated";
                    System.Windows.MessageBox.Show("No output video was generated. Please check the ComfyUI console for errors.", "Warning", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                StatusBarMessage = "Video generation cancelled";
                LogMessage("Generation was cancelled by user");
            }
            catch (Exception ex)
            {
                StatusBarMessage = $"Generation failed: {ex.Message}";
                LogMessage($"Error: {ex.Message}");
                _logger.LogError(ex, "Video generation failed");
            }
            finally
            {
                IsProcessing = false;
                ProcessingStatus = string.Empty;
                ProcessingProgress = 0;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void CancelGeneration()
        {
            _cancellationTokenSource?.Cancel();
            StatusBarMessage = "Cancelling generation...";
        }

        private void OpenResultFolder()
        {
            if (HasResultVideo && !string.IsNullOrEmpty(ResultVideoPath))
            {
                try
                {
                    var folder = Path.GetDirectoryName(ResultVideoPath);
                    if (!string.IsNullOrEmpty(folder))
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
                    StatusBarMessage = $"Error opening folder: {ex.Message}";
                    _logger.LogError(ex, "Failed to open result folder");
                }
            }
        }

        private void PlayResultVideo()
        {
            if (HasResultVideo && !string.IsNullOrEmpty(ResultVideoPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = ResultVideoPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    StatusBarMessage = $"Error playing video: {ex.Message}";
                    _logger.LogError(ex, "Failed to play result video");
                }
            }
        }

        private void ClearLogs()
        {
            LogOutput = string.Empty;
        }

        private async void RefreshConnection()
        {
            try
            {
                StatusBarMessage = "Checking ComfyUI connection...";

                // Parse the server and port from the current input
                var testUrl = $"http://{ComfyUIServer}:{ComfyUIPort}";

                // Test connection using a simple HTTP request
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                try
                {
                    var response = await httpClient.GetAsync($"{testUrl}/system_stats");
                    var isConnected = response.IsSuccessStatusCode;
                    StatusBarMessage = isConnected ? "Connected to ComfyUI" : "Failed to connect to ComfyUI";
                    LogMessage($"Connection test result: {(isConnected ? "Success" : "Failed")}");
                }
                catch (Exception)
                {
                    StatusBarMessage = "Failed to connect to ComfyUI";
                    LogMessage("Connection test result: Failed");
                }
            }
            catch (Exception ex)
            {
                StatusBarMessage = $"Connection check failed: {ex.Message}";
                LogMessage($"Connection test error: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private void LoadSettings()
        {
            // Parse BaseUrl to get server and port
            var baseUrl = _settingsService.Settings.BaseUrl;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                ComfyUIServer = uri.Host;
                ComfyUIPort = uri.Port.ToString();
            }
            else
            {
                ComfyUIServer = "127.0.0.1";
                ComfyUIPort = "8188";
            }
            _logger.LogInfo("I2V2A settings loaded");
        }

        private void LoadInputImage(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                InputImageSource = bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to load input image: {imagePath}");
            }
        }

        private JsonElement ModifyWorkflow(JsonDocument workflow, string uploadedImageName)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.RootElement.GetRawText());

            if (workflowDict == null) return workflow.RootElement;

            // Update the LoadImage node (10) with our uploaded image
            if (workflowDict.ContainsKey("10"))
            {
                var node10 = workflowDict["10"];
                var node10Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node10.GetRawText());
                if (node10Obj != null && node10Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node10Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["image"] = uploadedImageName;
                        node10Obj["inputs"] = inputs;
                        workflowDict["10"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node10Obj));
                    }
                }
            }

            // Update video prompt (node 6)
            if (workflowDict.ContainsKey("6"))
            {
                var node6 = workflowDict["6"];
                var node6Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node6.GetRawText());
                if (node6Obj != null && node6Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node6Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["text"] = VideoPrompt;
                        node6Obj["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node6Obj));
                    }
                }
            }

            // Update negative prompt (node 7)
            if (workflowDict.ContainsKey("7"))
            {
                var node7 = workflowDict["7"];
                var node7Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node7.GetRawText());
                if (node7Obj != null && node7Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node7Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["text"] = NegativePrompt;
                        node7Obj["inputs"] = inputs;
                        workflowDict["7"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node7Obj));
                    }
                }
            }

            // Update audio prompt (node 13)
            if (workflowDict.ContainsKey("13"))
            {
                var node13 = workflowDict["13"];
                var node13Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node13.GetRawText());
                if (node13Obj != null && node13Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node13Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["prompt"] = AudioPrompt;
                        inputs["negative_prompt"] = AudioNegativePrompt;
                        inputs["duration"] = AudioDuration;
                        inputs["steps"] = AudioSteps;
                        inputs["cfg"] = AudioCfg;
                        if (Seed > 0)
                        {
                            inputs["seed"] = Seed;
                        }
                        node13Obj["inputs"] = inputs;
                        workflowDict["13"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node13Obj));
                    }
                }
            }

            // Update video parameters (node 8)
            if (workflowDict.ContainsKey("8"))
            {
                var node8 = workflowDict["8"];
                var node8Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node8.GetRawText());
                if (node8Obj != null && node8Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node8Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["width"] = VideoWidth;
                        inputs["height"] = VideoHeight;
                        inputs["length"] = TotalFrames;
                        // WanImageToVideo doesn't have cfg/steps parameters directly
                        // The sampling is handled internally
                        node8Obj["inputs"] = inputs;
                        workflowDict["8"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node8Obj));
                    }
                }
            }

            // The WanImageToVideo node directly handles the sampling, no separate KSampler needed

            // Update CRF (node 12)
            if (workflowDict.ContainsKey("12"))
            {
                var node12 = workflowDict["12"];
                var node12Obj = JsonSerializer.Deserialize<Dictionary<string, object>>(node12.GetRawText());
                if (node12Obj != null && node12Obj.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(node12Obj["inputs"].ToString()!);
                    if (inputs != null)
                    {
                        inputs["crf"] = CRF;
                        inputs["frame_rate"] = FPS;
                        node12Obj["inputs"] = inputs;
                        workflowDict["12"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(node12Obj));
                    }
                }
            }

            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(workflowDict))!;
        }

        private object ModifyWorkflowInput(string inputName, JsonElement inputValue)
        {
            return inputName switch
            {
                // Video parameters
                "width" when inputValue.ValueKind == JsonValueKind.Number => VideoWidth,
                "height" when inputValue.ValueKind == JsonValueKind.Number => VideoHeight,
                "frames" => TotalFrames,
                "frame_rate" => FPS,
                "steps" => Steps,
                "step_to_swap" => StepToSwap,
                "cfg" => Cfg,
                "seed" when Seed > 0 => Seed,
                "crf" => CRF,

                // Audio parameters
                "duration" => AudioDuration,
                "audio_cfg" => AudioCfg,
                "audio_steps" => AudioSteps,

                // Prompts
                "text" when inputValue.GetString()?.Length > 100 => VideoPrompt,
                "negative_text" or "negative_prompt" when inputValue.GetString()?.Length > 50 => NegativePrompt,
                "audio_prompt" when inputValue.GetString()?.Length > 50 => AudioPrompt,
                "audio_negative_prompt" when inputValue.GetString()?.Length > 50 => AudioNegativePrompt,

                // Image
                "image" => Path.GetFileName(InputImagePath),

                // Default case
                _ => inputValue.ValueKind switch
                {
                    JsonValueKind.String => inputValue.GetString() ?? string.Empty,
                    JsonValueKind.Number => inputValue.GetDouble(),
                    JsonValueKind.True or JsonValueKind.False => inputValue.GetBoolean(),
                    _ => inputValue
                }
            };
        }

        private string? GetOutputVideoFromComfyUI(string promptId)
        {
            try
            {
                var comfyUIOutputDir = _settingsService.Settings.OutputFolderPath;

                if (string.IsNullOrEmpty(comfyUIOutputDir))
                {
                    // Default ComfyUI output directory
                    var comfyUIPath = _settingsService.Settings.ComfyUIFolderPath;
                    if (!string.IsNullOrEmpty(comfyUIPath))
                    {
                        comfyUIOutputDir = Path.Combine(comfyUIPath, "output");
                    }
                    else
                    {
                        // Try common default locations
                        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        comfyUIOutputDir = Path.Combine(appData, "ComfyUI", "output");
                    }
                }

                LogMessage($"Searching for output videos in: {comfyUIOutputDir}");

                if (Directory.Exists(comfyUIOutputDir))
                {
                    // Look for recently created videos (mp4) within the last 2 minutes
                    var videoFiles = Directory.GetFiles(comfyUIOutputDir, "*.mp4", SearchOption.AllDirectories)
                        .Select(f => new FileInfo(f))
                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalMinutes < 2)
                        .OrderByDescending(f => f.LastWriteTime)
                        .ToList();

                    LogMessage($"Found {videoFiles.Count} recent video files");

                    if (videoFiles.Any())
                    {
                        var latestFile = videoFiles.First();
                        LogMessage($"Using latest file: {latestFile.Name} (modified: {latestFile.LastWriteTime})");
                        return latestFile.FullName;
                    }
                    else
                    {
                        LogMessage("WARNING: No recent output videos found");
                        LogMessage("Please check that ComfyUI completed successfully and saved the output");
                    }
                }
                else
                {
                    LogMessage($"ERROR: Output directory not found: {comfyUIOutputDir}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"ERROR retrieving output videos: {ex.Message}");
            }

            return null;
        }

        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogOutput += $"[{timestamp}] {message}\n";
            _logger.LogInfo($"I2V2A: {message}");
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    public class LoRAItem
    {
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public double Strength { get; set; }
    }
}