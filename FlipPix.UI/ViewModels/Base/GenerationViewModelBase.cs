using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Interfaces;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Interfaces;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.ViewModels.Base;

/// <summary>
/// Base ViewModel for all generation workflows (image and video).
/// Provides common functionality for processing, progress tracking, and result handling.
/// </summary>
public abstract partial class GenerationViewModelBase : ObservableObject, IDisposable
{
    protected readonly IComfyUIService _comfyUIService;
    protected readonly IAppLogger _logger;
    protected CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isProcessing;

    [ObservableProperty]
    private string _processingStatus = string.Empty;

    [ObservableProperty]
    private double _processingProgress;

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private string _negativePrompt = string.Empty;

    [ObservableProperty]
    private int _seed = -1;

    [ObservableProperty]
    private BitmapImage? _resultImage;

    [ObservableProperty]
    private string? _resultPath;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private DateTime _generationStartTime;

    [ObservableProperty]
    private TimeSpan _generationDuration;

    #endregion

    #region Computed Properties

    public virtual bool CanGenerate => !IsProcessing && !string.IsNullOrWhiteSpace(Prompt);
    public virtual bool CanCancel => IsProcessing;
    public virtual string StatusText => IsProcessing ? ProcessingStatus : "Ready";

    #endregion

    #region Commands

    public IAsyncRelayCommand GenerateCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearResultCommand { get; }

    #endregion

    protected GenerationViewModelBase(IComfyUIService comfyUIService, IAppLogger logger)
    {
        _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => CanGenerate);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        ClearResultCommand = new RelayCommand(ClearResult);
    }

    #region Generation Methods

    protected virtual async Task GenerateAsync()
    {
        if (IsProcessing) return;

        try
        {
            ClearError();
            IsProcessing = true;
            ProcessingStatus = "Initializing...";
            ProcessingProgress = 0;
            GenerationStartTime = DateTime.Now;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            // Validate before starting
            var validationError = ValidateInputs();
            if (validationError != null)
            {
                SetError(validationError);
                return;
            }

            // Prepare the workflow
            ProcessingStatus = "Preparing workflow...";
            var workflow = await PrepareWorkflowAsync(token);

            if (workflow == null)
            {
                SetError("Failed to prepare workflow");
                return;
            }

            // Execute the workflow
            ProcessingStatus = "Executing workflow...";
            var progress = new Progress<ProgressMessage>(OnProgressUpdate);

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                workflow,
                progress,
                token,
                GetExecutionTimeout());

            // Handle the result
            ProcessingStatus = "Processing result...";
            await HandleResultAsync(promptId, token);

            GenerationDuration = DateTime.Now - GenerationStartTime;
            ProcessingStatus = $"Completed in {GenerationDuration.TotalSeconds:F1}s";

            _logger.LogInfo("Generation completed successfully: {PromptId}", promptId);
        }
        catch (OperationCanceledException)
        {
            ProcessingStatus = "Cancelled";
            _logger.LogInfo("Generation cancelled by user");
        }
        catch (Exception ex)
        {
            SetError($"Generation failed: {ex.Message}");
            _logger.LogError(ex, "Generation failed");
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            RefreshCommands();
        }
    }

    protected virtual void Cancel()
    {
        _cancellationTokenSource?.Cancel();
        ProcessingStatus = "Cancelling...";
    }

    protected virtual void ClearResult()
    {
        ResultImage = null;
        ResultPath = null;
        ClearError();
    }

    #endregion

    #region Abstract Methods

    /// <summary>
    /// Prepares the workflow JSON for execution.
    /// </summary>
    protected abstract Task<object?> PrepareWorkflowAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Handles the result after workflow execution completes.
    /// </summary>
    protected abstract Task HandleResultAsync(string promptId, CancellationToken cancellationToken);

    #endregion

    #region Virtual Methods

    /// <summary>
    /// Validates inputs before generation. Returns error message or null if valid.
    /// </summary>
    protected virtual string? ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(Prompt))
            return "Please enter a prompt";

        return null;
    }

    /// <summary>
    /// Gets the execution timeout for the workflow.
    /// </summary>
    protected virtual TimeSpan? GetExecutionTimeout() => null;

    /// <summary>
    /// Called when progress is updated during workflow execution.
    /// </summary>
    protected virtual void OnProgressUpdate(ProgressMessage progress)
    {
        if (progress.Data != null)
        {
            var current = progress.Data.Value;
            var max = progress.Data.Max;

            if (max > 0)
            {
                ProcessingProgress = (double)current / max * 100;
                ProcessingStatus = $"Processing... {current}/{max} ({ProcessingProgress:F0}%)";
            }
        }
    }

    #endregion

    #region Helper Methods

    protected void SetError(string message)
    {
        ErrorMessage = message;
        HasError = true;
        _logger.LogWarning("Generation error: {Message}", message);
    }

    protected void ClearError()
    {
        ErrorMessage = null;
        HasError = false;
    }

    protected void RefreshCommands()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Generates a random seed if the current seed is -1.
    /// </summary>
    protected int GetEffectiveSeed()
    {
        if (Seed == -1)
        {
            return Random.Shared.Next();
        }
        return Seed;
    }

    /// <summary>
    /// Loads an image from a file path into a BitmapImage.
    /// </summary>
    protected static BitmapImage? LoadImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
