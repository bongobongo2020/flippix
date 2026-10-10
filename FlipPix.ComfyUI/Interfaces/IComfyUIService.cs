using FlipPix.ComfyUI.Models;

namespace FlipPix.ComfyUI.Interfaces;

/// <summary>
/// Interface for ComfyUI service operations.
/// Provides abstraction for testing and dependency injection.
/// </summary>
public interface IComfyUIService : IDisposable
{
    /// <summary>Raised when workflow progress is updated.</summary>
    event EventHandler<ProgressMessage>? ProgressUpdated;

    /// <summary>Raised when workflow execution completes.</summary>
    event EventHandler<ExecutionCompleteMessage>? ExecutionCompleted;

    /// <summary>Raised when connection status changes.</summary>
    event EventHandler<string>? ConnectionStatusChanged;

    /// <summary>Raised when a node finishes executing and produces output.</summary>
    event EventHandler<NodeExecutedEventArgs>? NodeExecuted;

    /// <summary>Gets whether the WebSocket is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Connects to the ComfyUI server.</summary>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Disconnects from the ComfyUI server.</summary>
    Task DisconnectAsync();

    /// <summary>Checks if ComfyUI is running and responsive.</summary>
    Task<bool> IsComfyUIRunningAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects if ComfyUI has crashed and attempts to restart it.
    /// </summary>
    /// <returns>True if ComfyUI is running after the check.</returns>
    Task<bool> DetectAndRestartIfCrashedAsync(Action<string>? statusCallback = null, CancellationToken cancellationToken = default);

    /// <summary>Uploads an image file to ComfyUI.</summary>
    /// <returns>The filename of the uploaded image on the server.</returns>
    Task<string> UploadImageAsync(string imagePath, CancellationToken cancellationToken = default);

    /// <summary>Uploads a video file to ComfyUI.</summary>
    /// <returns>The filename of the uploaded video on the server.</returns>
    Task<string> UploadVideoAsync(string videoPath, CancellationToken cancellationToken = default);

    /// <summary>Uploads an audio file to ComfyUI.</summary>
    /// <returns>The filename of the uploaded audio on the server.</returns>
    Task<string> UploadAudioAsync(string audioPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks ComfyUI to unload models and free VRAM/RAM.
    /// </summary>
    Task<bool> FreeMemoryAsync(bool unloadModels = true, bool freeMemory = true, CancellationToken cancellationToken = default);

    /// <summary>Queues a workflow prompt without waiting for completion.</summary>
    /// <returns>The prompt ID.</returns>
    Task<string> QueuePromptAsync(object workflow, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads multiple files for a workflow.
    /// </summary>
    Task<Dictionary<string, string>> UploadAndPrepareFilesAsync(
        string videoFilePath,
        string styleImagePath,
        string faceImagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a workflow and waits for it to finish.
    /// </summary>
    /// <param name="workflow">The workflow JSON object.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="executionTimeout">Optional custom execution timeout.</param>
    /// <returns>The prompt ID of the completed workflow.</returns>
    Task<string> ExecuteWorkflowAsync(
        object workflow,
        IProgress<ProgressMessage>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? executionTimeout = null);
}
