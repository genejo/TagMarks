using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TagMarks.Desktop.Behaviors;

/// <summary>
/// Attached behavior that highlights substring matches inside a TextBlock.
/// Set <see cref="TextProperty"/> to the full string, <see cref="HighlightTermsProperty"/>
/// to a space-separated filter, and (optionally) <see cref="HighlightBrushProperty"/> to
/// control the highlight color. The behavior rebuilds <see cref="TextBlock.Inlines"/>
/// whenever any of these properties change.
/// </summary>
public class HighlightBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<HighlightBehavior, TextBlock, string?>("Text");

    public static readonly AttachedProperty<string?> HighlightTermsProperty =
        AvaloniaProperty.RegisterAttached<HighlightBehavior, TextBlock, string?>("HighlightTerms");

    public static readonly AttachedProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.RegisterAttached<HighlightBehavior, TextBlock, IBrush?>(
            "HighlightBrush", defaultValue: Brushes.Gold);

    public static string? GetText(TextBlock tb) => tb.GetValue(TextProperty);
    public static void SetText(TextBlock tb, string? value) => tb.SetValue(TextProperty, value);

    public static string? GetHighlightTerms(TextBlock tb) => tb.GetValue(HighlightTermsProperty);
    public static void SetHighlightTerms(TextBlock tb, string? value) => tb.SetValue(HighlightTermsProperty, value);

    public static IBrush? GetHighlightBrush(TextBlock tb) => tb.GetValue(HighlightBrushProperty);
    public static void SetHighlightBrush(TextBlock tb, IBrush? value) => tb.SetValue(HighlightBrushProperty, value);

    static HighlightBehavior()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>(OnPropertyChanged);
        HighlightTermsProperty.Changed.AddClassHandler<TextBlock>(OnPropertyChanged);
        HighlightBrushProperty.Changed.AddClassHandler<TextBlock>(OnPropertyChanged);
    }

    private static void OnPropertyChanged(TextBlock tb, AvaloniaPropertyChangedEventArgs _)
    {
        var text = GetText(tb);
        var terms = GetHighlightTerms(tb);
        var brush = GetHighlightBrush(tb);

        tb.Inlines?.Clear();
        // Clearing Text avoids the TextBlock showing its Text property alongside Inlines.
        tb.Text = null;

        if (string.IsNullOrEmpty(text))
            return;

        if (string.IsNullOrWhiteSpace(terms))
        {
            tb.Inlines?.Add(new Run(text));
            return;
        }

        var ranges = FindMatchRanges(text, terms);

        if (ranges.Count == 0)
        {
            tb.Inlines?.Add(new Run(text));
            return;
        }

        int pos = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > pos)
                tb.Inlines?.Add(new Run(text[pos..start]));

            tb.Inlines?.Add(new Run(text.Substring(start, length))
            {
                Foreground = brush
            });
            pos = start + length;
        }

        if (pos < text.Length)
            tb.Inlines?.Add(new Run(text[pos..]));
    }

    /// <summary>
    /// Finds all non-overlapping match ranges for each space-separated term,
    /// merges overlapping/adjacent ranges, and returns them sorted by position.
    /// </summary>
    private static List<(int Start, int Length)> FindMatchRanges(string text, string terms)
    {
        var tokens = terms.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];

        var all = new List<(int Start, int End)>();

        foreach (var token in tokens)
        {
            int idx = 0;
            while (idx <= text.Length - token.Length)
            {
                int found = text.IndexOf(token, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                all.Add((found, found + token.Length));
                idx = found + 1;
            }
        }

        if (all.Count == 0)
            return [];

        // Sort by start, then by end descending (longer match first)
        all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        // Merge overlapping ranges
        var merged = new List<(int Start, int Length)>();
        var (curStart, curEnd) = all[0];
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].Start <= curEnd)
            {
                curEnd = Math.Max(curEnd, all[i].End);
            }
            else
            {
                merged.Add((curStart, curEnd - curStart));
                (curStart, curEnd) = all[i];
            }
        }
        merged.Add((curStart, curEnd - curStart));

        return merged;
    }
}
