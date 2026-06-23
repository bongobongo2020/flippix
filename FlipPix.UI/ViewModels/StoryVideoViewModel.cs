using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        // Available workflows (for new batch configuration)
        private readonly ObservableCollection<WorkflowInfo> _allWorkflows = new();
        private int _selectedWorkflowIndex = 0;

        // Pending batch state (what the user has staged but not yet queued)
        private List<string> _pendingPrompts = new();
        private string _pendingBatchName = string.Empty;

        // Batch queue
        private readonly ObservableCollection<VideoBatch> _batchQueue = new();
        private Task? _queueProcessorTask;
        private CancellationTokenSource? _cancellationTokenSource;

        // UI state
        private bool _isProcessing = false;
        private string _processingStatus = "Ready";
        private double _processingProgress = 0;
        private string _logOutput = string.Empty;
        private string _statusBarMessage = "Ready — load prompts and configure a batch to begin";
        private bool _hasResultVideo = false;
        private string _resultVideoPath = string.Empty;
        private string _promptsFolderPath = string.Empty;

        public StoryVideoViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IFileDialogService fileDialogService)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            LoadPromptsFolderFromSettings();
            LoadWorkflows();

            LoadPromptsCommand = new RelayCommand(LoadPrompts);
            _addToBatchQueueCommand = new RelayCommand(AddToBatchQueue, () => _pendingPrompts.Count > 0 && SelectedWorkflow != null);
            _removeBatchCommand = new RelayCommand<VideoBatch>(RemoveBatch);
            _clearCompletedBatchesCommand = new RelayCommand(ClearCompletedBatches,
                () => _batchQueue.Any(b => b.Status == "Done" || b.Status == "Failed"));
            _cancelGenerationCommand = new RelayCommand(CancelGeneration, () => IsProcessing);
            _openResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResultVideo);

            AddLog("Story Video Generator initialized");
        }

        // ---------------------------------------------------------------
        // Workflow properties (for configuring a new batch)
        // ---------------------------------------------------------------

        public ObservableCollection<string> WorkflowNames { get; } = new();

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
                    OnPropertyChanged(nameof(SelectedWorkflow));
                    _addToBatchQueueCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string SelectedWorkflowName => _allWorkflows.Count > 0 && _selectedWorkflowIndex < _allWorkflows.Count
            ? _allWorkflows[_selectedWorkflowIndex].Name
            : string.Empty;

        public WorkflowInfo? SelectedWorkflow => _allWorkflows.Count > 0
            ? _allWorkflows[Math.Min(_selectedWorkflowIndex, _allWorkflows.Count - 1)]
            : null;

        // ---------------------------------------------------------------
        // Pending batch properties
        // ---------------------------------------------------------------

        public string PendingBatchSummary => _pendingPrompts.Count > 0
            ? $"{_pendingPrompts.Count} prompts loaded from: {_pendingBatchName}"
            : "No prompts loaded — click 'Load Prompts from File'";

        public bool HasPendingPrompts => _pendingPrompts.Count > 0;

        // ---------------------------------------------------------------
        // Queue
        // ---------------------------------------------------------------

        public ObservableCollection<VideoBatch> BatchQueue => _batchQueue;

        public int QueuedBatchCount => _batchQueue.Count(b => b.Status == "Queued");

        public bool HasBatchItems => _batchQueue.Count > 0;
        public bool HasNoBatchItems => _batchQueue.Count == 0;

        // ---------------------------------------------------------------
        // Processing state
        // ---------------------------------------------------------------

        public bool IsProcessing
        {
            get => _isProcessing;
            private set
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnPropertyChanged();
                    _cancelGenerationCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        public string ProcessingStatus
        {
            get => _processingStatus;
            private set { _processingStatus = value; OnPropertyChanged(); }
        }

        public double ProcessingProgress
        {
            get => _processingProgress;
            private set
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
            private set { _logOutput = value; OnPropertyChanged(); }
        }

        public string StatusBarMessage
        {
            get => _statusBarMessage;
            private set { _statusBarMessage = value; OnPropertyChanged(); }
        }

        public bool HasResultVideo
        {
            get => _hasResultVideo;
            private set
            {
                _hasResultVideo = value;
                OnPropertyChanged();
                _openResultFolderCommand?.NotifyCanExecuteChanged();
            }
        }

        public string ResultVideoPath
        {
            get => _resultVideoPath;
            private set { _resultVideoPath = value; OnPropertyChanged(); }
        }

        public string PromptsFolderPath
        {
            get => _promptsFolderPath;
            private set { _promptsFolderPath = value; OnPropertyChanged(); SavePromptsFolderToSettings(); }
        }

        // ---------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------

        private readonly RelayCommand _addToBatchQueueCommand = null!;
        private readonly RelayCommand _cancelGenerationCommand = null!;
        private readonly RelayCommand _openResultFolderCommand = null!;
        private readonly RelayCommand _clearCompletedBatchesCommand = null!;
        private readonly RelayCommand<VideoBatch> _removeBatchCommand = null!;

        public ICommand LoadPromptsCommand { get; }
        public ICommand AddToBatchQueueCommand => _addToBatchQueueCommand;
        public ICommand RemoveBatchCommand => _removeBatchCommand;
        public ICommand ClearCompletedBatchesCommand => _clearCompletedBatchesCommand;
        public ICommand CancelGenerationCommand => _cancelGenerationCommand;
        public ICommand OpenResultFolderCommand => _openResultFolderCommand;

        // ---------------------------------------------------------------
        // Workflow loading
        // ---------------------------------------------------------------

        private void LoadWorkflows()
        {
            _allWorkflows.Clear();
            WorkflowNames.Clear();

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var workflowDir = Path.Combine(baseDir, "workflow");
            if (!Directory.Exists(workflowDir))
            {
                AddLog($"Workflow directory not found: {workflowDir}");
                return;
            }

            var rootFiles = Directory.GetFiles(workflowDir, "*.json")
                .Where(f => !Path.GetDirectoryName(f)!.EndsWith("zimage", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileNameWithoutExtension(f));

            foreach (var file in rootFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                _allWorkflows.Add(new WorkflowInfo { Name = name, WorkflowFile = file });
                WorkflowNames.Add(name);
            }

            // Also include story-specific workflows from workflow/video/story/
            var storyDir = Path.Combine(workflowDir, "video", "story");
            AddLog($"Story workflow dir: {storyDir} | exists={Directory.Exists(storyDir)}");
            if (Directory.Exists(storyDir))
            {
                var storyFiles = Directory.GetFiles(storyDir, "*.json")
                    .OrderBy(f => Path.GetFileNameWithoutExtension(f));

                foreach (var file in storyFiles)
                {
                    var name = $"[Story] {Path.GetFileNameWithoutExtension(file)}";
                    _allWorkflows.Add(new WorkflowInfo { Name = name, WorkflowFile = file });
                    WorkflowNames.Add(name);
                }
            }

            AddLog($"Loaded {_allWorkflows.Count} workflows");
            if (_allWorkflows.Count > 0)
                OnPropertyChanged(nameof(SelectedWorkflowName));
        }

        // ---------------------------------------------------------------
        // Load prompts into pending batch
        // ---------------------------------------------------------------

        private async void LoadPrompts()
        {
            try
            {
                var filePath = await _fileDialogService.OpenFileDialogAsync(
                    "Load Prompts",
                    "Prompt Files|*.json;*.txt|JSON Files|*.json|Text Files|*.txt|All Files|*.*",
                    PromptsFolderPath,
                    persistKey: "storyvideo.prompts");

                if (filePath == null) return;

                List<string> prompts;
                var ext = Path.GetExtension(filePath).ToLowerInvariant();

                if (ext == ".txt")
                {
                    // One prompt per non-empty line
                    prompts = File.ReadAllLines(filePath)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                }
                else
                {
                    // JSON: { "Prompts": [...] } or a bare array
                    var json = File.ReadAllText(filePath);
                    var data = JsonSerializer.Deserialize<JsonElement>(json);
                    JsonElement arr;
                    if (data.TryGetProperty("Prompts", out var p) && p.ValueKind == JsonValueKind.Array)
                        arr = p;
                    else if (data.ValueKind == JsonValueKind.Array)
                        arr = data;
                    else
                    {
                        AddLog("ERROR: Unsupported file format — expected a JSON array or { \"Prompts\": [...] }");
                        return;
                    }
                    prompts = arr.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }

                if (prompts.Count == 0)
                {
                    AddLog("WARNING: No prompts found in file");
                    return;
                }

                _pendingPrompts = prompts;
                _pendingBatchName = Path.GetFileNameWithoutExtension(filePath);
                PromptsFolderPath = Path.GetDirectoryName(filePath) ?? PromptsFolderPath;

                OnPropertyChanged(nameof(PendingBatchSummary));
                OnPropertyChanged(nameof(HasPendingPrompts));
                (AddToBatchQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();

                AddLog($"Loaded {prompts.Count} prompts from: {Path.GetFileName(filePath)}");
                StatusBarMessage = $"{prompts.Count} prompts ready — select a workflow and click 'Add to Queue'";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR loading prompts: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // Batch queue management
        // ---------------------------------------------------------------

        private void AddToBatchQueue()
        {
            if (_pendingPrompts.Count == 0 || SelectedWorkflow == null) return;

            // Snapshot workflow and prompts at enqueue time — never changed again
            var batch = new VideoBatch
            {
                BatchName = _pendingBatchName,
                WorkflowFile = SelectedWorkflow.WorkflowFile,
                WorkflowName = SelectedWorkflow.Name,
                Prompts = new List<string>(_pendingPrompts)
            };

            _batchQueue.Add(batch);
            OnPropertyChanged(nameof(QueuedBatchCount));
            OnPropertyChanged(nameof(HasBatchItems));
            OnPropertyChanged(nameof(HasNoBatchItems));
            _clearCompletedBatchesCommand.NotifyCanExecuteChanged();

            AddLog($"Queued batch '{batch.BatchName}' — {batch.TotalCount} videos, workflow: {batch.WorkflowName}");
            StatusBarMessage = $"Batch queued — {_batchQueue.Count(b => b.Status == "Queued")} batch(es) waiting";

            // Clear pending state so user can configure the next batch independently
            _pendingPrompts = new List<string>();
            _pendingBatchName = string.Empty;
            OnPropertyChanged(nameof(PendingBatchSummary));
            OnPropertyChanged(nameof(HasPendingPrompts));
            (AddToBatchQueueCommand as RelayCommand)?.NotifyCanExecuteChanged();

            // Start the processor if it is not already running
            StartQueueProcessorIfNeeded();
        }

        private void RemoveBatch(VideoBatch? batch)
        {
            if (batch == null || batch.Status != "Queued") return;
            _batchQueue.Remove(batch);
            OnPropertyChanged(nameof(QueuedBatchCount));
            OnPropertyChanged(nameof(HasBatchItems));
            OnPropertyChanged(nameof(HasNoBatchItems));
            AddLog($"Removed batch '{batch.BatchName}' from queue");
        }

        private void ClearCompletedBatches()
        {
            var completed = _batchQueue.Where(b => b.Status == "Done" || b.Status == "Failed").ToList();
            foreach (var b in completed) _batchQueue.Remove(b);
            _clearCompletedBatchesCommand.NotifyCanExecuteChanged();
        }

        private void CancelGeneration()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                AddLog("Cancellation requested — current video will finish then processing stops");
                _cancellationTokenSource.Cancel();
                ProcessingStatus = "Cancelling...";
            }
        }

        // ---------------------------------------------------------------
        // Background queue processor
        // ---------------------------------------------------------------

        private void StartQueueProcessorIfNeeded()
        {
            if (_queueProcessorTask == null || _queueProcessorTask.IsCompleted)
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
                _queueProcessorTask = Task.Run(() => ProcessQueueLoopAsync(_cancellationTokenSource.Token));
            }
        }

        private async Task ProcessQueueLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                VideoBatch? batch = null;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    batch = _batchQueue.FirstOrDefault(b => b.Status == "Queued");
                    if (batch != null) batch.Status = "Processing";
                });

                if (batch == null) break;

                IsProcessing = true;

                try
                {
                    await ProcessBatchAsync(batch, cancellationToken);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        batch.Status = "Done";
                        OnPropertyChanged(nameof(QueuedBatchCount));
            OnPropertyChanged(nameof(HasBatchItems));
            OnPropertyChanged(nameof(HasNoBatchItems));
                    });
                    AddLog($"=== Batch '{batch.BatchName}' complete ({batch.ProcessedCount}/{batch.TotalCount} videos) ===");
                }
                catch (OperationCanceledException)
                {
                    // Reset to Queued so the user can resume by starting processing again
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        batch.Status = "Queued";
                        OnPropertyChanged(nameof(QueuedBatchCount));
            OnPropertyChanged(nameof(HasBatchItems));
            OnPropertyChanged(nameof(HasNoBatchItems));
                    });
                    AddLog($"Batch '{batch.BatchName}' cancelled — reset to Queued");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Batch processing error: {ex}");
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        batch.Status = "Failed";
                        OnPropertyChanged(nameof(QueuedBatchCount));
            OnPropertyChanged(nameof(HasBatchItems));
            OnPropertyChanged(nameof(HasNoBatchItems));
                    });
                    AddLog($"Batch '{batch.BatchName}' FAILED: {ex.Message}");
                }
            }

            IsProcessing = false;
            ProcessingStatus = "Ready";
            StatusBarMessage = "Processing complete";
        }

        // ---------------------------------------------------------------
        // Single-batch processing
        // ---------------------------------------------------------------

        private async Task ProcessBatchAsync(VideoBatch batch, CancellationToken cancellationToken)
        {
            // Read the workflow that was snapshotted when this batch was enqueued —
            // changing the workflow dropdown in the UI has no effect on this batch.
            var workflowFile = batch.WorkflowFile;
            var workflowName = batch.WorkflowName;

            AddLog($"=== Starting batch '{batch.BatchName}' | {batch.TotalCount} videos | workflow: {workflowName} ===");

            var comfyUIOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(
                status => AddLog($"[ComfyUI] {status}"),
                cancellationToken);

            if (!comfyUIOk)
                throw new InvalidOperationException("ComfyUI is not running and auto-restart failed");

            if (!_comfyUIService.IsConnected)
            {
                AddLog("Connecting to ComfyUI...");
                await _comfyUIService.ConnectAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(workflowFile))
                throw new FileNotFoundException($"Workflow file not found: {workflowFile}");

            var workflowJson = await File.ReadAllTextAsync(workflowFile, cancellationToken);
            var workflow = JsonSerializer.Deserialize<JsonElement>(workflowJson);

            var generatedVideos = new List<string>();
            var totalClips = batch.Prompts.Count;

            for (int i = 0; i < totalClips; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var prompt = batch.Prompts[i];
                var clipNum = i + 1;
                var preview = prompt.Length > 70 ? prompt.Substring(0, 70) + "..." : prompt;

                AddLog($"\n*** Clip {clipNum}/{totalClips} ***\n{preview}");

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ProcessingStatus = $"Batch '{batch.BatchName}' — clip {clipNum}/{totalClips}";
                    ProcessingProgress = (i / (double)totalClips) * 100;
                });

                var updatedWorkflow = UpdateVideoWorkflowParameters(workflow, prompt);

                var capturedI = i;
                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data?.Value != null && msg.Data?.Max != null)
                    {
                        var pct = (double)msg.Data.Value / msg.Data.Max;
                        var overall = ((capturedI + pct) / totalClips) * 100;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            ProcessingProgress = Math.Min(overall, 99);
                            ProcessingStatus = $"Batch '{batch.BatchName}' — clip {clipNum}/{totalClips}: {pct * 100:F0}%";
                        });
                    }
                });

                try
                {
                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(updatedWorkflow, progress, cancellationToken);
                    AddLog($"✓ Clip {clipNum} submitted (ID: {promptId})");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddLog($"✗ Clip {clipNum} failed: {ex.Message}");
                    continue;
                }

                // Give ComfyUI time to write the file
                await Task.Delay(5000, cancellationToken);

                var videoPath = GetOutputVideoFromComfyUI();
                if (!string.IsNullOrEmpty(videoPath))
                {
                    generatedVideos.Add(videoPath);
                    AddLog($"✓ Clip {clipNum} captured: {Path.GetFileName(videoPath)}");
                }
                else
                {
                    AddLog($"⚠ Clip {clipNum}: no output video found in ComfyUI output folder");
                }

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    batch.ProcessedCount = clipNum;
                    StatusBarMessage = $"Batch '{batch.BatchName}': {clipNum}/{totalClips} clips done";
                });
            }

            // Copy captured videos to our output folder
            if (generatedVideos.Any())
            {
                var baseOutputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-video");
                var sessionDir = GetUniqueFolderPath(baseOutputDir, batch.BatchName);
                Directory.CreateDirectory(sessionDir);

                for (int i = 0; i < generatedVideos.Count; i++)
                {
                    var destPath = Path.Combine(sessionDir, $"{batch.BatchName}-{i + 1}.mp4");
                    File.Copy(generatedVideos[i], destPath, true);
                    await LocalCopyService.CopyVideoAsync(destPath);
                    AddLog($"✓ Saved: {destPath}");
                }

                ResultVideoPath = Path.Combine(sessionDir, $"{batch.BatchName}-1.mp4");
                HasResultVideo = true;
                AddLog($"✓ {generatedVideos.Count} videos saved to: {sessionDir}");
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                ProcessingProgress = 100);
        }

        // ---------------------------------------------------------------
        // Workflow parameter injection
        // ---------------------------------------------------------------

        private JsonElement UpdateVideoWorkflowParameters(JsonElement workflow, string prompt)
        {
            var workflowDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText());
            if (workflowDict == null) return workflow;

            // WCFMAPI / Sulphur workflow: node "177:109" (PrimitiveStringMultiline)
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
                        AddLog($"Injected prompt into node 177:109");
                    }
                }
            }
            else if (workflowDict.ContainsKey("6"))
            {
                // painteri2v / CLIPTextEncode workflow: node "6" is the positive prompt
                var node = JsonSerializer.Deserialize<Dictionary<string, object>>(workflowDict["6"].GetRawText());
                if (node != null && node.ContainsKey("inputs"))
                {
                    var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(node["inputs"]));
                    if (inputs != null && inputs.ContainsKey("text"))
                    {
                        inputs["text"] = prompt;
                        node["inputs"] = inputs;
                        workflowDict["6"] = JsonSerializer.SerializeToElement(node);
                        AddLog($"Injected prompt into node 6 (CLIPTextEncode)");
                    }
                }
            }
            else
            {
                AddLog("⚠ No supported prompt node found — prompt not injected (check workflow format)");
            }

            return JsonSerializer.SerializeToElement(workflowDict);
        }

        // ---------------------------------------------------------------
        // Output video retrieval
        // ---------------------------------------------------------------

        private string GetOutputVideoFromComfyUI()
        {
            try
            {
                var comfyUIOutputDir = _settingsService.Settings?.OutputFolderPath;
                if (string.IsNullOrEmpty(comfyUIOutputDir) || !Directory.Exists(comfyUIOutputDir))
                {
                    AddLog("ERROR: ComfyUI output folder not configured or not found");
                    return string.Empty;
                }

                var videoFiles = Directory.GetFiles(comfyUIOutputDir, "*.mp4", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (videoFiles.Any())
                {
                    var latest = videoFiles.First();
                    var age = DateTime.Now - File.GetLastWriteTime(latest);
                    if (age.TotalMinutes < 10)
                    {
                        var tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "story-video");
                        Directory.CreateDirectory(tempDir);
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var tempPath = Path.Combine(tempDir, $"story-video_{timestamp}.mp4");
                        File.Copy(latest, tempPath, true);
                        _ = LocalCopyService.CopyVideoAsync(tempPath);
                        return tempPath;
                    }
                    else
                    {
                        AddLog($"Latest video is {age.TotalMinutes:F1} min old — skipping");
                    }
                }
                else
                {
                    AddLog("No .mp4 files found in ComfyUI output folder");
                }
            }
            catch (Exception ex)
            {
                AddLog($"ERROR retrieving output video: {ex.Message}");
            }
            return string.Empty;
        }

        // ---------------------------------------------------------------
        // Misc helpers
        // ---------------------------------------------------------------

        private void OpenResultFolder()
        {
            try
            {
                var folder = Path.GetDirectoryName(ResultVideoPath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog($"ERROR opening folder: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() => LogOutput += line);
            else
                LogOutput += line;
            _logger.LogInfo(message);
        }

        private string GetUniqueFolderPath(string baseDir, string folderName)
        {
            var path = Path.Combine(baseDir, folderName);
            if (!Directory.Exists(path)) return path;
            int counter = 2;
            string newPath;
            do { newPath = Path.Combine(baseDir, $"{folderName} ({counter++})"); }
            while (Directory.Exists(newPath));
            return newPath;
        }

        private void LoadPromptsFolderFromSettings()
        {
            try
            {
                var saved = _settingsService.Settings?.StoryVideoPromptsFolder;
                _promptsFolderPath = !string.IsNullOrEmpty(saved) && Directory.Exists(saved)
                    ? saved
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch { _promptsFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        }

        private void SavePromptsFolderToSettings()
        {
            try
            {
                if (_settingsService.Settings != null)
                {
                    _settingsService.Settings.StoryVideoPromptsFolder = _promptsFolderPath;
                    _settingsService.SaveSettings(_settingsService.Settings);
                }
            }
            catch (Exception ex) { AddLog($"ERROR saving folder setting: {ex.Message}"); }
        }

        // ---------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------

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
                WorkflowNames.Clear();
                _allWorkflows.Clear();
                _disposed = true;
            }
        }
    }
}
