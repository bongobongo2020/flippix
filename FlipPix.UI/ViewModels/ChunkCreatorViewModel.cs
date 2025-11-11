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

namespace FlipPix.UI.ViewModels;

public partial class ChunkCreatorViewModel : ObservableObject
{
    private readonly ComfyUIService _comfyUIService;
    private readonly IAppLogger _logger;
    private readonly ChunkCreatorService _chunkCreatorService;
    private readonly VideoAnalysisService _videoAnalysisService;
    private readonly WorkflowExecutionService _workflowExecutionService;

    [ObservableProperty]
    private string _videoFilePath = string.Empty;

    [ObservableProperty]
    private string _referenceImage1Path = string.Empty;

    [ObservableProperty]
    private string _referenceImage2Path = string.Empty;

    [ObservableProperty]
    private string _prompt = "Sexy woman in reference image wearing shorts.\nShe is practicing martial art kicks.";

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
    private string _statusBarMessage = "Ready to create video chunks";

    [ObservableProperty]
    private string _videoInfo = string.Empty;

    [ObservableProperty]
    private bool _isProcessing = false;

    [ObservableProperty]
    private bool _canStartProcessing = false;

    [ObservableProperty]
    private bool _canStopProcessing = false;

    [ObservableProperty]
    private ObservableCollection<string> _generatedChunks = new();

    [ObservableProperty]
    private string _chunkProgressStatus = string.Empty;

    [ObservableProperty]
    private int _totalChunks = 0;

    [ObservableProperty]
    private int _completedChunks = 0;

    [ObservableProperty]
    private bool _isJoiningVideo = false;

    [ObservableProperty]
    private string _finalVideoPath = string.Empty;

    [ObservableProperty]
    private string _joiningStatus = string.Empty;

    [ObservableProperty]
    private string _localFinishedVideoPath = string.Empty;

    [ObservableProperty]
    private bool _hasFinishedVideo = false;

    private VideoInfo? _currentVideoInfo;
    private List<string> _completedChunkPaths = new();
    private const string FinishedVideosFolder = "finished-videos";

    public ChunkCreatorViewModel(ComfyUIService comfyUIService, IAppLogger logger, ChunkCreatorService chunkCreatorService, VideoAnalysisService videoAnalysisService, WorkflowExecutionService workflowExecutionService)
    {
        _comfyUIService = comfyUIService;
        _logger = logger;
        _chunkCreatorService = chunkCreatorService;
        _videoAnalysisService = videoAnalysisService;
        _workflowExecutionService = workflowExecutionService;
        
        // Subscribe to chunk creator events
        _chunkCreatorService.ProgressUpdated += OnProgressUpdated;
        _chunkCreatorService.StatusUpdated += OnStatusUpdated;
        _chunkCreatorService.ChunkCompleted += OnChunkCompleted;
        _chunkCreatorService.ChunkProgressUpdated += OnChunkProgressUpdated;
        _chunkCreatorService.ProcessingCompleted += OnProcessingCompleted;
        
        PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoFilePath):
            case nameof(ReferenceImage1Path):
            case nameof(ReferenceImage2Path):
                UpdateCanStartProcessing();
                if (!string.IsNullOrEmpty(VideoFilePath) && File.Exists(VideoFilePath))
                {
                    _ = UpdateVideoInfoAsync();
                }
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
                           !string.IsNullOrEmpty(ReferenceImage1Path) && 
                           !string.IsNullOrEmpty(ReferenceImage2Path) &&
                           File.Exists(VideoFilePath) &&
                           File.Exists(ReferenceImage1Path) &&
                           File.Exists(ReferenceImage2Path);
        
        CanStopProcessing = IsProcessing;
    }

    private async Task UpdateVideoInfoAsync()
    {
        try
        {
            _currentVideoInfo = await _videoAnalysisService.AnalyzeVideoAsync(VideoFilePath);
            
            VideoInfo = $"Duration: {_currentVideoInfo.Duration:mm\\:ss} | " +
                       $"FPS: {_currentVideoInfo.FrameRate:F1} | " +
                       $"Total Frames: {_currentVideoInfo.TotalFrames} | " +
                       $"Resolution: {_currentVideoInfo.Width}x{_currentVideoInfo.Height} | " +
                       $"Orientation: {(_currentVideoInfo.Width > _currentVideoInfo.Height ? "Landscape" : "Portrait")}";
            
            TotalChunks = (int)Math.Ceiling((double)_currentVideoInfo.TotalFrames / 81);
            StatusBarMessage = $"Ready to process {TotalChunks} chunks ({_currentVideoInfo.TotalFrames} frames)";
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to analyze video: {ex.Message}");
            VideoInfo = "Failed to analyze video";
        }
    }

    [RelayCommand]
    private async Task StartProcessingAsync()
    {
        if (!CanStartProcessing || _currentVideoInfo == null) return;

        try
        {
            IsProcessing = true;
            ProcessingStatus = "Starting chunk creation...";
            GeneratedChunks.Clear();
            _completedChunkPaths.Clear();
            CompletedChunks = 0;
            FinalVideoPath = string.Empty;
            LocalFinishedVideoPath = string.Empty;
            HasFinishedVideo = false;
            JoiningStatus = string.Empty;
            
            // Configure ComfyUI settings
            var settings = new ComfyUISettings
            {
                BaseUrl = $"http://{ComfyUIServer}:{ComfyUIPort}"
            };
            
            _logger.LogInfo($"Starting chunk creation for {_currentVideoInfo.TotalFrames} frames");
            
            // Process video in chunks
            var chunks = await _chunkCreatorService.ProcessVideoInChunksAsync(
                VideoFilePath,
                ReferenceImage1Path,
                ReferenceImage2Path,
                Prompt,
                _currentVideoInfo.TotalFrames,
                settings);
            
            if (chunks.Count > 0)
            {
                ProcessingStatus = $"Completed! Generated {chunks.Count} chunks";
                StatusBarMessage = $"Successfully created {chunks.Count} video chunks";
            }
            else
            {
                ProcessingStatus = "Failed to generate chunks";
                StatusBarMessage = "Chunk creation failed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Processing failed: {ex}");
            ProcessingStatus = $"Error: {ex.Message}";
            StatusBarMessage = "Processing failed";
        }
        finally
        {
            IsProcessing = false;
            UpdateCanStartProcessing();
        }
    }

    [RelayCommand]
    private void StopProcessing()
    {
        if (!CanStopProcessing) return;
        
        _chunkCreatorService.StopProcessing();
        ProcessingStatus = "Stopping...";
    }

    [RelayCommand]
    private void SelectVideoFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video files (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|All files (*.*)|*.*",
            Title = "Select Video File"
        };
        
        if (dialog.ShowDialog() == true)
        {
            VideoFilePath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void SelectReferenceImage1()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
            Title = "Select Reference Image 1"
        };
        
        if (dialog.ShowDialog() == true)
        {
            ReferenceImage1Path = dialog.FileName;
        }
    }

    [RelayCommand]
    private void SelectReferenceImage2()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*",
            Title = "Select Reference Image 2"
        };
        
        if (dialog.ShowDialog() == true)
        {
            ReferenceImage2Path = dialog.FileName;
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var outputPath = @"\\proxmox-comfy\wan_vace";
        if (Directory.Exists(outputPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", outputPath);
        }
        else
        {
            StatusBarMessage = "Output folder not found";
        }
    }

    [RelayCommand]
    private void OpenFinishedVideo()
    {
        if (!string.IsNullOrEmpty(LocalFinishedVideoPath) && File.Exists(LocalFinishedVideoPath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LocalFinishedVideoPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to open finished video: {ex.Message}");
                StatusBarMessage = "Failed to open video";
            }
        }
    }

    private async Task CopyFinishedVideoToLocalFolderAsync(string sourcePath)
    {
        try
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                _logger.LogWarning($"Source video not found: {sourcePath}");
                return;
            }

            // Create finished-videos folder in the current directory
            var currentDirectory = Directory.GetCurrentDirectory();
            var finishedVideosPath = Path.Combine(currentDirectory, FinishedVideosFolder);

            if (!Directory.Exists(finishedVideosPath))
            {
                Directory.CreateDirectory(finishedVideosPath);
                _logger.LogInfo($"Created finished-videos folder at: {finishedVideosPath}");
            }

            // Generate destination path with timestamp to avoid overwriting
            var fileName = Path.GetFileName(sourcePath);
            var destinationPath = Path.Combine(finishedVideosPath, fileName);

            // If file already exists, add timestamp
            if (File.Exists(destinationPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);
                fileName = $"{fileNameWithoutExt}_{timestamp}{extension}";
                destinationPath = Path.Combine(finishedVideosPath, fileName);
            }

            _logger.LogInfo($"Copying finished video from {sourcePath} to {destinationPath}");

            // Update UI on UI thread
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                JoiningStatus = "Copying to finished-videos folder...";
            });

            // Copy the file asynchronously
            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            using (var destinationStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await sourceStream.CopyToAsync(destinationStream);
            }

            _logger.LogInfo($"Successfully copied finished video to: {destinationPath}");

            // Update UI on UI thread
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalFinishedVideoPath = destinationPath;
                HasFinishedVideo = true;
                JoiningStatus = "Video saved to finished-videos folder!";
                StatusBarMessage = $"Finished video ready: {fileName}";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to copy finished video: {ex.Message}");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                JoiningStatus = $"Failed to copy video: {ex.Message}";
            });
        }
    }

    private void OnProgressUpdated(object? sender, int progress)
    {
        ProcessingProgress = progress;
    }

    private void OnStatusUpdated(object? sender, string status)
    {
        ProcessingStatus = status;
    }

    private void OnChunkCompleted(object? sender, string chunkPath)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            GeneratedChunks.Add(chunkPath);
            _completedChunkPaths.Add(chunkPath);
            CompletedChunks++;
            ChunkProgressStatus = $"Completed {CompletedChunks}/{TotalChunks} chunks";
        });
    }

    private void OnChunkProgressUpdated(object? sender, (int completed, int total) progress)
    {
        CompletedChunks = progress.completed;
        TotalChunks = progress.total;
        ChunkProgressStatus = $"Progress: {progress.completed}/{progress.total} chunks";
    }

    private async void OnProcessingCompleted(object? sender, bool success)
    {
        ProcessingProgress = success ? 100 : ProcessingProgress;

        if (success && _completedChunkPaths.Count > 0)
        {
            StatusBarMessage = "Chunk creation completed successfully - starting video joining...";
            ProcessingStatus = "Joining video chunks...";

            // Automatically trigger video joining
            await JoinVideoChunksAsync();
        }
        else
        {
            StatusBarMessage = success ? "Chunk creation completed successfully" : "Chunk creation failed";
        }
    }

    private async Task JoinVideoChunksAsync()
    {
        try
        {
            IsJoiningVideo = true;
            JoiningStatus = "Joining video chunks...";

            _logger.LogInfo($"Starting automatic video joining for {_completedChunkPaths.Count} chunks");

            // Subscribe to workflow execution service events
            _workflowExecutionService.StatusUpdated += OnWorkflowStatusUpdated;
            _workflowExecutionService.ProgressUpdated += OnWorkflowProgressUpdated;
            _workflowExecutionService.FinalVideoCompleted += OnFinalVideoCompleted;

            // Call the workflow execution service to join videos
            var finalVideoPath = await _workflowExecutionService.JoinVideoChunksAsync(
                _completedChunkPaths,
                VideoFilePath,
                new System.Threading.CancellationToken());

            if (!string.IsNullOrEmpty(finalVideoPath))
            {
                FinalVideoPath = finalVideoPath;
                JoiningStatus = "Video joining completed successfully!";
                StatusBarMessage = $"Final video created: {Path.GetFileName(finalVideoPath)}";
                _logger.LogInfo($"Automatic video joining completed: {finalVideoPath}");
            }
            else
            {
                JoiningStatus = "Video joining failed";
                StatusBarMessage = "Failed to join video chunks";
                _logger.LogError("Automatic video joining failed - no output path returned");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Automatic video joining failed: {ex.Message}");
            JoiningStatus = $"Joining failed: {ex.Message}";
            StatusBarMessage = $"Video joining failed: {ex.Message}";
        }
        finally
        {
            IsJoiningVideo = false;

            // Unsubscribe from workflow execution service events
            _workflowExecutionService.StatusUpdated -= OnWorkflowStatusUpdated;
            _workflowExecutionService.ProgressUpdated -= OnWorkflowProgressUpdated;
            _workflowExecutionService.FinalVideoCompleted -= OnFinalVideoCompleted;
        }
    }

    private void OnWorkflowStatusUpdated(object? sender, string status)
    {
        JoiningStatus = status;
    }

    private void OnWorkflowProgressUpdated(object? sender, int progress)
    {
        // Could update a joining progress property if needed
        _logger.LogInfo($"Video joining progress: {progress}%");
    }

    private async void OnFinalVideoCompleted(object? sender, string finalVideoPath)
    {
        FinalVideoPath = finalVideoPath;
        _logger.LogInfo($"Final video completed: {finalVideoPath}");

        // Copy the finished video to the local finished-videos folder
        await CopyFinishedVideoToLocalFolderAsync(finalVideoPath);
    }

    public void Cleanup()
    {
        _chunkCreatorService.ProgressUpdated -= OnProgressUpdated;
        _chunkCreatorService.StatusUpdated -= OnStatusUpdated;
        _chunkCreatorService.ChunkCompleted -= OnChunkCompleted;
        _chunkCreatorService.ChunkProgressUpdated -= OnChunkProgressUpdated;
        _chunkCreatorService.ProcessingCompleted -= OnProcessingCompleted;
    }
}