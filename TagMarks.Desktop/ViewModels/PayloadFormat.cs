using System;

namespace TagMarks.Desktop.ViewModels;

/// <summary>
/// Text shown for a payload in the view: a multi-line clip as its first line plus a line count.
/// </summary>
internal static class PayloadFormat {
    public static string Preview(string payload) {
        var lines = payload.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        if (lines.Length <= 1) return payload;
        return $"{lines[0]} (+{lines.Length - 1} lines)";
    }

    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
