using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.ComfyUI.Http;
using FlipPix.ComfyUI.WebSocket;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Exceptions;

namespace FlipPix.ComfyUI.Services;

public class ComfyUIService : IDisposable
{
    private readonly ComfyUIHttpClient _httpClient;
    private readonly ComfyUIWebSocketClient _webSocketClient;
    private readonly IAppLogger _logger;
    private readonly ComfyUISettings _settings;
    private readonly string _clientId;
    private bool _disposed = false;

    public event EventHandler<ProgressMessage>? ProgressUpdated;
    public event EventHandler<ExecutionCompleteMessage>? ExecutionCompleted;
    public event EventHandler<string>? ConnectionStatusChanged;

    public bool IsConnected => _webSocketClient.IsConnected;

    public ComfyUIService(ComfyUIHttpClient httpClient, ComfyUIWebSocketClient webSocketClient, 
        IAppLogger logger, ComfyUISettings settings)
    {
        _httpClient = httpClient;
        _webSocketClient = webSocketClient;
        _logger = logger;
        _settings = settings;
        _clientId = Guid.NewGuid().ToString();

        // Subscribe to WebSocket events
        _webSocketClient.MessageReceived += OnWebSocketMessageReceived;
        _webSocketClient.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Connecting to ComfyUI service");

            // Test HTTP connection first
            var httpConnected = await RetryAsync(
                () => _httpClient.TestConnectionAsync(cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);

            if (!httpConnected)
            {
                throw new ComfyUIConnectionException("Failed to establish HTTP connection to ComfyUI");
            }

            // Connect WebSocket
            await RetryAsync(
                () => _webSocketClient.ConnectAsync(_clientId, cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);

            _logger.LogInfo("ComfyUI service connected successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to ComfyUI service");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await _webSocketClient.DisconnectAsync();
            _logger.LogInfo("ComfyUI service disconnected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during ComfyUI service disconnect");
        }
    }

    public async Task<string> UploadImageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Uploading image to ComfyUI: {ImagePath}", imagePath);

            var uploadedFileName = await RetryAsync(
                () => _httpClient.UploadImageAsync(imagePath, "input", cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);

            _logger.LogInfo("Image uploaded successfully: {FileName}", uploadedFileName);
            return uploadedFileName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload image");
            throw;
        }
    }

    public async Task<string> QueuePromptAsync(
        object workflow,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Queueing prompt to ComfyUI");

            var promptId = await RetryAsync(
                () => _httpClient.SubmitPromptAsync(workflow, _clientId, cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);

            _logger.LogInfo("Prompt queued with ID: {PromptId}", promptId);
            return promptId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue prompt");
            throw;
        }
    }

    public async Task<Dictionary<string, string>> UploadAndPrepareFilesAsync(
        string videoFilePath,
        string styleImagePath,
        string faceImagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Uploading files to ComfyUI");

            var uploadedFiles = new Dictionary<string, string>();

            // Upload video file
            var videoFileName = await RetryAsync(
                () => _httpClient.UploadImageAsync(videoFilePath, "input", cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);
            uploadedFiles["video"] = videoFileName;

            // Upload style reference image
            var styleFileName = await RetryAsync(
                () => _httpClient.UploadImageAsync(styleImagePath, "input", cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);
            uploadedFiles["style"] = styleFileName;

            // Upload face reference image
            var faceFileName = await RetryAsync(
                () => _httpClient.UploadImageAsync(faceImagePath, "input", cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);
            uploadedFiles["face"] = faceFileName;

            _logger.LogInfo("All files uploaded successfully");
            return uploadedFiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload files");
            throw;
        }
    }

    public async Task<string> ExecuteWorkflowAsync(
        object workflow, 
        IProgress<ProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInfo("Executing workflow");

            // Submit workflow
            var promptId = await RetryAsync(
                () => _httpClient.SubmitPromptAsync(workflow, _clientId, cancellationToken),
                _settings.MaxRetries,
                TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
                cancellationToken);

            _logger.LogInfo("Workflow submitted with prompt ID: {PromptId}", promptId);

            // Monitor execution via WebSocket
            var completionSource = new TaskCompletionSource<string>();
            var isCompleted = false;
            var lockObj = new object();

            void OnProgressUpdate(object? sender, ProgressMessage progressMsg)
            {
                if (progressMsg.Data?.PromptId == promptId)
                {
                    progress?.Report(progressMsg);
                }
            }

            void OnExecutionComplete(object? sender, ExecutionCompleteMessage completeMsg)
            {
                _logger.LogInfo("Received execution completion for prompt: {PromptId}, expected: {ExpectedId}",
                    completeMsg.Data?.PromptId ?? "null", promptId);

                if (completeMsg.Data?.PromptId == promptId)
                {
                    lock (lockObj)
                    {
                        if (!isCompleted)
                        {
                            isCompleted = true;
                            _logger.LogInfo("Completion source set for prompt: {PromptId}", promptId);
                            completionSource.TrySetResult(promptId);
                        }
                        else
                        {
                            _logger.LogWarning("Completion already triggered for prompt: {PromptId}, ignoring duplicate", promptId);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Received completion for different prompt. Got: {ReceivedId}, Expected: {ExpectedId}",
                        completeMsg.Data?.PromptId ?? "null", promptId);
                }
            }

            ProgressUpdated += OnProgressUpdate;
            ExecutionCompleted += OnExecutionComplete;

            // Wait for completion or timeout
            var timeout = TimeSpan.FromMinutes(30);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // Start a fallback completion detection task - extended to 60 seconds for longer workflows
            var fallbackTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken); // Wait 60 seconds
                _logger.LogWarning("Fallback completion triggered after 60 seconds for prompt: {PromptId}", promptId);
                lock (lockObj)
                {
                    if (!isCompleted)
                    {
                        isCompleted = true;
                        _logger.LogWarning("Forcing completion - WebSocket completion message may have been missed");
                        completionSource.TrySetResult(promptId); // Force completion
                    }
                }
            }, cancellationToken);

            try
            {
                var completedPromptId = await completionSource.Task.WaitAsync(combinedCts.Token);

                _logger.LogInfo("Workflow execution completed: {PromptId}", completedPromptId);
                return completedPromptId;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new ComfyUITimeoutException("Workflow execution timed out", timeout);
            }
            finally
            {
                // Ensure handlers are removed to prevent interference with future executions
                ProgressUpdated -= OnProgressUpdate;
                ExecutionCompleted -= OnExecutionComplete;

                // Cancel and dispose fallback task
                try
                {
                    fallbackTask.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error disposing fallback task: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute workflow");
            throw;
        }
    }

    private void OnWebSocketMessageReceived(object? sender, WebSocketMessage message)
    {
        try
        {
            _logger.LogInfo($"Processing WebSocket message type: {message.Type}");

            switch (message.Type)
            {
                case "progress":
                    if (message is ProgressMessage progressMsg)
                    {
                        _logger.LogInfo($"Progress message - PromptId: {progressMsg.Data?.PromptId}, Value: {progressMsg.Data?.Value}, Max: {progressMsg.Data?.Max}");
                        ProgressUpdated?.Invoke(this, progressMsg);
                    }
                    else
                    {
                        _logger.LogWarning("Received progress message but failed to cast to ProgressMessage");
                    }
                    break;

                case "executing":
                    if (message is ExecutingMessage execingMsg)
                    {
                        _logger.LogInfo($"Executing message - PromptId: {execingMsg.Data?.PromptId}, Node: {execingMsg.Data?.Node ?? "null"}");

                        // When node is null, it means execution has completed
                        if (execingMsg.Data?.Node == null && !string.IsNullOrEmpty(execingMsg.Data?.PromptId))
                        {
                            _logger.LogInfo($"Execution completed (executing with null node) for prompt: {execingMsg.Data.PromptId}");

                            // Create a synthetic ExecutionCompleteMessage
                            var syntheticComplete = new ExecutionCompleteMessage
                            {
                                Type = "execution_complete",
                                Data = new ExecutionCompleteData
                                {
                                    PromptId = execingMsg.Data.PromptId
                                }
                            };
                            ExecutionCompleted?.Invoke(this, syntheticComplete);
                        }
                    }
                    else
                    {
                        _logger.LogInfo($"Received executing message but failed to cast: {message.RawData}");
                    }
                    break;

                case "executed":
                    _logger.LogInfo($"Received executed message: {message.RawData}");
                    // Some workflows send "executed" instead of "execution_complete"
                    // Treat this as completion
                    if (message is ExecutionCompleteMessage execMsg)
                    {
                        _logger.LogInfo($"Executed message - PromptId: {execMsg.Data?.PromptId}");
                        ExecutionCompleted?.Invoke(this, execMsg);
                    }
                    break;

                case "execution_complete":
                    if (message is ExecutionCompleteMessage completeMsg)
                    {
                        _logger.LogInfo($"Execution complete message - PromptId: {completeMsg.Data?.PromptId}");
                        ExecutionCompleted?.Invoke(this, completeMsg);
                    }
                    else
                    {
                        _logger.LogWarning("Received execution_complete message but failed to cast to ExecutionCompleteMessage");
                        _logger.LogInfo($"Raw message: {message.RawData}");
                    }
                    break;

                default:
                    _logger.LogInfo($"Unhandled message type: {message.Type}, Raw: {message.RawData}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message");
        }
    }

    private void OnConnectionStatusChanged(object? sender, string status)
    {
        ConnectionStatusChanged?.Invoke(this, status);
    }

    private async Task<T> RetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                if (attempt == maxRetries)
                {
                    _logger.LogError(ex, "Operation failed after {MaxRetries} attempts", maxRetries);
                    break;
                }
                
                _logger.LogWarning("Operation failed on attempt {Attempt}/{MaxRetries}, retrying in {Delay}ms: {Error}", 
                    attempt, maxRetries, delay.TotalMilliseconds, ex.Message);
                
                await Task.Delay(delay * attempt, cancellationToken); // Exponential backoff
            }
        }
        
        throw lastException ?? new InvalidOperationException("Operation failed without exception");
    }

    private async Task RetryAsync(
        Func<Task> operation,
        int maxRetries,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await RetryAsync(async () =>
        {
            await operation();
            return true;
        }, maxRetries, delay, cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _webSocketClient?.Dispose();
            _disposed = true;
        }
    }
}