using FlipPix.Core.Interfaces;

namespace FlipPix.Core.Utilities;

/// <summary>
/// Provides robust retry logic with exponential backoff for transient failures.
/// Centralizes retry patterns used throughout the application.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Executes an operation with retry logic and exponential backoff.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="baseDelay">Base delay between retries (multiplied by attempt number).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="logger">Optional logger for retry diagnostics.</param>
    /// <param name="shouldRetry">Optional predicate to determine if a specific exception should trigger a retry.</param>
    /// <param name="maxDelay">Maximum delay cap for exponential backoff.</param>
    /// <returns>The result of the successful operation.</returns>
    /// <exception cref="Exception">Throws the last exception if all retries are exhausted.</exception>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = Constants.Workflow.DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null,
        Func<Exception, bool>? shouldRetry = null,
        TimeSpan? maxDelay = null)
    {
        var delay = baseDelay ?? TimeSpan.FromMilliseconds(Constants.Workflow.DefaultRetryDelayMs);
        var maxDelayMs = maxDelay?.TotalMilliseconds ?? Constants.Network.MaxReconnectDelayMs;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Check if this exception should be retried
                if (shouldRetry != null && !shouldRetry(ex))
                {
                    throw;
                }

                lastException = ex;

                if (attempt == maxRetries)
                {
                    logger?.LogError(ex, "Operation failed after {MaxRetries} attempts", maxRetries);
                    break;
                }

                // Calculate delay with exponential backoff, capped at maxDelay
                var currentDelayMs = Math.Min(delay.TotalMilliseconds * attempt, maxDelayMs);

                logger?.LogWarning(
                    "Operation failed on attempt {Attempt}/{MaxRetries}, retrying in {Delay}ms: {Error}",
                    attempt, maxRetries, currentDelayMs, ex.Message);

                await Task.Delay(TimeSpan.FromMilliseconds(currentDelayMs), cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Operation failed without exception");
    }

    /// <summary>
    /// Executes a void operation with retry logic and exponential backoff.
    /// </summary>
    public static async Task ExecuteAsync(
        Func<Task> operation,
        int maxRetries = Constants.Workflow.DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null,
        Func<Exception, bool>? shouldRetry = null,
        TimeSpan? maxDelay = null)
    {
        await ExecuteAsync(
            async () =>
            {
                await operation();
                return true;
            },
            maxRetries,
            baseDelay,
            cancellationToken,
            logger,
            shouldRetry,
            maxDelay);
    }

    /// <summary>
    /// Executes an operation with a specified number of immediate retries (no delay).
    /// Useful for operations that may fail due to transient resource contention.
    /// </summary>
    public static async Task<T> ExecuteWithImmediateRetryAsync<T>(
        Func<Task<T>> operation,
        int maxRetries = 3,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null)
    {
        return await ExecuteAsync(
            operation,
            maxRetries,
            TimeSpan.Zero,
            cancellationToken,
            logger);
    }

    /// <summary>
    /// Executes an operation with a circuit breaker pattern.
    /// After a threshold of failures, the circuit opens and operations fail fast.
    /// </summary>
    public static async Task<T> ExecuteWithCircuitBreakerAsync<T>(
        Func<Task<T>> operation,
        CircuitBreakerState state,
        CancellationToken cancellationToken = default,
        IAppLogger? logger = null)
    {
        if (state.IsOpen)
        {
            if (DateTime.UtcNow - state.LastFailure < state.OpenDuration)
            {
                throw new CircuitBreakerOpenException($"Circuit breaker is open. Retry after {state.OpenDuration.TotalSeconds}s");
            }
            state.HalfOpen();
        }

        try
        {
            var result = await operation();
            state.RecordSuccess();
            return result;
        }
        catch (Exception ex)
        {
            state.RecordFailure();
            logger?.LogWarning("Circuit breaker recorded failure ({Failures}/{Threshold}): {Error}",
                state.FailureCount, state.FailureThreshold, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Maintains state for circuit breaker pattern.
/// </summary>
public class CircuitBreakerState
{
    public int FailureCount { get; private set; }
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromMinutes(1);
    public DateTime LastFailure { get; private set; }
    public bool IsOpen => FailureCount >= FailureThreshold;
    public bool IsHalfOpen { get; private set; }

    public void RecordFailure()
    {
        FailureCount++;
        LastFailure = DateTime.UtcNow;
        IsHalfOpen = false;
    }

    public void RecordSuccess()
    {
        FailureCount = 0;
        IsHalfOpen = false;
    }

    public void HalfOpen()
    {
        IsHalfOpen = true;
    }

    public void Reset()
    {
        FailureCount = 0;
        IsHalfOpen = false;
    }
}

/// <summary>
/// Exception thrown when a circuit breaker is open.
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message) : base(message) { }
}
