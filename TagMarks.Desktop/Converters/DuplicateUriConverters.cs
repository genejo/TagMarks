using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TagMarks.Desktop.Converters;

/// <summary>
/// Shows the duplicate marker on a tagmark whose primary URI another tagmark owns (spec §11 #50).
/// Bind values: [0] = string name, [1] = IReadOnlyDictionary&lt;string, string&gt; duplicate → owner.
/// </summary>
public class DuplicateUriMarkerConverter : IMultiValueConverter
{
    public static readonly DuplicateUriMarkerConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        DuplicateUriConverters.FindOwner(values) is not null;
}

/// <summary>
/// The duplicate marker's tooltip, e.g. <c>Same URI as "alpha"</c>; null for other tagmarks.
/// Bind values: as for <see cref="DuplicateUriMarkerConverter"/>.
/// </summary>
public class DuplicateUriToolTipConverter : IMultiValueConverter
{
    public static readonly DuplicateUriToolTipConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        DuplicateUriConverters.FindOwner(values) is { } owner ? $"Same URI as \"{owner}\"" : null;
}

internal static class DuplicateUriConverters
{
    public static string? FindOwner(IList<object?> values) =>
        values.Count >= 2 && values[0] is string name && values[1] is IReadOnlyDictionary<string, string> owners
            && owners.TryGetValue(name, out var owner)
            ? owner
            : null;
}
