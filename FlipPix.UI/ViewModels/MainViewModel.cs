using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.IO;
using FlipPix.Core.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Services;
using System.ComponentModel;
using System.Net.Http;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.WebSocket;

namespace FlipPix.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ComfyUIService _comfyUIService;
    private readonly IAppLogger _logger;
    private readonly WorkflowExecutionService _workflowExecutionService;
    private readonly VideoAnalysisService _videoAnalysisService;

    [ObservableProperty]
    private string _videoFilePath = string.Empty;

    [ObservableProperty]
    private string _styleImagePath = string.Empty;

    [ObservableProperty]
    private string _faceImagePath = string.Empty;

    [ObservableProperty]
    private string _comfyUIServer = "192.168.1.237";

    [ObservableProperty]
    private string _comfyUIPort = "8188";

    [ObservableProperty]
    private string _connectionStatus = "Not connected";

    [ObservableProperty]
    private string _processingStatus = "Ready";

    [ObservableProperty]
    private double _processingProgress = 0.0;

    [ObservableProperty]
    private string _progressPercentage = "0%";

    [ObservableProperty]
    private string _logOutput = string.Empty;

    [ObservableProperty]
    private string _statusBarMessage = "Ready to process videos";

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");

    [ObservableProperty]
    private string _videoInfo = string.Empty;

    [ObservableProperty]
    private bool _isProcessing = false;

    [ObservableProperty]
    private bool _canStartProcessing = false;

    [ObservableProperty]
    private bool _canStopProcessing = false;

    [ObservableProperty]
    private ObservableCollection<WorkflowStepViewModel> _workflowSteps = new();

    [ObservableProperty]
    private string _outputVideoPath = string.Empty;

    [ObservableProperty]
    private bool _hasOutputVideo = false;

    [ObservableProperty]
    private bool _useIncrementalProcessing = true;

    [ObservableProperty]
    private ObservableCollection<string> _generatedVideoChunks = new();

    [ObservableProperty]
    private string _chunkProgressStatus = string.Empty;


    private VideoInfo? _currentVideoInfo;

    public MainViewModel(ComfyUIService comfyUIService, IAppLogger logger, WorkflowExecutionService workflowExecutionService, VideoAnalysisService videoAnalysisService)
    {
        _comfyUIService = comfyUIService;
        _logger = logger;
        _workflowExecutionService = workflowExecutionService;
        _videoAnalysisService = videoAnalysisService;
        
        // Subscribe to workflow execution events
        _workflowExecutionService.ProgressUpdated += OnWorkflowProgressUpdated;
        _workflowExecutionService.StatusUpdated += OnWorkflowStatusUpdated;
        _workflowExecutionService.ExecutionCompleted += OnWorkflowExecutionCompleted;
        _workflowExecutionService.VideoOutputPathUpdated += OnVideoOutputPathUpdated;
        _workflowExecutionService.FinalVideoCompleted += OnFinalVideoCompleted;
        
        InitializeWorkflowSteps();
        StartTimeUpdater();
        
        PropertyChanged += OnPropertyChanged;
    }

    private void InitializeWorkflowSteps()
    {
        WorkflowSteps.Clear();
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "Upload Files", StatusColor = "#6C757D" });
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "Configure Workflow", StatusColor = "#6C757D" });
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "Start Processing", StatusColor = "#6C757D" });
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "AI Enhancement", StatusColor = "#6C757D" });
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "Video Generation", StatusColor = "#6C757D" });
        WorkflowSteps.Add(new WorkflowStepViewModel { StepName = "Join Video Chunks", StatusColor = "#6C757D" });
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoFilePath):
            case nameof(StyleImagePath):
            case nameof(FaceImagePath):
                UpdateCanStartProcessing();
                break;
            case nameof(ProcessingProgress):
                ProgressPercentage = $"{ProcessingProgress:F1}%";
                break;
        }
    }

    private void UpdateCanStartProcessing()
    {
        CanStartProcessing = !IsProcessing && 
                           !string.IsNullOrEmpty(VideoFilePath) && 
                           !string.IsNullOrEmpty(StyleImagePath) && 
                           !string.IsNullOrEmpty(FaceImagePath);
        
        CanStopProcessing = IsProcessing;
    }


    private void StartTimeUpdater()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += (s, e) => CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        timer.Start();
    }

    public void UpdateVideoInfo(VideoInfo videoInfo)
    {
        _currentVideoInfo = videoInfo;
        bool isLandscape = videoInfo.Width > videoInfo.Height;
        string orientation = isLandscape ? "Landscape" : "Portrait";
        string targetResolution = isLandscape ? "1024x576" : "576x1024";
        
        VideoInfo = $"Duration: {videoInfo.Duration.ToString(@"mm\:ss")} | " +
                   $"Resolution: {videoInfo.Width}x{videoInfo.Height} ({orientation}) | " +
                   $"Output: {targetResolution} | " +
                   $"Codec: {videoInfo.Codec} | " +
                   $"Size: {videoInfo.FileSizeBytes / (1024.0 * 1024.0):F1} MB | " +
                   $"Total Frames: {videoInfo.TotalFrames:N0}";
    }

    public void AddLogMessage(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        LogOutput += $"[{timestamp}] {message}\n";
        
        StatusBarMessage = message;
    }

    public void ClearLog()
    {
        LogOutput = string.Empty;
    }

    public void UpdateWorkflowStep(int stepIndex, string status)
    {
        if (stepIndex >= 0 && stepIndex < WorkflowSteps.Count)
        {
            var step = WorkflowSteps[stepIndex];
            step.StatusColor = status switch
            {
                "active" => "#007BFF",
                "completed" => "#28A745",
                "error" => "#DC3545",
                _ => "#6C757D"
            };
        }
    }

    public async Task TestConnectionAsync()
    {
        try
        {
            ConnectionStatus = "Testing connection...";
            AddLogMessage("Testing ComfyUI connection...");
            
            // Create a direct HTTP client to test the specific URL
            var testUrl = $"http://{ComfyUIServer}:{ComfyUIPort}";
            
            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(testUrl);
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            AddLogMessage($"Testing connection to {testUrl}/system_stats");
            var response = await httpClient.GetAsync("/system_stats");
            
            if (response.IsSuccessStatusCode)
            {
                ConnectionStatus = "✅ Connected";
                AddLogMessage($"Successfully connected to ComfyUI at {ComfyUIServer}:{ComfyUIPort}");
            }
            else
            {
                ConnectionStatus = "❌ Connection failed";
                AddLogMessage($"Connection failed with status: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = "❌ Connection failed";
            AddLogMessage($"Connection failed: {ex.Message}");
            _logger.LogError($"ComfyUI connection test failed: {ex}");
        }
    }

    public async Task StartProcessingAsync()
    {
        try
        {
            IsProcessing = true;
            ProcessingStatus = "Processing...";
            ProcessingProgress = 0;
            OutputVideoPath = string.Empty;
            HasOutputVideo = false;
            GeneratedVideoChunks.Clear();
            ChunkProgressStatus = string.Empty;
            
            AddLogMessage("Starting video processing workflow...");
            UpdateWorkflowStep(0, "active");
            
            // Execute real workflow with UI settings
            AddLogMessage($"Executing workflow with ComfyUI at {ComfyUIServer}:{ComfyUIPort}");
            
            // Update ComfyUI settings to use UI values
            var settings = new ComfyUISettings 
            { 
                BaseUrl = $"http://{ComfyUIServer}:{ComfyUIPort}",
                ConnectionTimeout = 120000, // 2 minutes for connection
                MaxRetries = 3,
                RetryDelayMilliseconds = 2000
            };
            
            bool success;
            
            if (UseIncrementalProcessing && _currentVideoInfo != null)
            {
                AddLogMessage($"Using incremental processing: {_currentVideoInfo.TotalFrames} total frames in {Math.Ceiling(_currentVideoInfo.TotalFrames / 81.0)} chunks");
                
                // Subscribe to all videos completed event
                _workflowExecutionService.AllVideosCompleted += OnAllVideosCompleted;
                
                try
                {
                    success = await _workflowExecutionService.ExecuteWorkflowIncrementalAsync(
                        VideoFilePath, 
                        StyleImagePath, 
                        FaceImagePath,
                        _currentVideoInfo.TotalFrames,
                        settings);
                }
                finally
                {
                    _workflowExecutionService.AllVideosCompleted -= OnAllVideosCompleted;
                }
            }
            else
            {
                AddLogMessage("Using standard single-pass processing");
                success = await _workflowExecutionService.ExecuteWorkflowAsync(
                    VideoFilePath, 
                    StyleImagePath, 
                    FaceImagePath,
                    settings);
            }
            
            if (success)
            {
                ProcessingStatus = "Completed";
                ProcessingProgress = 100;
                AddLogMessage("Video processing completed successfully!");
                UpdateWorkflowStep(5, "completed");
            }
            else
            {
                ProcessingStatus = "Failed";
                AddLogMessage("Video processing failed.");
                ResetWorkflowSteps();
            }
        }
        catch (Exception ex)
        {
            ProcessingStatus = "Error";
            AddLogMessage($"Processing failed: {ex.Message}");
            _logger.LogError($"Video processing failed: {ex}");
            ResetWorkflowSteps();
        }
        finally
        {
            IsProcessing = false;
            UpdateCanStartProcessing();
        }
    }

    public void StopProcessing()
    {
        _workflowExecutionService.StopExecution();
        IsProcessing = false;
        ProcessingStatus = "Stopped";
        AddLogMessage("Processing stopped by user");
        ResetWorkflowSteps();
        UpdateCanStartProcessing();
    }


    private void OnWorkflowProgressUpdated(object? sender, int progress)
    {
        ProcessingProgress = progress;
    }

    private void OnWorkflowStatusUpdated(object? sender, string status)
    {
        ProcessingStatus = status;
        AddLogMessage(status);
        
        // Update chunk progress status for incremental processing
        if (status.Contains("chunk") && status.Contains("frames"))
        {
            ChunkProgressStatus = status;
        }
        
        // Update workflow steps based on status
        if (status.Contains("Uploading"))
            UpdateWorkflowStep(0, "active");
        else if (status.Contains("Loading") || status.Contains("Configuring"))
            UpdateWorkflowStep(1, "active");
        else if (status.Contains("Executing"))
            UpdateWorkflowStep(2, "active");
        else if (status.Contains("Processing"))
            UpdateWorkflowStep(3, "active");
        else if (status.Contains("Joining") || status.Contains("Step 2"))
            UpdateWorkflowStep(5, "active");
        else if (status.Contains("ComfyUI enhancement") || status.Contains("Step 3") || status.Contains("upscaling") || status.Contains("interpolation"))
            UpdateWorkflowStep(6, "active");
    }

    private void OnWorkflowExecutionCompleted(object? sender, bool success)
    {
        if (success)
        {
            for (int i = 0; i < WorkflowSteps.Count; i++)
            {
                UpdateWorkflowStep(i, "completed");
            }
        }
    }

    private void ResetWorkflowSteps()
    {
        for (int i = 0; i < WorkflowSteps.Count; i++)
        {
            UpdateWorkflowStep(i, "#6C757D"); // Reset to gray
        }
    }

    private void OnVideoOutputPathUpdated(object? sender, string outputPath)
    {
        OutputVideoPath = outputPath;
        HasOutputVideo = !string.IsNullOrEmpty(outputPath);
        
        // Add to chunks list if using incremental processing
        if (UseIncrementalProcessing && !string.IsNullOrEmpty(outputPath))
        {
            GeneratedVideoChunks.Add(outputPath);
            AddLogMessage($"✅ Video chunk {GeneratedVideoChunks.Count} generated: {Path.GetFileName(outputPath)}");
        }
        else
        {
            AddLogMessage($"✅ Video generated: {outputPath}");
        }
        
        if (HasOutputVideo)
        {
            UpdateWorkflowStep(4, "completed");
            if (!UseIncrementalProcessing)
            {
                UpdateWorkflowStep(6, "active"); // Completion step for single processing
            }
        }
    }

    private void OnAllVideosCompleted(object? sender, List<string> videoPaths)
    {
        if (videoPaths.Count > 0)
        {
            AddLogMessage($"✅ All video chunks completed! Generated {videoPaths.Count} videos:");
            foreach (var path in videoPaths)
            {
                AddLogMessage($"  - {Path.GetFileName(path)}");
            }
            
            // Set the first video as the main output for display
            if (videoPaths.Count > 0)
            {
                OutputVideoPath = videoPaths[0];
                HasOutputVideo = true;
            }
        }
    }

    private void OnFinalVideoCompleted(object? sender, string finalVideoPath)
    {
        AddLogMessage($"🎉 Final seamless video created: {Path.GetFileName(finalVideoPath)}");
        AddLogMessage($"Processing completed successfully!");
        
        // Update the main output path to the final joined video
        OutputVideoPath = finalVideoPath;
        HasOutputVideo = true;
        
        // Mark joining step as completed - this is now the final step
        UpdateWorkflowStep(5, "completed"); // Join Video Chunks
        
        ChunkProgressStatus = $"Final video: {Path.GetFileName(finalVideoPath)}";
    }

}

public partial class WorkflowStepViewModel : ObservableObject
{
    [ObservableProperty]
    private string _stepName = string.Empty;

    [ObservableProperty]
    private string _statusColor = "#6C757D";
}