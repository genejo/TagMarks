using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace TagMarks.Desktop.Converters;

/// <summary>
/// Shows ► for expandable tagmarks that are not currently expanded.
/// Bind values: [0] = string name, [1] = IReadOnlySet&lt;string&gt; expandableSet, [2] = string? expandedTagMark.
/// </summary>
public class ExpandableMarkerConverter : IMultiValueConverter
{
    public static readonly ExpandableMarkerConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 3 || values[0] is not string name || values[1] is not IReadOnlySet<string> set)
            return false;
        string? expandedName = values[2] as string;
        return set.Contains(name) && name != expandedName;
    }
}

/// <summary>
/// Shows ▼ for the tagmark that is currently expanded.
/// Bind values: [0] = string name, [1] = string? expandedTagMark.
/// </summary>
public class ExpandedItemConverter : IMultiValueConverter
{
    public static readonly ExpandedItemConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not string name)
            return false;
        return values[1] is string expandedName && name == expandedName;
    }
}
