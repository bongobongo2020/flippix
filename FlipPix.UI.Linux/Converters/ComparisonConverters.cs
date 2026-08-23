using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FlipPix.UI.Linux.Converters;

/// <summary>
/// True when the bound value is null. WPF writes this as
/// <c>&lt;DataTrigger Binding="{Binding Preview}" Value="{x:Null}"&gt;</c>, which usually means
/// "show the placeholder while there is no image".
/// </summary>
public class IsNullConverter : IValueConverter
{
    public static readonly IsNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the bound value is not null - the other half of the placeholder pair.</summary>
public class IsNotNullConverter : IValueConverter
{
    public static readonly IsNotNullConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the bound value equals ConverterParameter, compared as invariant strings so a
/// WPF <c>Value="1"</c> trigger ports without caring whether the property is int or string.
/// </summary>
public class EqualsConverter : IValueConverter
{
    public static readonly EqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(
            System.Convert.ToString(value, CultureInfo.InvariantCulture),
            System.Convert.ToString(parameter, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True when the bound value differs from ConverterParameter.</summary>
public class NotEqualsConverter : IValueConverter
{
    public static readonly NotEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.Equals(
            System.Convert.ToString(value, CultureInfo.InvariantCulture),
            System.Convert.ToString(parameter, CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
