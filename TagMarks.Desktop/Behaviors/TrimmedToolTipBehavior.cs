using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TagMarks.Desktop.Behaviors;

/// <summary>
/// Attached behavior that lets a TextBlock's <see cref="ToolTip"/> open only while its text is
/// cut off by <see cref="TextBlock.TextTrimming"/>. Set <see cref="IsEnabledProperty"/> to true
/// and <c>ToolTip.Tip</c> to the full text.
/// </summary>
public class TrimmedToolTipBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TrimmedToolTipBehavior, TextBlock, bool>("IsEnabled");

    public static bool GetIsEnabled(TextBlock tb) => tb.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBlock tb, bool value) => tb.SetValue(IsEnabledProperty, value);

    static TrimmedToolTipBehavior()
    {
        // ToolTipOpening is a direct event, so a class handler is the only way to see it for every TextBlock.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<TextBlock>(OnToolTipOpening);
    }

    private static void OnToolTipOpening(TextBlock tb, CancelRoutedEventArgs e)
    {
        if (GetIsEnabled(tb) && !tb.TextLayout.TextLines.Any(line => line.HasCollapsed))
            e.Cancel = true;
    }
}
