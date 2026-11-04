using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Cauldron.Models;

namespace TagMarks.Desktop.Converters;

public class AppModeToBrushConverter : IValueConverter {
    public static readonly AppModeToBrushConverter Instance = new();

    private static readonly IBrush SearchBrush = new SolidColorBrush(Color.Parse("#007ACC"));
    private static readonly IBrush AddBrush = new SolidColorBrush(Color.Parse("#FF8C00"));
    private static readonly IBrush RenameBrush = new SolidColorBrush(Color.Parse("#2E8B57"));

    public static IBrush GetBrush(AppMode mode) => mode switch {
        AppMode.Search => SearchBrush,
        AppMode.Add => AddBrush,
        AppMode.Rename => RenameBrush,
        _ => SearchBrush
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppMode mode ? mode switch {
            AppMode.Search => SearchBrush,
            AppMode.Add => AddBrush,
            AppMode.Rename => RenameBrush,
            _ => SearchBrush
        } : SearchBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
