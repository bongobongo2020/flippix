using FlipPix.Core.Interfaces;

namespace FlipPix.Core.Utilities;

/// <summary>
/// Provides safe fire-and-forget task execution with proper error handling.
/// Prevents swallowed exceptions from fire-and-forget patterns.
/// </summary>
public static class SafeTask
{
    /// <summary>
    /// Executes a task in the background with proper error handling.
    /// Replaces unsafe <c>_ = Task.Run(...)</c> patterns.
    /// </summary>
    /// <param name="action">The async action to execute.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the task is doing for error messages.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static void FireAndForget(
        Func<Task> action,
        IAppLogger? logger = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected cancellation, no need to log
                logger?.LogDebug("Background task cancelled: {Context}", context ?? "unknown");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Background task failed: {Context}", context ?? "unknown");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a task in the background with proper error handling and an error callback.
    /// </summary>
    /// <param name="action">The async action to execute.</param>
    /// <param name="onError">Callback invoked when an error occurs.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the task is doing.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static void FireAndForget(
        Func<Task> action,
        Action<Exception> onError,
        IAppLogger? logger = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogDebug("Background task cancelled: {Context}", context ?? "unknown");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Background task failed: {Context}", context ?? "unknown");
                try
                {
                    onError(ex);
                }
                catch (Exception callbackEx)
                {
                    logger?.LogError(callbackEx, "Error callback failed for: {Context}", context ?? "unknown");
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a task in the background and returns a handle to monitor completion.
    /// Useful when you want to fire-and-forget but still track if the task completed.
    /// </summary>
    /// <param name="action">The async action to execute.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the task is doing.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task representing the background operation (safe to ignore).</returns>
    public static Task RunInBackground(
        Func<Task> action,
        IAppLogger? logger = null,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger?.LogDebug("Background task cancelled: {Context}", context ?? "unknown");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Background task failed: {Context}", context ?? "unknown");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Safely awaits a task, catching and logging any exceptions.
    /// </summary>
    /// <param name="task">The task to await.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the task is doing.</param>
    /// <returns>True if the task completed successfully, false if it threw an exception.</returns>
    public static async Task<bool> SafeAwaitAsync(
        Task task,
        IAppLogger? logger = null,
        string? context = null)
    {
        try
        {
            await task;
            return true;
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Task cancelled: {Context}", context ?? "unknown");
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Task failed: {Context}", context ?? "unknown");
            return false;
        }
    }

    /// <summary>
    /// Safely awaits a task with a result, returning a default value on failure.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="task">The task to await.</param>
    /// <param name="defaultValue">Value to return if the task fails.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the task is doing.</param>
    /// <returns>The task result or the default value if it threw an exception.</returns>
    public static async Task<T?> SafeAwaitAsync<T>(
        Task<T> task,
        T? defaultValue = default,
        IAppLogger? logger = null,
        string? context = null)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            logger?.LogDebug("Task cancelled: {Context}", context ?? "unknown");
            return defaultValue;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Task failed: {Context}", context ?? "unknown");
            return defaultValue;
        }
    }

    /// <summary>
    /// Executes multiple tasks in parallel with proper error handling.
    /// Continues even if some tasks fail.
    /// </summary>
    /// <param name="tasks">The tasks to execute.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="context">Description of what the tasks are doing.</param>
    /// <returns>Results indicating success/failure for each task.</returns>
    public static async Task<bool[]> WhenAllSafe(
        IEnumerable<Task> tasks,
        IAppLogger? logger = null,
        string? context = null)
    {
        var taskList = tasks.ToList();
        var results = new bool[taskList.Count];

        var wrappedTasks = taskList.Select(async (task, index) =>
        {
            try
            {
                await task;
                results[index] = true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Parallel task {Index} failed: {Context}", index, context ?? "unknown");
                results[index] = false;
            }
        });

        await Task.WhenAll(wrappedTasks);
        return results;
    }
}
