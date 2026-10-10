using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace FlipPix.Core.Utilities;

/// <summary>
/// Provides standardized argument validation and null-checking patterns.
/// Use this class to ensure consistent validation across the codebase.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Throws if the value is null.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <param name="paramName">The parameter name (auto-captured).</param>
    /// <exception cref="ArgumentNullException">Thrown if value is null.</exception>
    public static T NotNull<T>(
        [NotNull] T? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
        return value;
    }

    /// <summary>
    /// Throws if the string is null or empty.
    /// </summary>
    public static string NotNullOrEmpty(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Value cannot be null or empty.", paramName);
        }
        return value;
    }

    /// <summary>
    /// Throws if the string is null, empty, or whitespace.
    /// </summary>
    public static string NotNullOrWhiteSpace(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be null, empty, or whitespace.", paramName);
        }
        return value;
    }

    /// <summary>
    /// Throws if the value is not within the specified range (inclusive).
    /// </summary>
    public static T InRange<T>(
        T value,
        T min,
        T max,
        [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {min} and {max}.");
        }
        return value;
    }

    /// <summary>
    /// Throws if the value is negative.
    /// </summary>
    public static int NotNegative(
        int value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value cannot be negative.");
        }
        return value;
    }

    /// <summary>
    /// Throws if the value is not positive (zero or negative).
    /// </summary>
    public static int Positive(
        int value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
        }
        return value;
    }

    /// <summary>
    /// Throws if the collection is null or empty.
    /// </summary>
    public static IReadOnlyCollection<T> NotNullOrEmpty<T>(
        [NotNull] IReadOnlyCollection<T>? collection,
        [CallerArgumentExpression(nameof(collection))] string? paramName = null)
    {
        if (collection is null || collection.Count == 0)
        {
            throw new ArgumentException("Collection cannot be null or empty.", paramName);
        }
        return collection;
    }

    /// <summary>
    /// Throws if the file does not exist.
    /// </summary>
    public static string FileExists(
        string path,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        NotNullOrWhiteSpace(path, paramName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }
        return path;
    }

    /// <summary>
    /// Throws if the directory does not exist.
    /// </summary>
    public static string DirectoryExists(
        string path,
        [CallerArgumentExpression(nameof(path))] string? paramName = null)
    {
        NotNullOrWhiteSpace(path, paramName);

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }
        return path;
    }

    /// <summary>
    /// Throws if the condition is false.
    /// </summary>
    public static void Condition(
        [DoesNotReturnIf(false)] bool condition,
        string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }

    /// <summary>
    /// Throws if the condition is false.
    /// </summary>
    public static void Condition(
        [DoesNotReturnIf(false)] bool condition,
        string message,
        string paramName)
    {
        if (!condition)
        {
            throw new ArgumentException(message, paramName);
        }
    }
}

/// <summary>
/// Extension methods for null-conditional operations.
/// </summary>
public static class NullExtensions
{
    /// <summary>
    /// Returns the value if not null, otherwise returns the default.
    /// Cleaner alternative to ?? operator for complex expressions.
    /// </summary>
    public static T Or<T>(this T? value, T defaultValue) where T : class
        => value ?? defaultValue;

    /// <summary>
    /// Returns the value if not null, otherwise returns the result of the factory.
    /// </summary>
    public static T OrGet<T>(this T? value, Func<T> factory) where T : class
        => value ?? factory();

    /// <summary>
    /// Returns the value if not null or empty, otherwise returns the default.
    /// </summary>
    public static string OrEmpty(this string? value)
        => value ?? string.Empty;

    /// <summary>
    /// Returns the value if not null or whitespace, otherwise returns the default.
    /// </summary>
    public static string OrDefault(this string? value, string defaultValue)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    /// <summary>
    /// Executes an action if the value is not null.
    /// </summary>
    public static void IfNotNull<T>(this T? value, Action<T> action) where T : class
    {
        if (value is not null)
        {
            action(value);
        }
    }

    /// <summary>
    /// Maps the value if not null, otherwise returns null.
    /// </summary>
    public static TResult? Map<T, TResult>(this T? value, Func<T, TResult> mapper)
        where T : class
        where TResult : class
        => value is null ? null : mapper(value);

    /// <summary>
    /// Returns true if the string is null, empty, or whitespace.
    /// </summary>
    public static bool IsNullOrWhiteSpace(this string? value)
        => string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Returns true if the string has meaningful content.
    /// </summary>
    public static bool HasValue(this string? value)
        => !string.IsNullOrWhiteSpace(value);
}
